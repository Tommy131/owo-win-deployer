#requires -version 5
# OwO! Win Deployer 引导 —— 在全新设备上一行启动：
#   irm https://raw.githubusercontent.com/Tommy131/owo-win-deployer/main/bootstrap/bootstrap.ps1 | iex
$ErrorActionPreference = 'Stop'

$Root = Join-Path $env:USERPROFILE '.owo-win-deployer'
$Repo = 'https://github.com/Tommy131/owo-win-deployer.git'
$ExeUrl = 'https://github.com/Tommy131/owo-win-deployer/releases/latest/download/WinDeploy.exe'

function Have($c) { [bool](Get-Command $c -ErrorAction SilentlyContinue) }

Write-Host '== OwO! Win Deployer bootstrap ==' -ForegroundColor Cyan

if (-not (Have 'winget')) {
    Write-Warning 'winget（App Installer）缺失，请先从 Microsoft Store 安装 “App Installer” 后重试。'
}
if (-not (Have 'git')) {
    Write-Host '安装 Git ...' -ForegroundColor Cyan
    winget install --id Git.Git -e --accept-source-agreements --accept-package-agreements --disable-interactivity
}

# 拉取仓库（携带 catalog / configs；GUI 会在 exe 旁定位这些数据）
if (Test-Path (Join-Path $Root '.git')) {
    Write-Host "更新仓库 $Root ..." -ForegroundColor Cyan
    git -C $Root pull --ff-only
} else {
    Write-Host "克隆仓库到 $Root ..." -ForegroundColor Cyan
    git clone --depth 1 $Repo $Root
}

# 优先用 Release 的自包含 exe（目标机免装 .NET）；失败则回退到源码运行
$Exe = Join-Path $Root 'WinDeploy.exe'
try {
    Write-Host '下载最新 WinDeploy.exe ...' -ForegroundColor Cyan
    Invoke-WebRequest -Uri $ExeUrl -OutFile $Exe -UseBasicParsing
    Start-Process $Exe -WorkingDirectory $Root
    Write-Host '已启动 OwO! Win Deployer。' -ForegroundColor Green
} catch {
    Write-Warning "下载 Release exe 失败（可能尚未发布）：$($_.Exception.Message)"
    if (Have 'dotnet') {
        Write-Host '回退：从源码运行 GUI ...' -ForegroundColor Cyan
        Start-Process dotnet -ArgumentList @('run', '--project', (Join-Path $Root 'src/WinDeploy.App')) -WorkingDirectory $Root
    } else {
        Write-Warning '无 Release exe 且未装 .NET SDK。请安装 .NET SDK 后重试，或等仓库发布 Release。'
    }
}
