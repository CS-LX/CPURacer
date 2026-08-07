using System.Diagnostics;
using CPURacer.Native;

namespace CPURacer.Taskmgr;

/// <summary>
/// Chart region in physical pixels.
/// </summary>
public readonly record struct ChartRoi(
    IntPtr ChartHwnd,
    IntPtr MainHwnd,
    int Left,
    int Top,
    int Width,
    int Height,
    uint Dpi,
    int VisibleChartCount)
{
    public long Area => (long)Width * Height;
}

/// <summary>
/// Discovers Taskmgr.exe and the largest visible CvChartWindow (CPU / performance graph).
/// </summary>
public sealed class TaskmgrWatcher : IDisposable
{
    public const string MainWindowClass = "TaskManagerWindow";
    public const string ChartWindowClass = "CvChartWindow";

    /// <summary>Minimum size to treat a chart as the main performance graph (not a sidebar thumb).</summary>
    public static int MinMainChartWidth { get; set; } = 200;

    public static int MinMainChartHeight { get; set; } = 150;

    /// <summary>Polling interval while tracking.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(150);

    public event Action<ChartRoi?>? RoiChanged;

    public bool IsTracking { get; private set; }

    public ChartRoi? CurrentRoi { get; private set; }

    private Timer? _timer;
    private ChartRoi? _lastEmitted;
    private readonly object _gate = new();

    public void Start()
    {
        lock (_gate)
        {
            if (IsTracking)
            {
                return;
            }

            IsTracking = true;
            _timer = new Timer(_ => PollSafe(), null, TimeSpan.Zero, PollInterval);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            IsTracking = false;
            _timer?.Dispose();
            _timer = null;
            _lastEmitted = null;
            CurrentRoi = null;
        }

        RoiChanged?.Invoke(null);
    }

    public void Dispose() => Stop();

    private void PollSafe()
    {
        try
        {
            if (!IsTracking)
            {
                return;
            }

            var roi = FindLargestChartRoi();
            CurrentRoi = roi;

            if (RoiEquals(_lastEmitted, roi))
            {
                return;
            }

            _lastEmitted = roi;
            RoiChanged?.Invoke(roi);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"TaskmgrWatcher poll failed: {ex}");
        }
    }

    public static ChartRoi? FindLargestChartRoi()
    {
        var processes = Process.GetProcessesByName("Taskmgr");
        if (processes.Length == 0)
        {
            return null;
        }

        try
        {
            var pids = new HashSet<uint>(processes.Select(p => (uint)p.Id));
            IntPtr mainHwnd = IntPtr.Zero;
            var charts = new List<(IntPtr Hwnd, RECT Rect)>();

            NativeMethods.EnumWindows((hWnd, _) =>
            {
                if (!NativeMethods.IsWindowVisible(hWnd))
                {
                    return true;
                }

                NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);
                if (!pids.Contains(pid))
                {
                    return true;
                }

                if (!string.Equals(NativeMethods.GetClassName(hWnd), MainWindowClass, StringComparison.Ordinal))
                {
                    return true;
                }

                if (!NativeMethods.GetWindowRect(hWnd, out var mainRect) || mainRect.Width < 100 || mainRect.Height < 100)
                {
                    return true;
                }

                mainHwnd = hWnd;
                CollectCharts(hWnd, charts);
                return true;
            }, IntPtr.Zero);

            if (mainHwnd == IntPtr.Zero || charts.Count == 0)
            {
                return null;
            }

            var best = charts
                .OrderByDescending(c => (long)c.Rect.Width * c.Rect.Height)
                .First();

            if (best.Rect.Width < MinMainChartWidth || best.Rect.Height < MinMainChartHeight)
            {
                // Only sidebar thumbs (or wrong page) — treat as no main CPU graph.
                return null;
            }

            var dpi = NativeMethods.GetDpiForWindow(best.Hwnd);
            if (dpi == 0)
            {
                dpi = NativeMethods.GetDpiForWindow(mainHwnd);
            }

            if (dpi == 0)
            {
                dpi = 96;
            }

            return new ChartRoi(
                best.Hwnd,
                mainHwnd,
                best.Rect.Left,
                best.Rect.Top,
                best.Rect.Width,
                best.Rect.Height,
                dpi,
                charts.Count);
        }
        finally
        {
            foreach (var p in processes)
            {
                p.Dispose();
            }
        }
    }

    private static void CollectCharts(IntPtr parent, List<(IntPtr Hwnd, RECT Rect)> charts)
    {
        NativeMethods.EnumChildWindows(parent, (hWnd, _) =>
        {
            if (NativeMethods.IsWindowVisible(hWnd)
                && string.Equals(NativeMethods.GetClassName(hWnd), ChartWindowClass, StringComparison.Ordinal)
                && NativeMethods.GetWindowRect(hWnd, out var rect)
                && rect.Width > 0
                && rect.Height > 0)
            {
                charts.Add((hWnd, rect));
            }

            CollectCharts(hWnd, charts);
            return true;
        }, IntPtr.Zero);
    }

    private static bool RoiEquals(ChartRoi? a, ChartRoi? b)
    {
        if (a is null && b is null)
        {
            return true;
        }

        if (a is null || b is null)
        {
            return false;
        }

        var x = a.Value;
        var y = b.Value;
        return x.ChartHwnd == y.ChartHwnd
               && x.MainHwnd == y.MainHwnd
               && x.Left == y.Left
               && x.Top == y.Top
               && x.Width == y.Width
               && x.Height == y.Height
               && x.Dpi == y.Dpi;
    }
}
