#!/usr/bin/env bash
# ============================================================
#  TunnelServer 在线一键安装脚本
#  无需提前下载源码，直接从 GitHub 拉取并编译安装
#
#  用法（VPS 上直接执行）：
#    curl -fsSL https://raw.githubusercontent.com/maifeipin/TcpTunnel/master/TunnelServer/install-online.sh | sudo bash
#  或：
#    wget -qO- https://raw.githubusercontent.com/maifeipin/TcpTunnel/master/TunnelServer/install-online.sh | sudo bash
# ============================================================
set -euo pipefail

RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; NC='\033[0m'
info()  { echo -e "${GREEN}[INFO]${NC}  $*"; }
warn()  { echo -e "${YELLOW}[WARN]${NC}  $*"; }
error() { echo -e "${RED}[ERR]${NC}   $*" >&2; }
die()   { error "$*"; exit 1; }

REPO_URL="https://github.com/maifeipin/TcpTunnel/archive/refs/heads/master.zip"
WORK_DIR=$(mktemp -d)
trap 'rm -rf "$WORK_DIR"' EXIT

# ══════════════════════════════════════════════════════════════
#  1. 检查 root 权限
# ══════════════════════════════════════════════════════════════
if [[ $EUID -ne 0 ]]; then
  die "请以 root 或 sudo 运行：sudo bash install-online.sh"
fi
info "权限检查通过 (root)"

# ══════════════════════════════════════════════════════════════
#  2. 下载源码
# ══════════════════════════════════════════════════════════════
info "正在从 GitHub 下载源码..."
if command -v curl &>/dev/null; then
  curl -fsSL "$REPO_URL" -o "$WORK_DIR/repo.zip"
elif command -v wget &>/dev/null; then
  wget -q "$REPO_URL" -O "$WORK_DIR/repo.zip"
else
  # 尝试安装 curl
  if [[ -f /etc/debian_version ]]; then
    apt-get update -qq && apt-get install -y curl
  else
    dnf install -y curl
  fi
  curl -fsSL "$REPO_URL" -o "$WORK_DIR/repo.zip"
fi

# ══════════════════════════════════════════════════════════════
#  3. 解压，定位 TunnelServer 目录
# ══════════════════════════════════════════════════════════════
info "解压源码..."
if command -v unzip &>/dev/null; then
  unzip -q "$WORK_DIR/repo.zip" -d "$WORK_DIR"
else
  if [[ -f /etc/debian_version ]]; then
    apt-get install -y unzip -qq
  else
    dnf install -y unzip
  fi
  unzip -q "$WORK_DIR/repo.zip" -d "$WORK_DIR"
fi

# GitHub zip 解压后目录名为 <repo>-<branch>
EXTRACTED=$(find "$WORK_DIR" -maxdepth 1 -mindepth 1 -type d | head -1)
SERVER_DIR="$EXTRACTED/TunnelServer"
[[ -d "$SERVER_DIR" ]] || die "未找到 TunnelServer 目录，源码结构可能已变更"
info "源码就绪 → $SERVER_DIR"

# ══════════════════════════════════════════════════════════════
#  4. 调用标准 install.sh 完成后续安装
# ══════════════════════════════════════════════════════════════
chmod +x "$SERVER_DIR/install.sh"
exec bash "$SERVER_DIR/install.sh"
