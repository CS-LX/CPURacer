using System.Drawing;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using CPURacer.Capture;
using CPURacer.Game;
using CPURacer.Localization;
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
    private Forms.ToolStripMenuItem? _raceItem;
    private Forms.ToolStripMenuItem? _restartItem;
    private Forms.ToolStripMenuItem? _debugItem;
    private Forms.ToolStripMenuItem? _fitItem;
    private Forms.ToolStripMenuItem? _advancedItem;
    private Forms.ToolStripMenuItem? _exitItem;
    private Forms.ToolStripMenuItem? _followMenuItem;
    private Forms.ToolStripMenuItem? _langMenuItem;
    private Forms.ToolStripMenuItem? _langEnItem;
    private Forms.ToolStripMenuItem? _langZhItem;
    private OverlayWindow? _overlay;
    private NativeExternalOverlay? _nativeOverlay;
    private TaskmgrWatcher? _watcher;
    private bool _debugOverlay;
    private bool _showFitPolyline;
    private bool _adminTipShown;
    private bool _tabWasDown;
    private TrackFollowMode _followMode = TrackFollowMode.External;

    private readonly ScreenRoiCapture _screenCapture = new();
    private readonly TaskmgrWindowCapture _windowCapture = new();
    private readonly HeightFieldExtractor _extractor = new();
    private readonly RaceSim _race = new();
    private DispatcherTimer? _captureTimer;
    private DispatcherTimer? _externalTimer;
    private DispatcherTimer? _raceTimer;
    private int _captureFailStreak;
    private int _externalFrameFailStreak;
    private string _capStatus = "";
    private int _trayTipFrame;
    private bool _raceWanted;
    private bool _spaceWasDown;
    private TimeSpan _lastRaceTime;

    protected override void OnStartup(StartupEventArgs e)
    {
        Forms.Application.SetHighDpiMode(Forms.HighDpiMode.PerMonitorV2);
        base.OnStartup(e);
        Locale.ApplyFromOs();
        Locale.Changed += OnLocaleChanged;
        GameInput.Install();

        _overlay = new OverlayWindow
        {
            ShowDebugChrome = _debugOverlay,
            ShowFitPolyline = _showFitPolyline,
            FollowMode = TrackFollowMode.Child,
        };
        _nativeOverlay = new NativeExternalOverlay
        {
            ShowDebugChrome = _debugOverlay,
            ShowFitPolyline = _showFitPolyline,
        };
        // Terrain egress only — never run physics inside TickExternalFrame.
        _overlay.HeightFieldUpdated += OnHeightFieldUpdated;
        _nativeOverlay.HeightFieldUpdated += OnHeightFieldUpdated;

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

        // Child capture remains at 30 Hz.
        _captureTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33),
        };
        _captureTimer.Tick += (_, _) => CaptureTickChild();

        // Native External HWND does not participate in WPF composition. A dedicated
        // dispatcher clock must keep ticking while the zero-sized WPF shell is hidden.
        _externalTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _externalTimer.Tick += OnExternalTick;

        // Bypass clock for RaceSim — must not share OnExternalTick / TickExternalFrame.
        _raceTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _raceTimer.Tick += OnRaceHostTick;

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

        // Player UX: watch Taskmgr immediately (lunar _watcher.Start on launch).
        StartTracking();

        // UIPI: non-admin vs elevated Taskmgr — tip once at launch (not only on race start).
        MaybeWarnNonAdmin();
    }

    private void BuildTray()
    {
        var trayIcon = LoadAppIcon() ?? SystemIcons.Application;
        _tray = new Forms.NotifyIcon
        {
            Text = Strings.TipOpenCpu,
            Visible = true,
            Icon = trayIcon,
        };

        var menu = new Forms.ContextMenuStrip();

        _raceItem = new Forms.ToolStripMenuItem();
        _raceItem.Click += (_, _) => ToggleRace();
        menu.Items.Add(_raceItem);

        _restartItem = new Forms.ToolStripMenuItem { Enabled = false };
        _restartItem.Click += (_, _) => RestartRace();
        menu.Items.Add(_restartItem);

        menu.Items.Add(new Forms.ToolStripSeparator());

        _advancedItem = new Forms.ToolStripMenuItem();

        _trackItem = new Forms.ToolStripMenuItem();
        _trackItem.Click += (_, _) => ToggleTracking();
        _advancedItem.DropDownItems.Add(_trackItem);

        _followMenuItem = new Forms.ToolStripMenuItem();
        _followExternalItem = new Forms.ToolStripMenuItem { CheckOnClick = true };
        _followChildItem = new Forms.ToolStripMenuItem { CheckOnClick = true };
        _followExternalItem.Click += (_, _) => SetFollowMode(TrackFollowMode.External);
        _followChildItem.Click += (_, _) => SetFollowMode(TrackFollowMode.Child);
        _followMenuItem.DropDownItems.Add(_followExternalItem);
        _followMenuItem.DropDownItems.Add(_followChildItem);
        _advancedItem.DropDownItems.Add(_followMenuItem);
        SyncFollowMenu();

        _overlayItem = new Forms.ToolStripMenuItem();
        _overlayItem.Click += (_, _) => ToggleOverlayManual();
        _advancedItem.DropDownItems.Add(_overlayItem);

        _debugItem = new Forms.ToolStripMenuItem { Checked = _debugOverlay, CheckOnClick = true };
        _debugItem.CheckedChanged += (_, _) =>
        {
            if (_debugItem is null)
            {
                return;
            }

            SetDebugChrome(_debugItem.Checked, syncMenu: false);
        };
        _advancedItem.DropDownItems.Add(_debugItem);

        _fitItem = new Forms.ToolStripMenuItem { Checked = _showFitPolyline, CheckOnClick = true };
        _fitItem.CheckedChanged += (_, _) =>
        {
            if (_fitItem is null)
            {
                return;
            }

            _showFitPolyline = _fitItem.Checked;
            if (_overlay is not null)
            {
                _overlay.ShowFitPolyline = _showFitPolyline;
            }

            if (_nativeOverlay is not null)
            {
                _nativeOverlay.ShowFitPolyline = _showFitPolyline;
            }
        };
        _advancedItem.DropDownItems.Add(_fitItem);

        _langMenuItem = new Forms.ToolStripMenuItem();
        _langEnItem = new Forms.ToolStripMenuItem { CheckOnClick = true };
        _langZhItem = new Forms.ToolStripMenuItem { CheckOnClick = true };
        _langEnItem.Click += (_, _) => Locale.SetEnglish();
        _langZhItem.Click += (_, _) => Locale.SetChinese();
        _langMenuItem.DropDownItems.Add(_langEnItem);
        _langMenuItem.DropDownItems.Add(_langZhItem);
        _advancedItem.DropDownItems.Add(_langMenuItem);

        _statusItem = new Forms.ToolStripMenuItem { Enabled = false };
        _advancedItem.DropDownItems.Add(_statusItem);
        menu.Items.Add(_advancedItem);
        menu.Items.Add(new Forms.ToolStripSeparator());

        _exitItem = new Forms.ToolStripMenuItem();
        _exitItem.Click += (_, _) => Shutdown();
        menu.Items.Add(_exitItem);

        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ToggleRace();
        ApplyTrayStrings();
    }

    private void OnLocaleChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(OnLocaleChanged);
            return;
        }

        ApplyTrayStrings();
        RefreshStagePrompts();
        UpdateTrayTip(_watcher?.CurrentRoi);
    }

    private void ApplyTrayStrings()
    {
        if (_raceItem is not null)
        {
            var active = _raceWanted || _race.IsRunning;
            _raceItem.Text = active ? Strings.TrayStop : Strings.TrayStart;
        }

        if (_restartItem is not null)
        {
            _restartItem.Text = Strings.TrayRestart;
        }

        if (_advancedItem is not null)
        {
            _advancedItem.Text = Strings.TrayAdvanced;
        }

        if (_trackItem is not null)
        {
            _trackItem.Text = _watcher?.IsTracking == true
                ? Strings.TrayPauseWatch
                : Strings.TrayResumeWatch;
        }

        if (_followMenuItem is not null)
        {
            _followMenuItem.Text = Strings.TrayFollowMode;
        }

        if (_followExternalItem is not null)
        {
            _followExternalItem.Text = Strings.TrayFollowExternal;
        }

        if (_followChildItem is not null)
        {
            _followChildItem.Text = Strings.TrayFollowChild;
        }

        if (_overlayItem is not null)
        {
            _overlayItem.Text = _overlay?.ForceVisible == true
                ? Strings.TrayManualOverlayCancel
                : Strings.TrayManualOverlay;
        }

        if (_debugItem is not null)
        {
            _debugItem.Text = Strings.TrayDebugChrome;
        }

        if (_fitItem is not null)
        {
            _fitItem.Text = Strings.TrayDebugFit;
        }

        if (_langMenuItem is not null)
        {
            _langMenuItem.Text = Strings.TrayLanguage;
        }

        if (_langEnItem is not null)
        {
            _langEnItem.Text = Strings.TrayLanguageEn;
            _langEnItem.Checked = !Locale.IsChinese;
        }

        if (_langZhItem is not null)
        {
            _langZhItem.Text = Strings.TrayLanguageZh;
            _langZhItem.Checked = Locale.IsChinese;
        }

        if (_exitItem is not null)
        {
            _exitItem.Text = Strings.TrayExit;
        }

        SyncRaceMenu();
    }

    private void SetCenterPrompt(string? expanded)
    {
        if (_nativeOverlay is not null)
        {
            _nativeOverlay.CenterPrompt = expanded;
        }

        if (_overlay is not null)
        {
            _overlay.CenterPrompt = expanded;
        }
    }

    private void ClearPlayerBanners()
    {
        if (_nativeOverlay is not null)
        {
            _nativeOverlay.PlayerBanner = null;
        }

        if (_overlay is not null)
        {
            _overlay.PlayerBanner = null;
        }
    }

    private void RefreshStagePrompts()
    {
        if (_race.IsDead)
        {
            SetCenterPrompt(FigglePrompt.FormatExpand(
                Strings.PromptGameOver,
                _race.DistanceMeters,
                _race.BestDistanceMeters));
            ClearPlayerBanners();
            return;
        }

        if (_raceWanted || _race.IsRunning)
        {
            SetCenterPrompt(null);
            return;
        }

        UpdateIdleBanners();
    }

    /// <summary>Child-only capture path (External uses TickExternalFrame).</summary>
    private void CaptureTickChild()
    {
        if (_overlay is null || _watcher is null || !_watcher.IsTracking)
        {
            return;
        }

        if (_followMode != TrackFollowMode.Child)
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

        var frame = _screenCapture.TryCapture(roi.Value);

        if (frame is null)
        {
            _captureFailStreak++;
            _capStatus = $"cap=fail({_captureFailStreak})";
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
            _capStatus = $"cap={_screenCapture.Name}-ok extract=skip";
            _overlay.SetCaptureStatus(_capStatus);
        }
        else
        {
            _capStatus = $"cap={_screenCapture.Name}-ok cols={field.PlotWidth}";
            _overlay.SetHeightField(field, _capStatus);
        }

        UpdateTrayTip(roi);
    }

    private void OnExternalTick(object? sender, EventArgs e)
    {
        if (_nativeOverlay is null || _watcher is null || !_watcher.IsTracking)
        {
            return;
        }

        if (_followMode != TrackFollowMode.External)
        {
            return;
        }

        try
        {
            _nativeOverlay.TickExternalFrame(_windowCapture, _extractor);
            _externalFrameFailStreak = 0;
        }
        catch (Exception ex)
        {
            // One transient placement/drawing failure must not stop the dispatcher
            // clock. Keep the previous native frame and retry on the next tick.
            _externalFrameFailStreak++;
            if (_externalFrameFailStreak == 1 || _externalFrameFailStreak % 60 == 0)
            {
                Debug.WriteLine(
                    $"External overlay frame failed ({_externalFrameFailStreak}): {ex}");
            }

            return;
        }

        // Throttle tray text updates; NotifyIcon churn every frame is expensive.
        if ((++_trayTipFrame & 15) == 0)
        {
            UpdateTrayTip(_watcher.CurrentRoi);
        }
    }

    private void OnHeightFieldUpdated(HeightField field)
    {
        if (!_raceWanted && !_race.IsRunning && !_race.IsDead)
        {
            return;
        }

        _race.SetTerrain(field);
        if (_raceWanted && !_race.IsRunning && !_race.IsDead)
        {
            _race.Start();
            SyncRaceMenu();
        }
    }

    /// <summary>
    /// Bypass clock: physics + SetCarPose only. Must not mutate tray/menu every frame.
    /// </summary>
    private void OnRaceHostTick(object? sender, EventArgs e)
    {
        PollDebugToggle();

        if (!_raceWanted && !_race.IsRunning && !_race.IsDead)
        {
            UpdateIdleBanners();
            var idleSpace = GameInput.RestartPressed;
            if (idleSpace && !_spaceWasDown)
            {
                TryHotkeyStartRace();
            }

            _spaceWasDown = idleSpace;
            return;
        }

        if (_race.IsDead && !_race.IsRunning)
        {
            SetCenterPrompt(FigglePrompt.FormatExpand(
                Strings.PromptGameOver,
                _race.DistanceMeters,
                _race.BestDistanceMeters));
            ClearPlayerBanners();
            var deadSpace = GameInput.RestartPressed;
            if (deadSpace && !_spaceWasDown)
            {
                RestartRace();
            }

            _spaceWasDown = deadSpace;

            var deadCar = _race.GetCarState();
            if (_followMode == TrackFollowMode.External)
            {
                _nativeOverlay?.SetCarPose(deadCar);
            }
            else
            {
                _overlay?.SetCarPose(deadCar);
            }

            return;
        }

        SetCenterPrompt(null);

        var now = TimeSpan.FromTicks(DateTime.UtcNow.Ticks);
        if (_lastRaceTime == TimeSpan.Zero)
        {
            _lastRaceTime = now;
        }

        var dt = (now - _lastRaceTime).TotalSeconds;
        _lastRaceTime = now;
        if (dt <= 0 || dt > 0.25)
        {
            dt = 1.0 / 60.0;
        }

        var space = GameInput.RestartPressed;
        if (space && !_spaceWasDown)
        {
            RestartRace();
        }

        _spaceWasDown = space;

        var wasRunning = _race.IsRunning;
        var throttle = GameInput.ThrottleDown;
        var brake = GameInput.BrakeDown;
        if (_race.IsRunning)
        {
            _race.SetInput(throttle, brake);
            _race.Step(dt);
        }

        var car = _race.GetCarState();
        if (_followMode == TrackFollowMode.External)
        {
            if (_nativeOverlay is not null)
            {
                _nativeOverlay.PlayerBanner = null;
                _nativeOverlay.SetCarPose(car);
            }
        }
        else if (_overlay is not null)
        {
            _overlay.PlayerBanner = null;
            _overlay.SetCarPose(car);
        }

        // Edge-only menu sync (e.g. running → dead). Never every-frame SyncRaceMenu.
        if (wasRunning != _race.IsRunning)
        {
            SyncRaceMenu();
        }
    }

    private void PollDebugToggle()
    {
        var tab = GameInput.DebugToggleDown;
        if (tab && !_tabWasDown)
        {
            SetDebugChrome(!_debugOverlay, syncMenu: true);
        }

        _tabWasDown = tab;
    }

    private void SetDebugChrome(bool on, bool syncMenu)
    {
        _debugOverlay = on;
        if (_overlay is not null)
        {
            _overlay.ShowDebugChrome = on;
        }

        if (_nativeOverlay is not null)
        {
            _nativeOverlay.ShowDebugChrome = on;
        }

        if (syncMenu && _debugItem is not null && _debugItem.Checked != on)
        {
            _debugItem.Checked = on;
        }
    }

    private void UpdateIdleBanners()
    {
        if (_watcher?.IsTracking != true)
        {
            ClearBanners();
            return;
        }

        var field = _followMode == TrackFollowMode.External
            ? _nativeOverlay?.CurrentHeightField
            : _overlay?.CurrentHeightField;
        var roi = _watcher.CurrentRoi;

        string tip;
        if (_capStatus.StartsWith("cap=fail", StringComparison.Ordinal)
            || _capStatus.StartsWith("cap=ext-fail", StringComparison.Ordinal))
        {
            tip = Strings.PromptCaptureFail;
        }
        else if (roi is null || field is null)
        {
            tip = Strings.PromptWaitingChart;
        }
        else
        {
            tip = Strings.PromptIdle;
        }

        ClearPlayerBanners();
        SetCenterPrompt(FigglePrompt.Expand(tip));
        if (_followMode == TrackFollowMode.External)
        {
            _nativeOverlay?.SetCarPose(null);
        }
        else
        {
            _overlay?.SetCarPose(null);
        }
    }

    private void ClearBanners()
    {
        ClearPlayerBanners();
        SetCenterPrompt(null);
    }

    private void SyncPipelineClocks()
    {
        if (_watcher?.IsTracking != true)
        {
            StopExternalLoop();
            StopRaceHostLoop();
            _captureTimer?.Stop();
            return;
        }

        if (_followMode == TrackFollowMode.External)
        {
            _captureTimer?.Stop();
            StartExternalLoop();
        }
        else
        {
            StopExternalLoop();
            _captureTimer?.Start();
        }

        SyncRaceHostLoop();
    }

    private void SyncRaceHostLoop()
    {
        // Keep host alive while tracking so idle Play banners / Tab toggle work.
        var need = _raceWanted || _race.IsRunning || _race.IsDead || _watcher?.IsTracking == true;
        if (need)
        {
            StartRaceHostLoop();
        }
        else
        {
            StopRaceHostLoop();
            ClearBanners();
        }
    }

    private void StartExternalLoop()
    {
        _externalTimer?.Start();
    }

    private void StopExternalLoop()
    {
        _externalTimer?.Stop();
    }

    private void StartRaceHostLoop()
    {
        if (_raceTimer is null || _raceTimer.IsEnabled)
        {
            return;
        }

        _lastRaceTime = TimeSpan.Zero;
        _raceTimer.Start();
    }

    private void StopRaceHostLoop()
    {
        _raceTimer?.Stop();
    }

    private void ToggleRace()
    {
        if (_raceWanted || _race.IsRunning)
        {
            _raceWanted = false;
            _race.Stop();
            _overlay?.SetCarPose(null);
            _nativeOverlay?.SetCarPose(null);
            SyncRaceHostLoop();
            SyncRaceMenu();
            UpdateTrayTip(_watcher?.CurrentRoi);
            return;
        }

        if (_watcher?.IsTracking != true)
        {
            Forms.MessageBox.Show(
                Strings.MsgWatchPaused,
                "CPURacer",
                Forms.MessageBoxButtons.OK,
                Forms.MessageBoxIcon.Information);
            return;
        }

        var field = _followMode == TrackFollowMode.External
            ? _nativeOverlay?.CurrentHeightField
            : _overlay?.CurrentHeightField;
        if (field is null)
        {
            Forms.MessageBox.Show(
                Strings.MsgNeedCpuChart,
                "CPURacer",
                Forms.MessageBoxButtons.OK,
                Forms.MessageBoxIcon.Information);
            return;
        }

        StartRaceCore(warnAdmin: true);
    }

    /// <summary>Space while idle: start race without tray click (External is click-through).</summary>
    private void TryHotkeyStartRace()
    {
        if (_watcher?.IsTracking != true)
        {
            return;
        }

        var field = _followMode == TrackFollowMode.External
            ? _nativeOverlay?.CurrentHeightField
            : _overlay?.CurrentHeightField;
        if (field is null)
        {
            return;
        }

        StartRaceCore(warnAdmin: false);
    }

    private void StartRaceCore(bool warnAdmin)
    {
        if (warnAdmin)
        {
            MaybeWarnNonAdmin();
        }

        _raceWanted = true;
        _lastRaceTime = TimeSpan.Zero;
        var field = _followMode == TrackFollowMode.External
            ? _nativeOverlay?.CurrentHeightField
            : _overlay?.CurrentHeightField;
        if (field is not null)
        {
            _race.SetTerrain(field);
            _race.Start();
        }

        SetCenterPrompt(null);
        SyncRaceHostLoop();
        SyncRaceMenu();
        UpdateTrayTip(_watcher?.CurrentRoi);
    }

    private void MaybeWarnNonAdmin()
    {
        if (_adminTipShown || IsUserAnAdmin())
        {
            return;
        }

        _adminTipShown = true;
        Forms.MessageBox.Show(
            Strings.MsgAdminUipi,
            "CPURacer",
            Forms.MessageBoxButtons.OK,
            Forms.MessageBoxIcon.Information);
    }

    private void RestartRace()
    {
        if (!_raceWanted && !_race.IsDead && !_race.IsRunning)
        {
            return;
        }

        _raceWanted = true;
        _lastRaceTime = TimeSpan.Zero;
        var field = _followMode == TrackFollowMode.External
            ? _nativeOverlay?.CurrentHeightField
            : _overlay?.CurrentHeightField;
        if (field is not null)
        {
            _race.SetTerrain(field);
        }

        _race.Restart();
        SyncRaceHostLoop();
        SyncRaceMenu();
    }

    private void SyncRaceMenu()
    {
        if (_raceItem is not null)
        {
            var active = _raceWanted || _race.IsRunning;
            _raceItem.Text = active ? Strings.TrayStop : Strings.TrayStart;
        }

        if (_restartItem is not null)
        {
            _restartItem.Enabled = _raceWanted || _race.IsRunning || _race.IsDead;
            _restartItem.Text = Strings.TrayRestart;
        }
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
        if (_watcher?.IsTracking == true)
        {
            if (mode == TrackFollowMode.External)
            {
                _overlay?.SetCarPose(null);
                _overlay?.ClearRoi();
                _nativeOverlay?.ApplyRoi(_watcher.CurrentRoi);
            }
            else
            {
                _nativeOverlay?.SetCarPose(null);
                _nativeOverlay?.ClearRoi();
                if (_overlay is not null)
                {
                    _overlay.FollowMode = TrackFollowMode.Child;
                    _overlay.ApplyRoi(_watcher.CurrentRoi);
                }
            }
        }

        SyncPipelineClocks();
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
        if (_followMode == TrackFollowMode.External)
        {
            _nativeOverlay?.ApplyRoi(roi);
        }
        else
        {
            _overlay?.ApplyRoi(roi);
        }

        UpdateTrayTip(roi);
    }

    private void UpdateTrayTip(ChartRoi? roi)
    {
        if (_tray is null)
        {
            return;
        }

        // Player tip stays short; engineering status only under Advanced.
        string playerTip;
        if (_watcher?.IsTracking != true)
        {
            playerTip = Strings.TipPausedWatch;
        }
        else if (_race.IsRunning)
        {
            playerTip = Strings.TipRacing;
        }
        else if (_race.IsDead)
        {
            playerTip = Strings.TipGameOver;
        }
        else if (roi is null)
        {
            playerTip = Strings.TipOpenCpu;
        }
        else
        {
            playerTip = Strings.TipSpaceStart;
        }

        _tray.Text = playerTip.Length <= 63 ? playerTip : playerTip[..63];

        if (_statusItem is null || _watcher is null)
        {
            return;
        }

        if (!_watcher.IsTracking)
        {
            _statusItem.Text = Strings.TrayStatusPrefix + Strings.TrayStatusPaused;
            return;
        }

        var backend = _watcher.UsingNativeTracker ? "native" : "managed";
        var follow = _followMode == TrackFollowMode.Child ? "child" : "external";
        var race = _race.IsDead ? "dead" : _race.IsRunning ? "race" : _raceWanted ? "wait" : "idle";
        if (roi is null)
        {
            _statusItem.Text =
                $"{Strings.TrayStatusPrefix}tracking ({backend}/{follow}, no CPU chart)";
            return;
        }

        var show = _followMode == TrackFollowMode.External && _nativeOverlay is not null
            ? _nativeOverlay.IsVisible
            : roi.Value.ShouldShow;
        var cap = _followMode == TrackFollowMode.External && _nativeOverlay is not null
            ? _nativeOverlay.DiagnosticStatus
            : _capStatus;
        _statusItem.Text =
            $"{Strings.TrayStatusPrefix}{roi.Value.Width}x{roi.Value.Height} ({backend}/{follow}) show={show} {cap} {race}";
    }

    private void ToggleTracking()
    {
        if (_watcher?.IsTracking == true)
        {
            StopTracking();
        }
        else
        {
            StartTracking();
        }
    }

    private void StartTracking()
    {
        if (_watcher is null
            || _trackItem is null
            || _overlay is null
            || _nativeOverlay is null
            || _captureTimer is null
            || _watcher.IsTracking)
        {
            return;
        }

        if (!TrackNativeApi.IsAvailable())
        {
            Forms.MessageBox.Show(
                Strings.MsgTrackNativeMissing,
                "CPURacer",
                Forms.MessageBoxButtons.OK,
                Forms.MessageBoxIcon.Warning);
        }

        _overlay.FollowMode = TrackFollowMode.Child;
        _watcher.SetFollowMode(_followMode);
        _watcher.Start();
        _trackItem.Text = Strings.TrayPauseWatch;
        _captureFailStreak = 0;
        _externalFrameFailStreak = 0;
        _capStatus = "cap=…";
        if (_followMode == TrackFollowMode.External)
        {
            _nativeOverlay.ApplyRoi(_watcher.CurrentRoi);
        }
        else
        {
            _overlay.ApplyRoi(_watcher.CurrentRoi);
        }

        SyncPipelineClocks();
        UpdateTrayTip(_watcher.CurrentRoi);
    }

    private void StopTracking()
    {
        if (_watcher is null
            || _trackItem is null
            || _overlay is null
            || _nativeOverlay is null
            || _captureTimer is null
            || !_watcher.IsTracking)
        {
            return;
        }

        _raceWanted = false;
        _race.Stop();
        _overlay.SetCarPose(null);
        _nativeOverlay.SetCarPose(null);
        StopRaceHostLoop();
        StopExternalLoop();
        _captureTimer.Stop();
        _watcher.Stop();
        _trackItem.Text = Strings.TrayResumeWatch;
        _capStatus = "";
        _captureFailStreak = 0;
        _externalFrameFailStreak = 0;
        _overlay.SetHeightField(null);
        _overlay.ClearRoi();
        _nativeOverlay.ClearRoi();
        ClearBanners();
        SyncRaceMenu();
        UpdateTrayTip(null);
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
            _overlayItem.Text = Strings.TrayManualOverlay;
        }
        else
        {
            _overlay.ShowDebugChrome = _debugOverlay;
            _overlay.ShowPlaceholder();
            _overlayItem.Checked = true;
            _overlayItem.Text = Strings.TrayManualOverlayCancel;
        }
    }

    private static Icon? LoadAppIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path))
            {
                return Icon.ExtractAssociatedIcon(path);
            }
        }
        catch
        {
            // fall through
        }

        return null;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Locale.Changed -= OnLocaleChanged;
        _raceWanted = false;
        _race.Stop();
        StopRaceHostLoop();
        StopExternalLoop();
        _captureTimer?.Stop();
        _captureTimer = null;
        _externalTimer = null;
        _raceTimer = null;
        GameInput.Uninstall();
        _windowCapture.Dispose();

        _watcher?.Dispose();
        _watcher = null;

        if (_overlay is not null)
        {
            _overlay.HeightFieldUpdated -= OnHeightFieldUpdated;
            _overlay.Close();
            _overlay = null;
        }

        if (_nativeOverlay is not null)
        {
            _nativeOverlay.HeightFieldUpdated -= OnHeightFieldUpdated;
            _nativeOverlay.Dispose();
            _nativeOverlay = null;
        }

        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }

        base.OnExit(e);
    }

    [DllImport("shell32.dll")]
    private static extern bool IsUserAnAdmin();
}
