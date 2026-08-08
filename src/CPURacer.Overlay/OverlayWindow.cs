using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using CPURacer.Capture;
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
    private bool _showDebugChrome = true;
    private bool _showFitPolyline = true;
    private string _statusText = "CPURacer";
    private TrackFollowMode _followMode = TrackFollowMode.External;
    private IntPtr _attachedParent = IntPtr.Zero;
    private bool _childStylesApplied;
    private int _savedStyle;
    private int _savedExStyle;
    private int _lastChildW = -1;
    private int _lastChildH = -1;
    private HeightField? _heightField;
    private string _captureStatus = "";
    private readonly DrawingGroup _backingStore = new();

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
    }

    public void SetCaptureStatus(string captureStatus)
    {
        _captureStatus = captureStatus;
        RefreshStatusText();
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

                if (ShowDebugChrome)
                {
                    var pen = new Pen(new SolidColorBrush(Color.FromArgb(200, 220, 60, 60)), 2);
                    dc.DrawRectangle(
                        null,
                        pen,
                        new Rect(1, 1, drawW - 2, drawH - 2));

                    var text = new FormattedText(
                        _statusText,
                        System.Globalization.CultureInfo.CurrentUICulture,
                        FlowDirection.LeftToRight,
                        new Typeface("Segoe UI"),
                        13,
                        new SolidColorBrush(Color.FromArgb(230, 220, 60, 60)),
                        VisualTreeHelper.GetDpi(this).PixelsPerDip);
                    dc.DrawText(text, new Point(10, 8));
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
