using System.Threading.Channels;

namespace Axiom.Providers.Windows;

// A shared capture-free subscription. Outer gates and ordinary HDR conditions
// hold separate leases, so suspending inner observations cannot hide HDR recovery.
public sealed class WindowsPrimaryHdrSource : IDisposable
{
    private static readonly object Gate = new();
    private static readonly HashSet<WindowsPrimaryHdrSource> Clients = [];
    private static PrimaryColorEvents? color;
    private static WindowsSessionEvents? display;
    private static CancellationTokenSource? stop;
    private static Task? pump;
    private static bool? current;
    private readonly Channel<bool?> values = Channel.CreateBounded<bool?>(new BoundedChannelOptions(16) { SingleReader = true });
    public ChannelReader<bool?> Values => values.Reader;
    public WindowsPrimaryHdrSource()
    {
        lock (Gate)
        {
            if (Clients.Count == 0)
            {
                current = null;
                try
                {
                    // Constructor callback runs on its dispatcher: it must not take Gate.
                    color = new(() => { }, _ => Publish(null), value => Publish(value));
                    display = new(displayOnly: true); stop = new();
                    pump = PumpAsync(display, stop.Token);
                }
                catch { color?.Dispose(); color = null; display?.Dispose(); display = null; current = null; }
            }
            Clients.Add(this);
            lock (ClientGate) { Subscribers = [.. Clients]; values.Writer.TryWrite(current); }
        }
    }
    private static void Publish(bool? value)
    {
        // Native callbacks are serialized by the dispatcher; client access uses
        // a separate lock so synchronous constructor/disposal cannot deadlock it.
        lock (ClientGate)
        {
            current = value;
            foreach (var client in Subscribers)
                if (!client.values.Writer.TryWrite(value)) client.values.Writer.TryComplete(new IOException("HDR通知拥塞，状态未知。"));
        }
    }
    private static readonly object ClientGate = new();
    private static WindowsPrimaryHdrSource[] Subscribers = [];
    private static async Task PumpAsync(WindowsSessionEvents source, CancellationToken token)
    {
        try { await foreach (var _ in source.Events.ReadAllAsync(token)) color?.Refresh(); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch { Publish(null); }
    }
    public void Dispose()
    {
        lock (Gate)
        {
            if (!Clients.Remove(this)) return;
            lock (ClientGate) Subscribers = [.. Clients];
            values.Writer.TryComplete();
            if (Clients.Count != 0) return;
            stop?.Cancel(); display?.Dispose(); color?.Dispose();
            pump?.GetAwaiter().GetResult(); stop?.Dispose();
            stop = null; display = null; color = null; pump = null;
        }
    }
}
