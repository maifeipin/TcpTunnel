# ============================================================
#  TunnelClient 打包脚本
#  功能：编译 -> 发布独立 exe -> 生成配置好的 start.bat
#  用法：在 TunnelClient 目录下运行：.\build-and-pack.ps1
#        可选参数示例：
#          .\build-and-pack.ps1 -ServerIp 1.2.3.4 -AuthKey MySecret -PublicPort 33890
# ============================================================
param(
    [string]$ServerIp    = "",          # VPS 公网 IP（留空则向导提示输入）
    [string]$AuthKey     = "",          # 认证密钥
    [int]   $ControlPort = 6666,
    [int]   $DataPort    = 6667,
    [int]   $TargetPort  = 3389,        # 本机 RDP 端口
    [string]$TargetIp    = "127.0.0.1",
    [string]$OutDir      = ""           # 输出目录，默认 .\publish
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ── 颜色输出 ────────────────────────────────────────────────
function Write-Info  { param($msg) Write-Host "[INFO]  $msg" -ForegroundColor Green  }
function Write-Warn  { param($msg) Write-Host "[WARN]  $msg" -ForegroundColor Yellow }
function Write-Err   { param($msg) Write-Host "[ERR]   $msg" -ForegroundColor Red    }

# ══════════════════════════════════════════════════════════════
#  1. 检查 .NET SDK
# ══════════════════════════════════════════════════════════════
Write-Info "检查 .NET SDK..."
try {
    $dotnetVer = (dotnet --version 2>$null).Trim()
    $major = [int]($dotnetVer -split '\.')[0]
    if ($major -lt 9) {
        Write-Warn "当前 .NET SDK 版本 $dotnetVer，建议 >= 9.0"
        Write-Warn "下载地址: https://dotnet.microsoft.com/download"
    } else {
        Write-Info ".NET SDK $dotnetVer 就绪"
    }
} catch {
    Write-Err ".NET SDK 未安装或不在 PATH 中"
    Write-Err "请先安装: https://dotnet.microsoft.com/download"
    exit 1
}

# ══════════════════════════════════════════════════════════════
#  2. 交互式参数引导
# ══════════════════════════════════════════════════════════════
if ([string]::IsNullOrWhiteSpace($ServerIp)) {
    Write-Host ""
    Write-Host "──────────────────────────────────────────" -ForegroundColor Cyan
    Write-Host "  请填写 TunnelClient 连接参数" -ForegroundColor Cyan
    Write-Host "──────────────────────────────────────────" -ForegroundColor Cyan
    $ServerIp = Read-Host "  VPS 公网 IP [必填]"
    if ([string]::IsNullOrWhiteSpace($ServerIp)) {
        Write-Err "ServerIp 不能为空"
        exit 1
    }
}
if ([string]::IsNullOrWhiteSpace($AuthKey)) {
    $input = Read-Host "  认证密钥 AUTH_KEY     [my_secure_key_123]"
    $AuthKey = if ($input) { $input } else { "my_secure_key_123" }
}

Write-Host ""
Write-Info "打包参数确认:"
Write-Host "  SERVER_IP    = $ServerIp"
Write-Host "  AUTH_KEY     = $AuthKey"
Write-Host "  CONTROL_PORT = $ControlPort"
Write-Host "  DATA_PORT    = $DataPort"
Write-Host "  TARGET_IP    = $TargetIp"
Write-Host "  TARGET_PORT  = $TargetPort"
Write-Host ""

# ══════════════════════════════════════════════════════════════
#  3. 编译 & 发布单文件 exe（依赖 .NET Runtime / 若要自包含改 --self-contained true）
# ══════════════════════════════════════════════════════════════
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

# OutDir 未指定时默认放在脚本旁的 publish\ 目录
if ([string]::IsNullOrWhiteSpace($OutDir)) {
    $OutDir = Join-Path $ScriptDir "publish"
}

# 清理旧产物
if (Test-Path $OutDir) {
    Remove-Item $OutDir -Recurse -Force
}
New-Item -ItemType Directory -Path $OutDir | Out-Null

Write-Info "编译发布中..."
$proj = Join-Path $ScriptDir "TunnelClient.csproj"
dotnet publish $proj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    /p:PublishSingleFile=true `
    /p:EnableCompressionInSingleFile=true `
    -o $OutDir `
    -nologo -v q

if ($LASTEXITCODE -ne 0) {
    Write-Err "编译失败，请检查源码"
    exit 1
}
Write-Info "编译完成 → $OutDir"

# ══════════════════════════════════════════════════════════════
#  4. 生成 start.bat（带参数，双击即可运行）
# ══════════════════════════════════════════════════════════════
$batPath = Join-Path $OutDir "start.bat"
$batContent = @"
@echo off
:: ============================================================
::  TunnelClient 启动脚本
::  双击运行，或右键"以管理员身份运行"
::  VPS 挂断后自动重连，关闭此窗口即断开隧道
:: ============================================================
title TunnelClient - RDP 隧道

:: ---- 连接配置（由 build-and-pack.ps1 自动填入）----
set SERVER_IP=$ServerIp
set AUTH_KEY=$AuthKey
set CONTROL_PORT=$ControlPort
set DATA_PORT=$DataPort
set TARGET_IP=$TargetIp
set TARGET_PORT=$TargetPort

echo [TunnelClient] 正在连接 %SERVER_IP%:%CONTROL_PORT% ...
echo 关闭此窗口即断开 RDP 隧道
echo.

TunnelClient.exe ^
  --server        %SERVER_IP%    ^
  --auth-key      %AUTH_KEY%     ^
  --control-port  %CONTROL_PORT% ^
  --data-port     %DATA_PORT%    ^
  --target-ip     %TARGET_IP%    ^
  --target-port   %TARGET_PORT%

echo.
echo [TunnelClient] 已退出，按任意键关闭...
pause > nul
"@

[System.IO.File]::WriteAllText($batPath, $batContent, [System.Text.Encoding]::GetEncoding("gbk"))
Write-Info "start.bat 已生成 → $batPath"

# ══════════════════════════════════════════════════════════════
#  5. 生成快捷 start.ps1（PowerShell 版，支持 UTF-8 中文）
# ══════════════════════════════════════════════════════════════
$ps1Path = Join-Path $OutDir "start.ps1"
$ps1Content = @"
# TunnelClient 启动脚本 (PowerShell 版)
# 运行: powershell -ExecutionPolicy Bypass -File start.ps1
`$SERVER_IP    = "$ServerIp"
`$AUTH_KEY     = "$AuthKey"
`$CONTROL_PORT = $ControlPort
`$DATA_PORT    = $DataPort
`$TARGET_IP    = "$TargetIp"
`$TARGET_PORT  = $TargetPort

Write-Host "[TunnelClient] 连接 `$SERVER_IP`:`$CONTROL_PORT" -ForegroundColor Green
& "`$PSScriptRoot\TunnelClient.exe" ``
    --server        `$SERVER_IP    ``
    --auth-key      `$AUTH_KEY     ``
    --control-port  `$CONTROL_PORT ``
    --data-port     `$DATA_PORT    ``
    --target-ip     `$TARGET_IP    ``
    --target-port   `$TARGET_PORT
"@

[System.IO.File]::WriteAllText($ps1Path, $ps1Content, [System.Text.UTF8Encoding]::new($false))
Write-Info "start.ps1 已生成 → $ps1Path"

# ══════════════════════════════════════════════════════════════
#  6. 输出结果
# ══════════════════════════════════════════════════════════════
Write-Host ""
Write-Host "══════════════════════════════════════════════════" -ForegroundColor Green
Write-Host "  打包完成！发布目录内容：" -ForegroundColor Green
Get-ChildItem $OutDir | Format-Table Name, Length, LastWriteTime -AutoSize
Write-Host "══════════════════════════════════════════════════" -ForegroundColor Green
Write-Host "  使用方法："
Write-Host "    1. 将 $OutDir 文件夹拷贝到目标内网 Windows 机器"
Write-Host "    2. 双击 start.bat（或运行 start.ps1）"
Write-Host "    3. 在任意位置用 mstsc 连接: $ServerIp`:$ControlPort（请改为公网RDP端口）"
Write-Host "══════════════════════════════════════════════════" -ForegroundColor Green

# ══════════════════════════════════════════════════════════════
#  7. 配置本机 Windows 防火墙（开放 RDP 入站端口）
#     客户端出站（→ VPS 6666/6667）Windows 默认放行，无需配置
#     需要配置的仅有：本机 RDP 入站 $TargetPort
# ══════════════════════════════════════════════════════════════
Write-Host ""
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltinRole]::Administrator)

