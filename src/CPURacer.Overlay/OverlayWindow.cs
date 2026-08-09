using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using CPURacer.Capture;
using CPURacer.Game;
using CPURacer.Native;
using CPURacer.Taskmgr;

namespace CPURacer.Overlay;

/// <summary>
/// WPF Child overlay retained for the SetParent / TaskmgrPlayer-style path.
/// External rendering is owned by <see cref="NativeExternalOverlay"/>.
/// </summary>
public sealed class OverlayWindow : Window
{
    private const double OffscreenTop = 20000;

    private ChartRoi? _roi;
    private bool _forceVisible;
    private bool _showDebugChrome;
    private bool _showFitPolyline;
    private string _statusText = "CPURacer";
    private string? _playerBanner;
    private string? _centerPrompt;
    private TrackFollowMode _followMode = TrackFollowMode.External;
    private IntPtr _attachedParent = IntPtr.Zero;
    private bool _childStylesApplied;
    private int _savedStyle;
    private int _savedExStyle;
    private int _lastChildW = -1;
    private int _lastChildH = -1;
    private HeightField? _heightField;
    private CarState? _carPose;
    private string _captureStatus = "";
    private readonly DrawingGroup _backingStore = new();

    /// <summary>Latest reliable terrain for RaceHost (read-only).</summary>
    public HeightField? CurrentHeightField => _heightField;

    /// <summary>Raised when a new height field is set (M3 thin egress).</summary>
    public event Action<HeightField>? HeightFieldUpdated;

