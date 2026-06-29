#requires -version 5
# Build a self-contained single-file GUI (WinDeploy.exe) into build\ — target machines need no .NET runtime.
# Version comes from the root Directory.Build.props (single source of truth); no need to edit this script.
# ASCII-only on purpose: runs under both Windows PowerShell 5.1 and pwsh 7 regardless of file encoding.
#   powershell -ExecutionPolicy Bypass -File build.ps1            # Release / win-x64 -> build\
#   powershell -ExecutionPolicy Bypass -File build.ps1 -Run       # build then launch
#   powershell -ExecutionPolicy Bypass -File build.ps1 -Config Debug
param(
    [string]$Runtime = 'win-x64',
    [string]$Config  = 'Release',
    [switch]$Run
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out  = Join-Path $root 'build'

# Stop any running instance so the single-file exe isn't locked during clean / publish.
Get-Process WinDeploy -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

# Read the single version source (display only).
$ver = '?'
$props = Join-Path $root 'Directory.Build.props'
if (Test-Path $props) {
    $m = [regex]::Match((Get-Content $props -Raw), '<Version>\s*([^<\s]+)\s*</Version>')
    if ($m.Success) { $ver = $m.Groups[1].Value }
}

Write-Host "== Build WinDeploy v$ver ($Config / $Runtime) -> build\ ==" -ForegroundColor Cyan

# Wipe old output for a clean, latest build. Best-effort: a prior admin run can leave a loaded kernel
# hardware-monitor driver (WinDeploy.sys / WinRing0) that locks a file — skip it and let publish refresh
# the exe anyway (reboot to fully clear the folder if you ever need it pristine).
if (Test-Path $out) { Remove-Item (Join-Path $out '*') -Recurse -Force -ErrorAction SilentlyContinue }

dotnet publish (Join-Path $root 'src\WinDeploy.App\WinDeploy.App.csproj') `
    -c $Config -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    --nologo -o $out
if ($LASTEXITCODE -ne 0) { Write-Host 'Build FAILED.' -ForegroundColor Red; exit 1 }

$exe = Join-Path $out 'WinDeploy.exe'
Write-Host "Done: $exe" -ForegroundColor Green
if (Test-Path $exe) { '{0,-14} {1,7:N1} MB' -f 'WinDeploy.exe', ((Get-Item $exe).Length / 1MB) }

if ($Run -and (Test-Path $exe)) { Start-Process $exe }
