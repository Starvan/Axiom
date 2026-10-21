using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Axiom.Contracts;
using Axiom.Domain;
using Axiom.Infrastructure;
using Axiom.Providers.Windows;
using Axiom.WinUI;

namespace Axiom.Engine.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class TrayActionTests
{
    [Fact]
    public void TrayModuleUsesStartupComposerAndBatchPathsWithDelayAndStrictValidation()
    {
        var modules = new IAutomationPlugin[] { new ProgramPlugin(),new AxiomStartupPlugin(),new TrayPlugin() };
        var compiler=new AutomationTaskCompiler();
        foreach(var module in modules) {foreach(var detection in module.Detections)compiler.Register(detection);foreach(var action in module.Actions)compiler.Register(action);}
        var combinations=modules.SelectMany(m=>m.Combinations).ToImmutableArray(); var editor=new TaskEditorState();editor.Load(null);editor.Title="启动隐藏托盘";
        editor.Select(new AxiomStartupTaskDetection().Descriptor,combinations);editor.SetParameter(TaskCapabilityKind.Detection,"frequency","firstBoot");
        editor.Select(TrayCapabilities.Descriptor,combinations);editor.SetParameter(TaskCapabilityKind.Action,"executable","C:\\Apps\\SampleAudio\\SampleAudio.exe\nC:\\Tools\\Other.exe\nC:\\Apps\\SampleAudio\\SampleAudio.exe");
        editor.SetParameter(TaskCapabilityKind.Action,"delaySeconds","2");
        var task=editor.Build();var rule=compiler.Compile(task,"owner",ResourceScope.Session("owner",1));
        Assert.Equal(RuleActionKind.Command,rule.Action.Kind);Assert.Equal(TrayCapabilities.Hide,rule.Action.Key.CapabilityId);Assert.Equal(TimeSpan.FromSeconds(2),rule.Timing.MinimumDuration);
        Assert.Equal(2,TrayCapabilities.ValidateTargets(task.Action).Length);
        Assert.Equal(TaskFieldKind.TraySelection,TrayCapabilities.Descriptor.Fields.Single(f=>f.Id=="executable").Kind);
        foreach(var value in new[]{"0","121","NaN"})Assert.Throws<ArgumentException>(()=>TrayCapabilities.ValidateTargets(task.Action with{Parameters=task.Action.Parameters.SetItem("waitSeconds",value)}));
        foreach(var value in new[]{"SampleAudio.exe",@"C:\Windows\cmd.exe",@"C:\Axiom.exe"})Assert.Throws<ArgumentException>(()=>TrayCapabilities.ValidateTargets(task.Action with{Parameters=task.Action.Parameters.SetItem("executable",value)}));
        Assert.Throws<ArgumentException>(()=>TrayCapabilities.ValidateTargets(task.Action with{Parameters=task.Action.Parameters.Add("inject","true")}));
    }
    [Fact(Timeout=15000)]
    public async Task CurrentCatalogIncludesDynamicWindowUidAndDropsRemovedIcons()
    {
        using var target = new IconWindow(false, true);
        target.Add(false);
        var catalog = WindowsTrayActions.ReadCatalog(default);
        var entry = Assert.Single(catalog.Entries, e => e.ExecutablePath.Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, entry.IconCount);
        Assert.True(target.Exists(false)); // Discovery has no side effects.
        var result = await WindowsTrayActions.HideAsync(Environment.ProcessPath!, TimeSpan.FromSeconds(2), default);
        Assert.True(result.Success, result.Reason);
        Assert.False(target.Exists(false)); Assert.True(target.Alive);
        Assert.DoesNotContain(WindowsTrayActions.ReadCatalog(default).Entries, e => e.ExecutablePath.Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase));
        target.Add(false); Assert.True(target.Exists(false));
    }
    [Fact]
    public async Task CatalogRequestsAreReadOnlyAndBoundedAndSelectionCancelIsIndependent()
    {
        var now = DateTimeOffset.UtcNow;
        var request = new AutomationMessage(1, "read-tray", "catalog", now.AddSeconds(10));
        AutomationProtocol.ValidateRequest(request, now);
        Assert.Throws<InvalidDataException>(() => AutomationProtocol.ValidateRequest(request with { Id = Guid.NewGuid().ToString() }, now));
        Assert.Throws<InvalidDataException>(() => AutomationProtocol.ValidateRequest(request with { Tray = new([], now) }, now));
        Assert.Throws<InvalidDataException>(() => AutomationProtocol.ValidateRequest(request, now, true));
        var provider = new ProviderMessage(1, "read-tray", "catalog", now.AddSeconds(10)); ProviderProtocol.Validate(provider, now);
        using var frame = new MemoryStream(); await AutomationProtocol.WriteAsync(frame, request, default);
        Assert.DoesNotContain("\"Tray\"", System.Text.Encoding.UTF8.GetString(frame.ToArray()));
        using var providerFrame = new MemoryStream(); await ProviderProtocol.WriteAsync(providerFrame, provider, default);
        Assert.DoesNotContain("\"Tray\"", System.Text.Encoding.UTF8.GetString(providerFrame.ToArray()));
        Assert.Throws<InvalidDataException>(() => ProviderProtocol.Validate(provider with { CommandId = Guid.NewGuid() }, now));
        Assert.Throws<InvalidDataException>(() => ProviderProtocol.Validate(provider with { Resources = [] }, now));
        TrayCatalogValidation.Validate(new([new(@"C:\Apps\bin\SampleApp.exe", "SampleApp", 2)], now));
        Assert.Throws<ArgumentException>(() => TrayCatalogValidation.Validate(new([new("SampleApp.exe", "SampleApp", 1)], now)));
        Assert.Throws<InvalidDataException>(() => TrayCatalogValidation.Validate(new([new(@"C:\Apps\One.exe", "One", 0)], now)));
        Assert.Throws<InvalidDataException>(() => TrayCatalogValidation.Validate(new([new(@"C:\Apps\One.exe", "One", 1), new(@"c:\apps\one.exe", "One", 1)], now)));
        var original = new ProcessSelectionState(@"C:\Apps\Existing.exe", true); var draft = new ProcessSelectionState(original.Value, true);
        draft.Set([@"C:\Apps\bin\SampleApp.exe", @"c:\apps\bin\sampleapp.exe"], true);
        Assert.Equal(2, draft.Names.Count); Assert.Single(original.Names);
    }
    [Fact(Timeout=15000)]
    public async Task RemovesIdAndGuidIconsIncludingMessageOnlyWindowWithoutStoppingProcess()
    {
        using var first=new IconWindow(false);using var second=new IconWindow(true);
        first.Add(false);first.Add(true);second.Add(false);
        Assert.True(first.Exists(false));Assert.True(first.Exists(true));Assert.True(second.Exists(false));
        var result=await WindowsTrayActions.HideAsync(Environment.ProcessPath!,TimeSpan.FromSeconds(3),default);
        Assert.True(result.Success,result.Reason);Assert.Contains("3 个",result.Reason);
        Assert.False(first.Exists(false));Assert.False(first.Exists(true));Assert.False(second.Exists(false));
        Assert.True(first.Alive);Assert.True(second.Alive);
        first.Add(false);Assert.True(first.Exists(false)); // A later re-add is deliberately not suppressed.
    }
    [Fact(Timeout=15000)]
    public async Task WaitsForIconOnExistingWindowUsingNotificationsAndCancellationPreventsLaterDeletion()
    {
        using var target=new IconWindow(false);
        var pending=WindowsTrayActions.HideAsync(Environment.ProcessPath!,TimeSpan.FromSeconds(4),default);
        await Task.Delay(1200); Assert.False(pending.IsCompleted);
        target.Add(false);Assert.True((await pending).Success);
        using var cancel=new CancellationTokenSource();
        var cancelled=WindowsTrayActions.HideAsync(Environment.ProcessPath!,TimeSpan.FromSeconds(4),cancel.Token);
        cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>cancelled);
        target.Add(false);Assert.True(target.Exists(false));
    }
    [Fact(Timeout=15000)]
    public async Task NoIconIsNoOpAndMissingPathDoesNotAffectOtherIcons()
    {
        using var target=new IconWindow(false);
        var noIcon=await WindowsTrayActions.HideAsync(Environment.ProcessPath!,TimeSpan.FromSeconds(1),default);
        Assert.True(noIcon.Success);Assert.True(noIcon.Skipped);Assert.Contains("无需处理",noIcon.Reason);
        Assert.DoesNotContain("已隐藏成功",noIcon.Reason);
        target.Add(false);
        var missing=await WindowsTrayActions.HideAsync(Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".exe"),TimeSpan.FromSeconds(1),default);
        Assert.False(missing.Success);Assert.True(target.Exists(false));
        var hidden = await WindowsTrayActions.HideAsync(Environment.ProcessPath!, TimeSpan.FromSeconds(1), default);
        Assert.True(hidden.Success); Assert.False(hidden.Skipped);
        var repeated = await WindowsTrayActions.HideAsync(Environment.ProcessPath!, TimeSpan.FromSeconds(1), default);
        Assert.True(repeated.Success); Assert.True(repeated.Skipped); Assert.True(target.Alive);
    }
    [Fact(Timeout = 15000)]
    public async Task ShortStartupWaitMissesLateWindowButLongWaitHidesItWithoutManualRetry()
    {
        var shortWait = await WindowsTrayActions.HideAsync(Environment.ProcessPath!, TimeSpan.FromSeconds(1), default);
        Assert.True(shortWait.Success); Assert.True(shortWait.Skipped);
        var pending = WindowsTrayActions.HideAsync(Environment.ProcessPath!, TimeSpan.FromSeconds(60), default);
        await Task.Delay(1500); Assert.False(pending.IsCompleted);
        using var target = new IconWindow(false, true);
        target.Add(false);
        var hidden = await pending.WaitAsync(TimeSpan.FromSeconds(4));
        Assert.True(hidden.Success, hidden.Reason); Assert.False(target.Exists(false)); Assert.True(target.Alive);
        using var cancel = new CancellationTokenSource();
        var cancelled = WindowsTrayActions.HideAsync(Environment.ProcessPath!, TimeSpan.FromSeconds(120), cancel.Token);
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        target.Add(false); Assert.True(target.Exists(false));
    }
    [Fact(Timeout=15000)]
    public async Task SharedWaitSourceSurvivesOneSubscriberAndCanBeRecreated()
    {
        using var target=new IconWindow(false);using var one=new WindowsTrayEvents();
        using(var two=new WindowsTrayEvents()) { target.Add(false);using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(3));Assert.True(await two.Events.Reader.ReadAsync(deadline.Token)); }
        target.Add(true);using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(3));Assert.True(await one.Events.Reader.ReadAsync(limit.Token));
        one.Dispose();
        using var recreated=new WindowsTrayEvents();target.Remove(false);using var end=new CancellationTokenSource(TimeSpan.FromSeconds(3));Assert.True(await recreated.Events.Reader.ReadAsync(end.Token));
    }
    [Fact(Timeout = 15000)]
    public async Task ContinuousTargetNotificationsHideSameIdentityAgainAndCancelLeavesNewIconVisible()
    {
        using var target = new IconWindow(false, true);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var opportunities = System.Threading.Channels.Channel.CreateBounded<long>(1);
        var completions = System.Threading.Channels.Channel.CreateUnbounded<ProgramControlResult>();
        var watcher = WindowsTrayActions.WatchTargetsAsync([Environment.ProcessPath!], (snapshot, counter, active) =>
        {
            if (snapshot) ready.TrySetResult(); else if (active) opportunities.Writer.TryWrite(counter);
        }, lifetime.Token);
        var worker = Task.Run(async () =>
        {
            while (!lifetime.IsCancellationRequested)
            {
                await opportunities.Reader.ReadAsync(lifetime.Token);
                var result = await WindowsTrayActions.HideAsync(Environment.ProcessPath!, TimeSpan.FromSeconds(2), lifetime.Token);
                completions.Writer.TryWrite(result);
            }
        }, lifetime.Token);
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(3));
            target.Add(false);
            var first = await completions.Reader.ReadAsync(lifetime.Token); Assert.True(first.Success, first.Reason); Assert.False(target.Exists(false));
            // NIM_ADD reuses the same HWND/UID, including when removal and creation notifications merge.
            target.Add(false);
            var second = await completions.Reader.ReadAsync(lifetime.Token); Assert.True(second.Success, second.Reason); Assert.False(target.Exists(false));
        }
        finally
        {
            lifetime.Cancel();
            try { await watcher; } catch (OperationCanceledException) { }
            try { await worker; } catch (OperationCanceledException) { }
        }
        target.Add(false); Assert.True(target.Exists(false)); Assert.True(target.Alive);
    }
    [Fact]
    public async Task BatchSummariesSeparateAbsentTargetsFromFailuresAndCollapseRepeatedReasons()
    {
        var paths = Enumerable.Range(1, 8).Select(i => $@"C:\Synthetic\Tray{i}.exe").ToArray();
        var absent = await WindowsProgramBatch.ExecuteAsync(paths,
            (_, _) => Task.FromResult(new ProgramControlResult(true, "未发现图标，无需处理。") { Skipped = true }), default);
        Assert.True(absent.Success); Assert.True(absent.Skipped);
        Assert.Contains("无需处理 8 个目标", absent.Reason); Assert.DoesNotContain("0/8", absent.Reason);
        Assert.DoesNotContain(paths[0], absent.Reason);
        var mixed = await WindowsProgramBatch.ExecuteAsync(paths, (path, _) => Task.FromResult(path == paths[0]
            ? new ProgramControlResult(false, "权限不足") : new ProgramControlResult(true, "未发现图标") { Skipped = true }), default);
        Assert.False(mixed.Success); Assert.Contains("失败 1 个", mixed.Reason); Assert.Contains("无需处理 7 个", mixed.Reason);
        Assert.Contains(paths[0], mixed.Reason); Assert.DoesNotContain(paths[1], mixed.Reason);
        var denied = await WindowsProgramBatch.ExecuteAsync(paths,
            (_, _) => Task.FromResult(new ProgramControlResult(false, "权限不足")), default);
        Assert.False(denied.Success); Assert.Equal(1, denied.Reason.Split("权限不足").Length - 1);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WindowsProgramBatch.ExecuteAsync(paths,
            (_, _) => Task.FromResult(new ProgramControlResult(true, "不应调用")), cancelled.Token));
    }

    private sealed class IconWindow : IDisposable
    {
        private static int serial;
        private uint id=(uint)Interlocked.Increment(ref serial)+20000;
        private readonly Guid guid=Guid.NewGuid();
        private readonly Thread thread;
        private readonly TaskCompletionSource ready=new(TaskCreationOptions.RunContinuationsAsynchronously);
        private nint window;private uint threadId;
        internal IconWindow(bool messageOnly, bool dynamicUid = false)
        {
            thread=new Thread(()=>{
                threadId=GetCurrentThreadId();
                window=CreateWindowEx(0,"STATIC","Axiom tray test",0,0,0,0,0,messageOnly?-3:0,0,0,0);
                if (dynamicUid) id = unchecked((uint)window.ToInt64());
                if(window==0){ready.TrySetException(new InvalidOperationException("Fixture window failed"));return;}ready.TrySetResult();
                while(GetMessage(out var msg,0,0,0)>0){TranslateMessage(ref msg);DispatchMessage(ref msg);}DestroyWindow(window);
            }){IsBackground=true};thread.Start();ready.Task.GetAwaiter().GetResult();
        }
        internal bool Alive=>IsWindow(window);
        private IconData Data(bool withGuid)=>new(){Size=(uint)Marshal.SizeOf<IconData>(),Window=window,Id=id+(withGuid?1u:0u),Flags=6u|(withGuid?32u:0u),Icon=LoadIcon(0,32512),Tip="Axiom tray test "+id,Info="",Title="",Guid=withGuid?guid:Guid.Empty};
        internal void Add(bool withGuid){var data=Data(withGuid);Assert.True(Shell_NotifyIcon(0,ref data),"Fixture icon could not be created on desktop");}
        internal void Remove(bool withGuid){var data=Data(withGuid);Shell_NotifyIcon(2,ref data);}
        internal bool Exists(bool withGuid){var data=Data(withGuid);var identity=new Identifier{Size=(uint)Marshal.SizeOf<Identifier>(),Window=window,Id=data.Id,Guid=data.Guid};return Shell_NotifyIconGetRect(ref identity,out _)>=0;}
        public void Dispose(){Remove(false);Remove(true);PostThreadMessage(threadId,0x0012,0,0);thread.Join(TimeSpan.FromSeconds(3));}
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct IconData{
            public uint Size;public nint Window;public uint Id,Flags,Message;public nint Icon;
            [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]public string Tip;public uint State,Mask;
            [MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)]public string Info;public uint Version;
            [MarshalAs(UnmanagedType.ByValTStr,SizeConst=64)]public string Title;public uint InfoFlags;public Guid Guid;public nint Balloon;}
        [StructLayout(LayoutKind.Sequential)] private struct Identifier{public uint Size;public nint Window;public uint Id;public Guid Guid;}
        [StructLayout(LayoutKind.Sequential)] private struct Rect{public int Left,Top,Right,Bottom;}
        [StructLayout(LayoutKind.Sequential)]private struct Msg{public nint Window;public uint Id;public nint W,L;public uint Time;public int X,Y;public uint Private;}
        [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern nint CreateWindowEx(uint ex,string cls,string title,uint style,int x,int y,int w,int h,nint parent,nint menu,nint instance,nint parameter);
        [DllImport("user32.dll")]private static extern nint LoadIcon(nint instance,nint name);
        [DllImport("user32.dll")]private static extern int GetMessage(out Msg message,nint window,uint min,uint max);
        [DllImport("user32.dll")]private static extern bool TranslateMessage(ref Msg message);
        [DllImport("user32.dll")]private static extern nint DispatchMessage(ref Msg message);
        [DllImport("user32.dll")]private static extern bool DestroyWindow(nint window);
        [DllImport("user32.dll")]private static extern bool IsWindow(nint window);
        [DllImport("user32.dll")]private static extern bool PostThreadMessage(uint thread,uint message,nint w,nint l);
        [DllImport("kernel32.dll")]private static extern uint GetCurrentThreadId();
        [DllImport("shell32.dll",CharSet=CharSet.Unicode)]private static extern bool Shell_NotifyIcon(uint operation,ref IconData data);
        [DllImport("shell32.dll")]private static extern int Shell_NotifyIconGetRect(ref Identifier id,out Rect rect);
    }
}
