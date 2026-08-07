using System.Runtime.InteropServices;

namespace CPURacer.Native;

/// <summary>
/// Win32 interop surface. M0 only declares DPI helper; expand in M1+.
/// </summary>
public static class NativeMethods
{
    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hwnd);
}
