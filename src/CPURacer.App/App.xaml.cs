using System.Drawing;
using System.Windows;
using System.Windows.Threading;
using CPURacer.Capture;
using CPURacer.Overlay;
using CPURacer.Taskmgr;
using Application = System.Windows.Application;
using Forms = System.Windows.Forms;

namespace CPURacer.App;

public partial class App : Application
{
    private Forms.NotifyIcon? _tray;
    private Forms.ToolStripMenuItem? _trackItem;
    private Forms.ToolStripMenuItem? _overlayItem;
    private Forms.ToolStripMenuItem? _statusItem;
    private Forms.ToolStripMenuItem? _followExternalItem;
    private Forms.ToolStripMenuItem? _followChildItem;
    private OverlayWindow? _overlay;
    private TaskmgrWatcher? _watcher;
    private bool _debugOverlay = true;
    private bool _showFitPolyline = true;
    private TrackFollowMode _followMode = TrackFollowMode.External;

    private readonly ScreenRoiCapture _capture = new();
    private readonly HeightFieldExtractor _extractor = new();
    private DispatcherTimer? _captureTimer;
    private int _captureFailStreak;
    private string _capStatus = "";

    protected override void OnStartup(StartupEventArgs e)
    {
        Forms.Application.SetHighDpiMode(Forms.HighDpiMode.PerMonitorV2);
        base.OnStartup(e);

        _overlay = new OverlayWindow
        {
            ShowDebugChrome = _debugOverlay,
            ShowFitPolyline = _showFitPolyline,
            FollowMode = _followMode,
        };
        _watcher = new TaskmgrWatcher();
        _watcher.SetFollowMode(_followMode);
        _watcher.RoiChanged += roi =>
        {
            if (Dispatcher.CheckAccess())
            {
                OnRoiChanged(roi);
            }
            else
            {
                Dispatcher.BeginInvoke(() => OnRoiChanged(roi));
            }
        };

        _captureTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            // 80 ms was directly visible as ~0.1 s lag behind Taskmgr's advancing edge.
            // 30 Hz keeps latency below one display frame or two without busy polling.
            Interval = TimeSpan.FromMilliseconds(33),
        };
        _captureTimer.Tick += (_, _) => CaptureTick();

        BuildTray();

