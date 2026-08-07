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
    private OverlayWindow? _overlay;
    private TaskmgrWatcher? _watcher;
    private bool _tracking;
    private bool _debugOverlay = true;

    protected override void OnStartup(StartupEventArgs e)
    {
        Forms.Application.SetHighDpiMode(Forms.HighDpiMode.PerMonitorV2);
        base.OnStartup(e);

        _overlay = new OverlayWindow { ShowDebugChrome = _debugOverlay };
        _watcher = new TaskmgrWatcher();
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

        _tray = new Forms.NotifyIcon
        {
            Text = "CPURacer",
            Visible = true,
            Icon = SystemIcons.Application,
        };

        var menu = new Forms.ContextMenuStrip();
        _trackItem = new Forms.ToolStripMenuItem("开始跟踪 Taskmgr") { Name = "track" };
        _trackItem.Click += (_, _) => ToggleTracking();
        menu.Items.Add(_trackItem);

        _overlayItem = new Forms.ToolStripMenuItem("手动显示 Overlay") { Name = "overlay", Checked = false };
        _overlayItem.Click += (_, _) => ToggleOverlayManual();
        menu.Items.Add(_overlayItem);

        var debugItem = new Forms.ToolStripMenuItem("调试描边") { Name = "debug", Checked = _debugOverlay, CheckOnClick = true };
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

        _statusItem = new Forms.ToolStripMenuItem("状态: 未跟踪") { Name = "status", Enabled = false };
        menu.Items.Add(_statusItem);

        menu.Items.Add(new Forms.ToolStripSeparator());

        var exitItem = new Forms.ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => ExitApp();
        menu.Items.Add(exitItem);

        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ToggleOverlayManual();

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

        if (!_tracking)
        {
            _tray.Text = "CPURacer";
            if (_statusItem is not null)
            {
                _statusItem.Text = "状态: 未跟踪";
            }

            return;
        }

        var mode = _watcher?.UsingNativeTracker == true ? "native" : "managed";
        string status;
        if (roi is null)
        {
            status = $"tracking ({mode}, no CPU chart)";
            _tray.Text = $"CPURacer — {status}";
        }
        else
        {
            status = $"{roi.Value.Width}x{roi.Value.Height} ({mode}) show={roi.Value.ShouldShow}";
            _tray.Text = $"CPURacer — {status}";
        }

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

        if (_tracking)
        {
            _watcher.Stop();
            _tracking = false;
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

        _watcher.Start();
        _tracking = true;
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

    private void ExitApp()
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

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _watcher?.Dispose();
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }

        base.OnExit(e);
    }
}
