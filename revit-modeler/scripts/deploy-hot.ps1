# Hot-deploy the command set into a RUNNING Revit - no restart required.
#
# How it works: the plugin loads CommandSet.dll into a collectible AssemblyLoadContext
# from a shadow copy in %TEMP%, so the deployed files are not locked. This script
# rebuilds the command set, copies it over, then asks the plugin to reload in place.
#
# Use deploy.ps1 instead when you changed RevitModeler.Plugin itself (the host),
# because the host assembly cannot be reloaded inside the process.
#
# Kept ASCII-only for Windows PowerShell 5.1 compatibility.

param(
    [string]$RevitVersion = "2025",
    [string]$Configuration = "Release",
    [int]$Port = 8982,
    [switch]$SkipBuild,
    [switch]$VerifyOnly
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "lib/revit-rpc.ps1")

$projectRoot = Split-Path -Parent $PSScriptRoot
$tfm = "net8.0-windows10.0.19041.0"

# Resolve the real plugin directory from the add-in manifest, so we never deploy
# into a stale versioned folder.
$addinsRoot = Join-Path $env:APPDATA "Autodesk/Revit/Addins/$RevitVersion"
$addinFile = Join-Path $addinsRoot "BIMOne.RevitModeler.addin"
if (-not (Test-Path $addinFile)) {
    throw "Add-in manifest not found: $addinFile. Run deploy.ps1 first."
}

[xml]$addin = Get-Content $addinFile -Raw -Encoding UTF8
$assemblyRelative = $addin.RevitAddIns.AddIn.Assembly
$pluginDir = Split-Path -Parent (Join-Path $addinsRoot $assemblyRelative)
$commandsDir = Join-Path $pluginDir "Commands/RevitModeler"

Write-Host "Plugin dir : $pluginDir"
Write-Host "Commands   : $commandsDir"

function Show-Status {
    $raw = Invoke-FanmoRpc -Method "revit__ping" -Port $Port -TimeoutMs 15000
    $obj = $raw | ConvertFrom-Json
    $result = $obj.result
    if ($null -eq $result) { throw "Unexpected ping response: $raw" }

    Write-Host ""
    Write-Host "Service    : $($result.service) (port $($result.port))"
    Write-Host "Hot reload : $($result.hotReload)"
    Write-Host "Commands   : $($result.commands -join ', ')"
    Write-Host "Shadow dir : $($result.shadowDirectory)"
    return $result
}

if ($VerifyOnly) {
    Show-Status | Out-Null
    return
}

# 1) Build only the command set (the host is untouched)
if (-not $SkipBuild) {
    Write-Host ""
    Write-Host "Building RevitModeler.CommandSet ..."
    $cmdSetProj = Join-Path $projectRoot "src/RevitModeler.CommandSet/RevitModeler.CommandSet.csproj"
    dotnet build $cmdSetProj -c $Configuration --nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Build failed" }
}

$cmdSetOutFlat = Join-Path $projectRoot "src/RevitModeler.CommandSet/bin/$Configuration"
$cmdSetOutTfm = Join-Path $cmdSetOutFlat $tfm
$cmdSetOut = $cmdSetOutTfm
if (Test-Path (Join-Path $cmdSetOutFlat "RevitModeler.CommandSet.dll")) { $cmdSetOut = $cmdSetOutFlat }

if (-not (Test-Path (Join-Path $cmdSetOut "RevitModeler.CommandSet.dll"))) {
    throw "Command set build output not found: $cmdSetOut"
}

# 2) Copy over the deployed files. A sharing violation here means the running plugin
#    still loads assemblies directly from this folder (pre-hot-reload build).
try {
    Get-ChildItem -Path $cmdSetOut -Filter "*.dll" | Copy-Item -Force -Destination $commandsDir
    Get-ChildItem -Path $cmdSetOut -Filter "*.pdb" -ErrorAction SilentlyContinue |
        Copy-Item -Force -Destination $commandsDir
    Copy-Item -Force (Join-Path $projectRoot "command.json") (Join-Path $commandsDir "command.json")
} catch {
    Write-Host ""
    Write-Host "COPY FAILED - the command assemblies are locked by Revit." -ForegroundColor Red
    Write-Host "This build predates shadow loading. Restart Revit once with the current"
    Write-Host "plugin deployed (deploy.ps1); afterwards this script never needs a restart."
    exit 2
}

Write-Host ""
Write-Host "Files copied. Requesting in-place reload ..." -ForegroundColor Cyan

# 3) Trigger the reload
$raw = Invoke-FanmoRpc -Method "fanmo_reload" -Port $Port -TimeoutMs 120000
$obj = $raw | ConvertFrom-Json

if ($obj.error) {
    if ($obj.error.code -eq -32601) {
        Write-Host ""
        Write-Host "RELOAD NOT SUPPORTED by the running plugin (method not found)." -ForegroundColor Yellow
        Write-Host "Restart Revit once with the current plugin deployed; later reloads will be live."
        exit 3
    }
    Write-Host ""
    Write-Host "RELOAD FAILED: $($obj.error.message)" -ForegroundColor Red
    Write-Host "The previous version is still serving - nothing is broken." -ForegroundColor Yellow
    exit 4
}

$reload = $obj.result
if (-not $reload.ok) {
    Write-Host ""
    Write-Host "RELOAD FAILED: $($reload.error)" -ForegroundColor Red
    Write-Host "The previous version is still serving - nothing is broken." -ForegroundColor Yellow
    exit 4
}

Write-Host ""
Write-Host "Reload OK" -ForegroundColor Green
Write-Host "  commands   : $($reload.commands -join ', ')"
foreach ($prop in $reload.assemblies.PSObject.Properties) {
    Write-Host "  $($prop.Name) : $($prop.Value)"
}
if ($reload.warnings -and $reload.warnings.Count -gt 0) {
    Write-Host "  warnings   : $($reload.warnings -join ' | ')" -ForegroundColor Yellow
}
Write-Host "  retired    : $($reload.retiredContexts)  live: $($reload.liveContexts)"
