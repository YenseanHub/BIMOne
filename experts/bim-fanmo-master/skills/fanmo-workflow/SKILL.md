---
name: fanmo-workflow
description: CAD(DWG/DXF)→Revit 翻模的标准工作流、语义推断规则、图像交叉验证判读与复查清单。当执行 BIM 翻模、CAD 图层推断、图纸交叉验证、Revit 模型复查时使用。
---

# CAD→Revit 翻模工作流

本 skill 固化「BIM翻模大师」的执行细节。主流程见 `SKILL.md` 的 Phase 0~5，这里提供**判据、阈值、输出模板**。

## 〇、文件交接协议（CAD 端与 Revit 端唯一耦合点）

两端**不共享进程、不直接通信**，只通过本地 JSON 文件交接（详见 `BIMOne/DATA-LAYER.md`）：

```
work/<task>.ir.json          ← cad__parse_cad(path, outPath=...)
work/<task>.verify.json      ← cad__verify_image(path, outPath=...)
work/<task>.commands.json    ← 你研判后写出（核心产物，可直接 Read/Edit）
work/<task>.review.json      ← revit__fanmo_review(outPath=...) 回写
```

- Phase 1 落盘 IR 后 `Read` 它做研判；Phase 4 写出 commands.json；Phase 5 把它作为 `revit__fanmo_build(dataPath=...)` 的输入。
- **commands.json 是唯一交接产物**：改研判结果只改这个文件，不必重跑 CAD、不必动 Revit。
- 每项坐标必须来自 IR 真实 `start/end/points`；`category` 用 `Wall`/`Floor`/`Door` 等英文（与 C# `FanmoElement` 对齐）。

## 一、语义推断判据（无项目规则时）

优先级从高到低，命中即停：

1. **图层名关键词**（高置信 0.88~0.95）
   墙/WALL、门/DOOR、窗/WINDOW、柱/COLUMN、楼板/FLOOR/SLAB、标注/DIM/TEXT、家具/家俱/FURNITURE、天花/CEILING、地面/FINISH。
2. **文字图元**（0.95）→ 标注，Skip。
3. **块名含关键词**（0.55~0.75）→ 对应族类，LOD200 时用。
4. **几何特征**
   - 闭合多段线且面积 ≥1㎡ → 楼板（0.72）；<1㎡ → 楼板（0.55，待复核）
   - 线段长 ≥600mm 且有平行近邻（间距 20~400mm）→ 墙（0.72，双线墙）
   - 线段长 ≥600mm 无平行近邻 → 墙占位（0.45，**必须图像复核**）
   - 圆弧 → 门开启线（0.45，待复核）
   - 圆 → 柱（0.40，待复核）

置信度分档：≥0.7 直接建模｜0.4~0.7 占位+标记待复核｜<0.4 跳过并列问题清单。
阈值定义在 `RevitModeler.Core/LayerSemantics.cs`，可由专家在研判中调整但需记录理由。

## 二、图像交叉验证判读

- 工具：`cad__verify_image(path, irPath?, outPath?)`，图像源 = DWG 内嵌预览图（DwgReader.ReadPreview）。传 `outPath` 落盘报告供 Read。
- 关键字段：`globalIoU`、`previewCoverage`（预览线被解析覆盖比例）、`irPrecision`（解析线在预览有支撑比例）、`regions`（低一致网格）。
- **判读原则**：
  - `previewCoverage` 低 → 重点怀疑**漏解析**（图层被跳过、Z 值异常、类型不支持）。
  - `irPrecision` 低 → 重点怀疑**误判**（标注/家具被当成墙）。
  - 单格 IoU<0.15 且提示"预览有线解析缺失" → 定位该网格对应图纸坐标，检查该区域图元。
  - 预览图只含保存时可见图层、线宽 2~3px → 存在系统性偏差，**低 IoU 不等于错误**。
- **多轮收敛**：不一致 → 换证据/调阈值/问用户 → 重推断 → 再验证。两轮仍不一致 → 明确标记为"需人工确认"，不强行下结论。

## 三、常见坑与处置

| 现象 | 处置 |
|---|---|
| Z 值非零 | 平面翻模必须先 Z 归零，否则墙板飘空间 |
| Spline 占比高 | 样条拟合降级为多段线，不能直接成墙 |
| 图层名全是 0/3/数字 | 走几何特征推断 + 图像复核，不要套图层名规则 |
| 大量短线段 | 多为家具/标注细节，低置信跳过比误建成墙好 |
| 墙全堆原点 | 检查 IR 的 start/end/points 是否贯穿（CommandSerializer 会拒绝无坐标图元）|
| 编译通过但无模型 | 本机无 Revit 时属正常，ExternalEvent/Transaction 需实机验证 |

## 四、输出模板

### 单张图纸报告

```
## 图纸：<文件名>
- 读取：N 图元 / M 图层 / Z 值：(归零|漂移 x~y) / 类型分布：...
- 语义推断：高置信 A / 中置信 B / 低置信 C / 跳过 D
- 图像验证：IoU=x.xx, coverage=x.xx, precision=x.xx, 低一致区 N 处
- 建模：墙 W / 楼板 F / 族实例 P（LOD100 占位）

| 图元 | 图层 | 推断类别 | 置信度 | 依据 | 图像验证 | 处理 |
|---|---|---|---|---|---|---|
| ... | ... | ... | ... | ... | ... | 建模/待复核/跳过 |

### 已确认建模 / 待人工复核 / 已跳过（附原因）
```

### 多张图纸汇总

| 图纸 | 图元数 | 可执行命令 | 墙 | 楼板 | 待复核 | 图像 IoU |
|---|---|---|---|---|---|---|
| ... | ... | ... | ... | ... | ... | ... |

## 五、边界声明（必须随报告给出）

1. 交叉验证只能显著降低错漏，**不能保证 100%**，最终质量靠人工在 Revit 复核。
2. 无 Revit 2025 实机时，只能声明"编译通过/逻辑验证"，不能声称模型已生成。
3. 族库精化（LOD200）需要真实 .rfa 族文件；无族库时只做 LOD100 占位。
