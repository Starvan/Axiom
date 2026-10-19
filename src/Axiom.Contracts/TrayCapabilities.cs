using System.Globalization;
using Axiom.Domain;

namespace Axiom.Contracts;

public static class TrayCapabilities
{
    public const string Hide = "tray.hide";
    public const int MaximumWaitSeconds = 120;
    public static TaskCapability Descriptor { get; } = new(Hide, 1, TaskCapabilityKind.Action, "托盘", "隐藏托盘图标",
        "移除目标程序的通知区域图标，包括折叠区；保留后台进程，不关闭窗口。",
        [new("executable", "目标程序", TaskFieldKind.TraySelection, true, "直接从当前托盘多选；也可批量添加完整 .exe 路径，最多 32 个。", null, ExecutablePaths.MaximumTextLength, []),
         new("waitSeconds", "等待托盘图标出现（秒）", TaskFieldKind.Number, true, "已有图标立即隐藏；否则等待系统通知，超时会说明未找到。", "30", 8, [], 1, MaximumWaitSeconds),
         new("delaySeconds", "延迟执行（秒）", TaskFieldKind.Number, true, "0 表示条件成立后开始等待；暂停、修改或删除取消未执行的等待。", "0", 8, [], 0, 86400)],
        "每次触发隐藏目标当前可识别的全部托盘图标。软件或资源管理器重新创建后可能再出现，不持续压制。",
        "隐藏后托盘菜单和气泡通知可能不可用；暂停不会恢复图标，可重新打开或重启目标软件。Windows 更新可能影响图标识别，失败会说明原因。");
    public static string Value(TaskInvocation config, string field) => config.Parameters.GetValueOrDefault(field)
        ?? Descriptor.Fields.Single(f => f.Id == field).DefaultValue ?? "";
    public static decimal Number(TaskInvocation config, string field) => decimal.Parse(Value(config, field), CultureInfo.InvariantCulture);
    public static string[] ValidateTargets(TaskInvocation config)
    {
        if (config.CapabilityId != Hide || config.SchemaVersion != 1 || config.Parameters is null || config.Parameters.Keys.Any(k => !Descriptor.Fields.Any(f => f.Id == k)))
            throw new ArgumentException("托盘操作配置无效。");
        foreach (var field in Descriptor.Fields)
        {
            var value = Value(config, field.Id);
            if (field.Required && string.IsNullOrWhiteSpace(value) || value.Length > field.MaximumLength) throw new ArgumentException("请正确填写" + field.Label + "。");
            if (field.Kind == TaskFieldKind.Number && (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var n) || n < field.Minimum || n > field.Maximum))
                throw new ArgumentException(field.Label + "超出范围。");
        }
        return ExecutablePaths.Parse(Value(config, "executable"));
    }
}
