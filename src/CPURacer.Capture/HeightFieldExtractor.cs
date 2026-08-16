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

    // 动态 accent 识别：自举前评分不假设色相（便于识别任意主题曲线色）；
    // 自举后按目标色准入并平滑跟踪主题/强调色变化。
    private const float AccentAdaptRate = 0.10f;
    private const int AccentDominanceMin = 20;
    private const int AccentDominanceRatio = 40;

    private readonly PlotInset _inset;
    private readonly int _smoothRadius;

    private byte _accentB = 187;
    private byte _accentG = 125;
    private byte _accentR = 12;
    private bool _accentValid;
    private int _accentFailStreak;

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
            // 连续提取失败可能是主题/强调色切换导致目标色拒掉了新曲线：
            // 回到无假设自举，让下一帧重新识别颜色。遮挡时无假设也找不到
            // 高饱和像素，会继续保持失败态，不会误自举。
            if (++_accentFailStreak >= 30)
            {
                _accentValid = false;
            }

            return null;
        }

        _accentFailStreak = 0;

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
            UpdateAccentTarget(ab, ag, ar, accentWeight);
        }

        return new HeightField(w, h, inset, smooth, ab, ag, ar);
    }

    /// <summary>
    /// 用当前帧的加权平均曲线色（含 fill 的暗变体）平滑更新目标色。
    /// 候选无清晰 dominant（如被遮挡/无曲线）时保持现有目标不变。
    /// </summary>
    private void UpdateAccentTarget(byte candB, byte candG, byte candR, long weight)
    {
        if (weight < 64)
        {
            return;
        }

        var max = Math.Max(candB, Math.Max(candG, candR));
        var min = Math.Min(candB, Math.Min(candG, candR));
        if (max - min < AccentDominanceMin)
        {
            return;
        }

        if (!_accentValid)
        {
            _accentB = candB;
            _accentG = candG;
            _accentR = candR;
            _accentValid = true;
            return;
        }

        // 温和跟随：主题/强调色中途变化时收敛，瞬变则几乎不动。
        _accentB = (byte)(_accentB + (candB - _accentB) * AccentAdaptRate);
        _accentG = (byte)(_accentG + (candG - _accentG) * AccentAdaptRate);
        _accentR = (byte)(_accentR + (candR - _accentR) * AccentAdaptRate);
    }

    /// <summary>Extract from raw BGRA (for tests / fixtures).</summary>
    public HeightField? ExtractBgra(int width, int height, byte[] bgra)
        => Extract(new CapturedFrame(width, height, bgra));

    /// <summary>
    /// Brightest-blue Y near the stroke. Windowed around argmax so dim fill cannot
    /// merge into a wide band and flip bandTop/centroid frame-to-frame.
    /// </summary>
    private bool TrySampleColumnRidge(
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
    /// 动态目标色评分。自举前只看饱和度+亮度（不假设色相，便于首帧识别任意主题）；
    /// 自举后要求像素与目标色同 dominant 通道且通道差成比例，从而剔除网格/背景
    /// 以及同 dominant 但不同色相的干扰（如红 vs 橙）。
    /// </summary>
    public int AccentScore(byte b, byte g, byte r)
    {
        var maxP = Math.Max(b, Math.Max(g, r));
        var minP = Math.Min(b, Math.Min(g, r));
        var sat = maxP - minP;
        if (sat < 40)
        {
            return 0;
        }

        if (!_accentValid)
        {
            // 自举阶段：无假设，只按饱和度+亮度给分。
            return sat * 2 + maxP / 3;
        }

        // dominant 通道必须与目标一致。
        var targetMax = Math.Max(_accentB, Math.Max(_accentG, _accentR));
        if (targetMax == _accentB && (b < g || b < r)
            || targetMax == _accentG && (g < b || g < r)
            || targetMax == _accentR && (r < b || r < g))
        {
            return 0;
        }

        // 通道差比例约束：像素通道差至少为目标通道差的 AccentDominanceRatio%。
        // 同色系不同亮度（stroke/fill/光晕）按比例缩放后仍能通过；
        // 同 dominant 但色相不同的干扰（如红 vs 橙）通道差结构不同而被剔除。
        var secondP = MidOfThree(b, g, r);
        var targetSecond = MidOfThree(_accentB, _accentG, _accentR);
        var chroma = maxP - secondP;
        if (chroma * 100 < (targetMax - targetSecond) * AccentDominanceRatio)
        {
            return 0;
        }

        // Bright saturated same-hue pixels score high; dim wash scores low.
        return chroma * 2 + sat + maxP / 3;
    }

    private static int MidOfThree(int a, int b, int c)
        => Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));

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
