using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.Providers.Windows;
using Axiom.WinUI;

namespace Axiom.Engine.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[Collection("Native application windows")]
public sealed class WindowActionTests
{
    [Fact]
    public void WindowModuleCompilesWithExistingStartupEditorAndValidatesItsOwnParameters()
    {
        var modules = new IAutomationPlugin[] { new ProgramPlugin(), new AxiomStartupPlugin(), new WindowPlugin() };
        var compiler = new AutomationTaskCompiler();
        foreach (var module in modules) { foreach (var detection in module.Detections) compiler.Register(detection); foreach (var action in module.Actions) compiler.Register(action); }
        var combinations = modules.SelectMany(m => m.Combinations).ToImmutableArray(); var editor = new TaskEditorState(); editor.Load(null); editor.Title = "耳机启动窗口";
        editor.Select(new AxiomStartupTaskDetection().Descriptor, combinations); editor.SetParameter(TaskCapabilityKind.Detection, "frequency", "firstBoot");
        editor.Select(WindowCapabilities.Descriptor, combinations); editor.SetParameter(TaskCapabilityKind.Action, "executable", @"C:\Apps\SampleAudio\SampleAudio.exe");
        editor.SetParameter(TaskCapabilityKind.Action, "delaySeconds", "2");
        var task = editor.Build(); var rule = compiler.Compile(task, "owner", ResourceScope.Session("owner", 1));
        Assert.Equal(RuleActionKind.Command, rule.Action.Kind); Assert.Equal(WindowCapabilities.Close, rule.Action.Key.CapabilityId);
        Assert.Equal(TimeSpan.FromSeconds(2), rule.Timing.MinimumDuration); Assert.Equal("firstBoot", task.Detection.Parameters["frequency"]);
        foreach (var value in new[] { "0", "31", "NaN" }) Assert.Throws<ArgumentException>(() => WindowCapabilities.Validate(task.Action with { Parameters = task.Action.Parameters.SetItem("waitSeconds", value) }));
        Assert.Throws<ArgumentException>(() => WindowCapabilities.Validate(task.Action with { Parameters = task.Action.Parameters.Add("kill", "true") }));
        Assert.Throws<ArgumentException>(() => WindowCapabilities.Validate(task.Action with { Parameters = task.Action.Parameters.SetItem("executable", @"C:\Windows\cmd.exe") }));
    }
    [Fact(Timeout = 10000)]
    public async Task CloseVisibleWindowConfirmsHiddenAndLeavesProcessAndLaterManualReopenAlone()
    {
        using var window = new TrayWindow(); window.Show();
        var result = await WindowsWindowActions.CloseAsync(Environment.ProcessPath!, window.Title, TimeSpan.FromSeconds(2), default);
        Assert.True(result.Success, result.Reason); Assert.False(window.Visible); Assert.Equal(1, window.CloseCount);
        Assert.Contains("程序仍在运行", result.Reason);
        window.Show(); Assert.True(window.Visible); await Task.Delay(100); Assert.Equal(1, window.CloseCount);
    }
    [Fact(Timeout = 10000)]
    public async Task WaitForShowUsesEventAndDoesNotCloseOtherWindows()
    {
        using var other = new TrayWindow(); using var target = new TrayWindow(); other.Show();
        var pending = WindowsWindowActions.CloseAsync(Environment.ProcessPath!, target.Title, TimeSpan.FromSeconds(3), default);
        target.Show(); var result = await pending;
        Assert.True(result.Success, result.Reason); Assert.False(target.Visible); Assert.True(other.Visible); Assert.Equal(0, other.CloseCount);
    }
    [Fact(Timeout = 10000)]
    public async Task BatchMissingTargetDoesNotPreventClosingRealWindow()
    {
        using var target = new TrayWindow(); target.Show();
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe");
        var result = await WindowsProgramBatch.ExecuteAsync([missing, Environment.ProcessPath!], (path, token) =>
            WindowsWindowActions.CloseAsync(path, target.Title, TimeSpan.FromSeconds(1), token), default);
        Assert.False(result.Success); Assert.Contains("完成 1 个", result.Reason); Assert.Contains("失败 1 个", result.Reason); Assert.False(target.Visible); Assert.Equal(1, target.CloseCount);
    }
    [Fact(Timeout = 10000)]
    public async Task CancelledWaitCannotCloseWindowShownAfterCancellationAndTimeoutDoesNotSucceed()
    {
        using var target = new TrayWindow(); using var cancel = new CancellationTokenSource();
        var pending = WindowsWindowActions.CloseAsync(Environment.ProcessPath!, target.Title, TimeSpan.FromSeconds(3), cancel.Token);
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        target.Show(); Assert.True(target.Visible); Assert.Equal(0, target.CloseCount);
        var result = await WindowsWindowActions.CloseAsync(Environment.ProcessPath!, "missing-" + Guid.NewGuid(), TimeSpan.FromSeconds(1), default);
        Assert.False(result.Success); Assert.Contains("超时", result.Reason); Assert.Equal(0, target.CloseCount);
    }
    [Fact(Timeout = 10000)]
    public async Task PostedCloseIsNotSuccessIfApplicationKeepsWindowVisible()
    {
        using var target = new TrayWindow(ignoreClose: true); target.Show();
        var result = await WindowsWindowActions.CloseAsync(Environment.ProcessPath!, target.Title, TimeSpan.FromSeconds(1), default);
        Assert.False(result.Success); Assert.True(target.Visible); Assert.Equal(1, target.CloseCount);
    }
    [Fact(Timeout = 10000)]
    public async Task SharedWindowSourceSurvivesOneSubscriberReleaseAndFinalReleaseAllowsReuse()
    {
        using var first = new TrayWindow(); using var second = new TrayWindow();
        using var cancel = new CancellationTokenSource();
        var one = WindowsWindowActions.CloseAsync(Environment.ProcessPath!, first.Title, TimeSpan.FromSeconds(3), cancel.Token);
        var two = WindowsWindowActions.CloseAsync(Environment.ProcessPath!, second.Title, TimeSpan.FromSeconds(3), default);
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => one);
        second.Show(); Assert.True((await two).Success); first.Show(); Assert.True(first.Visible);
        Assert.True((await WindowsWindowActions.CloseAsync(Environment.ProcessPath!, first.Title, TimeSpan.FromSeconds(1), default)).Success);
    }

    [Theory(Timeout = 10000)]
    [InlineData("minimize")]
    [InlineData("maximize")]
    [InlineData("restore")]
    public async Task WindowStateCommandsReadBackNativeStateAndLeaveLaterUserChangesAlone(string state)
    {
        using var target = new TrayWindow(); target.Show();
        target.SetState(state == "restore" ? 3 : 9);
        if (state == "restore") target.SetState(6); // native restore first returns this window to maximized
        var result = await WindowsWindowActions.CloseAsync(Environment.ProcessPath!, target.Title, TimeSpan.FromSeconds(2), default, state);
        Assert.True(result.Success, result.Reason);
        Assert.True(state == "minimize" ? target.Minimized : state == "maximize" ? target.Maximized : !target.Minimized && !target.Maximized);
        target.SetState(9); Assert.False(target.Minimized); Assert.False(target.Maximized);
        Assert.Equal(0, target.CloseCount);
    }
    [Theory(Timeout = 10000)]
    [InlineData("left")]
    [InlineData("right")]
    [InlineData("center")]
    public async Task NativeLayoutMovesAndResizesWithinCurrentWorkArea(string layout)
    {
        using var target = new TrayWindow(); target.Show(); target.SetState(3); target.SetState(6);
        var result = await WindowsWindowActions.CloseAsync(Environment.ProcessPath!, target.Title, TimeSpan.FromSeconds(2), default, layout);
        Assert.True(result.Success, result.Reason); Assert.False(target.Maximized); Assert.False(target.Minimized); Assert.Equal(0, target.CloseCount);
        Assert.True(target.Visible);
        target.SetState(3); Assert.True(target.Maximized); // later user control remains possible
    }

    [Theory(Timeout = 10000)]
    [InlineData("top-left")]
    [InlineData("top-right")]
    [InlineData("bottom-left")]
    [InlineData("bottom-right")]
    public async Task NativeCornerLayoutReadsBackPhysicalBoundsAndLeavesOtherWindowsAndManualChangesAlone(string layout)
    {
        using var target = new TrayWindow(); using var other = new TrayWindow(); target.Show(); other.Show();
        var otherBounds = PhysicalWindow(() => other.Bounds);
        target.SetState(3); target.SetState(6);
        var result = await WindowsWindowActions.CloseAsync(Environment.ProcessPath!, target.Title, TimeSpan.FromSeconds(2), default, layout);
        Assert.True(result.Success, result.Reason); Assert.False(target.Maximized); Assert.False(target.Minimized);
        var monitor = WindowsWindowMonitor.ReadMonitors().Single(m => m.Handle == PhysicalWindow(() => MonitorFromWindow(target.WindowHandle, 2)));
        var expected = WindowLayoutGeometry.Calculate(layout, monitor.Work.Left, monitor.Work.Top, monitor.Work.Width, monitor.Work.Height);
        Assert.Equal((expected.X, expected.Y, expected.X + expected.Width, expected.Y + expected.Height), PhysicalWindow(() => target.Bounds));
        Assert.Equal(otherBounds, PhysicalWindow(() => other.Bounds)); Assert.Equal(0, target.CloseCount); Assert.Equal(0, other.CloseCount);
        target.SetState(3); Assert.True(target.Maximized); // No continuing controller restores the requested corner.
    }

    [Fact(Timeout = 10000)]
    public async Task WrongCreationOrCancelledLayoutDoesNotRestoreOrMoveTheSelectedWindow()
    {
        using var target = new TrayWindow(); target.Show(); target.SetState(3); var before = PhysicalWindow(() => target.Bounds);
        using var events = new WindowsWindowEvents();
        var stale = await WindowsWindowLayout.ApplyAsync(target.WindowHandle, Environment.ProcessId, Environment.ProcessPath!,
            DateTime.UnixEpoch, "bottom-right", events, default);
        Assert.False(stale.Success); Assert.Contains("进程已变化", stale.Reason); Assert.True(target.Maximized);
        Assert.Equal(before, PhysicalWindow(() => target.Bounds));
        using var process = Process.GetCurrentProcess(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WindowsWindowLayout.ApplyAsync(target.WindowHandle, Environment.ProcessId,
            Environment.ProcessPath!, process.StartTime.ToUniversalTime(), "top-left", events, cancellation.Token));
        Assert.True(target.Maximized); Assert.Equal(before, PhysicalWindow(() => target.Bounds)); Assert.Equal(0, target.CloseCount);
    }

    [Fact(Timeout = 10000)]
    public async Task AWindowThatRejectsPlacementTimesOutWithoutRepeatedWritesOrLaterAutomaticRecovery()
    {
        using var target = new TrayWindow(); target.Show(); var before = PhysicalWindow(() => target.Bounds);
        target.RejectPlacement = true;
        var result = await WindowsWindowActions.CloseAsync(Environment.ProcessPath!, target.Title, TimeSpan.FromSeconds(2), default, "bottom-right");
        Assert.False(result.Success); Assert.Contains("期限", result.Reason); Assert.Contains("不会自动重试", result.Reason);
        Assert.Equal(1, target.RejectedPlacements); Assert.Equal(before, PhysicalWindow(() => target.Bounds));
        target.RejectPlacement = false; target.SetState(3); Assert.True(target.Maximized);
        Assert.Equal(1, target.RejectedPlacements); Assert.Equal(0, target.CloseCount);
    }

    private static T PhysicalWindow<T>(Func<T> read)
    {
        var previous = SetThreadDpiAwarenessContext(-4); Assert.NotEqual(0, previous);
        try { return read(); } finally { SetThreadDpiAwarenessContext(previous); }
    }
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [Theory(Timeout = 10000)]
    [InlineData("maximize")]
    [InlineData("top-left")]
    public async Task CancelledStateWaitDoesNotChangeALaterWindow(string state)
    {
        using var target = new TrayWindow(); using var cancellation = new CancellationTokenSource();
        var pending = WindowsWindowActions.CloseAsync(Environment.ProcessPath!, target.Title, TimeSpan.FromSeconds(3), cancellation.Token, state);
        cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        target.Show(); Assert.False(target.Maximized); Assert.Equal(0, target.CloseCount);
    }

    [Fact(Timeout = 15000)]
    public async Task WindowPresenceTracksShowHideAndRetainsSharedCommandSubscription()
    {
        using var target = new TrayWindow(); using var cancellation = new CancellationTokenSource();
        var changes = System.Threading.Channels.Channel.CreateUnbounded<WindowPresenceValue[]>();
        var observing = WindowsWindowPresence.ObserveAsync([Path.GetFileName(Environment.ProcessPath!)], s => changes.Writer.TryWrite(s), cancellation.Token);
        var initial = await changes.Reader.ReadAsync(); Assert.False(initial.Single().Visible);
        target.Show(); Assert.True((await changes.Reader.ReadAsync()).Single().Visible);
        var result = await WindowsWindowActions.CloseAsync(Environment.ProcessPath!, target.Title, TimeSpan.FromSeconds(2), default);
        Assert.True(result.Success, result.Reason); Assert.False((await changes.Reader.ReadAsync()).Single().Visible);
        cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observing);
        target.Show(); Assert.True(target.Visible);
        Assert.True((await WindowsWindowActions.CloseAsync(Environment.ProcessPath!, target.Title, TimeSpan.FromSeconds(2), default)).Success);
    }

    [Fact(Timeout = 10000)]
    public async Task TopmostAndUndoReadBackNativeStyleAndPreserveWindowSizeAndVisibility()
    {
        using var target = new TrayWindow(); target.Show();
        var initial = target.Bounds;
        var pinned = await WindowsWindowActions.CloseAsync(Environment.ProcessPath!, target.Title, TimeSpan.FromSeconds(2), default, "topmost");
        Assert.True(pinned.Success, pinned.Reason); Assert.True(target.Topmost); Assert.Equal(initial, target.Bounds); Assert.True(target.Visible);
        var unpinned = await WindowsWindowActions.CloseAsync(Environment.ProcessPath!, target.Title, TimeSpan.FromSeconds(2), default, "notopmost");
        Assert.True(unpinned.Success, unpinned.Reason); Assert.False(target.Topmost); Assert.Equal(initial, target.Bounds); Assert.Equal(0, target.CloseCount);
        target.SetState(6); Assert.True(target.Minimized); // no ongoing control after either command
    }

    [Fact(Timeout = 15000)]
    public async Task WindowObservationReadsActualStatesAndStopsWithoutAffectingCommandConsumers()
    {
        using var target = new TrayWindow(); using var cancellation = new CancellationTokenSource();
        var changes = System.Threading.Channels.Channel.CreateUnbounded<string>();
        var observing = WindowsUsageWindows.RunAsync([], (_, kind, path) => { if (string.Equals(path, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) changes.Writer.TryWrite(kind); }, cancellation.Token);
        var received = new List<string>();
        async Task ReadKind(string expected)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                while (true)
                {
                    var read = changes.Reader.ReadAsync(deadline.Token).AsTask();
                    if (await Task.WhenAny(read, observing) == observing) await observing;
                    var kind = await read; received.Add(kind); if (kind == expected) return;
                }
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            { throw new TimeoutException($"Expected {expected}; observed: {string.Join(", ", received)}; minimized={target.Minimized}, maximized={target.Maximized}, visible={target.Visible}."); }
        }
        try
        {
            target.Show(); await ReadKind("window-shown");
            target.SetState(3); await ReadKind("window-maximized");
            target.SetState(6); await ReadKind("window-minimized");
            target.SetState(9); await ReadKind("window-maximized");
            target.SetState(9); await ReadKind("window-normal");
            foreach (var layout in new[] { "left", "right", "center" })
            {
                Assert.True((await WindowsWindowActions.CloseAsync(Environment.ProcessPath!, target.Title, TimeSpan.FromSeconds(2), default, layout)).Success);
                await ReadKind("window-" + layout);
            }
            Assert.True((await WindowsWindowActions.CloseAsync(Environment.ProcessPath!, target.Title, TimeSpan.FromSeconds(2), default)).Success);
            await ReadKind("window-hidden");
        }
        finally { cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observing); }
        target.Show(); Assert.True((await WindowsWindowActions.CloseAsync(Environment.ProcessPath!, target.Title, TimeSpan.FromSeconds(2), default)).Success);
    }

    [Fact(Timeout = 20000)]
    public async Task SyntheticNativeWindowEvidenceRoundTripsStorageAndBecomesOnlyAReviewedPausedDraft()
    {
        using var cancellation = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var records = System.Threading.Channels.Channel.CreateBounded<UsageSample>(128);
        var recorder = new UsageRecorder(new(1, false, false, [], Purpose: "synthetic native window fixture", WindowStates: true), DateTimeOffset.UtcNow);
        var observing = WindowsUsageWindows.RunAsync([], (at, kind, path) =>
        {
            if (string.Equals(path, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase) && !records.Writer.TryWrite(new(at, kind, path)))
                throw new IOException("Synthetic fixture queue overflowed.");
        }, cancellation.Token);
        async Task ReadThrough(string expected)
        {
            while (true)
            {
                var next = records.Reader.ReadAsync(deadline.Token).AsTask();
                if (await Task.WhenAny(next, observing) == observing) await observing;
                var sample = await next; recorder.Add(sample); if (sample.Kind == expected) return;
            }
        }
        try
        {
            for (var i = 0; i < 3; i++)
            {
                using var window = new TrayWindow(); window.Show(); await ReadThrough("window-shown");
                window.SetState(6); await ReadThrough("window-minimized");
                window.SetState(0); await ReadThrough("window-hidden");
            }
        }
        finally { cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observing); }
        recorder.State("stopped", atUtc: DateTimeOffset.UtcNow);
        var path = Path.Combine(Path.GetTempPath(), "axiom-synthetic-observation-" + Guid.NewGuid() + ".db");
        try
        {
            using var journal = new UsageJournal(path); journal.Save(recorder.Session);
            var session = Assert.Single(journal.Read(DateTimeOffset.UtcNow)); Assert.Equal("stopped", session.State);
            var suggestion = Assert.Single(UsageAnalysis.Analyze(session)); Assert.Equal(3, suggestion.Occurrences); Assert.Equal(3, suggestion.Opportunities);
            var modules = new IAutomationPlugin[] { new WindowPresencePlugin(), new WindowPlugin() };
            var catalog = new AutomationSnapshot([], [.. modules.SelectMany(m => m.Detections.Select(d => d.Descriptor).Concat(m.Actions.Select(a => a.Descriptor)))],
                [.. modules.SelectMany(m => m.Combinations)], true, "ready", "", DateTimeOffset.UtcNow, []);
            var summary = UsageAnalysis.Summary(session, catalog);
            Assert.Contains("synthetic native window fixture", summary); Assert.DoesNotContain(Environment.ProcessPath!, summary);
            var draft = UsageAnalysis.Draft(session, suggestion, catalog);
            Assert.True(draft.Paused); Assert.Equal(0, draft.Version); Assert.Equal("minimize", draft.Action.Parameters["state"]);
            var compiler = new AutomationTaskCompiler();
            foreach (var module in modules) { foreach (var d in module.Detections) compiler.Register(d); foreach (var a in module.Actions) compiler.Register(a); }
            Assert.NotNull(compiler.Compile(draft, "owner", ResourceScope.Session("owner", 1)));
            journal.Delete(session.Id); Assert.Empty(journal.Read(DateTimeOffset.UtcNow));
        }
        finally { File.Delete(path); }
    }

    // Native fixture deliberately implements × as hide-to-tray, retaining its process.
    internal sealed class TrayWindow : IDisposable
    {
        private readonly Thread thread;
        private readonly Proc procedure;
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly bool ignoreClose;
        private int count;
        private int rejectedPlacements;
        public volatile bool RejectPlacement;
        public int RejectedPlacements => Volatile.Read(ref rejectedPlacements);
        private nint handle;
        public string Title { get; } = "Axiom window fixture " + Guid.NewGuid();
        public nint WindowHandle => handle;
        public int CloseCount => Volatile.Read(ref count);
        public bool Topmost => (GetWindowLongPtr(handle, -20).ToInt64() & 8) != 0;
        public (int Left, int Top, int Right, int Bottom) Bounds { get { Assert.True(GetWindowRect(handle, out var rect)); return (rect.Left, rect.Top, rect.Right, rect.Bottom); } }
        [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
        public bool Minimized => IsIconic(handle);
        public bool Maximized => IsZoomed(handle);
        public void SetState(int state) => SendMessage(handle, 0x8003, state, 0);
        public bool Visible => IsWindowVisible(handle);
        public TrayWindow(bool ignoreClose = false)
        {
            this.ignoreClose = ignoreClose; procedure = Handle;
            thread = new Thread(Run) { IsBackground = true }; thread.SetApartmentState(ApartmentState.STA); thread.Start();
            ready.Task.GetAwaiter().GetResult();
        }
        private void Run()
        {
            var cls = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Procedure = procedure, Name = Title, Instance = GetModuleHandle(null) };
            if (RegisterClassEx(ref cls) == 0) { ready.SetException(new InvalidOperationException("fixture class failed")); return; }
            handle = CreateWindowEx(0, Title, Title, 0x00CF0000, 20, 20, 250, 100, 0, 0, cls.Instance, 0);
            if (handle == 0) { ready.SetException(new InvalidOperationException("fixture window failed")); return; }
            ready.SetResult();
            while (GetMessage(out var message, 0, 0, 0) > 0) { TranslateMessage(ref message); DispatchMessage(ref message); }
            UnregisterClass(Title, cls.Instance);
        }
        private nint Handle(nint window,uint message,nint w,nint l)
        {
            if (message == 0x0046 && RejectPlacement)
            {
                var position = Marshal.PtrToStructure<WindowPosition>(l);
                position.Flags |= 0x0001 | 0x0002; // This fixture deliberately refuses resize and movement.
                Marshal.StructureToPtr(position, l, false); Interlocked.Increment(ref rejectedPlacements); return 0;
            }
            if (message == 0x0010) { Interlocked.Increment(ref count); if (!ignoreClose) ShowWindow(window, 0); return 0; }
            if (message == 0x8003) { ShowWindow(window, (int)w); return 0; }
            if (message == 0x8001) { DestroyWindow(window); return 0; }
            if (message == 0x0002) { PostQuitMessage(0); return 0; }
            return DefWindowProc(window,message,w,l);
        }
        public void Show() { SendMessage(handle, 0x8002, 0, 0); ShowWindow(handle, 5); }
        public void Dispose() { if (handle != 0) PostMessage(handle, 0x8001, 0, 0); thread.Join(TimeSpan.FromSeconds(2)); }
        private delegate nint Proc(nint window,uint message,nint w,nint l);
        [StructLayout(LayoutKind.Sequential)] private struct WindowPosition { public nint Window, After; public int X, Y, Width, Height; public uint Flags; }
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct WindowClass { public uint Size,Style; public Proc Procedure; public int ClassExtra,WindowExtra; public nint Instance,Icon,Cursor,Background; public string? Menu,Name; public nint SmallIcon; }
        [StructLayout(LayoutKind.Sequential)] private struct Message { public nint Window; public uint Id; public nint W,L; public uint Time; public int X,Y; public uint Private; }
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WindowClass cls);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern bool UnregisterClass(string name,nint instance);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern nint CreateWindowEx(uint extra,string cls,string title,uint style,int x,int y,int width,int height,nint parent,nint menu,nint instance,nint param);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern nint DefWindowProc(nint window,uint message,nint w,nint l);
        [DllImport("user32.dll")] private static extern int GetMessage(out Message message,nint window,uint min,uint max);
        [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
        [DllImport("user32.dll")] private static extern nint DispatchMessage(ref Message message);
        [DllImport("user32.dll")] private static extern bool ShowWindow(nint window,int state);
        [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
        [DllImport("user32.dll")] private static extern bool IsZoomed(nint window);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
        [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
        [DllImport("user32.dll")] private static extern bool PostMessage(nint window,uint message,nint w,nint l);
        [DllImport("user32.dll")] private static extern nint SendMessage(nint window,uint message,nint w,nint l);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] private static extern nint GetModuleHandle(string? module);
    }
}
