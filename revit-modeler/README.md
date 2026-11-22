# RevitModeler —— CAD 翻模建模端（独立三件套）

对齐 `mcp-servers-for-revit` 的软件架构：**本目录是独立自包含的三件套** ——
`McpServer`（对 AI 暴露工具）→ `Plugin`（Revit 内 Host）→ `CommandSet`（Revit API 命令实现），
三层与原项目 `server/` → `plugin/` → `commandset/` 一一对应。

> 完整对齐说明见 [`docs/architecture-alignment.md`](docs/architecture-alignment.md)；
> 全系统端点契约见 [`../CONTRACTS.md`](../CONTRACTS.md)。

## 目录结构

```
revit-modeler/
├── RevitModeler.sln                # plugin + commandset + core + tests
├── command.json                    # 本插件 Host 反射注册：fanmo_build / fanmo_review
├── docs/architecture-alignment.md  # 与 revit-mcp 的目录对齐说明（v2 独立三件套）
├── scripts/deploy.ps1              # 部署完整三件套到独立 addin 目录
├── src/
│   ├── RevitModeler.McpServer/     # 【嘴】Node/stdio MCP server，暴露 revit__* 工具
│   │   └── src/{index.ts,tools/,utils/}
│   ├── RevitModeler.Core/          # 【脑子】纯逻辑 net8.0，不依赖 RevitAPI，可单测
│   │   ├── Models/                 #   CadEntity / ModelCommand / ModelAction / FamilyMappingRule
│   │   ├── Semantics/              #   无规则场景的图层语义推断（BIM翻模大师主路脑子）
│   │   ├── Mapping/                #   IR → ModelCommand（ModelMapper）
│   │   ├── Serialization/          #   ModelCommand → fanmo_build data JSON
│   │   └── Utils/                  #   纯几何计算（长度/面积/周长）
│   ├── RevitModeler.Plugin/        # 【Host】Revit add-in，TCP 8982 + 反射加载命令
│   │   ├── BIMOne.RevitModeler.addin  # 独立 ClientId
│   │   ├── Core/{Application,ServerSwitchCommand,FanmoSocketService,FanmoCommandLoader,CommandRegistry}
│   │   ├── Configuration/CommandManifest.cs
│   │   └── Utils/{PluginConfig,AppLogger}
│   └── RevitModeler.CommandSet/    # 【手】执行平面 net8.0-windows
│       ├── Commands/{Architecture,Review}/   # FanmoBuildCommand / FanmoReviewCommand
│       ├── Services/{Architecture,Review}/   # 对应 EventHandler（Transaction / 只读复查）
│       ├── Models/{Common,Architecture,Review}/  # AIResult / FanmoElement / ReviewReport
│       └── Utils/                  # CadUnits(mm↔ft) / DocumentUtils(元素查找)
└── tests/RevitModeler.Tests/       # {Mapping,Semantics,Serialization,Integration}
```

## 架构对齐关系

| mcp-servers-for-revit | revit-modeler | 说明 |
| --- | --- | --- |
| `server/`（Node MCP） | `src/RevitModeler.McpServer/` | stdio server，工具按文件分注册，TCP 连插件 |
| `plugin/`（Host Kernel） | `src/RevitModeler.Plugin/` | Ribbon 开关 + TCP 8982 JSON-RPC + 反射加载命令 |
| `commandset/Commands/{Architecture,...}/` | `src/RevitModeler.CommandSet/Commands/{Architecture,Review}/` | 同样按领域分包，`ExternalEventCommandBase` + `Execute(JObject, requestId)` |
| `commandset/Services/`、`Models/`、`Utils/` | 同名子包 | DTO/EventHandler/工具按领域分包 |
| `command.json` | `command.json` | 由本插件 Host 反射加载 |
| `plugin/Core/ExternalEventManager.cs` | 不复制 | SDK 基类自建 ExternalEvent + 自等待（IL 探针实证），无需复刻 |
| （无对应层） | `src/RevitModeler.Core/` | 增补：纯逻辑脑子，可脱离 RevitAPI 单测 |

