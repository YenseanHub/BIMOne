# BIMOne RevitModeler deployment script (independent three-tier stack)
# Deploys the Revit plugin, command set and command.json to an isolated directory.
# Kept separate from the mcp-servers-for-revit host (own ClientId / port 8982 / commands directory).
#
# Restart policy:
#   - RevitModeler.Plugin (host) is loaded by Revit itself and can never be reloaded
#     in-process: changing it always requires one Revit restart.
#   - RevitModeler.CommandSet is hot-reloadable; after the host supports shadow loading,
#     use deploy-hot.ps1 for command-set-only changes (no restart).
#
# When Revit is running, the plugin directory is locked, so this script automatically
# deploys into the next versioned directory and repoints the add-in manifest.
#
# Keep this file ASCII-only for Windows PowerShell 5.1 compatibility.

param(
    [string]$RevitVersion = "2025",
    [string]$Configuration = "Release",
    [string]$AddinsRoot = (Join-Path $env:APPDATA "Autodesk/Revit/Addins/$RevitVersion"),
    [switch]$Versioned
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$tfm = "net8.0-windows10.0.19041.0"

$pluginProj = Join-Path $projectRoot "src/RevitModeler.Plugin/RevitModeler.Plugin.csproj"
$cmdSetProj = Join-Path $projectRoot "src/RevitModeler.CommandSet/RevitModeler.CommandSet.csproj"
$addinSource = Join-Path $projectRoot "src/RevitModeler.Plugin/BIMOne.RevitModeler.addin"
$commandJson = Join-Path $projectRoot "command.json"

# Build the two .NET projects
Write-Host "Building RevitModeler.Plugin ..."
dotnet build $pluginProj -c $Configuration --nologo | Out-Null
Write-Host "Building RevitModeler.CommandSet ..."
dotnet build $cmdSetProj -c $Configuration --nologo | Out-Null

# The plugin sets AppendTargetFrameworkToOutputPath=false, while the command set keeps the TFM subfolder.
$pluginOutFlat = Join-Path $projectRoot "src/RevitModeler.Plugin/bin/$Configuration"
$pluginOutTfm = Join-Path $pluginOutFlat $tfm
$pluginOut = $pluginOutFlat
if (-not (Test-Path (Join-Path $pluginOutFlat "RevitModeler.Plugin.dll")) -and (Test-Path $pluginOutTfm)) {
    $pluginOut = $pluginOutTfm
}

$cmdSetOutFlat = Join-Path $projectRoot "src/RevitModeler.CommandSet/bin/$Configuration"
$cmdSetOutTfm = Join-Path $cmdSetOutFlat $tfm
$cmdSetOut = $cmdSetOutTfm
if (Test-Path (Join-Path $cmdSetOutFlat "RevitModeler.CommandSet.dll")) {
    $cmdSetOut = $cmdSetOutFlat
}

if (-not (Test-Path (Join-Path $pluginOut "RevitModeler.Plugin.dll"))) { Write-Error "Plugin build output not found: $pluginOut"; exit 1 }
if (-not (Test-Path (Join-Path $cmdSetOut "RevitModeler.CommandSet.dll"))) { Write-Error "Command set build output not found: $cmdSetOut"; exit 1 }

# Decide the target directory. Prefer the directory the manifest already points at
# (so an un-restarted deployment is overwritten instead of piling up _vN folders);
# if that one is locked by a running Revit, move to the next versioned directory.
$dirName = "BIMOne.RevitModeler"
$existingAddin = Join-Path $AddinsRoot "BIMOne.RevitModeler.addin"
if (Test-Path $existingAddin) {
    $existingText = Get-Content $existingAddin -Raw
    $match = [regex]::Match($existingText, '<Assembly>([^/\\]+)[/\\]')
    if ($match.Success) { $dirName = $match.Groups[1].Value }
}

$pluginDir = Join-Path $AddinsRoot $dirName

$probe = Join-Path $pluginDir "RevitModeler.Plugin.dll"
$locked = $false
if (Test-Path $probe) {
    try {
        $fs = [System.IO.File]::Open($probe, [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
        $fs.Close()
    } catch {
        $locked = $true
    }
}

if ($Versioned -or $locked) {
    $max = 1
    Get-ChildItem -Path $AddinsRoot -Directory -Filter "BIMOne.RevitModeler_v*" -ErrorAction SilentlyContinue |
        ForEach-Object {
            $suffix = $_.Name -replace "^BIMOne\.RevitModeler_v", ""
            $n = 0
            if ([int]::TryParse($suffix, [ref]$n) -and $n -gt $max) { $max = $n }
        }
    $dirName = "BIMOne.RevitModeler_v$($max + 1)"
    $pluginDir = Join-Path $AddinsRoot $dirName
    Write-Host "Plugin directory is in use by a running Revit - deploying to $dirName"
} else {
    Write-Host "Deploying to $dirName (not in use)"
}

$commandsDir = Join-Path $pluginDir "Commands/RevitModeler"
New-Item -ItemType Directory -Force -Path $commandsDir | Out-Null

# 1) Add-in registration file, repointed at the actual plugin directory.
#    Written as UTF-8 to match the source manifest; Revit reads it on next start.
$addinText = Get-Content $addinSource -Raw -Encoding UTF8
$addinText = $addinText -replace '<Assembly>[^<]*</Assembly>', "<Assembly>$dirName/RevitModeler.Plugin.dll</Assembly>"
$addinPath = Join-Path $AddinsRoot "BIMOne.RevitModeler.addin"
[System.IO.File]::WriteAllText($addinPath, $addinText, (New-Object System.Text.UTF8Encoding($false)))

# 2) Plugin DLL and dependencies in the plugin directory
Get-ChildItem -Path $pluginOut -Filter "*.dll" | Copy-Item -Force -Destination $pluginDir
Get-ChildItem -Path $pluginOut -Filter "*.pdb" -ErrorAction SilentlyContinue | Copy-Item -Force -Destination $pluginDir

# 3) Command set DLLs and command.json in the isolated commands directory
Get-ChildItem -Path $cmdSetOut -Filter "*.dll" | Copy-Item -Force -Destination $commandsDir
Get-ChildItem -Path $cmdSetOut -Filter "*.pdb" -ErrorAction SilentlyContinue | Copy-Item -Force -Destination $commandsDir
Copy-Item -Force $commandJson (Join-Path $commandsDir "command.json")

# Revit provides RevitAPI/RevitAPIUI in-process. Never ship a copied API assembly,
# otherwise Revit 2025 reports an assembly version conflict and commands can hang.
Get-ChildItem $pluginDir, $commandsDir -Filter "RevitAPI*.dll" -ErrorAction SilentlyContinue | Remove-Item -Force

Write-Host ""
Write-Host "Deployed to:"
Write-Host "  $addinPath"
Write-Host "  $pluginDir (plugin + dependencies)"
Write-Host "  $commandsDir (command set + command.json)"
Write-Host ""
Write-Host "Next steps:"
Write-Host "  1. Start (or restart) Revit $RevitVersion and open the BIMOne ribbon tab"
Write-Host "  2. Click the modeling service on/off button (default port 8982, override with BIMONE_REVIT_PORT)"
Write-Host "  3. Later command-set-only changes: .\scripts\deploy-hot.ps1  (no restart)"
