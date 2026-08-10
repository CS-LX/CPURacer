using System.Diagnostics;
using CPURacer.Native;
using CPURacer.Taskmgr;

namespace CPURacer.Capture;

/// <summary>
/// Captures the Task Manager window through Windows Graphics Capture, cropped to
/// the CPU chart. Because WGC targets Taskmgr rather than the composed desktop,
/// CPURacer's separate External overlay is absent from terrain frames while it
/// remains visible to screenshots and display recorders.
/// </summary>
public sealed class TaskmgrWindowCapture : IFrameCapture, IDisposable
{
    private readonly object _gate = new();

    private CaptureKey? _key;
    private LatestFrame? _latest;
    private CancellationTokenSource? _workerCts;
    private Task? _worker;
    private bool _workerRunning;
    private int _generation;
    private DateTime _nextRetryUtc;
    private bool _disposed;

    public string Name => "wgc";

    public CapturedFrame? TryCapture(in ChartRoi roi)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!roi.ShouldShow
            || roi.MainHwnd == IntPtr.Zero
            || !NativeMethods.IsWindow(roi.MainHwnd)
            || roi.Width < 8
            || roi.Height < 8)
        {
            return null;
        }

        // Screen-space chart rect — same space as Overlay GetWindowRect. Map into the
        // WGC buffer via DWMWA_EXTENDED_FRAME_BOUNDS (WGC item.Size matches that outer).
        var key = new CaptureKey(
            roi.MainHwnd,
            roi.Left,
            roi.Top,
            roi.Width,
            roi.Height);

        lock (_gate)
        {
            if (_key != key)
            {
                StartWorkerLocked(key);
            }
            else if (!_workerRunning && DateTime.UtcNow >= _nextRetryUtc)
            {
                StartWorkerLocked(key);
            }

            if (_latest is not { } latest
                || latest.Key != key
                || latest.Width != roi.Width
                || latest.Height != roi.Height)
            {
                return null;
            }

            return new CapturedFrame(latest.Width, latest.Height, latest.Bgra);
        }
    }

    private void StartWorkerLocked(CaptureKey key)
    {
        _workerCts?.Cancel();

        _key = key;
        _latest = null;
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

                var (frameW, frameH, frameBgra) = captured.Value;
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
                        _latest = new LatestFrame(key, key.Width, key.Height, bgra);
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
    /// Maps the chart screen rect into the WGC buffer via ExtendedFrame bounds.
    /// Measured on this machine: item.Size == DWMWA_EXTENDED_FRAME_BOUNDS, and the
    /// chart top border sits exactly at chart.Top - ext.Top (not ScreenToClient).
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
        int Height);

    private sealed record LatestFrame(
        CaptureKey Key,
        int Width,
        int Height,
        byte[] Bgra);
}
