using System.Threading.Channels;
using Axiom.Contracts;

namespace Axiom.SessionAgent;

internal sealed class MonthlySchedule
{
    private sealed class Entry(MonthlyTimeSpec spec) { internal readonly MonthlyTimeSpec Spec = spec; internal DateTimeOffset Next; internal DateOnly? LastFired; internal decimal Count; }
    private readonly Entry[] entries;
    private TimeZoneInfo zone;
    internal MonthlySchedule(MonthlyTimeSpec[] specs, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (specs.Length is < 1 or > 128) throw new ArgumentException("定时资源数量无效。");
        entries = [.. specs.Distinct().Select(s => new Entry(s))]; this.zone = zone; Reset(now, zone);
    }
    internal void Reset(DateTimeOffset now, TimeZoneInfo currentZone)
    {
        zone = currentZone;
        foreach (var entry in entries) entry.Next = entry.Spec.Next(now, zone, entry.LastFired);
    }
    internal TimeSpan Remaining(DateTimeOffset now) => TimeSpan.FromTicks(Math.Max(0, entries.Min(e => e.Next.UtcTicks) - now.UtcTicks));
    internal (MonthlyTimeSpec Spec, decimal Count)[] Due(DateTimeOffset now)
    {
        var ready = new List<(MonthlyTimeSpec, decimal)>();
        foreach (var entry in entries)
        {
            if (entry.Next > now) continue;
            if (now - entry.Next <= TimeSpan.FromMinutes(1))
            {
                entry.LastFired = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(entry.Next, zone).DateTime);
                ready.Add((entry.Spec, ++entry.Count));
            }
            entry.Next = entry.Spec.Next(now, zone, entry.LastFired);
        }
        return [.. ready];
    }
}
public static class MonthlyEventSource
{
    public static async Task RunAsync(MonthlyTimeSpec[] specs, ChannelReader<byte> clockChanges, Action<MonthlyTimeSpec, decimal> publish,
        CancellationToken token, TimeProvider? timeProvider = null, Func<TimeZoneInfo>? readZone = null)
    {
        var time = timeProvider ?? TimeProvider.System;
        readZone ??= () => { TimeZoneInfo.ClearCachedData(); return TimeZoneInfo.Local; };
        var zone = readZone(); var schedule = new MonthlySchedule(specs, time.GetUtcNow(), zone);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            using var iteration = CancellationTokenSource.CreateLinkedTokenSource(token);
            // System.Threading.Timer supports at most uint.MaxValue - 1 milliseconds; split only longer calendar waits.
            var wait = schedule.Remaining(time.GetUtcNow());
            var due = Task.Delay(wait > TimeSpan.FromMilliseconds(uint.MaxValue - 1) ? TimeSpan.FromMilliseconds(uint.MaxValue - 1) : wait, time, iteration.Token);
            var changed = clockChanges.ReadAsync(iteration.Token).AsTask();
            try
            {
                await Task.WhenAny(due, changed);
                token.ThrowIfCancellationRequested();
                if (changed.IsCompleted)
                {
                    await changed; clockChanges.TryRead(out _); // coalesce the one-slot native mailbox without an unbounded drain
                    zone = readZone(); schedule.Reset(time.GetUtcNow(), zone);
                }
                else
                {
                    await due; var currentZone = readZone();
                    if (currentZone.Id != zone.Id || !currentZone.HasSameRules(zone)) { zone = currentZone; schedule.Reset(time.GetUtcNow(), zone); }
                    else foreach (var (spec, count) in schedule.Due(time.GetUtcNow())) { token.ThrowIfCancellationRequested(); publish(spec, count); }
                }
            }
            finally
            {
                iteration.Cancel();
                try { await Task.WhenAll(due, changed); } catch (OperationCanceledException) when (iteration.IsCancellationRequested) { }
            }
        }
    }
}
