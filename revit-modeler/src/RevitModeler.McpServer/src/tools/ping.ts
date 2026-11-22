import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevit } from "../utils/ConnectionManager.js";

export function registerPingTool(server: McpServer) {
  server.tool(
    "revit__ping",
    "自检 BIMOne 翻模链路：确认 MCP Server 能连到 Revit 插件，并返回已注册的命令清单。排查连接问题时先用这个。",
    {},
    async () => {
      try {
        const result = await withRevit((client) => client.sendCommand("revit__ping", {}, 10000));
        return { content: [{ type: "text", text: JSON.stringify(result, null, 2) }] };
      } catch (error) {
        return {
          isError: true,
          content: [{ type: "text", text: `revit__ping 失败: ${error instanceof Error ? error.message : String(error)}` }],
        };
      }
    }
  );
}
