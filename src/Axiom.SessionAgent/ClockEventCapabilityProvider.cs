using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Providers.Windows;

namespace Axiom.SessionAgent;

internal sealed class ClockEventCapabilityProvider(Func<Action, CancellationToken, Task>? observe = null) : ISessionCapabilityProvider
{
    public IReadOnlyCollection<string> Capabilities => [SystemEventCapabilities.ClockEvents];
    public async Task ObserveAsync(ProviderResource[] resources, Action<ProviderUpdate> publish, CancellationToken token)
    {
        if (resources.Length != 1 || resources[0] != new ProviderResource(SystemEventCapabilities.ClockEvents, "local"))
            throw new ArgumentException("内部时钟资源无效。");
        var first = true; decimal count = 0;
        void Changed()
        {
            publish(new(first, [new(resources[0], StateQuality.Known, "axiom.event-count", 1, Number: count++)])); first = false;
        }
        try
        {
            await (observe ?? ObserveWindowsAsync)(Changed, token);
            if (!token.IsCancellationRequested) throw new IOException("时钟通知已停止。");
        }
        catch (Exception error) when (!token.IsCancellationRequested && error is not (OutOfMemoryException or StackOverflowException))
        {
            publish(new(first, [new(resources[0], StateQuality.Unknown, "axiom.event-count", 1, Reason: "Windows时钟通知不可用。") ]));
            await Task.Delay(Timeout.Infinite, token);
        }
    }
    private static async Task ObserveWindowsAsync(Action changed, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("系统时钟通知需要Windows。");
        using var clock = new WindowsSessionEvents(clockOnly: true);
        changed(); // Subscribe before the initial report so time/resume changes cannot be lost.
        await foreach (var _ in clock.ClockChanges.ReadAllAsync(token)) changed();
    }
    public Task<(OutcomeStatus Status, string Reason)> InvokeAsync(TaskInvocation invocation, CancellationToken token) => throw new ArgumentException("内部时钟只用于通知。");
}
