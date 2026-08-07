using System.Runtime.InteropServices;

namespace CPURacer.Taskmgr;

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
}

public static class TrackNativeApi
{
    public const string DllName = "CPURacer.TrackNative.dll";

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void RoiCallback(ref TrackRoiState state, IntPtr userData);

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
            var path = Path.Combine(AppContext.BaseDirectory, DllName);
            return File.Exists(path);
        }
        catch
        {
            return false;
        }
    }
}
