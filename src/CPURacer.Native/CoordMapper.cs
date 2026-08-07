namespace CPURacer.Native;

/// <summary>
/// Converts between physical pixels and WPF device-independent units.
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
}
