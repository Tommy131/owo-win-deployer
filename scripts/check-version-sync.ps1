# Cross-artifact version sync check: the root Directory.Build.props <Version> (the single source of
# truth that App / CLI / Core all inherit) must match the version line at the top of README.md and
# README_CH.md. Exit 0 = in sync, 1 = mismatch. Run from anywhere: pwsh scripts/check-version-sync.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

$propsPath = Join-Path $root 'Directory.Build.props'
if (-not (Test-Path $propsPath)) { Write-Host "Directory.Build.props not found at repo root" -ForegroundColor Red; exit 1 }
$props = Get-Content $propsPath -Raw
if ($props -notmatch '<Version>\s*([^<\s]+)\s*</Version>') { Write-Host "<Version> not found in Directory.Build.props" -ForegroundColor Red; exit 1 }
$version = $Matches[1].Trim()
Write-Host "Source of truth (Directory.Build.props): v$version"

$problems = 0
# Each README's version line must read exactly "v<version>" (followed by a non-digit / end, so v1.2.5.2
# does not spuriously match v1.2.5.21).
$checks = @(
  @{ File = 'README.md';    Pattern = "Current version: v$([regex]::Escape($version))(\D|$)" },
  @{ File = 'README_CH.md'; Pattern = "当前版本：v$([regex]::Escape($version))(\D|$)" }
)
foreach ($c in $checks) {
  $path = Join-Path $root $c.File
  if (-not (Test-Path $path)) { Write-Host "MISSING  $($c.File)" -ForegroundColor Red; $problems++; continue }
  $text = Get-Content $path -Raw
  if ($text -match $c.Pattern) { Write-Host "OK       $($c.File) - v$version" -ForegroundColor Green }
  else { Write-Host "MISMATCH $($c.File) - expected version line 'v$version'" -ForegroundColor Red; $problems++ }
}

if ($problems -eq 0) { Write-Host "version sync OK: all artifacts and READMEs agree on v$version" -ForegroundColor Green; exit 0 }
else { Write-Host "version sync: $problems mismatch(es) found" -ForegroundColor Red; exit 1 }
