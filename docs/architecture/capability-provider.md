# Capability / Provider contract

面向任务编辑的检测/动作描述、独立编译器与后端接入见 [任务接入 contract](../development/task-authoring-contracts.md)。表单描述不能替代本页运行时能力授权与观察/执行语义。

Capability 描述“允许做什么及其语义”，Provider 是实现这些能力的系统适配器。规则和 UI 依赖能力标识与 schema，不依赖 Win32 API 或 Provider 类名。

## Descriptor 必须声明

- 稳定 CapabilityId、schema version、typed 参数/结果、资源 scope、Observe/Command/DesiredState 支持情况。
- 执行位置（Service 或对应 Session Agent）、所需权限、可发现的可用性与不可用原因。
- 绝对值设置/幂等能力、超时、可取消性、并发范围、重试类别、状态确认来源。
- 事件保真要求（latest-value 或 edge-sensitive）、依赖键、刷新/验证允许的最大延迟。
- 成本等级、观察器共享方式、激活条件、正常/轻量模式支持、是否需要经批准的轮询例外。
- 参数验证与资源授权范围；不可接受原始 shell 字符串或未经约束的系统路径作为通用执行能力。

## 生命周期

Discover 元数据不启动全部观察器。按有效规则、用户订阅、Override 必要监测与待执行动作计算需求；订阅使用引用计数，最后依赖释放后注销 OS 通知、停止定时器、释放句柄。昂贵 Provider 仅按需激活，禁用规则不能遗留后台观察器。

启动顺序采用订阅与快照对账，避免“先快照再订阅”的事件窗口；具体 API 若不能原子保证，必须记录 gap 并补快照。Stop 可重复调用；异常退出后的句柄、子任务、订阅必须清理。事件回调只做有界投递，不阻塞系统回调执行规则或写数据库。

## 执行与故障

Engine 发出已授权且带 generation/commandId 的请求；Provider 再验证操作在当前会话/权限下有效。拒绝 unsupported、scope mismatch 和不合法参数。结果区分瞬态、永久、超时未知副作用；超时不等于没有发生。

同一资源只允许一个动作在途。非幂等命令超时后禁止自动重试；幂等状态设置允许受控退避，但必须重验最新 Desired/Override。Provider 健康状态变化事件唤醒恢复；没有有效工作时不跑健康检查循环。

2026-10-04 已接入显式注册的内置模块系统：`IAutomationPlugin`（纯能力声明/编译）、`PluginTaskRuntime`（统一任务存储、Engine、会话命令分发）、`ISessionCapabilityProvider`（会话观察/执行）。`ProgramPlugin` 和 `ProgramCapabilityProvider` 是真实例子。新增会话命令模块不修改 UI 或数据库表；外部 DLL 自动扫描、热加载和插件市场不在当前范围。HDR Desired State 的接管/恢复仍保留专门生命周期适配器。

程序启动只调用本机完整路径 .exe，`UseShellExecute=false`；启动参数直接交给目标程序，不拼接命令行解释器。关闭限定当前 SID/会话和完全匹配的映像路径，并先固定进程句柄防 PID 复用；正常关闭超时不擅自转为强制结束。延迟由 Engine 维护而非 Provider 等待。管理员 Agent 按需共享 `Win32_ProcessStartTrace` 原生事件，无窗口进程也可检测；当前会话和名称过滤后才核对 PID/路径/SID/创建时间，退出等待句柄。无消费者时注销，队列溢出/源故障标 Unknown，不使用 WITHIN、窗口降级或周期枚举。初始/重连仅建基线，不补发命令；极短进程可能在核对前已退出。

2026-10-05 `TrayPlugin`/`TrayCapabilityProvider` 复用上述会话 Command 链路，支持 32 个完整路径目标，等待 1–30 秒与 Engine 延迟。WindowsTrayActions 只读托盘注册记录、按完整映像身份授权并移除当前可识别图标，保留进程；按需共享系统通知，无注入或周期扫描。不提供持续抑制/自动恢复，图标重建可再次出现；系统记录格式非公开，无法识别明确失败。GUID 的归属依赖系统记录路径并拒绝冲突，普通 ID 另外核对 HWND/PID；详细限制与当前平台证据见 [ADR 0010](../adr/0010-tray-icon-discovery.md)。

2026-10-16工具箱另提供可选持续管理：按启用配置订阅Agent目标托盘变化，通知经Host/Engine授权后再次执行原一次命令；原tray.hide动作语义不变。订阅按完整路径限定、只读观察不执行删除，系统事件合并后核对当前活动身份；不定时扫描，暂停/改版/停止释放，Explorer重建重新绑定。关闭管理不恢复图标；事件与识别覆盖仍受ADR0010限制。

2026-10-21 `hosts.update` 是显式注册的Service Command，仍由PluginTaskRuntime/Engine授权和仲裁，管理员Host只访问系统目录的`drivers/etc/hosts`，调用参数不能提供文件路径。复用所有已注册公开检测；单一system-hosts资源、能力级串行、90秒期限、不自动重试。DNS为显式IP上游UDP/截断转TCP，DoH使用[RFC8484](https://www.rfc-editor.org/rfc/rfc8484)标准报文POST。`useSystemProxy`为可选Boolean，缺省/旧配置为false；仅DoH可显式启用系统默认HTTP代理，按.NET默认代理（Windows环境变量优先、否则当前用户设置）及绕过规则连接，DoH服务地址解析交由系统/代理，bootstrap只在关闭选项时的直连路径使用。未配置代理或命中绕过可能直连，不承诺强制代理；普通DNS仍显式IP直连，不经HTTP代理。无自定义代理地址/凭据字段、不持久化系统代理信息；新参数旧执行方严格拒绝，不能静默忽略。TLS保留原域名与证书校验、禁止重定向。每次至多16域名/4并发，A/AAAA及有界CNAME链；响应核对ID、问题、类别及答案归属。整批解析成功后才读取/合并管理区，拒绝手工同名冲突/损坏标记，保留原编码、其他内容及ACL，无配置备份。写入前重新检查取消/时段和版本，提交后完成有界写入及回读；I/O或持久回执不明标Unknown，不能当未发送重试。暂停/移除名单不撤销已写记录；程序自身缓存和现有连接不承诺立即变化。
