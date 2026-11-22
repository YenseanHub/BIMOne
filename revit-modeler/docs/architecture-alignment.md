# revit-modeler 与 revit-mcp 的目录架构对齐说明

> 目的：解释 revit-modeler 为什么长成现在这样——哪些分层照搬了 `mcp-servers-for-revit`，哪些是翻模系统特有的增补。
>
> **v2 变更（本次）**：从 v1 的「只做 commandset、复用 revit-mcp Host」改为「独立三件套」——
> 自建 `McpServer` + `Plugin` + `CommandSet`，与 revit-mcp 三层一一对应。原因见 §4。

## 1. 一句话结论

revit-modeler 是**独立自包含的三件套**：`RevitModeler.McpServer`（Node/stdio，对 AI 暴露工具）
→ `RevitModeler.Plugin`（Revit add-in，TCP/JSON-RPC Host）→ `RevitModeler.CommandSet`（Revit API 命令实现），
与 `mcp-servers-for-revit` 的 `server/` → `plugin/` → `commandset/` 一一对应。

**它不与 CAD 端共享进程、不直接通信**：两端只通过本地 JSON 文件交接（见 `BIMOne/DATA-LAYER.md`）。
Revit 端是独立插件、独立建模/review，不需要和任何外部 Host 共存于同一 Revit 进程。

## 2. 对齐映射表

| mcp-servers-for-revit | revit-modeler | 对齐方式 |
| --- | --- | --- |
| `server/`（Node.js MCP server，暴露 tools） | `src/RevitModeler.McpServer/` | ✅ 照搬：Node + `@modelcontextprotocol/sdk` + stdio，工具按文件分注册 |
| `server/src/utils/SocketClient.ts` + `ConnectionManager.ts` | `src/RevitModeler.McpServer/src/utils/FanmoRevitClient.ts` + `ConnectionManager.ts` | ✅ 照搬并增强：同 JSON-RPC 协议 + 响应分包 + mutex + 单例连接 |
| `plugin/Core/Application.cs`（Ribbon + 开关） | `src/RevitModeler.Plugin/Core/Application.cs` + `ServerSwitchCommand.cs` | ✅ 照搬：Ribbon「BIMOne 翻模」+ 服务开关按钮 |
| `plugin/Core/SocketService.cs`（TCP/JSON-RPC） | `src/RevitModeler.Plugin/Core/FanmoSocketService.cs` | ✅ 照搬：TCP 监听 + JSON-RPC 2.0，端口独立 8982 |
| `plugin/Core/CommandManager.cs`（反射加载命令） | `src/RevitModeler.Plugin/Core/FanmoCommandLoader.cs` | ✅ 照搬：读 command.json → 反射 `IRevitCommand` → 注册到自己注册表 |
| `plugin/Core/RevitCommandRegistry.cs` / `CommandExecutor.cs` | `src/RevitModeler.Plugin/Core/CommandRegistry.cs` + FanmoSocketService 内联执行 | ✅ 照搬：SDK `ICommandRegistry` + JSON-RPC dispatch |
| `plugin/Core/ExternalEventManager.cs` | — | ⚠️ 刻意省略：SDK `ExternalEventCommandBase` 构造函数自建 `ExternalEvent`、自等待（已由 IL 探针实证），无需复刻 Host 事件管理器 |
| `commandset/Commands/{Architecture,...}/` | `src/RevitModeler.CommandSet/Commands/{Architecture,Review}/` | ✅ 照搬：按业务领域分包，一类一文件 |
| `commandset/Services/{Architecture,...}/` | `src/RevitModeler.CommandSet/Services/{Architecture,Review}/` | ✅ 照搬：EventHandler 按领域分包 |
| `commandset/Models/{Common,...}/` | `src/RevitModeler.CommandSet/Models/{Common,Architecture,Review}/` | ✅ 照搬：DTO 按领域分包，`AIResult<T>` 归 Common |
| `command.json`（声明式命令注册） | 仓库根 `command.json`（`fanmo_build` + `fanmo_review`） | ✅ 照搬：本插件 Host 反射加载（非 revit-mcp 的那张表） |
| `tests/commandset/` | `tests/RevitModeler.Tests/` | ⚠️ 变体：按翻模关注点分包，而非 Revit 元素类别 |
| （revit-mcp 无对应层） | `src/RevitModeler.Core/`（5 个子包） | ➕ 增补：纯逻辑"脑子"，可脱离 RevitAPI 单测 |

