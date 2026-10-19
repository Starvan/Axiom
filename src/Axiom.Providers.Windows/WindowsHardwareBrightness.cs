using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace Axiom.Providers.Windows;

[SupportedOSPlatform("windows")]
public static partial class WindowsHardwareBrightness
{
    public static Task<BrightnessWheelGestureResult> RunWheelGestureAsync(string target, string sid, int step, int smoothMilliseconds,
        int writesPerSecond, long fromSteps, Func<long> latestSteps, CancellationToken token)
    {
        var completion = new TaskCompletionSource<BrightnessWheelGestureResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        new Thread(() =>
        {
            BrightnessRestoreCapture? capture = null; IDisposable? ownership = null;
            var result = new BrightnessWheelGestureResult(false, fromSteps, "亮度手势未确认；不重试。");
            try
            {
                token.ThrowIfCancellationRequested();
                ownership = BrightnessNativeOwnership.TryAcquire(manualGesture: true) ?? throw new IOException("旧亮度原生工作尚未清理或没有手动授权。");
                capture = WindowsHardwareBrightness.Capture(target, sid, token, directVcp: true);
                if (capture.Targets.Count == 0 || capture.Unknown != 0) throw new IOException("目标亮度未知；本次未修改。");
                result = WindowsBrightnessWheelGesture.RunCaptured(capture, step, smoothMilliseconds, writesPerSecond, fromSteps, latestSteps, token);
            }
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
            { result = result with { Reason = "亮度手势已停止或未确认；没有补写或恢复旧值。" }; }
            finally
            {
                if (capture is not null) foreach (var item in capture.Targets) item.Transaction.Relinquish();
                try { capture?.Dispose(); }
                catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
                { BrightnessNativeOwnership.FailCleanup(); result = result with { Success = false, Reason = "原生资源清理未确认；已禁止后续亮度准入，请正常重启后台后重新核验。" }; }
                ownership?.Dispose(); completion.TrySetResult(result);
            }
        }) { IsBackground = true, Name = "Axiom authorized brightness wheel" }.Start();
        return completion.Task;
    }
    public static Task BeginManualTakeover()
    {
        var native = BrightnessNativeOwnership.BeginManual();
        return Task.WhenAll(native, Commands.DrainAsync());
    }
    public static async Task EndManualTakeoverAsync()
    {
        await DrainAsync(); BrightnessNativeOwnership.EndManual();
    }
    private static readonly BoundedNativeCommand Commands = new("硬件屏幕亮度", TimeSpan.FromSeconds(10));
    public static Task DrainAsync() => Task.WhenAll(Commands.DrainAsync(), BrightnessNativeOwnership.DrainAsync()).WaitAsync(TimeSpan.FromSeconds(30));
    public static Task<(bool Success, string Reason)> SetAsync(string target, int percent, string sid, CancellationToken token, BrightnessFadeOptions fade = default)
    {
        if (target is not ("primary" or "all") || percent is < 0 or > 100) throw new ArgumentException("硬件亮度参数无效。");
        return RunSingleAsync(cancellation => SetOwned(target, percent, sid, cancellation, fade), token, fade);
    }
    public static Task<(bool Success, string Reason)> AdjustAsync(string target, string operation, int step, string sid, CancellationToken token, BrightnessFadeOptions fade = default)
    {
        if (target is not ("primary" or "all") || operation is not ("increase" or "decrease") || step is < 1 or > 100) throw new ArgumentException("相对亮度参数无效。");
        return RunSingleAsync(cancellation => AdjustOwned(target, operation, step, sid, cancellation, fade), token, fade);
    }
    // The caller already holds BrightnessNativeOwnership and its command admission.
    internal static (bool Success, string Reason) SetOwned(string target, int percent, string sid, CancellationToken token, BrightnessFadeOptions fade = default) =>
        HardwareBrightnessOperation.Apply(new Api(sid, token, fade.Enabled), target, percent, token, fade);
    internal static (bool Success, string Reason) AdjustOwned(string target, string operation, int step, string sid, CancellationToken token, BrightnessFadeOptions fade = default) =>
        HardwareBrightnessOperation.Adjust(new Api(sid, token, fade.Enabled), target, operation, step, token, fade);
    private static Task<(bool Success, string Reason)> RunSingleAsync(Func<CancellationToken, (bool Success, string Reason)> action, CancellationToken token, BrightnessFadeOptions fade) =>
        Commands.RunAsync(cancellation =>
        {
            using var ownership = BrightnessNativeOwnership.TryAcquire();
            return ownership is null ? (false, "Axiom已有亮度恢复租约或原生操作；请先暂停已有亮度恢复任务并等待释放完成，再执行其他亮度动作。本次未执行，不排队。") : action(cancellation);
        }, token, operationTimeout: fade.OperationTimeout);
    private sealed class Api(string sid, CancellationToken token, bool directVcp = false) : IHardwareBrightnessApi
    {
        private readonly HardwareBrightnessIo io = new(directVcp, monitor =>
        {
            var accepted = GetMonitorBrightness(monitor, out var minimum, out var current, out var maximum);
            var error = accepted ? 0 : Marshal.GetLastPInvokeError();
            return (accepted ? new HardwareBrightnessRange(minimum, current, maximum) : null, error);
        }, monitor =>
        {
            var accepted = GetVCPFeatureAndVCPFeatureReply(monitor, 0x10, out var type, out var current, out var maximum);
            var error = accepted ? 0 : Marshal.GetLastPInvokeError();
            return (accepted ? HardwareBrightnessOperation.VcpRange(type, current, maximum) : null, error);
        }, SetMonitorBrightness, (monitor, value) => SetVCPFeature(monitor, 0x10, value));
        internal void UseToken(CancellationToken value) => token = value;
        public bool IsAuthorized()
        {
            token.ThrowIfCancellationRequested();
            using var identity = WindowsIdentity.GetCurrent();
            if (string.IsNullOrWhiteSpace(sid) || identity.User?.Value != sid || !Environment.UserInteractive) return false;
            using var current = Process.GetCurrentProcess();
            if (current.SessionId <= 0) return false;
            var accepted = WTSQuerySessionInformation(0, current.SessionId, 8, out var allocation, out var size); // WTSConnectState; no account or display names
            try { return accepted && allocation != 0 && size == sizeof(int) && Marshal.ReadInt32(allocation) == 0; } // WTSActive; locked is allowed
            finally { if (allocation != 0) WTSFreeMemory(allocation); }
        }
        public IReadOnlyList<HardwareLogicalMonitor> ReadLogicalMonitors()
        {
            token.ThrowIfCancellationRequested(); var monitors = new List<HardwareLogicalMonitor>(); var unavailable = false;
            if (!EnumDisplayMonitors(0, 0, (monitor, _, _, _) =>
            {
                if (token.IsCancellationRequested || monitors.Count >= 64) { unavailable = true; return false; }
                var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
                if (!GetMonitorInfo(monitor, ref info)) { unavailable = true; return false; }
                monitors.Add(new(monitor, (info.Flags & 1) != 0)); return true;
            }, 0) || unavailable) throw new Win32Exception("无法读取完整显示布局。");
            token.ThrowIfCancellationRequested(); return monitors;
        }
        public IHardwareMonitorLease Acquire(nint logicalMonitor, int remainingCapacity)
        {
            token.ThrowIfCancellationRequested();
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(logicalMonitor, out var count)) throw new Win32Exception(Marshal.GetLastPInvokeError());
            token.ThrowIfCancellationRequested();
            if (count > 8 || count > remainingCapacity) throw new HardwareBrightnessLimitException(); // reject before allocating or opening handles
            var monitors = new PhysicalMonitor[count];
            if (count == 0) return new Lease(monitors);
            // Transfer every returned nonzero handle to a lease before checking cancellation, even on partial native failure.
            var accepted = GetPhysicalMonitorsFromHMONITOR(logicalMonitor, count, monitors);
            var lease = new Lease(monitors);
            if (accepted) return lease;
            var error = Marshal.GetLastPInvokeError();
            lease.Dispose(); throw new Win32Exception(error);
        }
        public uint? Capabilities(nint monitor)
        {
            token.ThrowIfCancellationRequested();
            return GetMonitorCapabilities(monitor, out var capabilities, out _) ? capabilities : null;
        }
        public HardwareBrightnessRange? Read(nint monitor) => io.Read(monitor, token);
        public bool Set(nint monitor, uint brightness)
        {
            token.ThrowIfCancellationRequested(); return io.Write(monitor, brightness);
        }
    }
    private sealed class Lease(PhysicalMonitor[] monitors) : IHardwareMonitorLease
    {
        private bool disposed;
        public IReadOnlyList<nint> Handles { get; } = monitors.Select(m => m.Handle).ToArray();
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            var owned = monitors.Where(m => m.Handle != 0).ToArray();
            if (owned.Length > 0 && !DestroyPhysicalMonitors((uint)owned.Length, owned))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "物理屏幕句柄释放未确认。");
        }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)] private struct PhysicalMonitor
    { public nint Handle; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public uint Size; public Rect Monitor, Work; public uint Flags; }
    private delegate bool MonitorCallback(nint monitor, nint dc, nint rect, nint data);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("dxva2.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(nint monitor, out uint count);
    [DllImport("dxva2.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetPhysicalMonitorsFromHMONITOR(nint monitor, uint count, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] PhysicalMonitor[] monitors);
    [DllImport("dxva2.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyPhysicalMonitors(uint count, [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] PhysicalMonitor[] monitors);
    [DllImport("dxva2.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorCapabilities(nint monitor, out uint capabilities, out uint temperatures);
    [DllImport("dxva2.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorBrightness(nint monitor, out uint minimum, out uint current, out uint maximum);
    [DllImport("dxva2.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetMonitorBrightness(nint monitor, uint brightness);
    [DllImport("dxva2.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetVCPFeatureAndVCPFeatureReply(nint monitor, byte code, out int type, out uint current, out uint maximum);
    [DllImport("dxva2.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetVCPFeature(nint monitor, byte code, uint value);
    [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WTSQuerySessionInformation(nint server, int session, int information, out nint allocation, out int size);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(nint allocation);
}
