using CPURacer.Capture;

namespace CPURacer.Tests;

public class HeightFieldExtractorTests
{
    [Fact]
    public void Extract_FlatAccentBand_YieldsNearConstantHeight()
    {
        const int w = 200;
        const int h = 120;
        var inset = new PlotInset(10, 20, 10, 20);
        const int lineY = 70;
        var bgra = MakeFrame(w, h, (x, y) =>
        {
            if (y >= lineY && x >= inset.Left && x < w - inset.Right
                && y >= inset.Top && y < h - inset.Bottom)
            {
                return (240, 140, 50); // B,G,R Taskmgr-like blue
            }

            return (20, 20, 20);
        });

        var ext = new HeightFieldExtractor(inset, smoothRadius: 1);
        var field = ext.ExtractBgra(w, h, bgra);
        Assert.NotNull(field);
        Assert.Equal(inset.ContentWidth(w), field!.PlotWidth);

        var mid = field.YFromTop[field.PlotWidth / 2];
        Assert.InRange(mid, lineY - 2, lineY + 2);

        var min = field.YFromTop.Min();
        var max = field.YFromTop.Max();
        Assert.True(max - min < 4, $"flat band should be stable, span={max - min}");
    }

    [Fact]
    public void Extract_PeakInMiddle_HasLowerYAtCenter()
    {
        const int w = 180;
        const int h = 100;
        var inset = new PlotInset(5, 10, 5, 10);
        var bgra = MakeFrame(w, h, (x, y) =>
        {
            var plotX = x - inset.Left;
            var plotW = inset.ContentWidth(w);
            var t = plotW <= 1 ? 0 : Math.Abs(plotX - plotW / 2.0) / (plotW / 2.0);
            var lineY = inset.Top + (int)(10 + t * 50);
            if (y >= lineY && x >= inset.Left && x < w - inset.Right
                && y >= inset.Top && y < h - inset.Bottom)
            {
                return (230, 150, 40);
            }

            return (18, 18, 18);
        });

        var ext = new HeightFieldExtractor(inset, smoothRadius: 0);
        var field = ext.ExtractBgra(w, h, bgra);
        Assert.NotNull(field);

        var center = field!.YFromTop[field.PlotWidth / 2];
        var edge = field.YFromTop[2];
        Assert.True(center < edge - 8, $"center Y {center} should be above edge Y {edge}");
    }

    [Fact]
    public void Extract_IgnoresCyanStrokeAboveFill()
    {
        const int w = 160;
        const int h = 100;
        var inset = new PlotInset(8, 12, 8, 12);
        const int fillTop = 72;
        const int fakeStrokeY = 55;
        var bgra = MakeFrame(w, h, (x, y) =>
        {
            if (x < inset.Left || x >= w - inset.Right || y < inset.Top || y >= h - inset.Bottom)
            {
                return (18, 18, 18);
            }

            if (y == fakeStrokeY)
            {
                return (200, 220, 40);
            }

            if (y >= fillTop)
            {
                return (240, 140, 50);
            }

            return (18, 18, 18);
        });

        var ext = new HeightFieldExtractor(inset, smoothRadius: 0);
        var field = ext.ExtractBgra(w, h, bgra);
        Assert.NotNull(field);
        var mid = field!.YFromTop[field.PlotWidth / 2];
        Assert.InRange(mid, fillTop - 2, fillTop + 2);
    }

    [Fact]
    public void Extract_PrefersBrightStrokeOverFaintWash()
    {
        const int w = 160;
        const int h = 110;
        var inset = new PlotInset(8, 10, 8, 10);
        const int strokeY = 75;
        const int washTop = 45;
        var bgra = MakeFrame(w, h, (x, y) =>
        {
            if (x < inset.Left || x >= w - inset.Right || y < inset.Top || y >= h - inset.Bottom)
            {
                return (30, 30, 30);
            }

            if (y >= washTop && y < strokeY)
            {
                return (90, 70, 55);
            }

            if (y == strokeY || y == strokeY + 1)
            {
                return (245, 150, 45);
            }

            if (y > strokeY)
            {
                return (200, 120, 40);
            }

            return (30, 30, 30);
        });

        var ext = new HeightFieldExtractor(inset, smoothRadius: 0);
        var field = ext.ExtractBgra(w, h, bgra);
        Assert.NotNull(field);
        var mid = field!.YFromTop[field.PlotWidth / 2];
        Assert.InRange(mid, strokeY - 2, strokeY + 2);
    }

