namespace Axiom.WinUI;

internal static class WorkbenchPresentation
{
    public static string SaveError(string? error)
    {
        if (error == "DNS服务器须为IP地址，可附带1–65535端口。")
            return "普通 DNS 服务器或 Bootstrap DNS 格式无效。请填写单个IP地址，可带端口；DoH网址请填在“DoH 地址”栏。";
        if (error?.Contains("版本", StringComparison.Ordinal) == true || error?.Contains("别处更改", StringComparison.Ordinal) == true || error?.Contains("conflict", StringComparison.OrdinalIgnoreCase) == true)
            return "任务已被修改或类型版本已变化。输入已保留，请重新读取已保存版本后再编辑。";
        if (error?.Contains("32 条", StringComparison.Ordinal) == true) return "已达到当前任务数量上限。请先删除不需要的任务，当前输入已保留。";
        if (error?.StartsWith("请", StringComparison.Ordinal) == true && error.Length < 200) return error;
        return "更改未确认，输入已保留。请检查检测和动作参数，或重新读取已保存版本。";
    }
}
