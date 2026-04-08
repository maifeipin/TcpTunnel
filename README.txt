════════════════════════════════════════════════════════════════
  Tunnel — 简易 RDP 内网穿透工具
  场景：通过公网 VPS (Linux) 用 MSTSC 远程连接内网 Windows 主机
════════════════════════════════════════════════════════════════

【原理】
  内网 Win ──[主动连接]──► VPS TunnelServer ◄──[MSTSC]── 任意位置
  内网机器无需公网 IP，仅需能访问 VPS 即可。

  端口说明
  ┌──────────────────┬────────┬──────────────────────────────┐
  │ 用途             │ 默认   │ 说明                         │
  ├──────────────────┼────────┼──────────────────────────────┤
  │ 控制端口         │ 6666   │ 客户端长连接，接收指令       │
  │ 数据端口         │ 6667   │ 每个 RDP Session 的数据通道  │
  │ 公网 RDP 端口    │ 33890  │ MSTSC 连接此端口             │
  └──────────────────┴────────┴──────────────────────────────┘

────────────────────────────────────────────────────────────────
  一、VPS 服务端部署（Linux）
────────────────────────────────────────────────────────────────

  前置要求：
    - Ubuntu 20+ / Debian 11+ / CentOS 8+ / Rocky / AlmaLinux
    - 具有 sudo/root 权限
    - VPS 安全组/防火墙放行以下入站端口（TCP）：
        6666, 6667, 33890

  步骤：
  1. 将整个 TunnelServer/ 目录上传到 VPS，例如：
       mkdir /opt/tunnel-server-src/TunnelServer/
       scp -r TunnelServer/ root@<vps_ip>:/opt/tunnel-server-src/

  2. SSH 登录 VPS，进入目录并执行安装脚本：
       cd /opt/tunnel-server-src/TunnelServer/
       chmod +x install.sh
       sudo ./install.sh

     脚本会自动：
       ✓ 检测系统版本和权限
       ✓ 安装 .NET 9 Runtime（若未安装）
       ✓ 编译并发布可执行文件到 /opt/tunnel-server/
       ✓ 交互引导填写端口和认证密钥
       ✓ 开放 ufw / firewalld 防火墙端口
       ✓ 注册 systemd 服务（开机自启）

  3. 验证服务运行状态：
       systemctl status tunnel-server
       journalctl -u tunnel-server -f

  常用管理命令：
    修改配置  →  nano /opt/tunnel-server/tunnel.conf
                 systemctl restart tunnel-server
    停止服务  →  systemctl stop tunnel-server
    卸载服务  →  systemctl disable --now tunnel-server
                 rm /etc/systemd/system/tunnel-server.service

────────────────────────────────────────────────────────────────
  二、内网 Windows 客户端
────────────────────────────────────────────────────────────────

  方式 A：直接使用发布包（推荐，无需安装 .NET）
  ──────────────────────────────────────────────
  1. 在开发机（已装 .NET SDK）上进入 TunnelClient/ 目录，运行：
       .\build-and-pack.ps1

     按提示输入 VPS 公网 IP 和认证密钥，脚本会自动：
       ✓ 编译并发布独立 exe（无需目标机安装 .NET）
       ✓ 生成预填好参数的 start.bat 和 start.ps1

  2. 将生成的 publish\ 文件夹整体拷贝到内网 Windows 机器。

  3. 双击 start.bat 启动隧道（保持窗口开着）。

  也可以指定参数直接打包，省去交互提示：
    .\build-and-pack.ps1 -ServerIp 1.2.3.4 -AuthKey MySecret

  方式 B：源码运行（已有 .NET 9 Runtime）
  ──────────────────────────────────────────
    dotnet run --project TunnelClient -- ^
      --server 1.2.3.4 --auth-key MySecret

────────────────────────────────────────────────────────────────
  三、使用 MSTSC 连接
────────────────────────────────────────────────────────────────

  1. 确保 VPS 服务端正在运行，内网客户端已连接（start.bat 窗口显示"等待指令"）。

  2. 在任意 Windows 上打开运行（Win+R），输入：
       mstsc /v:<VPS公网IP>:33890

     或直接在 MSTSC 地址栏填写：
       <VPS公网IP>:33890

  3. 输入内网机器的 Windows 账号密码即可。

  注意：首次连接 RDP 需要内网机器开启"远程桌面"
    开启方法：设置 → 系统 → 远程桌面 → 开启

────────────────────────────────────────────────────────────────
  四、命令行参数说明
────────────────────────────────────────────────────────────────

  TunnelServer 参数：
    --auth-key      认证密钥（默认: my_secure_key_123）
    --control-port  控制端口（默认: 6666）
    --data-port     数据端口（默认: 6667）
    --public-port   公网 RDP 端口（默认: 33890）

  TunnelClient 参数：
    --server        VPS 公网 IP（必填）
    --auth-key      认证密钥，需与服务端一致
    --control-port  服务端控制端口（默认: 6666）
    --data-port     服务端数据端口（默认: 6667）
    --target-ip     本地 RDP 目标 IP（默认: 127.0.0.1）
    --target-port   本地 RDP 端口（默认: 3389）

────────────────────────────────────────────────────────────────
  五、安全建议
────────────────────────────────────────────────────────────────

  ⚠  修改默认认证密钥！"my_secure_key_123" 仅为示例，
     上线前务必改为随机强密码（16+ 字符）。

  建议操作：
  1. 在 VPS 安全组仅对你的 IP 放行 6666、6667 端口。
     33890 端口需要对外开放（MSTSC 连接使用）。

  2. 为 RDP 账号设置强密码，开启 Windows 账户锁定策略。

  3. 若 VPS 上的 6666/6667 只有自己的内网机器连接，
     可通过 iptables 限制这两个端口只允许你的出口 IP：
       iptables -A INPUT -p tcp --dport 6666 -s <你的IP> -j ACCEPT
       iptables -A INPUT -p tcp --dport 6666 -j DROP

────────────────────────────────────────────────────────────────
  六、故障排查
────────────────────────────────────────────────────────────────

  MSTSC 连接超时
  → 检查服务端状态:  systemctl status tunnel-server
  → 检查客户端窗口是否显示"等待指令..."
  → 检查 VPS 安全组是否放行 33890 端口

  客户端频繁断开重连
  → 通常是 VPS 防火墙或 NAT 超时，属正常现象，程序会自动重连
  → 已内置 TCP KeepAlive + PING/PONG 心跳，空闲 RDP 不会轻易断开

  RDP 连接后卡顿/花屏
  → 在 MSTSC 选项 → 体验 → 连接速度选"低速宽带"
  → 降低颜色深度和分辨率

  端口被占用无法启动
  → 检查是否有其他程序占用：  ss -tlnp | grep 6666
  → 修改 /opt/tunnel-server/tunnel.conf 换用其他端口

════════════════════════════════════════════════════════════════
