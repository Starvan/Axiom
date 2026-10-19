# 按功能定位代码

只读本轮相关行；先定位入口再核对实际实现。方法级细节在 [能力参考](capability-reference.md)，不要默认通读。产品状态见 [当前交接](../product/current-state.md)。

| 改动范围 | 代码入口 / 检索词 | 相关测试定位 |
|---|---|---|
| 通用任务声明、编译和组合 | [AutomationTasks](../../src/Axiom.Domain/AutomationTasks.cs)、[AutomationTaskCompiler](../../src/Axiom.Engine/AutomationTaskCompiler.cs)、[内置注册](../../src/Axiom.Infrastructure/BuiltInAutomationPlugins.cs) | 该能力Catalog/注册/严格导入用例 |
| 保存、IPC、执行与生命周期 | [任务库](../../src/Axiom.Infrastructure/SqliteAutomationTaskStore.cs)、[PluginTaskRuntime](../../src/Axiom.ServiceHost/PluginTaskRuntime.cs)、[AutomationPipeServer](../../src/Axiom.ServiceHost/AutomationPipeServer.cs) | 对应持久化/Host用例，不默认全跑 |
| Engine仲裁、时序、执行上下文 | [Engine目录](../../src/Axiom.Engine)、[Domain目录](../../src/Axiom.Domain)；检索EngineHost.Rules/Execution与对应contract | 按当前行为选精确Fact |
| WinUI任务表单、字段与选择弹窗 | [Views](../../src/Axiom.WinUI/WorkbenchWindow.Views.cs)、[CapabilityPicker](../../src/Axiom.WinUI/WorkbenchWindow.CapabilityPicker.cs)、[TaskEditorState](../../src/Axiom.WinUI/TaskEditorState.cs) | 相关状态类及真实本轮UI操作 |
| 时段一次、重试与持久去重 | [WindowOnce声明](../../src/Axiom.Contracts/WindowOnceCapabilities.cs)、[事件源](../../src/Axiom.SessionAgent/WindowOnceEventSource.cs)、[Host](../../src/Axiom.ServiceHost/PluginTaskRuntime.WindowOnce.cs)、[记录](../../src/Axiom.Infrastructure/SqliteAutomationTaskStore.WindowOnce.cs) | WindowOnceCatalogTests / WindowOnceRuntimeTests |
| 精准每日、单次日期、时段间隔 | [Contracts](../../src/Axiom.Contracts)、[Agent](../../src/Axiom.SessionAgent)；检索DailyTime / OnceTime / WindowedInterval | 同名Tests，仅受影响项 |
| 普通/硬件亮度与恢复 | [普通接口](../../src/Axiom.Providers.Windows/WindowsBuiltInBrightness.cs)、[硬件接口](../../src/Axiom.Providers.Windows/WindowsHardwareBrightness.cs)；[有限渐变](../../src/Axiom.Providers.Windows/BrightnessFade.cs)、[字段](../../src/Axiom.Contracts/BrightnessFadeSettings.cs)；检索BrightnessRestore | BrightnessFade / BuiltInBrightness / HardwareBrightness / BrightnessAdjustment / BrightnessRestore Tests |
| 画面明亮面积、检测时段与HDR停采 | [声明](../../src/Axiom.Contracts/ScreenContentCapabilities.cs)、[Agent](../../src/Axiom.SessionAgent/ScreenContentCapabilityProvider.cs)、[状态](../../src/Axiom.Providers.Windows/ScreenContentObservation.cs)、[DXGI/GPU](../../src/Axiom.Providers.Windows/WindowsScreenContentCapture.cs)、[颜色通知](../../src/Axiom.Providers.Windows/WindowsScreenContentColorEvents.cs) | ScreenContentTests；WindowsScreenContentTests仅本机；新目录需精确摘要预算Fact |
| 程序、窗口、前台、快捷键、托盘 | [Infrastructure](../../src/Axiom.Infrastructure)、[Agent](../../src/Axiom.SessionAgent)、[Windows](../../src/Axiom.Providers.Windows)；检索Program / Window / Foreground / Keyboard / Tray | 对应能力名Tests |
| 工具箱任务栏托盘管理 | [UI](../../src/Axiom.WinUI/WorkbenchWindow.TrayTool.cs)、[工具配置](../../src/Axiom.Contracts/TrayTool.cs)、[Host](../../src/Axiom.ServiceHost/PluginTaskRuntime.TrayTool.cs)、[观察流](../../src/Axiom.SessionAgent/TrayToolObservationStream.cs)；WindowsTrayEvents / WatchTargetsAsync | TrayToolTests及本轮精确托盘原生用例 |
| 快捷键+滚轮手动亮度 | [工具UI](../../src/Axiom.WinUI/WorkbenchWindow.BrightnessWheel.cs)、[配置](../../src/Axiom.Contracts/BrightnessWheelTool.cs)、[Host](../../src/Axiom.ServiceHost/PluginTaskRuntime.BrightnessWheel.cs)、[Agent](../../src/Axiom.SessionAgent/BrightnessWheelAgent.cs)、[输入](../../src/Axiom.Providers.Windows/WindowsBrightnessWheelHook.cs)、[有限手势](../../src/Axiom.Providers.Windows/WindowsBrightnessWheelGesture.cs)；BrightnessNativeOwnership | BrightnessWheel相关精确用例；实机仅新工具输入/接管/停止 |
| 音频、电源、会话、网络、USB、文件 | 同上三目录；检索Audio / Microphone / Power / Session / Network / Wifi / Usb / FileArrival | 对应能力名Tests |
| 主动观察、摘要与严格导入 | [UsageAnalysis](../../src/Axiom.Infrastructure/UsageAnalysis.cs)、[UsageJournal](../../src/Axiom.Infrastructure/UsageJournal.cs)；检索UsageObservation | UsageObservationTests的直接方法；目录变化才选完整目录预算Fact |
| 打包、清理、CI与发布 | [tools](../../tools)、[workflows](../../.github/workflows)、[分发说明](distribution.md) | 只选本轮发布/来源/布局边界 |

## 最短查找路径

1. 先由能力ID/用户名称找Contracts声明。
2. 在Infrastructure找对应Plugin/编译/允许组合。
3. 到SessionAgent找Provider，再到Providers.Windows找原生适配。
4. 仅遇到保存/执行/恢复问题追Host/Engine及对应contract；UI不直接调Provider。

## 容易误判

- WorkbenchWindow是当前界面；旧MainWindow/RuleDraftWindow是维护入口，不是用户主流程。
- HDR兼容后端与通用PluginTaskRuntime并存，不把新能力塞进旧HDR表。
- CommandOrigin只给Host冻结规则版本/事件来源和值，不扩Provider wire。
- 新能力沿用声明字段、通用编辑和测试；不为定位进度扫描整个源码，旧测试数不代表当前验证。
