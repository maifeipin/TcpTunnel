using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

// 关闭控制台"快速编辑模式"：防止用户误点黑框导致进程被系统挂起，MSTSC 无法连接
if (OperatingSystem.IsWindows())
{
    var hStdin = NativeMethods.GetStdHandle(-10);               // STD_INPUT_HANDLE
    if (NativeMethods.GetConsoleMode(hStdin, out uint mode))
        NativeMethods.SetConsoleMode(hStdin, mode & ~0x0040u);  // 清除 ENABLE_QUICK_EDIT_MODE
}

// === 配置（支持 JSON 配置文件 + 命令行回退）===
// 推荐: TunnelClient --config tunnel-client.json
// 兼容: TunnelClient [--server 1.2.3.4] [--control-port 6666] [--data-port 6667] [--auth-key secret] [--target-ip 127.0.0.1] [--target-port 3389] [--public-port 33890]
var cliArgs = Environment.GetCommandLineArgs()[1..];
var config = LoadConfig(cliArgs);

string serverIp = config.Server.Host;
int controlPort = config.Server.ControlPort;
int dataPort = config.Server.DataPort;
string authKey = config.Server.AuthKey;
var proxyMap = config.Proxies
    .Where(p => p.Enabled)
    .ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

if (proxyMap.Count == 0)
    throw new InvalidOperationException("配置中没有可用代理规则，请检查 proxies[].enabled。");

var proxySummary = string.Join(", ", config.Proxies.Where(p => p.Enabled).Select(p => $"{p.Name}:{p.PublicPort}->{p.LocalHost}:{p.LocalPort}"));
const int heartbeatTimeoutSec = 60; // 服务端每 25s 发 PING，若 60s 内无消息视为半开连接，强制重连

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

int retryDelay = 2000;
Log($"[Client] 服务端: {serverIp}:{controlPort} | 数据端口: {dataPort}");
Log($"[Client] 代理规则: {proxySummary}");

while (!cts.IsCancellationRequested)
{
    try
    {
        using var ctrl = new TcpClient { NoDelay = true };
        // 连接超时 8s：避免因服务端暂时不可达导致每次重试等待 OS 默认 ~20s TCP 超时
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        connectTimeout.CancelAfter(TimeSpan.FromSeconds(8));
        await ctrl.ConnectAsync(serverIp, controlPort, connectTimeout.Token);
        ApplyKeepAlive(ctrl);  // 控制连接启用 KeepAlive，防止 VPS 防火墙静默断开
        var ctrlStream = ctrl.GetStream();

        // 发送认证
        await ctrlStream.WriteAsync(Encoding.ASCII.GetBytes(authKey + "\n"), cts.Token);
        var registration = JsonSerializer.Serialize(new ProxyRegistration(config.Proxies.Where(p => p.Enabled).ToList()));
        await ctrlStream.WriteAsync(Encoding.ASCII.GetBytes("PROXIES " + registration + "\n"), cts.Token);
        Log("[Client] 已连接服务端，等待指令...");
        retryDelay = 2000; // 连接成功后重置退避时间

        // 读取服务端指令
        using var reader = new StreamReader(ctrlStream, leaveOpen: true);
        while (!cts.IsCancellationRequested)
        {
            // 若 heartbeatTimeoutSec 内未收到任何消息（含 PING），说明连接已半开断开，抛异常触发重连
            using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            readTimeout.CancelAfter(TimeSpan.FromSeconds(heartbeatTimeoutSec));
            var line = await reader.ReadLineAsync(readTimeout.Token);
            if (line == null) break; // EOF = 服务端断开

            if (line.StartsWith("CONNECT:", StringComparison.Ordinal))
            {
                // 新协议: CONNECT:<proxyName>:<sessionId>
                // 旧协议: CONNECT:<sessionId>
                string proxyName;
                string sessionId;
                var payload = line["CONNECT:".Length..];
                var parts = payload.Split(':', 2);
                if (parts.Length == 2)
                {
                    proxyName = parts[0];
                    sessionId = parts[1];
                }
                else
                {
                    proxyName = "default";
                    sessionId = payload;
                }

                if (!proxyMap.TryGetValue(proxyName, out var rule))
                {
                    Log($"[Client] 未找到代理规则 '{proxyName}'，忽略 session {sessionId[..Math.Min(8, sessionId.Length)]}");
                    continue;
                }

                _ = HandleSessionAsync(sessionId, rule, cts.Token);
            }
            else if (line == "PING")
            {
                // 响应服务端心跳，保持控制连接活跃
                await ctrlStream.WriteAsync(Encoding.ASCII.GetBytes("PONG\n"), cts.Token);
            }
        }
    }
    catch (OperationCanceledException) when (cts.IsCancellationRequested) { break; }
    catch (Exception ex)
    {
        // OperationCanceledException 且 cts 未取消 = 连接超时（8s），给出更明确的提示
        var msg = ex is OperationCanceledException ? $"连接服务端超时(8s)，请确认服务端进程是否运行" : ex.Message;
        Log($"[Client] 断开 ({msg})，{retryDelay / 1000}s 后重连...");
        await Task.Delay(retryDelay, cts.Token).ContinueWith(_ => { });
        retryDelay = Math.Min(retryDelay * 2, 30_000); // 指数退避，最长 30s
    }
}

