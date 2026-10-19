using Axiom.Domain;

namespace Axiom.Contracts;

public static class SystemEventCapabilities
{
    // Host/Agent transport only; deliberately absent from the user capability catalog.
    public const string ClockEvents = "system.clock-events";
    public const string Event = "system.event";
    public static readonly string[] Kinds = ["locked", "unlocked", "resumed", "ac", "battery", "network-up", "network-down", "battery-low20", "battery-low10", "display-changed", "usb-arrived", "usb-removed"];
    public static TaskCapability Descriptor { get; } = new(Event, 1, TaskCapabilityKind.Detection, "系统", "系统环境变化",
        "锁屏、解锁、睡眠恢复、供电、低电量、显示模式、USB接入/移除或网络可用性变化后执行。",
        [new("event", "何时", TaskFieldKind.Choice, true, "仅响应新通知；保存或重连时不补发之前的事件。", "unlocked", 16,
            [new("locked", "锁屏时"), new("unlocked", "解锁时"), new("resumed", "从睡眠恢复时"), new("ac", "接通交流电时"), new("battery", "切换到电池时"), new("network-up", "网络可用时"), new("network-down", "网络不可用时"), new("battery-low20", "电量从高于 20% 降至 20% 或以下"), new("battery-low10", "电量从高于 10% 降至 10% 或以下"), new("display-changed", "显示分辨率或色深变化时"), new("usb-arrived", "任意USB设备接入通知"), new("usb-removed", "任意USB设备移除通知")])],
        "使用 Windows 会话和电源通知，无周期检查。条件为一次事件；暂停、修改或删除取消尚未执行的延迟。",
        "睡眠恢复不等于用户已解锁。锁屏期间不适合键盘输入；电源事件以系统实际通知为准，未知供电不猜测。网络可用指本机存在可用网络接口，不代表互联网连通。低电量只响应已知百分比向下穿越阈值；初始或未知状态不补发，重新升至阈值以上才再次触发。不区分电池数量，也不要求正在用电池供电。显示变化来自Windows显示模式通知，不识别具体显示器，不保证每次插拔均产生此通知。USB为任意设备接口类别通知，复合设备可能多次，不能筛选具体设备；系统重枚举也可能通知，不证明人为插拔。");
    public static string Validate(TaskInvocation invocation)
    {
        if (invocation.CapabilityId != Event || invocation.SchemaVersion != 1 || invocation.Parameters is null || invocation.Parameters.Count != 1 ||
            !invocation.Parameters.TryGetValue("event", out var kind) || !Kinds.Contains(kind, StringComparer.Ordinal)) throw new ArgumentException("系统事件配置无效。");
        return kind;
    }
}
