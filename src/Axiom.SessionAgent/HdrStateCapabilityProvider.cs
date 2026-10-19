using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Providers.Windows;

namespace Axiom.SessionAgent;

internal sealed class HdrStateCapabilityProvider : ISessionCapabilityProvider
{
    public IReadOnlyCollection<string> Capabilities => [HdrStateCapabilities.State];
    public async Task ObserveAsync(ProviderResource[] resources, Action<ProviderUpdate> publish, CancellationToken token)
    {
        if (resources.Length != 1 || resources[0] != new ProviderResource(HdrStateCapabilities.State, "primary")) throw new ArgumentException("主屏HDR资源无效。");
        var first = true; bool? previous = null;
        using var source = new WindowsPrimaryHdrSource();
        try
        {
            await foreach (var value in source.Values.ReadAllAsync(token))
            {
                if (!first && previous == value) continue;
                publish(new(first || previous is null, [new(resources[0], value.HasValue ? StateQuality.Known : StateQuality.Unknown,
                    "axiom.bool", 1, Boolean: value, Reason: value is null ? "Windows主屏HDR状态未知。" : null)]));
                previous = value; first = false;
            }
        }
        catch (Exception error) when (!token.IsCancellationRequested && error is not (OutOfMemoryException or StackOverflowException))
        { publish(new(first, [new(resources[0], StateQuality.Unknown, "axiom.bool", 1, Reason: "主屏HDR通知不可用。") ])); await Task.Delay(Timeout.Infinite, token); }
    }
    public Task<(OutcomeStatus Status, string Reason)> InvokeAsync(TaskInvocation invocation, CancellationToken token) => throw new ArgumentException("HDR状态只能作为检测。");
}
