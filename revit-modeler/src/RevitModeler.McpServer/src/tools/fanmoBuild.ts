import { z } from "zod";
import fs from "fs";
import path from "path";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevit } from "../utils/ConnectionManager.js";

/** 墙/板定位图元，字段与 C# FanmoElement 一一对应，长度单位 mm */
const FanmoElementSchema = z.object({
  handle: z.string().optional().describe("CAD 图元句柄，用于全流程追溯"),
  category: z.string().describe("目标类别：Wall / Floor / Door / Window / Column 等"),
  start: z.array(z.number()).length(3).nullable().optional().describe("定位线起点 [x,y,z]，单位 mm；墙用，楼板可为 null"),
  end: z.array(z.number()).length(3).nullable().optional().describe("定位线终点 [x,y,z]，单位 mm；墙用，楼板可为 null"),
  points: z.array(z.array(z.number()).length(3)).nullable().optional().describe("闭合轮廓顶点（楼板用）[[x,y,z],...]，单位 mm；墙可为 null"),
  baseLevel: z.number().optional().describe("标高 mm，相对项目基准"),
  height: z.number().optional().describe("墙高 / 板厚 mm，默认 3000"),
  typeId: z.number().optional().describe("族类型 Id，-1 表示用默认类型"),
  layer: z.string().optional().describe("来源 CAD 图层"),
  z: z.number().optional().describe("原图 Z 值 mm"),
  wallTypeName: z.string().nullable().optional().describe("期望墙类型名，为空则按墙厚解析"),
  wallThicknessMm: z.number().optional().describe("墙厚 mm，<=0 表示默认墙类型"),
  floorTypeName: z.string().nullable().optional().describe("期望楼板类型名，为空则使用默认楼板类型"),
  floorThicknessMm: z.number().optional().describe("板厚 mm，<=0 表示不覆盖类型默认厚度"),
  familyName: z.string().nullable().optional().describe("族名，门/窗/柱占位用"),
  widthMm: z.number().optional().describe("族实例宽度 mm，门/窗占位用"),
  rotationDeg: z.number().optional().describe("族实例旋转角，单位度"),
});

export function registerFanmoBuildTool(server: McpServer) {
  server.tool(
    "revit__fanmo_build",
    "在 Revit 中按 CAD 图元清单批量建模（墙/楼板占位，LOD100）。二选一：dataPath 读建模命令 JSON 文件（推荐，大模型可直接 Read/Edit 研判），或 data 内联数组。长度单位 mm。需先在 Revit 打开「BIMOne 翻模」服务。",
    {
      dataPath: z.string().optional().describe("建模命令 JSON 文件路径，内容为 FanmoElement 数组（见 DATA-LAYER.md 的 commands.json）。传此参数时优先读文件，忽略 data。"),
      data: z.array(FanmoElementSchema).optional().describe("内联建模命令数组，至少 1 条。与 dataPath 二选一。"),
    },
    async (args) => {
      try {
        let elements: z.infer<typeof FanmoElementSchema>[];

        if (args.dataPath) {
          const full = path.resolve(args.dataPath);
          if (!fs.existsSync(full)) {
            return { isError: true, content: [{ type: "text", text: `建模命令文件不存在: ${full}` }] };
          }
          const parsed = JSON.parse(fs.readFileSync(full, "utf-8"));
          const arr = Array.isArray(parsed) ? parsed : parsed?.data;
          if (!Array.isArray(arr) || arr.length === 0) {
            return { isError: true, content: [{ type: "text", text: `建模命令文件不是非空数组（也非含 data 数组的对象）: ${full}` }] };
          }
          const validated = z.array(FanmoElementSchema).safeParse(arr);
          if (!validated.success) {
            return {
              isError: true,
              content: [{ type: "text", text: `建模命令文件校验失败: ${validated.error.message}` }],
            };
          }
          elements = validated.data;
        } else if (args.data) {
          elements = args.data;
        } else {
          return {
            isError: true,
            content: [{ type: "text", text: "revit__fanmo_build 需要 dataPath 或 data 之一" }],
          };
        }

        if (elements.length === 0) {
          return { isError: true, content: [{ type: "text", text: "建模命令数组为空" }] };
        }

        const result = await withRevit((client) =>
          client.sendCommand("fanmo_build", { data: elements })
        );
        const source = args.dataPath ? `文件 ${path.resolve(args.dataPath)}（${elements.length} 条）` : `内联 ${elements.length} 条`;
        return {
          content: [{ type: "text", text: `已从 ${source} 建模\n${JSON.stringify(result, null, 2)}` }],
        };
      } catch (error) {
        return {
          isError: true,
          content: [{ type: "text", text: `revit__fanmo_build 失败: ${error instanceof Error ? error.message : String(error)}` }],
        };
      }
    }
  );
}
