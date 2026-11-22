import * as net from "net";

/**
 * 到 RevitModeler.Plugin 的 JSON-RPC over TCP 连接。
 * 与 mcp-servers-for-revit/server/src/utils/SocketClient.ts 同协议，
 * 但端口指向 BIMOne 独立插件（默认 8982），并做了响应分包处理。
 */
export class FanmoRevitClient {
  private host: string;
  private port: number;
  private socket: net.Socket | null = null;
  private connected = false;
  private pending = new Map<string, (raw: string) => void>();
  private buffer = "";

  constructor(host = "127.0.0.1", port = 8982) {
    this.host = host;
    this.port = port;
  }

  static fromEnv(): FanmoRevitClient {
    const host = process.env.BIMONE_REVIT_HOST || "127.0.0.1";
    const rawPort = process.env.BIMONE_REVIT_PORT || "8982";
    const port = Number.parseInt(rawPort, 10);
    return new FanmoRevitClient(host, Number.isFinite(port) ? port : 8982);
  }

  private setup(): void {
    if (!this.socket) {
      this.socket = new net.Socket();
      this.socket.on("data", (data) => {
        this.buffer += data.toString();
        this.drain();
      });
      this.socket.on("close", () => {
        this.connected = false;
      });
      this.socket.on("error", (err) => {
        this.connected = false;
        console.error("[bimone] Revit 连接错误:", err.message);
      });
    }
  }

  /** 按完整 JSON 对象切分响应，避免多条响应粘包导致解析失败 */
  private drain(): void {
    let depth = 0;
    let inString = false;
    let escape = false;
    let start = -1;

    for (let i = 0; i < this.buffer.length; i++) {
      const ch = this.buffer[i];
      if (inString) {
        if (escape) escape = false;
        else if (ch === "\\") escape = true;
        else if (ch === '"') inString = false;
        continue;
      }
      if (ch === '"') { inString = true; continue; }
      if (ch === "{") { if (depth === 0) start = i; depth++; }
      else if (ch === "}") {
        depth--;
        if (depth === 0 && start >= 0) {
          const raw = this.buffer.slice(start, i + 1);
          this.route(raw);
          this.buffer = this.buffer.slice(i + 1);
          i = -1;
          start = -1;
        }
      }
    }
  }

  private route(raw: string): void {
    try {
      const msg = JSON.parse(raw);
      const cb = this.pending.get(String(msg.id));
      if (cb) {
        this.pending.delete(String(msg.id));
        cb(raw);
      }
    } catch (err) {
      console.error("[bimone] 响应解析失败:", String(err));
    }
  }

  private async ensureConnected(): Promise<void> {
    this.setup();
    if (this.connected && this.socket && !this.socket.destroyed) return;

    await new Promise<void>((resolve, reject) => {
      const sock = this.socket!;
      const onConnect = () => {
        cleanup();
        this.connected = true;
        resolve();
      };
      const onError = (err: Error) => {
        cleanup();
        reject(new Error(`无法连接 Revit 翻模插件 ${this.host}:${this.port}（请先在 Revit 中开启「翻模服务」）: ${err.message}`));
      };
      const cleanup = () => {
        sock.removeListener("connect", onConnect);
        sock.removeListener("error", onError);
      };
      sock.once("connect", onConnect);
      sock.once("error", onError);
      sock.connect(this.port, this.host);
    });
  }

  async sendCommand(method: string, params: unknown, timeoutMs = 600000): Promise<any> {
    await this.ensureConnected();
    const id = `${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
    const request = { jsonrpc: "2.0", method, params, id };

    return new Promise<any>((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error(`命令 ${method} 超时（${timeoutMs}ms）`));
      }, timeoutMs);

      this.pending.set(id, (raw) => {
        clearTimeout(timer);
        const msg = JSON.parse(raw);
        if (msg.error) reject(new Error(msg.error.message || "Revit 返回错误"));
        else resolve(msg.result);
      });

      this.socket!.write(JSON.stringify(request), (err) => {
        if (err) {
          clearTimeout(timer);
          this.pending.delete(id);
          reject(err);
        }
      });
    });
  }
}
