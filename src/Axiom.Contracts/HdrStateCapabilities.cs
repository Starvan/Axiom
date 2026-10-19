using Axiom.Domain;

namespace Axiom.Contracts;

public static class HdrStateCapabilities
{
    public const string State = "display.hdr-state";
    public static TaskCapability Descriptor { get; } = new(State, 1, TaskCapabilityKind.Detection, "显示", "主屏HDR状态",
        "主显示器HDR开启或关闭时满足条件；不采集画面。",
        [new("state", "HDR状态", TaskFieldKind.Choice, true, "读取Windows主显示器HDR状态。", "on", 3, [new("on", "开启"), new("off", "关闭")])],
        "初次读取，随后响应Windows颜色、布局和恢复通知，无轮询。一次性动作只响应新的状态边沿。",
        "状态未知时不执行动作。", IsStateCondition: true);
    public static string Validate(TaskInvocation invocation)
    {
        if (invocation.CapabilityId != State || invocation.SchemaVersion != 1 || invocation.Parameters is null || invocation.Parameters.Count != 1 ||
            !invocation.Parameters.TryGetValue("state", out var state) || state is not ("on" or "off")) throw new ArgumentException("请选择主屏HDR开启或关闭。");
        return state;
    }
}