**独立性**：Revit 端是独立插件、独立建模/review，不与 CAD 端共享进程、不直接通信。ClientId / 端口 8982 只是 Revit add-in 的常规唯一标识，不是为共存做的隔离设计。两端只通过本地 JSON 文件交接（详见 `BIMOne/DATA-LAYER.md`）。

## 执行链路

```
agent 调 revit__fanmo_build(data)  [McpServer, stdio]
  → TCP 8982 JSON-RPC: method=fanmo_build
  → Plugin FanmoSocketService → CommandRegistry 查命令
  → FanmoBuildCommand.Execute(JObject, requestId) → Handler.SetParameters
  → RaiseAndWaitForCompletion() → SDK 自建 ExternalEvent 切 Revit UI 线程
  → Handler.Execute(UIApplication)：建模开 Transaction / 复查只读扫描
  → AIResult<T> 原路回传 → agent
```

## 工具 / 命令清单

MCP 工具（agent 直接调用）：

| 工具 | 说明 | 输入 | 输出 |
| --- | --- | --- | --- |
| `revit__ping` | 链路自检 + 已注册命令清单 | 无 | `{ok,service,port,commands[]}` |
| `revit__fanmo_build` | 按 IR 图元批量建模（LOD100） | **`dataPath`**：建模命令 JSON 文件路径（推荐）；或 `data`：`FanmoElement[]` 内联 | `AIResult<List<int>>` |
| `revit__fanmo_review` | 复查墙/板并输出问题清单 | `minWallLengthMm`（可选，默认 10）、`outPath`（可选，落盘报告） | `AIResult<ReviewReport>` |

> `dataPath` 指向的 commands.json 与内联 `data` 等价——这是 CAD/Revit 两端的唯一交接文件，大模型可直接 Read/Edit。详见 `BIMOne/DATA-LAYER.md`。

底层命令（JSON-RPC method，`command.json` 声明）：`fanmo_build` / `fanmo_review`。

## 构建与验证

```bash
cd BIMOne/revit-modeler
dotnet build -c Release                       # 三件套 .NET 项目全部编译
dotnet test -c Release                        # 12 个测试（含真实 IR 端到端）全绿
cd src/RevitModeler.McpServer
npm install && npm run build                  # 编译 TypeScript
node smoke-test.mjs                           # SMOKE_PASS：基础链路（假 plugin + stdio）
node smoke-file-handoff.mjs                   # FILE_HANDOFF_PASS：dataPath 文件交接链路
```

## 部署

```powershell
.\scripts\deploy.ps1 -RevitVersion 2025 -Configuration Release
```

部署布局：

```
%AppData%/Autodesk/Revit/Addins/2025/
├── BIMOne.RevitModeler.addin          # addin 注册（独立 ClientId）
└── BIMOne.RevitModeler/               # 插件目录
    ├── RevitModeler.Plugin.dll + 依赖
    └── Commands/RevitModeler/         # 独立命令目录
        ├── RevitModeler.CommandSet.dll + RevitModeler.Core.dll + 依赖
        └── command.json
```

启动 Revit → 功能区「BIMOne 翻模」选项卡 → 点「翻模服务 开/关」开启（默认 8982）。
端口可用环境变量 `BIMONE_REVIT_PORT` 覆盖（server 与 plugin 两侧需一致）。

## 已知限制

- 占位建模只做墙/楼板（LOD100），族精化（门/窗/柱 LOD200+）在 P3。
- 复查当前只检查零长度墙 + 构件计数，碰撞/闭合/漏画检查待 P4。
- 本机无 Revit，仅编译验证 + 纯逻辑单测 + 协议冒烟；实机 UI 线程执行与 Transaction 建模需在 Revit 2025 验证。
