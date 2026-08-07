using System.Drawing;
using System.Windows;
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
    private TrackFollowMode _followMode = TrackFollowMode.External;

    protected override void OnStartup(StartupEventArgs e)
    {
        Forms.Application.SetHighDpiMode(Forms.HighDpiMode.PerMonitorV2);
        base.OnStartup(e);

        _overlay = new OverlayWindow { ShowDebugChrome = _debugOverlay, FollowMode = _followMode };
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

        _statusItem = new Forms.ToolStripMenuItem("状态: 未跟踪") { Enabled = false };
        menu.Items.Add(_statusItem);
        menu.Items.Add(new Forms.ToolStripSeparator());

        var exitItem = new Forms.ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => Shutdown();
        menu.Items.Add(exitItem);

        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ToggleOverlayManual();
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
        var status = roi is null
            ? $"tracking ({backend}/{follow}, no CPU chart)"
            : $"{roi.Value.Width}x{roi.Value.Height} ({backend}/{follow}) show={roi.Value.ShouldShow}";

        _tray.Text = $"CPURacer — {status}";
        if (_statusItem is not null)
        {
            _statusItem.Text = $"状态: {status}";
        }
    }

    private void ToggleTracking()
    {
        if (_watcher is null || _trackItem is null || _overlay is null)
        {
            return;
        }

        if (_watcher.IsTracking)
        {
            _watcher.Stop();
            _trackItem.Text = "开始跟踪 Taskmgr";
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
