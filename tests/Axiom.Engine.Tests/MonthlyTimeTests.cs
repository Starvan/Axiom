using System.Collections.Immutable;
using System.Threading.Channels;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.SessionAgent;

namespace Axiom.Engine.Tests;

public sealed class MonthlyTimeTests
{
    private static TaskInvocation Invocation(string dates = "1,15,last") => new(MonthlyTimeCapabilities.Due, 1,
        MonthlyTimeCapabilities.Descriptor.Fields.ToImmutableDictionary(field => field.Id, field => field.Id == "dates" ? dates : field.DefaultValue!));
    [Fact]
    public void MultipleDatesAndMonthEndAreCanonicalAndStrictlyValidated()
    {
        var invocation = Invocation("15,1,15,last"); var spec = MonthlyTimeCapabilities.Validate(invocation);
        Assert.Equal("09:00/1,15,last", spec.Resource);
        Assert.Equal(spec, MonthlyTimeCapabilities.ParseResource(spec.Resource));
        Assert.Contains("最后一天", MonthlyTimeCapabilities.Label(spec.Resource));
        var condition = new MonthlyTimeTaskDetection().Compile(invocation, ResourceScope.Session("owner", 1));
        Assert.Equal(ConditionKind.Event, condition.Kind); Assert.Equal(spec.Resource, Assert.Single(condition.Dependencies).ResourceId);
        foreach (var dates in new[] { "", "0", "32", "1.5", "1,,2", "1;2", "Last", "last,", "-1" })
            Assert.Throws<ArgumentException>(() => MonthlyTimeCapabilities.Validate(Invocation(dates)));
        foreach (var (field, value) in new[] { ("hour", "24"), ("hour", "1.5"), ("minute", "60"), ("extra", "1") })
            Assert.Throws<ArgumentException>(() => MonthlyTimeCapabilities.Validate(invocation with { Parameters = invocation.Parameters.SetItem(field, value) }));
        foreach (var resource in new[] { "9:00/1", "09:00/01", "09:00/15,1", "09:00/1,1", "24:00/1", "09:00/last,1" })
            Assert.Throws<ArgumentException>(() => MonthlyTimeCapabilities.ParseResource(resource));
    }
    [Fact]
    public void FutureMonthDaysSkipNonexistentDatesAndMonthEndTracksLeapYearsWithoutDuplicates()
    {
        var day31 = MonthlyTimeCapabilities.Validate(Invocation("31"));
        Assert.Equal(new DateTimeOffset(2026, 3, 31, 9, 0, 0, TimeSpan.Zero), day31.Next(new(2026, 1, 31, 9, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc));
        var last = MonthlyTimeCapabilities.Validate(Invocation("28,29,last"));
        Assert.Equal(new DateTimeOffset(2028, 2, 29, 9, 0, 0, TimeSpan.Zero), last.Next(new(2028, 2, 28, 9, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc));
        Assert.Equal(new DateTimeOffset(2028, 3, 28, 9, 0, 0, TimeSpan.Zero), last.Next(new(2028, 2, 29, 9, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc));
        var schedule = new MonthlySchedule([MonthlyTimeCapabilities.Validate(Invocation("31,last"))], new(2026, 1, 31, 8, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc);
        Assert.Single(schedule.Due(new(2026, 1, 31, 9, 0, 0, TimeSpan.Zero)));
        Assert.Empty(schedule.Due(new(2026, 1, 31, 9, 0, 30, TimeSpan.Zero)));
    }
    [Fact]
    public void DstMissingTimesSkipAndRepeatedTimesUseFirstOccurrenceOnly()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("synthetic-monthly-DST", TimeSpan.FromHours(-5), "synthetic", "standard", "daylight",
            [TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new(2000, 1, 1), new(2099, 12, 31), TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday))]);
        Assert.Equal(new DateTimeOffset(2026, 4, 8, 6, 30, 0, TimeSpan.Zero), new MonthlyTimeSpec(2, 30, 1u << 8).Next(new(2026, 3, 8, 5, 0, 0, TimeSpan.Zero), zone));
        var repeated = new MonthlyTimeSpec(1, 30, 1u << 1);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero), repeated.Next(new(2026, 11, 1, 4, 0, 0, TimeSpan.Zero), zone));
        Assert.Equal(new DateTimeOffset(2026, 12, 1, 6, 30, 0, TimeSpan.Zero), repeated.Next(new(2026, 11, 1, 5, 31, 0, TimeSpan.Zero), zone));
    }
    [Fact(Timeout = 10000)]
    public async Task ClockChangesSkipMissedRunsRollbackNeverReplaysAndCancellationReleasesDeadline()
    {
        var time = new ManualTimeProvider(); time.JumpUtc(new DateTimeOffset(2026, 10, 1, 8, 59, 0, TimeSpan.Zero) - time.GetUtcNow());
        var deadlines = Channel.CreateBounded<TimeSpan>(8); time.TimerCreated = due => Assert.True(deadlines.Writer.TryWrite(due));
        var changes = Channel.CreateBounded<byte>(1); var events = Channel.CreateBounded<decimal>(4);
        using var lifetime = new CancellationTokenSource(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = MonthlyEventSource.RunAsync([MonthlyTimeCapabilities.Validate(Invocation("1,15,last"))], changes.Reader,
            (_, count) => Assert.True(events.Writer.TryWrite(count)), lifetime.Token, time, () => TimeZoneInfo.Utc);
        Assert.Equal(TimeSpan.FromMinutes(1), await deadlines.Reader.ReadAsync(timeout.Token));
        time.JumpUtc(TimeSpan.FromHours(2)); Assert.True(changes.Writer.TryWrite(1));
        var next = TimeSpan.FromDays(14) - TimeSpan.FromMinutes(119);
        Assert.Equal(next, await deadlines.Reader.ReadAsync(timeout.Token)); Assert.False(events.Reader.TryRead(out _));
        time.Advance(next); Assert.Equal(1m, await events.Reader.ReadAsync(timeout.Token));
        Assert.Equal(TimeSpan.FromDays(16), await deadlines.Reader.ReadAsync(timeout.Token));
        time.JumpUtc(TimeSpan.FromDays(-14)); Assert.True(changes.Writer.TryWrite(1));
        Assert.Equal(TimeSpan.FromDays(30), await deadlines.Reader.ReadAsync(timeout.Token)); Assert.False(events.Reader.TryRead(out _));
        lifetime.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run); Assert.All(time.Timers, timer => Assert.Null(timer.At));
    }
    [Fact(Timeout = 10000)]
    public async Task LongCalendarWaitSplitsAtNativeTimerLimitWithoutEarlyEventAndLostSubscriptionStops()
    {
        var time = new ManualTimeProvider(); time.JumpUtc(new DateTimeOffset(2026, 1, 31, 9, 0, 0, TimeSpan.Zero) - time.GetUtcNow());
        var deadlines = Channel.CreateBounded<TimeSpan>(8); time.TimerCreated = due => Assert.True(deadlines.Writer.TryWrite(due));
        var changes = Channel.CreateBounded<byte>(1); var events = Channel.CreateBounded<decimal>(4);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = MonthlyEventSource.RunAsync([MonthlyTimeCapabilities.Validate(Invocation("31"))], changes.Reader,
            (_, count) => Assert.True(events.Writer.TryWrite(count)), default, time, () => TimeZoneInfo.Utc);
        var maximum = TimeSpan.FromMilliseconds(uint.MaxValue - 1);
        Assert.Equal(maximum, await deadlines.Reader.ReadAsync(timeout.Token));
        time.Advance(maximum);
        Assert.Equal(TimeSpan.FromDays(59) - maximum, await deadlines.Reader.ReadAsync(timeout.Token)); Assert.False(events.Reader.TryRead(out _));
        time.Advance(TimeSpan.FromDays(59) - maximum); Assert.Equal(1m, await events.Reader.ReadAsync(timeout.Token));
        await deadlines.Reader.ReadAsync(timeout.Token);
        changes.Writer.Complete(); await Assert.ThrowsAsync<ChannelClosedException>(() => run);
        Assert.All(time.Timers, timer => Assert.Null(timer.At));
    }
    [Fact]
    public void DelayedWakeSkipsOldOpportunityAndDateRollbackKeepsLastFiredFence()
    {
        var spec = MonthlyTimeCapabilities.Validate(Invocation("1,15,last"));
        var start = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
        var schedule = new MonthlySchedule([spec], start, TimeZoneInfo.Utc);
        Assert.Empty(schedule.Due(start.AddHours(1).AddMinutes(2)));
        Assert.Equal(TimeSpan.FromDays(14) - TimeSpan.FromMinutes(2), schedule.Remaining(start.AddHours(1).AddMinutes(2)));
        Assert.Single(schedule.Due(new(2026, 10, 15, 9, 0, 30, TimeSpan.Zero)));
        schedule.Reset(new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc);
        Assert.Equal(new DateTimeOffset(2026, 10, 31, 9, 0, 0, TimeSpan.Zero) - new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero),
            schedule.Remaining(new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero)));
    }
}
