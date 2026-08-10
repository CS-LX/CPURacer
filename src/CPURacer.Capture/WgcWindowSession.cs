using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using WinRT;
using Buffer = Windows.Storage.Streams.Buffer;

namespace CPURacer.Capture;

/// <summary>
/// Minimal Windows Graphics Capture session for one HWND.
/// Sets <see cref="GraphicsCaptureSession.IsBorderRequired"/> before StartCapture
/// (no third-party wrapper, no reflection).
/// </summary>
internal sealed class WgcWindowSession : IDisposable
{
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _framePool;
    private readonly GraphicsCaptureSession _session;
    private readonly IDirect3DDevice _device;
    private readonly IntPtr _d3dDevice;
    private readonly IntPtr _d3dContext;
    private SizeInt32 _lastSize;
    private bool _disposed;

    private WgcWindowSession(
        GraphicsCaptureItem item,
        Direct3D11CaptureFramePool framePool,
        GraphicsCaptureSession session,
        IDirect3DDevice device,
        IntPtr d3dDevice,
        IntPtr d3dContext,
        SizeInt32 size)
    {
        _item = item;
        _framePool = framePool;
        _session = session;
        _device = device;
        _d3dDevice = d3dDevice;
        _d3dContext = d3dContext;
        _lastSize = size;
        _item.Closed += OnClosed;
    }

