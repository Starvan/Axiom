using System.Runtime.InteropServices;
using Windows.Graphics.Display;
using Windows.System;
using WinRT;

namespace Axiom.Providers.Windows;

// Display mode broadcasts alone do not guarantee advanced-color changes. This
// narrow consumer owns the required DispatcherQueue and watches the primary
// monitor even while HDR has paused capture, so turning HDR off can resume it.
internal sealed unsafe class PrimaryColorEvents : IDisposable
{
    private readonly DispatcherQueueController controller = DispatcherQueueController.CreateOnDedicatedThread();
    private readonly Action changed;
    private readonly Action<Exception> failed;
    private readonly Action<bool>? hdr;
    private readonly ScreenContentColorModeFilter modes = new();
    private DisplayInformation? info;
    private int disposed;
    internal PrimaryColorEvents(Action changed, Action<Exception> failed, Action<bool>? hdr = null)
    {
        this.changed = changed; this.failed = failed; this.hdr = hdr;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            if (!controller.DispatcherQueue.TryEnqueue(() =>
            {
                try { Bind(); ready.SetResult(); }
                catch (Exception error) { ready.SetException(error); }
            })) throw new IOException("无法订阅主屏颜色变化。");
            ready.Task.GetAwaiter().GetResult();
        }
        catch { Dispose(); throw; }
    }
    internal void Refresh()
    {
        if (Volatile.Read(ref disposed) != 0) return;
        if (!controller.DispatcherQueue.TryEnqueue(() =>
        {
            if (Volatile.Read(ref disposed) != 0) return;
            try { Bind(); changed(); } catch (Exception error) { failed(error); }
        })) failed(new IOException("主屏颜色事件队列已停止。"));
    }
    private void Changed(DisplayInformation sender, object args)
    {
        if (Volatile.Read(ref disposed) != 0 || info is null) return;
        try
        {
            var kind = info.GetAdvancedColorInfo().CurrentAdvancedColorKind;
            if (modes.Update((int)kind)) { hdr?.Invoke(kind == AdvancedColorKind.HighDynamicRange); changed(); }
        }
        catch (Exception error) { failed(error); }
    }
    private void Bind()
    {
        ReleaseInfo();
        const string runtimeClass = "Windows.Graphics.Display.DisplayInformation";
        Marshal.ThrowExceptionForHR(WindowsCreateString(runtimeClass, runtimeClass.Length, out var name));
        nint factory = 0, created = 0;
        try
        {
            var iid = new Guid("7449121C-382B-4705-8DA7-A795BA482013");
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(name, ref iid, out factory));
            var displayIid = new Guid("BED112AE-ADC3-4DC9-AE65-851F4D7D4799");
            var method = Marshal.ReadIntPtr(Marshal.ReadIntPtr(factory), 7 * IntPtr.Size);
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint,nint,Guid*,nint*,int>)method)(factory, MonitorFromPoint(default, 1), &displayIid, &created));
            info = MarshalInspectable<DisplayInformation>.FromAbi(created);
            info.AdvancedColorInfoChanged += Changed;
            // Read after subscribing so a mode transition cannot fall between
            // the initial read and registration. Reading failures abort setup.
            var kind = info.GetAdvancedColorInfo().CurrentAdvancedColorKind;
            modes.Baseline((int)kind); hdr?.Invoke(kind == AdvancedColorKind.HighDynamicRange);
        }
        finally { if (created != 0) Marshal.Release(created); if (factory != 0) Marshal.Release(factory); WindowsDeleteString(name); }
    }
    private void ReleaseInfo()
    {
        var previous = info; info = null;
        if (previous is null) return;
        try { previous.AdvancedColorInfoChanged -= Changed; }
        finally { ((IWinRTObject)previous).NativeObject.Dispose(); }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            if (controller.DispatcherQueue.TryEnqueue(() => { try { ReleaseInfo(); released.SetResult(); } catch (Exception error) { released.SetException(error); } }))
                released.Task.GetAwaiter().GetResult();
        }
        finally { controller.ShutdownQueueAsync().AsTask().GetAwaiter().GetResult(); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string value, int length, out nint name);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(nint name);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(nint name, ref Guid iid, out nint factory);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(Point point, uint flags);
}
