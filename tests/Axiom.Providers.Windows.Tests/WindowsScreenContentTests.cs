using Axiom.Providers.Windows;

namespace Axiom.Providers.Windows.Tests;

// Local desktop/GPU validation, deliberately excluded from cross-platform CI.
public sealed class WindowsScreenContentTests
{
    [Fact(Timeout = 15000)]
    public async Task ActualGpuRegionAnalysisLinearizesColorsAndDistinguishesPanelFromDots()
    {
        await Task.Yield(); Assert.True(OperatingSystem.IsWindows());
        byte[] Image(byte r, byte g, byte b, bool dots = false)
        {
            var pixels = new byte[64 * 36 * 4];
            for (var y = 0; y < 36; y++) for (var x = 0; x < 64; x++)
            {
                var i = (y * 64 + x) * 4; pixels[i + 3] = 255;
                if (dots ? y % 2 == 0 && x % 2 == 0 : x >= 16 && x < 48 && y >= 9 && y < 27)
                { pixels[i] = b; pixels[i + 1] = g; pixels[i + 2] = r; }
            }
            return pixels;
        }
        var panel = WindowsScreenContentCapture.MeasureSyntheticRegions(64, 36, Image(255, 255, 255));
        Assert.True(panel.Score >= 20); Assert.Equal(25, panel.LargestAreaPercent);
        Assert.Equal(0, WindowsScreenContentCapture.MeasureSyntheticRegions(64, 36, Image(255, 255, 255, true)).Score);
        Assert.Equal(0, WindowsScreenContentCapture.MeasureSyntheticRegions(64, 36, Image(200, 200, 200)).Score);
        Assert.True(WindowsScreenContentCapture.MeasureSyntheticRegions(64, 36, Image(255, 255, 0)).Score >= 20);
    }

    [Fact(Timeout = 15000)]
    public async Task ActualGpuCountsWhiteBlackGrayAndColoredAreasRatherThanWhiteEqualityOrAConstant()
    {
        await Task.Yield();
        Assert.True(OperatingSystem.IsWindows());
        foreach (var (r, g, b, expected) in new (byte, byte, byte, double)[] { (255,255,255,100), (0,0,0,0), (200,200,200,100), (100,100,100,0), (0,255,0,100), (255,0,0,0), (255,255,0,100), (0,0,255,0) })
            Assert.Equal(expected, WindowsScreenContentCapture.MeasureSynthetic(1, 1, [b,g,r,255]));
        var pixels = new byte[64 * 36 * 4];
        for (var i = 0; i < 64 * 36; i++) { var offset = i * 4; pixels[offset + 3] = 255; if (i % 64 < 48) { pixels[offset + 1] = 255; pixels[offset + 2] = 255; } }
        Assert.Equal(75, WindowsScreenContentCapture.MeasureSynthetic(64, 36, pixels));
    }

    [Fact(Timeout = 15000)]
    public async Task RealAdvancedColorRegistrationCanRebindAndDisposeItsDispatcherWithoutChangingHdr()
    {
        Assert.True(OperatingSystem.IsWindows());
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var source = new PrimaryColorEvents(() => changed.TrySetResult(), e => failed.TrySetResult(e)))
        {
            source.Refresh();
            await Task.WhenAny(changed.Task, failed.Task).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(failed.Task.IsCompleted); Assert.True(changed.Task.IsCompleted);
        }
        using var next = new PrimaryColorEvents(() => { }, e => failed.TrySetResult(e));
        Assert.False(failed.Task.IsCompleted);
    }
}
