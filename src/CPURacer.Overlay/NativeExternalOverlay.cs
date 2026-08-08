using System.Numerics;
using System.Runtime.InteropServices;
using CPURacer.Capture;
using CPURacer.Native;
using CPURacer.Taskmgr;
using SharpGen.Runtime;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using FactoryType = Vortice.Direct2D1.FactoryType;
using WriteFactoryType = Vortice.DirectWrite.FactoryType;

namespace CPURacer.Overlay;

/// <summary>
/// Render-only External overlay. Owns a native Win32 HWND and Direct2D target;
/// it never activates and all mouse input passes through to Taskmgr.
/// </summary>
public sealed class NativeExternalOverlay : IDisposable
{
    private const uint WsPopup = 0x80000000;
    private const uint WsExTransparent = 0x00000020;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExLayered = 0x00080000;
    private const uint WsExNoActivate = 0x08000000;

    private const int SwHide = 0;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const uint LwaAlpha = 0x00000002;
    private const uint WmDestroy = 0x0002;

    private static readonly IntPtr HwndTop = IntPtr.Zero;

    private readonly WndProc _wndProc;
    private readonly string _className = $"CPURacer.NativeOverlay.{Guid.NewGuid():N}";

    private IntPtr _hwnd;
    private ChartRoi? _roi;
    private HeightField? _heightField;
    private int _captureFailStreak;
    private bool _visible;
    private bool _disposed;
    private int _pixelWidth = 1;
    private int _pixelHeight = 1;

    private ID2D1Factory? _d2dFactory;
    private ID2D1HwndRenderTarget? _renderTarget;
    private ID2D1SolidColorBrush? _orangeBrush;
    private ID2D1SolidColorBrush? _redBrush;
    private IDWriteFactory? _writeFactory;
    private IDWriteTextFormat? _textFormat;

    public NativeExternalOverlay()
    {
        _wndProc = WindowProcedure;
        CreateWindow();
    }

    public bool ShowDebugChrome { get; set; } = true;

    public bool ShowFitPolyline { get; set; } = true;

    public bool ForceVisible { get; set; }

    public bool IsVisible => _visible;

    public bool DisplayAffinityApplied { get; private set; }

    public int DisplayAffinityError { get; private set; }

    public string CaptureStatus { get; private set; } = "";

    public string WindowStatus { get; private set; } = "window=hidden";

    public string DiagnosticStatus =>
        $"{CaptureStatus} {WindowStatus} {(DisplayAffinityApplied ? "wda=ok" : $"wda=fail({DisplayAffinityError})")}";

    public void ApplyRoi(ChartRoi? roi)
    {
        ThrowIfDisposed();
        _roi = roi;
        if (roi is null)
        {
            _heightField = null;
            _captureFailStreak = 0;
            CaptureStatus = "cap=sleep";
            Hide();
        }
    }

    public void ClearRoi() => ApplyRoi(null);

    public void TickExternalFrame(ScreenRoiCapture capture, HeightFieldExtractor extractor)
    {
        ThrowIfDisposed();

        if (_roi is null
            || _roi.Value.ChartHwnd == IntPtr.Zero
            || !NativeMethods.IsWindow(_roi.Value.ChartHwnd)
            || !NativeMethods.IsWindowVisible(_roi.Value.ChartHwnd)
            || (_roi.Value.MainHwnd != IntPtr.Zero && IsIconic(_roi.Value.MainHwnd)))
        {
            Hide();
            return;
        }

        var previous = _roi.Value;
        if (!NativeMethods.GetWindowRect(previous.ChartHwnd, out var rect)
            || rect.Width < 8
            || rect.Height < 8)
        {
            CaptureStatus = "cap=bad-rect";
            Hide();
            return;
        }

        var dpi = NativeMethods.GetDpiForWindow(previous.ChartHwnd);
        if (dpi == 0)
        {
            dpi = previous.Dpi == 0 ? 96u : previous.Dpi;
        }

        var targetForeground = IsForegroundRelated(previous.MainHwnd, previous.ChartHwnd);
        _roi = new ChartRoi(
            previous.ChartHwnd,
            previous.MainHwnd,
            rect.Left,
            rect.Top,
            rect.Width,
            rect.Height,
            dpi,
            previous.VisibleChartCount,
            targetForeground,
            previous.IsCpuPage);

        if (!PlaceAndShow(rect, targetForeground))
        {
            return;
        }

        // Desktop ROI capture is valid only while Taskmgr is the visible foreground
        // surface. When occluded, retain the last reliable terrain for M3.
        if (targetForeground || ForceVisible)
        {
            var captureRoi = _roi.Value with { ShouldShow = true };
            var frame = capture.TryCapture(captureRoi);
            if (frame is null)
            {
                _captureFailStreak++;
                CaptureStatus = $"cap=fail({_captureFailStreak})";
                if (_captureFailStreak >= 3)
                {
                    _heightField = null;
                }
            }
            else
            {
                _captureFailStreak = 0;
                var field = extractor.Extract(frame);
                if (field is null)
                {
                    CaptureStatus = "cap=ok extract=skip";
                }
                else
                {
                    _heightField = field;
                    CaptureStatus = $"cap=ok cols={field.PlotWidth}";
                }
            }
        }
        else
        {
            CaptureStatus = "cap=occluded";
        }

        RenderFrame();
    }

