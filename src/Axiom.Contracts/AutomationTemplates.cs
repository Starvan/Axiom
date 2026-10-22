using System.Collections.Immutable;
using Axiom.Domain;

namespace Axiom.Contracts;

public sealed record AutomationTemplate(string Id, string Name, string Description, AutomationTask Draft);

// Recipes reference the live catalog; they do not duplicate capability schemas or save rules.
public static class AutomationTemplates
{
    private sealed record Recipe(string Id, string Name, string Description, string Detection, string Action,
        (string Field, string Value)[] DetectionValues, (string Field, string Value)[] ActionValues);
    private static readonly Recipe[] Recipes =
    [
        new("screen-content-night-dim", "夜间明亮画面时调整亮度并恢复", "默认20:00–08:00按大块亮区评分检测；进入20、退出12，稳定0.5秒后设置40%或恢复原值。HDR暂停由任务运行条件另行选择。40%是绝对目标，请改成低于日常亮度的值；先审阅后启用。", ScreenContentCapabilities.Bright, BuiltInBrightnessRestoreCapabilities.Set, [("mode", "regions"), ("enterPercent", "20"), ("exitPercent", "12"), ("stableSeconds", "0.5"), ("start", "20:00"), ("end", "08:00")], [("percent", "40")]),
        new("public-network-reminder", "出现公用网络分类后提醒", "Windows从没有公用匹配新变为有公用匹配时，请求安静桌面提醒；首次、重新启用及未知恢复不补提醒，不读取网络名称。分类不代表Internet可用性或安全可信，请按实际需要审阅规则。", NetworkCategoryCapabilities.State, NotificationCapabilities.Show, [("category", "public")], [("title", "Windows网络分类"), ("body", "Windows报告当前有已连接网络归为公用；分类不代表Internet可用性或安全可信。")]),
        new("application-audio-awake", "指定程序音频活动期间保持唤醒", "选择实际播放器或会议工具完整exe路径；默认播放端点存在其已核验活动渲染会话时保持系统唤醒，屏幕仍可关闭。首次已活动即可持有，全部不活动、未知或暂停后释放；不证明实际发声、收音或会议进行，不修改音量/静音。", ApplicationAudioActivityCapabilities.Active, KeepAwakeCapabilities.Hold, [], [("target", "system")]),
        new("current-media-awake", "当前媒体播放期间保持系统唤醒", "Windows选中的当前媒体会话报告正在播放时，持有Axiom自己的系统唤醒请求，屏幕仍可关闭。暂停、停止、没有会话、未知或暂停规则后释放；不指定播放器，不证明实际出声，不阻止手动睡眠。", MediaPlaybackCapabilities.State, KeepAwakeCapabilities.Hold, [("state", "playing")], [("target", "system")]),
        new("lock-pause-current-media", "锁屏后暂停Windows当前媒体", "锁屏的新通知后，向Windows选中的当前媒体会话请求暂停一次；目标可能是播放器或浏览器，不指定程序。没有媒体会话或播放器不支持时会说明，解锁不自动播放。", SystemEventCapabilities.Event, CurrentMediaCapabilities.Control, [("event", "locked")], [("operation", "pause")]),
        new("screen-off-pause-current-media", "当前会话屏幕关闭后暂停媒体", "Windows当前会话新报告屏幕关闭后，向Windows选中的当前媒体会话请求暂停一次；没有媒体会话或播放器不支持时会说明。首次及睡眠恢复不补执行，屏幕亮起不自动播放，不证明每块物理屏状态。", SessionDisplayCapabilities.State, CurrentMediaCapabilities.Control, [("state", "off")], [("operation", "pause")]),
        new("session-absent-mute", "Windows报告离开后静音播放设备", "当前会话新报告离开后，将默认播放设备静音一次；不自动恢复，不证明实际离座。首次已离开及恢复只建基线；请审阅后启用。", SessionPresenceCapabilities.State, AudioPlaybackCapabilities.Set, [("presence", "absent")], [("operation", "mute"), ("percent", "30"), ("delaySeconds", "0")]),
        new("foreground-cover-keep-awake", "前台窗口覆盖屏幕期间保持唤醒", "当前前台应用可见边框覆盖整块显示器时保持系统及屏幕唤醒；离开、未知或暂停后释放。不证明游戏独占或视频播放，无需填程序路径；请审阅后启用。", ForegroundScreenCoverCapabilities.Covers, KeepAwakeCapabilities.Hold, [], [("target", "display")]),
        new("screen-off-mute", "当前会话屏幕关闭后静音", "Windows当前会话报告屏幕关闭后，将默认播放设备静音一次；不自动恢复。首次及睡眠恢复不补执行，不证明锁屏或每块物理屏已关闭，现代待机可能延迟执行。", SessionDisplayCapabilities.State, AudioPlaybackCapabilities.Set, [("state", "off")], [("operation", "mute"), ("percent", "30"), ("delaySeconds", "0")]),
        new("multi-display-awake", "多屏工作期间保持系统唤醒", "Windows逻辑桌面至少2个显示器且状态已知时持有系统唤醒请求，屏幕仍可关闭；退回单屏、未知或暂停后释放。首次已多屏即可生效，不保证物理屏幕数量，不阻止用户手动睡眠。", DisplayCountCapabilities.Count, KeepAwakeCapabilities.Hold, [("comparison", "at-least"), ("count", "2")], [("target", "system")]),
        new("single-display-primary-window", "返回单屏后整理工作窗口", "选择程序完整路径；只在Windows逻辑桌面新变为恰好1屏时，把一个匹配主窗口放到主屏中央80%。最小化或最大化先还原，首次单屏和睡眠恢复不补移动，不保存或恢复旧布局。", DisplayCountCapabilities.Count, WindowMonitorCapabilities.Move, [("comparison", "exact"), ("count", "1")], [("monitor", "primary")]),
        new("internet-tools", "Windows报告Internet可用后启动工具", "选择同步或工作工具；只在Windows报告的新可用状态后请求启动，首次已有Internet不补执行。不主动探测，不保证目标服务可达，VPN或NCSI策略可能影响报告；未知不当作离线。", InternetConnectivityCapabilities.State, "program.start", [("state", "available")], []),
        new("lower-background-priority", "后台工具开始运行时降低优先级", "选择检测程序名及目标完整exe路径；检测组从全部未运行变为任一运行时，把执行时目标的所有当前实例设为低于正常。同名检测可能来自其他路径；组仍运行时新增实例不再触发，首次已运行不补执行。未运行或权限不足会失败，不限制CPU使用率，退出或暂停不恢复。", "process.running", ProgramPriorityCapabilities.Set, [], [("priority", "below-normal")]),
        new("temporary-program-priority", "指定程序运行期间降低优先级，结束恢复", "选择检测程序名及一个完整目标exe路径；固定本轮已运行的全部匹配实例，结束仅在优先级仍为Axiom最后确认值时恢复各实例原值。新实例/退出/身份未知或外部调整不被覆盖；不是CPU使用率限制。", "process.running", ConditionRestoreCapabilities.ProgramPriority, [], [("priority", "below-normal")]),
        new("temporary-window-layout", "指定程序运行期间整理工作窗口，结束恢复", "选择检测程序及一个完整目标exe路径；固定当前一个可见主窗口居中80%，结束仅在完整placement/身份/拓扑仍可核验且未被外部改变时恢复原显示状态/位置。不等后来窗口，拖动/销毁/未知让出。", "process.running", ConditionRestoreCapabilities.WindowState, [], [("state", "center")]),
        new("temporary-playback-volume", "指定程序运行期间调低音量，结束恢复", "选择检测程序；首次已知运行即可将默认播放音量设为30%，结束仅在仍等于Axiom最后确认值时恢复原音量。外部调整、端点变化或未确认写入让出，不改新设备；不恢复静音或应用状态。", "process.running", ConditionRestoreCapabilities.Playback, [], [("operation", "volume"), ("percent", "30")]),
        new("temporary-microphone-mute", "指定程序运行期间静音麦克风，结束恢复", "选择检测程序；条件期间设置执行开始默认通信麦克风静音，结束按最后确认值核验后恢复原状态。恢复原取消静音可能重新收音；外部修改或端点变化不覆盖。", "process.running", ConditionRestoreCapabilities.Microphone, [], [("operation", "mute")]),
        new("temporary-power-scheme", "指定程序运行期间用高性能方案，结束恢复", "选择检测程序；仅使用本机已有标准高性能方案，结束可恢复本轮原标准或自定义方案。Windows/其他来源改变则让出；缺方案或未确认不重试，不操作电源模式滑块。", "process.running", ConditionRestoreCapabilities.PowerScheme, [], [("scheme", "performance")]),
        new("launch-companion", "打开程序后启动配套工具", "选择检测程序和要启动的工具；工具已运行时跳过，结束后不自动关闭。", "process.running", "program.start", [], []),
        new("cleanup-companion", "程序退出后关闭配套工具", "只请求正常关闭最近启动的一个匹配实例；不强制结束、不自动重开，未保存内容可能需要确认。", "process.exited", "program.close", [], [("mode", "graceful"), ("instances", "latest")]),
        new("window-cleanup", "工作窗口全部消失后收尾", "选择工作程序和配套工具；全部工作主窗口隐藏或销毁后请求正常关闭配套工具最新实例。最小化不触发，程序可能仍在后台；不强杀、不自动重开。", WindowPresenceCapabilities.Absent, "program.close", [], [("mode", "graceful"), ("instances", "latest")]),
        new("maximized-window-awake", "指定程序窗口最大化期间保持唤醒", "选择程序完整路径；任意一个可见主窗口最大化时持有系统及屏幕唤醒请求。全部还原、最小化、隐藏或未知后释放，暂停也释放；首次已最大化即可生效。最大化不等于全屏演示，不阻止用户主动睡眠。", WindowStateDetectionCapabilities.StateIs, KeepAwakeCapabilities.Hold, [("state", "maximized")], [("target", "display")]),
        new("restore-work", "睡眠恢复后打开工作工具", "选择要启动的程序；恢复不等于已经解锁，已运行则跳过，不补发之前的唤醒。", SystemEventCapabilities.Event, "program.start", [("event", "resumed")], []),
        new("low-battery-dim-panel", "电池电量低于阈值时调暗屏幕（自动选择）", "默认低于25%且使用电池时设为30%；优先所有支持内屏，明确没有内屏接口时使用主屏硬件亮度。可修改阈值，首次已低电量不补执行，接电或电量恢复不自动改回；查询未知不切换外屏。", PowerStateCapabilities.State, BuiltInBrightnessCapabilities.Set, [("mode", "battery-below"), ("threshold", "25")], [("percent", "30")]),
        new("plugged-in-awake", "接电期间保持系统唤醒", "接电且状态已知时持有Axiom自己的系统电源请求，允许屏幕关闭；拔电、状态未知或暂停后释放。首次接电即生效，不修改全局电源方案，不阻止用户主动睡眠。", PowerStateCapabilities.State, KeepAwakeCapabilities.Hold, [("mode", "ac")], [("target", "system")]),
        new("battery-low-brightness-restore", "低电量期间调暗屏幕并恢复（自动选择）", "电池供电且电量低于25%期间设为30%；首次优先支持内屏，明确没有内屏接口时捕获主屏硬件亮度。后续固定原目标；结束或暂停时仅仍等于最后确认值才恢复最初亮度，未知或其他来源改值不覆盖。请审阅后启用。", PowerStateCapabilities.State, BuiltInBrightnessRestoreCapabilities.Set, [("mode", "battery-below"), ("threshold", "25")], [("percent", "30")]),
        new("standard-saver-hardware-brightness-restore", "标准节能方案期间调暗主屏并恢复", "标准节能方案条件成立时将支持硬件控制的主屏设为30%，结束后仅按本轮已知基线和最后确认值恢复；拓扑改变或外部改值不覆盖。通常需要DDC/CI支持，请审阅后启用。", PowerSchemeStateCapabilities.State, HardwareBrightnessRestoreCapabilities.Set, [("scheme", "saver")], [("target", "primary"), ("percent", "30")]),
        new("shortcut-internal-brightness-down", "按快捷键降低屏幕亮度5个百分点（自动选择）", "优先读取支持内屏，明确没有内屏接口时读取主屏硬件亮度，再降低5个百分点并限幅；未知不写或切换。新亮度保留，不自动恢复，审阅快捷键后启用。", HotkeyCapabilities.Pressed, BuiltInBrightnessAdjustCapabilities.Adjust, [], [("operation", "decrease"), ("step", "5")]),
        new("shortcut-hardware-brightness-up", "按快捷键提高主屏硬件亮度5个百分点", "执行时对主屏中支持硬件控制的屏幕提高5个百分点；到范围上限限幅，未知不写。可改为全部支持屏幕，新亮度保留。", HotkeyCapabilities.Pressed, HardwareBrightnessAdjustCapabilities.Adjust, [], [("target", "primary"), ("operation", "increase"), ("step", "5")]),
        new("maximized-taskbar-auto-hide-restore", "指定程序窗口最大化期间自动隐藏任务栏，结束恢复", "选择程序完整路径；任一可见主窗口最大化且状态已知期间请求自动隐藏。全部还原、最小化、隐藏、未知或暂停后，仅原任务栏仍等于本次确认值才恢复本轮原选项。系统仍可在边缘展开，外部改变则让出；请审阅后启用。", WindowStateDetectionCapabilities.StateIs, TaskbarAutoHideRestoreCapabilities.Restore, [("state", "maximized")], [("state", "enable")]),
        new("taskbar-auto-hide-shortcut", "按快捷键开启任务栏自动隐藏", "填写快捷键；一次开启Windows任务栏自动隐藏，也可改为关闭。移到屏幕边缘仍可展开，设置之后保留，暂停不恢复；请审阅后启用。", HotkeyCapabilities.Pressed, TaskbarAutoHideCapabilities.Set, [], [("state", "enable")]),
        new("shortcut-hardware-brightness", "按快捷键将支持的主屏亮度设为30%", "选择快捷键；执行时把主屏中明确支持硬件控制的屏幕亮度设为30%，也可改为全部支持屏幕。通常需显示器启用DDC/CI；未知不写，部分完成会说明。设置保留，暂停不恢复；请审阅后启用。", HotkeyCapabilities.Pressed, HardwareBrightnessCapabilities.Set, [], [("target", "primary"), ("percent", "30")]),
        new("battery-dim-panel", "切换到电池时调暗屏幕（自动选择）", "切换到电池的新通知后设为30%；优先支持内屏，明确没有内屏接口时使用主屏硬件亮度，查询未知不切换。新亮度保留，接电不自动恢复；首次已经用电池不补执行。", SystemEventCapabilities.Event, BuiltInBrightnessCapabilities.Set, [("event", "battery")], [("percent", "30")]),
        new("unmuted-microphone-awake", "默认通信麦克风取消静音期间保持唤醒", "按Windows默认通信麦克风的软件静音状态保持系统唤醒；不代表会议正在录音。首次已取消静音即可持有请求，静音、设备未知或暂停后释放，不改变电源方案。", EndpointMuteCapabilities.State, KeepAwakeCapabilities.Hold, [("endpoint", "input"), ("state", "unmuted")], [("target", "system")]),
        new("windows-saver-dim-panel", "Windows报告电池节能开启后调暗屏幕（自动选择）", "Windows兼容电池节能API新报告开启后设为30%；优先支持内屏，明确没有内屏接口时使用主屏硬件亮度。查询未知不切换，不按电量或方案猜测、不改节能设置；首次及恢复不补执行，亮度不自动恢复。请审阅后启用。", PowerSaverStateCapabilities.State, BuiltInBrightnessCapabilities.Set, [("state", "on")], [("percent", "30")]),
        new("standard-saver-dim-panel", "使用标准节能方案后调暗屏幕（自动选择）", "Windows新报告标准节能方案后设为30%；优先支持内屏，明确没有内屏接口时使用主屏硬件亮度。查询未知不切换；自定义方案和电源模式滑块不匹配，首次及恢复不补执行，不改方案、不自动恢复亮度。请审阅后启用。", PowerSchemeStateCapabilities.State, BuiltInBrightnessCapabilities.Set, [("scheme", "saver")], [("percent", "30")]),
        new("battery-power-saver", "切换到电池时使用节能方案", "切换到电池的新通知后，一次切换为本机标准节能方案；若本机没有该方案会失败，不创建。之后保留节能方案，接电不自动恢复；请审阅后启用。", SystemEventCapabilities.Event, PowerSchemeCapabilities.Set, [("event", "battery")], [("scheme", "saver")]),
        new("shortcut-work-preparation", "按快捷键准备工作工具和资料", "填写快捷键和最多8行有序步骤：程序、目录、文档或网页。程序无参数、已运行跳过；全部预检后顺序请求，整组8秒，失败/取消/超时停止后续，已开始步骤可能已打开，不关闭/回滚/重试。", HotkeyCapabilities.Pressed, WorkPreparationCapabilities.Prepare, [], []),
        new("shortcut-workspace", "按快捷键打开一组工作目标", "选择快捷键并手工填写目录、文档和网页列表，最多8项；全部预检后按列表顺序请求打开。整批最多8秒，失败或取消停止后续项，不自动关闭已打开的窗口、不重试。", HotkeyCapabilities.Pressed, OpenTargetBatchCapabilities.Open, [], []),
        new("open-work-folder", "解锁后打开工作目录", "填写本机目录路径；每次新解锁请求打开一次，之后不会自动关闭目录窗口。", SystemEventCapabilities.Event, OpenTargetCapabilities.Open, [("event", "unlocked")], [("kind", "folder")]),
        new("wifi-work-folder", "连接工作 Wi-Fi 后打开目录", "手工填写本机保存的 Wi-Fi 配置文件名和工作目录；任一所选配置新连接后打开一次，同组选项间切换不重复打开。首次已连接不补发，暂停不关闭已打开的目录。", WifiProfileCapabilities.Connected, OpenTargetCapabilities.Open, [], [("kind", "folder")]),
        new("wifi-leave-lock", "离开指定 Wi-Fi 后请求锁屏", "手工填写本机保存的 Wi-Fi 配置文件名；全部确认断开后请求锁定当前会话，需要正常登录解锁。未知不当断开、首次已断开不执行，不检查网络可信性，不自动解锁。", WifiProfileCapabilities.Disconnected, SessionLockCapabilities.Lock, [], []),
        new("usb-dock-tools", "接入指定 USB 设备后启动工具", "从设备管理器复制提供 USB_DEVICE 接口的设备完整实例路径，并选择配套程序；新接入时启动一次，其他所选设备仍存在时不重复启动。首次已接入不补发，移除不自动关闭工具。", UsbDeviceCapabilities.Present, "program.start", [], []),
        new("window-prepare", "窗口出现后整理", "选择检测和目标程序，再选择布局；初始已有窗口不补执行，多窗口时只处理一个匹配窗口。", WindowPresenceCapabilities.Visible, WindowCapabilities.State, [], [("state", "maximize")]),
        new("focus-layout", "切到应用时摆到左半屏", "选择前台应用与目标程序；按触发时的显示器工作区整理一次，之后手动调整不会被持续干预。", ForegroundCapabilities.Active, WindowCapabilities.State, [], [("state", "left")]),
        new("display-layout", "显示变化后整理窗口", "选择目标程序和布局；显示模式通知后整理一次，按该窗口当前显示器处理，不识别特定屏幕组合、不恢复旧布局。", SystemEventCapabilities.Event, WindowCapabilities.State, [("event", "display-changed")], [("state", "left")]),
        new("shortcut-foreground-next-monitor", "快捷键把当前窗口移到下一屏", "填写快捷键，无需选择程序；执行开始捕获当前前台应用窗口，按显示器桌面坐标循环到下一屏并居中80%。只有一屏时不还原、不移动；切走、身份未知或显示器布局变化后不再写，可能已还原/移动不撤回。选主屏即使已在主屏也会居中80%。请审阅后启用。", HotkeyCapabilities.Pressed, ForegroundWindowMonitorCapabilities.Move, [], [("monitor", "next")]),
        new("shortcut-foreground-layout", "按快捷键整理当前前台窗口", "填写快捷键并选择布局，无需选择程序；执行开始捕获当前前台应用窗口，最大化/最小化先还原，再按当前工作区整理并读回。切走、身份未知或窗口消失后不再写，已还原或移动不撤回；不处理Axiom自身、桌面、任务栏或确认对话框。", HotkeyCapabilities.Pressed, ForegroundWindowLayoutCapabilities.Layout, [], [("layout", "left")]),
        new("shortcut-next-monitor", "按快捷键把窗口移到下一个显示器", "选择快捷键和目标程序完整路径；按执行时显示器桌面位置循环移屏并居中80%，最大化/最小化窗口先还原。只有一屏时跳过，不抢回后续手动调整。", HotkeyCapabilities.Pressed, WindowMonitorCapabilities.Move, [], [("monitor", "next")]),
        new("display-primary-window", "显示变化后把窗口放回主显示器", "选择目标程序完整路径；显示模式通知后移到执行时主显示器并居中80%，最大化/最小化窗口先还原。不匹配特定设备、不恢复旧坐标，也不持续固定。", SystemEventCapabilities.Event, WindowMonitorCapabilities.Move, [("event", "display-changed")], [("monitor", "primary")]),
        new("workday-system-sound", "仅工作时段定期播放提示音", "默认工作日9–18点，每30分钟请求一次默认系统提示音，不弹窗口、不改音量。静音或系统声音设置可能导致无声，不补发错过机会，不适合紧急提醒；请审阅后启用。", WindowedIntervalCapabilities.Due, SystemSoundCapabilities.Sound, [], []),
        new("workday-reminder", "仅工作时段定期提醒", "默认工作日9–18点，每30分钟请求安静提醒；填写内容后审阅启用。结束时刻不触发，无节假日判断，不补发错过机会。", WindowedIntervalCapabilities.Due, NotificationCapabilities.Show, [], []),
        new("desktop-reminder", "定期显示桌面提醒", "填写提醒内容；默认每30分钟请求一次安静提醒。Windows勿扰可能隐藏通知，不适合紧急提醒。", IntervalCapabilities.Elapsed, NotificationCapabilities.Show, [], []),
        new("pdf-arrival", "文件夹出现PDF后打开工具", "填写本机文件夹和配套程序；新建或重命名出现PDF后一秒合并触发，不读取内容、不证明下载完成、不传递文件名。可给动作增加延迟。", FileArrivalCapabilities.Arrived, "program.start", [("kind", "pdf")], []),
        new("playback-toggle-shortcut", "按同一快捷键切换播放静音", "选择快捷键；读取默认播放设备的静音状态后反向设置一次并读回。读不到不修改，不改音量、不停止播放、不自动恢复；取消静音可能立即发声，请审阅后启用。", HotkeyCapabilities.Pressed, AudioPlaybackToggleCapabilities.Toggle, [], []),
        new("microphone-toggle-shortcut", "按同一快捷键切换麦克风静音", "填写快捷键；读取默认通信麦克风当前软件静音状态后反向设置一次并读回，无法读取时不修改。取消静音可能立即恢复收音；不是硬件隐私开关、不停止录音程序、不修改输入音量，暂停/结束不恢复。请审阅后启用。", HotkeyCapabilities.Pressed, MicrophoneToggleCapabilities.Toggle, [], []),
        new("volume-up-shortcut", "按快捷键提高播放音量5个百分点", "选择快捷键；按执行时默认播放设备音量提高5个百分点，最多100%，保持静音状态。一次调整并读回，不自动恢复，独立路由的应用可能不受影响。", HotkeyCapabilities.Pressed, AudioPlaybackAdjustCapabilities.Adjust, [], [("operation", "increase"), ("step", "5")]),
        new("volume-down-shortcut", "按快捷键降低播放音量5个百分点", "选择快捷键；按执行时默认播放设备音量降低5个百分点，最低0%，保持静音状态。一次调整并读回，不自动恢复，独立路由的应用可能不受影响。", HotkeyCapabilities.Pressed, AudioPlaybackAdjustCapabilities.Adjust, [], [("operation", "decrease"), ("step", "5")]),
        new("microphone-shortcut", "按快捷键静音默认通信麦克风", "选择快捷键；只静音Windows默认通信麦克风一次，不自动取消静音。应用选择的其他输入设备可能仍收音，请核对应用。", HotkeyCapabilities.Pressed, MicrophoneCapabilities.Set, [], [("operation", "mute")]),
        new("lock-display-off", "锁屏后请求关闭屏幕", "锁屏新通知后向Windows请求关闭屏幕一次，电脑与后台工作继续；不睡眠、不改电源方案。输入可能唤醒屏幕，驱动与保持屏幕唤醒请求可能影响效果，接受请求不代表物理屏幕已关闭。", SystemEventCapabilities.Event, DisplayOffCapabilities.Request, [("event", "locked")], []),
        new("lock-mute", "锁屏后静音默认播放设备", "锁屏时静音一次，不停止播放、不改麦克风，解锁不自动恢复。之后可手动取消静音；请审阅后启用。", SystemEventCapabilities.Event, AudioPlaybackCapabilities.Set, [("event", "locked")], [("operation", "mute")]),
        new("focus-quiet-apps", "切到工作应用时降低播放应用音量", "选择前台工作程序及实际发声程序完整路径；只把默认播放设备上的活动会话音量设为30%，不取消静音、不自动恢复。目标未播放或无会话会说明失败。", ForegroundCapabilities.Active, ApplicationAudioCapabilities.Set, [], [("operation", "volume"), ("percent", "30")]),
        new("keep-transfer-awake", "传输或会议程序运行期间保持系统唤醒", "选择需要持续运行的程序；任意一个运行时持有Axiom自己的电源请求，全部退出或状态未知后释放。默认允许屏幕关闭，不改变电源计划、不阻止手动睡眠。", "process.running", KeepAwakeCapabilities.Hold, [], [("target", "system")]),
        new("once-meeting", "指定日期打开会议页面", "填写日期、本地时刻和http/https会议网址；只等待未来时刻，不补发已错过的会议，不自动加入或操作会议。", OnceTimeCapabilities.Due, OpenTargetCapabilities.Open, [], [("kind", "url")]),
        new("daily-document", "工作日定时打开计划文档", "手工填写本机文档完整路径；默认工作日9点交给默认关联应用打开，不读取内容、不保证加载完成，暂停不关闭已打开文档。", DailyTimeCapabilities.Due, OpenTargetCapabilities.Open, [], [("kind", "document")]),
        new("window-once-tool", "每天时段内启动一次工具", "选择工具；默认每天6–18点，8点开机也能执行。本规则该时段成功后不再重复，重启仍保留记录；结束后不补执行，也不关闭已启动程序。请审阅后启用。", WindowOnceCapabilities.Due, "program.start", [], []),
        new("window-once-reminder", "每天时段内提醒一次", "填写提醒内容；默认每天6–18点，晚开机但仍在时段内也能提醒。本规则该时段成功后不重复，不唤醒电脑；Windows勿扰可能隐藏已受理通知。请审阅后启用。", WindowOnceCapabilities.Due, NotificationCapabilities.Show, [], []),
        new("daily-work-folder", "工作日定时打开目录", "填写工作目录，选择本地时刻和星期；默认周一至周五9点。只等未来时刻，错过不补发，也不唤醒电脑。", DailyTimeCapabilities.Due, OpenTargetCapabilities.Open, [], [("kind", "folder")]),
        new("regular-reminder", "定期打开提醒页", "填写提醒页的 http/https 网址和间隔；首次也需等待完整间隔，每次到期会请求打开一次，请避免过短间隔造成重复标签页。", IntervalCapabilities.Elapsed, OpenTargetCapabilities.Open, [], [("kind", "url")]),
        new("pin-shortcut", "按快捷键置顶工具窗口", "选择全局快捷键与目标程序；一次置顶。可把窗口状态改为取消置顶建立另一条规则。", HotkeyCapabilities.Pressed, WindowCapabilities.State, [], [("state", "topmost")])
    ];
    public static AutomationTemplate[] Build(AutomationSnapshot catalog)
    {
        var result = new List<AutomationTemplate>();
        foreach (var recipe in Recipes)
        {
            if (!catalog.Combinations.Contains(new(recipe.Detection, recipe.Action))) continue;
            var detection = Invocation(recipe.Detection, TaskCapabilityKind.Detection, recipe.DetectionValues);
            var action = Invocation(recipe.Action, TaskCapabilityKind.Action, recipe.ActionValues);
            if (detection is null || action is null) continue;
            result.Add(new(recipe.Id, recipe.Name, recipe.Description, new(Guid.NewGuid().ToString("D"), 0, recipe.Name, true, detection, action)));
        }
        return [.. result];

        TaskInvocation? Invocation(string id, TaskCapabilityKind kind, (string Field, string Value)[] overrides)
        {
            var capability = catalog.Capabilities.SingleOrDefault(c => c.Id == id && c.Kind == kind);
            if (capability is null) return null;
            var values = capability.Fields.Where(f => f.DefaultValue is not null || f.Kind == TaskFieldKind.Boolean)
                .ToImmutableDictionary(f => f.Id, f => f.DefaultValue ?? "false").ToBuilder();
            foreach (var (fieldId, value) in overrides)
            {
                var field = capability.Fields.SingleOrDefault(f => f.Id == fieldId);
                if (field is null || value.Length > field.MaximumLength || field.Kind == TaskFieldKind.Choice && !field.Choices.Any(c => c.Value == value)) return null;
                values[fieldId] = value;
            }
            return new(id, capability.SchemaVersion, values.ToImmutable());
        }
    }
}
