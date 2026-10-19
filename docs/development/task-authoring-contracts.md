# 通用任务接入 contract

2026-10-07，决策见 [ADR 0008](../adr/0008-task-capability-composition.md)。

## 配置到执行

`AutomationTask` 包含 ID、乐观版本、名称、暂停状态和两个 `TaskInvocation`：Detection 与 Action。每个调用保存稳定能力 ID、schema 版本及参数，不含 UI 控件或平台对象。`TaskCapability` 描述类别、名称、字段、默认值及运行/编辑提示。

`ITaskDetectionCompiler` 与 `ITaskActionCompiler` 独立注册；`AutomationTaskCompiler` 验证 schema、字段、类型和值，编译为已有 `RuleCondition` / `RuleAction`。Command 与 Desired 由动作编译器明确选择。描述/编译不启动观察器、不执行副作用。

检测编译器可覆盖带 `taskId` 的 `Compile` 重载，以声明任务独立的观察资源；默认重载仍调用原纯编译方法，普通能力无需修改。启动检测需要该标识，不能把内部任务 ID 加入用户参数或依赖 UI 特例。

时段内一次检测也使用任务独立资源。Engine发出Command时可携带冻结的内部`CommandOrigin`（规则ID/版本及原事件来源和值），供Host核对本次时段执行及持久化结果；默认手动Command来源为空，Provider调用参数及wire不扩展，不允许从共享动作目标反推是哪条规则。新检测明确选择“有效时段内晚启动可执行”的策略，原精准钟点与间隔默认不补发的行为不改变；成功终止、有限失败重试和未确认不重发由该适配器负责，不更改Engine默认不重试。

`IAutomationTaskBackend` 声明实际支持的 `TaskCombination`，负责该组合的读取、持久化、版本检查、运行生命周期和删除；`AutomationTaskRouter` 拒绝未接通及重复组合。宿主注册适配器，不能因为两个描述存在就假定它们能够组合执行。跨不同后端修改现有任务目前拒绝，用户需新建；不尝试跨存储非原子迁移。

WinUI 经 `AutomationProtocol` 的受权本地管道读取目录及任务，提交通用 save/delete/read/shutdown。SID 与会话来自 OS，不能相信请求中的身份；有大小、并发和期限上限。UI 不碰 Provider 或数据库，不轮询。

## 接入一种能力

1. 注册描述及纯编译器。字段支持文本、多行文本、布尔、枚举、资源、文件/目录路径、带范围的数字；`Group` 将低频参数折叠到“更多选项”；文本字段可声明 `BrowseLocalPath=true` 提供本机文件/文件夹选择，默认false。该声明仅影响输入辅助，不改变执行类型、路径校验或导入权限。新增普通能力不修改编辑器。动态资源选项由后台读取时填充，离线原值保留；资源合法性与授权仍由实际后端确认。
2. 接通观察/执行端口、权限及生命周期，声明可执行组合。复用现有 Engine 仲裁、未知状态和事件订阅，不绕过 Engine。新增真实能力须明确通知来源和按需停止条件。
3. 新会话命令能力复用 `PluginTaskRuntime` 和 `SqliteAutomationTaskStore`；先提交再确认，保留 ID、版本、暂停状态，冲突不能静默覆盖。不自动加载外部 DLL。
4. 添加编译/拒绝/恢复测试及真实配置到执行验收。测试能力不得混入生产目录。

## 当前适配与边界

`GameHdrTaskBackend` 是首个兼容适配器，`GameHdrTasks` 映射旧 DTO，沿用旧 SQLite 数据与运行时；实际 RegisterRule 已调用通用编译器。旧 HDR 管道保留供 Agent/维护兼容。未来能力不能塞进 HDR 数据表或新增 UI 的 capability-ID 分支。

`process.running` 检测 schema 2 声明通用 `ProcessSelection` 字段，以换行文本保存最多 128 个进程名，编译成 `RuleCondition.Any`；单进程规则从旧数据库映射到同一字段。组仅提供选择来源，修改组不改已保存任务；每个适配器合计最多 128 个不同目标的限制保持。该列表属于一种检测的参数，不表示通用 AND/OR 编辑已开放。目录验证只为该字段类型允许 32768 字符，其他类型保持 4096 的上限。

`ProgramPlugin` 是第二组真实能力：`process.running` / `process.exited` 与 `program.start` / `program.close`。退出检测复用运行观察，Not(Any) 保持 Unknown；正常/强制关闭只匹配完整路径、当前用户与会话。

`AxiomStartupPlugin` 提供 `axiom.started`（schema 1）：Choice 参数 `frequency=every|firstBoot`，与程序启动/关闭组合。事件来自 Host 本身，加载时启用且版本未变的规则在 Agent 首次就绪、完整资源计划确认后收到 false → true；保留 true 让现有 Engine 支持动作延迟。UI 重开、Agent 重连、运行中新建/编辑/启用不产生启动事件；修改、暂停、删除或连接中断取消未执行延迟，不补发。

主动调整无关检测资源（例如HDR门禁恢复内层观察）不属于Agent断线：若启动资源集合不变且不要求全新基线，保留Host启动源的epoch、序列和已知值，等待新订阅就绪再接受新的工具请求。不撤销已有启动动作，也不重新发出启动边沿。真实连接中断、启动资源变化、强制新基线与停止仍释放原源并遵守取消/不补发；不能用重试掩盖取消。

