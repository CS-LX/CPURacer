using System.Collections.Generic;

namespace CPURacer.Game;

/// <summary>金币的绘制快照（frame 像素坐标，与 CarState 同一空间）。</summary>
public readonly record struct CoinView(float X, float YFromTop);

/// <summary>Snapshot for overlay drawing (frame / plot pixel space mixed as noted).</summary>
public readonly struct CarState
{
    public CarState(
        float chassisX,
        float chassisYFromTop,
        float angleRad,
        float wheelRadius,
        float wheelOffsetX,
        float wheelOffsetY,
        float wheelSpinRad,
        float halfWidth,
        float halfHeight,
        float pedal,
        float speedPxPerSec,
        float distanceMeters,
        float bestDistanceMeters,
        byte accentB,
        byte accentG,
        byte accentR,
        bool isDead,
        bool controlsDisabled,
        bool isRunning,
        string hud,
        IReadOnlyList<CoinView> coins,
        bool coinsEnabled,
        int coinsCollected)
    {
        ChassisX = chassisX;
        ChassisYFromTop = chassisYFromTop;
        AngleRad = angleRad;
        WheelRadius = wheelRadius;
        WheelOffsetX = wheelOffsetX;
        WheelOffsetY = wheelOffsetY;
        WheelSpinRad = wheelSpinRad;
        HalfWidth = halfWidth;
        HalfHeight = halfHeight;
        Pedal = pedal;
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
        Coins = coins;
        CoinsEnabled = coinsEnabled;
        CoinsCollected = coinsCollected;
    }

    public float ChassisX { get; }
    public float ChassisYFromTop { get; }
    public float AngleRad { get; }
    public float WheelRadius { get; }
    /// <summary>Chassis-local +X to wheel center (frame px).</summary>
    public float WheelOffsetX { get; }
    /// <summary>Chassis-local +Y (down) to wheel center in draw space.</summary>
    public float WheelOffsetY { get; }
    public float WheelSpinRad { get; }
    public float HalfWidth { get; }
    public float HalfHeight { get; }
    /// <summary>Drive pedal in [-1, 1].</summary>
    public float Pedal { get; }
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

    /// <summary>当前视口内可见的金币（frame 像素坐标）。</summary>
    public IReadOnlyList<CoinView> Coins { get; }

    /// <summary>金币模式是否启用。</summary>
    public bool CoinsEnabled { get; }

    /// <summary>本局已收集的金币数。</summary>
    public int CoinsCollected { get; }
}
