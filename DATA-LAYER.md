# BIMOne 共享数据层：本地 JSON 文件交接契约

> 设计原则：**CAD 读取端与 Revit 建模端互不感知、不共享进程、不直接通信**。
> 两者只通过一组落在磁盘上的 JSON 文件交接数据。大模型（「BIM翻模大师」专家）
> 可以直接 `Read`/`Edit` 这些文件——**文件即契约，也是大模型的工作记忆**。

## 1. 为什么是文件而不是共享 Host / 内存 / SQLite

| 方案 | 否决/采纳理由 |
| --- | --- |
| 共享 Host（同一 Revit 进程内两插件） | ❌ 用户明确不需要共存于一个 Revit；两个能力生命周期绑死，互相拖累 |
| 两 server 直接 TCP/HTTP 通信 | ❌ 引入耦合与可用性强依赖（建模必须等读取端在线）；中间产物不可审阅 |
| 本地 SQLite | ⚠️ 可行但**不利于大模型直接阅读**；结构化查询优势在当前单图纸/少图纸场景用不上 |
| **本地 JSON 文件** | ✅ 大模型可直接读写研判、可 diff/版本管理、两端彻底解耦、零新增依赖 |

## 2. 工作目录约定

默认工作目录：`BIMOne/work/`（可用环境变量 `BIMONE_WORKDIR` 覆盖）。
每次翻模任务建议一个子目录，文件名约定：

```
BIMOne/work/<task-name>/
├── <task>.ir.json          # (1) CAD 读取产物：CadParser 的结构化 IR
├── <task>.verify.json      # (2) 图像交叉验证报告（可选，定向复核线索）
├── <task>.commands.json    # (3) 语义映射后的建模命令（大模型研判/可编辑的核心产物）
└── <task>.review.json      # (4) Revit 复查报告（建模后端回写）
```

> 文件是**无状态快照**：工具不维护会话，每次调用只读写路径指向的文件。
> 这让两端可以独立重启、独立执行，也便于中断后从任意文件继续。

## 3. 四个文件的结构契约

### (1) `<task>.ir.json` — CAD 读取产物

由 `cad__parse_cad(path, outPath?)` 写出。顶层字段（camelCase）：

```json
{
  "source": "一居室公寓.dwg",
  "version": "AC1015",
  "units": "mm",
  "stats": { "total": 949, "byType": {}, "layers": [], "nonZeroZ": 0 },
  "entities": [
    {
      "handle": "2A3",
      "type": "LwPolyline",
      "layer": "0",
      "closed": true,
      "start": [0.0, 0.0, 0.0],
      "end": [3600.0, 0.0, 0.0],
      "points": [[0,0,0], [3600,0,0], [3600,2700,0]],
      "length": 3600.0,
      "text": null
    }
  ]
}
```

**关键**：`start/end/points` 是真实坐标（mm），必须贯穿到建模命令，否则墙堆原点。

### (2) `<task>.verify.json` — 图像交叉验证报告

由 `cad__verify_image(path, outPath?)` 写出。字段：`globalIoU` / `previewCoverage` /
`irPrecision` / `regions[]` / `verdict`。**定位是定向复核线索，不是判据**。

### (3) `<task>.commands.json` — 建模命令（核心交接产物）

由大模型研判 IR 后写出（可借助 `RevitModeler.Core` 的映射逻辑，但文件本身可被人工/模型任意编辑）。
结构与 `revit__fanmo_build` 的 `data` 参数完全一致——**这让文件与内联两种调用方式等价**：

```json
[
  {
    "handle": "2A3",
    "category": "Wall",
    "start": [0.0, 0.0, 0.0],
    "end": [3600.0, 0.0, 0.0],
    "points": null,
    "baseLevel": 0,
    "height": 3000,
    "typeId": -1,
    "layer": "0",
    "z": 0,
    "wallThicknessMm": 240,
    "wallTypeName": "BIMOne_Wall_240mm"
  },
  {
    "handle": "2B1",
    "category": "Floor",
    "start": null,
    "end": null,
    "points": [[0,0,0], [3600,0,0], [3600,2700,0], [0,2700,0]],
    "baseLevel": 0,
    "height": 120,
    "typeId": -1,
    "layer": "FLOOR",
    "z": 0,
    "floorThicknessMm": 120,
    "floorTypeName": "BIMOne_Floor_120mm"
  },
  {
    "handle": "DOOR_12",
    "category": "Door",
    "start": [1200, 0, 0],
    "end": null,
    "points": null,
    "baseLevel": 0,
    "height": 2100,
    "typeId": -1,
    "layer": "0",
    "z": 0,
    "familyName": "单扇门",
    "widthMm": 900
  }
]
```

字段与 C# `FanmoElement` 一一对应（见 `Models/Architecture/FanmoElement.cs`），长度单位 mm。

完整翻模扩展字段：

| 字段 | 作用 |
| --- | --- |
| `wallThicknessMm` / `wallTypeName` | 墙厚与目标墙类型；Revit 端按名称/厚度查找，找不到则复制默认类型并设宽 |
| `floorThicknessMm` / `floorTypeName` | 板厚与目标楼板类型 |
| `familyName` / `widthMm` / `rotationDeg` | 门/窗/柱等族占位 |

**坐标必须来自 IR 的真实 start/end/points，不得臆造**。

### (4) `<task>.review.json` — 复查报告

由 `revit__fanmo_review(outPath?)` 回写。字段：`success` / `message` /
`data.wallCount` / `data.floorCount` / `data.issues[]`（问题清单）。

## 4. 工具的文件参数

| 工具 | 参数 | 行为 |
| --- | --- | --- |
| `cad__parse_cad` | `outPath?` | 传则写 IR 到该文件并返回路径+摘要；不传则保持原行为（返回完整 IR 文本） |
| `cad__verify_image` | `outPath?` | 传则写报告到文件；不传则原行为 |
| `revit__fanmo_build` | `dataPath?` / `data?` | 二选一：`dataPath` 从文件读建模命令数组；`data` 内联数组。文件优先 |
| `revit__fanmo_review` | `outPath?` | 传则写复查报告到文件；不传则原行为 |

> **向后兼容**：所有文件参数可选，老调用方式（内联/文本返回）继续有效。

## 5. 端到端数据流（文件驱动）

```
[1] cad__parse_cad(dwg, outPath=work/t.ir.json)
        ↓ 写 t.ir.json
[2] 专家 Read t.ir.json → 语义研判 + 图像复核(cad__verify_image)
        ↓ 写 t.commands.json（核心交接产物，大模型可直接编辑）
[3] revit__fanmo_build(dataPath=work/t.commands.json)
        → McpServer 读文件 → TCP 8982 → Plugin → CommandSet 建模
        ↓
[4] revit__fanmo_review(outPath=work/t.review.json) → 回写复查报告
        ↓
[5] 专家 Read t.review.json → 问题清单 → 人工兜底
```

**两端零直接通信**：CAD server 写完文件即结束；Revit server 只读文件、不认识 CAD server。
任何一步都可单独重跑，中间产物全程可审阅。
