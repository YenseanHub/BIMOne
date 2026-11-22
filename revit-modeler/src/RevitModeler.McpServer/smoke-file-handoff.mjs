// 文件交接冒烟：验证 revit__fanmo_build(dataPath) 能从磁盘读 commands.json 并转发给 plugin
import net from "net";
import { spawn } from "child_process";
import fs from "fs";
import path from "path";
import os from "os";
import { fileURLToPath } from "url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const serverEntry = path.resolve(__dirname, "build/index.js");

// 1) 临时 commands.json（大模型研判后的产物）
const tmp = fs.mkdtempSync(path.join(os.tmpdir(), "bimone-"));
const commands = [
  { handle: "52", category: "Wall", start: [0, 0, 0], end: [3600, 0, 0], baseLevel: 0, height: 3000, layer: "0", z: 0 },
  { handle: "60", category: "Floor", points: [[0,0,0],[3600,0,0],[3600,2700,0],[0,2700,0]], baseLevel: 0, height: 120, layer: "FLOOR", z: 0 },
];
const cmdPath = path.join(tmp, "task.commands.json");
fs.writeFileSync(cmdPath, JSON.stringify(commands, null, 2), "utf-8");
console.log("[test] 已写 commands.json:", cmdPath);

// 2) 假 plugin 回显收到的 params，便于断言文件内容确实被读出并转发
let receivedParams = null;
const fake = net.createServer((sock) => {
  sock.on("error", () => {});
  sock.on("data", (data) => {
    const req = JSON.parse(data.toString());
    receivedParams = req.params;
    sock.write(JSON.stringify({ jsonrpc: "2.0", id: req.id, result: { ok: true, method: req.method, count: req.params?.data?.length } }));
  });
});
await new Promise((r) => fake.listen(8091, "127.0.0.1", r));

const child = spawn(process.execPath, [serverEntry], {
  stdio: ["pipe", "pipe", "pipe"],
  env: { ...process.env, BIMONE_REVIT_PORT: "8091" },
});
let stderr = "";
child.stderr.on("data", (d) => { stderr += d.toString(); });

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
      if (msg.id && waiters.has(msg.id)) { waiters.get(msg.id)(msg); waiters.delete(msg.id); }
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
  await call("initialize", { protocolVersion: "2025-06-18", capabilities: {}, clientInfo: { name: "smoke", version: "1" } });
  child.stdin.write(JSON.stringify({ jsonrpc: "2.0", method: "notifications/initialized" }) + "\n");

  const res = await call("tools/call", { name: "revit__fanmo_build", arguments: { dataPath: cmdPath } });
  const text = res.result?.content?.[0]?.text || "";
  console.log("[test] 返回:", text.slice(0, 200));
  console.log("[test] plugin 收到 data 条数:", receivedParams?.data?.length);
  console.log("[test] plugin 收到首条 handle:", receivedParams?.data?.[0]?.handle);

  const ok =
    res.result?.isError !== true &&
    receivedParams?.data?.length === 2 &&
    receivedParams?.data?.[0]?.handle === "52" &&
    receivedParams?.data?.[1]?.category === "Floor" &&
    text.includes("文件");

  console.log(ok ? "\nFILE_HANDOFF_PASS" : "\nFILE_HANDOFF_FAIL");
  if (!ok) console.log("stderr:", stderr);
  process.exitCode = ok ? 0 : 1;
} catch (e) {
  console.error("[test] 失败:", e, "\nstderr:", stderr);
  process.exitCode = 1;
} finally {
  child.kill();
  fake.close();
  fs.rmSync(tmp, { recursive: true, force: true });
}
