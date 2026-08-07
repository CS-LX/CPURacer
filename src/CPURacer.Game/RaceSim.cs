namespace CPURacer.Game;

/// <summary>
/// Racing simulation driven by height fields. Stub in M0; implement in M3.
/// </summary>
public sealed class RaceSim
{
    public bool IsRunning { get; private set; }

    public void Reset()
    {
        IsRunning = false;
    }

    public void Step(double dtSeconds)
    {
        if (!IsRunning)
        {
            return;
        }

        _ = dtSeconds;
        // M3: Box2D step
    }

    public void Start() => IsRunning = true;

    public void Stop() => IsRunning = false;
}