// ─── 为每个外网会话建立独立的数据隧道 ──────────────────────────
async Task HandleSessionAsync(string sessionId, ProxyRule rule, CancellationToken token)
{
    TcpClient? dataConn  = null;
    TcpClient? localConn = null;
    try
    {
        // 向服务端打开数据连接，报告 sessionId
        dataConn = new TcpClient { NoDelay = true };
        await dataConn.ConnectAsync(serverIp, dataPort, token);
        ApplyKeepAlive(dataConn);
        var dataStream = dataConn.GetStream();
        await dataStream.WriteAsync(Encoding.ASCII.GetBytes(sessionId + "\n"), token);

        // 连接本地目标（RDP/SSH/HTTP 等 TCP 服务）
        localConn = new TcpClient { NoDelay = true };
        await localConn.ConnectAsync(rule.LocalHost, rule.LocalPort, token);
        ApplyKeepAlive(localConn);
        var localStream = localConn.GetStream();

        Log($"[Client] 转发 session {sessionId[..Math.Min(8, sessionId.Length)]} | {rule.Name} -> {rule.LocalHost}:{rule.LocalPort}");

        // 双向转发；任意一端关闭则同时终止另一端
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        await Task.WhenAny(
            dataStream.CopyToAsync(localStream,  sessionCts.Token),
            localStream.CopyToAsync(dataStream,  sessionCts.Token));
        sessionCts.Cancel();
    }
    catch (OperationCanceledException) { }
    catch (Exception ex) { Log($"[Client] session {sessionId[..Math.Min(8, sessionId.Length)]} 错误: {ex.Message}"); }
    finally
    {
        dataConn?.Close();
        localConn?.Close();
    }
}

// ─── 工具函数 ─────────────────────────────────────────────────
// 启用 TCP KeepAlive：防止 Linux VPS 防火墙静默断开空闲 RDP 连接（RDP 空闲时无数据流量）
static void ApplyKeepAlive(TcpClient client)
{
    var s = client.Client;
    s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
    s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 30);     // 30s 无数据后开始探测
    s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 10); // 每 10s 一次探测
    s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);// 3 次无响应则断开
}

static void Log(string msg) => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {msg}");

static string Arg(string[] args, string key, string def)
{
    var i = Array.IndexOf(args, key);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : def;
}

static int ArgInt(string[] args, string key, int def)
    => int.TryParse(Arg(args, key, null!), out var v) ? v : def;

