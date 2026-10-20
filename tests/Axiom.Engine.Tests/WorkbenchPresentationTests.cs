using Axiom.WinUI;

namespace Axiom.Engine.Tests;

public sealed class WorkbenchPresentationTests
{
    [Fact]
    public void HostsDnsValidationExplainsWhichFieldToCorrectAndPreservesBackendAdvice()
    {
        var legacy = WorkbenchPresentation.SaveError("DNS服务器须为IP地址，可附带1–65535端口。");
        Assert.Contains("Bootstrap DNS", legacy); Assert.Contains("单个IP地址", legacy); Assert.DoesNotContain("重新读取", legacy);
        const string advice = "请将“Bootstrap DNS”填写为单个DNS服务器IP地址，可带端口；不要填写域名、DoH网址或多个地址。";
        Assert.Equal(advice, WorkbenchPresentation.SaveError(advice));
        Assert.DoesNotContain("SQLite", WorkbenchPresentation.SaveError("SQLite failure"));
    }
    [Fact]
    public void ErrorsHideDeveloperDetailsAndKeepRecoveryInstructions()
    {
        Assert.DoesNotContain("SID", Axiom.Infrastructure.GameHdrTaskPresentation.ResultReason("等待会话程序", "Client SID rejected"));
        Assert.Contains("重新打开", Axiom.Infrastructure.GameHdrTaskPresentation.ResultReason("检测异常", "queue overflow"));
        Assert.Contains("重新读取", WorkbenchPresentation.SaveError("规则已在别处更改，请重新读取。 "));
        Assert.DoesNotContain("SQLite", WorkbenchPresentation.SaveError("SQLite failure"));
        Assert.Equal("检测暂不可用", Axiom.Infrastructure.GameHdrTaskPresentation.ResultTitle("会话程序已断开"));
    }

    [Theory]
    [InlineData(true, true, true, "HDR 已开启并生效")]
    [InlineData(false, false, true, "HDR 已关闭")]
    [InlineData(true, false, true, "HDR 已开启，但尚未生效")]
    [InlineData(null, false, true, "HDR 状态未确认")]
    [InlineData(false, true, true, "HDR 状态未确认")]
    [InlineData(true, true, false, "当前状态未确认，请刷新后查看。")]
    [InlineData(false, false, false, "当前状态未确认，请刷新后查看。")]
    public void UnknownOrDisconnectedNeverReportsCurrentSuccess(bool? enabled, bool? active, bool reachable, string expected)
        => Assert.Equal(expected, Axiom.Infrastructure.GameHdrTaskPresentation.DisplayState(enabled, active, reachable));
}