## 3. 目录结构（v2）

```
revit-modeler/
├── RevitModeler.sln                # plugin + commandset + core + tests
├── command.json                    # 本插件 Host 反射注册 fanmo_build / fanmo_review
├── docs/
│   └── architecture-alignment.md   # 本文件
├── scripts/
│   └── deploy.ps1                  # 部署完整三件套到独立 addin 目录
├── src/
│   ├── RevitModeler.McpServer/     # 【嘴】Node/stdio，对 AI 暴露 revit__* 工具
│   │   ├── package.json / tsconfig.json
│   │   └── src/
│   │       ├── index.ts            #   McpServer + StdioServerTransport
│   │       ├── tools/{ping,fanmoBuild,fanmoReview}.ts
│   │       └── utils/{FanmoRevitClient,ConnectionManager}.ts
│   ├── RevitModeler.Core/          # 【脑子】纯逻辑，net8.0，不依赖 RevitAPI
│   │   ├── Models/ Semantics/ Mapping/ Serialization/ Utils/
│   ├── RevitModeler.Plugin/        # 【Host】Revit add-in，TCP 8982 + 反射加载命令
│   │   ├── BIMOne.RevitModeler.addin  # 独立 ClientId
│   │   ├── Core/{Application,ServerSwitchCommand,FanmoSocketService,FanmoCommandLoader,CommandRegistry}
│   │   ├── Configuration/CommandManifest.cs
│   │   └── Utils/{PluginConfig,AppLogger}
│   └── RevitModeler.CommandSet/    # 【手】Revit 执行平面
│       ├── Commands/{Architecture,Review}/
│       ├── Services/{Architecture,Review}/
│       ├── Models/{Common,Architecture,Review}/
│       └── Utils/{CadUnits,DocumentUtils}
└── tests/RevitModeler.Tests/
```

## 4. 为什么从「复用 Host」改为「独立三件套」

v1 的决策是「revit-modeler 只做 commandset、挂 revit-mcp Host」，理由是不重复造 Host。
本次改为独立三件套，因为：

1. **契约对称、一眼可辨**：用户需要在文件层面就能分清 server / plugin / commandset 三层。
   复用 Host 会让 revit-modeler 只剩一个 commandset，从目录上看不到另两层，是"单薄感"的根源。
2. **独立可控**：翻模链路的端口、命令注册表、部署目录、工具命名都可自主决定（8982 / revit__* 前缀），
   不受 revit-mcp 版本升级影响。
3. **实证支持**：IL 探针确认 SDK `ExternalEventCommandBase` 自建 `ExternalEvent` + 自等待，
   不依赖 Host 的 `ExternalEventManager`，因此 plugin 层可以很薄，复刻成本远低于预期。

**代价**：无。ClientId / 端口 8982 只是 Revit 插件的常规唯一标识（避免与其他 add-in 冲突），
不是为共存做的隔离设计。

## 5. 与 CAD 端的关系：文件交接，零直接通信

CAD 读取链路是独立的进程外 `cad-reader/CadReader.McpServer`（`cad__parse_cad` / `cad__verify_image`），
与 Revit 端 McpServer 是**两个互不感知的 MCP server**。唯一耦合点是本地 JSON 文件
（详见 `BIMOne/DATA-LAYER.md`）：CAD 写 `*.ir.json` / `*.verify.json`，大模型研判写 `*.commands.json`，
Revit 读 `*.commands.json` 建模、写 `*.review.json`。职责不同，不合并、不通信、不共享运行时。

## 6. 执行链路

```
agent 调 revit__fanmo_build(dataPath=work/t.commands.json)
  → RevitModeler.McpServer 读文件 → (stdio) ── TCP 8982 JSON-RPC: method=fanmo_build ──▶
  → RevitModeler.Plugin FanmoSocketService → CommandRegistry 查 fanmo_build
  → FanmoBuildCommand.Execute(JObject, requestId)：解析 data → Handler.SetParameters
  → RaiseAndWaitForCompletion() → SDK 自建 ExternalEvent 切 UI 线程
  → Handler.Execute(UIApplication)：Transaction 内建墙/板
  → AIResult<T> 原路经 Plugin → McpServer → agent
```
