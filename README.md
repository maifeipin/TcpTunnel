# Tunnel — 轻量 RDP 内网穿透工具

> 通过公网 VPS（Linux）用 MSTSC 远程连接内网 Windows 主机，无需公网 IP，无需安装 FRP。

```
内网 Win ──[主动连出]──► VPS TunnelServer ◄──── MSTSC（任意位置）
```

## 特性

- **零依赖**：客户端发布为独立 exe，内网机器无需安装 .NET
- **自动重连**：指数退避（2 s → 30 s），断线即重试
- **防掉线**：TCP KeepAlive + PING/PONG 心跳，空闲 RDP 不被防火墙杀掉
- **多会话**：每个 RDP 连接独立数据通道，互不干扰
- **一键部署**：服务端 `install.sh` 自动安装 SDK、编译、注册 systemd 服务
- **防暴力**：认证密钥、最大并发会话限制（默认 20）

## 端口说明

| 端口 | 默认 | 说明 |
|---|---|---|
| 控制端口 | 6666 | 客户端长连接，接收 CONNECT 指令 |
| 数据端口 | 6667 | 每个 RDP Session 的独立数据通道 |
| 公网 RDP 端口 | 33890 | MSTSC 连接此端口 |

## 快速开始

### 一、服务端（VPS / Linux）

**支持系统**：Ubuntu 20+ / Debian 11+ / CentOS 8+ / Rocky / AlmaLinux

```bash
# 1. 上传源码
scp -r TunnelServer/ root@<VPS_IP>:/opt/tunnel-server-src/

# 2. SSH 登录后执行安装脚本
cd /opt/tunnel-server-src/TunnelServer/
chmod +x install.sh
sudo ./install.sh
```

脚本会自动完成：

- ✅ 检测系统版本与 root 权限
- ✅ 安装 .NET 9 SDK（支持 apt / dnf / dotnet-install 三种方式）
- ✅ `dotnet publish` 编译为 Linux 单文件可执行
- ✅ 交互引导填写端口和认证密钥（**回车随机生成强密钥**）
- ✅ 自动开放 ufw / firewalld 防火墙端口
- ✅ 注册 systemd 服务，开机自启

安装完成后会显示客户端所需参数：

```
┌─────────────────────────────────────────────────┐
│        ★  客户端所需配置参数（请记录）★         │
├─────────────────────────────────────────────────┤
│  AUTH_KEY     = aB3xK9mQw2nRpL7vTz4Y           │
│  CONTROL_PORT = 6666                            │
│  DATA_PORT    = 6667                            │
│  PUBLIC_PORT  = 33890                           │
└─────────────────────────────────────────────────┘
```

**VPS 安全组**需放行入站端口：`6666`、`6667`、`33890`（TCP）

---

### 二、客户端（内网 Windows）

在**开发机**（已装 .NET 9 SDK）上运行打包脚本：

```powershell
cd TunnelClient
.\build-and-pack.ps1
```

按提示输入 VPS 公网 IP 和认证密钥，脚本会：

- ✅ 编译发布为独立 exe（目标机无需安装 .NET）
- ✅ 生成预填参数的 `start.bat` 和 `start.ps1`
- ✅ 如以管理员运行：自动开启远程桌面、添加防火墙入站规则

将生成的 `publish\` 文件夹拷贝到**内网 Windows 机器**，双击 `start.bat` 即可。

也可以直接传参跳过交互：

```powershell
.\build-and-pack.ps1 -ServerIp 1.2.3.4 -AuthKey MySecret
```

---

### 三、连接远程桌面

```
mstsc /v:<VPS公网IP>:33890
```

或在 MSTSC 地址栏填写 `<VPS公网IP>:33890`，输入内网机器的 Windows 账号密码即可。

> **前提**：内网机器已开启"远程桌面"（设置 → 系统 → 远程桌面 → 开启）

## 命令行参数

**TunnelServer**

```
TunnelServer [--auth-key <key>] [--control-port <port>] [--data-port <port>] [--public-port <port>]
```

**TunnelClient**

```
TunnelClient --server <VPS_IP> [--auth-key <key>] [--control-port <port>] [--data-port <port>] [--target-ip <ip>] [--target-port <port>]
```

## 服务端管理

```bash
systemctl status tunnel-server          # 查看状态
journalctl -u tunnel-server -f         # 实时日志
nano /opt/tunnel-server/tunnel.conf    # 修改配置
systemctl restart tunnel-server        # 重启服务
systemctl disable --now tunnel-server  # 卸载服务
```

## 安全建议

> ⚠️ 上线前务必修改默认认证密钥，安装脚本可自动随机生成。

1. **最小化暴露**：在 VPS 安全组中，仅对你的出口 IP 放行 `6666`、`6667`；`33890` 对外开放。
2. **强密码**：为 Windows RDP 账户设置强密码，启用账户锁定策略。
3. **iptables 限制**（可选）：
   ```bash
   iptables -A INPUT -p tcp --dport 6666 -s <你的IP> -j ACCEPT
   iptables -A INPUT -p tcp --dport 6666 -j DROP
   ```

## 故障排查

| 现象 | 排查方向 |
|---|---|
| MSTSC 连接超时 | 检查 `systemctl status tunnel-server`；检查 VPS 安全组 33890 是否放行 |
| 客户端频繁断线重连 | 正常现象（程序自动重连）；已内置 KeepAlive + 心跳，通常不影响使用 |
| RDP 卡顿/花屏 | MSTSC → 体验 → 连接速度选"低速宽带"；降低颜色深度 |
| 端口被占用 | `ss -tlnp \| grep 6666`；修改 `/opt/tunnel-server/tunnel.conf` |

## 项目结构

```
Tunnel/
├── README.md
├── README.txt
├── TunnelServer/
│   ├── Program.cs           # 服务端主程序
│   ├── TunnelServer.csproj
│   └── install.sh           # VPS 一键安装脚本
└── TunnelClient/
    ├── Program.cs           # 客户端主程序
    ├── TunnelClient.csproj
    └── build-and-pack.ps1   # 打包脚本（生成 exe + start.bat）
```

## 技术栈

- .NET 9 / C# 13 Top-level statements
- 纯 BCL（`System.Net.Sockets`），无第三方依赖
- 异步 I/O + `CancellationToken` 全链路取消

## License

MIT