    [Fact]
    public void Extract_StrokeCentroid_BelowAaHalo()
    {
        const int w = 160;
        const int h = 110;
        var inset = new PlotInset(8, 10, 8, 10);
        const int haloY = 70;
        const int strokeY0 = 74;
        const int strokeY1 = 75;
        var bgra = MakeFrame(w, h, (x, y) =>
        {
            if (x < inset.Left || x >= w - inset.Right || y < inset.Top || y >= h - inset.Bottom)
            {
                return (28, 28, 28);
            }

            // Weaker AA fringe above the true stroke (old topmost-threshold floated here).
            if (y == haloY || y == haloY + 1)
            {
                return (160, 110, 70);
            }

            if (y == strokeY0 || y == strokeY1)
            {
                return (250, 145, 40);
            }

            if (y > strokeY1)
            {
                return (190, 115, 45);
            }

            return (28, 28, 28);
        });

        var ext = new HeightFieldExtractor(inset, smoothRadius: 0);
        var field = ext.ExtractBgra(w, h, bgra);
        Assert.NotNull(field);
        var mid = field!.YFromTop[field.PlotWidth / 2];
        Assert.InRange(mid, strokeY0 - 0.5f, strokeY1 + 0.5f);
        Assert.True(mid > haloY + 1.5f, $"should sit below AA halo, mid={mid}");
    }

    [Fact]
    public void AccentScore_RejectsGrayGridAndOrangeDebug()
    {
        var ext = new HeightFieldExtractor();
        // 先用蓝色帧自举出蓝色目标，使 AccentScore 进入目标色准入分支。
        _ = ext.ExtractBgra(160, 100, MakeFrame(160, 100, (_, _) => (240, 140, 50)));

        Assert.Equal(0, ext.AccentScore(80, 80, 80));
        Assert.Equal(0, ext.AccentScore(70, 75, 90)); // gray-blue grid-ish
        Assert.Equal(0, ext.AccentScore(0, 140, 255)); // orange debug (B,G,R)
        Assert.True(ext.AccentScore(240, 140, 50) >= 90);
    }

    [Fact]
    public void Extract_GreenCurve_YieldsGreenAccentAndCorrectRidge()
    {
        const int w = 180;
        const int h = 100;
        var inset = new PlotInset(5, 10, 5, 10);
        var bgra = MakeFrame(w, h, (x, y) =>
        {
            var plotX = x - inset.Left;
            var plotW = inset.ContentWidth(w);
            var t = plotW <= 1 ? 0 : Math.Abs(plotX - plotW / 2.0) / (plotW / 2.0);
            var lineY = inset.Top + (int)(10 + t * 50);
            if (y >= lineY && x >= inset.Left && x < w - inset.Right
                && y >= inset.Top && y < h - inset.Bottom)
            {
                return (60, 220, 50); // B,G,R green stroke/fill
            }

            return (18, 18, 18);
        });

        var field = new HeightFieldExtractor(inset, smoothRadius: 0).ExtractBgra(w, h, bgra);
        Assert.NotNull(field);
        var center = field!.YFromTop[field.PlotWidth / 2];
        var edge = field.YFromTop[2];
        Assert.True(center < edge - 8, $"center Y {center} should be above edge Y {edge}");
        Assert.True(
            field.AccentG > field.AccentB && field.AccentG > field.AccentR,
            $"accent should be green-dominant, got B={field.AccentB} G={field.AccentG} R={field.AccentR}");
    }

    [Fact]
    public void Extract_OrangeCurve_YieldsOrangeAccentAndCorrectRidge()
    {
        const int w = 180;
        const int h = 100;
        var inset = new PlotInset(5, 10, 5, 10);
        var bgra = MakeFrame(w, h, (x, y) =>
        {
            var plotX = x - inset.Left;
            var plotW = inset.ContentWidth(w);
            var t = plotW <= 1 ? 0 : Math.Abs(plotX - plotW / 2.0) / (plotW / 2.0);
            var lineY = inset.Top + (int)(10 + t * 50);
            if (y >= lineY && x >= inset.Left && x < w - inset.Right
                && y >= inset.Top && y < h - inset.Bottom)
            {
                return (50, 120, 220); // B,G,R orange stroke/fill
            }

            return (18, 18, 18);
        });

        var field = new HeightFieldExtractor(inset, smoothRadius: 0).ExtractBgra(w, h, bgra);
        Assert.NotNull(field);
        var center = field!.YFromTop[field.PlotWidth / 2];
        var edge = field.YFromTop[2];
        Assert.True(center < edge - 8, $"center Y {center} should be above edge Y {edge}");
        Assert.True(
            field.AccentR > field.AccentG && field.AccentR > field.AccentB,
            $"accent should be orange-dominant, got B={field.AccentB} G={field.AccentG} R={field.AccentR}");
    }

