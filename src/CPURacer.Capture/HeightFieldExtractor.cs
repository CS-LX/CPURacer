namespace CPURacer.Capture;

/// <summary>
/// Height field for one frame. YFromTop[i] is the terrain Y in full-frame pixels from the top
/// for plot column i (plot-local x = inset.Left + i).
/// </summary>
public sealed class HeightField
{
    public HeightField(
        int frameWidth,
        int frameHeight,
        PlotInset inset,
        float[] yFromTop,
        byte accentB = 212,
        byte accentG = 120,
        byte accentR = 0)
    {
        FrameWidth = frameWidth;
        FrameHeight = frameHeight;
        Inset = inset;
        YFromTop = yFromTop;
        AccentB = accentB;
        AccentG = accentG;
        AccentR = accentR;
    }

    public int FrameWidth { get; }
    public int FrameHeight { get; }
    public PlotInset Inset { get; }
    public float[] YFromTop { get; }
    public int PlotWidth => YFromTop.Length;

    /// <summary>Sampled Taskmgr stroke color (BGRA channels) for play tinting.</summary>
    public byte AccentB { get; }
    public byte AccentG { get; }
    public byte AccentR { get; }
}

/// <summary>Plot area inset inside the captured chart HWND (physical pixels).</summary>
public readonly record struct PlotInset(int Left, int Top, int Right, int Bottom)
{
    /// <summary>
    /// CvChartWindow ROI is already the graph rectangle. Keep only a small border guard;
    /// a large bottom inset discards the 0–5% utilization valleys.
    /// </summary>
    public static PlotInset DefaultWin11Cpu { get; } = new(4, 4, 4, 4);

    public int ContentWidth(int frameWidth) => Math.Max(1, frameWidth - Left - Right);

    public int ContentHeight(int frameHeight) => Math.Max(1, frameHeight - Top - Bottom);
}

public sealed class HeightFieldExtractor
{
    /// <summary>Minimum accent score to accept a column ridge (rejects gray grid / faint wash).</summary>
    private const int MinAccentScore = 90;

    /// <summary>Only sample brightness around the column peak (stroke), never the whole fill.</summary>
    private const int RidgeHalfWindow = 3;

    /// <summary>Replace a sample only if it is an impulse vs both neighbors (px).</summary>
    private const float ImpulseThresholdPx = 14f;

    /// <summary>Reject incomplete composition frames instead of drawing a fallback plateau.</summary>
    private const float MinReliableCoverage = 0.25f;

    private readonly PlotInset _inset;
    private readonly int _smoothRadius;

    public HeightFieldExtractor(PlotInset? inset = null, int smoothRadius = 1)
    {
        _inset = inset ?? PlotInset.DefaultWin11Cpu;
        _smoothRadius = Math.Max(0, smoothRadius);
    }

    public PlotInset Inset => _inset;

    public HeightField? Extract(CapturedFrame frame)
    {
        var w = frame.Width;
        var h = frame.Height;
        if (w < 16 || h < 16)
        {
            return null;
        }

        var inset = _inset;
        var plotW = inset.ContentWidth(w);
        var plotH = inset.ContentHeight(h);
        if (plotW < 8 || plotH < 8)
        {
            return null;
        }

        var raw = new float[plotW];
        var detected = 0;
        long accentWeight = 0;
        long accentB = 0;
        long accentG = 0;
        long accentR = 0;
        for (var x = 0; x < plotW; x++)
        {
            var fx = inset.Left + x;
            if (TrySampleColumnRidge(
                    frame,
                    fx,
                    inset.Top,
                    inset.Top + plotH - 1,
                    out var y,
                    out var b,
                    out var g,
                    out var r,
                    out var score))
            {
                raw[x] = y;
                detected++;
                accentWeight += score;
                accentB += (long)b * score;
                accentG += (long)g * score;
                accentR += (long)r * score;
            }
            else
            {
                raw[x] = float.NaN;
            }
        }

        // Desktop BitBlt can occasionally observe Taskmgr between composition passes.
        // Never turn such a frame into a fixed 92%-height platform.
        if (detected < Math.Max(4, (int)(plotW * MinReliableCoverage)))
        {
            return null;
        }

        var completed = InterpolateMissing(raw);
        var despike = DespikeImpulses(completed, ImpulseThresholdPx);
        var smooth = Smooth(despike, _smoothRadius);
        byte ab = 212;
        byte ag = 120;
        byte ar = 0;
        if (accentWeight > 0)
        {
            ab = (byte)System.Math.Clamp(accentB / accentWeight, 0, 255);
            ag = (byte)System.Math.Clamp(accentG / accentWeight, 0, 255);
            ar = (byte)System.Math.Clamp(accentR / accentWeight, 0, 255);
        }

        return new HeightField(w, h, inset, smooth, ab, ag, ar);
    }

    /// <summary>Extract from raw BGRA (for tests / fixtures).</summary>
    public HeightField? ExtractBgra(int width, int height, byte[] bgra)
        => Extract(new CapturedFrame(width, height, bgra));

