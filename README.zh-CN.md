# BIMOne

BIMOne 是一套面向建筑 BIM 领域的 **AI 辅助 CAD→Revit 翻模工具链**。它把 DWG/DXF 图纸解析为结构化数据，在没有项目级图层规则的前提下推断墙体、楼板等建筑语义，并可借助 DWG 内嵌预览图做交叉验证，最终通过 Revit 2025 插件生成 LOD100 占位模型和复查报告。

> 核心原则：**数据正确性优先于建模速度**；推断过程可审计，低置信结果交由人工兜底。

## 核心特性

- **双端解耦**：CAD 读取端与 Revit 建模端互不通信、不共享进程，只通过本地 JSON 文件交接；任一步都可单独重跑。
- **CAD 读取与诊断**：基于 `ACadSharp` 解析 DWG/DXF，提取图元类型、图层、坐标、文字、闭合性与 Z 值，并输出统计摘要。
- **无规则语义推断**：结合图层名关键词、几何特征与置信度，把图元推断为墙、楼板、门、窗、柱等类别，并标记待复核项。
- **图像交叉验证**：利用 DWG 内嵌预览图做分区 IoU 对比，输出低一致区域作为定向复核线索；不作为最终判据。
- **Revit 建模与复查**：通过 MCP 工具驱动 Revit 插件，以 Transaction 批量创建墙/楼板占位模型，并回写构件计数与问题清单。
- **文件即契约**：中间产物全程可阅读、可编辑、可 diff，便于大模型研判和人工修正。

## 系统架构

```text
DWG / DXF 图纸
      │
      ▼
[1] cad__parse_cad                         → <task>.ir.json
      │                                      （图元、图层、坐标、闭合、Z 值）
      ▼
[2] 大模型研判 + cad__verify_image（可选） → <task>.verify.json
      │                                      （IoU、coverage、低一致区）
      ▼
[3] 写出建模命令                           → <task>.commands.json
      │                                      （核心交接产物，可直接编辑）
      ▼
[4] revit__fanmo_build(dataPath=...)
      │   RevitModeler.McpServer → TCP 8982 → Plugin → CommandSet
      ▼
[5] revit__fanmo_review                     → <task>.review.json
      │                                      （墙/板计数、问题清单）
      ▼
人工复核与兜底
```

CAD 端只负责读取和验证，Revit 端只负责建模和复查；两端唯一的耦合点是磁盘上的 JSON 文件。

## 目录结构

```text
BIMOne/
├── cad-reader/                         # CAD 读取端（.NET 8）
│   ├── CadReader.sln
│   └── src/
│       ├── CadReader.Core/             # DWG/DXF 解析、IR、内置预览图验证
│       ├── CadReader.Cli/              # 命令行验证与批处理入口
│       └── CadReader.McpServer/        # stdio MCP Server：cad__* 工具
├── revit-modeler/                      # Revit 建模端（独立三件套）
│   ├── RevitModeler.sln
│   ├── command.json                    # fanmo_build / fanmo_review 命令清单
│   ├── docs/architecture-alignment.md
│   ├── scripts/deploy.ps1              # Revit add-in 部署脚本
│   ├── src/
│   │   ├── RevitModeler.McpServer/     # Node/stdio MCP Server：revit__* 工具
│   │   ├── RevitModeler.Core/          # 纯逻辑层，不依赖 RevitAPI，可单测
│   │   ├── RevitModeler.Plugin/        # Revit add-in：Ribbon + TCP JSON-RPC Host
│   │   └── RevitModeler.CommandSet/    # Revit API 命令实现：建模与复查
│   └── tests/RevitModeler.Tests/       # xUnit 测试
├── experts/bim-fanmo-master/           # WorkBuddy「BIM翻模大师」专家包源目录
│   ├── agents/
│   ├── skills/fanmo-workflow/
│   └── .codebuddy-plugin/plugin.json
├── sampleData/一居室公寓.dwg             # 示例图纸
├── scripts/sync-expert.ps1             # 专家包同步脚本
├── work/                               # 默认任务工作目录
├── CONTRACTS.md                        # 端点与契约唯一索引
└── DATA-LAYER.md                       # 共享数据层契约
```

