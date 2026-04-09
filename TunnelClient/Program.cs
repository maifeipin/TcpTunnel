using System.Net.Sockets;
using System.Text;

// === 配置（支持命令行参数覆盖）===
// 用法: TunnelClient [--server 1.2.3.4] [--control-port 6666] [--data-port 6667] [--auth-key secret] [--target-port 3389]
var cliArgs      = Environment.GetCommandLineArgs()[1..];
string serverIp  = Arg(cliArgs, "--server",        "192.168.1.100");
int controlPort  = ArgInt(cliArgs, "--control-port", 6666);
int dataPort     = ArgInt(cliArgs, "--data-port",    6667);
string authKey   = Arg(cliArgs, "--auth-key",       "my_secure_key_123");
string targetIp  = Arg(cliArgs, "--target-ip",      "127.0.0.1");
int targetPort   = ArgInt(cliArgs, "--target-port",  3389);
const int heartbeatTimeoutSec = 60; // 服务端每 25s 发 PING，若 60s 内无消息视为半开连接，强制重连

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

int retryDelay = 2000;
Console.WriteLine($"[Client] 服务端: {serverIp}:{controlPort} | 本地目标: {targetIp}:{targetPort}");

while (!cts.IsCancellationRequested)
{
    try
    {
        using var ctrl = new TcpClient { NoDelay = true };
        await ctrl.ConnectAsync(serverIp, controlPort, cts.Token);
        ApplyKeepAlive(ctrl);  // 控制连接启用 KeepAlive，防止 VPS 防火墙静默断开
        var ctrlStream = ctrl.GetStream();

        // 发送认证
        await ctrlStream.WriteAsync(Encoding.ASCII.GetBytes(authKey + "\n"), cts.Token);
        Console.WriteLine("[Client] 已连接服务端，等待指令...");
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
                var sessionId = line["CONNECT:".Length..];
                _ = HandleSessionAsync(sessionId, cts.Token);
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
        Console.WriteLine($"[Client] 断开 ({ex.Message})，{retryDelay / 1000}s 后重连...");
        await Task.Delay(retryDelay, cts.Token).ContinueWith(_ => { });
        retryDelay = Math.Min(retryDelay * 2, 30_000); // 指数退避，最长 30s
    }
}

// ─── 为每个外网会话建立独立的数据隧道 ──────────────────────────
async Task HandleSessionAsync(string sessionId, CancellationToken token)
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

        // 连接本地目标（RDP）
        localConn = new TcpClient { NoDelay = true };
        await localConn.ConnectAsync(targetIp, targetPort, token);
        ApplyKeepAlive(localConn);
        var localStream = localConn.GetStream();

        Console.WriteLine($"[Client] 转发 session {sessionId[..8]}");

        // 双向转发；任意一端关闭则同时终止另一端
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        await Task.WhenAny(
            dataStream.CopyToAsync(localStream,  sessionCts.Token),
            localStream.CopyToAsync(dataStream,  sessionCts.Token));
        sessionCts.Cancel();
    }
    catch (OperationCanceledException) { }
    catch (Exception ex) { Console.WriteLine($"[Client] session {sessionId[..8]} 错误: {ex.Message}"); }
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

static string Arg(string[] args, string key, string def)
{
    var i = Array.IndexOf(args, key);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : def;
}

static int ArgInt(string[] args, string key, int def)
    => int.TryParse(Arg(args, key, null!), out var v) ? v : def;