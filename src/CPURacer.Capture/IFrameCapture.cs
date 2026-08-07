namespace CPURacer.Capture;

/// <summary>
/// Captures a composed frame for a chart ROI. Stub in M0; implement in M2.
/// </summary>
public interface IFrameCapture
{
    /// <summary>Returns null when capture is unavailable.</summary>
    byte[]? CaptureBgra(int width, int height);
}

public sealed class NullFrameCapture : IFrameCapture
{
    public byte[]? CaptureBgra(int width, int height) => null;
}
