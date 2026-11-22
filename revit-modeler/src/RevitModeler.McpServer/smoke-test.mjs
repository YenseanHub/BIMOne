// 端到端冒烟：假 plugin (TCP 8091) + 真 MCP Server (stdio) + JSON-RPC 客户端
import net from "net";
import { spawn } from "child_process";
import path from "path";
import { fileURLToPath } from "url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const serverEntry = path.resolve(__dirname, "build/index.js");

// 1) 假 plugin：监听 8091，收到 JSON-RPC 就回 pong
const fake = net.createServer((sock) => {
  sock.on("error", () => {}); // 子进程被 kill 时会 ECONNRESET，忽略
  sock.on("data", (data) => {
    const req = JSON.parse(data.toString());
    const res = {
      jsonrpc: "2.0",
      id: req.id,
      result: {
        ok: true,
        service: "BIMOne.RevitModeler",
        port: 8091,
        commands: ["fanmo_build", "fanmo_review"],
        echoMethod: req.method,
      },
    };
    sock.write(JSON.stringify(res));
  });
});
await new Promise((resolve) => fake.listen(8091, "127.0.0.1", resolve));
console.log("[test] 假 plugin 已监听 8091");

// 2) 启动 MCP Server 子进程
const child = spawn(process.execPath, [serverEntry], {
  stdio: ["pipe", "pipe", "pipe"],
  env: { ...process.env, BIMONE_REVIT_PORT: "8091" },
});
let stderr = "";
child.stderr.on("data", (d) => { stderr += d.toString(); });

// 3) stdio 客户端：按行发 JSON-RPC（MCP stdio 用换行分隔）
let buf = "";
const waiters = new Map();
child.stdout.on("data", (d) => {
  buf += d.toString();
  let idx;
  while ((idx = buf.indexOf("\n")) >= 0) {
    const line = buf.slice(0, idx).trim();
    buf = buf.slice(idx + 1);
    if (!line) continue;
    try {
      const msg = JSON.parse(line);
      if (msg.id && waiters.has(msg.id)) {
        waiters.get(msg.id)(msg);
        waiters.delete(msg.id);
      }
    } catch {}
  }
});
let idSeq = 0;
function call(method, params) {
  const id = ++idSeq;
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error("timeout: " + method)), 8000);
    waiters.set(id, (msg) => { clearTimeout(timer); resolve(msg); });
    child.stdin.write(JSON.stringify({ jsonrpc: "2.0", id, method, params }) + "\n");
  });
}

try {
  const init = await call("initialize", {
    protocolVersion: "2025-06-18",
    capabilities: {},
    clientInfo: { name: "smoke-test", version: "1.0" },
  });
  console.log("[test] initialize:", init.result?.serverInfo?.name, init.result?.protocolVersion);

  child.stdin.write(JSON.stringify({ jsonrpc: "2.0", method: "notifications/initialized" }) + "\n");

  const list = await call("tools/list", {});
  const names = list.result?.tools?.map((t) => t.name);
  console.log("[test] tools/list:", names);

  const ping = await call("tools/call", { name: "revit__ping", arguments: {} });
  console.log("[test] tools/call revit__ping:", ping.result?.content?.[0]?.text);

  const ok =
    init.result?.serverInfo?.name === "bimone-revit-modeler" &&
    names?.includes("revit__fanmo_build") &&
    names?.includes("revit__fanmo_review") &&
    names?.includes("revit__ping") &&
    ping.result?.content?.[0]?.text?.includes("\"echoMethod\": \"revit__ping\"");
  console.log(ok ? "\nSMOKE_PASS" : "\nSMOKE_FAIL");
  if (!ok) console.log("stderr:", stderr);
  process.exitCode = ok ? 0 : 1;
} catch (e) {
  console.error("[test] 失败:", e, "\nstderr:", stderr);
  process.exitCode = 1;
} finally {
  child.kill();
  fake.close();
}
