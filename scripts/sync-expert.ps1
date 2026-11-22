param(
  [switch]$Zip
)

# Sync the in-repo expert package to the WorkBuddy marketplace install directory.
# Source of truth: <projectRoot>/experts/bim-fanmo-master
# Install target : %USERPROFILE%\.workbuddy\plugins\marketplaces\my-experts\plugins\bim-fanmo-master
# All paths are ASCII on purpose for Windows PowerShell 5.1 compatibility.

$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$src = Join-Path $projectRoot "experts\bim-fanmo-master"
$pluginsRoot = Join-Path $env:USERPROFILE ".workbuddy\plugins\marketplaces\my-experts\plugins"
$dst = Join-Path $pluginsRoot "bim-fanmo-master"
$zipPath = Join-Path (Split-Path -Parent $projectRoot) "outputs\bim-fanmo-master.zip"

if (-not (Test-Path $src)) {
  throw "Source expert package not found: $src"
}

New-Item -ItemType Directory -Force -Path $pluginsRoot | Out-Null
New-Item -ItemType Directory -Force -Path $dst | Out-Null

# /E copies subdirectories including empty ones. No /MIR: never delete extra files from the install dir.
& robocopy $src $dst /E /NFL /NDL /NJH /NJS /NC /NS /NP | Out-Null
$rc = $LASTEXITCODE
if ($rc -ge 8) {
  throw "robocopy failed with exit code $rc"
}

Write-Host "EXPERT_SYNCED: $src -> $dst (robocopy=$rc)"

if ($Zip) {
  Compress-Archive -Path $src -DestinationPath $zipPath -CompressionLevel Optimal -Force
  Write-Host "EXPERT_ZIPPED: $zipPath"
}
