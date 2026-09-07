using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using CPURacer.Taskmgr;

namespace CPURacer.Capture;

[StructLayout(LayoutKind.Sequential)]
internal struct NativeCaptureConfig
{
    public long MainHwnd;
    public int ScreenLeft;
    public int ScreenTop;
    public int Width;
    public int Height;
    public int InsetLeft;
    public int InsetTop;
    public int InsetRight;
    public int InsetBottom;
    public int SmoothRadius;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeCaptureFrameInfo
{
    public ulong Sequence;
    public int FrameWidth;
    public int FrameHeight;
    public int InsetLeft;
    public int InsetTop;
    public int InsetRight;
    public int InsetBottom;
    public int PlotWidth;
    public byte AccentB;
    public byte AccentG;
    public byte AccentR;
    public byte Reserved;
    public int HasUpdateTiming;
    public long LastUpdateTicks;
    public long UpdatePeriodTicks;
    public long NextUpdateTicks;
}

internal static unsafe class NativeCaptureApi
{
    private const string DllName = TrackNativeApi.DllName;

    [DllImport(DllName, EntryPoint = "Capture_Start", CallingConvention = CallingConvention.StdCall)]
    private static extern int StartNative(in NativeCaptureConfig config);

    [DllImport(
        DllName,
        EntryPoint = "Capture_TryGetHeightField",
        CallingConvention = CallingConvention.StdCall)]
    private static extern int TryGetHeightFieldNative(
        ulong afterSequence,
        float* yFromTop,
        int capacity,
        out NativeCaptureFrameInfo info);

    [DllImport(DllName, EntryPoint = "Capture_Stop", CallingConvention = CallingConvention.StdCall)]
    private static extern void StopNative();

    [DllImport(
        DllName,
        EntryPoint = "CaptureExtractor_Create",
        CallingConvention = CallingConvention.StdCall)]
    private static extern NativeExtractorHandle CreateExtractorNative(
        int insetLeft,
        int insetTop,
        int insetRight,
        int insetBottom,
        int smoothRadius);

    [DllImport(
        DllName,
        EntryPoint = "CaptureExtractor_Destroy",
        CallingConvention = CallingConvention.StdCall)]
    private static extern void DestroyExtractorNative(IntPtr extractor);

    [DllImport(
        DllName,
        EntryPoint = "CaptureExtractor_ExtractBgra",
        CallingConvention = CallingConvention.StdCall)]
    private static extern int ExtractBgraNative(
        NativeExtractorHandle extractor,
        byte* bgra,
        int width,
        int height,
        int stride,
        float* yFromTop,
        int capacity,
        out NativeCaptureFrameInfo info);

    public static bool IsAvailable
    {
        get
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

    public static bool TryStart(in NativeCaptureConfig config, out int error)
    {
        try
        {
            error = StartNative(config);
            return error >= 0;
        }
        catch (Exception ex) when (IsNativeLoadFailure(ex))
        {
            error = ex.HResult;
            return false;
        }
    }

    public static int TryGetHeightField(
        ulong afterSequence,
        float[] destination,
        out NativeCaptureFrameInfo info)
    {
        try
        {
            fixed (float* output = destination)
            {
                return TryGetHeightFieldNative(
                    afterSequence,
                    output,
                    destination.Length,
                    out info);
            }
        }
        catch (Exception ex) when (IsNativeLoadFailure(ex))
        {
            info = default;
            return ex.HResult;
        }
    }

    public static void Stop()
    {
        try
        {
            StopNative();
        }
        catch (Exception ex) when (IsNativeLoadFailure(ex))
        {
        }
    }

    public static NativeHeightExtractor? TryCreateExtractor(PlotInset inset, int smoothRadius)
    {
        if (!IsAvailable)
        {
            return null;
        }

        try
        {
            var handle = CreateExtractorNative(
                inset.Left,
                inset.Top,
                inset.Right,
                inset.Bottom,
                smoothRadius);
            return handle.IsInvalid ? null : new NativeHeightExtractor(handle, inset);
        }
        catch (Exception ex) when (IsNativeLoadFailure(ex))
        {
            return null;
        }
    }

    internal sealed class NativeHeightExtractor(
        NativeExtractorHandle handle,
        PlotInset inset)
    {
        private readonly NativeExtractorHandle _handle = handle;

        public bool TryExtract(CapturedFrame frame, out HeightField? field)
        {
            var plotWidth = inset.ContentWidth(frame.Width);
            var yFromTop = new float[plotWidth];
            try
            {
                NativeCaptureFrameInfo info;
                int result;
                fixed (byte* bgra = frame.Bgra)
                fixed (float* output = yFromTop)
                {
                    result = ExtractBgraNative(
                        _handle,
                        bgra,
                        frame.Width,
                        frame.Height,
                        frame.Stride,
                        output,
                        yFromTop.Length,
                        out info);
                }

                if (result != 1
                    || info.PlotWidth != plotWidth
                    || info.FrameWidth != frame.Width
                    || info.FrameHeight != frame.Height)
                {
                    field = null;
                    return false;
                }

                field = new HeightField(
                    info.FrameWidth,
                    info.FrameHeight,
                    new PlotInset(
                        info.InsetLeft,
                        info.InsetTop,
                        info.InsetRight,
                        info.InsetBottom),
                    yFromTop,
                    info.AccentB,
                    info.AccentG,
                    info.AccentR);
                return true;
            }
            catch (Exception ex) when (IsNativeLoadFailure(ex))
            {
                field = null;
                return false;
            }
        }
    }

    internal sealed class NativeExtractorHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private NativeExtractorHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle()
        {
            DestroyExtractorNative(handle);
            return true;
        }
    }

    private static bool IsNativeLoadFailure(Exception ex)
        => ex is DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException
            or MarshalDirectiveException;
}
