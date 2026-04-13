using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

// === 配置（支持命令行参数覆盖）===
// 用法: TunnelServer [--auth-key secret] [--control-port 6666] [--data-port 6667] [--public-port 33890(兼容旧模式)]
var cliArgs       = Environment.GetCommandLineArgs()[1..];
string authKey    = Arg(cliArgs, "--auth-key",      "my_secure_key_123");
int controlPort   = ArgInt(cliArgs, "--control-port", 6666);
int dataPort      = ArgInt(cliArgs, "--data-port",    6667);
int legacyPublicPort = ArgInt(cliArgs, "--public-port", 33890);
const int maxPendingSessions = 20;   // 最多同时等待的会话数（防 DoS）
const int heartbeatSec       = 25;   // 控制通道心跳间隔（秒）

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var pendingSessions = new ConcurrentDictionary<string, TaskCompletionSource<TcpClient>>();
NetworkStream? activeControl = null;
long activeControlGeneration = 0;
var controlWriteLock = new SemaphoreSlim(1, 1);
var configLock = new SemaphoreSlim(1, 1);
var activeProxies = new ConcurrentDictionary<string, ProxyRule>(StringComparer.OrdinalIgnoreCase);
var activeListeners = new ConcurrentDictionary<int, ListenerRuntime>();

var controlListener = new TcpListener(IPAddress.Any, controlPort);
var dataListener    = new TcpListener(IPAddress.Any, dataPort);
// SO_REUSEADDR：进程重启后端口立即可用，不等 TIME_WAIT
controlListener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
dataListener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
controlListener.Start();
dataListener.Start();
Console.WriteLine($"[Server] 控制:{controlPort} | 数据:{dataPort} | 兼容单端口:{legacyPublicPort}");

try
{
    await Task.WhenAll(
        AcceptLoopAsync(controlListener, HandleControlAsync),
    AcceptLoopAsync(dataListener,    HandleDataAsync));
}
finally
{
    // 优雅退出时释放所有监听器，避免端口被占用
    controlListener.Stop();
    dataListener.Stop();
    await ClearPublicListenersAsync();
    Console.WriteLine("[Server] 已停止");
}

// ─── 通用 Accept 循环 ────────────────────────────────────────
async Task AcceptLoopAsync(TcpListener listener, Func<TcpClient, Task> handler)
{
    while (!cts.IsCancellationRequested)
    {
        try
        {
            var client = await listener.AcceptTcpClientAsync(cts.Token);
            ApplyKeepAlive(client);   // 所有连接统一启用 TCP KeepAlive
            _ = handler(client);
        }
        catch (OperationCanceledException) { break; }
        catch (Exception ex) { Console.WriteLine($"[Accept] {ex.Message}"); await Task.Delay(500); }
    }
}

// ─── 控制连接：客户端保持长连接，用于接收 CONNECT 指令 ───────
async Task HandleControlAsync(TcpClient client)
{
    client.NoDelay = true;
    var stream = client.GetStream();
    var myGeneration = Interlocked.Increment(ref activeControlGeneration);
    try
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        var key = await reader.ReadLineAsync(cts.Token);
        if (key != authKey)
        {
            Console.WriteLine($"[Control] 认证失败 {client.Client.RemoteEndPoint}");
            return;
        }
        Console.WriteLine($"[Control] 已连接: {client.Client.RemoteEndPoint}");

        // 原子替换，踢掉旧连接
        var prev = Interlocked.Exchange(ref activeControl, stream);
        prev?.Close();

        // 注册代理规则（新模式），如果客户端未发送则回退为旧模式单端口
        var firstLine = await reader.ReadLineAsync(cts.Token);
        var proxies = ParseProxyRegistration(firstLine, legacyPublicPort);
        await ApplyProxyRegistrationAsync(proxies);

        // 心跳发送任务：定期发 PING，检测 VPS 防火墙是否静默断开控制连接
        using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        var pingBytes = Encoding.ASCII.GetBytes("PING\n");
        _ = Task.Run(async () =>
        {
            while (!pingCts.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(heartbeatSec), pingCts.Token);
                    await controlWriteLock.WaitAsync(pingCts.Token);
                    try   { await stream.WriteAsync(pingBytes, pingCts.Token); }
                    finally { controlWriteLock.Release(); }
                }
                catch { break; }
            }
        }, pingCts.Token);

        // 读取控制端指令（仅处理 PONG 心跳回应，忽略其他）
        while (!cts.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cts.Token);
            if (line == null) break; // EOF = 客户端断开
        }
        pingCts.Cancel();
    }
    catch { }
    finally
    {
        Console.WriteLine("[Control] 客户端断开");
        if (Volatile.Read(ref activeControlGeneration) == myGeneration)
        {
            Interlocked.CompareExchange(ref activeControl, null, stream);
            await ClearPublicListenersAsync();
            activeProxies.Clear();
        }
        client.Close();
    }
}