    [Fact]
    public void Extract_RecoversAfterAccentThemeSwitch()
    {
        // 先蓝色自举，再给绿色帧：目标色拒绝绿色 → 连续失败 → 重置自举 → 学回绿色。
        const int w = 160;
        const int h = 100;
        var inset = new PlotInset(5, 8, 5, 8);
        var blue = MakeFrame(w, h, (_, _) => (240, 140, 50));
        var green = MakeFrame(w, h, (_, _) => (60, 220, 50));
        var ext = new HeightFieldExtractor(inset);

        Assert.NotNull(ext.ExtractBgra(w, h, blue));
        for (var i = 0; i < 35; i++)
        {
            _ = ext.ExtractBgra(w, h, green);
        }

        var field = ext.ExtractBgra(w, h, green);
        Assert.NotNull(field);
        Assert.True(
            field!.AccentG > field.AccentB && field.AccentG > field.AccentR,
            $"accent should re-learn green, got B={field.AccentB} G={field.AccentG} R={field.AccentR}");
    }

    [Fact]
    public void Extract_KeepsNarrowValleyBetweenPeaks()
    {
        // Median(r=3) used to bridge peaks into a high plateau across the valley.
        const int w = 220;
        const int h = 100;
        var inset = new PlotInset(5, 8, 5, 8);
        const int peakY = 30;
        const int valleyY = 70;
        var bgra = MakeFrame(w, h, (x, y) =>
        {
            if (x < inset.Left || x >= w - inset.Right || y < inset.Top || y >= h - inset.Bottom)
            {
                return (18, 18, 18);
            }

            var plotX = x - inset.Left;
            var plotW = inset.ContentWidth(w);
            var t = plotW <= 1 ? 0.5 : plotX / (double)(plotW - 1);
            // Two peaks with a deep valley in the middle.
            var lineY = t is > 0.35 and < 0.65
                ? valleyY
                : peakY;
            if (y >= lineY)
            {
                return (240, 140, 50);
            }

            return (18, 18, 18);
        });

        var ext = new HeightFieldExtractor(inset, smoothRadius: 0);
        var field = ext.ExtractBgra(w, h, bgra);
        Assert.NotNull(field);
        var mid = field!.YFromTop[field.PlotWidth / 2];
        var edge = field.YFromTop[4];
        Assert.InRange(edge, peakY - 3, peakY + 3);
        Assert.True(mid > edge + 20, $"valley should stay deep, mid={mid} edge={edge}");
    }

    [Fact]
    public void Extract_InterpolatesMissingColumnsWithoutFallbackPlateau()
    {
        const int w = 200;
        const int h = 100;
        var inset = new PlotInset(5, 8, 5, 8);
        var bgra = MakeFrame(w, h, (x, y) =>
        {
            var plotX = x - inset.Left;
            var missing = plotX is >= 70 and < 90;
            var lineY = 55 + plotX / 20;
            if (!missing && x >= inset.Left && x < w - inset.Right
                && y >= lineY && y >= inset.Top && y < h - inset.Bottom)
            {
                return (240, 140, 50);
            }

            return (18, 18, 18);
        });

        var field = new HeightFieldExtractor(inset, smoothRadius: 0).ExtractBgra(w, h, bgra);
        Assert.NotNull(field);
        var values = field!.YFromTop;
        Assert.DoesNotContain(values, float.IsNaN);
        Assert.True(values[80] > values[65] && values[80] < values[95],
            $"gap should interpolate contour: {values[65]}, {values[80]}, {values[95]}");
    }

    [Fact]
    public void Extract_RejectsFrameWithInsufficientBlueCoverage()
    {
        const int w = 200;
        const int h = 100;
        var inset = new PlotInset(5, 8, 5, 8);
        var bgra = MakeFrame(w, h, (x, y) =>
        {
            // Only a tiny fragment: incomplete composition frame, not a valid contour.
            if (x is >= 10 and < 20 && y >= 70)
            {
                return (240, 140, 50);
            }

            return (18, 18, 18);
        });

        Assert.Null(new HeightFieldExtractor(inset).ExtractBgra(w, h, bgra));
    }

    [Fact]
    public void DefaultInset_TracksLowUtilizationNearGraphBottom()
    {
        const int w = 200;
        const int h = 100;
        const int lineY = 93;
        var bgra = MakeFrame(w, h, (x, y) =>
        {
            if (x >= 4 && x < w - 4 && y >= lineY && y < h - 4)
            {
                return (240, 140, 50);
            }

            return (18, 18, 18);
        });

        var field = new HeightFieldExtractor(smoothRadius: 0).ExtractBgra(w, h, bgra);
        Assert.NotNull(field);
        Assert.InRange(field!.YFromTop[field.PlotWidth / 2], lineY - 2, lineY + 2);
    }

    private static byte[] MakeFrame(int w, int h, Func<int, int, (byte B, byte G, byte R)> pixel)
    {
        var data = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var (b, g, r) = pixel(x, y);
                var i = (y * w + x) * 4;
                data[i] = b;
                data[i + 1] = g;
                data[i + 2] = r;
                data[i + 3] = 255;
            }
        }

        return data;
    }
}