    public static async Task<WgcWindowSession?> TryStartAsync(
        IntPtr hwnd,
        CancellationToken cancellationToken)
    {
        if (hwnd == IntPtr.Zero || !NativeMethodsIsWindow(hwnd))
        {
            return null;
        }

        if (!GraphicsCaptureSession.IsSupported())
        {
            return null;
        }

        await TryRequestBorderlessAccessAsync(cancellationToken).ConfigureAwait(false);

        var item = CreateItemForWindow(hwnd);
        if (item is null)
        {
            return null;
        }

        var size = item.Size;
        if (size.Width <= 0 || size.Height <= 0)
        {
            return null;
        }

        var (device, d3dDevice, d3dContext) = CreateDevice();
        Direct3D11CaptureFramePool? pool = null;
        GraphicsCaptureSession? session = null;
        try
        {
            pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                device,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2,
                size);
            session = pool.CreateCaptureSession(item);

            // Must set before StartCapture when possible; still safe afterward.
            TrySetBorderRequired(session, required: false);
            try
            {
                session.IsCursorCaptureEnabled = false;
            }
            catch
            {
                // Property may be unavailable on older builds.
            }

            session.StartCapture();
            return new WgcWindowSession(item, pool, session, device, d3dDevice, d3dContext, size);
        }
        catch
        {
            session?.Dispose();
            pool?.Dispose();
            device.Dispose();
            Release(d3dContext);
            Release(d3dDevice);
            throw;
        }
    }

    public async Task<(int Width, int Height, byte[] Bgra)?> CaptureBgraAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var frame = await WaitForUsableFrameAsync(cancellationToken).ConfigureAwait(false);
        if (frame is null)
        {
            return null;
        }

        using var bitmap = await SoftwareBitmap
            .CreateCopyFromSurfaceAsync(frame.Surface)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);

        var width = bitmap.PixelWidth;
        var height = bitmap.PixelHeight;
        var byteCount = width * height * 4;
        var buffer = new Buffer((uint)byteCount);
        bitmap.CopyToBuffer(buffer);
        var bgra = new byte[byteCount];
        buffer.CopyTo(bgra);
        return (width, height, bgra);
    }

    private async Task<Direct3D11CaptureFrame?> WaitForUsableFrameAsync(
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);

            var frame = await WaitForNextFrameAsync(cancellationToken).ConfigureAwait(false);
            if (frame is null)
            {
                return null;
            }

            var contentSize = frame.ContentSize;
            if (contentSize.Width <= 0 || contentSize.Height <= 0)
            {
                frame.Dispose();
                continue;
            }

            if (contentSize.Width == _lastSize.Width && contentSize.Height == _lastSize.Height)
            {
                return frame;
            }

            frame.Dispose();
            _framePool.Recreate(
                _device,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2,
                contentSize);
            _lastSize = contentSize;
        }
    }

    private async Task<Direct3D11CaptureFrame?> WaitForNextFrameAsync(
        CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<Direct3D11CaptureFrame?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Direct3D11CaptureFrame? result = null;

        void OnArrived(Direct3D11CaptureFramePool sender, object args)
        {
            try
            {
                var frame = sender.TryGetNextFrame();
                if (frame is null)
                {
                    return;
                }

                if (!tcs.TrySetResult(frame))
                {
                    frame.Dispose();
                }
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }

        _framePool.FrameArrived += OnArrived;
        try
        {
            var existing = _framePool.TryGetNextFrame();
            if (existing is not null)
            {
                result = existing;
                return result;
            }

            result = await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _framePool.FrameArrived -= OnArrived;
            if (tcs.Task.IsCompletedSuccessfully)
            {
                var leftover = tcs.Task.Result;
                if (leftover is not null && !ReferenceEquals(leftover, result))
                {
                    leftover.Dispose();
                }
            }
        }
    }

    private void OnClosed(GraphicsCaptureItem sender, object args) => Dispose();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _item.Closed -= OnClosed;
        }
        catch
        {
            // Ignore.
        }

        try
        {
            _session.Dispose();
        }
        catch
        {
            // Ignore.
        }

        try
        {
            _framePool.Dispose();
        }
        catch
        {
            // Ignore.
        }

        _device.Dispose();
        Release(_d3dContext);
        Release(_d3dDevice);
    }

    private static async Task TryRequestBorderlessAccessAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!ApiInformation.IsMethodPresent(
                    "Windows.Graphics.Capture.GraphicsCaptureAccess",
                    "RequestAccessAsync"))
            {
                return;
            }

            var status = await GraphicsCaptureAccess
                .RequestAccessAsync(GraphicsCaptureAccessKind.Borderless)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
            Debug.WriteLine($"WGC borderless access: {status}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WGC borderless access failed: {ex.Message}");
        }
    }

    private static void TrySetBorderRequired(GraphicsCaptureSession session, bool required)
    {
        try
        {
            if (!ApiInformation.IsPropertyPresent(
                    "Windows.Graphics.Capture.GraphicsCaptureSession",
                    "IsBorderRequired"))
            {
                return;
            }

            session.IsBorderRequired = required;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WGC IsBorderRequired failed: {ex.Message}");
        }
    }

    private static GraphicsCaptureItem? CreateItemForWindow(IntPtr hwnd)
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        if (WindowsCreateString(className, className.Length, out var hName) != 0)
        {
            return null;
        }

        try
        {
            var interopGuid = typeof(IGraphicsCaptureItemInterop).GUID;
            if (RoGetActivationFactory(hName, ref interopGuid, out var factory) != 0)
            {
                return null;
            }

            try
            {
                var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factory);
                var iid = Guid.Parse("79C3F95B-31F7-4EC2-A464-632EF5D30760");
                var ptr = interop.CreateForWindow(hwnd, ref iid);
                if (ptr == IntPtr.Zero)
                {
                    return null;
                }

                try
                {
                    return MarshalInspectable<GraphicsCaptureItem>.FromAbi(ptr);
                }
                finally
                {
                    Marshal.Release(ptr);
                }
            }
            finally
            {
                Marshal.Release(factory);
            }
        }
        finally
        {
            WindowsDeleteString(hName);
        }
    }

    private static (IDirect3DDevice Device, IntPtr D3dDevice, IntPtr D3dContext) CreateDevice()
    {
        var hr = D3D11CreateDevice(
            IntPtr.Zero,
            1,
            IntPtr.Zero,
            0x20,
            IntPtr.Zero,
            0,
            7,
            out var d3dDevice,
            out _,
            out var d3dContext);
        if (hr != 0)
        {
            throw new InvalidOperationException($"D3D11CreateDevice failed: 0x{hr:X}");
        }

        var dxgiGuid = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
        hr = Marshal.QueryInterface(d3dDevice, ref dxgiGuid, out var dxgi);
        if (hr != 0)
        {
            Release(d3dContext);
            Release(d3dDevice);
            throw new InvalidOperationException($"QueryInterface(IDXGIDevice) failed: 0x{hr:X}");
        }

        try
        {
            hr = CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var inspectable);
            if (hr != 0)
            {
                throw new InvalidOperationException(
                    $"CreateDirect3D11DeviceFromDXGIDevice failed: 0x{hr:X}");
            }

            try
            {
                var device = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
                return (device, d3dDevice, d3dContext);
            }
            finally
            {
                Marshal.Release(inspectable);
            }
        }
        catch
        {
            Release(d3dContext);
            Release(d3dDevice);
            throw;
        }
        finally
        {
            Marshal.Release(dxgi);
        }
    }

    private static void Release(IntPtr p)
    {
        if (p != IntPtr.Zero)
        {
            Marshal.Release(p);
        }
    }

    private static bool NativeMethodsIsWindow(IntPtr hwnd) =>
        CPURacer.Native.NativeMethods.IsWindow(hwnd);

    [ComImport]
    [System.Runtime.InteropServices.Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);

        IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
    }

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string sourceString, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(
        IntPtr pAdapter,
        int driverType,
        IntPtr software,
        uint flags,
        IntPtr featureLevels,
        uint featureLevelCount,
        uint sdkVersion,
        out IntPtr device,
        out int featureLevel,
        out IntPtr context);

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(
        IntPtr dxgiDevice,
        out IntPtr graphicsDevice);
}