仓库没有统一根解决方案，`cad-reader/` 与 `revit-modeler/` 是两个独立的 .NET 解决方案。

## 环境要求

| 组件 | 要求 |
| --- | --- |
| 操作系统 | Windows（Revit 插件为 x64） |
| .NET SDK | .NET 8 |
| Node.js | >= 20 |
| Revit | 2025 或更高版本（仅建模/部署需要） |
| PowerShell | 5.1 或更高版本 |
| MCP 客户端 | 例如 WorkBuddy，用于加载两个 stdio MCP Server |

主要依赖版本：

- `ACadSharp 3.8.35`：DWG/DXF 解析。
- `Nice3point.Revit.Api.* 2025.*`、`RevitMCPSDK 2025.0.0`：Revit 2025 add-in 与命令集。
- `@modelcontextprotocol/sdk ^1.26.0`、`zod ^3.24.2`：Revit MCP Server。

## 快速开始

以下命令均在项目根目录执行，使用 PowerShell。

### 1. 构建与测试

```powershell
# CAD 读取端
dotnet build cad-reader/CadReader.sln -c Release

# Revit 建模端
dotnet build revit-modeler/RevitModeler.sln -c Release
dotnet test  revit-modeler/RevitModeler.sln -c Release

# Revit MCP Server
cd revit-modeler/src/RevitModeler.McpServer
npm install
npm run build
node smoke-test.mjs
node smoke-file-handoff.mjs
cd ../../..
```

### 2. 用 CLI 解析示例图纸

```powershell
dotnet run --project cad-reader/src/CadReader.Cli -c Release -- `
  sampleData/一居室公寓.dwg work/demo/apartment.ir.json `
  --commands work/demo/apartment.commands.json
```

CLI 默认把完整 IR 写入 `outPath`；加 `--summary` 时 `outPath` 只写统计摘要。加 `--commands <path>` 可基于同一份 IR 直接生成完整 Revit 命令文件，包含墙厚推断、整层楼板轮廓和族占位。

### 3. 部署 Revit 插件

```powershell
powershell -ExecutionPolicy Bypass -File revit-modeler/scripts/deploy.ps1 `
  -RevitVersion 2025 -Configuration Release
```

脚本会构建插件和命令集，并复制到：

```text
%APPDATA%\Autodesk\Revit\Addins\2025\
├── BIMOne.RevitModeler.addin
└── BIMOne.RevitModeler\
    ├── RevitModeler.Plugin.dll 及依赖
    └── Commands\RevitModeler\
        ├── RevitModeler.CommandSet.dll、RevitModeler.Core.dll 及依赖
        └── command.json
```

启动 Revit 后，在功能区打开「BIMOne 翻模」选项卡，点击「翻模服务 开/关」开启服务。

### 4. 配置 MCP Server

两个 MCP Server 都需要以 stdio 方式注册到 MCP 客户端。仓库已说明它们尚未写入 `~/.workbuddy/mcp.json`，请按客户端实际格式添加：

| Server | 用途 | 启动命令示例 |
| --- | --- | --- |
| `CadReader.McpServer` | `cad__*` 工具 | `dotnet <repo>\cad-reader\src\CadReader.McpServer\bin\Release\net8.0\CadReader.McpServer.dll` |
| `RevitModeler.McpServer` | `revit__*` 工具 | `node <repo>\revit-modeler\src\RevitModeler.McpServer\build\index.js` |

`<repo>` 替换为项目根目录的绝对路径。

### 5. 同步 WorkBuddy 专家包

`experts/bim-fanmo-master/` 是源目录。修改专家后必须同步，否则 WorkBuddy 仍加载旧版本：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/sync-expert.ps1
powershell -ExecutionPolicy Bypass -File scripts/sync-expert.ps1 -Zip
```

同步会覆盖安装目录中的同名文件，但不会删除其中的额外文件；`.codebuddy-plugin/plugin.json` 也会被同步。

## MCP 工具

