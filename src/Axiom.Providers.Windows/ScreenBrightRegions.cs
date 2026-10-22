namespace Axiom.Providers.Windows;

public sealed record ScreenBrightRegionMetrics(double Score, double LargestAreaPercent, double LuminancePercent, double Contrast);

// Fixed-size, reusable scratch space. Only a quantized luminance grid reaches
// the CPU; no full-resolution image, OCR, model or per-frame scratch allocation.
public sealed class ScreenBrightRegions
{
    public const int Width = 64, Height = 36, Count = Width * Height;
    private readonly byte[] visited = new byte[Count];
    private readonly int[] queue = new int[Count];

    public ScreenBrightRegionMetrics Analyze(ReadOnlySpan<uint> luminance)
    {
        if (luminance.Length != Count) throw new ArgumentException("亮度网格尺寸无效。");
        foreach (var value in luminance) if (value > 255) throw new ArgumentException("亮度网格数据无效。");
        Array.Clear(visited);
        double score = 0, largest = 0, largestMean = 0, largestContrast = 1;
        for (var seed = 0; seed < Count; seed++)
        {
            if (visited[seed] != 0 || luminance[seed] < 153) continue; // linear luminance >= 0.60
            var head = 0; var tail = 1; queue[0] = seed; visited[seed] = 1;
            var left = Width; var right = 0; var top = Height; var bottom = 0; double sum = 0;
            while (head < tail)
            {
                var index = queue[head++]; var x = index % Width; var y = index / Width;
                left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                sum += luminance[index];
                for (var d = 0; d < 4; d++)
                {
                    var nx = x + (d == 0 ? -1 : d == 1 ? 1 : 0);
                    var ny = y + (d == 2 ? -1 : d == 3 ? 1 : 0);
                    if (nx < 0 || nx >= Width || ny < 0 || ny >= Height) continue;
                    var next = ny * Width + nx;
                    if (visited[next] != 0 || luminance[next] < 153) continue;
                    visited[next] = 1; queue[tail++] = next;
                }
            }
            // Small islands and thin, sparse structures do not behave like a panel.
            if (right - left < 2 || bottom - top < 2 || tail < (Count + 49) / 50 || tail < (right - left + 1) * (bottom - top + 1) * 0.55) continue;
            double surround = 0; var surroundCount = 0;
            for (var y = Math.Max(0, top - 2); y <= Math.Min(Height - 1, bottom + 2); y++)
                for (var x = Math.Max(0, left - 2); x <= Math.Min(Width - 1, right + 2); x++)
                {
                    if (x >= left && x <= right && y >= top && y <= bottom) continue;
                    surround += luminance[y * Width + x]; surroundCount++;
                }
            var mean = sum / tail / 255;
            var contrast = surroundCount == 0 ? 1 : (mean + 0.05) / (surround / surroundCount / 255 + 0.05);
            var area = tail * 100.0 / Count;
            var intensity = Math.Clamp((mean * 100 - 55) / 45, 0, 1);
            var contrastWeight = 1 + 0.35 * Math.Clamp((contrast - 1) / 4, 0, 1);
            score += area * intensity * contrastWeight;
            if (area > largest) { largest = area; largestMean = mean * 100; largestContrast = contrast; }
        }
        return new(Math.Min(100, score), largest, largestMean, largestContrast);
    }
}