// ─── 数据连接：客户端为每个会话主动打开，携带 sessionId ────────
async Task HandleDataAsync(TcpClient client)
{
    client.NoDelay = true;
    var stream = client.GetStream();
    try
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        var sessionId = await reader.ReadLineAsync(cts.Token);
        if (string.IsNullOrEmpty(sessionId) ||
            !pendingSessions.TryRemove(sessionId, out var tcs))
        {
            Console.WriteLine("[Data] 未知 sessionId，拒绝");
            client.Close();
            return;
        }
        tcs.SetResult(client); // 流的所有权转给 HandlePublicAsync
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Data] {ex.Message}");
        client.Close();
    }
}

// ─── 公网连接：通知客户端建立数据连接，然后双向转发 ─────────────
async Task HandlePublicAsync(TcpClient publicClient, string proxyName)
{
    publicClient.NoDelay = true;

    // 限制同时等待的会话数，防止通过大量连接公网端口 DoS 内网客户端
    if (pendingSessions.Count >= maxPendingSessions)
    {
        Console.WriteLine("[Public] 等待队列已满，丢弃连接");
        publicClient.Close();
        return;
    }

    var sessionId = Guid.NewGuid().ToString("N");
    var tcs = new TaskCompletionSource<TcpClient>(TaskCreationOptions.RunContinuationsAsynchronously);
    pendingSessions[sessionId] = tcs;

    TcpClient? dataClient = null;
    try
    {
        var ctrl = activeControl;
        if (ctrl == null)
        {
            Console.WriteLine("[Public] 无客户端连接，拒绝");
            return;
        }

        if (!activeProxies.TryGetValue(proxyName, out var proxy))
        {
            Console.WriteLine($"[Public] 代理规则不存在: {proxyName}");
            return;
        }

        // 向客户端发送建立数据连接指令
        var cmd = Encoding.ASCII.GetBytes($"CONNECT:{proxy.Name}:{sessionId}\n");
        await controlWriteLock.WaitAsync(cts.Token);
        try   { await ctrl.WriteAsync(cmd, cts.Token); }
        finally { controlWriteLock.Release(); }

        // 等待客户端建立数据连接（10 秒超时）
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
        try   { dataClient = await tcs.Task.WaitAsync(timeoutCts.Token); }
        catch { Console.WriteLine($"[Public] session {sessionId[..8]} 超时"); return; }

        Console.WriteLine($"[Public] 转发开始 {sessionId[..8]}");
        var publicStream = publicClient.GetStream();
        var dataStream   = dataClient.GetStream();

        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        await Task.WhenAny(
            publicStream.CopyToAsync(dataStream,  sessionCts.Token),
            dataStream.CopyToAsync(publicStream,  sessionCts.Token));
        sessionCts.Cancel();
        Console.WriteLine($"[Public] 转发结束 {sessionId[..8]} | {proxy.Name} -> {proxy.LocalHost}:{proxy.LocalPort}");
    }
    catch (OperationCanceledException) { }
    catch (Exception ex) { Console.WriteLine($"[Public] {ex.Message}"); }
    finally
    {
        pendingSessions.TryRemove(sessionId, out _);
        dataClient?.Close();
        publicClient.Close();
    }
}

// ─── 工具函数 ─────────────────────────────────────────────────
// 启用 TCP KeepAlive：防止 Linux VPS 防火墙静默断开空闲 RDP 连接
static void ApplyKeepAlive(TcpClient client)
{
    var s = client.Client;
    s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
    s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 30);     // 30s 无数据后开始探测
    s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 10); // 每 10s 一次探测
    s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);// 3 次无响应则断开
}

static string Arg(string[] args, string key, string def)
{
    var i = Array.IndexOf(args, key);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : def;
}

static int ArgInt(string[] args, string key, int def)
    => int.TryParse(Arg(args, key, null!), out var v) ? v : def;

