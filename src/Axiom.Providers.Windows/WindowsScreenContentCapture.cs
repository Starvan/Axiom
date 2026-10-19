using System.Runtime.InteropServices;
using System.Threading.Channels;
namespace Axiom.Providers.Windows;
// Frames remain GPU-only. One compute group samples 64x36 points and the CPU
// reads only one uint. No desktop texture is mapped, encoded, saved, or logged.
public sealed unsafe class WindowsScreenContentCapture : IDisposable
{
    private static readonly object SharedGate = new();
    // Serialize native creation/last release without holding the publish lock
    // while joining the capture worker (its final callback needs SharedGate).
    private static readonly object LifecycleGate = new();
    private static SharedCapture? shared;
    private static int activeCaptures;
    public static int ActiveCaptures => Volatile.Read(ref activeCaptures);
    public static int ActiveConsumers { get { lock (SharedGate) return shared?.Clients.Count ?? 0; } }
    private readonly Action<ScreenContentReading> publish;
    private readonly CancellationTokenSource stop = new();
    private readonly Thread? worker;
    private nint device, context, shader, result, staging, uav, duplication;
    private bool held, counted;
    private int disposed;
    public int AcquiredFrames, PointerOnlyFrames, TimeoutCount;
    internal WindowsScreenContentCapture(Action<ScreenContentReading> publish)
    {
        this.publish = publish;
        try { OpenPrimary(); CreateReduction(); Interlocked.Increment(ref activeCaptures); counted = true; }
        catch { Cleanup(); stop.Dispose(); throw; }
        worker = new Thread(Run) { IsBackground = true, Name = "Axiom screen content GPU sample" }; worker.Start();
    }
    private void OpenPrimary()
    {
        var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); Check(CreateDXGIFactory1(ref iid, out var factory));
        var monitor = MonitorFromPoint(default, 1);
        try
        {
            byte* desc = stackalloc byte[160];
            for (uint a = 0; a < 16; a++)
            {
                nint adapter = 0;
                var hr = ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(factory, 12))(factory, a, &adapter);
                if (hr == unchecked((int)0x887A0002)) break; Check(hr);
                try
                {
                    for (uint i = 0; i < 64; i++)
                    {
                        nint output = 0;
                        hr = ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(adapter, 7))(adapter, i, &output);
                        if (hr == unchecked((int)0x887A0002)) break; Check(hr);
                        try
                        {
                            Check(((delegate* unmanaged[Stdcall]<nint, byte*, int>)Slot(output, 7))(output, desc));
                            if (*(nint*)(desc + 88) != monitor) continue;
                            Check(D3D11CreateDevice(adapter, 0, 0, 0x20, 0, 0, 7, out device, out _, out context));
                            var output1 = Query(output, new("00CDDEA8-939B-4B83-A340-A685226666CC"));
                            try { nint created; Check(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(output1, 22))(output1, device, &created)); duplication = created; }
                            finally { Marshal.Release(output1); }
                            return;
                        }
                        finally { Marshal.Release(output); }
                    }
                }
                finally { Marshal.Release(adapter); }
            }
            throw new IOException("无法定位主屏显示输出。");
        }
        finally { Marshal.Release(factory); }
    }
    private void Run()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                if (held) { Check(((delegate* unmanaged[Stdcall]<nint, int>)Slot(duplication, 14))(duplication)); held = false; }
                FrameInfo info; nint resource = 0;
                // DXGI's wait cannot be cancelled; a finite one-second wait is
                // only a cancellation checkpoint, not an unchanged-frame sample.
                var hr = ((delegate* unmanaged[Stdcall]<nint, uint, FrameInfo*, nint*, int>)Slot(duplication, 8))(duplication, 1000, &info, &resource);
                if (hr == unchecked((int)0x887A0027)) { TimeoutCount++; continue; }
                Check(hr); held = true; AcquiredFrames++;
                try
                {
                    if (info.ProtectedContentMaskedOut != 0) publish(new(null, "受保护画面被系统遮蔽，无法计算明亮面积。"));
                    else if (info.LastPresentTime != 0)
                    {
                        var texture = Query(resource, new("6F15AAF2-D208-4E89-9AB4-489535D34F9C"));
                        try { publish(new(ReadArea(texture))); } finally { Marshal.Release(texture); }
                    }
                    else PointerOnlyFrames++;
                }
                finally { if (resource != 0) Marshal.Release(resource); }
                // Keep ownership while idle, per ReleaseFrame's OS-copy guidance.
                // Pointer-only frames have the same cap and do not run the shader.
                stop.Token.WaitHandle.WaitOne(250);
            }
        }
        catch (Exception) { if (!stop.IsCancellationRequested) publish(new(null, "主屏捕获或GPU统计已失效，等待显示或会话恢复通知后重建。", CaptureLost: true)); }
        finally { Cleanup(); }
    }
    private double ReadArea(nint texture)
    {
        nint srv = 0;
        try
        {
            Check(((delegate* unmanaged[Stdcall]<nint, nint, nint, nint*, int>)Slot(device, 7))(device, texture, 0, &srv));
            var output = uav;
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)Slot(context, 67))(context, 0, 1, &srv);
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, nint, void>)Slot(context, 68))(context, 0, 1, &output, 0);
            ((delegate* unmanaged[Stdcall]<nint, nint, nint, uint, void>)Slot(context, 69))(context, shader, 0, 0);
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, uint, void>)Slot(context, 41))(context, 1, 1, 1);
            nint empty = 0;
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)Slot(context, 67))(context, 0, 1, &empty);
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, nint, void>)Slot(context, 68))(context, 0, 1, &empty, 0);
            ((delegate* unmanaged[Stdcall]<nint, nint, nint, void>)Slot(context, 47))(context, staging, result);
            Mapped mapped;
            Check(((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, Mapped*, int>)Slot(context, 14))(context, staging, 0, 1, 0, &mapped));
            try { return *(uint*)mapped.Data * 100.0 / (64 * 36); }
            finally { ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)Slot(context, 15))(context, staging, 0); }
        }
        finally { Release(ref srv); }
    }
    private void CreateReduction()
    {
        const string source = """
            Texture2D<float4> image : register(t0);
            RWStructuredBuffer<uint> result : register(u0);
            groupshared uint counts[64];
            [numthreads(64,1,1)] void main(uint3 id : SV_GroupThreadID) {
                uint w,h; image.GetDimensions(w,h); uint n=0;
                for(uint y=0;y<36;y++) {
                    uint2 p=uint2((id.x+0.5)*w/64.0,(y+0.5)*h/36.0);
                    float3 rgb=image.Load(int3(p,0)).rgb;
                    n += dot(rgb,float3(0.2126,0.7152,0.0722)) >= 0.7 ? 1 : 0;
                }
                counts[id.x]=n; GroupMemoryBarrierWithGroupSync();
                if(id.x==0) { uint sum=0; for(uint i=0;i<64;i++) sum+=counts[i]; result[0]=sum; }
            }
            """;
        var bytes = System.Text.Encoding.ASCII.GetBytes(source);
        var compiled = D3DCompile(bytes, (nuint)bytes.Length, null, 0, 0, "main", "cs_5_0", 0x8000, 0, out var code, out var errors);
        try
        {
            Check(compiled);
            var data = ((delegate* unmanaged[Stdcall]<nint, nint>)Slot(code, 3))(code);
            var length = ((delegate* unmanaged[Stdcall]<nint, nuint>)Slot(code, 4))(code);
            nint created;
            Check(((delegate* unmanaged[Stdcall]<nint, nint, nuint, nint, nint*, int>)Slot(device, 18))(device, data, length, 0, &created)); shader = created;
        }
        finally { if (code != 0) Marshal.Release(code); if (errors != 0) Marshal.Release(errors); }
        var desc = new BufferDesc { ByteWidth = 4, BindFlags = 0x80, MiscFlags = 0x40, StructureByteStride = 4 };
        nint buffer;
        Check(((delegate* unmanaged[Stdcall]<nint, BufferDesc*, nint, nint*, int>)Slot(device, 3))(device, &desc, 0, &buffer)); result = buffer;
        nint view;
        Check(((delegate* unmanaged[Stdcall]<nint, nint, nint, nint*, int>)Slot(device, 8))(device, result, 0, &view)); uav = view;
        desc = new BufferDesc { ByteWidth = 4, Usage = 3, CpuAccessFlags = 0x20000 };
        Check(((delegate* unmanaged[Stdcall]<nint, BufferDesc*, nint, nint*, int>)Slot(device, 3))(device, &desc, 0, &buffer)); staging = buffer;
    }

    public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) != 0) return; stop.Cancel(); worker?.Join(); if (worker is null) Cleanup(); stop.Dispose(); }
    public static IScreenContentSource Subscribe()
    {
        lock (LifecycleGate)
            lock (SharedGate)
            {
                if (shared is null || !shared.Healthy) shared = new SharedCapture();
                var lease = new SharedLease(shared); shared.Clients.Add(lease);
                if (shared.Last is { } reading) lease.Publish(reading);
                return lease;
            }
    }
    private sealed class SharedCapture
    {
        internal readonly HashSet<SharedLease> Clients = [];
        internal readonly WindowsScreenContentCapture Native;
        internal ScreenContentReading? Last;
        internal bool Healthy = true;
        internal SharedCapture() { Native = new(Publish); }
        private void Publish(ScreenContentReading reading) { lock (SharedGate) { Last = reading; if (reading.CaptureLost) Healthy = false; foreach (var client in Clients) client.Publish(reading); } }
    }
    private sealed class SharedLease(SharedCapture owner) : IScreenContentSource
    {
        private readonly Channel<ScreenContentReading> readings = Channel.CreateBounded<ScreenContentReading>(64);
        public ChannelReader<ScreenContentReading> Readings => readings.Reader;
        internal void Publish(ScreenContentReading reading) { if (!readings.Writer.TryWrite(reading)) readings.Writer.TryComplete(new IOException("画面统计队列已满。")); }
        public void Dispose()
        {
            lock (LifecycleGate)
            {
                bool last;
                lock (SharedGate) { if (!owner.Clients.Remove(this)) return; readings.Writer.TryComplete(); last = owner.Clients.Count == 0; if (last && ReferenceEquals(shared, owner)) shared = null; }
                if (last) owner.Native.Dispose();
            }
        }
    }
    // Validate the real GPU path with synthetic input, never desktop pixels.
    internal static double MeasureSynthetic(int width, int height, ReadOnlySpan<byte> bgra)
    {
        if (width < 1 || height < 1 || bgra.Length != checked(width * height * 4)) throw new ArgumentException("Synthetic texture size mismatch.");
        using var native = new WindowsScreenContentCapture();
        Check(D3D11CreateDevice(0, 1, 0, 0x20, 0, 0, 7, out native.device, out _, out native.context)); native.CreateReduction();
        var desc = new TextureDesc { Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1, Format = 87, SampleCount = 1, BindFlags = 8 };
        nint texture = 0;
        fixed (byte* pixels = bgra)
        {
            var data = new TextureData { Memory = (nint)pixels, Pitch = (uint)width * 4 };
            Check(((delegate* unmanaged[Stdcall]<nint, TextureDesc*, TextureData*, nint*, int>)Slot(native.device, 5))(native.device, &desc, &data, &texture));
        }
        try { return native.ReadArea(texture); } finally { Marshal.Release(texture); }
    }
    private WindowsScreenContentCapture() { publish = _ => { }; }
    private void Cleanup()
    {
        if (held && duplication != 0) { ((delegate* unmanaged[Stdcall]<nint, int>)Slot(duplication, 14))(duplication); held = false; }
        if (counted) { counted = false; Interlocked.Decrement(ref activeCaptures); }
        Release(ref duplication); Release(ref uav); Release(ref staging); Release(ref result); Release(ref shader); Release(ref context); Release(ref device);
    }
    private static nint Slot(nint value, int index) => Marshal.ReadIntPtr(Marshal.ReadIntPtr(value), index * IntPtr.Size);
    private static nint Query(nint value, Guid iid) { Check(Marshal.QueryInterface(value, in iid, out var result)); return result; }
    private static void Check(int hr) => Marshal.ThrowExceptionForHR(hr);
    private static void Release(ref nint value) { if (value != 0) { Marshal.Release(value); value = 0; } }
    [StructLayout(LayoutKind.Sequential)] private struct TextureDesc { public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccessFlags, MiscFlags; }
    [StructLayout(LayoutKind.Sequential)] private struct TextureData { public nint Memory; public uint Pitch, SlicePitch; }
    [StructLayout(LayoutKind.Sequential)] private struct BufferDesc { public uint ByteWidth, Usage, BindFlags, CpuAccessFlags, MiscFlags, StructureByteStride; }
    [StructLayout(LayoutKind.Sequential)] private struct Mapped { public nint Data; public uint RowPitch, DepthPitch; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct FrameInfo { public long LastPresentTime, LastMouseUpdateTime; public uint AccumulatedFrames; public int RectsCoalesced, ProtectedContentMaskedOut; public Point PointerPosition; public int PointerVisible; public uint TotalMetadataBufferSize, PointerShapeBufferSize; }
    [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory1(ref Guid iid, out nint factory);
    [DllImport("d3d11.dll")] private static extern int D3D11CreateDevice(nint adapter, uint type, nint software, uint flags, nint levels, uint count, uint sdk, out nint device, out uint level, out nint context);
    [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi)] private static extern int D3DCompile(byte[] source, nuint size, string? name, nint defines, nint include, string entry, string target, uint flags, uint flags2, out nint code, out nint errors);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(Point point, uint flags);
}
