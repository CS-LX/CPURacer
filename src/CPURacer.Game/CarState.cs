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
        float distanceMeters,
        float bestDistanceMeters,
        byte accentB,
        byte accentG,
        byte accentR,
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
        DistanceMeters = distanceMeters;
        BestDistanceMeters = bestDistanceMeters;
        AccentB = accentB;
        AccentG = accentG;
        AccentR = accentR;
        IsDead = isDead;
        ControlsDisabled = controlsDisabled;
        IsRunning = isRunning;
        Hud = hud;
    }

    public float ChassisX { get; }
    public float ChassisYFromTop { get; }
    public float AngleRad { get; }
    public float WheelRadius { get; }
    public float HalfWidth { get; }
    public float HalfHeight { get; }
    public float SpeedPxPerSec { get; }
    public float DistanceMeters { get; }
    public float BestDistanceMeters { get; }
    public byte AccentB { get; }
    public byte AccentG { get; }
    public byte AccentR { get; }
    public bool IsDead { get; }
    public bool ControlsDisabled { get; }
    public bool IsRunning { get; }
    public string Hud { get; }
}