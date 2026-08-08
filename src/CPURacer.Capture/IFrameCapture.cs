using CPURacer.Taskmgr;

namespace CPURacer.Capture;

/// <summary>BGRA32 frame in physical pixels.</summary>
public sealed class CapturedFrame
{
    public CapturedFrame(int width, int height, byte[] bgra)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (bgra.Length < width * height * 4)
        {
            throw new ArgumentException("Buffer too small for BGRA frame.", nameof(bgra));
        }

        Width = width;
        Height = height;
        Bgra = bgra;
        Stride = width * 4;
    }

    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public byte[] Bgra { get; }
}

/// <summary>
/// Captures a composed screen frame for a chart ROI.
/// Must not use CvChartWindow BitBlt (grid only; no utilization polyline).
/// </summary>
public interface IFrameCapture
{
    /// <summary>Returns null when capture is unavailable or ROI invalid.</summary>
    CapturedFrame? TryCapture(in ChartRoi roi);
}
