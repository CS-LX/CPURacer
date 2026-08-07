using System.Windows;
using System.Windows.Media;

namespace CPURacer.Overlay;

/// <summary>
/// Transparent topmost overlay. M0: empty placeholder window for tray demo.
/// </summary>
public sealed class OverlayWindow : Window
{
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
        Top = 120;
        ShowDebugChrome = true;
    }

    public bool ShowDebugChrome { get; set; }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (!ShowDebugChrome)
        {
            return;
        }

        var pen = new Pen(new SolidColorBrush(Color.FromArgb(180, 220, 60, 60)), 2);
        drawingContext.DrawRectangle(null, pen, new Rect(1, 1, ActualWidth - 2, ActualHeight - 2));

        var text = new FormattedText(
            "CPURacer M0 — empty overlay",
            System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            14,
            new SolidColorBrush(Color.FromArgb(200, 220, 60, 60)),
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        drawingContext.DrawText(text, new Point(12, 12));
    }

    public void ShowPlaceholder()
    {
        Show();
        Activate();
        InvalidateVisual();
    }

    public void HidePlaceholder()
    {
        Hide();
    }
}
