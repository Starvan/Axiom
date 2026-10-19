using System.Globalization;
using Axiom.Domain;

namespace Axiom.Contracts;

public readonly record struct BrightnessFadeSettings(bool Enabled = false, int DurationSeconds = 3, int WritesPerSecond = 10)
{
    public static TaskField[] Fields { get; } =
    [new("fadeEnabled", "渐变亮度", TaskFieldKind.Boolean, false, "逐步改变亮度；关闭时直接设置。恢复动作在正常结束时也逐步恢复。硬件渐变按最多1秒的连续段核对，段内手动调节可能被覆盖。", "false", 5, [], Group: "渐变设置"),
     new("fadeDurationSeconds", "渐变时长（秒）", TaskFieldKind.Number, false, "1–15整数，默认3秒；这是大约过渡时间，实际速度受屏幕响应与亮度粒度影响。", "3", 2, [], Minimum: 1, Maximum: 15, Group: "渐变设置"),
     new("fadeWritesPerSecond", "渐变平滑度（次/秒）", TaskFieldKind.Number, false, "1–60整数，默认10，推荐18；这是请求频率上限，实际速度受设备与原生调用耗时限制。硬件末笔至少间隔100毫秒并严格核对。", "10", 2, [], Minimum: 1, Maximum: 60, Group: "渐变设置")];

    public TimeSpan OperationTimeout => TimeSpan.FromSeconds(10 + (Enabled ? DurationSeconds : 0));
    public static BrightnessFadeSettings Parse(IReadOnlyDictionary<string, string> parameters)
    {
        var enabled = false; var duration = 3;
        if (parameters.TryGetValue("fadeEnabled", out var flag))
        {
            if (flag is not ("true" or "false")) throw new ArgumentException("请选择是否渐变亮度。");
            enabled = flag == "true";
        }
        if (parameters.TryGetValue("fadeDurationSeconds", out var seconds) &&
            (!int.TryParse(seconds, NumberStyles.None, CultureInfo.InvariantCulture, out duration) || duration is < 1 or > 15))
            throw new ArgumentException("渐变时长须为1–15整数秒。");
        var writes = 10;
        if (parameters.TryGetValue("fadeWritesPerSecond", out var frequency) &&
            (!int.TryParse(frequency, NumberStyles.None, CultureInfo.InvariantCulture, out writes) || writes is < 1 or > 60))
            throw new ArgumentException("渐变平滑度须为1–60整数次/秒。");
        return new(enabled, duration, writes);
    }
    internal static bool ValidParameters(IReadOnlyDictionary<string, string> parameters, params string[] required)
    {
        Parse(parameters);
        return required.All(parameters.ContainsKey) && parameters.Keys.All(k => required.Contains(k) || k is "fadeEnabled" or "fadeDurationSeconds" or "fadeWritesPerSecond");
    }
}
