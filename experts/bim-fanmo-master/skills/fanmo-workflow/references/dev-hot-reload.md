# 插件开发与热更新

开发期最耗时的不是改代码，而是「改一行 → 关 Revit → 开 Revit → 开服务」。本文件把可重载边界和操作流程固定下来。

## 一、可重载边界（先判断改的是哪一层）

| 程序集 | 由谁加载 | 能否进程内热更新 |
|---|---|---|
| `RevitModeler.Plugin.dll` | Revit 按 `.addin` 清单加载 | ❌ 必须重启 Revit |
| `RevitModeler.CommandSet.dll` | 插件用可回收 ALC 加载 | ✅ `deploy-hot.ps1` |
| `RevitModeler.Core.dll` | CommandSet 的依赖，同 ALC | ✅ 同上 |
| `command.json` | 插件每次加载时读取 | ✅ 同上 |
| `RevitModeler.McpServer`（Node） | MCP 客户端拉起 | ✅ 重启该 Node 进程即可，不涉及 Revit |

**架构原则**：易变逻辑（翻模命令、几何处理、识别规则）放 CommandSet / Core；Plugin 只保留壳（Ribbon、socket 监听、加载器）。这样日常迭代基本不碰 Plugin。

## 二、日常迭代流程（无需关闭 Revit）

```powershell
# 改完 CommandSet / Core 代码后
cd d:/RIAMB/Code/BIMOne/BIMOne
powershell -ExecutionPolicy Bypass -File revit-modeler/scripts/deploy-hot.ps1
```

脚本做三件事：编译 CommandSet → 覆盖到已部署的命令目录 → 调 `fanmo_reload` 触发进程内重载。

只想看当前状态、不重载：

```powershell
powershell -ExecutionPolicy Bypass -File revit-modeler/scripts/deploy-hot.ps1 -VerifyOnly
```

## 三、为什么可以热更新

1. **可回收加载上下文**：`CommandLoadContext` 继承 `AssemblyLoadContext(isCollectible: true)`，命令程序集装在这里，卸载后即可加载新版本。
2. **影子拷贝**：加载前把命令目录复制到 `%TEMP%\BIMOne.Commands\<时间戳>`，从副本加载。否则 dll 被加载器锁住，覆盖会失败。
3. **共享程序集回落**：`RevitAPI` / `RevitAPIUI` / `RevitMCPSDK` / `Newtonsoft.Json` 一律回落到默认上下文解析。若各加载一份，`IRevitCommand` 会出现「同名不同类型」，注册时强转失败。
4. **原子切换**：新版本全部命令加载成功才替换注册表；任一失败则回滚，旧版本继续服务。**不会出现「热更新失败把服务搞死」**。
5. **API 上下文切换**：重建命令会调用 `ExternalEvent.Create`，必须在 Revit 主线程。重载请求经常驻 `ExternalEvent` 转交主线程执行。
6. **读写锁**：命令执行持读锁，重载持写锁，避免「边执行边卸载」。
7. **ExternalEvent 必须显式释放**：`ExternalEventCommandBase` 只实现 `IRevitCommand`，**没有实现 `IDisposable`**，其 `Event` 属性持有的 Revit `ExternalEvent` 不会自动释放。若重载时不 Dispose，每轮泄漏「命令数量」个事件，累积后 Revit 的 ExternalEvent 机制会**整体停止响应**（所有命令 Raise 都不再执行，只能重启 Revit）。`FanmoCommandLoader.ReleaseActiveCommands()` 在切换前用反射取 `Event` 属性并 Dispose。

> 症状识别：`revit__ping` 秒回、`commands` 列表正常，但任何命令（含 `fanmo_reload`）都等到超时——就是事件机制失效，重启 Revit 加载修复版即可，期间数据无损。

## 四、什么时候仍需重启 Revit

- 改了 `RevitModeler.Plugin`（含 `Application.cs`、`FanmoSocketService.cs`、`CommandLoadContext.cs`、`PluginConfig.cs`）。
- 第一次从不支持热更新的旧版本升级（旧版本用 `Assembly.LoadFrom`，命令 dll 被锁，热部署会因文件占用失败并明确提示）。
- Revit 启动时清单指向的目录换了。

部署时机判断：`deploy.ps1` 发现目标目录被运行中的 Revit 锁住时，会自动部署到下一个 `BIMOne.RevitModeler_vN` 并改写清单，重启后生效。

## 五、验证与排错

```powershell
# 端到端验证热重载链路（重启后跑一次）
node work/hotel/verify-hotreload.mjs

# 压力测试：连续 N 轮 reload，每轮之后真的执行一次走 ExternalEvent 的命令
node work/hotel/stress-hotreload.mjs 10
```

压测的判据不是「reload 返回 ok」，而是**每轮之后命令还能真的执行**——泄漏的症状恰恰是
「前几轮还能回包，之后静默超时」。通过标准：`STRESS_PASS`，`liveContexts` 全程为 0，
命令耗时无逐轮恶化（实测 10 轮：reload 中位 147ms、命令中位 58ms，末轮不比首轮慢）。

| 症状 | 原因 | 处置 |
|---|---|---|
| `RELOAD NOT SUPPORTED (method not found)` | 运行中的仍是旧插件 | 重启一次 Revit；之后不再需要 |
| `COPY FAILED ... locked by Revit` | 旧版用 `LoadFrom`，文件被占用 | 重启一次，切到影子加载版本 |
| `Hot reload : ` 为空 | 同上，旧插件无此字段 | 同上 |
| `reload.ok=false` + 具体原因 | 新版本编译错误/缺依赖 | 修好后重跑 `deploy-hot.ps1`，旧版本仍可用 |
| ping 正常但所有命令超时（含 reload） | ExternalEvent 泄漏导致事件机制失效（v3 及更早） | 重启 Revit 加载 v4+，之后重载会释放旧事件 |
| `retiredContexts` 有值但 `liveContexts` > 0 | 旧程序集未完全回收（引用泄漏） | 不影响运行，属内存占用；长期迭代多了再重启一次 |

## 六、常驻方式

在 Revit 里点一次 Ribbon「BIMOne 翻模 → 翻模服务 开/关」启动，之后服务常驻：

- 之后只改命令集，全程走 `deploy-hot.ps1`，插件本体与 socket 监听都不动，**不必再关 Revit**。
- 重复点击按钮是「关 → 开」；再开会重新加载命令集（等价一次重载），可用于应急恢复。
- 关闭 Revit 时经 `OnShutdown` 停止服务；下次打开需再点一次开启（刻意不做自启，避免端口长期被占）。
