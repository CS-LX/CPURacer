namespace CPURacer.Taskmgr;

/// <summary>
/// Chart region in physical pixels. Populated by TaskmgrWatcher in M1.
/// </summary>
public readonly record struct ChartRoi(
    IntPtr ChartHwnd,
    IntPtr MainHwnd,
    int Left,
    int Top,
    int Width,
    int Height,
    uint Dpi);

/// <summary>
/// Discovers Taskmgr.exe and the largest CvChartWindow. Stub in M0.
/// </summary>
public sealed class TaskmgrWatcher : IDisposable
{
    public event Action<ChartRoi?>? RoiChanged;

    public bool IsTracking { get; private set; }

    public void Start()
    {
        IsTracking = true;
        // M1: poll Taskmgr / CvChartWindow
        RoiChanged?.Invoke(null);
    }

    public void Stop()
    {
        IsTracking = false;
        RoiChanged?.Invoke(null);
    }

    public void Dispose() => Stop();
}
