using Axiom.Domain;
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
    [Fact]
    public void ConsecutiveActivityGroupsPreserveDistinctResultsSubjectsAndBoundaries()
    {
        var noon = new DateTimeOffset(DateTime.Today.AddHours(12));
        var normal = new ActivityEvent(noon, "自动化", "已完成", "程序 A", ActivityLevel.Success, "任务 A");
        ActivityRecord[] records = [new(9, normal), new(8, normal with { AtUtc = noon.AddSeconds(-1) }),
            new(7, normal with { Level = ActivityLevel.Warning, Detail = "设备未回应" }),
            new(6, normal with { Subject = "任务 B" }), new(5, normal),
            new(4, normal with { AtUtc = noon.AddDays(-1) })];
        var groups = WorkbenchPresentation.GroupActivity(records);
        Assert.Equal(new[] { 2, 1, 1, 1, 1 }, groups.Select(g => g.Count));
        Assert.Same(records[0], groups[0].Latest);
        Assert.Equal(noon.AddSeconds(-1), groups[0].FirstAtUtc);
        Assert.Equal("设备未回应", groups[1].Latest.Event.Detail);
        Assert.Equal("任务 B", groups[2].Latest.Event.Subject);
        Assert.Equal(6, groups.Sum(g => g.Count));
        Assert.Empty(WorkbenchPresentation.GroupActivity([]));
    }
    [Fact]
    public void ActivityRangeUsesEventTimesWhenDurableWritesArriveOutOfOrder()
    {
        var time = new DateTimeOffset(DateTime.Today.AddHours(12));
        var first = new ActivityEvent(time, "自动化", "检测恢复", "已连接");
        var group = Assert.Single(WorkbenchPresentation.GroupActivity([
            new ActivityRecord(3, first), new ActivityRecord(2, first with { AtUtc = time.AddSeconds(2) }),
            new ActivityRecord(1, first with { AtUtc = time.AddSeconds(-2) })]));
        Assert.Equal(3, group.Count);
        Assert.Equal(time.AddSeconds(-2), group.FirstAtUtc);
        Assert.Equal(time.AddSeconds(2), group.LastAtUtc);
        Assert.Equal(3, group.Latest.Id);
    }
}
