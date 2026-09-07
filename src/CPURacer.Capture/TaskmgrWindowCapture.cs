using System.Diagnostics;
using CPURacer.Native;
using CPURacer.Taskmgr;

namespace CPURacer.Capture;

public enum HeightFieldCapturePoll
{
    Unavailable,
    NoUpdate,
    Updated,
    ExtractSkipped,
}

/// <summary>
/// Captures the Task Manager window through Windows Graphics Capture, cropped to
/// the CPU chart. Because WGC targets Taskmgr rather than the composed desktop,
/// CPURacer's separate External overlay is absent from terrain frames while it
/// remains visible to screenshots and display recorders.
/// </summary>
public sealed class TaskmgrWindowCapture : IDisposable
{
    private readonly object _gate = new();

    // 图表更新周期学习：基于 WGC 帧的呈现时间戳（SystemRelativeTime）与 ROI 内容变化。
    private static readonly TimeSpan MaxUpdateInterval = TimeSpan.FromSeconds(5);
    private const int UpdateIntervalWindow = 16;
    private const long ChangeDiffThreshold = 20000;

    private CaptureKey? _key;
    private LatestFrame? _latest;
    private CancellationTokenSource? _workerCts;
    private Task? _worker;
    private bool _workerRunning;
    private int _generation;
    private DateTime _nextRetryUtc;
    private bool _disposed;
    private bool _nativeActive;
    private bool _nativeDisabled;
    private float[] _nativeScratch = Array.Empty<float>();
    private ulong _lastNativeSequence;
    private bool _hasNativeUpdateTiming;
    private long _nextManagedSequence;
    private long _lastManagedSequence;

    private readonly Queue<TimeSpan> _updateIntervals = new();
    private TimeSpan _lastUpdateTicks;
    private TimeSpan _updatePeriod;
    private byte[]? _lastCompareBgra;

    /// <summary>周期学习是否已积累足够样本（≥3 个间隔）。</summary>
    public bool HasUpdateTiming
    {
        get
        {
            lock (_gate)
            {
                return _nativeActive
                    ? _hasNativeUpdateTiming
                    : _updateIntervals.Count >= 3;
            }
        }
    }

    /// <summary>估计的图表更新周期（呈现时间基准）。</summary>
    public TimeSpan UpdatePeriod
    {
        get
        {
            lock (_gate)
            {
                return _updatePeriod;
            }
        }
    }

    /// <summary>最近一次更新帧的呈现时间（QPC）。</summary>
    public TimeSpan LastUpdatePresentTime
    {
        get
        {
            lock (_gate)
            {
                return _lastUpdateTicks;
            }
        }
    }

    /// <summary>预测的下次更新时刻（QPC）：上次更新呈现时间 + 周期。</summary>
    public TimeSpan NextUpdateTime
    {
        get
        {
            lock (_gate)
            {
                return _lastUpdateTicks + _updatePeriod;
            }
        }
    }

    public string Name => _nativeActive ? "wgc-native" : "wgc-managed";

    public HeightFieldCapturePoll TryCaptureHeightField(
        in ChartRoi roi,
        HeightFieldExtractor extractor,
        out HeightField? field)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        field = null;

        if (!roi.ShouldShow
            || roi.MainHwnd == IntPtr.Zero
            || !NativeMethods.IsWindow(roi.MainHwnd)
            || roi.Width < 8
            || roi.Height < 8)
        {
            return HeightFieldCapturePoll.Unavailable;
        }

        var inset = extractor.Inset;
        var key = new CaptureKey(
            roi.MainHwnd,
            roi.Left,
            roi.Top,
            roi.Width,
            roi.Height,
            inset.Left,
            inset.Top,
            inset.Right,
            inset.Bottom,
            extractor.SmoothRadius);

        LatestFrame? managedFrame = null;

