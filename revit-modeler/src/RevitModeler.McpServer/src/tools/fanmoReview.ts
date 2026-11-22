import { z } from "zod";
import fs from "fs";
import path from "path";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevit } from "../utils/ConnectionManager.js";

export function registerFanmoReviewTool(server: McpServer) {
  server.tool(
    "revit__fanmo_review",
    "复查当前 Revit 模型的墙/楼板：统计数量并输出问题清单（零长度墙、坐标缺失等）。只读，不改动模型。可传 outPath 把报告写入 JSON 文件供大模型 Read。",
    {
      minWallLengthMm: z.number().optional().describe("零长度墙的最小阈值 mm，默认 10"),
      outPath: z.string().optional().describe("可选。把复查报告写入该 JSON 文件（见 DATA-LAYER.md 的 review.json），返回路径+报告。"),
    },
    async (args) => {
      try {
        const result = await withRevit((client) =>
          client.sendCommand("fanmo_review", { minWallLengthMm: args.minWallLengthMm ?? 10 })
        );
        const reportJson = JSON.stringify(result, null, 2);

        if (args.outPath) {
          const full = path.resolve(args.outPath);
          const dir = path.dirname(full);
          fs.mkdirSync(dir, { recursive: true });
          fs.writeFileSync(full, reportJson, "utf-8");
          return {
            content: [{ type: "text", text: `复查报告已写入: ${full}\n${reportJson}` }],
          };
        }
        return { content: [{ type: "text", text: reportJson }] };
      } catch (error) {
        return {
          isError: true,
          content: [{ type: "text", text: `revit__fanmo_review 失败: ${error instanceof Error ? error.message : String(error)}` }],
        };
      }
    }
  );
}
