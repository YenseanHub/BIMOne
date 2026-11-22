import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevit } from "../utils/ConnectionManager.js";

export function registerFanmoCleanupTool(server: McpServer) {
  server.tool(
    "revit__fanmo_cleanup",
    "按 ElementId 清理 BIMOne 翻模生成的构件，用于重跑前回滚。",
    {
      elementIds: z.array(z.number().int().positive()).min(1).describe("要删除的 Revit ElementId 列表"),
    },
    async (args) => {
      try {
        const result = await withRevit((client) =>
          client.sendCommand("fanmo_cleanup", { elementIds: args.elementIds })
        );
        return { content: [{ type: "text", text: JSON.stringify(result, null, 2) }] };
      } catch (error) {
        return {
          isError: true,
          content: [{ type: "text", text: `revit__fanmo_cleanup 失败: ${error instanceof Error ? error.message : String(error)}` }],
        };
      }
    }
  );
}
