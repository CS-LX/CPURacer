using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using CPURacer.Capture;
using CPURacer.Game;
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
    private const uint WsExTopmost = 0x00000008;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExLayered = 0x00080000;
    private const uint WsExNoActivate = 0x08000000;

    private const int SwHide = 0;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const uint LwaAlpha = 0x00000002;
    private const uint WmDestroy = 0x0002;

    private readonly WndProc _wndProc;
    private readonly string _className = $"CPURacer.NativeOverlay.{Guid.NewGuid():N}";

    private IntPtr _hwnd;
    private ChartRoi? _roi;
    private HeightField? _heightField;
    private CarState? _carPose;
    private int _captureFailStreak;
    private bool _visible;
    private bool _disposed;
    private int _pixelWidth = 1;
    private int _pixelHeight = 1;
    private string _captureName = "none";

    private ID2D1Factory? _d2dFactory;
    private ID2D1HwndRenderTarget? _renderTarget;
    private ID2D1SolidColorBrush? _orangeBrush;
    private ID2D1SolidColorBrush? _redBrush;
    private ID2D1SolidColorBrush? _hudBrush;
    private ID2D1SolidColorBrush? _carBrush;
    private ID2D1SolidColorBrush? _carFlippedBrush;
    private ID2D1SolidColorBrush? _carDeadBrush;
    private ID2D1SolidColorBrush? _cabinBrush;
    private ID2D1SolidColorBrush? _stripeBrush;
    private ID2D1SolidColorBrush? _wheelBrush;
    private ID2D1SolidColorBrush? _hubBrush;
    private ID2D1SolidColorBrush? _glowBrush;
    private ID2D1SolidColorBrush? _strokeBrush;
    private ID2D1SolidColorBrush? _trackBrush;
    private ID2D1SolidColorBrush? _pedalBrush;
    private IDWriteFactory? _writeFactory;
    private IDWriteTextFormat? _textFormat;

    public NativeExternalOverlay()
    {
        _wndProc = WindowProcedure;
        CreateWindow();
    }

    public bool ShowDebugChrome { get; set; }

    public bool ShowFitPolyline { get; set; }

    public bool ForceVisible { get; set; }

    public bool IsVisible => _visible;

    public string CaptureStatus { get; private set; } = "";

    public string WindowStatus { get; private set; } = "window=hidden";

    public string DiagnosticStatus =>
        $"{CaptureStatus} {WindowStatus} source={_captureName} recordable=yes";

    /// <summary>Latest reliable terrain for RaceHost (read-only; Overlay never owns RaceSim).</summary>
    public HeightField? CurrentHeightField => _heightField;

    /// <summary>Raised when a new height field is extracted (M3 thin egress).</summary>
    public event Action<HeightField>? HeightFieldUpdated;

    public void ApplyRoi(ChartRoi? roi)
    {
        ThrowIfDisposed();
        _roi = roi;
        if (roi is null)
        {
            _heightField = null;
            _carPose = null;
            _captureFailStreak = 0;
            CaptureStatus = "cap=sleep";
            Hide();
        }
    }

    /// <summary>Draw-only car pose. Does not affect placement, Z-order, or capture.</summary>
    public void SetCarPose(CarState? pose)
    {
        ThrowIfDisposed();
        _carPose = pose;
    }

    /// <summary>
    /// RaceHost 每帧在物理步进后调用：用“最近一次地形 + 同一帧物理结果”重绘。
    /// 画面刷新统一收敛到此入口，捕获时钟不再直接渲染，避免地形与车位配对错一帧。
    /// </summary>
    public void RenderNow()
    {
        ThrowIfDisposed();
        if (!_visible || _disposed)
        {
            return;
        }

        try
        {
            RenderFrame();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"External overlay render failed: {ex}");
        }
    }

    /// <summary>Top-left player line (racing HUD). Idle/game-over use <see cref="CenterPrompt"/>.</summary>
    public string? PlayerBanner { get; set; }

    /// <summary>Pre-expanded multiline prompt drawn centered (Figgle already expanded by App).</summary>
    public string? CenterPrompt { get; set; }

    public void ClearRoi() => ApplyRoi(null);

    public void TickExternalFrame(IFrameCapture capture, HeightFieldExtractor extractor)
    {
        ThrowIfDisposed();
        _captureName = capture.Name;

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
        var appForeground = IsForegroundProcess((uint)Environment.ProcessId);
        var shouldShow = targetForeground || appForeground || ForceVisible;
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

        if (!shouldShow)
        {
            CaptureStatus = "cap=occluded";
            Hide();
            return;
        }

        if (!PlaceAndShow(rect))
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
                    CaptureStatus = $"cap={capture.Name}-ok extract=skip";
                }
                else
                {
                    _heightField = field;
                    CaptureStatus = $"cap={capture.Name}-ok cols={field.PlotWidth}";
                    HeightFieldUpdated?.Invoke(field);
                }
            }
        }
        else
        {
            CaptureStatus = "cap=occluded";
        }

        // 渲染不在捕获时钟触发：由 RaceHost 在物理步进后统一 RenderNow，
        // 保证画面始终是“最新地形 + 同一帧物理结果”的配对，避免错一帧。
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

        const uint exStyle =
            WsExLayered | WsExTransparent | WsExTopmost | WsExNoActivate | WsExToolWindow;
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
            "Consolas",
            null,
            FontWeight.Normal,
            FontStyle.Normal,
            FontStretch.Normal,
            13f,
            "zh-CN");
    }

    private bool PlaceAndShow(RECT rect)
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

        return true;
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
        // TaskmgrPlayer: fill=ColorDark, stroke=ColorEdge. See ApplyTaskmgrPalette.
        _hudBrush = _renderTarget.CreateSolidColorBrush(TmEdge);
        _carBrush = _renderTarget.CreateSolidColorBrush(TmFill);
        _carFlippedBrush = _renderTarget.CreateSolidColorBrush(new Color4(1f, 0.92f, 0.78f, 0.94f));
        _carDeadBrush = _renderTarget.CreateSolidColorBrush(new Color4(1f, 0.88f, 0.88f, 0.94f));
        _cabinBrush = _renderTarget.CreateSolidColorBrush(TmFill);
        _stripeBrush = _renderTarget.CreateSolidColorBrush(TmEdge);
        _wheelBrush = _renderTarget.CreateSolidColorBrush(TmFill);
        _hubBrush = _renderTarget.CreateSolidColorBrush(TmFill);
        _glowBrush = _renderTarget.CreateSolidColorBrush(TmWash);
        _strokeBrush = _renderTarget.CreateSolidColorBrush(TmEdge);
        _trackBrush = _renderTarget.CreateSolidColorBrush(TmWash);
        _pedalBrush = _renderTarget.CreateSolidColorBrush(TmEdge);
        ApplyTaskmgrPalette();
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

        if (_carPose is { } car)
        {
            ApplyAccent(car.AccentB, car.AccentG, car.AccentR);
            DrawCar(target, car);
            if (!ShowDebugChrome && car.IsRunning && !car.IsDead)
            {
                DrawThrottleBar(target, car);
            }
        }
        else if (_heightField is { } hf)
        {
            ApplyAccent(hf.AccentB, hf.AccentG, hf.AccentR);
        }

        if (ShowDebugChrome)
        {
            target.DrawRectangle(
                new Rect(1, 1, Math.Max(1, _pixelWidth - 2), Math.Max(1, _pixelHeight - 2)),
                _redBrush!,
                2f);

            var hud = _carPose is { } pose ? pose.Hud : null;
            var status = string.IsNullOrEmpty(hud)
                ? $"CPURacer [native] {_pixelWidth}x{_pixelHeight} {CaptureStatus} {WindowStatus} source={_captureName} recordable=yes"
                : $"CPURacer [native] {CaptureStatus} | {hud}";
            target.DrawText(
                status,
                _textFormat!,
                new Rect(10, 8, Math.Max(20, _pixelWidth - 10), 48),
                _redBrush!);
        }
        else if (_carPose is { IsRunning: true, IsDead: false } running)
        {
            if (!string.IsNullOrEmpty(running.Hud))
            {
                target.DrawText(
                    running.Hud,
                    _textFormat!,
                    new Rect(10, 8, Math.Max(20, _pixelWidth - 10), 48),
                    _hudBrush!);
            }
        }
        else if (!string.IsNullOrEmpty(PlayerBanner))
        {
            target.DrawText(
                PlayerBanner,
                _textFormat!,
                new Rect(10, 8, Math.Max(20, _pixelWidth - 10), 48),
                _hudBrush!);
        }

        if (!ShowDebugChrome && !string.IsNullOrEmpty(CenterPrompt))
        {
            DrawCenteredPrompt(target, CenterPrompt!);
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

    // TaskmgrPlayer/config.cfg — BGR→RGB.
    // ColorEdge/Frame = 187,125,12 → RGB(12,125,187)  stroke
    // ColorDark       = 250,246,241 → RGB(241,246,250)  fill
    private static readonly Color4 TmEdge = new(12f / 255f, 125f / 255f, 187f / 255f, 1f);
    private static readonly Color4 TmFill = new(241f / 255f, 246f / 255f, 250f / 255f, 0.94f);
    private static readonly Color4 TmWash = new(241f / 255f, 246f / 255f, 250f / 255f, 0.75f);

    /// <summary>Play paint: TaskmgrPlayer edge stroke + dark fill (not solid edge blob).</summary>
    private void ApplyAccent(byte b, byte g, byte r) => ApplyTaskmgrPalette();

    private void ApplyTaskmgrPalette()
    {
        if (_hudBrush is null || _carBrush is null || _wheelBrush is null
            || _pedalBrush is null || _strokeBrush is null || _trackBrush is null)
        {
            return;
        }

        _hudBrush.Color = TmEdge;
        _carBrush.Color = TmFill;
        _wheelBrush.Color = TmFill;
        _strokeBrush.Color = TmEdge;
        _pedalBrush.Color = TmEdge;
        _trackBrush.Color = TmWash;
        if (_carFlippedBrush is not null)
        {
            // Warm fill; stroke still uses edge blue unless dead.
            _carFlippedBrush.Color = new Color4(1f, 0.92f, 0.78f, 0.94f);
        }

        if (_carDeadBrush is not null)
        {
            _carDeadBrush.Color = new Color4(1f, 0.88f, 0.88f, 0.94f);
        }
    }

    /// <summary>Low poly: ColorDark fill + ColorEdge stroke (TaskmgrPlayer Binarylize roles).</summary>
    private void DrawCar(ID2D1HwndRenderTarget target, CarState car)
    {
        var frameW = _heightField?.FrameWidth ?? _roi?.Width ?? _pixelWidth;
        var frameH = _heightField?.FrameHeight ?? _roi?.Height ?? _pixelHeight;
        if (frameW < 8 || frameH < 8)
        {
            return;
        }

        var sx = _pixelWidth / (float)frameW;
        var sy = _pixelHeight / (float)frameH;
        var cx = car.ChassisX * sx;
        var cy = car.ChassisYFromTop * sy;
        var hw = car.HalfWidth * sx;
        var hh = car.HalfHeight * sy;
        var wr = car.WheelRadius * Math.Min(sx, sy);
        var ox = car.WheelOffsetX * sx;
        var oy = car.WheelOffsetY * sy;
        var fill = car.IsDead
            ? _carDeadBrush!
            : car.ControlsDisabled
                ? _carFlippedBrush!
                : _carBrush!;
        // Always ColorEdge stroke (Binarylize edge role); fill is ColorDark / state wash.
        var outline = _strokeBrush!;
        if (car.IsDead)
        {
            outline.Color = new Color4(0.75f, 0.2f, 0.2f, 1f);
        }

        // Box2D +angle is CCW in Y-up; D2D +angle is CW in Y-down → negate.
        var angle = -car.AngleRad;
        const float strokeW = 2f;
        target.Transform = Matrix3x2.CreateRotation(angle, new Vector2(cx, cy));

        var body = new Rect(cx - hw, cy - hh, hw * 2f, hh * 2f);
        target.FillRectangle(body, fill);
        target.DrawRectangle(body, outline, strokeW);
        var cabin = new Rect(cx - hw * 0.15f, cy - hh * 1.85f, hw * 0.85f, hh * 0.85f);
        target.FillRectangle(cabin, fill);
        target.DrawRectangle(cabin, outline, strokeW);

        target.Transform = Matrix3x2.Identity;

        DrawWheel(target, cx, cy, -ox, oy, angle, wr, fill, outline, strokeW);
        DrawWheel(target, cx, cy, ox, oy, angle, wr, fill, outline, strokeW);

        outline.Color = TmEdge;
    }

    private static void DrawWheel(
        ID2D1HwndRenderTarget target,
        float cx,
        float cy,
        float localX,
        float localY,
        float angleRad,
        float radius,
        ID2D1SolidColorBrush fill,
        ID2D1SolidColorBrush outline,
        float strokeW)
    {
        var cos = MathF.Cos(angleRad);
        var sin = MathF.Sin(angleRad);
        var x = cx + localX * cos - localY * sin;
        var y = cy + localX * sin + localY * cos;
        target.FillEllipse(new Ellipse(new Vector2(x, y), radius, radius), fill);
        target.DrawEllipse(new Ellipse(new Vector2(x, y), radius, radius), outline, strokeW);
    }

    private void DrawCenteredPrompt(ID2D1HwndRenderTarget target, string prompt)
    {
        if (_writeFactory is null || _hudBrush is null)
        {
            return;
        }

        var normalized = prompt.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lineCount = 1;
        for (var i = 0; i < normalized.Length; i++)
        {
            if (normalized[i] == '\n')
            {
                lineCount++;
            }
        }

        var fontSize = System.Math.Clamp(_pixelHeight / (lineCount * 1.25f), 8f, 20f);
        using var format = _writeFactory.CreateTextFormat(
            "Consolas",
            null,
            FontWeight.Normal,
            FontStyle.Normal,
            FontStretch.Normal,
            fontSize,
            "en-US");
        format.TextAlignment = TextAlignment.Leading;
        format.ParagraphAlignment = ParagraphAlignment.Near;

        var maxW = _pixelWidth * 0.94f;
        var maxH = _pixelHeight * 0.92f;
        using var layout = _writeFactory.CreateTextLayout(normalized, format, maxW, maxH);
        var metrics = layout.Metrics;
        var x = (_pixelWidth - metrics.Width) * 0.5f;
        var y = (_pixelHeight - metrics.Height) * 0.5f;
        target.DrawTextLayout(new Vector2(x, y), layout, _hudBrush);
    }

    /// <summary>Vertical center-zero throttle: up forward / down reverse.</summary>
    private void DrawThrottleBar(ID2D1HwndRenderTarget target, CarState car)
    {
        if (_trackBrush is null || _pedalBrush is null || _strokeBrush is null || _hudBrush is null)
        {
            return;
        }

        var barW = 8f;
        var barH = System.Math.Clamp(_pixelHeight * 0.28f, 72f, 140f);
        var x0 = 12f;
        var y0 = System.Math.Max(36f, _pixelHeight - 28f - barH);
        var track = new Rect(x0, y0, barW, barH);
        target.FillRectangle(track, _trackBrush);
        target.DrawRectangle(track, _strokeBrush, 1f);

        var midY = y0 + (barH * 0.5f);
        target.DrawLine(new Vector2(x0 - 2f, midY), new Vector2(x0 + barW + 2f, midY), _hudBrush, 1f);

        var pedal = System.Math.Clamp(car.Pedal, -1f, 1f);
        if (MathF.Abs(pedal) >= 0.02f)
        {
            float fillY;
            float fillH;
            if (pedal >= 0f)
            {
                fillH = (barH * 0.5f) * pedal;
                fillY = midY - fillH;
            }
            else
            {
                fillH = (barH * 0.5f) * -pedal;
                fillY = midY;
            }

            target.FillRectangle(
                new Rect(x0 + 1f, fillY, barW - 2f, System.Math.Max(1f, fillH)),
                _pedalBrush);
        }

        var label = MathF.Abs(pedal) < 0.02f ? "油门" : $"{(int)MathF.Round(pedal * 100f)}%";
        target.DrawText(
            label,
            _textFormat!,
            new Rect(x0 + barW + 6f, midY - 8f, 56f, 20f),
            _hudBrush);
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
        var foreground = NativeMethods.GetForegroundWindow();
        for (var current = foreground;
             current != IntPtr.Zero;
             current = NativeMethods.GetParent(current))
        {
            if (current == mainHwnd || current == chartHwnd)
            {
                return true;
            }
        }

        // Win11's XAML Taskmgr can expose the active surface as a separate top-level
        // HWND in the same process instead of a descendant of TaskManagerWindow.
        // Parent-only matching therefore reports false after a tray/Alt+Tab round trip.
        if (foreground == IntPtr.Zero || mainHwnd == IntPtr.Zero)
        {
            return false;
        }

        _ = NativeMethods.GetWindowThreadProcessId(foreground, out var foregroundPid);
        _ = NativeMethods.GetWindowThreadProcessId(mainHwnd, out var taskmgrPid);
        return foregroundPid != 0 && foregroundPid == taskmgrPid;
    }

    private static bool IsForegroundProcess(uint processId)
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return false;
        }

        _ = NativeMethods.GetWindowThreadProcessId(foreground, out var foregroundPid);
        return foregroundPid != 0 && foregroundPid == processId;
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
        _hudBrush?.Dispose();
        _carBrush?.Dispose();
        _carFlippedBrush?.Dispose();
        _carDeadBrush?.Dispose();
        _cabinBrush?.Dispose();
        _stripeBrush?.Dispose();
        _wheelBrush?.Dispose();
        _hubBrush?.Dispose();
        _glowBrush?.Dispose();
        _strokeBrush?.Dispose();
        _trackBrush?.Dispose();
        _pedalBrush?.Dispose();
        _renderTarget?.Dispose();
        _orangeBrush = null;
        _redBrush = null;
        _hudBrush = null;
        _carBrush = null;
        _carFlippedBrush = null;
        _carDeadBrush = null;
        _cabinBrush = null;
        _stripeBrush = null;
        _wheelBrush = null;
        _hubBrush = null;
        _glowBrush = null;
        _strokeBrush = null;
        _trackBrush = null;
        _pedalBrush = null;
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

