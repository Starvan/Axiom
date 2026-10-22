using System.Collections.Immutable;
using Axiom.Contracts;
using Axiom.Providers.Windows;

namespace Axiom.Engine.Tests;

public sealed class ScreenBrightRegionsTests
{
    private static uint[] Grid(uint value = 0) => Enumerable.Repeat(value, ScreenBrightRegions.Count).ToArray();
    private static void Panel(uint[] grid, uint value, int left = 16, int top = 9, int width = 32, int height = 18)
    { for (var y = top; y < top + height; y++) for (var x = left; x < left + width; x++) grid[y * 64 + x] = value; }

    [Fact]
    public void EqualBrightCountsDistinguishOnePanelFromScatteredDotsAndThinLines()
    {
        var analyzer = new ScreenBrightRegions(); var panel = Grid(); Panel(panel, 255);
        var dots = Grid(); for (var y = 0; y < 36; y += 2) for (var x = 0; x < 64; x += 2) dots[y * 64 + x] = 255;
        Assert.Equal(panel.Count(v => v == 255), dots.Count(v => v == 255));
        Assert.True(analyzer.Analyze(panel).Score >= 20); Assert.Equal(0, analyzer.Analyze(dots).Score);
        var lines = Grid(); Panel(lines, 255, 0, 0, 64, 1);
        Assert.Equal(0, analyzer.Analyze(lines).Score);
    }

    [Fact]
    public void WhiteDocumentWithTextAndBrightColorsQualifyButModerateGrayAndSmallIconsDoNot()
    {
        var analyzer = new ScreenBrightRegions(); var document = Grid(); Panel(document, 255);
        for (var y = 10; y < 26; y += 3) for (var x = 18; x < 45; x += 3) document[y * 64 + x] = 0;
        Assert.True(analyzer.Analyze(document).Score >= 20);
        var cyan = Grid(); Panel(cyan, 201, 8, 6, 48, 24); // linear Y of saturated cyan is about 0.787
        Assert.True(analyzer.Analyze(cyan).Score >= 20);
        var gray = Grid(115); Assert.Equal(0, analyzer.Analyze(gray).Score);
        var icons = Grid(); Panel(icons, 255, 16, 9, 4, 4); Assert.Equal(0, analyzer.Analyze(icons).Score);
    }

    [Fact]
    public void DarkSurroundIncreasesScoreAndFullWhiteNeedsNoDarkBorder()
    {
        var analyzer = new ScreenBrightRegions(); var dark = Grid(); Panel(dark, 255);
        var light = Grid(140); Panel(light, 255);
        var a = analyzer.Analyze(dark); var b = analyzer.Analyze(light);
        Assert.True(a.Score > b.Score); Assert.True(a.Contrast > b.Contrast); Assert.Equal(25, a.LargestAreaPercent);
        var white = analyzer.Analyze(Grid(255)); Assert.Equal(100, white.Score); Assert.Equal(100, white.LargestAreaPercent);
        Assert.Equal(0, analyzer.Analyze(Grid()).Score); // reused scratch must not retain old regions
        Assert.Throws<ArgumentException>(() => analyzer.Analyze(new uint[1]));
        var invalid = Grid(); invalid[0] = 256; Assert.Throws<ArgumentException>(() => analyzer.Analyze(invalid));
    }

    [Fact]
    public void NewModeIsExplicitAndCanonicalWhileLegacyResourcesAreUnchanged()
    {
        var fields = new Dictionary<string, string> { ["enterPercent"] = "20", ["exitPercent"] = "12", ["stableSeconds"] = "0.5", ["start"] = "", ["end"] = "" }.ToImmutableDictionary();
        var old = ScreenContentCapabilities.Validate(new(ScreenContentCapabilities.Bright, 1, fields));
        Assert.Equal("20:12:0.5@*", old.Resource); Assert.Equal("area", old.Mode);
        var next = ScreenContentCapabilities.Validate(new(ScreenContentCapabilities.Bright, 1, fields.Add("mode", "regions")));
        Assert.Equal("r|20:12:0.5@*", next.Resource); Assert.Equal(next, ScreenContentCapabilities.ParseResource(next.Resource));
        Assert.Equal(old, ScreenContentCapabilities.ParseResource(old.Resource));
        Assert.Throws<ArgumentException>(() => ScreenContentCapabilities.Validate(new(ScreenContentCapabilities.Bright, 1, fields.Add("mode", "unknown"))));
        Assert.Throws<ArgumentException>(() => ScreenContentCapabilities.ParseResource("r|020:12:0.5@*"));
    }

    [Fact]
    public void RegionStateUsesScoreWithExistingStabilityHysteresisAndUnknownProtection()
    {
        var state = new ScreenContentState(20, 12, TimeSpan.FromMilliseconds(500), true);
        var bright = new ScreenContentReading(0, Regions: new(30, 25, 100, 10));
        Assert.Null(state.Read(bright, TimeSpan.Zero).Bright);
        Assert.Null(state.Advance(TimeSpan.FromMilliseconds(499)).Bright);
        Assert.True(state.Advance(TimeSpan.FromMilliseconds(500)).Bright);
        Assert.Contains("评分30.0/100", state.Current.Reason);
        Assert.True(state.Read(bright with { Regions = new(15, 15, 90, 5) }, TimeSpan.FromSeconds(1)).Bright);
        Assert.True(state.Read(bright with { Regions = new(10, 8, 90, 5) }, TimeSpan.FromSeconds(2)).Bright);
        Assert.False(state.Advance(TimeSpan.FromSeconds(2.5)).Bright);
        Assert.Null(state.Read(new(100), TimeSpan.FromSeconds(3)).Bright); // never fall back to area in region mode
        Assert.False(state.OutsideWindow().Bright);
        var legacy = new ScreenContentState(20, 12, TimeSpan.Zero);
        Assert.True(legacy.Read(new(100, Regions: new(0, 0, 0, 1)), TimeSpan.Zero).Bright);
    }
}