    public OverlayWindow()
    {
        Title = "CPURacer Overlay";
        AllowsTransparency = true;
        WindowStyle = WindowStyle.None;
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Focusable = true;
        Width = 400;
        Height = 200;
        Left = 120;
        Top = OffscreenTop;
        SourceInitialized += (_, _) =>
        {
            var hwnd = EnsureHandle();
            // Keep the visible overlay out of desktop capture without hide/show flashing.
            _ = NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WdaExcludeFromCapture);
        };
    }

    public bool ShowDebugChrome
    {
        get => _showDebugChrome;
        set
        {
            if (_showDebugChrome == value)
            {
                return;
            }

            _showDebugChrome = value;
            RebuildBackingStore();
        }
    }

    /// <summary>Draw extracted height-field polyline (orange) for M2 fit check.</summary>
    public bool ShowFitPolyline
    {
        get => _showFitPolyline;
        set
        {
            if (_showFitPolyline == value)
            {
                return;
            }

            _showFitPolyline = value;
            RebuildBackingStore();
        }
    }

    /// <summary>Optional plot inset in physical pixels (M2+).</summary>
    public Thickness PlotInsetPx { get; set; }

    public TrackFollowMode FollowMode
    {
        get => _followMode;
        set
        {
            if (_followMode == value)
            {
                return;
            }

            DetachFromChart();
            _followMode = value;
            Topmost = value == TrackFollowMode.External;
            ApplyPlacement();
        }
    }

    public bool ForceVisible
    {
        get => _forceVisible;
        set
        {
            if (_forceVisible == value)
            {
                return;
            }

            _forceVisible = value;
            ApplyPlacement();
        }
    }

    public void ApplyRoi(ChartRoi? roi)
    {
        SetRoiCore(roi);
    }

    /// <summary>Definitively clear tracking state (tracking stopped / app shutdown).</summary>
    public void ClearRoi() => SetRoiCore(null);

    private void SetRoiCore(ChartRoi? roi)
    {
        _roi = roi;
        if (roi is null)
        {
            _statusText = "CPURacer — no CPU chart";
            _heightField = null;
            _carPose = null;
            DetachFromChart();
            HideOverlaySurface();
            RebuildBackingStore();
            return;
        }

        RefreshStatusText();

        if (FollowMode == TrackFollowMode.Child)
        {
            ApplyPlacement();
        }
        else
        {
            RebuildBackingStore();
        }
    }

    public void SetHeightField(HeightField? field, string captureStatus = "")
    {
        _heightField = field;
        _captureStatus = captureStatus;
        RefreshStatusText();
        RebuildBackingStore();
        if (field is not null)
        {
            HeightFieldUpdated?.Invoke(field);
        }
    }

    public void SetCaptureStatus(string captureStatus)
    {
        _captureStatus = captureStatus;
        RefreshStatusText();
        RebuildBackingStore();
    }

    /// <summary>Top-left player line (racing HUD).</summary>
    public string? PlayerBanner
    {
        get => _playerBanner;
        set
        {
            _playerBanner = value;
            RebuildBackingStore();
        }
    }

    /// <summary>Pre-expanded multiline prompt drawn centered.</summary>
    public string? CenterPrompt
    {
        get => _centerPrompt;
        set
        {
            _centerPrompt = value;
            RebuildBackingStore();
        }
    }

    public void SetCarPose(CarState? pose)
    {
        _carPose = pose;
        RebuildBackingStore();
    }

    public void ShowPlaceholder()
    {
        _forceVisible = true;
        if (_roi is null)
        {
            DetachFromChart();
            Width = 400;
            Height = 200;
            Left = 120;
            Top = 120;
            Topmost = true;
            _statusText = "CPURacer — manual overlay";
            EnsureWindowShown();
            RebuildBackingStore();
            return;
        }

        ApplyPlacement();
        RebuildBackingStore();
    }

    public void HidePlaceholder()
    {
        _forceVisible = false;
        Topmost = FollowMode == TrackFollowMode.External;
        ApplyPlacement();
        RebuildBackingStore();
    }

    protected override void OnClosed(EventArgs e)
    {
        DetachFromChart();
        base.OnClosed(e);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        drawingContext.DrawDrawing(_backingStore);
    }

    private void RebuildBackingStore()
    {
        var drawW = ActualWidth > 2 ? ActualWidth : Width;
        var drawH = ActualHeight > 2 ? ActualHeight : Height;
        var dc = _backingStore.Open();
        try
        {
            if (drawW > 2 && drawH > 2)
            {
                if (ShowFitPolyline && _heightField is { PlotWidth: > 1 } field)
                {
                    DrawFitPolyline(dc, field, drawW, drawH);
                }

                // TaskmgrPlayer ColorEdge RGB(12,125,187)
                const byte ar = 12, ag = 125, ab = 187;
                if (_carPose is { } car)
                {
                    DrawCar(dc, car, drawW, drawH);
                    if (!ShowDebugChrome && car.IsRunning && !car.IsDead)
                    {
                        DrawThrottleBar(dc, car, drawW, drawH, ar, ag, ab);
                    }
                }

                if (ShowDebugChrome)
                {
                    var pen = new Pen(new SolidColorBrush(Color.FromArgb(200, 220, 60, 60)), 2);
                    dc.DrawRectangle(
                        null,
                        pen,
                        new Rect(1, 1, drawW - 2, drawH - 2));

                    var hud = _carPose is { } pose ? pose.Hud : null;
                    var status = string.IsNullOrEmpty(hud)
                        ? _statusText
                        : $"{_statusText}  |  {hud}";
                    var text = new FormattedText(
                        status,
                        System.Globalization.CultureInfo.CurrentUICulture,
                        FlowDirection.LeftToRight,
                        new Typeface("Consolas"),
                        12,
                        new SolidColorBrush(Color.FromArgb(230, 220, 60, 60)),
                        VisualTreeHelper.GetDpi(this).PixelsPerDip);
                    dc.DrawText(text, new Point(10, 8));
                }
                else if (_carPose is { IsRunning: true, IsDead: false } running
                         && !string.IsNullOrEmpty(running.Hud))
                {
                    var text = new FormattedText(
                        running.Hud,
                        System.Globalization.CultureInfo.CurrentUICulture,
                        FlowDirection.LeftToRight,
                        new Typeface("Consolas"),
                        13,
                        new SolidColorBrush(Color.FromArgb(245, ar, ag, ab)),
                        VisualTreeHelper.GetDpi(this).PixelsPerDip);
                    dc.DrawText(text, new Point(8, 6));
                }
                else if (!string.IsNullOrEmpty(_playerBanner))
                {
                    var text = new FormattedText(
                        _playerBanner!,
                        System.Globalization.CultureInfo.CurrentUICulture,
                        FlowDirection.LeftToRight,
                        new Typeface("Consolas"),
                        13,
                        new SolidColorBrush(Color.FromArgb(245, ar, ag, ab)),
                        VisualTreeHelper.GetDpi(this).PixelsPerDip);
                    dc.DrawText(text, new Point(8, 6));
                }

                if (!ShowDebugChrome && !string.IsNullOrEmpty(_centerPrompt))
                {
                    DrawCenteredPrompt(dc, _centerPrompt!, drawW, drawH, ar, ag, ab);
                }
            }
        }
        finally
        {
            dc.Close();
        }

        InvalidateVisual();
    }

    private static void DrawFitPolyline(DrawingContext dc, HeightField field, double drawW, double drawH)
    {
        var sx = drawW / field.FrameWidth;
        var sy = drawH / field.FrameHeight;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            var inset = field.Inset;
            var first = true;
            for (var i = 0; i < field.PlotWidth; i++)
            {
                var x = (inset.Left + i + 0.5) * sx;
                var y = field.YFromTop[i] * sy;
                var pt = new Point(x, y);
                if (first)
                {
                    ctx.BeginFigure(pt, false, false);
                    first = false;
                }
                else
                {
                    ctx.LineTo(pt, true, false);
                }
            }
        }

        geo.Freeze();
        // Orange — must not look like Taskmgr blue, or BitBlt feedback slowly lifts the fit.
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(230, 255, 140, 0)), 2.0);
        pen.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }

    private void DrawCar(DrawingContext dc, CarState car, double drawW, double drawH)
    {
        var frameW = _heightField?.FrameWidth ?? _roi?.Width ?? (int)drawW;
        var frameH = _heightField?.FrameHeight ?? _roi?.Height ?? (int)drawH;
        if (frameW < 8 || frameH < 8)
        {
            return;
        }

        var sx = drawW / frameW;
        var sy = drawH / frameH;
        var cx = car.ChassisX * sx;
        var cy = car.ChassisYFromTop * sy;
        var hw = car.HalfWidth * sx;
        var hh = car.HalfHeight * sy;
        var wr = car.WheelRadius * Math.Min(sx, sy);
        var ox = car.WheelOffsetX * sx;
        var oy = car.WheelOffsetY * sy;

        // TaskmgrPlayer: ColorDark fill + ColorEdge stroke (not solid edge).
        const byte er = 12, eg = 125, eb = 187;
        const byte fr = 241, fg = 246, fb = 250;
        var fill = car.IsDead
            ? new SolidColorBrush(Color.FromArgb(240, 255, 224, 224))
            : car.ControlsDisabled
                ? new SolidColorBrush(Color.FromArgb(240, 255, 235, 200))
                : new SolidColorBrush(Color.FromArgb(240, fr, fg, fb));
        var strokeRgb = car.IsDead
            ? Color.FromArgb(255, 190, 50, 50)
            : Color.FromArgb(255, er, eg, eb);
        var strokeBrush = new SolidColorBrush(strokeRgb);
        var stroke = new Pen(strokeBrush, 2.0);
        fill.Freeze();
        strokeBrush.Freeze();
        stroke.Freeze();

        var deg = -car.AngleRad * 180.0 / Math.PI;
        dc.PushTransform(new RotateTransform(deg, cx, cy));
        dc.DrawRectangle(fill, stroke, new Rect(cx - hw, cy - hh, hw * 2, hh * 2));
        dc.DrawRectangle(fill, stroke, new Rect(cx - hw * 0.15, cy - hh * 1.85, hw * 0.85, hh * 0.85));
        dc.Pop();

        var screenAngle = -car.AngleRad;
        DrawWheel(dc, cx, cy, -ox, oy, screenAngle, wr, fill, stroke);
        DrawWheel(dc, cx, cy, ox, oy, screenAngle, wr, fill, stroke);
    }

    private static void DrawWheel(
        DrawingContext dc,
        double cx,
        double cy,
        double localX,
        double localY,
        float angleRad,
        double radius,
        Brush fill,
        Pen stroke)
    {
        var cos = Math.Cos(angleRad);
        var sin = Math.Sin(angleRad);
        var x = cx + localX * cos - localY * sin;
        var y = cy + localX * sin + localY * cos;
        dc.DrawEllipse(fill, stroke, new Point(x, y), radius, radius);
    }

    private void DrawCenteredPrompt(
        DrawingContext dc,
        string prompt,
        double drawW,
        double drawH,
        byte ar,
        byte ag,
        byte ab)
    {
        var normalized = prompt.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lineCount = 1;
        foreach (var ch in normalized)
        {
            if (ch == '\n')
            {
                lineCount++;
            }
        }

        var fontSize = Math.Clamp(drawH / (lineCount * 1.25), 8, 20);
        var brush = new SolidColorBrush(Color.FromArgb(245, ar, ag, ab));
        brush.Freeze();
        var text = new FormattedText(
            normalized,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Consolas"),
            fontSize,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var x = (drawW - text.Width) * 0.5;
        var y = (drawH - text.Height) * 0.5;
        dc.DrawText(text, new Point(Math.Max(0, x), Math.Max(0, y)));
    }

    private void DrawThrottleBar(DrawingContext dc, CarState car, double drawW, double drawH, byte ar, byte ag, byte ab)
    {
        var barW = 8.0;
        var barH = Math.Clamp(drawH * 0.28, 72, 140);
        var x0 = 12.0;
        var y0 = Math.Max(36, drawH - 28 - barH);
        // ColorDark wash RGB(241,246,250); fill/rim = ColorEdge.
        var track = new SolidColorBrush(Color.FromArgb(220, 241, 246, 250));
        var accent = new SolidColorBrush(Color.FromArgb(240, ar, ag, ab));
        var hud = new SolidColorBrush(Color.FromArgb(255, ar, ag, ab));
        track.Freeze();
        accent.Freeze();
        hud.Freeze();
        var rim = new Pen(hud, 1);
        dc.DrawRectangle(track, rim, new Rect(x0, y0, barW, barH));
        var midY = y0 + (barH * 0.5);
        dc.DrawLine(rim, new Point(x0 - 2, midY), new Point(x0 + barW + 2, midY));

        var pedal = Math.Clamp(car.Pedal, -1f, 1f);
        if (Math.Abs(pedal) >= 0.02f)
        {
            double fillY;
            double fillH;
            if (pedal >= 0f)
            {
                fillH = (barH * 0.5) * pedal;
                fillY = midY - fillH;
            }
            else
            {
                fillH = (barH * 0.5) * -pedal;
                fillY = midY;
            }

            dc.DrawRectangle(accent, null, new Rect(x0 + 1, fillY, barW - 2, Math.Max(1, fillH)));
        }

        var label = Math.Abs(pedal) < 0.02f ? "油门" : $"{(int)MathF.Round(pedal * 100f)}%";
        var text = new FormattedText(
            label,
            System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface("Consolas"),
            11,
            hud,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(text, new Point(x0 + barW + 6, midY - 8));
    }

    private void RefreshStatusText()
    {
        if (_roi is null)
        {
            _statusText = "CPURacer — no CPU chart";
            return;
        }

        var r = _roi.Value;
        var tag = FollowMode == TrackFollowMode.Child ? "child" : "external";
        var cap = string.IsNullOrEmpty(_captureStatus) ? "" : $" {_captureStatus}";
        _statusText =
            $"CPURacer [{tag}] — {r.Width}x{r.Height} dpi={r.Dpi} show={r.ShouldShow}{cap}";
    }

    private void ApplyPlacement()
    {
        if (_roi is null)
        {
            if (!_forceVisible)
            {
                HideOverlaySurface();
            }

            return;
        }

        var show = _forceVisible || _roi.Value.ShouldShow;
        if (FollowMode == TrackFollowMode.Child)
        {
            PlaceAsChild(show);
        }
        else
        {
            PlaceExternal(show);
        }
    }

    private void PlaceExternal(bool show)
    {
        // Avoid touching Child attachment; Detach is a no-op when not attached.
        if (_attachedParent != IntPtr.Zero || _childStylesApplied)
        {
            DetachFromChart();
        }

        Topmost = true;

        var roi = _roi!.Value;
        var leftPx = roi.Left + (int)PlotInsetPx.Left;
        var topPx = roi.Top + (int)PlotInsetPx.Top;
        var widthPx = Math.Max(1, roi.Width - (int)PlotInsetPx.Left - (int)PlotInsetPx.Right);
        var heightPx = Math.Max(1, roi.Height - (int)PlotInsetPx.Top - (int)PlotInsetPx.Bottom);
        var (left, top, width, height) = CoordMapper.RectPixelsToDiu(leftPx, topPx, widthPx, heightPx, roi.Dpi);

        Left = left;
        Width = width;
        Height = height;
        Top = show ? top : OffscreenTop;
        EnsureWindowShown();
    }

    private void PlaceAsChild(bool show)
    {
        var roi = _roi!.Value;
        var chart = roi.ChartHwnd;
        if (chart == IntPtr.Zero || !NativeMethods.IsWindow(chart))
        {
            DetachFromChart();
            HideOverlaySurface();
            return;
        }

        var hwnd = EnsureHandle();
        Topmost = false;

        if (!show)
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SwHide);
            InvalidateVisual();
            return;
        }

        if (_attachedParent != chart)
        {
            DetachFromChart();
            AttachToChart(chart);
        }

        // TaskmgrPlayer: GetWindowRect(parent) → MoveWindow(child, 0,0,w,h).
        // Parent drag moves us; only resize when w/h change.
        if (!NativeMethods.GetWindowRect(chart, out var wr) || wr.Width <= 0 || wr.Height <= 0)
        {
            return;
        }

        var w = wr.Width;
        var h = wr.Height;
        if (w != _lastChildW || h != _lastChildH)
        {
            NativeMethods.MoveWindow(hwnd, 0, 0, w, h, true);
            SyncWpfRenderSize(hwnd, w, h);
            NativeMethods.MoveWindow(hwnd, 0, 0, w, h, true);
            _lastChildW = w;
            _lastChildH = h;
            InvalidateVisual();
        }

        NativeMethods.ShowWindow(hwnd, NativeMethods.SwShow);
        EnsureWindowShown();
    }

    private void SyncWpfRenderSize(IntPtr hwnd, int pixelW, int pixelH)
    {
        var source = HwndSource.FromHwnd(hwnd);
        if (source?.CompositionTarget is null)
        {
            return;
        }

        var size = source.CompositionTarget.TransformFromDevice.Transform(new Vector(pixelW, pixelH));
        if (Math.Abs(Width - size.X) > 0.5)
        {
            Width = Math.Max(1, size.X);
        }

        if (Math.Abs(Height - size.Y) > 0.5)
        {
            Height = Math.Max(1, size.Y);
        }
    }

    private void AttachToChart(IntPtr chart)
    {
        var hwnd = EnsureHandle();
        if (!_childStylesApplied)
        {
            _savedStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GwlStyle);
            _savedExStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GwlExStyle);
            _childStylesApplied = true;
        }

        var style = _savedStyle;
        style &= ~(NativeMethods.WsPopup | NativeMethods.WsCaption | NativeMethods.WsThickFrame
                   | NativeMethods.WsBorder | NativeMethods.WsSysMenu);
        style |= NativeMethods.WsChild;
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GwlStyle, style);

        var ex = _savedExStyle | NativeMethods.WsExLayered | NativeMethods.WsExNoActivate
                 | NativeMethods.WsExToolWindow;
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GwlExStyle, ex);

        NativeMethods.SetParent(hwnd, chart);
        _attachedParent = chart;
        _lastChildW = -1;
        _lastChildH = -1;

        NativeMethods.SetWindowPos(
            hwnd,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate | NativeMethods.SwpFrameChanged);
    }

    private void DetachFromChart()
    {
        if (_attachedParent == IntPtr.Zero && !_childStylesApplied)
        {
            return;
        }

        var hwnd = EnsureHandle();
        if (_attachedParent != IntPtr.Zero)
        {
            NativeMethods.SetParent(hwnd, IntPtr.Zero);
            _attachedParent = IntPtr.Zero;
        }

        if (_childStylesApplied)
        {
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GwlStyle, _savedStyle);
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GwlExStyle, _savedExStyle);
            NativeMethods.SetWindowPos(
                hwnd,
                IntPtr.Zero,
                0,
                0,
                0,
                0,
                NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate | NativeMethods.SwpFrameChanged);
            _childStylesApplied = false;
        }

        _lastChildW = -1;
        _lastChildH = -1;
        Topmost = FollowMode == TrackFollowMode.External;
    }

    private void HideOverlaySurface()
    {
        if (FollowMode == TrackFollowMode.Child && _attachedParent != IntPtr.Zero)
        {
            NativeMethods.ShowWindow(EnsureHandle(), NativeMethods.SwHide);
        }
        else
        {
            Top = OffscreenTop;
        }

        if (_roi is null && !_forceVisible)
        {
            Hide();
        }

        InvalidateVisual();
    }

    private void EnsureWindowShown()
    {
        if (!IsVisible)
        {
            // Showing a topmost WPF window must not steal Taskmgr focus.
            ShowActivated = FollowMode != TrackFollowMode.External;
            Show();
        }
    }

    private IntPtr EnsureHandle()
    {
        var helper = new WindowInteropHelper(this);
        return helper.Handle == IntPtr.Zero ? helper.EnsureHandle() : helper.Handle;
    }
}
