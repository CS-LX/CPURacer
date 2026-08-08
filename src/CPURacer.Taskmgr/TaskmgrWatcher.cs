using System.Diagnostics;
using System.Runtime.InteropServices;
using CPURacer.Native;

namespace CPURacer.Taskmgr;

/// <summary>Chart region in physical pixels.</summary>
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
/// Tracks Taskmgr CPU chart via native WinEvent DLL when available; otherwise a degraded timer poll.
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
    public TrackFollowMode FollowMode { get; private set; } = TrackFollowMode.External;

    private Timer? _timer;
    private ChartRoi? _lastEmitted;
    private TrackNativeApi.RoiCallback? _nativeCallback;
    private GCHandle _callbackHandle;
    private int _dispatchPosted;
    private ChartRoi? _pendingRoi;
    private readonly object _gate = new();
    private SynchronizationContext? _sync;

    public void SetFollowMode(TrackFollowMode mode)
    {
        lock (_gate)
        {
            FollowMode = mode;
            if (UsingNativeTracker)
            {
                TrackNativeApi.SetFollowMode((int)mode);
            }
        }
    }

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

            if (TryStartNative())
            {
                return;
            }

            UsingNativeTracker = false;
            _timer = new Timer(_ => PollManagedSafe(), null, TimeSpan.Zero, PollInterval);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!IsTracking)
            {
                return;
            }

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

    private bool TryStartNative()
    {
        if (!TrackNativeApi.IsAvailable())
        {
            return false;
        }

        TrackNativeApi.SetFollowMode((int)FollowMode);
        _nativeCallback = OnNativeRoi;
        _callbackHandle = GCHandle.Alloc(_nativeCallback);
        if (TrackNativeApi.Start(_nativeCallback, IntPtr.Zero) == 0)
        {
            UsingNativeTracker = true;
            return true;
        }

        Debug.WriteLine("Track_Start failed; falling back to managed poll.");
        FreeCallback();
        return false;
    }

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
        _pendingRoi = ChartRoi.FromNative(in state);
        if (Interlocked.Exchange(ref _dispatchPosted, 1) == 1)
        {
            return;
        }

        void Deliver()
        {
            Interlocked.Exchange(ref _dispatchPosted, 0);
            var latest = _pendingRoi;
            Emit(latest);
            if (!Equals(latest, _pendingRoi) && IsTracking
                && Interlocked.Exchange(ref _dispatchPosted, 1) == 0)
            {
                Post(Deliver);
            }
        }

        Post(Deliver);
    }

    private void Post(Action action)
    {
        if (_sync is not null)
        {
            _sync.Post(_ => action(), null);
        }
        else
        {
            action();
        }
    }

    private void PollManagedSafe()
    {
        try
        {
            if (IsTracking)
            {
                Emit(FindLargestChartRoiManaged());
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"TaskmgrWatcher managed poll failed: {ex}");
        }
    }

    private void Emit(ChartRoi? roi)
    {
        CurrentRoi = roi;
        if (Equals(_lastEmitted, roi))
        {
            return;
        }

        _lastEmitted = roi;
        RoiChanged?.Invoke(roi);
    }

    /// <summary>
    /// Degraded fallback only (no sticky CPU page). Prefer TrackNative.
    /// </summary>
    public static ChartRoi? FindLargestChartRoiManaged()
    {
        using var processes = new ProcessList("Taskmgr");
        if (processes.Count == 0)
        {
            return null;
        }

        var pids = processes.Pids;
        IntPtr mainHwnd = IntPtr.Zero;
        var charts = new List<(IntPtr Hwnd, RECT Rect)>();

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hWnd))
            {
                return true;
            }

            NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);
            if (!pids.Contains(pid)
                || !string.Equals(NativeMethods.GetClassName(hWnd), MainWindowClass, StringComparison.Ordinal)
                || !NativeMethods.GetWindowRect(hWnd, out var mainRect)
                || mainRect.Width < 100
                || mainRect.Height < 100)
            {
                return true;
            }

            mainHwnd = hWnd;
            CollectVisibleCharts(hWnd, charts);
            return false;
        }, IntPtr.Zero);

        if (mainHwnd == IntPtr.Zero || charts.Count == 0)
        {
            return null;
        }

        var best = charts.MaxBy(c => (long)c.Rect.Width * c.Rect.Height);
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

        return new ChartRoi(
            best.Hwnd,
            mainHwnd,
            best.Rect.Left,
            best.Rect.Top,
            best.Rect.Width,
            best.Rect.Height,
            dpi,
            charts.Count,
            IsForegroundRelated(mainHwnd, best.Hwnd),
            IsCpuPage: true);
    }

    private static bool IsForegroundRelated(IntPtr mainHwnd, IntPtr chartHwnd)
    {
        var foreground = NativeMethods.GetForegroundWindow();
        for (var cur = foreground; cur != IntPtr.Zero; cur = NativeMethods.GetParent(cur))
        {
            if (cur == mainHwnd || cur == chartHwnd)
            {
                return true;
            }
        }

        if (foreground == IntPtr.Zero || mainHwnd == IntPtr.Zero)
        {
            return false;
        }

        _ = NativeMethods.GetWindowThreadProcessId(foreground, out var foregroundPid);
        _ = NativeMethods.GetWindowThreadProcessId(mainHwnd, out var taskmgrPid);
        return foregroundPid != 0 && foregroundPid == taskmgrPid;
    }

    private static void CollectVisibleCharts(IntPtr parent, List<(IntPtr Hwnd, RECT Rect)> charts)
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

            CollectVisibleCharts(hWnd, charts);
            return true;
        }, IntPtr.Zero);
    }

    private sealed class ProcessList : IDisposable
    {
        private readonly Process[] _items;

        public ProcessList(string name) => _items = Process.GetProcessesByName(name);

        public int Count => _items.Length;

        public HashSet<uint> Pids => _items.Select(p => (uint)p.Id).ToHashSet();

        public void Dispose()
        {
            foreach (var p in _items)
            {
                p.Dispose();
            }
        }
    }
}