        lock (_gate)
        {
            if (_key != key)
            {
                StartCaptureLocked(key);
            }

            if (_nativeActive)
            {
                var result = NativeCaptureApi.TryGetHeightField(
                    _lastNativeSequence,
                    _nativeScratch,
                    out var info);
                if (result < 0)
                {
                    DisableNativeAndStartManagedLocked(key);
                    return HeightFieldCapturePoll.Unavailable;
                }
                if (result == 0)
                {
                    return HeightFieldCapturePoll.NoUpdate;
                }
                if (info.PlotWidth != _nativeScratch.Length
                    || info.FrameWidth != key.Width
                    || info.FrameHeight != key.Height)
                {
                    DisableNativeAndStartManagedLocked(key);
                    return HeightFieldCapturePoll.Unavailable;
                }

                _lastNativeSequence = info.Sequence;
                _hasNativeUpdateTiming = info.HasUpdateTiming != 0;
                _lastUpdateTicks = TimeSpan.FromTicks(info.LastUpdateTicks);
                _updatePeriod = TimeSpan.FromTicks(info.UpdatePeriodTicks);
                field = new HeightField(
                    info.FrameWidth,
                    info.FrameHeight,
                    new PlotInset(
                        info.InsetLeft,
                        info.InsetTop,
                        info.InsetRight,
                        info.InsetBottom),
                    (float[])_nativeScratch.Clone(),
                    info.AccentB,
                    info.AccentG,
                    info.AccentR);
                return HeightFieldCapturePoll.Updated;
            }

            if (!_workerRunning && DateTime.UtcNow >= _nextRetryUtc)
            {
                StartWorkerLocked(key);
            }

            if (_latest is not { } latest
                || latest.Key != key
                || latest.Width != roi.Width
                || latest.Height != roi.Height)
            {
                return HeightFieldCapturePoll.Unavailable;
            }
            if (latest.Sequence <= _lastManagedSequence)
            {
                return HeightFieldCapturePoll.NoUpdate;
            }

            _lastManagedSequence = latest.Sequence;
            managedFrame = latest;
        }