    /// <summary>
    /// Brightest-blue Y near the stroke. Windowed around argmax so dim fill cannot
    /// merge into a wide band and flip bandTop/centroid frame-to-frame.
    /// </summary>
    private static bool TrySampleColumnRidge(
        CapturedFrame frame,
        int x,
        int yTop,
        int yBottom,
        out float ridgeY,
        out byte accentB,
        out byte accentG,
        out byte accentR,
        out int peakScore)
    {
        var rowStride = frame.Stride;
        var maxScore = 0;
        var bestY = yBottom;
        accentB = 0;
        accentG = 0;
        accentR = 0;
        peakScore = 0;

        for (var y = yTop; y <= yBottom; y++)
        {
            var i = y * rowStride + x * 4;
            var score = AccentScore(frame.Bgra[i], frame.Bgra[i + 1], frame.Bgra[i + 2]);
            if (score > maxScore)
            {
                maxScore = score;
                bestY = y;
                accentB = frame.Bgra[i];
                accentG = frame.Bgra[i + 1];
                accentR = frame.Bgra[i + 2];
            }
        }

        if (maxScore < MinAccentScore)
        {
            ridgeY = 0;
            return false;
        }

        peakScore = maxScore;
        var threshold = Math.Max(MinAccentScore, (maxScore * 85) / 100);
        var y0 = Math.Max(yTop, bestY - RidgeHalfWindow);
        var y1 = Math.Min(yBottom, bestY + RidgeHalfWindow);
        long scoreSum = 0;
        long scoreWeightY = 0;
        for (var y = y0; y <= y1; y++)
        {
            var i = y * rowStride + x * 4;
            var score = AccentScore(frame.Bgra[i], frame.Bgra[i + 1], frame.Bgra[i + 2]);
            if (score < threshold)
            {
                continue;
            }

            scoreSum += score;
            scoreWeightY += (long)score * y;
        }

        // The window always contains bestY, whose score is at least threshold.
        ridgeY = (float)scoreWeightY / scoreSum;
        return true;
    }

    /// <summary>
    /// Higher for saturated Taskmgr stroke blues; near-zero for gray grids and faint washes.
    /// </summary>
    public static int AccentScore(byte b, byte g, byte r)
    {
        // Blue-dominant only (rejects orange debug stroke and cyan overlays).
        if (b <= r + 24 || b <= g + 12)
        {
            return 0;
        }

        var max = Math.Max(b, Math.Max(g, r));
        var min = Math.Min(b, Math.Min(g, r));
        var sat = max - min;
        if (sat < 40)
        {
            return 0;
        }

        var chroma = b - Math.Max(r, g);
        // Bright saturated blues score high; dim translucent wash scores low.
        return chroma * 2 + sat + (b / 3);
    }

    /// <summary>
    /// Fill undetected column runs from their neighboring contour samples. This preserves
    /// continuity without inventing a constant fallback altitude.
    /// </summary>
    private static float[] InterpolateMissing(float[] src)
    {
        var dst = (float[])src.Clone();
        var first = 0;
        while (first < dst.Length && float.IsNaN(dst[first]))
        {
            first++;
        }

        for (var i = 0; i < first; i++)
        {
            dst[i] = dst[first];
        }

        var left = first;
        while (left < dst.Length)
        {
            var right = left + 1;
            while (right < dst.Length && float.IsNaN(dst[right]))
            {
                right++;
            }

            if (right >= dst.Length)
            {
                for (var i = left + 1; i < dst.Length; i++)
                {
                    dst[i] = dst[left];
                }

                break;
            }

            var span = right - left;
            for (var i = 1; i < span; i++)
            {
                var t = i / (float)span;
                dst[left + i] = dst[left] + (dst[right] - dst[left]) * t;
            }

            left = right;
        }

        return dst;
    }

    /// <summary>
    /// Fix only isolated spikes (both neighbors agree and this sample disagrees).
    /// Unlike median, preserves real valleys between peaks.
    /// </summary>
    private static float[] DespikeImpulses(float[] src, float thresholdPx)
    {
        if (src.Length < 3)
        {
            return src;
        }

        var dst = (float[])src.Clone();
        for (var i = 1; i < src.Length - 1; i++)
        {
            var left = src[i - 1];
            var right = src[i + 1];
            var self = src[i];
            if (Math.Abs(left - right) > thresholdPx)
            {
                continue;
            }

            if (Math.Abs(self - left) > thresholdPx && Math.Abs(self - right) > thresholdPx)
            {
                dst[i] = (left + right) * 0.5f;
            }
        }

        return dst;
    }

    private static float[] Smooth(float[] src, int radius)
    {
        if (radius <= 0 || src.Length == 0)
        {
            return src;
        }

        var dst = new float[src.Length];
        for (var i = 0; i < src.Length; i++)
        {
            float sum = 0;
            var n = 0;
            var a = Math.Max(0, i - radius);
            var b = Math.Min(src.Length - 1, i + radius);
            for (var j = a; j <= b; j++)
            {
                sum += src[j];
                n++;
            }

            dst[i] = sum / n;
        }

        return dst;
    }
}