List<ProxyRule> ParseProxyRegistration(string? line, int fallbackPublicPort)
{
    if (!string.IsNullOrWhiteSpace(line) && line.StartsWith("PROXIES ", StringComparison.Ordinal))
    {
        var json = line["PROXIES ".Length..];
        var payload = JsonSerializer.Deserialize<ProxyRegistration>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        var parsed = payload?.Proxies?.Where(p => p.Enabled).ToList() ?? [];
        ValidateProxyRules(parsed);
        Console.WriteLine($"[Control] 已注册代理规则 {parsed.Count} 条");
        return parsed;
    }

    // 兼容旧客户端：单端口 default
    var legacy = new List<ProxyRule>
    {
        new()
        {
            Name = "default",
            PublicPort = fallbackPublicPort,
            LocalHost = "127.0.0.1",
            LocalPort = 3389,
            Enabled = true
        }
    };

    ValidateProxyRules(legacy);
    Console.WriteLine("[Control] 未收到代理注册，启用兼容单端口模式");
    return legacy;
}

void ValidateProxyRules(List<ProxyRule> rules)
{
    if (rules.Count == 0)
        throw new InvalidOperationException("客户端未提供任何可用代理规则。");

    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var ports = new HashSet<int>();
    foreach (var r in rules)
    {
        if (string.IsNullOrWhiteSpace(r.Name))
            throw new InvalidOperationException("代理规则 name 不能为空。");

        if (!names.Add(r.Name))
            throw new InvalidOperationException($"代理规则 name 重复: {r.Name}");

        if (r.PublicPort is <= 0 or > 65535)
            throw new InvalidOperationException($"代理规则 publicPort 非法: {r.Name}");

        if (!ports.Add(r.PublicPort))
            throw new InvalidOperationException($"代理规则 publicPort 重复: {r.PublicPort}");
    }
}

async Task ApplyProxyRegistrationAsync(List<ProxyRule> rules)
{
    await configLock.WaitAsync(cts.Token);
    try
    {
        await ClearPublicListenersAsync();
        activeProxies.Clear();

        foreach (var r in rules)
        {
            activeProxies[r.Name] = r;
            TryStartPublicListener(r);
        }
    }
    finally
    {
        configLock.Release();
    }
}

void TryStartPublicListener(ProxyRule rule)
{
    var listener = new TcpListener(IPAddress.Any, rule.PublicPort);
    listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
    listener.Start();

    var runtime = new ListenerRuntime(rule.PublicPort, rule.Name, listener, CancellationTokenSource.CreateLinkedTokenSource(cts.Token));
    runtime.LoopTask = Task.Run(async () =>
    {
        while (!runtime.Cts.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(runtime.Cts.Token);
                ApplyKeepAlive(client);
                _ = HandlePublicAsync(client, rule.Name);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Public:{rule.PublicPort}] {ex.Message}");
                await Task.Delay(200, runtime.Cts.Token).ContinueWith(_ => { });
            }
        }
    }, runtime.Cts.Token);

    activeListeners[rule.PublicPort] = runtime;
    Console.WriteLine($"[Public] 监听 {rule.PublicPort} -> {rule.Name}");
}

async Task ClearPublicListenersAsync()
{
    foreach (var kv in activeListeners)
    {
        kv.Value.Cts.Cancel();
        kv.Value.Listener.Stop();
    }

    foreach (var kv in activeListeners)
    {
        try { if (kv.Value.LoopTask is not null) await kv.Value.LoopTask; }
        catch { }
        kv.Value.Cts.Dispose();
    }

    activeListeners.Clear();
}

sealed class ProxyRegistration
{
    public List<ProxyRule> Proxies { get; set; } = [];
}

sealed class ProxyRule
{
    public string Name { get; set; } = string.Empty;
    public int PublicPort { get; set; }
    public string LocalHost { get; set; } = string.Empty;
    public int LocalPort { get; set; }
    public bool Enabled { get; set; } = true;
}

sealed class ListenerRuntime(int port, string proxyName, TcpListener listener, CancellationTokenSource cts)
{
    public int Port { get; } = port;
    public string ProxyName { get; } = proxyName;
    public TcpListener Listener { get; } = listener;
    public CancellationTokenSource Cts { get; } = cts;
    public Task? LoopTask { get; set; }
}