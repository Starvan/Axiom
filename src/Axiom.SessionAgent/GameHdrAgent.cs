using System.IO.Pipes;
using System.Security.Principal;
using System.Threading.Channels;
using Axiom.Contracts;
using Axiom.Providers.Windows;

namespace Axiom.SessionAgent;

internal sealed class GameHdrAgent : IAsyncDisposable
{
    private readonly string sid;
    private readonly CancellationTokenSource stopping = new();
    private readonly SemaphoreSlim watchGate = new(1, 1);
    private readonly Channel<(string Generation, ProcessPresenceChange Change)> events = Channel.CreateBounded<(string, ProcessPresenceChange)>(
        new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private int eventGap;
    private WindowsGameProcessObserver? observer;
    private Task? sender;
    private string[] watching = [];
    private string? watchGeneration;
    private readonly ShortcutAgent shortcuts;
    private Task? shortcutServer;
    private readonly CapabilityProviderServer providers;
    private Task? providerServer;
    internal GameHdrAgent(string sid)
    {
        this.sid = sid; shortcuts = new(sid);
        providers = new(sid, [new MediaPlaybackCapabilityProvider(() => new WindowsMediaPlayback()), new SessionDisplayCapabilityProvider(), new SessionPresenceCapabilityProvider(), new DisplayCountCapabilityProvider(), new NetworkCategoryCapabilityProvider(), new InternetConnectivityCapabilityProvider(), new ProgramCapabilityProvider(), new WindowCapabilityProvider(), new TrayCapabilityProvider(), new KeyboardCapabilityProvider(shortcuts), new SystemEventCapabilityProvider(), new IntervalCapabilityProvider(), new DailyTimeCapabilityProvider(), new WindowOnceCapabilityProvider(), new WindowedIntervalCapabilityProvider(), new FileArrivalCapabilityProvider(), new OnceTimeCapabilityProvider(), new AudioCapabilityProvider(), new AudioPlaybackCapabilityProvider(), new AudioPlaybackAdjustCapabilityProvider(), new AudioPlaybackToggleCapabilityProvider(), new PowerStateCapabilityProvider(), new PowerSchemeStateCapabilityProvider(), new PowerSaverStateCapabilityProvider(), new BuiltInBrightnessCapabilityProvider(sid), new BuiltInBrightnessRestoreCapabilityProvider(sid), new HardwareBrightnessRestoreCapabilityProvider(sid), new HardwareBrightnessCapabilityProvider(sid), new EndpointMuteCapabilityProvider(), new SessionLockCapabilityProvider(), new SessionLockStateCapabilityProvider(), new DisplayOffCapabilityProvider(sid), new WindowMonitorCapabilityProvider(), new ForegroundWindowLayoutCapabilityProvider(sid), new ForegroundWindowMonitorCapabilityProvider(sid), new UsbDeviceCapabilityProvider(), new PowerSchemeCapabilityProvider(), new TaskbarAutoHideCapabilityProvider(sid), new TaskbarAutoHideRestoreCapabilityProvider(sid), new WifiProfileCapabilityProvider(), new ApplicationAudioCapabilityProvider(), new ApplicationAudioActivityCapabilityProvider(paths => new WindowsApplicationAudioActivity(paths)), new ProgramPriorityCapabilityProvider(), new CurrentMediaCapabilityProvider(), new MicrophoneCapabilityProvider(), new MicrophoneToggleCapabilityProvider(), new NotificationCapabilityProvider(), new SystemSoundCapabilityProvider(), new OpenTargetCapabilityProvider(), new OpenTargetBatchCapabilityProvider(), new WorkPreparationCapabilityProvider(), new ForegroundCapabilityProvider(), new ForegroundScreenCoverCapabilityProvider(), new ScreenContentCapabilityProvider(() => new WindowsScreenContentEnvironment()), new WindowPresenceCapabilityProvider(), new WindowStateDetectionCapabilityProvider(), new KeepAwakeCapabilityProvider(),
            new RestorableSettingCapabilityProvider(ConditionRestoreCapabilities.Playback, (parameters, token) => WindowsAudioPlayback.CreateRestorableSessionAsync(false, parameters["operation"] == "volume", token), ConditionRestoreCapabilities.Validate, ConditionRestoreCapabilities.ResourceId),
            new RestorableSettingCapabilityProvider(ConditionRestoreCapabilities.Microphone, (_, token) => WindowsAudioPlayback.CreateRestorableSessionAsync(true, false, token), ConditionRestoreCapabilities.Validate, ConditionRestoreCapabilities.ResourceId),
            new RestorableSettingCapabilityProvider(ConditionRestoreCapabilities.PowerScheme, (_, token) => WindowsPowerScheme.CreateRestorableSessionAsync(token), ConditionRestoreCapabilities.Validate, ConditionRestoreCapabilities.ResourceId),
            new RestorableSettingCapabilityProvider(ConditionRestoreCapabilities.ProgramPriority, (parameters, token) => WindowsProgramPriority.CreateRestorableSessionAsync(ExecutablePaths.ValidatePath(parameters["executable"]), token), ConditionRestoreCapabilities.Validate, ConditionRestoreCapabilities.ResourceId),
            new RestorableSettingCapabilityProvider(ConditionRestoreCapabilities.WindowState, (parameters, token) => WindowsWindowActions.CreateRestorableSessionAsync(ExecutablePaths.ValidatePath(parameters["executable"]), token), ConditionRestoreCapabilities.Validate, ConditionRestoreCapabilities.ResourceId)], new BrightnessWheelAgent(shortcuts, sid));
    }

    public async Task RunAsync()
    {
        sender = SendEventsAsync();
        shortcutServer = shortcuts.RunAsync(stopping.Token);
        providerServer = providers.RunAsync(stopping.Token);
        _ = AnnounceAsync();
        while (!stopping.IsCancellationRequested)
        {
            var pipe = GameHdrLocalPipeFactory.Create(GameHdrProtocol.AgentPipe(sid), sid);
            try { await pipe.WaitForConnectionAsync(stopping.Token); }
            catch (OperationCanceledException) { pipe.Dispose(); break; }
            _ = HandleAsync(pipe);
        }
        await shortcutServer;
        await providerServer;
    }

    private async Task AnnounceAsync()
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", GameHdrProtocol.HostPipe(sid), PipeDirection.InOut,
                    PipeOptions.Asynchronous);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                await pipe.ConnectForUserAsync(2000, timeout.Token);
                var id = Guid.NewGuid().ToString("N");
                await GameHdrProtocol.WriteAsync(pipe, new(1, "hello", id, DateTimeOffset.UtcNow.AddSeconds(5)), timeout.Token);
                var reply = await GameHdrProtocol.ReadAsync(pipe, timeout.Token);
                if (reply?.Type == "received" && reply.RequestId == id)
                {
                    try { await shortcuts.RestoreFromHostAsync(stopping.Token); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or TimeoutException or OperationCanceledException)
                    { Console.Error.WriteLine("Shortcut restore: " + error.Message); }
                    await GameHdrProtocol.ReadAsync(pipe, stopping.Token);
                    continue;
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or TimeoutException or OperationCanceledException or InvalidOperationException)
            { /* Retry only while the host is unavailable; this does not scan processes or displays. */ }
            try { await Task.Delay(TimeSpan.FromSeconds(5), stopping.Token); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe)
    {
        using (pipe)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                var request = await GameHdrProtocol.ReadAsync(pipe, timeout.Token) ?? throw new EndOfStreamException();
                string? clientSid = null;
                pipe.RunAsClient(() => clientSid = WindowsIdentity.GetCurrent().User?.Value);
                if (clientSid != sid) throw new UnauthorizedAccessException("Client SID rejected.");
                GameHdrPipePeer.RequireHost(pipe);
                Validate(request);
                GameHdrMessage reply;
                if (request.Type == "displays")
                    reply = new(1, "displays", request.RequestId, Displays: ReadDisplays());
                else if (request.Type == "watch")
                {
                    await SetWatchAsync(request.ProcessNames!, request.RequestId, timeout.Token);
                    reply = new(1, "watching", request.RequestId);
                }
                else if (request.Type == "shutdown")
                    reply = new(1, "stopping", request.RequestId);
                else
                {
                    UsageObservationStream.MarkAction();
                    var result = WindowsHdrDisplayProbe.Apply(request.DisplayPath!, request.Enabled!.Value,
                        request.ExpectedEnabled!.Value);
                    reply = new(1, "applied", request.RequestId, Displays: ReadDisplays(),
                        Reason: result.Reason, Error: result.Confirmed ? null : result.Reason);
                }
                await GameHdrProtocol.WriteAsync(pipe, reply, timeout.Token);
                if (request.Type == "shutdown") stopping.Cancel();
            }
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
            {
                Console.Error.WriteLine("Game HDR agent: " + error.Message);
            }
        }
    }

    private static void Validate(GameHdrMessage request)
    {
        if (request.Version != 1 || request.Minor != 0 || request.Schema != 1 || request.Scope != "user-session" ||
            request.RequestId.Length is < 1 or > 64 || request.Rule is not null || request.Status is not null ||
            request.Error is not null || request.Displays is not null ||
            request.DeadlineUtc is not { } deadline || deadline <= DateTimeOffset.UtcNow || deadline > DateTimeOffset.UtcNow.AddSeconds(15))
            throw new InvalidDataException("Invalid agent request.");
        if (request.Type == "displays" && request.ProcessNames is null && request.DisplayPath is null) return;
        if (request.Type == "shutdown" && request.ProcessNames is null && request.DisplayPath is null) return;
        if (request.Type == "watch" && request.ProcessNames is { Length: <= 128 } names &&
            names.All(ValidName) && request.DisplayPath is null) return;
        if (request.Type == "apply" && request.DisplayPath is { Length: > 0 and <= 1024 } &&
            request.Enabled.HasValue && request.ExpectedEnabled.HasValue) return;
        throw new InvalidDataException("Invalid agent operation.");
    }

    private static bool ValidName(string name) => name.Length is > 4 and <= 132 &&
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Path.GetFileName(name), name, StringComparison.OrdinalIgnoreCase);

    private async Task SetWatchAsync(string[] names, string generation, CancellationToken token)
    {
        await watchGate.WaitAsync(token);
        try
        {
            names = names.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            if (generation == watchGeneration && names.SequenceEqual(watching, StringComparer.OrdinalIgnoreCase)) return;
            if (observer is not null) await observer.DisposeAsync();
            observer = null;
            watching = names;
            watchGeneration = generation;
            if (names.Length > 0)
            {
                var snapshotFailed = 0;
                observer = new WindowsGameProcessObserver(names, change =>
                {
                    if (change.Instance is null) Interlocked.Exchange(ref snapshotFailed, 1);
                    if (!events.Writer.TryWrite((generation, change)))
                        Interlocked.Exchange(ref eventGap, 1);
                });
                try { observer.Start(); }
                catch
                {
                    await observer.DisposeAsync(); observer = null; watching = []; watchGeneration = null;
                    throw;
                }
                events.Writer.TryWrite((generation, new(null, false,
                    snapshotFailed == 0 ? "snapshot-complete" : "snapshot-inspection-failed")));
            }
        }
        finally { watchGate.Release(); }
    }

    private async Task SendEventsAsync()
    {
        await foreach (var item in events.Reader.ReadAllAsync(stopping.Token))
        {
            try
            {
                var change = item.Change;
                using var pipe = new NamedPipeClientStream(".", GameHdrProtocol.HostPipe(sid), PipeDirection.InOut,
                    PipeOptions.Asynchronous);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                await pipe.ConnectForUserAsync(2000, timeout.Token);
                var instance = change.Instance;
                var request = new GameHdrMessage(1, "event", Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.AddSeconds(5),
                    Displays: ReadDisplays(), ProcessName: instance is null ? null : Path.GetFileName(instance.ExecutablePath),
                    ProcessPath: instance?.ExecutablePath, ProcessId: instance?.ProcessId,
                    StartedAtUtc: instance?.StartedAtUtc, Running: change.Running, Reason: change.Reason,
                    Generation: item.Generation);
                await GameHdrProtocol.WriteAsync(pipe, request, timeout.Token);
                await GameHdrProtocol.ReadAsync(pipe, timeout.Token);
            }
            catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException or InvalidOperationException)
            { Console.Error.WriteLine("Game HDR event delivery failed: " + error.Message); }
            if (Interlocked.Exchange(ref eventGap, 0) != 0)
                if (!events.Writer.TryWrite((watchGeneration ?? item.Generation,
                    new(null, false, "Process notification queue overflow; state unknown."))))
                    Interlocked.Exchange(ref eventGap, 1);
        }
    }

    private static GameHdrDisplayDto[] ReadDisplays() => WindowsHdrDisplayProbe.ReadActiveDisplays()
        .Select(x => new GameHdrDisplayDto(x.FriendlyName, x.DevicePath, x.Supported, x.UserEnabled, x.Active, x.Error)).ToArray();

    public async ValueTask DisposeAsync()
    {
        stopping.Cancel();
        events.Writer.TryComplete();
        if (observer is not null) await observer.DisposeAsync();
        if (sender is not null) try { await sender; } catch (OperationCanceledException) { }
        if (shortcutServer is not null) try { await shortcutServer; } catch (OperationCanceledException) { }
        if (providerServer is not null) try { await providerServer; } catch (OperationCanceledException) { }
        try { await WindowsDesktopNotification.DrainAsync(); } catch (TimeoutException) { }
        try { await WindowsSystemSound.DrainAsync(); } catch (TimeoutException) { }
        try { await WindowsAudioPlayback.DrainAsync(); } catch (TimeoutException) { }
        try { await WindowsPowerScheme.DrainAsync(); } catch (TimeoutException) { }
        try { await WindowsTaskbarAutoHide.DrainAsync(); } catch (TimeoutException) { }
        try { await WindowsProgramPriority.DrainAsync(); } catch (TimeoutException) { }
        try { await WindowsForegroundWindowLayout.DrainAsync(); } catch (TimeoutException) { }
        try { await WindowsForegroundWindowMonitor.DrainAsync(); } catch (TimeoutException) { }
        try { await WindowsSessionLock.DrainAsync(); } catch (TimeoutException) { }
        try { await WindowsDisplayOff.DrainAsync(); } catch (TimeoutException) { }
        try { await WindowsCurrentMedia.DrainAsync(); } catch (TimeoutException) { }
        try { await WindowsHardwareBrightness.DrainAsync(); } catch (TimeoutException) { }
        try { await WindowsBuiltInBrightness.DrainAsync(); } catch (TimeoutException) { }
        shortcuts.Dispose();
        stopping.Dispose(); watchGate.Dispose();
    }
}
