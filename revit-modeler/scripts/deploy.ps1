# BIMOne RevitModeler「独立三件套」部署脚本
# 把 ① Revit 插件(plugin) ② 命令集(commandset) ③ command.json 部署到独立目录，
# 与 mcp-servers-for-revit Host 完全隔离（独立 ClientId / 端口 8091 / 命令目录）。

param(
    [string]$RevitVersion = "2025",
    [string]$Configuration = "Release",
    [string]$AddinsRoot = (Join-Path $env:APPDATA "Autodesk/Revit/Addins/$RevitVersion")
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$tfm = "net8.0-windows10.0.19041.0"

$pluginProj = Join-Path $projectRoot "src/RevitModeler.Plugin/RevitModeler.Plugin.csproj"
$cmdSetProj = Join-Path $projectRoot "src/RevitModeler.CommandSet/RevitModeler.CommandSet.csproj"
$addinSource = Join-Path $projectRoot "src/RevitModeler.Plugin/BIMOne.RevitModeler.addin"
$commandJson = Join-Path $projectRoot "command.json"

# 构建两个 .NET 项目
Write-Host "构建 RevitModeler.Plugin ..."
dotnet build $pluginProj -c $Configuration --nologo | Out-Null
Write-Host "构建 RevitModeler.CommandSet ..."
dotnet build $cmdSetProj -c $Configuration --nologo | Out-Null

$pluginOut = Join-Path $projectRoot "src/RevitModeler.Plugin/bin/$Configuration/$tfm"
$cmdSetOut = Join-Path $projectRoot "src/RevitModeler.CommandSet/bin/$Configuration/$tfm"

if (-not (Test-Path $pluginOut)) { Write-Error "找不到 plugin 构建产物: $pluginOut"; exit 1 }
if (-not (Test-Path $cmdSetOut)) { Write-Error "找不到 commandset 构建产物: $cmdSetOut"; exit 1 }

# 独立插件目录：%AddinsRoot%/BIMOne.RevitModeler/
$pluginDir = Join-Path $AddinsRoot "BIMOne.RevitModeler"
$commandsDir = Join-Path $pluginDir "Commands/RevitModeler"
New-Item -ItemType Directory -Force -Path $commandsDir | Out-Null

# ① addin 注册文件放 Addins 根
Copy-Item -Force $addinSource (Join-Path $AddinsRoot "BIMOne.RevitModeler.addin")

# ② 插件 DLL + 全部依赖放插件目录
Get-ChildItem -Path $pluginOut -Filter "*.dll" | Copy-Item -Force -Destination $pluginDir
Get-ChildItem -Path $pluginOut -Filter "*.pdb" -ErrorAction SilentlyContinue | Copy-Item -Force -Destination $pluginDir

# ③ 命令集 DLL + Core + command.json 放独立命令目录
Get-ChildItem -Path $cmdSetOut -Filter "*.dll" | Copy-Item -Force -Destination $commandsDir
Get-ChildItem -Path $cmdSetOut -Filter "*.pdb" -ErrorAction SilentlyContinue | Copy-Item -Force -Destination $commandsDir
Copy-Item -Force $commandJson (Join-Path $commandsDir "command.json")

Write-Host "已部署到:"
Write-Host "  $AddinsRoot/BIMOne.RevitModeler.addin"
Write-Host "  $pluginDir (插件 + 依赖)"
Write-Host "  $commandsDir (命令集 + command.json)"
Write-Host ""
Write-Host "下一步:"
Write-Host "  1. 启动 Revit $RevitVersion，功能区出现「BIMOne 翻模」选项卡"
Write-Host "  2. 点「翻模服务 开/关」开启服务（默认端口 8091，可用环境变量 BIMONE_REVIT_PORT 覆盖）"
Write-Host "  3. 在 MCP 客户端配置 RevitModeler.McpServer（build/index.js）"
