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
        _watcher.RoiChanged += _ =>
        {
            // M1: align overlay to ROI
        };

        _tray = new Forms.NotifyIcon
        {
            Text = "CPURacer",
            Visible = true,
            Icon = SystemIcons.Application,
        };

        var menu = new Forms.ContextMenuStrip();
        var trackItem = new Forms.ToolStripMenuItem("开始跟踪 Taskmgr（M1）") { Name = "track" };
        trackItem.Click += (_, _) => ToggleTracking(trackItem);
        menu.Items.Add(trackItem);

        var overlayItem = new Forms.ToolStripMenuItem("显示空 Overlay") { Name = "overlay", Checked = false };
        overlayItem.Click += (_, _) => ToggleOverlay(overlayItem);
        menu.Items.Add(overlayItem);

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

        menu.Items.Add(new Forms.ToolStripSeparator());

        var exitItem = new Forms.ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => ExitApp();
        menu.Items.Add(exitItem);

        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ToggleOverlay(overlayItem);

        // Keep a hidden WPF window so the dispatcher stays alive cleanly.
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

    private void ToggleTracking(Forms.ToolStripMenuItem item)
    {
        if (_watcher is null)
        {
            return;
        }

        if (_tracking)
        {
            _watcher.Stop();
            _tracking = false;
            item.Text = "开始跟踪 Taskmgr（M1）";
        }
        else
        {
            _watcher.Start();
            _tracking = true;
            item.Text = "停止跟踪 Taskmgr";
            Forms.MessageBox.Show(
                "跟踪已开启（M0 占位）。M1 将定位 CvChartWindow 并钉住 Overlay。",
                "CPURacer",
                Forms.MessageBoxButtons.OK,
                Forms.MessageBoxIcon.Information);
        }
    }

    private void ToggleOverlay(Forms.ToolStripMenuItem item)
    {
        if (_overlay is null)
        {
            return;
        }

        if (_overlay.IsVisible)
        {
            _overlay.HidePlaceholder();
            item.Checked = false;
            item.Text = "显示空 Overlay";
        }
        else
        {
            _overlay.ShowDebugChrome = _debugOverlay;
            _overlay.ShowPlaceholder();
            item.Checked = true;
            item.Text = "隐藏空 Overlay";
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
