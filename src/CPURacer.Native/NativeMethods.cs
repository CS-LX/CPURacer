using System.Runtime.InteropServices;
using System.Text;

namespace CPURacer.Native;

public static class NativeMethods
{
    public const int GwlStyle = -16;
    public const int GwlExStyle = -20;
    public const int WsChild = 0x40000000;
    public const int WsPopup = unchecked((int)0x80000000);
    public const int WsCaption = 0x00C00000;
    public const int WsThickFrame = 0x00040000;
    public const int WsBorder = 0x00800000;
    public const int WsSysMenu = 0x00080000;
    public const int WsExLayered = 0x00080000;
    public const int WsExTransparent = 0x00000020;
    public const int WsExNoActivate = 0x08000000;
    public const int WsExToolWindow = 0x00000080;
    public const uint WdaExcludeFromCapture = 0x00000011;

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll")]
    public static extern bool MoveWindow(IntPtr hWnd, int x, int y, int nWidth, int nHeight, bool bRepaint);

    [DllImport("user32.dll")]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    public const int SwHide = 0;
    public const int SwShow = 5;
    public const uint SwpNoZOrder = 0x0004;
    public const uint SwpNoActivate = 0x0010;
    public const uint SwpShowWindow = 0x0040;
    public const uint SwpFrameChanged = 0x0020;

    public static string GetClassName(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        _ = GetClassName(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public readonly int Width => Right - Left;
    public readonly int Height => Bottom - Top;
}