首次是当前用户本次 Windows 开机第一次启动后台，不是各条规则分别第一次执行。Host 每次启动只读取一次 Windows 注册表 BootId，并在 `tasks.db` 的 `host_startup` 单行表事务认领；库 schema 原地升级至 2，不改既有配置、不备份。即使没有启动规则也记录首次，错过/暂停/执行失败/取消/崩溃均不自动补发。BootId 或持久化不可用时首次观察为 Unknown 并记录原因，每次启动仍可触发。BootId 来源见 [微软文档](https://learn.microsoft.com/windows-hardware/design/device-experiences/oem-hvci-enablement)；本轮未实际重启电脑验证快速启动/休眠组合。

当前一次检测配一个动作；AND/OR 编辑、多步骤任务未实现。延迟、窗口状态、重复实例和关闭策略是程序能力真实参数；活动历史已持久化。测试中的会话检测/消息动作仍不代表系统会话通知功能已交付。

`WindowPlugin` 提供独立 `window.close`（schema 1），与 `process.running` / `process.exited` / `axiom.started` 组合。字段为完整 exe 路径名单、等待窗口 1–30 秒、Engine 延迟 0–86400 秒和可选标题包含过滤。Windows 端复用程序完整路径/用户/会话核对，订阅共享按需 WinEvent 后每个目标做一次初始主窗口枚举；等待 create/show 通知，无周期枚举、无注入。每次为每个目标向一个可见无 owner 主窗口发 WM_CLOSE，之后以隐藏/销毁确认，最长 3 秒；不自动处理确认框、不强杀、不重试，后续手动重开不干预。目标程序决定退出还是托盘保留，不能把成功投递消息当作窗口已经关闭。

关闭窗口和关闭程序通过通用 `ExecutableSelection` 字段支持最多 32 个目标；同一 `executable` 参数换行保存完整路径，最大 32768 字符，每项最大 1024，仍用 schema 1 兼容已有单路径，无数据库改表或写回迁移。`ExecutablePaths` 统一规范、去重及拒绝脚本/网络/Axiom 自身；启动程序仍是一个目标。单目标资源标识保持路径，多目标用排序、规范化去重路径集的 SHA256 摘要，重排不改变资源。每个批量动作仍是一个 Command，独立任务异步执行；不提供不同名单交集的事务仲裁，部分已经执行的操作不能回滚。

2026-10-06 修正跨规则阻塞：持久任务容量与运行时并发统一使用 `AutomationTaskLimits.MaximumTasks=128`。现有 Engine 的 worker 是异步任务，等待 pipe/目标/回执时不占用线程；所有支持范围内的独立规则均可等待，不排在其他规则的 30 秒等待后。延迟仍由 Engine deadline 驱动，同资源和能力声明的 single-flight、超时保留真实在途、暂停/停止取消及 Command 不重试语义不变。通用 Engine 默认并发仍是 4、显式小容量仍受限；该配置仅用于会话命令插件运行时。Agent 通信预算为 136 个客户端加一个监听实例，预留观察和当前托盘读取；无新轮询或跨规则先后顺序承诺。127 条真正在途无回执 + 第 128 条动作/暂停/安全退出回归，以及正式预览五条等待托盘规则 + 真实窗口规则通过；不代表全后台 CPU 预算已验收。

`WindowsProgramBatch` 在触发时并行启动至多 32 个独立有界等待，全部结束后汇总每个完整路径的结果；不把 30 秒等待串成多倍期限，40 秒命令期限保持。个别权限/文件/退出错误不跳过其他目标，部分失败整体为 Failed 并记录完成数及逐项原因；暂停 token 取消全部等待，已关闭的不回滚，不自动重试。正常/强制及实例范围分别应用于每个程序目标。批量能力需保存/执行/取消真正接通，不能只扩展表单。

会话 Invoke 的管道对端断开时立即取消调用 token；暂停/删除取消尚在等待的窗口动作，不能在 Host 已取消后仍等待后续窗口并关闭。已发出的关闭消息无法撤销。通知溢出/不可用或窗口仍显示返回失败/原因；命令回执不能用于推断持续后台状态。窗口通知仅用于该动作的有限等待，原进程检测继续使用原生进程事件。

`TrayPlugin` 提供 `tray.hide`（schema 1），与 `process.running` / `process.exited` / `axiom.started` 组合，声明 `TraySelection` 完整路径多选（最多 32）、等待图标 1–120 秒和 Engine 延迟 0–86400 秒。TraySelection 复用 ExecutableSelection 的名单/批量输入和校验，增加当前托盘搜索、多选、刷新面板；选择仅保存 executable 换行完整路径，旧 schema/单路径不迁移，暂无图标的原目标保留。Host 转发 Agent 的只读发现与动作共用核验，不因选取而立即执行；UI 按字段类型渲染，无具体能力 ID 分支。同一 WindowsProgramBatch 汇总逐项结果；匹配当前用户/会话/完整路径，普通 ID 核对窗口（含 message-only），除缓存 UID 外额外核验当前 HWND 数值 UID；GUID 核对系统路径且拒绝归属冲突，公开 Shell_NotifyIcon 删除并确认。注册表/Explorer 通知只在等待期间共享，末订阅释放，无轮询。一次触发移除当前可识别图标，重建后可能再出现，暂停不恢复。未知身份记录/UID 明确失败，不用折叠假成功，不注入或修改全局偏好；决策见 [ADR 0010](../adr/0010-tray-icon-discovery.md)。

托盘等待的命令期限为130秒，Provider仅对已通过参数校验的tray.hide Invoke接受最长135秒的请求；其他Provider操作维持45秒上限。目标仍并行等待，退出/暂停立即取消，不增加轮询。

## 内置插件模块接入步骤

`KeyboardPlugin` 提供 `keyboard.hotkey` 检测与 `keyboard.send` 动作，通用 `HotkeyList` 字段（枚举末尾追加，旧字段编号不变）以最多 1024 字符保存 16 组合，检测名单排序去重作为观察资源，动作按原有序序列含重复计算 SHA256 资源标识，不让不同序列长期争同一资源。检测编译为 `RuleCondition.Event`，新通知才触发；动作仍用会话 Command，延迟由 Engine 调度、短输入包声明能力级 single-flight。按键来源和发送边界见 [快捷键 contract](keyboard-shortcuts.md)。组合与字段由目录声明，WinUI 不按能力 ID 分支。单资源 Unknown 的首快照为有效状态，不整体拒绝订阅；只对失效资源停止判断，其他程序/启动规则继续可用。

1. 在 Infrastructure 实现 `IAutomationPlugin`，提供纯检测/动作编译器、支持组合和运行时 `CapabilityBinding`。可参考 `ProgramPlugin`。会话命令使用 `TaskInvocationCommands.Compile` 和其 `axiom.task-invocation` 参数 schema；动作编译器的 `Timing` 可映射到 Engine 时间策略，不能在 Provider 内 sleep 实现延迟。
2. 在 SessionAgent 实现 `ISessionCapabilityProvider`：稳定能力 ID、按需 `ObserveAsync`（首个完整快照，后续有界事件）、`InvokeAsync`（再次验证参数，再调用 Windows 适配器）。OS 代码留在 Providers.Windows。
3. 在 `Infrastructure/BuiltInAutomationPlugins` 的模块列表和 Agent `GameHdrAgent` 的 Provider 列表各登记一次；Host `Program` 消费同一模块列表。复用任务数据库、路由、权限、传输、调度和字段渲染器；无需改专用 UI、通用表结构或为能力新增 pipe 类型。
4. 明确真实事件来源、权限与故障语义；验证配置保存/读回、真实触发/动作、活动记录、暂停/退出 UI。命令结果未知时不重试。

当前通用插件运行时接受上述会话 Command contract，并以专用租约支持power.keepAwake；HDR Desired State的接管/恢复继续使用独立适配器。新 Desired State 场景需自己的真实恢复语义，不能伪装成命令。外部 DLL 安装、卸载、热加载、版本隔离尚未实现，不宣称是已开放的第三方插件平台。

资源计划包含动作与检测资源；仅 SessionAgent 的 Observe 资源进入会话观察流，Agent 同时核对所需会话能力 ID。只有 Host 检测时允许空观察资源，以空 snapshot 确认动作能力就绪，并持有管道等待断开，无健康轮询。Host 观察由本地 source 提供，确认订阅后再向 Engine 确认完整资源计划。连接中断、事件缺失/溢出置 Unknown 并取消未执行动作；仅有有效任务且连接失败时按五秒间隔重连，空闲不探测健康。初始/重连快照不补执行历史边沿，任务暂停/删除或条件不成立取消延迟。

插件订阅结束使用 `Engine.ReleaseSource` 显式释放资源所有权与容量，再为新资源计划握手。仍持有的 source 重握手必须保持资源集合；释放使旧 epoch 永久失效，新注册必须重新给完整快照。这解决增删规则/检测目标时沿用同一 source ID 却改变资源导致的订阅失败。

`IntervalPlugin` 的 `interval.elapsed` 是用户配置的周期检测，不是环境轮询或动作延迟：1–1440整数分钟转稳定资源，首快照计数0。同周期共享观察资源，`IntervalEventSource` 只安排所有周期最近一个到期点；以单调经过时间判断到期，每个过期周期仅发一次并从当前时间续算。取消释放定时器，重启/重连重新建立完整周期，不持久化错过次数，不唤醒电脑。提供程序、窗口、托盘、目录/网页命令组合，不默认组合键盘输入；重复动作不承诺恢复。实际注册目录进入摘要，导入再次校验整数约束。

## 一次性动作的时间段限制

`ITaskActionCompiler.SupportsTimeWindow` 显式声明支持后，真实目录的动作显示时段编辑器。`AutomationTask.TimeWindow` 保存本地周时段（分钟起止、星期bitmask），使用task schema 2；无时段的既有任务保持schema 1，旧后端因版本不兼容拒绝含时段配置，避免忽略限制直接执行。能力自身schema不变。编译器同时校验声明及范围；Domain拒绝Desired动作挂载此门禁，HDR兼容转换也明确拒绝，不能丢字段。

Engine在新条件边沿/事件进入延迟前、延迟deadline以及队列取出前检查当前时区的本地时间；开始包含、结束排除，跨午夜归开始星期。时段外消费边沿、取消其等待，不开窗补发，不增加计时器。已经开始的动作仍按原取消/超时规则运行，不因时间结束反向撤回。夏令时重复小时都属于同一本地时段，仍须真实新边沿；没有节假日或唤醒电脑语义。运行时钟提供LocalTimeZone，测试可以注入固定时区。

`power.keepAwake` 是拥有可释放OS请求的具体Desired动作，使用独立lease管道与ownership观察。Engine的`ILeasedDesiredExecutor`授权回调只更新内存并异步发出取消，不在Engine锁内进行IPC。最后条件撤销、观察未就绪/未知或停止释放租约；Agent管道结束在finally关闭本请求，未改全局状态，不套用旧值恢复。其他Desired动作不能仅声明后绕过具体生命周期接入。

`TaskField.EditAsList`仅配合TextList使用，通用名单编辑弹窗支持最多16项、每项255字符；保留空格及大小写，UI去重只按整行Ordinal，能力仍负责自身规范和最终校验。普通TextList仍用于多行通知正文。名单默认值和选项在可导出目录中清空；Wi-Fi配置名及USB实例路径仅由用户手工补齐，AI导入只能给空占位，不接受非空自由身份参数。USB可能含序列号，不随能力接入新增主动观察身份采集。

## 精确USB、移屏及锁屏的最小边界

`UsbDevicePlugin`提供schema 1的`usb.device-present`与`usb.device-absent`，`deviceInstanceIds`最多16个完整`USB\\设备\\实例`值；整项OrdinalIgnoreCase去重、转大写资源ID，不接受VID/PID子串、通配符或设备接口路径。两种检测消费同一presence bool资源，分别编译Any true与All false，不另开移除观察源。只有presence可组合`power.keepAwake`；初始已成立的Desired条件可持有请求，首次/重新启用/恢复不补发Command。

`WindowsSessionEvents`保留`clockOnly`参数并追加`usbOnly`/`powerOnly`可选租约；同一个原生隐藏窗口和`RegisterDeviceNotificationW(GUID_DEVINTERFACE_USB_DEVICE)`注册由消费者共享。USB租约只收接入、移除及恢复提示，普通系统/clock消费者过滤和独立释放语义不变。消费者每批最多64提示后用SetupAPI读取DIGCF_PRESENT接口对应的PnP实例，通知interface path本身不当实例ID；查询失败、通知故障/溢出或超过256接口整体Unknown，无接口是Known空集合。睡眠恢复和Unknown后恢复发送新baseline，不补旧动作。只支持该接口类所对应实例，不自动关联HID/USBSTOR子节点、容器或型号；不承诺物理拔出识别。

`WindowMonitorPlugin`的`window.monitor`沿用多完整exe路径、标题过滤、窗口等待和Engine延迟/时段门禁，`monitor=primary|next|previous`只引用执行时拓扑，不保存显示器身份。Windows端核验完整路径/SID/会话/创建时间，最多64屏按桌面坐标排序，目标工作区中央80%；最大化/最小化先还原，单屏上/下一屏直接跳过。3秒确认期限结束后不再发送新移动请求，读回须身份/拓扑/目标屏和位置大小一致；失败或取消可能已有部分副作用，不重试或恢复。真实多屏、DPI及拓扑改变另验。

`SessionLockPlugin`的`session.lock`是单次Command，仅`delaySeconds`整数参数；共享当前会话资源，不接受目标会话/解锁参数。Agent用WTSInfoEx连接与锁元数据确认当前活动、未锁定交互会话，再请求`LockWorkStation`；Unknown/已锁定/非活动拒绝。独立5秒有界原生准入，超时或取消不提前复用阻塞线程，不重试；成功是ReceiptOnly接受，不证明锁定完成，不自动解锁。纯API及Win11 x64 WTS头解析回归不实际锁CI桌面；真实会话、权限和系统策略待实机。

## 亮度、静音与供电状态

`BuiltInBrightnessCapabilities`/`BuiltInBrightnessPlugin`声明`display.brightness`，0–100整数和普通Engine延迟/时段门禁；执行时通过活动内置连接及WMI接口核验面板身份，一块一次写入、读回。外接连接不处理，未知/重复身份和缺失接口不写；无支持面板失败，多面板部分完成不回滚。新亮度保留，不恢复/重试；有界原生超时或取消可能已写入，仍保留真实在途占用。测试不调用真实面板写入。

`EndpointMutePlugin`的`audio.mute-state`以`endpoint=input|output`选择eCapture/eCommunications或eRender/eConsole，以`state=muted|unmuted`编译同一端点bool资源的真/假条件。`WindowsEndpointMuteEvents`共享按需MTA源，回调只加有界提示，工作线程GetMute核验；默认选择通知保留代次，即使合并后身份相同也重建基线，普通inventory同ID保留绑定但验证可用性。旧代回调隔离，缺设备/失效/查询或通知失败Unknown；首次及恢复不补Command，已知初始Desired可生效。末订阅注销/释放，端点ID只暂存在进程内，不持久化/传输，不新增音频内容或设备身份观察。

`PowerStatePlugin`的`power.state`使用`mode=ac|battery|battery-below`及1–99整数`threshold`，只将低电模式编码成`battery-below:N`；严格低于且电池供电，接电结束低电条件。`WindowsPowerState`复用`WindowsSessionEvents(powerOnly:true)`的供电来源/电量通知，每批最多64提示后GetSystemPowerStatus读取共享快照，无轮询；新增GUID注册失败只拒绝power消费者。供电255、电量255、查询故障或无电池却报告电池供电等矛盾状态为相关Unknown，无电池不当低电。首次/恢复/睡眠恢复baseline不补Command；持续保持唤醒按已知当前条件持有，结束、Unknown、暂停或停止释放。

## 窗口状态条件

`WindowStateDetectionPlugin`提供schema 1检测`window.state-is`，`executable`为1–32完整exe路径，`state=minimized|maximized|normal`；每个路径/状态是独立bool资源，多路径编译Any true。只认可见顶层无拥有者HWND，最小化优先于最大化位，normal不包含隐藏/不存在。同名路径不合并；文件名只排除明确无关进程，最终核验SID/会话/完整路径、固定进程句柄/创建时间/存活和HWND所属PID。不读取标题或正文；已知满足压过Unknown，只有可能满足所选状态的未知身份影响该状态。

先接共享`WindowsWindowEvents`再一次`EnumWindows`，随后仅按事件读当前HWND，最多256已排队提示去重，无周期扫描。新增最小化开始/结束hook由`requireMinimizeEvents`显式要求，注册失败只拒新检测，既有窗口动作/出现/观察保持原hook。最多128订阅资源、4096相关窗口，源/容量故障整体Unknown、不自动重连；首次及Unknown后恢复逐资源baseline，不补Command，已知初始Desired可保持唤醒。短暂往返变化可能合并，最大化不等于全屏或手动调整证据；组合排除`window.state`/`window.monitor`自身反馈，允许程序、窗口关闭、打开目标、通知及保持唤醒。

新增声明进入真实注册、Agent及导入校验；AI只能用当前观察应用别名映射本地路径。最大匿名摘要仍限定64KiB，完整字段ID/schema/类型/必填/长度/非空范围/默认值/选项与组合保留；在既有说明/hint缩减仍超限时，Infrastructure的compact序列化仅省略`TaskField.DefaultValue/Minimum/Maximum/Group`为null的可选字段，反序列化仍为null、非null原样保留；实际新增能力再次超限后，仅同层额外省略BrowseLocalPath/EditAsList默认false，全部true保留，缺失还原false，omissions明确。分类/字段展示名/能力名称和选项标签保留，omissions解释缺失表示未声明值，不改变授权或校验；Domain不增加JSON属性、不升级协议或放大预算，完整往返回归不减断言。

## 显示数量与Internet条件

`DisplayCountPlugin`的schema 1 `display.count`接受`comparison=at-least|exact`和1–16整数`count`，资源规范化为`at-least:N|exact:N`，Provider拒非canonical资源。`WindowsSessionEvents`追加`displayOnly`最后可选参数，只收WM_DISPLAYCHANGE/恢复，原系统/时钟/USB/供电租约语义不变。同隐藏窗口线程共享通知，64提示合并后一次`EnumDisplayMonitors`计数服务全部最多32条件；只有完整1–64个逻辑屏才Known，0/失败/超64/通知故障Unknown，不当单屏。首次/睡眠恢复/Unknown恢复baseline不补Command；Desired已知初始可持有，结束/Unknown/暂停释放。逻辑数量不是物理数量、设备身份或屏幕布局；不存HMONITOR/ID/坐标，窗口动作执行前自行核验。

`InternetConnectivityPlugin`的schema 1 `network.internet-state`接受`state=available|unavailable`，编译同一`windows-report` bool资源的true/false条件。`WindowsInternetConnectivityEvents`共享MTA Network List Manager connection point，回调只排有界提示，之后读取当前IPv4/IPv6 Internet标志，无ping/HTTP或周期查询；订阅/查询/服务失败及未知标志保持Unknown，明确本地/断开才为false。通知或消费者队列溢出即标故障，包括COM查询仍阻塞的情形；Provider丢弃失效源全部缓存Known，先发布Unknown再释放/等待重启，不使用失效快照继续授权或触发。初始/重新启用/Unknown恢复只建baseline，持续条件可持有唤醒请求、未知释放；源故障不自动重订阅。组合为程序启动、打开目标、通知及保持唤醒，不把Windows报告当应用可达性或网络可信性，不采名称/SSID/IP/位置/设备标识。末租约注销/释放，各消费者独立取消。

## Windows当前媒体控制

`media.current-control`（schema 1）提供暂停、播放、上一项、下一项和Engine延迟，支持现有通用一次性命令星期/时段门禁。用户新增能完成：锁屏后暂停Windows当前媒体、进入指定前台程序后暂停，或用快捷键/定时请求当前媒体上一项/下一项。目标明确是执行时Windows选中的媒体会话；不是按完整exe路径指定播放器，也不是前台窗口或枚举的第一个会话。

公开 [GetCurrentSession](https://learn.microsoft.com/uwp/api/windows.media.control.globalsystemmediatransportcontrolssessionmanager.getcurrentsession) 返回Windows认为用户最希望控制的会话。公开 `SourceAppUserModelId`只提供AUMID，不提供PID或完整exe路径映射，故本能力不声明“指定应用”，不保存或采集AUMID，也不调用媒体属性/标题/曲目API。现有指定应用播放音量能力继续独立使用Core Audio的完整路径核验。

执行端按需 `RequestAsync`，固定当前会话引用，读 `GetPlaybackInfo().Controls.IsPauseEnabled/IsPlayEnabled/IsPreviousEnabled/IsNextEnabled`；请求前重读当前会话并按规范COM `IUnknown`身份及 `CurrentSessionChanged`代际核验，查询取得的引用在finally释放。切换后再切回同一会话也不补请求，不改控新的目标。校验与请求并非系统原子操作，校验后瞬间切换仍可能由刚才固定的旧会话接受请求。

已暂停再暂停、已播放再播放明确成功且不发送请求。没有当前会话、操作未启用、已切换或访问拒绝明确失败。四种 `Try*Async`的true仅为请求接受，不确认实际状态或媒体内容改变；不做读回轮询。`BoundedNativeCommand`共用5秒有界单准入，原始WinRT操作未完成时不会提前释放占用；取消/超时可能已请求，不重试、不恢复，不创建无限线程。没有规则执行时不订阅媒体变化；UI关闭不停止规则。

官方API标注 `globalMediaControl`，AppContainer使用 [uap7 capability](https://learn.microsoft.com/windows/uwp/packaging/app-capability-declarations#general-use-capabilities)。当前Agent为无包身份完整信任桌面程序；SDK WinRT投影已由项目Windows目标框架提供，无新生产依赖。独立 `Axiom.Providers.Windows.Tests`在Windows CI真正请求管理器并读取当前会话/操作支持，不发送播放命令、不吞权限或COM异常；没有媒体会话是正常空结果，不能等同API访问失败。Linux只验证合成会话及交叉编译，不证明Windows授权。真实播放器、浏览器、多会话切换、锁屏、操作支持性及实际播放效果须实机验收。

## 当前交互会话屏幕电源

`SessionDisplayPlugin` schema 1 `screen.state`接受`state=on|dim|off`，最多3个共享只读bool资源，对应同一当前交互会话显示电源报告；仅读GUID_SESSION_DISPLAY_STATUS的DWORD 0/1/2。`WindowsSessionEvents`追加最后可选`screenPowerOnly`，保留clockOnly/usbOnly/powerOnly/displayOnly参数与旧消费者过滤；原生注册失败仅拒新消费者，最后租约注销。仅复制通知值，不读输入、屏幕内容、设备身份或亮度，不主动查询/轮询。畸形长度/其他值、睡眠挂起/恢复及WTS控制台/远程连接切换转Unknown；不能用锁/解锁、WM_DISPLAYCHANGE或亮度代替报告。每次订阅首先Unknown，首次/恢复Known是snapshot基线，不补Command；64队列失效立即Failed、丢积压Known，Unknown先发布再清理，不自动重订阅。整体报告不保证每块物理屏/远程客户端状态，dim可能不报，Modern Standby会延迟或暂停动作。仅组合一次音频/麦克风、当前媒体控制、提醒和program.close；排除整个KeepAwake、亮度/HDR/电源方案/移屏等显示设置组合，无新参数级组合框架。

## 窗口四角布局

一次性`window.state`保持schema 1，`state`增量接受`top-left|top-right|bottom-left|bottom-right`，旧值与已保存参数不变；沿用完整exe多选、标题、等待、延迟、时段策略及独立目标结果。既有模板可选择新布局，不新增设备ID、持久坐标、结束恢复或持续控制。工作区宽高至少2个物理像素，2×2分割，奇数余量分到右侧/下侧；计算边界checked，兼容负坐标。

仅布局（左右/中央/四角）补齐原移屏的进程创建时间核验；不改变关闭、最小化/最大化/置顶路径。`WindowsWindowLayout`固定当前匹配进程句柄、验证存活/创建时间及顶层无拥有者HWND，DPI scope在每次await前恢复，3秒linked deadline在还原后及SetWindowPos前再次核验。相关显示器/工作区变化与精确位置大小读回失败返回未确认，允许已经还原/发送请求；不声称撤销取消前的Windows请求，不重复写入或回写旧布局。主屏/上一屏/下一屏仍由`window.monitor`单独中央80%处理。

## Windows当前媒体播放条件

`media.playback-state` schema1接受`playing|paused|stopped`，同会话最多3个bool资源；Stopped和Closed对应已停止，Opened/Changing/未知枚举为Unknown，没有当前会话明确Known且三个条件全false。只查询公开PlaybackStatus，不调用曲目/应用ID/媒体属性。CurrentSessionChanged先使旧状态Unknown并递增代际，固定新会话并先订阅PlaybackInfoChanged再查询，查询后重读选择及代际核验；旧代回调丢弃。首次/选中会话变化/Unknown恢复仅为Command基线，Known初始状态可授权Desired。64有界提示合并后查询，无轮询；提示溢出立即使租约Failed，Provider丢缓存Known并先Unknown后清理。共享查询线程未退出时拒新租约以免原生阻塞堆线程，末租约取消/注销，不能保证播放器及时或准确报告实际声画。只组合保持唤醒、音频/应用音量/麦克风及提醒，排除直接媒体控制、程序启停和模拟按键反馈。所有校验/组合仍由实际注册和严格导入承担，不改变协议或目录预算。

## Windows网络分类条件

`network.category-state` schema1单选`category=public|private|domain`，同会话最多3个bool资源。公开[SDK netlistmgr.idl](https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/netlistmgr.idl)的INetworkEvents（DCB00004）四方法保持原序；NetworkPropertyChanged仅category(0x10)/connection(0x01)，added/deleted/connectivity亦提示。回调GUID仅入参且不保留，不读取名称/描述/SSID/IP或ID getter。独立共享MTA先Advise再读GetNetworks(CONNECTED=1)，公开_NewEnum及.NET标准IEnumVARIANT最多64项分类及一项超限探测、IsConnected及GetCategory(0/1/2)；异常/未知类别为可能连接的Unknown，不完整枚举保留已读Known匹配但其余不能否定。通知合并和查询代际检查，无ping/HTTP/轮询；64提示/样本队列失效即时Failed，Provider丢缓冲Known，先Unknown后清理。各分类分别维护初次/重新启用/Unknown恢复基线，不补Command，Known初始可授权Desired；不额外订阅睡眠恢复，实机按真实NLM通知核验。不提供分类切换动作，不把专用/域已验证等同安全或可信；末消费者注销/释放，原MTA未结束时拒新源，旧Internet检测不改。


## 当前标准电源方案条件

`power.scheme-state` schema 1仅接受`scheme=balanced|saver|performance`，每项编译独立bool资源，最多3个。匹配实际标准GUID，不按方案显示名或personality推断；有效自定义方案三项均false，空GUID/失败Unknown。组合排除`power.scheme`自反馈，允许现有工具、亮度、音频、提醒及保持唤醒。所有首次/重新启用/Unknown恢复/睡眠恢复均baseline，不补Command；已知Desired可立即成立，结束/未知/暂停释放。

公开[SDK winnt.h](https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/winnt.h)定义`GUID_ACTIVE_POWERSCHEME`与GUID payload；[PowerGetActiveScheme](https://learn.microsoft.com/windows/win32/api/powersetting/nf-powersetting-powergetactivescheme)要求LocalFree释放返回GUID。共享消息窗口追加opt-in过滤，原生注册失败仅拒该条件；合法通知最多64提示合并后一次当前查询，畸形/挂起先Unknown，恢复查询为baseline；不轮询或切方案。电源模式overlay/滑块与此GUID不同；实际设备/策略/Modern Standby通知行为待实机。


## Windows电池节能兼容报告

`power.saver-state` schema 1仅接受`state=on|off`，最多2个独立bool资源。使用[GUID_POWER_SAVING_STATUS](https://learn.microsoft.com/windows/win32/power/power-setting-guids)通知后一次[GetSystemPowerStatus](https://learn.microsoft.com/windows/win32/api/winbase/nf-winbase-getsystempowerstatus)读取[SYSTEM_POWER_STATUS.SystemStatusFlag](https://learn.microsoft.com/windows/win32/api/winbase/ns-winbase-system_power_status)。只认0已关闭/1已开启，任何其他值或失败为Unknown；不查有无电池，不假设真实电池或节能界面支持、不以低电量/方案/电源模式推断。

较新[SDK winnt.h](https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/winnt.h)把此GUID保留为电池节能兼容报告，另有GUID_ENERGY_SAVER_STATUS的Off/Standard/High Savings；当前能力不冒充新版强度检测。实际不同Windows版本/设备/策略的兼容报告待实机验收。

`saverOnly`过滤不泄漏新通知到旧通用电源事件；新注册失败只拒新消费者。合法DWORD 0/1仅为hint，局部PowerContextObservation与电源方案条件共同处理64合并→当前查询；畸形/其他值/挂起先Unknown，恢复查询为baseline。首次/重新启用/Unknown及睡眠恢复不补Command，暂停或源结束后不保持旧Known。支持亮度/音量/提醒/工具等现有动作，排除`power.scheme`与`power.keepAwake`明显反馈；不启用、关闭或改变系统节能设置，无轮询。

## 支持硬件亮度控制的屏幕

`display.hardware-brightness` schema 1严格接受`target=primary|all`、0–100整数`percent`和0–86400整数`delaySeconds`。默认主屏，all为用户明确批量范围；主屏及全部均按执行时逻辑布局选择，不持久化设备身份。两范围/所有百分比共享一个动作资源；只提供一次Command和现有星期/时段门禁，组合快捷键、供电、系统事件及合理定时/工具检测，排除屏幕电源/显示数量等显示反馈检测。原内屏`display.brightness`与窗口API不变。

公开[GetPhysicalMonitorsFromHMONITOR](https://learn.microsoft.com/windows/win32/api/physicalmonitorenumerationapi/nf-physicalmonitorenumerationapi-getphysicalmonitorsfromhmonitor)配[PHYSICAL_MONITOR](https://learn.microsoft.com/windows/win32/api/physicalmonitorenumerationapi/ns-physicalmonitorenumerationapi-physical_monitor)为HANDLE与128 WCHAR，[SDK physicalmonitorenumerationapi.h](https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/physicalmonitorenumerationapi.h)为Pack=1。2026-10-14 实机确认：能力查询可能返回DDC/CI命令错误，但同一物理目标的高层亮度读取、设置仍可成功，故不再把能力位作为前置门。默认未启用渐变时先用[GetMonitorBrightness](https://learn.microsoft.com/windows/win32/api/highlevelmonitorconfigurationapi/nf-highlevelmonitorconfigurationapi-getmonitorbrightness)读取已知有效范围，才允许同一高层[SetMonitorBrightness](https://learn.microsoft.com/windows/win32/api/highlevelmonitorconfigurationapi/nf-highlevelmonitorconfigurationapi-setmonitorbrightness)写入；此时不额外查询能力。读取失败或范围无效时，才用[GetMonitorCapabilities](https://learn.microsoft.com/windows/win32/api/highlevelmonitorconfigurationapi/nf-highlevelmonitorconfigurationapi-getmonitorcapabilities)的MC_CAPS_BRIGHTNESS=0x2区分明确不支持与Unknown；两者均不写。必须min<max且current在范围内，按`min + floor((max-min)*percent/100)`用UInt64计算；设置后范围及值精确读回才确认，不接受猜测范围或写入重试，不因写后读回失败切换接口重写。

首次启用硬件渐变时，在读基线前固定选择亮度VCP0x10的[GetVCPFeatureAndVCPFeatureReply](https://learn.microsoft.com/windows/win32/api/lowlevelmonitorconfigurationapi/nf-lowlevelmonitorconfigurationapi-getvcpfeatureandvcpfeaturereply)与[SetVCPFeature](https://learn.microsoft.com/windows/win32/api/lowlevelmonitorconfigurationapi/nf-lowlevelmonitorconfigurationapi-setvcpfeature)，只接受连续型且0≤current≤max、max>0的有效回复，按0–max映射。参考[PowerToys亮度入口](https://github.com/microsoft/PowerToys/blob/main/src/modules/powerdisplay/PowerDisplay.Lib/Drivers/DDC/DdcCiController.cs)，不扩展其他VCP。恢复租约首次捕获即固定接口，后续开关渐变不切换接口、不重抓基线；未知、写失败或不匹配不fallback。默认旧规则继续高层接口，自动亮度仍优先WMI。

同次实机还确认写入可生效而紧接着的读取暂时失败。高层和VCP亮度读取仅对已观察的`ERROR_GRAPHICS_DDCCI_INVALID_MESSAGE_COMMAND`（0xC0262589）允许在原句柄上最多3次只读查询；失败后可取消等待150ms，仍计入原10秒期限。底层读取成功即返回实际值，不因范围无效或值与目标不同而循环查询；硬件渐变段末识别自己已接受的延迟中间值时，使用下述独立有限只读等待；其他错误或取消立即停止。首次基线、Command读回和恢复守卫共用此窄策略，Set始终单次，全部查询结束前保留原句柄与准入；无后台计时器、环境轮询或写入重发。[PowerToys暂态查询实现](https://github.com/microsoft/PowerToys/blob/main/src/modules/powerdisplay/PowerDisplay.Lib/Drivers/DDC/VcpFeatureProbeService.cs)与[错误分类](https://github.com/microsoft/PowerToys/blob/main/src/modules/powerdisplay/PowerDisplay.Lib/Drivers/DDC/DdcErrorClassifier.cs)提供对照，Axiom仅启用上述实际观察错误。

最多64逻辑、每逻辑8物理、总64句柄，枚举数量先检查再打开，完整有界发现后才写。未知目标不写但其他支持目标可继续；结果仅计数，不记录显示器描述/身份。SID和当前WTS活动会话授权不成立拒绝；锁定仍可执行已授权系统事件动作。所有成功/部分发现/异常/取消/超时均由worker finally尝试[DestroyPhysicalMonitors](https://learn.microsoft.com/windows/win32/api/physicalmonitorenumerationapi/nf-physicalmonitorenumerationapi-destroyphysicalmonitors)，清理失败不阻断其他lease释放并使回执未确认。整批10秒BoundedNativeCommand，调用者取消/超时不提前释放gate/句柄、真正原生返回后才清理；Agent关停Drain。不操作其他VCP、不轮询、不依赖厂商接口，新亮度保留，取消可能已写入，不回滚。真实DDC/CI启用/坞站/MST/虚拟显示/驱动差异待设备验证。


## 条件期间亮度与结束恢复

`display.brightness.restore`/`display.hardware-brightness.restore`新schema 1沿原绝对percent/delay及硬件primary/all字段，不更改旧Command行为。只有持续检测组合，亮度模块各自固定`brightness`Desired资源，配置经严格校验后沿`RestorableSettingInvocations`规范化绝对目标；不同硬件selection同资源竞争；范围改变先等待旧lease明确释放/清理确认才新捕获，不在原lease内换目标。首次capture只Known，硬件原始DWORD范围/值保留而非重算百分比基线；更新百分比不重抓基线，相对增减保持一次Command。

本地两个native入口共享亮度ownership，恢复期间其他Axiom亮度写拒绝，外部变化仅在更新/释放及有限渐变确认点（硬件为段边界）核验，稳定持有时无周期读取或持续压制。原生worker固定所有WMI对象/物理句柄并同线程执行；硬件捕获复用上述已知亮度优先准入，后续更新/释放继续核验首次固定接口、原范围、授权及拓扑，不重复以能力查询否决有效当前值。displayOnly拓扑/恢复信号永久失效本轮，失败不枚举替代屏。per-target共享ConditionalSettingTransaction当前/最后确认相等检查，Unknown、不一致/其他来源改变后让出，不恢复猜测值；部分支持/未知/写读回/恢复与资源失败只输出计数，不输出设备描述或WMI身份。

Release用独立清理token，末尾全部finally尝试清理，内屏对象及每组物理句柄清理异常不阻断其他资源。捕获固定10秒；未启用渐变的Apply/Release仍10秒，启用后增加所选过渡时长，调用者超时取消并不表示真实原生停止，资源与准入仍保留到worker真正返回；只按事件/条件生命周期工作，不重试或相对累加。比较与写入非原子、异常进程退出/驱动阻塞不能保证恢复，实机支持性待验。UI退出不结束后台租约；暂停、删除、Unknown及条件结束走共享租约释放回执。
六亮度动作共享fadeEnabled（默认false）、fadeDurationSeconds（1–15整数，默认3）和fadeWritesPerSecond（1–60请求次/秒，缺省10，推荐18），兼容旧规则。有限smoothstep时间线目标16ms；原生慢时按实际时间跳帧，不积压、不重写同值。WMI逐步严格确认并按耗时重算。硬件段开始核对最后确认值，最多1秒连续写入，每个候选单次Set；按1000/fadeWritesPerSecond限流，最终Set另保留至少100ms绝对期限。段内手动改值可能被覆盖，这是已选择的有限顺滑窗口；稳态无轮询。

2026-10-18重复往返实机发现Set接受后中间值可永久跳过（例如发41、实读43），所以继续下一段/发终点前执行“进度检查”：只接受最近有限段内实际写过的值，记录实际读数，丢弃未兑现尾部并重新计算后续帧；不声称旧期望已完成、不重发它。仅保留该已观测且写过的读数，允许它在下一次终点严格确认时作为≤1秒延迟值等待。最初基线和未写过的值不在接受集合；Unknown、取消、外部值、写失败立即让出。最终目标及恢复原基线仍必须精确读回，进度检查不能完成终点或Release；未确认pending不能凭旧Confirmed恢复。非渐变读写及窄只读错误策略不变，所有清理仍保持原生准入。

缺省10次/秒是兼容旧硬件设置，不是测得的硬件频率上限。补充有限实机实验：整段75/60/约55ms写入各0/3完整往返；改为中段75ms或无人工等待（实际约55ms）、仅最终Set与前一笔间隔至少100ms后，两档均3/3终点严格确认成功，前后100ms对照各1/1。原生接口、确认与清理均沿生产DLL，实验时仅夹具改变调度；本批已移植为可调平滑度的硬件末笔策略。它证明更高频请求与终点确认可以兼得，不证明每个中间物理亮度都兑现或手机级视觉效果，见[实测记录](../exec-plans/completed/brightness-responsive-fade.md)。

原生动作10+过渡时长最多25秒，命令绑定30秒、恢复绑定40秒；Host仅启用亮度渐变时使用首次37秒、更新/释放30秒，Provider协议45秒上限保持；Agent亮度清理/Drain30秒。正常停止须覆盖有限恢复，超时仍保留现场而非强退。设备实际响应与粒度决定可见平滑度，非逐帧动画；[SetMonitorBrightness](https://learn.microsoft.com/en-us/windows/win32/api/highlevelmonitorconfigurationapi/nf-highlevelmonitorconfigurationapi-setmonitorbrightness)为同步设备调用。

## 有界状态组合检测

`TaskDetectionExpression(Kind, Invocation?, Children)`只支持leaf/all/any/not：leaf只有Invocation；all/any至少2个子节点；not恰好1个。根为深度1，最多4层、16叶、64节点，空组/混合叶与子节点/未知运算符/越界拒绝。没有脚本或任意表达式。`AutomationTask.DetectionExpression`非空时使用task schema3（有外层运行条件则schema4），`Detection`严格按内容等于深度优先有序首叶，仅供旧展示，绝不替代树执行。schema1/2不得携树；3可保留原TimeWindow并沿原动作门禁校验。Domain不依赖JSON，叶的默认Children归一为空数组；常规序列化允许null/false默认字段，单检测及协商语义由版本/树校验保持，匿名摘要末级省略false标记仍还原false。

Snapshot.CompositeDetectionVersion=1及所选action.SupportsCompositeDetection=true共同协商；只有检测IsStateCondition=true可选为叶，脉冲/快捷键/定时/启动检测保留单检测路径、不组合或否定。PluginTaskRuntime启用compiler支持并登记每个允许对，编译、保存、加载和Router逐叶检查同一个动作/同一个支持树的backend，继承全部反馈排除。HDR旧backend/legacy转换明确拒绝树。Domain已有All/Any/Not三值：NOT Unknown=Unknown，AND中明确false可决定false，OR中明确true可决定true；Engine依赖集合及观察计划去重合并，未知/结束/暂停/源释放撤销持续授权，首次及恢复snapshot不补Command。

SQLite无需新配置表：schema3树JSON保存/读回，首次保存3时在同一事务把user_version提升3，重新打开不降版；旧应用在读取/覆盖前因数据库版本较新拒绝，旧1/2记录由新应用完整读取。任务管道仍版本1/原frame上限，save先检查树结构与schema，未知新树不能落回anchor。AI建议组合使用UsageImport.Version2且携同一DetectionExpression/首叶Detection，版本1不能夹树；逐叶解析观察别名、必填/手填和能力约束，每叶支持同动作且目录协商才给默认暂停schema3草稿。匿名摘要/机器目录保留协商及state/action标记，原64KiB不变。

“非锁定”使用独立`session.lock-state`，state=locked|unlocked，资源current-session为锁定bool；只读WTSInfoEx当前Active会话及锁元数据，未知/非活动不假未锁定，不读取用户名等内容。WindowsSessionEvents(lockStateOnly:true)先订阅后初读；7/8仅提示查询，1/3/5/9及恢复先Unknown再重查建基线，2/4/6及挂起只失效等待报告。64有界合并保留reset，队列失败立即标记并清缓存，新kind不进旧租约；没有轮询、锁屏请求或自动解锁。允许提醒、播放设置/应用音量及保持唤醒，排除锁屏/显示动作反馈。

## 任务级自动暂停

`AutomationTask.RunConditions`使用schema4，包含主屏`Hdr=any|on|off`与可选`WeeklyTimeWindow`，两者均须满足；不改旧`TimeWindow`动作执行门禁。schema4也可保留组合树。Snapshot.RunConditionsVersion=1协商，未支持的编译器、旧schema夹带或旧数据库版本不能静默忽略。SQLite首次保存4原地升user_version4，不批量改旧配置；导入Version3可携运行条件，旧导入1/2拒绝夹带。

Host维持独立外层HDR订阅和时钟/恢复通知，仅设置一个最近时段边界deadline。HDR先订阅再初读，随后依赖AdvancedColorInfoChanged及拓扑/恢复事件；外层和普通`display.hdr-state`检测共享原生来源，不采集画面、不轮询状态。HDR未知保持自动暂停，不假装关闭。时间按本地周时段、起点包含终点排除、跨午夜归起点星期。

Engine自动暂停独立于持久手动Paused/version，移除该规则内部观察需求，取消等待及在途授权，Desired State沿既有保护释放；其他任务共享的需求不释放。恢复重新获取状态基线，旧边沿不重放，一次Command不因恢复快照补发；Desired State按新的当前条件重算。手动暂停不会因HDR/时间满足而自动启用。

同用户`watch-run-states`原子订阅并返回全量TaskRunStatus，初始sequence0，后续有界合并状态变化；断开释放。仅握手有deadline，闲置无心跳/刷新计时器。UI显示手动暂停、自动暂停及原因；流断开显示状态未确认，显式刷新重连，不继续宣称旧状态有效。
