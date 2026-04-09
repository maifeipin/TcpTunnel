#!/usr/bin/env bash
# ============================================================
#  TunnelServer 一键安装脚本
#  适用：Ubuntu 20+  /  Debian 11+  /  CentOS 8+  /  Rocky/Alma
#  用法：chmod +x install.sh && sudo ./install.sh
# ============================================================
set -euo pipefail

# ── 颜色输出 ────────────────────────────────────────────────
RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; NC='\033[0m'
info()  { echo -e "${GREEN}[INFO]${NC}  $*"; }
warn()  { echo -e "${YELLOW}[WARN]${NC}  $*"; }
error() { echo -e "${RED}[ERR]${NC}   $*" >&2; }
die()   { error "$*"; exit 1; }

# ══════════════════════════════════════════════════════════════
#  1. 检查 root 权限
# ══════════════════════════════════════════════════════════════
if [[ $EUID -ne 0 ]]; then
  die "请以 root 或 sudo 运行此脚本：sudo ./install.sh"
fi
info "权限检查通过 (root)"

# ══════════════════════════════════════════════════════════════
#  2. 检测发行版
# ══════════════════════════════════════════════════════════════
if [[ -f /etc/os-release ]]; then
  . /etc/os-release
  DISTRO="$ID"
  VERSION_NUM="${VERSION_ID:-unknown}"
else
  die "无法识别操作系统，请手动安装。"
fi
info "系统: $PRETTY_NAME"

# ══════════════════════════════════════════════════════════════
#  3. 安装 .NET 9 Runtime（若未安装）
# ══════════════════════════════════════════════════════════════
DOTNET_MIN=9
install_dotnet() {
  # 必须安装 SDK（包含运行时），dotnet publish 需要 SDK，仅 Runtime 无法编译
  info "正在安装 .NET ${DOTNET_MIN} SDK..."
  case "$DISTRO" in
    ubuntu|debian|linuxmint)
      apt-get update -qq
      apt-get install -y wget apt-transport-https
      local TMP; TMP=$(mktemp -d)
      wget -q "https://packages.microsoft.com/config/${DISTRO}/${VERSION_NUM}/packages-microsoft-prod.deb" \
           -O "$TMP/ms.deb" || die "下载 Microsoft 源失败，请检查网络或手动安装 .NET SDK"
      dpkg -i "$TMP/ms.deb"
      apt-get update -qq
      apt-get install -y dotnet-sdk-${DOTNET_MIN}.0
      ;;
    centos|rhel|rocky|almalinux|fedora)
      dnf install -y "https://packages.microsoft.com/config/rhel/8/packages-microsoft-prod.rpm" || true
      dnf install -y dotnet-sdk-${DOTNET_MIN}.0
      ;;
    *)
      warn "未知发行版 '$DISTRO'，尝试使用 dotnet-install 脚本（安装完整 SDK）..."
      wget -q https://dot.net/v1/dotnet-install.sh -O /tmp/dotnet-install.sh
      # 不加 --runtime，安装完整 SDK（包含编译器 + 运行时）
      bash /tmp/dotnet-install.sh --channel "${DOTNET_MIN}.0" --install-dir /usr/share/dotnet
      ln -sf /usr/share/dotnet/dotnet /usr/local/bin/dotnet || true
      ;;
  esac
}

check_dotnet() {
  # 检测 SDK（能执行 publish 的前提），而不仅仅是 Runtime
  if command -v dotnet &>/dev/null; then
    local sdk_ver
    sdk_ver=$(dotnet --list-sdks 2>/dev/null | grep -E "^${DOTNET_MIN}\."|  tail -1)
    if [[ -n "$sdk_ver" ]]; then
      info ".NET SDK 已满足要求 ($sdk_ver)"
      return 0
    fi
    local ver
    ver=$(dotnet --version 2>/dev/null | cut -d. -f1)
    if [[ "$ver" -ge $DOTNET_MIN ]]; then
      warn ".NET ${ver} 已安装，但未检测到 SDK，可能仅装了 Runtime，编译可能失败"
    else
      warn ".NET SDK 未安装或版本过低"
    fi
  else
    warn ".NET 未安装"
  fi
  return 1
}