| 工具 | 输入 | 输出 | 用途 |
| --- | --- | --- | --- |
| `cad__parse_cad` | `path`，可选 `summaryOnly`、`outPath` | IR JSON 或“路径 + 摘要” | 解析 DWG/DXF，建议传 `outPath` 落盘 |
| `cad__verify_image` | `path`，可选 `irPath`、`outPath` | IoU、coverage、precision、低一致区 | DWG 内嵌预览图交叉验证 |
| `revit__ping` | 无 | 服务状态与命令列表 | 链路自检 |
| `revit__fanmo_build` | `dataPath`（推荐）或 `data` | 创建的 Revit ElementId 列表 | 批量建模，当前为墙/楼板 LOD100 |
| `revit__fanmo_review` | 可选 `minWallLengthMm`、`outPath` | 复查报告 | 只读扫描墙/板并输出问题清单 |

## 数据交接契约

建议每个任务使用 `work/<task-name>/` 子目录：

```text
work/<task-name>/
├── <task>.ir.json          # cad__parse_cad 写出：结构化图元数据
├── <task>.verify.json      # cad__verify_image 写出：图像交叉验证报告，可选
├── <task>.commands.json    # 大模型研判写出：建模命令，核心交接产物
└── <task>.review.json      # revit__fanmo_review 回写：复查报告
```

关键约定：

- 长度单位统一为 **mm**。
- `commands.json` 中 `start` / `end` / `points` 必须来自 IR 的真实坐标，不得臆造，否则会出现“墙堆原点”。
- 所有文件参数均可选；不落盘时保持内联返回，便于向后兼容调用。
- 文件是无状态快照：两端不维护会话，任何一步都可独立重跑。

详细字段定义见 `DATA-LAYER.md`，端点对照见 `CONTRACTS.md`。

## 配置

| 环境变量 | 默认值 | 作用 |
| --- | --- | --- |
| `BIMONE_REVIT_PORT` | `8982` | Revit 插件监听端口与 MCP 客户端连接端口，两侧必须一致 |
| `BIMONE_REVIT_HOST` | `127.0.0.1` | MCP 客户端连接 Revit 插件的主机，仅客户端使用 |
| `BIMONE_WORKDIR` | `BIMOne/work/` | 文档约定的工作目录覆盖变量；当前代码尚未接线，请勿依赖 |

## 验证状态与已知限制

当前已完成：

- CAD 端真实 DWG 解析与 `outPath` 落盘冒烟。
- Revit 端 .NET 项目编译、12 个纯逻辑测试通过。
- Revit MCP Server `tsc` 编译、基础链路与文件交接链路冒烟通过。

当前限制：

- **Revit 实机未验证**：`ExternalEvent`、UI 线程切换和 `Transaction` 建模需在装有 Revit 2025 的环境实测；未实测前不应声称建模已跑通。
- **两个 MCP Server 尚未写入 `~/.workbuddy/mcp.json`**，WorkBuddy 默认不会加载工具。
- 当前只做墙/楼板 LOD100 占位建模；门、窗、柱等族精化（LOD200+）尚未实现。
- 复查仅检查零长度墙和构件计数；碰撞、闭合、漏画检查待后续实现。
- 图像验证只适用于含内嵌预览图的 DWG；DXF 无此能力。预览图仅包含保存时可见图层，低 IoU 不等于错误，只作为复核线索。
- 平面翻模前应确认 Z 值归零；Spline 需拟合降级；图层名混乱的图纸必须走语义推断与人工复核。
- `BIMONE_WORKDIR` 仅存在于文档约定，当前实现未读取该变量。

## 文档索引

- `CONTRACTS.md`：MCP 工具、交接文件与 Revit 命令的唯一端点索引。
- `DATA-LAYER.md`：四个 JSON 文件的字段契约与端到端数据流。
- `revit-modeler/README.md`：Revit 三件套结构、构建、部署与限制。
- `revit-modeler/docs/architecture-alignment.md`：Revit 端分层设计说明。
- `experts/bim-fanmo-master/README.md`：专家包安装、同步与使用示例。
- `experts/bim-fanmo-master/agents/bim-fanmo-master.md`：专家能力与 Phase 0~5 工作流程。
- `experts/bim-fanmo-master/skills/fanmo-workflow/SKILL.md`：语义推断阈值、图像判读与输出模板。

## 许可与维护

- 维护者：Yensean
- 联系邮箱：`yenseaon@foxmail.com`
- 项目采用 MIT License，详见 `LICENSE`。