        MainWindow = new Window
        {
            Width = 0,
            Height = 0,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            Opacity = 0,
        };
        MainWindow.Show();
        MainWindow.Hide();
    }

    private void BuildTray()
    {
        _tray = new Forms.NotifyIcon
        {
            Text = "CPURacer",
            Visible = true,
            Icon = SystemIcons.Application,
        };

        var menu = new Forms.ContextMenuStrip();

        _trackItem = new Forms.ToolStripMenuItem("开始跟踪 Taskmgr");
        _trackItem.Click += (_, _) => ToggleTracking();
        menu.Items.Add(_trackItem);

        var followMenu = new Forms.ToolStripMenuItem("跟随方式");
        _followExternalItem = new Forms.ToolStripMenuItem("外部 Overlay（WinEvent）") { CheckOnClick = true };
        _followChildItem = new Forms.ToolStripMenuItem("子窗 SetParent（TaskmgrPlayer 式）") { CheckOnClick = true };
        _followExternalItem.Click += (_, _) => SetFollowMode(TrackFollowMode.External);
        _followChildItem.Click += (_, _) => SetFollowMode(TrackFollowMode.Child);
        followMenu.DropDownItems.Add(_followExternalItem);
        followMenu.DropDownItems.Add(_followChildItem);
        menu.Items.Add(followMenu);
        SyncFollowMenu();

        _overlayItem = new Forms.ToolStripMenuItem("手动显示 Overlay");
        _overlayItem.Click += (_, _) => ToggleOverlayManual();
        menu.Items.Add(_overlayItem);

        var debugItem = new Forms.ToolStripMenuItem("调试描边") { Checked = _debugOverlay, CheckOnClick = true };
        debugItem.CheckedChanged += (_, _) =>
        {
            _debugOverlay = debugItem.Checked;
            if (_overlay is not null)
            {
                _overlay.ShowDebugChrome = _debugOverlay;
                _overlay.InvalidateVisual();
            }
        };
        menu.Items.Add(debugItem);

        var fitItem = new Forms.ToolStripMenuItem("调试拟合线") { Checked = _showFitPolyline, CheckOnClick = true };
        fitItem.CheckedChanged += (_, _) =>
        {
            _showFitPolyline = fitItem.Checked;
            if (_overlay is not null)
            {
                _overlay.ShowFitPolyline = _showFitPolyline;
                _overlay.InvalidateVisual();
            }
        };
        menu.Items.Add(fitItem);

        _statusItem = new Forms.ToolStripMenuItem("状态: 未跟踪") { Enabled = false };
        menu.Items.Add(_statusItem);
        menu.Items.Add(new Forms.ToolStripSeparator());

        var exitItem = new Forms.ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => Shutdown();
        menu.Items.Add(exitItem);

        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ToggleOverlayManual();
    }

    private void CaptureTick()
    {
        if (_overlay is null || _watcher is null || !_watcher.IsTracking)
        {
            return;
        }

        var roi = _watcher.CurrentRoi;
        if (roi is null || !roi.Value.ShouldShow)
        {
            _captureFailStreak = 0;
            _capStatus = "cap=sleep";
            _overlay.SetHeightField(null, _capStatus);
            UpdateTrayTip(roi);
            return;
        }

        // Overlay is excluded from capture at the HWND level; never hide/show it per tick.
        var frame = _capture.TryCapture(roi.Value);

        if (frame is null)
        {
            _captureFailStreak++;
            _capStatus = $"cap=fail({_captureFailStreak})";
            // Treat one or two failures like skipped composition frames. Clearing
            // immediately would blink at 30 Hz; sustained failure must clear stale terrain.
            if (_captureFailStreak < 3)
            {
                _overlay.SetCaptureStatus(_capStatus);
            }
            else
            {
                _overlay.SetHeightField(null, _capStatus);
            }

            UpdateTrayTip(roi);
            return;
        }

        _captureFailStreak = 0;
        var field = _extractor.Extract(frame);
        if (field is null)
        {
            // Keep the last reliable contour when BitBlt catches Taskmgr between
            // composition passes. Clearing or using a synthetic fallback causes
            // the visible fit to alternate between the line and a high plateau.
            _capStatus = "cap=ok extract=skip";
            _overlay.SetCaptureStatus(_capStatus);
        }
        else
        {
            _capStatus = $"cap=ok cols={field.PlotWidth}";
            _overlay.SetHeightField(field, _capStatus);
        }

        UpdateTrayTip(roi);
    }

    private void SetFollowMode(TrackFollowMode mode)
    {
        if (_followMode == mode)
        {
            SyncFollowMenu();
            return;
        }

        _followMode = mode;
        _watcher?.SetFollowMode(mode);
        if (_overlay is not null)
        {
            _overlay.FollowMode = mode;
            if (_watcher?.IsTracking == true)
            {
                _overlay.ApplyRoi(_watcher.CurrentRoi);
            }
        }

        SyncFollowMenu();
        UpdateTrayTip(_watcher?.CurrentRoi);
    }

    private void SyncFollowMenu()
    {
        if (_followExternalItem is not null)
        {
            _followExternalItem.Checked = _followMode == TrackFollowMode.External;
        }

        if (_followChildItem is not null)
        {
            _followChildItem.Checked = _followMode == TrackFollowMode.Child;
        }
    }

    private void OnRoiChanged(ChartRoi? roi)
    {
        _overlay?.ApplyRoi(roi);
        UpdateTrayTip(roi);
    }

    private void UpdateTrayTip(ChartRoi? roi)
    {
        if (_tray is null)
        {
            return;
        }

        if (_watcher?.IsTracking != true)
        {
            _tray.Text = "CPURacer";
            if (_statusItem is not null)
            {
                _statusItem.Text = "状态: 未跟踪";
            }

            return;
        }

        var backend = _watcher.UsingNativeTracker ? "native" : "managed";
        var follow = _followMode == TrackFollowMode.Child ? "child" : "external";
        string status;
        if (roi is null)
        {
            status = $"tracking ({backend}/{follow}, no CPU chart)";
        }
        else
        {
            status =
                $"{roi.Value.Width}x{roi.Value.Height} ({backend}/{follow}) show={roi.Value.ShouldShow} {_capStatus}";
        }

        _tray.Text = $"CPURacer — {status}";
        if (_statusItem is not null)
        {
            _statusItem.Text = $"状态: {status}";
        }
    }

    private void ToggleTracking()
    {
        if (_watcher is null || _trackItem is null || _overlay is null || _captureTimer is null)
        {
            return;
        }

        if (_watcher.IsTracking)
        {
            _captureTimer.Stop();
            _watcher.Stop();
            _trackItem.Text = "开始跟踪 Taskmgr";
            _capStatus = "";
            _captureFailStreak = 0;
            _overlay.SetHeightField(null);
            _overlay.ApplyRoi(null);
            UpdateTrayTip(null);
            return;
        }

        if (!TrackNativeApi.IsAvailable())
        {
            Forms.MessageBox.Show(
                "未找到 CPURacer.TrackNative.dll。\n请先运行仓库根目录的 build.cmd（一行构建），再启动。",
                "CPURacer",
                Forms.MessageBoxButtons.OK,
                Forms.MessageBoxIcon.Warning);
        }

        _overlay.FollowMode = _followMode;
        _watcher.SetFollowMode(_followMode);
        _watcher.Start();
        _trackItem.Text = "停止跟踪 Taskmgr";
        _captureFailStreak = 0;
        _capStatus = "cap=…";
        _captureTimer.Start();
        UpdateTrayTip(_watcher.CurrentRoi);
    }

    private void ToggleOverlayManual()
    {
        if (_overlay is null || _overlayItem is null)
        {
            return;
        }

        if (_overlay.ForceVisible)
        {
            _overlay.HidePlaceholder();
            _overlayItem.Checked = false;
            _overlayItem.Text = "手动显示 Overlay";
        }
        else
        {
            _overlay.ShowDebugChrome = _debugOverlay;
            _overlay.ShowPlaceholder();
            _overlayItem.Checked = true;
            _overlayItem.Text = "取消手动显示";
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _captureTimer?.Stop();
        _captureTimer = null;

        _watcher?.Dispose();
        _watcher = null;

        if (_overlay is not null)
        {
            _overlay.Close();
            _overlay = null;
        }

        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }

        base.OnExit(e);
    }
}