    private void CreateWindow()
    {
        var instance = GetModuleHandle(null);
        var wndClass = new WndClassEx
        {
            Size = (uint)Marshal.SizeOf<WndClassEx>(),
            Instance = instance,
            WindowProc = _wndProc,
            ClassName = _className,
        };

        if (RegisterClassEx(ref wndClass) == 0)
        {
            throw new InvalidOperationException($"RegisterClassEx failed: {Marshal.GetLastWin32Error()}");
        }

        const uint exStyle = WsExLayered | WsExTransparent | WsExNoActivate | WsExToolWindow;
        _hwnd = CreateWindowEx(
            exStyle,
            _className,
            "CPURacer Native External Overlay",
            WsPopup,
            0,
            0,
            1,
            1,
            IntPtr.Zero,
            IntPtr.Zero,
            instance,
            IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            _ = UnregisterClass(_className, instance);
            throw new InvalidOperationException($"CreateWindowEx failed: {error}");
        }

        var margins = new Margins(-1, -1, -1, -1);
        _ = DwmExtendFrameIntoClientArea(_hwnd, ref margins);
        _ = SetLayeredWindowAttributes(_hwnd, 0, 255, LwaAlpha);

        _d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory>(FactoryType.SingleThreaded, DebugLevel.None);
        _writeFactory = DWrite.DWriteCreateFactory<IDWriteFactory>(WriteFactoryType.Shared);
        _textFormat = _writeFactory.CreateTextFormat(
            "Segoe UI",
            null,
            FontWeight.Normal,
            FontStyle.Normal,
            FontStretch.Normal,
            13f,
            "zh-CN");
    }

    private bool PlaceAndShow(RECT rect, bool targetForeground)
    {
        // Geometry/show is independent of cross-process relative Z-order.
        if (!SetWindowPos(
                _hwnd,
                IntPtr.Zero,
                rect.Left,
                rect.Top,
                rect.Width,
                rect.Height,
                SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder | SwpShowWindow))
        {
            WindowStatus = $"window=pos-fail({Marshal.GetLastWin32Error()})";
            _visible = false;
            return false;
        }

        _visible = true;
        WindowStatus = "window=visible";

        if (rect.Width != _pixelWidth || rect.Height != _pixelHeight)
        {
            _pixelWidth = rect.Width;
            _pixelHeight = rect.Height;
            _renderTarget?.Resize(new SizeI(_pixelWidth, _pixelHeight));
        }

        // Raising our own normal-tier HWND to HWND_TOP while Taskmgr is foreground
        // avoids a cross-process hWndInsertAfter dependency. When another app becomes
        // foreground, Windows naturally raises it above both Taskmgr and this overlay.
        if (targetForeground
            && !SetWindowPos(
                _hwnd,
                HwndTop,
                0,
                0,
                0,
                0,
                SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder))
        {
            WindowStatus = $"window=z-fail({Marshal.GetLastWin32Error()})";
        }

        if (!DisplayAffinityApplied && DisplayAffinityError == 0)
        {
            ApplyDisplayAffinity();
        }

        return true;
    }

    private void ApplyDisplayAffinity()
    {
        DisplayAffinityApplied = NativeMethods.SetWindowDisplayAffinity(
            _hwnd,
            NativeMethods.WdaExcludeFromCapture);
        DisplayAffinityError = DisplayAffinityApplied ? 0 : Marshal.GetLastWin32Error();
    }

