# 应用功能与全局快捷键

更新：2026-10-07。设置快捷键提供应用入口；规则快捷键检测和模拟输入经原有 Engine 执行，共用同一个会话消息线程，不新增执行引擎。

## 用户行为

设置页支持新增、编辑、启用/停用、删除；当前唯一功能是显示 / 关闭 Axiom 窗口。首次创建快捷键库时默认 Ctrl+Alt+A，用户删除后不重建默认值。组合支持 Ctrl/Alt/Shift/Win，加 A–Z、0–9、F1–F11；至少含 Ctrl 或 Alt，最多 32 条。支持多条不同组合调用同一功能。已保存、已注册和功能执行结果是不同事实；注册冲突明确显示原因。

UI 可完全退出，快捷键仍由 Session Agent 监听。同目录 UI 存在时请求正常关闭，保留未保存编辑确认；关闭 UI 后任务栏按钮一起消失，后台继续运行。UI 不存在时启动固定同目录 Axiom.exe。功能 ID 保持 `app.show-window`，已有快捷键配置无需迁移。停止后台会释放所有登记；重启/重新连接后按持久配置重建，失败项显示未生效，不高频重试。

## 实现边界

- `KeyboardShortcuts` 定义组合、绑定、功能描述和状态；绑定有稳定 GUID 和版本。
- `ShortcutActionRegistry.Register(ApplicationFunction, Action)` 在 Agent 组合根注册已实现的功能和处理器。UI 功能列表来自描述，SQLite 只保存功能 ID 和组合。新增真实功能只需增加受权处理器和描述，不复制表单、不新增按键监听器。第二测试功能已验证分发复用。
- 涉及自动化规则、物理状态、命令执行的未来处理器必须请求宿主/Engine，经授权和相应能力 contract 执行。不得添加任意路径/脚本执行、另起规则引擎或绕过宿主存储。
- `WindowsGlobalHotkeys` 使用 RegisterHotKey / MOD_NOREPEAT，在专用阻塞 GetMessage 线程接收 WM_HOTKEY；注册、解除和分发在该线程串行处理，配置工作队列最多 64 项。没有轮询或键盘 hook，不读取键入文本。
- `ShortcutRegistrationSet.Replace` 先申请新组合，成功后才解除旧组合；失败时解除已申请项并保留旧登记。恢复允许部分失败，其原因分别可读；不存在的旧功能保留配置但不执行。
- `ShortcutCoordinator` 序列化宿主请求、先检验配置版本、试注册后提交 SQLite；注册结果回复丢失或持久化失败时尝试恢复旧配置。恢复也未确认时提示重新连接并刷新。提交与外部注册不是跨进程原子事务；重启按已保存配置恢复。
- 存储为现有受保护数据目录下 `shortcuts.db`，schema 1，SQLite FULL、2 秒 busy timeout，只有宿主写入。规则数据库和登录登记独立。

## IPC

沿用 AutomationProtocol 的帧上限、严格 JSON 和 15 秒以内期限，只接受本机当前用户 SID 与同会话连接；服务器最多 8 个客户端。

- UI → 宿主：`read-shortcuts`、`save-shortcut`（版本化绑定）、`delete-shortcut`（ID/预期版本）。不接受执行路径、命令或整个绑定数组。
- Agent → 宿主：`restore-shortcuts`，额外核对实际客户端进程为 Axiom.SessionAgent，宿主下发已保存配置。接入现有连接恢复通知，不增加重连计时器。
- 宿主 → 独立快捷键 Agent 管道：`read-shortcuts`、`apply-shortcuts`、`restore-shortcuts`。Agent 核对 OS SID、同会话宿主进程，再校验字段和注册目录。未注册功能不能从 UI 启用。

当前是管理员同用户会话后台预览；多会话、更多 DPI/高对比度和全后台性能数值尚待验收。

## 规则检测与模拟输入

`KeyboardPlugin` 声明 `keyboard.hotkey` / `keyboard.send`（schema 1），与现有程序、窗口、托盘和启动模块组合。检测最多 16 组合，至少含 Ctrl 或 Alt，F12 保留；动作最多 16 个有序组合，可重复、可单键，支持 Engine 延迟 0–86400 秒。通用 `HotkeyList` 字段使用应用内编辑弹窗，检测去重，动作保留顺序/重复，不新增专用能力页面。设置窗口切换原有按键范围和配置不变。

`HotkeyRegistrationHub` 在唯一 WindowsGlobalHotkeys 线程引用计数共享同组合；设置功能与多个规则可各自收到通知，暂停/删除/停止最后使用者才 UnregisterHotKey。没有 timer、轮询、键盘 hook 或键入内容记录。Observe 首先发布计数零基线，新 WM_HOTKEY 增加事件计数；占用导致 Unknown 和原因，不拆掉其他资源的有效订阅。Engine 的独立 Event 条件处理新计数，初始/重连不回放，动作/延迟忙时不积压，动作结束消费事件并释放候选；只支持根 Event + Command，暂不支持 Event 与 AND/OR/Desired 混合。

模拟动作在该消息线程一次性读取当前修饰键和目标按键状态；临时松开已有修饰键、按序发送完整 down/up、恢复原修饰键，右 Ctrl/Alt 及导航键包含扩展标志。所有输入构成一次 SendInput 包，keyboard.send 的能力级 single-flight 防止包交错，不阻塞其他能力；部分发送失败只补偿已注入按键，不重试整条命令。拒绝当前已按住的主键以及 Axiom 当前登记组合，避免自触发循环/误开关 UI。只作用于执行时的前台窗口，不切焦点；结果确认输入提交而非目标软件功能。系统/UIPI/软件限制不绕过。

真实重复按键、不同触发组合、左右修饰键、共享触发执行程序、零 UI 工作及暂停释放已验；见 0027 验收（本机历史记录）。API 依据：[RegisterHotKey](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey)、[SendInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput)。

## 快捷键与滚轮亮度工具

工具箱提供独立的直接配置，不要求用户拼检测与动作：启停、自选Ctrl/Alt/Shift/Win及可选主键的多键组合、屏幕范围、每格调节幅度、平滑时间和每秒写入次数。它使用启用期间的[WH_MOUSE_LL](https://learn.microsoft.com/windows/win32/winmsg/lowlevelmouseproc)事件，复用会话消息线程；RegisterHotKey处理键盘组合，不能代替全局滚轮消息。回调只在WM_MOUSEWHEEL时读取配置所需键状态、过滤并合并delta，不读取键入文本、不轮询、不做I/O或显示器调用；未命中组合继续传递，停用解除hook。

高分辨率滚轮保留余量，连续输入更新同一有限手势的最新目标，不按格排队。物理写入由Host Engine授权，捕获目标一次，原生线程执行有限平滑过程并确认；UI退出不停止。自动亮度的交接与显式恢复见[手动接管](../architecture/manual-override.md)，保存/启用、收到手势、写入确认和失败状态必须区分。真实效果与版本以当前交接为准，不把声明或合成输入测试当作主观手感验收。
