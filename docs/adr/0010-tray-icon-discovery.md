# ADR 0010：托盘图标按系统身份记录发现并一次性移除

2026-10-05，接受。用户要求规则动作多选程序，图标在通知区域和折叠区都消失，进程继续运行。复用会话 Command，不增加目标注入/Explorer 内存操作或周期扫描。

公开 Shell_NotifyIcon(NIM_DELETE) 支持按 HWND/ID 或 GUID 删除，但不提供按进程枚举。Chromium 使用过的未公开 ITrayNotify 在已验证的 Windows 11 环境 返回成功、零 cookie 和空回调，不能用它证明没有图标。真实独立夹具的普通 ID/GUID 图标已验证跨进程删除、删除后 GetRect 失败、再次删除失败且进程保持。

选择：只读当前用户 `Control Panel/NotifyIconSettings` 的 ExecutablePath、UID、IconGuid；解析系统 KnownFolder 路径，匹配规则完整路径。普通 ID 与当前用户/会话、完整映像路径核对过的进程窗口（含 message-only）组合；删除前再核对 PID/窗口归属。GUID 使用系统记录的完整路径归属，拒绝同 GUID 的路径冲突，且目标必须真实运行；不枚举任意编号或删除所有图标。只用公开 Shell_NotifyIcon 删除，不修改显示偏好/缓存注册表。

该注册表格式未公开，Windows 更新可能改变；识别失败/图标未找到/删除拒绝明确失败，不能把折叠或缓存条目当成功。按需共享注册表通知与 Explorer 图标窗口事件，订阅后读一次初始名单；最长等待120秒（2026-10-19替代原30秒上限）、取消立即释放，无周期枚举。通知只是触发重新核对，不当成身份凭据。没有待执行命令就无此订阅。

一次触发隐藏目标当前全部可识别图标。不持续抑制后续创建；重启程序或 Explorer 可重新出现，暂停不会恢复已经移除的图标。移除可能同时去掉气泡通知/托盘菜单入口；需要重新打开或重启目标软件恢复。不接入持续 Desired State、不假装能原样恢复缺失的图标配置。不等同于窗口的任务栏按钮。

依据：[Shell_NotifyIcon](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shell_notifyiconw)、[图标身份](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/ns-shellapi-notifyicondataw)、[Chromium 的接口及版本警告](https://chromium.googlesource.com/chromium/src/+/refs/heads/main/chrome/browser/ui/views/status_icons/status_tray_state_changer_win.cc)。当前实测不代表全部 Windows 更新版本通过。

2026-10-06 决策调整：已证实目标应用可能以当前 HWND 数值作为动态 UID，而缓存仍保存旧 UID。允许对每个已核对归属的目标窗口额外只读检查一个 `uID = (uint)HWND` 身份候选，只有 GetRect 确认当前存在才进入删除列表；删除前仍重验窗口/PID/进程存活和 GetRect。没有编号区间试探、注入或 Explorer 内存操作。此修改明确替代原“只用缓存 UID”的限制，不承诺覆盖任意未知 UID。

动作和选择面板共用 FindActive 活动核验。面板只在打开/刷新读取，列出核验过的当前图标（含折叠区），按实际完整 exe 路径合并同程序图标；名称读取当前 exe 的描述，不能使用含旧版本的 InitialTooltip 冒充实时文字。发现路径来自当前会话进程及只读记录，最终仍经 SID/会话/完整映像路径核对，不据父进程或提示文字推断。读取上限 512 个候选运行路径、128 个可操作程序、每目标 64 记录/64 活动图标/256 窗口、4096 message-only 窗口；超限拒绝截断成功，权限或归属不明跳过并说明。Host 经原 Provider 管道向 Agent 发只读 read-tray，UI 不直接调用系统或执行动作。缓存历史/已经移除图标不显示，选择只保存完整路径，暂态 HWND/UID 不持久化。

某些应用实际归属内层 `bin/Application.exe`，外层启动器路径不匹配；直接选当前托盘可保存正确路径，但不会静默把已有规则扩展到子进程。公开 Windows 接口没有完备的按进程枚举，本机旧 COM 又无结果；系统记录格式或未知动态 UID 可使部分图标无法识别，面板明确“当前可识别”，不虚报完整全版本支持。动作仍一次性处理，不增加常驻轮询。

## 2026-10-16：工具箱的可选持续管理

所有者要求工具箱提供“任务栏托盘管理”，由用户选择仅Axiom启动时隐藏或持续隐藏。此决策替代上文对工具持续订阅的禁止；原规则动作tray.hide仍一次性执行，已有用户规则不迁移。

持续管理以系统通知触发重新核对配置目标，经Host/Engine授权后执行一次删除；它不是可恢复的Desired State。不定时扫描、不注入、不修改Explorer内存或托盘偏好。只有启用且持续模式才保持观察订阅；暂停、改模式、停止均释放，回调只作有界通知并合并去重。删除引发的通知必须核对当前活动图标，不能形成重复空转执行。订阅后核对初始状态，Explorer重建后重新建立必要观察，不把旧PID的hook当成仍有效。

公开[SetWinEventHook](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwineventhook)按非零进程ID过滤；Shell的[TaskbarCreated通知](https://learn.microsoft.com/en-us/windows/win32/shell/taskbar)说明任务栏重建。这些通知仅作失效提示，不证明图标归属或覆盖所有应用。完整路径及当前身份授权、识别失败说明、关闭不恢复、菜单与通知影响的限制继续适用。

2026-10-19：实机启动记录显示部分目标晚于后台约30秒启动，旧1秒配置错过后无后续执行。等待范围扩至1–120秒，默认仍30秒；由工具配置选择适合本机的期限，不静默迁移其他规则。托盘命令截止为130秒，Provider仅对通过TrayCapabilities校验的tray.hide Invoke允许最多135秒请求，其他请求仍45秒。各目标并行等待，不添加定时重试或常驻订阅；启动一次仍只处理期限内首批可识别图标，不承诺压制之后的重建。