    private void EnsureRenderTarget()
    {
        if (_renderTarget is not null)
        {
            return;
        }

        var properties = new RenderTargetProperties(
            RenderTargetType.Default,
            Vortice.DCommon.PixelFormat.Premultiplied,
            96f,
            96f,
            RenderTargetUsage.None,
            FeatureLevel.Default);
        var hwndProperties = new HwndRenderTargetProperties
        {
            Hwnd = _hwnd,
            PixelSize = new SizeI(_pixelWidth, _pixelHeight),
            PresentOptions = PresentOptions.Immediately,
        };

        _renderTarget = _d2dFactory!.CreateHwndRenderTarget(properties, hwndProperties);
        _orangeBrush = _renderTarget.CreateSolidColorBrush(new Color4(1f, 0.55f, 0f, 0.9f));
        _redBrush = _renderTarget.CreateSolidColorBrush(new Color4(0.86f, 0.24f, 0.24f, 0.9f));
    }

    private void RenderFrame()
    {
        EnsureRenderTarget();
        var target = _renderTarget!;
        target.BeginDraw();
        target.Clear(new Color4(0f, 0f, 0f, 0f));

        if (ShowFitPolyline && _heightField is { PlotWidth: > 1 } field)
        {
            var sx = _pixelWidth / (float)field.FrameWidth;
            var sy = _pixelHeight / (float)field.FrameHeight;
            var inset = field.Inset;
            var previous = new Vector2(
                (inset.Left + 0.5f) * sx,
                field.YFromTop[0] * sy);

            for (var i = 1; i < field.PlotWidth; i++)
            {
                var current = new Vector2(
                    (inset.Left + i + 0.5f) * sx,
                    field.YFromTop[i] * sy);
                target.DrawLine(previous, current, _orangeBrush!, 2f);
                previous = current;
            }
        }

        if (ShowDebugChrome)
        {
            target.DrawRectangle(
                new Rect(1, 1, Math.Max(1, _pixelWidth - 2), Math.Max(1, _pixelHeight - 2)),
                _redBrush!,
                2f);

            var affinity = DisplayAffinityApplied
                ? "wda=ok"
                : $"wda=fail({DisplayAffinityError})";
            target.DrawText(
                $"CPURacer [native] {_pixelWidth}x{_pixelHeight} {CaptureStatus} {WindowStatus} {affinity}",
                _textFormat!,
                new Rect(10, 8, Math.Max(20, _pixelWidth - 10), 30),
                _redBrush!);
        }

        try
        {
            target.EndDraw(out _, out _);
        }
        catch (SharpGenException ex) when (ex.ResultCode == ResultCode.RecreateTarget)
        {
            DisposeRenderTarget();
        }
    }

    private void Hide()
    {
        if (!_visible || _hwnd == IntPtr.Zero)
        {
            return;
        }

        _ = ShowWindow(_hwnd, SwHide);
        _visible = false;
        WindowStatus = "window=hidden";
    }

    private static bool IsForegroundRelated(IntPtr mainHwnd, IntPtr chartHwnd)
    {
        for (var current = NativeMethods.GetForegroundWindow();
             current != IntPtr.Zero;
             current = NativeMethods.GetParent(current))
        {
            if (current == mainHwnd || current == chartHwnd)
            {
                return true;
            }
        }

        return false;
    }

    private IntPtr WindowProcedure(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmDestroy)
        {
            _visible = false;
            return IntPtr.Zero;
        }

        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    private void DisposeRenderTarget()
    {
        _orangeBrush?.Dispose();
        _redBrush?.Dispose();
        _renderTarget?.Dispose();
        _orangeBrush = null;
        _redBrush = null;
        _renderTarget = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeRenderTarget();
        _textFormat?.Dispose();
        _writeFactory?.Dispose();
        _d2dFactory?.Dispose();
        _textFormat = null;
        _writeFactory = null;
        _d2dFactory = null;

        if (_hwnd != IntPtr.Zero)
        {
            _ = DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        _ = UnregisterClass(_className, GetModuleHandle(null));
        GC.KeepAlive(_wndProc);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint Size;
        public uint Style;
        public WndProc WindowProc;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public Margins(int left, int right, int top, int bottom)
        {
            Left = left;
            Right = right;
            Top = top;
            Bottom = bottom;
        }

        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    private delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WndClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClass(string className, IntPtr instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(
        IntPtr hwnd,
        uint message,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hwnd,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(
        IntPtr hwnd,
        uint colorKey,
        byte alpha,
        uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
}