if (-not $isAdmin) {
    Write-Warn "当前非管理员权限，跳过防火墙配置。"
    Write-Warn "如需自动开放 RDP 端口，请以管理员身份重新运行此脚本。"
} else {
    Write-Info "配置本机防火墙..."

    # 1. 开启远程桌面（注册表）
    $rdpKey = "HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server"
    $current = (Get-ItemProperty -Path $rdpKey -Name fDenyTSConnections).fDenyTSConnections
    if ($current -ne 0) {
        Set-ItemProperty -Path $rdpKey -Name fDenyTSConnections -Value 0
        Write-Info "已启用远程桌面（fDenyTSConnections = 0）"
    } else {
        Write-Info "远程桌面已处于启用状态"
    }

    # 2. 防火墙放行 RDP 入站（TCP $TargetPort）
    $ruleName = "TunnelClient-RDP-$TargetPort"
    $existing = Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue
    if ($existing) {
        Write-Info "防火墙规则已存在: $ruleName"
    } else {
        New-NetFirewallRule `
            -DisplayName  $ruleName `
            -Direction    Inbound `
            -Protocol     TCP `
            -LocalPort    $TargetPort `
            -Action       Allow `
            -Profile      Any `
            -Description  "TunnelClient: 允许 RDP 入站，端口 $TargetPort" | Out-Null
        Write-Info "已添加防火墙入站规则: TCP $TargetPort (Allow)"
    }

    # 3. 确认 RDP 服务（TermService）为自动启动且运行中
    $svc = Get-Service -Name TermService -ErrorAction SilentlyContinue
    if ($svc) {
        if ($svc.StartType -ne 'Automatic') {
            Set-Service -Name TermService -StartupType Automatic
            Write-Info "已将 TermService 设为自动启动"
        }
        if ($svc.Status -ne 'Running') {
            Start-Service -Name TermService
            Write-Info "已启动 TermService (远程桌面服务)"
        } else {
            Write-Info "TermService 运行中"
        }
    }

    Write-Host ""
    Write-Host "  防火墙配置完成：" -ForegroundColor Green
    Write-Host "    ? 远程桌面已启用"
    Write-Host "    ? 入站 TCP $TargetPort 已放行"
    Write-Host "    ? 出站 TCP → VPS（6666/6667）Windows 默认放行，无需额外配置"
}