static ClientConfig LoadConfig(string[] args)
{
    var configPath = Arg(args, "--config", string.Empty);
    if (string.IsNullOrWhiteSpace(configPath))
    {
        var defaultPath = Path.Combine(AppContext.BaseDirectory, "tunnel-client.json");
        configPath = File.Exists(defaultPath) ? defaultPath : string.Empty;
    }

    if (!string.IsNullOrWhiteSpace(configPath))
    {
        var fullPath = Path.GetFullPath(configPath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"配置文件不存在: {fullPath}");

        var json = File.ReadAllText(fullPath);
        var cfg = JsonSerializer.Deserialize<ClientConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException("配置文件解析失败。");

        ValidateConfig(cfg);
        return cfg;
    }

    // 命令行回退模式：保持旧版可用（单规则）
    var serverIp = Arg(args, "--server", "192.168.1.100");
    var controlPort = ArgInt(args, "--control-port", 6666);
    var dataPort = ArgInt(args, "--data-port", 6667);
    var authKey = Arg(args, "--auth-key", "my_secure_key_123");
    var targetIp = Arg(args, "--target-ip", "127.0.0.1");
    var targetPort = ArgInt(args, "--target-port", 3389);
    var publicPort = ArgInt(args, "--public-port", 33890);

    var fallback = new ClientConfig
    {
        Server = new ServerConfig
        {
            Host = serverIp,
            AuthKey = authKey,
            ControlPort = controlPort,
            DataPort = dataPort
        },
        Proxies =
        [
            new ProxyRule
            {
                Name = "default",
                PublicPort = publicPort,
                LocalHost = targetIp,
                LocalPort = targetPort,
                Enabled = true
            }
        ]
    };

    ValidateConfig(fallback);
    return fallback;
}

static void ValidateConfig(ClientConfig cfg)
{
    if (cfg.Server is null)
        throw new InvalidOperationException("配置缺少 server 节点。");

    if (string.IsNullOrWhiteSpace(cfg.Server.Host))
        throw new InvalidOperationException("server.host 不能为空。");

    if (string.IsNullOrWhiteSpace(cfg.Server.AuthKey))
        throw new InvalidOperationException("server.authKey 不能为空。");

    if (cfg.Server.ControlPort is <= 0 or > 65535)
        throw new InvalidOperationException("server.controlPort 必须在 1-65535。");

    if (cfg.Server.DataPort is <= 0 or > 65535)
        throw new InvalidOperationException("server.dataPort 必须在 1-65535。");

    cfg.Proxies ??= [];
    var enabled = cfg.Proxies.Where(p => p.Enabled).ToList();
    if (enabled.Count == 0)
        throw new InvalidOperationException("至少需要一个 enabled=true 的代理规则。");

    var nameSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var portSet = new HashSet<int>();
    foreach (var p in enabled)
    {
        if (string.IsNullOrWhiteSpace(p.Name))
            throw new InvalidOperationException("proxy.name 不能为空。");

        if (!nameSet.Add(p.Name))
            throw new InvalidOperationException($"proxy.name 重复: {p.Name}");

        if (p.PublicPort is <= 0 or > 65535)
            throw new InvalidOperationException($"proxy.publicPort 非法: {p.Name}");

        if (!portSet.Add(p.PublicPort))
            throw new InvalidOperationException($"proxy.publicPort 重复: {p.PublicPort}");

        if (string.IsNullOrWhiteSpace(p.LocalHost))
            throw new InvalidOperationException($"proxy.localHost 不能为空: {p.Name}");

        if (p.LocalPort is <= 0 or > 65535)
            throw new InvalidOperationException($"proxy.localPort 非法: {p.Name}");
    }
}

sealed class ClientConfig
{
    public ServerConfig Server { get; set; } = new();
    public List<ProxyRule> Proxies { get; set; } = [];
}

sealed class ServerConfig
{
    public string Host { get; set; } = string.Empty;
    public string AuthKey { get; set; } = string.Empty;
    public int ControlPort { get; set; } = 6666;
    public int DataPort { get; set; } = 6667;
}

sealed class ProxyRule
{
    public string Name { get; set; } = string.Empty;
    public int PublicPort { get; set; } = 33890;
    public string LocalHost { get; set; } = "127.0.0.1";
    public int LocalPort { get; set; } = 3389;
    public bool Enabled { get; set; } = true;
}

sealed class ProxyRegistration(List<ProxyRule> proxies)
{
    public List<ProxyRule> Proxies { get; } = proxies;
}

// ─── Windows 控制台 API ───────────────────────────────────────
static class NativeMethods
{
    [DllImport("kernel32.dll")]
    public static extern IntPtr GetStdHandle(int n);

    [DllImport("kernel32.dll")]
    public static extern bool GetConsoleMode(IntPtr h, out uint mode);

    [DllImport("kernel32.dll")]
    public static extern bool SetConsoleMode(IntPtr h, uint mode);
}