import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { registerPingTool } from "./tools/ping.js";
import { registerFanmoBuildTool } from "./tools/fanmoBuild.js";
import { registerFanmoReviewTool } from "./tools/fanmoReview.js";

const server = new McpServer({
  name: "bimone-revit-modeler",
  version: "0.1.0",
});

registerPingTool(server);
registerFanmoBuildTool(server);
registerFanmoReviewTool(server);

async function main() {
  const transport = new StdioServerTransport();
  await server.connect(transport);
  console.error("[bimone] RevitModeler MCP Server started (stdio)");
}

main().catch((error) => {
  console.error("[bimone] 启动失败:", error);
  process.exit(1);
});
