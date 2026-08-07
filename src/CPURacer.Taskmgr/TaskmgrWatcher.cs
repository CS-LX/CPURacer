using System.Diagnostics;
using System.Runtime.InteropServices;
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
    int VisibleChartCount,
    bool ShouldShow,
    bool IsCpuPage)
{
    public long Area => (long)Width * Height;

    public static ChartRoi? FromNative(in TrackRoiState s)
    {
        if (s.ChartHwnd == 0 || s.Width <= 0 || s.Height <= 0)
        {
            return null;
        }

        return new ChartRoi(
            new IntPtr(s.ChartHwnd),
            new IntPtr(s.MainHwnd),
            s.Left,
            s.Top,
            s.Width,
            s.Height,
            s.Dpi == 0 ? 96u : s.Dpi,
            s.ChartCount,
            s.ShouldShow != 0,
            s.IsCpuPage != 0);
    }
}

/// <summary>
/// Tracks Taskmgr CPU chart via native WinEvent DLL when available; otherwise managed fallback.
/// </summary>
public sealed class TaskmgrWatcher : IDisposable
{
    public const string MainWindowClass = "TaskManagerWindow";
    public const string ChartWindowClass = "CvChartWindow";

    public static int MinMainChartWidth { get; set; } = 200;
    public static int MinMainChartHeight { get; set; } = 150;
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(150);

    public event Action<ChartRoi?>? RoiChanged;

    public bool IsTracking { get; private set; }
    public bool UsingNativeTracker { get; private set; }
    public ChartRoi? CurrentRoi { get; private set; }

    private Timer? _timer;
    private ChartRoi? _lastEmitted;
    private TrackNativeApi.RoiCallback? _nativeCallback;
    private GCHandle _callbackHandle;
    private int _dispatchPosted;
    private ChartRoi? _pendingRoi;
    private readonly object _gate = new();
    private SynchronizationContext? _sync;

    public void Start()
    {
        lock (_gate)
        {
            if (IsTracking)
            {
                return;
            }

            IsTracking = true;
            _sync = SynchronizationContext.Current;
            _lastEmitted = null;

            if (TrackNativeApi.IsAvailable())
            {
                _nativeCallback = OnNativeRoi;
                _callbackHandle = GCHandle.Alloc(_nativeCallback);
                var rc = TrackNativeApi.Start(_nativeCallback, IntPtr.Zero);
                if (rc == 0)
                {
                    UsingNativeTracker = true;
                    return;
                }

                Debug.WriteLine($"Track_Start failed: {rc}");
                FreeCallback();
            }

            UsingNativeTracker = false;
            _timer = new Timer(_ => PollManagedSafe(), null, TimeSpan.Zero, PollInterval);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            IsTracking = false;
            if (UsingNativeTracker)
            {
                TrackNativeApi.Stop();
                UsingNativeTracker = false;
                FreeCallback();
            }

            _timer?.Dispose();
            _timer = null;
            _lastEmitted = null;
            CurrentRoi = null;
        }

        RoiChanged?.Invoke(null);
    }

    public void Dispose() => Stop();

    private void FreeCallback()
    {
        if (_callbackHandle.IsAllocated)
        {
            _callbackHandle.Free();
        }

        _nativeCallback = null;
    }

    private void OnNativeRoi(ref TrackRoiState state, IntPtr _)
    {
        var roi = ChartRoi.FromNative(in state);
        // Empty chart_hwnd => clear
        if (state.ChartHwnd == 0)
        {
            roi = null;
        }

        _pendingRoi = roi;
        if (Interlocked.Exchange(ref _dispatchPosted, 1) == 1)
        {
            return;
        }

        void Deliver()
        {
            Interlocked.Exchange(ref _dispatchPosted, 0);
            var latest = _pendingRoi;
            Emit(latest);
            // If newer state arrived while we were delivering, schedule again.
            if (!Equals(latest, _pendingRoi) && IsTracking)
            {
                if (Interlocked.Exchange(ref _dispatchPosted, 1) == 0)
                {
                    PostDeliver();
                }
            }
        }

        void PostDeliver()
        {
            if (_sync is not null)
            {
                _sync.Post(_ => Deliver(), null);
            }
            else
            {
                Deliver();
            }
        }

        PostDeliver();
    }

    private void PollManagedSafe()
    {
        try
        {
            if (!IsTracking)
            {
                return;
            }

            Emit(FindLargestChartRoiManaged());
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"TaskmgrWatcher managed poll failed: {ex}");
        }
    }

    private void Emit(ChartRoi? roi)
    {
        CurrentRoi = roi;
        if (RoiEquals(_lastEmitted, roi))
        {
            return;
        }

        _lastEmitted = roi;
        RoiChanged?.Invoke(roi);
    }

    /// <summary>Managed fallback (M1 behavior + foreground flag). Prefer native path.</summary>
    public static ChartRoi? FindLargestChartRoiManaged()
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

            var best = charts.OrderByDescending(c => (long)c.Rect.Width * c.Rect.Height).First();
            if (best.Rect.Width < MinMainChartWidth || best.Rect.Height < MinMainChartHeight)
            {
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

            var shouldShow = IsForegroundRelated(mainHwnd, best.Hwnd);
            return new ChartRoi(best.Hwnd, mainHwnd, best.Rect.Left, best.Rect.Top, best.Rect.Width,
                best.Rect.Height, dpi, charts.Count, shouldShow, IsCpuPage: true);
        }
        finally
        {
            foreach (var p in processes)
            {
                p.Dispose();
            }
        }
    }

    private static bool IsForegroundRelated(IntPtr mainHwnd, IntPtr chartHwnd)
    {
        var fg = NativeMethods.GetForegroundWindow();
        for (var cur = fg; cur != IntPtr.Zero; cur = NativeMethods.GetParent(cur))
        {
            if (cur == mainHwnd || cur == chartHwnd)
            {
                return true;
            }
        }

        return false;
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

        return a.Equals(b);
    }
}
