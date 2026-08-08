using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using CPURacer.Taskmgr;

namespace CPURacer.Capture;

/// <summary>
/// PerMonitorV2 screen ROI via desktop BitBlt. The overlay HWND uses
/// WDA_EXCLUDEFROMCAPTURE, so capture never needs to hide/show it.
/// </summary>
public sealed class ScreenRoiCapture : IFrameCapture
{
    private const int SrcCopy = 0x00CC0020;

    public CapturedFrame? TryCapture(in ChartRoi roi)
    {
        if (!roi.ShouldShow || roi.Width < 8 || roi.Height < 8)
        {
            return null;
        }

        IntPtr hdcScreen = IntPtr.Zero;
        IntPtr hdcMem = IntPtr.Zero;
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr hOld = IntPtr.Zero;

        try
        {
            hdcScreen = GetDC(IntPtr.Zero);
            if (hdcScreen == IntPtr.Zero)
            {
                return null;
            }

            hdcMem = CreateCompatibleDC(hdcScreen);
            hBitmap = CreateCompatibleBitmap(hdcScreen, roi.Width, roi.Height);
            if (hdcMem == IntPtr.Zero || hBitmap == IntPtr.Zero)
            {
                return null;
            }

            hOld = SelectObject(hdcMem, hBitmap);
            // Do not request layered-window capture; overlay exclusion is also enforced
            // explicitly by WDA_EXCLUDEFROMCAPTURE on the overlay HWND.
            if (!BitBlt(hdcMem, 0, 0, roi.Width, roi.Height, hdcScreen, roi.Left, roi.Top, SrcCopy))
            {
                return null;
            }

            using var bmp = Image.FromHbitmap(hBitmap);
            var data = bmp.LockBits(
                new Rectangle(0, 0, bmp.Width, bmp.Height),
                ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb);
            try
            {
                var packed = new byte[roi.Width * roi.Height * 4];
                if (data.Stride == roi.Width * 4)
                {
                    Marshal.Copy(data.Scan0, packed, 0, packed.Length);
                }
                else
                {
                    for (var y = 0; y < roi.Height; y++)
                    {
                        Marshal.Copy(
                            data.Scan0 + y * data.Stride,
                            packed,
                            y * roi.Width * 4,
                            roi.Width * 4);
                    }
                }

                return new CapturedFrame(roi.Width, roi.Height, packed);
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hOld != IntPtr.Zero && hdcMem != IntPtr.Zero)
            {
                SelectObject(hdcMem, hOld);
            }

            if (hBitmap != IntPtr.Zero)
            {
                DeleteObject(hBitmap);
            }

            if (hdcMem != IntPtr.Zero)
            {
                DeleteDC(hdcMem);
            }

            if (hdcScreen != IntPtr.Zero)
            {
                ReleaseDC(IntPtr.Zero, hdcScreen);
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr h);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(
        IntPtr hdcDest,
        int xDest,
        int yDest,
        int w,
        int h,
        IntPtr hdcSrc,
        int xSrc,
        int ySrc,
        int rop);
}
