# BIMOne 翻模系统：端点与契约清单

> 本文件是**唯一的端点索引**：把「MCP 工具」与「共享数据层文件」「Revit 命令」放在同一张表里对照，
> 让每类边界从文件上一眼可辨。
>
> **架构原则**：CAD 读取端与 Revit 建模端**彼此独立、互不通信、不共享进程**。
> 唯一耦合点是本地 JSON 文件（见 [`DATA-LAYER.md`](DATA-LAYER.md)）。不需要两个 Revit 插件共存。

## 1. 两个独立端点

| 维度 | CAD 读取端 | Revit 建模端 |
| --- | --- | --- |
| 顶层目录 | `cad-reader/` | `revit-modeler/` |
| MCP Server | `src/CadReader.McpServer/`（net8.0，手写 JSON-RPC over stdio） | `src/RevitModeler.McpServer/`（Node/stdio，MCP SDK） |
| 运行位置 | 进程外，独立 stdio 进程 | 进程外 stdio server + 进程内 Revit add-in |
| 直接通信 | ❌ 不与 Revit 端通信 | ❌ 不与 CAD 端通信 |
| 交接方式 | 写 JSON 文件（`*.ir.json` / `*.verify.json`） | 读 `*.commands.json`，写 `*.review.json` |

**Revit 建模端内部三层**（对齐 `mcp-servers-for-revit` 的分层范式，但独立部署）：

| 层 | 项目 | 职责 |
| --- | --- | --- |
| MCP Server | `RevitModeler.McpServer/`（Node/stdio） | 暴露 `revit__*` 工具；读 commands.json → TCP 发命令 |
| Plugin Host | `RevitModeler.Plugin/`（Revit add-in） | TCP 8982 JSON-RPC + 反射加载 command.json |
| CommandSet | `RevitModeler.CommandSet/` | `fanmo_build` / `fanmo_review` 的 Revit API 实现 |

> 端口 8982 与 add-in 的 ClientId 只是 Revit 插件的常规唯一标识，避免与其他 add-in 冲突，
> **不是为与任何外部 Host 共存做的隔离设计**。可用 `BIMONE_REVIT_PORT` / `BIMONE_REVIT_HOST` 覆盖连接参数。

## 2. MCP 工具总表（agent 直接调用）

### 2.1 CAD 读取（`CadReader.McpServer`）

| 工具 | 输入 | 输出 | 用途 |
| --- | --- | --- | --- |
| `cad__parse_cad` | `path`（DWG/DXF 绝对路径）、`summaryOnly`（可选）、`outPath`（可选，落盘 IR） | IR JSON 或「文件路径+摘要」 | 数据读取阶段 |
| `cad__verify_image` | `path`、可选 `irPath` / `outPath` | IoU / coverage / 低一致区清单 / verdict（可落盘） | 低置信图元的图像交叉验证（定向线索，非判据） |

### 2.2 Revit 建模与复查（`RevitModeler.McpServer`）

| 工具 | 输入 | 输出 | 用途 |
| --- | --- | --- | --- |
| `revit__ping` | 无 | `{ok, service, port, commands[]}` | 链路自检 |
| `revit__fanmo_build` | `dataPath`（建模命令 JSON 文件，**推荐**）或 `data`（内联数组） | `AIResult<List<int>>`（ElementId 列表） | 数据建模阶段：Transaction 建墙/楼板（LOD100） |
| `revit__fanmo_review` | `minWallLengthMm`（可选，默认 10）、`outPath`（可选，落盘报告） | `AIResult<ReviewReport>`（可落盘） | 模型复查阶段：只读扫描 |

> 工具名 `{ns}__{cmd}`：`cad__` = 读取端、`revit__` = 建模端。
> 对外工具 `revit__fanmo_build` 与底层 JSON-RPC method `fanmo_build`（CommandName）分层，后者不变。

## 3. 共享数据层：四个 JSON 文件

详见 [`DATA-LAYER.md`](DATA-LAYER.md)。摘要：

| 文件 | 生产者 → 消费者 | 结构要点 |
| --- | --- | --- |
| `<task>.ir.json` | `cad__parse_cad` → 大模型研判 | 图元几何/图层/文字/闭合/Z；**start/end/points 真实坐标(mm)** |
| `<task>.verify.json` | `cad__verify_image` → 大模型研判 | IoU / coverage / precision / regions |
| `<task>.commands.json` | 大模型研判写出 → `revit__fanmo_build(dataPath)` | FanmoElement[]；**唯一交接产物，可直接编辑** |
| `<task>.review.json` | `revit__fanmo_review` 回写 → 大模型/人工 | 墙板计数 + 问题清单 |

## 4. 数据流（文件驱动，两端零直接通信）

```
[1] cad__parse_cad(dwg, outPath=work/t.ir.json) ──写──▶ t.ir.json
[2] 专家 Read t.ir.json → 语义研判 + cad__verify_image(可选)
                                    ──写──▶ t.commands.json
[3] revit__fanmo_build(dataPath=t.commands.json)
        → McpServer 读文件 → TCP 8982 → Plugin → CommandSet 建模
[4] revit__fanmo_review(outPath=t.review.json) ──写──▶ t.review.json
[5] 专家 Read t.review.json → 问题清单 → 人工兜底
```

**关键契约点**：IR 的 `start/end/points` 坐标(mm)必须贯穿到 `commands.json`，否则墙堆原点——
已由 `CommandSerializer` 过滤无坐标图元 + 端到端测试守住。文件是无状态快照，任一步可单独重跑。

## 5. 验证状态与未接线项

- ✅ `CadReader.McpServer`：真实 DWG 解析（949 图元）；`outPath` 落盘冒烟通过（540KB IR，坐标字段齐全）。
- ✅ `RevitModeler.CommandSet`：编译通过，12 个纯逻辑测试全绿。
- ✅ `RevitModeler.Plugin`：编译通过。
- ✅ `RevitModeler.McpServer`：tsc 通过；`smoke-test.mjs` SMOKE_PASS（基础链路）、`smoke-file-handoff.mjs` FILE_HANDOFF_PASS（dataPath 文件交接）。
- ⚠️ **实机未验证**：Revit 2025 内 UI 线程执行 / Transaction 建模；端口监听需实机确认。
- ⚠️ **WorkBuddy MCP 配置**：两个 server 尚未写入 `~/.workbuddy/mcp.json`，WorkBuddy 不会自动加载工具。
