using System.Runtime.InteropServices;

namespace CPURacer.Taskmgr;

public enum TrackFollowMode
{
    /// <summary>External topmost overlay in screen coordinates (WinEvent follow).</summary>
    External = 0,

    /// <summary>TaskmgrPlayer-style: overlay is SetParent'd into the chart HWND.</summary>
    Child = 1,
}

[StructLayout(LayoutKind.Sequential)]
public struct TrackRoiState
{
    public long ChartHwnd;
    public long MainHwnd;
    public int Left;
    public int Top;
    public int Width;
    public int Height;
    public uint Dpi;
    public int ChartCount;
    public int ShouldShow;
    public int IsCpuPage;
    public int FollowMode;
}

public static class TrackNativeApi
{
    public const string DllName = "CPURacer.TrackNative.dll";

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void RoiCallback(ref TrackRoiState state, IntPtr userData);

    [DllImport(DllName, EntryPoint = "Track_SetFollowMode", CallingConvention = CallingConvention.StdCall)]
    public static extern void SetFollowMode(int mode);

    [DllImport(DllName, EntryPoint = "Track_Start", CallingConvention = CallingConvention.StdCall)]
    public static extern int Start(RoiCallback callback, IntPtr userData);

    [DllImport(DllName, EntryPoint = "Track_Stop", CallingConvention = CallingConvention.StdCall)]
    public static extern void Stop();

    [DllImport(DllName, EntryPoint = "Track_GetState", CallingConvention = CallingConvention.StdCall)]
    public static extern int GetState(out TrackRoiState state);

    public static bool IsAvailable()
    {
        try
        {
            return File.Exists(Path.Combine(AppContext.BaseDirectory, DllName));
        }
        catch
        {
            return false;
        }
    }
}
