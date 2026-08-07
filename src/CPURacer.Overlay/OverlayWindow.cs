using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using CPURacer.Native;
using CPURacer.Taskmgr;

namespace CPURacer.Overlay;

/// <summary>
/// Transparent topmost overlay aligned to Taskmgr CPU chart ROI.
/// </summary>
public sealed class OverlayWindow : Window
{
    private const double OffscreenTop = 20000;

    private ChartRoi? _roi;
    private bool _forceVisible;
    private string _statusText = "CPURacer M1";

    public OverlayWindow()
    {
        Title = "CPURacer Overlay";
        AllowsTransparency = true;
        WindowStyle = WindowStyle.None;
        // Alpha must be >= 1 or clicks pass through.
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Width = 400;
        Height = 200;
        Left = 120;
        Top = OffscreenTop;
        ShowDebugChrome = true;
    }

    public bool ShowDebugChrome { get; set; }

    /// <summary>Manual plot inset in physical pixels (M1; refine in M2).</summary>
    public Thickness PlotInsetPx { get; set; } = new(0, 0, 0, 0);

    /// <summary>When true, keep overlay on-screen even if Taskmgr is not foreground (manual preview).</summary>
    public bool ForceVisible
    {
        get => _forceVisible;
        set
        {
            _forceVisible = value;
            ApplyPlacement();
        }
    }

    public void ApplyRoi(ChartRoi? roi)
    {
        _roi = roi;
        if (roi is null)
        {
            _statusText = "CPURacer M1 — no chart";
            Sleep();
            return;
        }

        _statusText = $"CPURacer M1 — {roi.Value.Width}x{roi.Value.Height} dpi={roi.Value.Dpi} charts={roi.Value.VisibleChartCount}";
        if (!IsVisible)
        {
            Show();
        }

        ApplyPlacement();
        InvalidateVisual();
    }

    public void TickForeground()
    {
        if (_roi is not null)
        {
            ApplyPlacement();
        }
    }

    public void ShowPlaceholder()
    {
        ForceVisible = true;
        if (_roi is null)
        {
            Width = 400;
            Height = 200;
            Left = 120;
            Top = 120;
            _statusText = "CPURacer M1 — manual overlay";
            Show();
            Activate();
            InvalidateVisual();
        }
        else
        {
            ApplyPlacement();
        }
    }

    public void HidePlaceholder()
    {
        ForceVisible = false;
        ApplyPlacement();
    }

    public void Sleep()
    {
        Top = OffscreenTop;
        // Keep window "shown" for HWND stability but off-screen when tracking.
        if (!IsVisible && _roi is not null)
        {
            Show();
        }

        if (_roi is null && !_forceVisible)
        {
            Hide();
        }

        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (!ShowDebugChrome || ActualWidth <= 2 || ActualHeight <= 2)
        {
            return;
        }

        var pen = new Pen(new SolidColorBrush(Color.FromArgb(200, 220, 60, 60)), 2);
        drawingContext.DrawRectangle(
            new SolidColorBrush(Color.FromArgb(40, 220, 60, 60)),
            pen,
            new Rect(1, 1, ActualWidth - 2, ActualHeight - 2));

        var text = new FormattedText(
            _statusText,
            System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            13,
            new SolidColorBrush(Color.FromArgb(230, 220, 60, 60)),
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        drawingContext.DrawText(text, new Point(10, 8));
    }

    private void ApplyPlacement()
    {
        if (_roi is null)
        {
            if (_forceVisible)
            {
                return;
            }

            Sleep();
            return;
        }

        var roi = _roi.Value;
        var leftPx = roi.Left + (int)PlotInsetPx.Left;
        var topPx = roi.Top + (int)PlotInsetPx.Top;
        var widthPx = Math.Max(1, roi.Width - (int)PlotInsetPx.Left - (int)PlotInsetPx.Right);
        var heightPx = Math.Max(1, roi.Height - (int)PlotInsetPx.Top - (int)PlotInsetPx.Bottom);

        var (left, top, width, height) = CoordMapper.RectPixelsToDiu(leftPx, topPx, widthPx, heightPx, roi.Dpi);

        Left = left;
        Width = width;
        Height = height;

        var show = _forceVisible || ShouldShowForForeground(roi);
        Top = show ? top : OffscreenTop;

        if (!IsVisible)
        {
            Show();
        }

        InvalidateVisual();
    }

    private bool ShouldShowForForeground(ChartRoi roi)
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return false;
        }

        var helper = new WindowInteropHelper(this);
        if (helper.Handle != IntPtr.Zero && foreground == helper.Handle)
        {
            return true;
        }

        if (foreground == roi.MainHwnd || foreground == roi.ChartHwnd)
        {
            return true;
        }

        // Walk parents — Taskmgr chrome / XAML hosts may be foreground children.
        var current = foreground;
        for (var i = 0; i < 16 && current != IntPtr.Zero; i++)
        {
            if (current == roi.MainHwnd)
            {
                return true;
            }

            current = NativeMethods.GetParent(current);
        }

        // Same process check: if foreground belongs to Taskmgr main window's process tree via parent walk failed,
        // also accept when foreground is a child of main by Enum — GetParent walk should cover most cases.
        return false;
    }
}
