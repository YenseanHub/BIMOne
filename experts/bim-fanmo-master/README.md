# BIM翻模大师（bim-fanmo-master）

输入一张或多张 DWG 图纸，无项目级图层规则地推断墙/楼板/门窗语义，用图纸内嵌预览图交叉验证，并驱动 Revit 2025+ 占位建模与复查的 Agent 型专家。

## 类型

Agent 型（单个 AI 专家），`expertType: agent`，`agentName: bim-fanmo-master`。

## 文件位置与同步关系

| 角色 | 路径 | 说明 |
| --- | --- | --- |
| **源（改这里）** | `BIMOne/experts/bim-fanmo-master/` | 与代码同仓，可版本管理；本 README 所在目录 |
| **安装副本（WorkBuddy 实际加载）** | `%USERPROFILE%\.workbuddy\plugins\marketplaces\my-experts\plugins\bim-fanmo-master\` | 由同步脚本从源覆盖生成 |
| 分享包 | `BIMOne/outputs/bim-fanmo-master.zip` | 由同步脚本 `-Zip` 生成 |

**改完源必须同步**，否则 WorkBuddy 加载的还是旧版：

```powershell
# 在 BIMOne 工程根目录执行
powershell -ExecutionPolicy Bypass -File scripts/sync-expert.ps1          # 同步到安装目录
powershell -ExecutionPolicy Bypass -File scripts/sync-expert.ps1 -Zip     # 同时重打 zip
```

同步是**覆盖式**的（只增改、不删安装目录里的额外文件）。源目录里的 `.codebuddy-plugin/plugin.json` 会一并同步，不要只复制 `agents/` 和 `skills/`。

## 功能

1. **读取**：`cad__parse_cad(path, outPath?)` 解析 DWG → IR JSON，可落盘到工作目录。
2. **诊断**：图层/图元统计、Z 值检查、Spline 占比、图层名混乱度预判。
3. **语义推断**：图层名关键词 + 几何特征 + 置信度，无项目规则也能先出占位模型；产出带 `confidence` / `needsReview` / `reason` 的建模命令。
4. **交叉验证**：`cad__verify_image(path, irPath?, outPath?)` 用 DWG 内嵌预览图做分区 IoU，输出低一致区作为人工复核线索（不作为最终判据）。
5. **建模与复查**：研判结果写成 `*.commands.json`，由 `revit__fanmo_build(dataPath)` 驱动 Revit；`revit__fanmo_review(outPath)` 回写复查报告。

四个交接文件的约定见 `BIMOne/DATA-LAYER.md`，端点清单见 `BIMOne/CONTRACTS.md`。

## 使用示例

- 我有一张 DWG 图纸，帮我从读取、推断、交叉验证到 Revit 翻模走一遍，并告诉我哪些地方需要人工复核。
- 先只解析这张 DWG，报告图层与图元统计、Z 值问题，先不要建模。
- 用 DWG 内嵌预览图交叉验证解析结果，列出低一致区域和疑似漏解析的地方。

## 前置条件（不要假装工具可用）

- 两个 MCP server 需已写入 `~/.workbuddy/mcp.json` 并挂载：`CadReader.McpServer`（`cad__*`）与 `RevitModeler.McpServer`（`revit__*`）。
- Revit 2025+ 需已安装并加载 `BIMOne.RevitModeler.addin`，插件面板打开服务端。
- 本机未装 Revit 时，只能说"编译通过、待实机烟测"，不能声称建模已跑通。

## 头像

`avatars/expert.jpg`。替换要求：PNG（推荐）或 JPG，512×512 px，单张不超过 500KB。
