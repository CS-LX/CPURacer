namespace CPURacer.Native;

/// <summary>
/// Converts between physical pixels, WPF DIU, and race world coordinates.
/// World origin is the plot's bottom-left; +Y is up (1 unit = 1 plot pixel).
/// </summary>
public static class CoordMapper
{
    public static double PixelsToDiu(double pixels, uint dpi)
    {
        if (dpi == 0)
        {
            dpi = 96;
        }

        return pixels * 96.0 / dpi;
    }

    public static (double Left, double Top, double Width, double Height) RectPixelsToDiu(
        int leftPx,
        int topPx,
        int widthPx,
        int heightPx,
        uint dpi)
    {
        return (
            PixelsToDiu(leftPx, dpi),
            PixelsToDiu(topPx, dpi),
            PixelsToDiu(widthPx, dpi),
            PixelsToDiu(heightPx, dpi));
    }

    /// <summary>Frame Y-from-top → world Y (plot bottom = 0).</summary>
    public static float FrameYFromTopToWorldY(float yFromTop, int insetTop, int plotHeight)
        => plotHeight - (yFromTop - insetTop);

    /// <summary>World Y → frame Y-from-top.</summary>
    public static float WorldYToFrameYFromTop(float worldY, int insetTop, int plotHeight)
        => insetTop + (plotHeight - worldY);
}
