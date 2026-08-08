namespace CPURacer.Game;

/// <summary>Snapshot for overlay drawing (frame / plot pixel space mixed as noted).</summary>
public readonly struct CarState
{
    public CarState(
        float chassisX,
        float chassisYFromTop,
        float angleRad,
        float wheelRadius,
        float halfWidth,
        float halfHeight,
        float speedPxPerSec,
        bool isDead,
        bool controlsDisabled,
        bool isRunning,
        string hud)
    {
        ChassisX = chassisX;
        ChassisYFromTop = chassisYFromTop;
        AngleRad = angleRad;
        WheelRadius = wheelRadius;
        HalfWidth = halfWidth;
        HalfHeight = halfHeight;
        SpeedPxPerSec = speedPxPerSec;
        IsDead = isDead;
        ControlsDisabled = controlsDisabled;
        IsRunning = isRunning;
        Hud = hud;
    }

    /// <summary>Chassis center X in frame pixels (includes left inset).</summary>
    public float ChassisX { get; }

    /// <summary>Chassis center Y from top of frame (pixels).</summary>
    public float ChassisYFromTop { get; }

    public float AngleRad { get; }

    public float WheelRadius { get; }

    public float HalfWidth { get; }

    public float HalfHeight { get; }

    public float SpeedPxPerSec { get; }

    public bool IsDead { get; }

    public bool ControlsDisabled { get; }

    public bool IsRunning { get; }

    public string Hud { get; }
}
