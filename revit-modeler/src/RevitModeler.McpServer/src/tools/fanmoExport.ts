import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevit } from "../utils/ConnectionManager.js";

export function registerFanmoExportTool(server: McpServer) {
  server.tool(
    "revit__fanmo_export",
    "导出当前 Revit 文档中墙/楼板的真实几何（位置、长度、墙厚、类型），用于与 CAD 命令做精度校验。",
    {},
    async () => {
      try {
        const result = await withRevit((client) => client.sendCommand("fanmo_export", {}));
        return { content: [{ type: "text", text: JSON.stringify(result, null, 2) }] };
      } catch (error) {
        return {
          isError: true,
          content: [{ type: "text", text: `revit__fanmo_export 失败: ${error instanceof Error ? error.message : String(error)}` }],
        };
      }
    }
  );
}