check_dotnet || install_dotnet
# 二次确认
dotnet --version &>/dev/null || die ".NET 安装失败，请手动安装 .NET ${DOTNET_MIN} Runtime。\n  参考: https://aka.ms/dotnet-download"
info ".NET $(dotnet --version) 就绪"

# ══════════════════════════════════════════════════════════════
#  4. 编译 TunnelServer
# ══════════════════════════════════════════════════════════════
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
INSTALL_DIR="/opt/tunnel-server"
SERVICE_NAME="tunnel-server"
BINARY="$INSTALL_DIR/TunnelServer"

info "编译 TunnelServer..."
cd "$SCRIPT_DIR"
dotnet publish TunnelServer.csproj \
  -c Release \
  -r linux-x64 \
  --self-contained false \
  -o "$INSTALL_DIR" \
  /p:PublishSingleFile=true \
  -nologo -v q || die "编译失败，请检查源码"
info "编译完成 → $INSTALL_DIR"

chmod +x "$BINARY"

# ══════════════════════════════════════════════════════════════
#  5. 生成配置文件
# ══════════════════════════════════════════════════════════════
CONFIG_FILE="$INSTALL_DIR/tunnel.conf"
if [[ -f "$CONFIG_FILE" ]]; then
  warn "配置文件已存在，跳过生成（如需重置请删除 $CONFIG_FILE）"
else
  # 随机生成强密钥（24 字节 base64，字母+数字，无特殊符号）
  RANDOM_KEY=$(head -c 24 /dev/urandom | base64 | tr -dc 'A-Za-z0-9' | head -c 24)

  # 交互式引导
  echo ""
  echo "──────────────────────────────────────────"
  echo "  请配置 TunnelServer 启动参数"
  echo "──────────────────────────────────────────"
  # </dev/tty 确保在 curl|bash 管道场景下也能读取用户键盘输入
  read -r -p "  认证密钥 AUTH_KEY       [直接回车随机生成]: " AUTH_KEY </dev/tty
  # 回车留空 → 使用随机生成的密钥
  AUTH_KEY="${AUTH_KEY:-$RANDOM_KEY}"
  read -r -p "  控制端口 CONTROL_PORT   [6666]: " CONTROL_PORT </dev/tty
  CONTROL_PORT="${CONTROL_PORT:-6666}"
  read -r -p "  数据端口 DATA_PORT      [6667]: " DATA_PORT </dev/tty
  DATA_PORT="${DATA_PORT:-6667}"
  read -r -p "  公网RDP端口 PUBLIC_PORT [33890]: " PUBLIC_PORT </dev/tty
  PUBLIC_PORT="${PUBLIC_PORT:-33890}"

  cat > "$CONFIG_FILE" <<EOF
# TunnelServer 配置文件
# 修改后重启服务：systemctl restart ${SERVICE_NAME}
AUTH_KEY="${AUTH_KEY}"
CONTROL_PORT="${CONTROL_PORT}"
DATA_PORT="${DATA_PORT}"
PUBLIC_PORT="${PUBLIC_PORT}"
EOF
  info "配置文件已生成 → $CONFIG_FILE"

  # 立即回显关键信息，方便填写客户端 start.bat
  echo ""
  echo -e "${YELLOW}┌─────────────────────────────────────────────────┐${NC}"
  echo -e "${YELLOW}│        ★  客户端所需配置参数（请记录）★         │${NC}"
  echo -e "${YELLOW}├─────────────────────────────────────────────────┤${NC}"
  echo -e "${YELLOW}│  AUTH_KEY     = ${GREEN}${AUTH_KEY}${YELLOW}│${NC}"
  echo -e "${YELLOW}│  CONTROL_PORT = ${GREEN}${CONTROL_PORT}${YELLOW}│${NC}"
  echo -e "${YELLOW}│  DATA_PORT    = ${GREEN}${DATA_PORT}${YELLOW}│${NC}"
  echo -e "${YELLOW}│  PUBLIC_PORT  = ${GREEN}${PUBLIC_PORT}${YELLOW}│${NC}"
  echo -e "${YELLOW}└─────────────────────────────────────────────────┘${NC}"
  echo ""