        field = extractor.Extract(new CapturedFrame(
            managedFrame!.Width,
            managedFrame.Height,
            managedFrame.Bgra));
        return field is null
            ? HeightFieldCapturePoll.ExtractSkipped
            : HeightFieldCapturePoll.Updated;
    }

    private void StartCaptureLocked(CaptureKey key)
    {
        _workerCts?.Cancel();
        _workerRunning = false;
        ++_generation;
        if (_nativeActive)
        {
            NativeCaptureApi.Stop();
            _nativeActive = false;
        }

        ResetStateLocked(key);
        if (!_nativeDisabled && NativeCaptureApi.IsAvailable)
        {
            var config = new NativeCaptureConfig
            {
                MainHwnd = key.MainHwnd.ToInt64(),
                ScreenLeft = key.ScreenLeft,
                ScreenTop = key.ScreenTop,
                Width = key.Width,
                Height = key.Height,
                InsetLeft = key.InsetLeft,
                InsetTop = key.InsetTop,
                InsetRight = key.InsetRight,
                InsetBottom = key.InsetBottom,
                SmoothRadius = key.SmoothRadius,
            };
            if (NativeCaptureApi.TryStart(config, out _))
            {
                _nativeActive = true;
                _nativeScratch = new float[
                    Math.Max(1, key.Width - key.InsetLeft - key.InsetRight)];
                return;
            }

            _nativeDisabled = true;
        }

        StartWorkerLocked(key);
    }

    private void DisableNativeAndStartManagedLocked(CaptureKey key)
    {
        NativeCaptureApi.Stop();
        _nativeActive = false;
        _nativeDisabled = true;
        StartWorkerLocked(key);
    }

    private void ResetStateLocked(CaptureKey key)
    {
        _key = key;
        _latest = null;
        _updateIntervals.Clear();
        _lastUpdateTicks = TimeSpan.Zero;
        _updatePeriod = TimeSpan.Zero;
        _lastCompareBgra = null;
        _lastNativeSequence = 0;
        _hasNativeUpdateTiming = false;
        _lastManagedSequence = 0;
    }

    private void StartWorkerLocked(CaptureKey key)
    {
        _workerCts?.Cancel();

        ResetStateLocked(key);
        var cts = new CancellationTokenSource();
        _workerCts = cts;
        _workerRunning = true;
        var generation = ++_generation;
        _worker = Task.Run(
            () => CaptureLoopAsync(key, generation, cts),
            CancellationToken.None);
    }

    private async Task CaptureLoopAsync(
        CaptureKey key,
        int generation,
        CancellationTokenSource cts)
    {
        var token = cts.Token;
        try
        {
            using var session = await WgcWindowSession
                .TryStartAsync(key.MainHwnd, token)
                .ConfigureAwait(false);
            if (session is null)
            {
                throw new PlatformNotSupportedException(
                    "Windows Graphics Capture requires Windows 10 20H1 or later.");
            }

            while (!token.IsCancellationRequested)
            {
                var captured = await session.CaptureBgraAsync(token).ConfigureAwait(false);
                if (captured is null)
                {
                    continue;
                }

                var (frameW, frameH, frameBgra, presentTime) = captured.Value;
                if (frameW < 8 || frameH < 8)
                {
                    continue;
                }

                if (!TryCropChartBgra(key, frameW, frameH, frameBgra, out var bgra))
                {
                    continue;
                }

                lock (_gate)
                {
                    if (_generation == generation
                        && _key == key
                        && !token.IsCancellationRequested)
                    {
                        _latest = new LatestFrame(
                            key,
                            key.Width,
                            key.Height,
                            bgra,
                            ++_nextManagedSequence);
                        TrackUpdateTiming(presentTime, bgra, key.Width, key.Height);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Taskmgr WGC capture failed: {ex}");
        }
        finally
        {
            cts.Dispose();
            lock (_gate)
            {
                if (_generation == generation && _key == key)
                {
                    _workerRunning = false;
                    _nextRetryUtc = DateTime.UtcNow.AddSeconds(1);
                }
            }
        }
    }

    /// <summary>
    /// 基于 ROI 内容变化识别 Taskmgr 更新帧（跳变），并用呈现时间戳维护周期序列。
    /// 必须在 _gate 锁内调用。
    ///
    /// 现状（2026-08 诊断）：Taskmgr CPU 图约 1000ms 跳变一次（±2% 波动），
    /// 中位数估计可靠；每次跳变 Δ≈24px。注意：这里在 worker 线程更新相位
    /// （_lastUpdateTicks），比 UI 线程处理该帧的滚动入账快一个循环，
    /// 是 RaceSim 预偏移偶发漏过本次跳变的机制之一（详见 RaceSim 注释）。
    /// </summary>
    private void TrackUpdateTiming(TimeSpan presentTime, byte[] bgra, int width, int height)
    {
        var changed = _lastCompareBgra == null
            || SampleDiff(_lastCompareBgra, bgra, width, height) > ChangeDiffThreshold;
        _lastCompareBgra = bgra;
        if (!changed)
        {
            return;
        }

        if (_lastUpdateTicks > TimeSpan.Zero)
        {
            var interval = presentTime - _lastUpdateTicks;
            if (interval > TimeSpan.Zero && interval < MaxUpdateInterval)
            {
                _updateIntervals.Enqueue(interval);
                if (_updateIntervals.Count > UpdateIntervalWindow)
                {
                    _updateIntervals.Dequeue();
                }

                var sorted = _updateIntervals.OrderBy(i => i).ToArray();
                _updatePeriod = sorted[sorted.Length / 2];
                // 诊断：更新间隔与当前中位数（周期估计稳定性）。
                DiagLog.Write(
                    $"upd: interval={interval.TotalMilliseconds:F0}ms median={_updatePeriod.TotalMilliseconds:F0}ms");
            }
        }

        _lastUpdateTicks = presentTime;
    }

    /// <summary>抽样比较两帧 ROI 的像素差异（每 16px 一个采样点）。</summary>
    private static long SampleDiff(byte[] a, byte[] b, int width, int height)
    {
        long diff = 0;
        for (var y = 0; y < height; y += 16)
        {
            var rowBase = y * width * 4;
            for (var x = 0; x < width; x += 16)
            {
                var i = rowBase + x * 4;
                diff += Math.Abs(a[i] - b[i]);
                diff += Math.Abs(a[i + 1] - b[i + 1]);
                diff += Math.Abs(a[i + 2] - b[i + 2]);
            }
        }

        return diff;
    }

    /// <summary>
    /// Maps the chart screen rect into the WGC buffer via ExtendedFrame bounds.
    /// Measured on this machine: item.Size == DWMWA_EXTENDED_FRAME_BOUNDS, and the
    /// chart top border sits exactly at chart.Top - ext.Top.
    /// </summary>
    private static bool TryCropChartBgra(
        in CaptureKey key,
        int frameW,
        int frameH,
        byte[] frameBgra,
        out byte[] bgra)
    {
        bgra = Array.Empty<byte>();

        if (!NativeMethods.TryGetExtendedFrameBounds(key.MainHwnd, out var outer)
            || outer.Width <= 0
            || outer.Height <= 0)
        {
            return false;
        }

        var scaleX = (double)frameW / outer.Width;
        var scaleY = (double)frameH / outer.Height;
        var srcX = (int)Math.Round((key.ScreenLeft - outer.Left) * scaleX);
        var srcY = (int)Math.Round((key.ScreenTop - outer.Top) * scaleY);
        var srcW = Math.Max(1, (int)Math.Round(key.Width * scaleX));
        var srcH = Math.Max(1, (int)Math.Round(key.Height * scaleY));

        if (srcX < 0
            || srcY < 0
            || srcX + srcW > frameW
            || srcY + srcH > frameH)
        {
            return false;
        }

        var srcStride = frameW * 4;
        var dstW = key.Width;
        var dstH = key.Height;
        bgra = new byte[dstW * dstH * 4];

        if (srcW == dstW && srcH == dstH)
        {
            for (var row = 0; row < dstH; row++)
            {
                var srcOffset = ((srcY + row) * srcStride) + (srcX * 4);
                var dstOffset = row * dstW * 4;
                Buffer.BlockCopy(frameBgra, srcOffset, bgra, dstOffset, dstW * 4);
            }

            return true;
        }

        for (var row = 0; row < dstH; row++)
        {
            var sy = srcY + Math.Min(srcH - 1, row * srcH / dstH);
            var dstRow = row * dstW * 4;
            for (var col = 0; col < dstW; col++)
            {
                var sx = srcX + Math.Min(srcW - 1, col * srcW / dstW);
                var srcOffset = (sy * srcStride) + (sx * 4);
                var dstOffset = dstRow + (col * 4);
                bgra[dstOffset] = frameBgra[srcOffset];
                bgra[dstOffset + 1] = frameBgra[srcOffset + 1];
                bgra[dstOffset + 2] = frameBgra[srcOffset + 2];
                bgra[dstOffset + 3] = frameBgra[srcOffset + 3];
            }
        }

        return true;
    }

    public void Dispose()
    {
        Task? worker;
        bool stopNative;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _workerCts?.Cancel();
            worker = _worker;
            _latest = null;
            stopNative = _nativeActive;
            _nativeActive = false;
        }

        if (stopNative)
        {
            NativeCaptureApi.Stop();
        }

        try
        {
            worker?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException ex) when (
            ex.InnerExceptions.All(static e => e is OperationCanceledException))
        {
        }

        lock (_gate)
        {
            _workerCts = null;
            _worker = null;
        }
    }

    private readonly record struct CaptureKey(
        IntPtr MainHwnd,
        int ScreenLeft,
        int ScreenTop,
        int Width,
        int Height,
        int InsetLeft,
        int InsetTop,
        int InsetRight,
        int InsetBottom,
        int SmoothRadius);

    private sealed record LatestFrame(
        CaptureKey Key,
        int Width,
        int Height,
        byte[] Bgra,
        long Sequence);
}
