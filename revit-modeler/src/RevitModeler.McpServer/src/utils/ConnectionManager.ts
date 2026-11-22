import { FanmoRevitClient } from "./FanmoRevitClient.js";

// 串行化请求，避免多个 tools/call 并发时争用同一条 Revit UI 线程
let mutex: Promise<void> = Promise.resolve();

/** 复用的单例客户端：避免每次工具调用都新建 socket */
let shared: FanmoRevitClient | null = null;

export async function withRevit<T>(op: (client: FanmoRevitClient) => Promise<T>): Promise<T> {
  const previous = mutex;
  let release!: () => void;
  mutex = new Promise<void>((resolve) => { release = resolve; });
  await previous;
  try {
    if (!shared) shared = FanmoRevitClient.fromEnv();
    return await op(shared);
  } finally {
    release();
  }
}