fi

# 读取配置
. "$CONFIG_FILE"

# ══════════════════════════════════════════════════════════════
#  6. 开放防火墙端口（ufw / firewalld）
# ══════════════════════════════════════════════════════════════
open_port() {
  local port=$1
  if command -v ufw &>/dev/null && ufw status | grep -q "Status: active"; then
    ufw allow "$port/tcp" &>/dev/null && info "ufw: 已开放 $port/tcp"
  elif command -v firewall-cmd &>/dev/null && systemctl is-active --quiet firewalld; then
    firewall-cmd --permanent --add-port="$port/tcp" &>/dev/null
    info "firewalld: 已开放 $port/tcp"
  fi
}
open_port "$CONTROL_PORT"
open_port "$DATA_PORT"
open_port "$PUBLIC_PORT"
command -v firewall-cmd &>/dev/null && systemctl is-active --quiet firewalld && firewall-cmd --reload &>/dev/null

# ══════════════════════════════════════════════════════════════
#  7. 创建 systemd 服务（开机自启）
# ══════════════════════════════════════════════════════════════
SERVICE_FILE="/etc/systemd/system/${SERVICE_NAME}.service"
cat > "$SERVICE_FILE" <<EOF
[Unit]
Description=TunnelServer - RDP Reverse Tunnel
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
WorkingDirectory=${INSTALL_DIR}
EnvironmentFile=${CONFIG_FILE}
ExecStart=${BINARY} \\
  --auth-key \${AUTH_KEY} \\
  --control-port \${CONTROL_PORT} \\
  --data-port \${DATA_PORT} \\
  --public-port \${PUBLIC_PORT}
Restart=on-failure
RestartSec=5
StandardOutput=journal
StandardError=journal
SyslogIdentifier=tunnel-server

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable "$SERVICE_NAME"
systemctl restart "$SERVICE_NAME"

sleep 2
if systemctl is-active --quiet "$SERVICE_NAME"; then
  info "服务已启动并设为开机自启 ✓"
else
  error "服务启动失败！查看日志：journalctl -u ${SERVICE_NAME} -n 50"
  exit 1
fi

# ══════════════════════════════════════════════════════════════
#  8. 输出使用说明
# ══════════════════════════════════════════════════════════════
VPS_IP=$(curl -s --max-time 5 https://api.ipify.org 2>/dev/null || echo "<VPS公网IP>")
echo ""
echo -e "${GREEN}══════════════════════════════════════════════════════${NC}"
echo -e "${GREEN}  TunnelServer 安装完成！${NC}"
echo -e "${GREEN}══════════════════════════════════════════════════════${NC}"
echo "  VPS 公网 IP    : $VPS_IP"
echo "  认证密钥       : $AUTH_KEY"
echo "  控制端口       : $CONTROL_PORT"
echo "  数据端口       : $DATA_PORT"
echo "  公网 RDP 端口  : $PUBLIC_PORT"
echo ""
echo "  常用命令:"
echo "    查看状态  : systemctl status $SERVICE_NAME"
echo "    查看日志  : journalctl -u $SERVICE_NAME -f"
echo "    修改配置  : nano $CONFIG_FILE && systemctl restart $SERVICE_NAME"
echo "    停止服务  : systemctl stop $SERVICE_NAME"
echo ""
echo "  客户端配置参数（填入 start.bat）:"
echo "    SERVER_IP     = $VPS_IP"
echo "    AUTH_KEY      = $AUTH_KEY"
echo "    CONTROL_PORT  = $CONTROL_PORT"
echo "    DATA_PORT     = $DATA_PORT"
echo ""
echo "  MSTSC 连接地址 : mstsc /v:${VPS_IP}:${PUBLIC_PORT}"
echo -e "${GREEN}══════════════════════════════════════════════════════${NC}"
