# 族库能力建设方案（只出方案，不改代码）

## 一、问题定义：先把"族库缺失"拆成三种

实测探针（`fanmo_diagnose`）结果：当前文档柱族 13 个类型、梁族 20 个类型，一个不少；
但整个仓库里 **0 个 `.rfa` 文件**。所以"族库老是缺失"不是一个问题，是三种：

| 类型 | 表现 | 现状代码行为 | 危害 |
|---|---|---|---|
| **真缺失** | 文档里没有该类别族（`symbols.Count == 0`） | `ResolveSectionSymbol` 返回 null | 构件**直接跳过不建**，静默丢构件 |
| **语义缺失** | 族在、类型不对 | 用 `BIMOne_Column_600x600`（从「热轧普通工字钢柱」复制），或 fallback 到无 b/h 参数的「热轧无缝钢管」 | 截面数字对、族语义错；或截面静默不符 |
| **资产缺失** | 没有外部 `.rfa` 库 | 无加载逻辑，完全依赖当前样板预置 | 换个样板 / 新文档就归零 |

## 二、架构结论：不建独立族库 MCP

**保持"业务读写统一走 MCP 单一通道"原则**：族装载必然要改 Revit 文档，绕不开
`ExternalEvent`，独立 MCP 只能再转发一层，多一跳、多一个失败点、多一处配置。
族资产是只读文件，不配拥有写入通道。

```
AI / 客户端 ──► 现有 Revit MCP（唯一写入通道）
                  ├─ 现有命令：fanmo_build / export / zoom
                  └─ 新增族命令：fanmo_family_probe / index / ensure
                                          │                    ▲
                                          ▼                    │ 只读索引
                              用户指定路径的族资产库（.rfa + manifest）
```

- **族资产层**：用户指定的目录，只读，不进 MCP 写入链路。
- **命令层**：三个 `fanmo_family_*` 命令加进现有 CommandSet，走热更新，不需要重启 Revit。
- **复用原则**：`fanmo_build` 内部必须调用同一个 `ensure` 逻辑，不允许两套族解析代码并存。

## 三、族库路径配置（用户可指定）

沿用 `PluginConfig` 现有套路（端口走 `BIMONE_REVIT_PORT` 环境变量），新增**两级配置**：

1. **环境变量 `BIMONE_FAMILY_ROOT`**：全局默认族库根目录。未设置时为空 → 退化为"只用文档内族"，不报错。
2. **命令参数 `familyRoot`**：`fanmo_family_index` / `fanmo_family_ensure` 可显式传路径覆盖环境变量，
   便于一次会话里切换不同项目的族库。

路径解析规则：
- 支持绝对路径与相对路径（相对路径以命令集工作目录为基准）；
- 目录不存在时返回明确的 `ok=false` + 路径，不静默当作空库；
- 只扫描目录一级 + `families/` 子目录，不递归全盘。

## 四、命令契约（设计，未实现）

### `fanmo_family_probe`（只读）
列出当前文档全部 `FamilySymbol`：族名 / 类型名 / 类别 / 可用参数槽 / 是否可写截面 / 已被 BIMOne 引用情况。
取代 `fanmo_diagnose` 里那两个粗糙的 `columnSymbols` / `beamSymbols` 探针。
用途：回答"当前文档到底有什么族"，不需要指定路径。

### `fanmo_family_index`（只读）
```
params: { familyRoot?: string }
resp:   { root, files: [{ file, familyName, category, symbols, paramSlots, semanticTags }], warnings }
```
扫描 `familyRoot` 下的 `.rfa` 和 `manifest.json`，返回资产清单。
不加载任何族、不改文档。用途：建模前让 AI 知道"有什么可加载"。

### `fanmo_family_ensure`（写命令）
```
params: { category, bMm, hMm, prefer?: "concrete"|"steel"|..., familyRoot?, typePrefix? }
resp:   { ok, source: "document"|"loaded"|"placeholder", symbolId, symbolName, note }
```
降级链（**关键**，修复静默跳过）：
1. 文档内同名同截面类型 → 直接复用；
2. 文档内其他可写截面族 → Duplicate + 写 b/h + 回读校验；
3. 从 `familyRoot` 找匹配 `.rfa` → `LoadFamily` + 选符号 + `Activate` + 写截面；
4. 都不行 → 返回 `ok=false` 和明确原因，**由 build 决定是报错还是建占位构件**，不再静默丢弃。

## 五、技术手段清单（按性价比排序）

| # | 手段 | 解决什么 |
|---|---|---|
| 1 | 修 `ResolveSectionSymbol` 降级链，去掉静默 `return null` | 真缺失丢构件 |
| 2 | manifest JSON：族名/类别/参数槽/语义标签 | AI 建模前知道有什么族、参数叫什么 |
| 3 | 内置少量种子族 `.rfa`（可选，放插件目录） | 换样板归零，不依赖用户配置 |
| 4 | Type Catalog `.txt` | 一次加载批量生成柱梁类型 |
| 5 | 扩展 `FamilyMappingRule`：图层 → 类别 + 截面 + 语义偏好 | 语义缺失，混凝土柱不再用钢柱族 |

## 六、manifest 格式约定

`familyRoot/manifest.json`（可选，缺失时按文件名和族元数据猜测）：

```json
{
  "version": 1,
  "families": [
    {
      "file": "concrete/矩形柱.rfa",
      "familyName": "混凝土 - 矩形 - 柱",
      "category": "OST_StructuralColumns",
      "paramSlots": { "b": "b", "h": "h" },
      "semanticTags": ["concrete", "rectangular"],
      "note": "混凝土矩形柱，b/h 可写"
    }
  ]
}
```

## 七、实施顺序（待批准后执行）

1. `fanmo_family_probe` + `ResolveSectionSymbol` 降级链修复 + 路径配置骨架；
2. `fanmo_family_index` + manifest 约定；
3. `fanmo_family_ensure` + `fanmo_build` 接入同一逻辑；
4. 可选：种子族与 Type Catalog。

每步都配可执行验证：probe 看真实族清单、build 后 `fanmo_export` 核对构件数量零丢失。

## 八、验收判据

- 空样板（无任何族）下 build 柱梁：要么建成、要么明确报错，**不允许静默少构件**；
- 指定 `familyRoot` 后 `fanmo_family_index` 能列出 `.rfa` 清单，`ensure` 能加载出可用符号；
- 换样板 / 换族库路径后重复 build，结果与配置一致，无类型无限增殖；
- 全程热更新生效，不重启 Revit。
