using System.Diagnostics;
using Box2DX.Collision;
using Box2DX.Common;
using Box2DX.Dynamics;
using CPURacer.Capture;
using CPURacer.Localization;
using CPURacer.Native;

namespace CPURacer.Game;

/// <summary>
/// Hill-climb racer on Taskmgr height-field.
/// Physics lives in a true world polyline (meters); camera-space HF only advances
/// <see cref="_scrollOriginPx"/> and appends right-edge columns. Drawing projects
/// world → viewport pixels. Car: hard axle + revolute motors (Feronato / wireframe style).
/// </summary>
public sealed class RaceSim
{
    /// <summary>Plot pixels per physics meter. Car length ≈ 1.4 m at this scale.</summary>
    private const float PixelsPerMeter = 60f;

    private const float GravityY = -10f;
    private const float FixedDt = 1f / 60f;
    private const int MaxSubSteps = 4;

    private const float ChassisHalfW = 0.55f;
    private const float ChassisHalfH = 0.14f;
    private const float WheelRadius = 0.22f;
    private const float WheelOffsetX = 0.48f;
    /// <summary>重生/开局时轮子与地形的间隙（米）：太小会让刚体初始重叠被弹飞。</summary>
    private const float SpawnClearanceM = 0.04f;

    // Pedal ∈ [-1,1]: 按住 W/S 越久油门越大（约 1s 到满）；松开后自动缓慢回落到 0。
    private const float ThrottleRampPerSec = 1.0f;
    private const float PedalDecayPerSec = 0.7f;
    // Revolute motor at |pedal|=1 (rad/s, N·m). Negative ω → +X (forward).
    private const float DriveMotorSpeed = 28f;
    private const float DriveMotorTorque = 3f;
    private const float CoastMotorTorque = 0.5f;
    private const float FrontDriveBlend = 0.6f;

    /// <summary>翻车提示阈值：底盘角度超过 90° 视为翻倒（仅提示/车身变色，不禁用油门）。</summary>
    private const float FlipAngleRad = MathF.PI / 2f;
    /// <summary>翻车解除角：翻倒后角度回到 60° 以下才算回正（滞回，防角度摆动重置计时）。</summary>
    private const float FlipRecoverClearRad = MathF.PI / 3f;
    /// <summary>翻车回正惩罚：翻倒持续此时间后才允许按 R 回正（毫秒）。</summary>
    private const long FlipRecoverDelayMs = 1000;
    /// <summary>物理地面折线段数上限：越大越贴合可见曲线（出生/行驶误差越小）。</summary>
    private const int MaxTerrainSegments = 240;
    private const float WorldMarginScreens = 0.35f;
    private const int MaxScrollShiftPx = 24;
    private const float ScrollSmooth = 0.25f;
    /// <summary>左侧：车中心超出图表左边界此量才判失败（整车调出图表区域）。</summary>
    private const float LeftFailPx = 40f;
    /// <summary>右侧空气墙：视口右缘外此量的位置（物理垂直墙，随滚动重建跟随）。</summary>
    private const float RightWallMarginPx = 8f;
    /// <summary>底部传送线：车中心低于图底此量即重生（须低于最低地形，防正常低谷误触）。</summary>
    private const float BottomRespawnPx = 24f;
    // 金币：严格贴附任务管理器曲线生成（每枚独立采样曲线高度），随世界滚动在前方生成、在身后回收。
    private const float CoinHoverPx = 26f;
    private const float CoinCollectRadiusPx = 30f;
    private const float CoinSpawnAheadPx = 240f;
    private const float CoinGapMinPx = 150f;
    private const float CoinGapMaxPx = 260f;
    private const float CoinArcStepPx = 28f;
    private const float CoinDespawnBehindPx = 100f;
    /// <summary>跳变前馈：预测更新时刻前开始偏移的提前窗，与入账超时窗（QPC）。</summary>
    private static readonly TimeSpan JumpLeadWindow = TimeSpan.FromMilliseconds(32);
    private static readonly TimeSpan JumpTimeoutWindow = TimeSpan.FromMilliseconds(60);

    private World? _world;
    private Body? _ground;
    private Body? _chassis;
    private Body? _wheelBack;
    private Body? _wheelFront;
    private RevoluteJoint? _motorBack;
    private RevoluteJoint? _motorFront;

    private HeightField? _terrain;
    /// <summary>Last camera-space surface Y in plot pixels (for scroll correlation).</summary>
    private float[]? _cameraYPx;
    private readonly List<float> _worldXPx = new(1024);
    private readonly List<float> _worldYPx = new(1024);

    private int _plotWPx;
    private int _plotHPx;
    private float _plotWM;
    private float _plotHM;
    private int _insetLeft;
    private int _insetTop;

    /// <summary>World plot-pixel X of the viewport's left edge.</summary>
    private float _scrollOriginPx;
    private long _lastScrollSampleMs;
    private float _scrollPxPerSec;
    private double _stepAccumulator;

    // 跳变前馈预测状态（渲染层）：预测更新时刻到达时把车预偏移 Δ，
    // 滚动实际入账后取消；预测超时未入账则放弃本次偏移。
    //
    // 现状（2026-08 诊断日志 %TEMP%\CPURacer-diag.log）：
    // - Taskmgr 采样周期极稳（1000ms ±2%，捕获层中位数估计），跳变量 Δ≈24px。
    // - 命中率约 85%（jump: preApplied=True）：跳变瞬间车与背景同步，零延迟。
    // - 未命中 ~15% 的机制：
    //   1) 捕获 worker 更新相位（_lastUpdateTicks）比 UI 滚动入账快一个循环，
    //      _nextUpdateTime 偶尔已指向下下次跳变，预偏移漏过本次（err≈-966ms 簇）；
    //   2) EstimateScrollShiftPx 保守阈值（bestErr > baseline*0.88 判 0）
    //      在跳变帧（整图左移 24px + 曲线形状变化）偶发漏检，滚动入账延迟。
    // - 自愈：错过一次后，下一次 SetPredictedUpdate 基于新相位立即恢复命中。
    // - 若再优化：让相位来源（_lastUpdateTicks）与滚动入账基于同一帧（数据流重构），
    //   可消除 1) 的错位；放宽滚动检测阈值可减少 2)。两者收益均为边际。
    private TimeSpan _nextUpdateTime;
    private bool _hasPrediction;
    private float _pendingJumpPx;
    private float _lastJumpPx;
    private bool _jumpApplied;

    private bool _throttleKey;
    private bool _brakeKey;
    /// <summary>Continuous pedal in [-1, 1]. +1 full forward, -1 full reverse.</summary>
    private float _pedal;
    private bool _dead;
    private bool _controlsDisabled;
    /// <summary>翻车开始时刻（TickCount64）；0 = 未翻车。翻倒持续超时后允许 R 回正。</summary>
    private long _flipSinceMs;
    private float _spawnWorldXPx;
    private float _maxWorldXPx;
    /// <summary>重生视口 X（相对图表左缘，可为负=界外）；NaN 表示未记录（用 22% 兑底）。</summary>
    private float _respawnViewXPx = float.NaN;
    private float _runDistanceM;
    private float _sessionBestM;
    private string _deathReason = "";
    /// <summary>存活金币（世界坐标，plot px；X 为世界 X，Y 为世界 Y 向上）。</summary>
    private readonly List<Vec2> _coins = new(64);
    private float _nextCoinXPx;
    private int _coinsCollected;
    private readonly Random _coinRandom = new();
    private bool _coinsEnabled = true;

    public bool IsRunning { get; private set; }

    public bool IsDead => _dead;

    /// <summary>Smoothed scroll of the viewport through world space (plot px/s).</summary>
    public float ScrollPxPerSec => _scrollPxPerSec;

    public float DistanceMeters => _runDistanceM;

    public float BestDistanceMeters => _sessionBestM;

    /// <summary>本局已收集的金币数（供 HUD / 结算展示）。</summary>
    public int CoinsCollected => _coinsCollected;

    /// <summary>金币模式默认开启；关闭后不生成、绘制或收集金币。</summary>
    public bool CoinsEnabled => _coinsEnabled;

    public void SetCoinModeEnabled(bool enabled)
    {
        if (_coinsEnabled == enabled)
        {
            return;
        }

        _coinsEnabled = enabled;
        _coins.Clear();
        if (!enabled || !IsRunning)
        {
            return;
        }

        var carXPx = _chassis is null
            ? _scrollOriginPx + (_plotWPx * 0.22f)
            : _chassis.GetPosition().X * PixelsPerMeter;
        _nextCoinXPx = System.Math.Max(
            carXPx + (CoinSpawnAheadPx * 0.5f),
            _scrollOriginPx + (_plotWPx * 0.5f));
        SpawnCoinsAhead();
    }

    public void Start()
    {
        if (_terrain is null || _plotWPx < 16 || _cameraYPx is null)
        {
            return;
        }

        EnsureWorld();
        _stepAccumulator = 0;
        SeedWorldFromCamera();
        RebuildGroundFromWorld();
        SpawnVehicle();
        ResetRunStats();
        ResetCoins();
        _pedal = 0;
        _dead = false;
        _controlsDisabled = false;
        IsRunning = true;
        _lastScrollSampleMs = Environment.TickCount64;
        _scrollPxPerSec = 0;
        ResetJumpPrediction();
    }

    public void Stop()
    {
        IsRunning = false;
        DestroyVehicle();
        DestroyGround();
        _worldXPx.Clear();
        _worldYPx.Clear();
        _scrollOriginPx = 0;
        _pedal = 0;
        _runDistanceM = 0;
        _deathReason = "";
        _coins.Clear();
        _coinsCollected = 0;
        _nextCoinXPx = 0;
        _dead = false;
        _controlsDisabled = false;
        _stepAccumulator = 0;
        _scrollPxPerSec = 0;
        ResetJumpPrediction();
    }

    public void Restart()
    {
        if (_terrain is null || _cameraYPx is null)
        {
            return;
        }

        EnsureWorld();
        _stepAccumulator = 0;
        SeedWorldFromCamera();
        RebuildGroundFromWorld();
        SpawnVehicle();
        ResetRunStats();
        ResetCoins();
        _pedal = 0;
        _dead = false;
        _controlsDisabled = false;
        IsRunning = true;
        _lastScrollSampleMs = Environment.TickCount64;
        _scrollPxPerSec = 0;
        ResetJumpPrediction();
    }

    private void ResetRunStats()
    {
        _spawnWorldXPx = _scrollOriginPx + (_plotWPx * 0.22f);
        _maxWorldXPx = _spawnWorldXPx;
        _runDistanceM = 0;
        _deathReason = "";
        _respawnViewXPx = float.NaN;
        _flipSinceMs = 0;
    }

    /// <summary>W = 油门随时长正向增大，S = 反向增大；松开后油门自动缓慢归零。</summary>
    public void SetInput(bool throttle, bool brake)
    {
        _throttleKey = throttle;
        _brakeKey = brake;
    }

    /// <summary>
    /// Push camera-space height field. Detects scroll, advances
    /// <see cref="_scrollOriginPx"/>, and appends new right-edge world columns.
    /// Does not translate the car.
    /// </summary>
    public void SetTerrain(HeightField field)
    {
        var previousCamera = _cameraYPx;
        var previousPlotW = _plotWPx;
        var sizeChanged = previousPlotW > 0
            && (field.PlotWidth != previousPlotW
                || field.Inset.ContentHeight(field.FrameHeight) != _plotHPx
                || field.Inset.Left != _insetLeft
                || field.Inset.Top != _insetTop);

        _terrain = field;
        CacheCameraSurface(field);

        if (!IsRunning)
        {
            _lastScrollSampleMs = Environment.TickCount64;
            return;
        }

        if (sizeChanged)
        {
            SeedWorldFromCamera();
            RebuildGroundFromWorld();
            SpawnVehicle();
            ResampleCoinHeights();
            _lastScrollSampleMs = Environment.TickCount64;
            return;
        }

        var shiftPx = EstimateScrollShiftPx(previousCamera, _cameraYPx, previousPlotW);
        UpdateScrollRate(shiftPx);

        if (shiftPx > 0)
        {
            _scrollOriginPx += shiftPx;
            _lastJumpPx = shiftPx;
            // 诊断：预偏移是否已生效（预测命中率）、入账相对预测时刻的偏差。
            var appliedBefore = _jumpApplied;
            // 滚动已入账：取消预偏移，车回到物理正确位置。
            _jumpApplied = false;
            if (_hasPrediction)
            {
                var errMs = (QpcNow() - _nextUpdateTime).TotalMilliseconds;
                DiagLog.Write(
                    $"jump: preApplied={appliedBefore} err={errMs:F0}ms delta={shiftPx}px");
            }
        }

        // Lock in-view world columns to the live HF so physics cannot lead/lag the polyline.
        var yDelta = SyncViewportFromCamera();
        if (shiftPx > 0 || _ground is null || yDelta >= 0.8f)
        {
            RebuildGroundFromWorld();
        }
    }

    public void Step(double dtSeconds)
    {
        if (!IsRunning || _world is null || _chassis is null || _dead)
        {
            return;
        }

        var dt = System.Math.Clamp(dtSeconds, 0, 0.1);
        _stepAccumulator += dt;
        var steps = 0;
        while (_stepAccumulator >= FixedDt && steps < MaxSubSteps)
        {
            FixedStep(FixedDt);
            _stepAccumulator -= FixedDt;
            steps++;
        }

        if (steps == MaxSubSteps)
        {
            _stepAccumulator = 0;
        }

        UpdateFlipState();
        var pos = _chassis.GetPosition();
        UpdateDistance(pos);
        UpdateCoins(pos);
        // 失败/边界：左侧调出图表区域才判失败；顶部允许出去（重力会掉回）；
        // 右侧为空气墙（物理墙）；底部低于地形即传送重生（多为物理 bug 卡穿，
        // 不是玩家失误，不判失败且保留本局距离/成绩）。
        var viewXPx = (pos.X * PixelsPerMeter) - _scrollOriginPx;
        if (viewXPx < -LeftFailPx)
        {
            _dead = true;
            IsRunning = false;
            _deathReason = "驶出赛道";
            // The score remains available to the game-over prompt, but the live pickups
            // belong to the active race only and must not survive into the result screen.
            _coins.Clear();
            if (_runDistanceM > _sessionBestM)
            {
                _sessionBestM = _runDistanceM;
            }

            SetWheelMotors(0f, 0f, 0f, 0f);
        }
        else if (pos.Y * PixelsPerMeter < -BottomRespawnPx)
        {
            // 记录掉下去时的视口 X，原地重生（不回到 22%）。
            _respawnViewXPx = viewXPx;
            RespawnVehicle();
        }
    }

    private void UpdateDistance(Vec2 centerM)
    {
        var worldXPx = centerM.X * PixelsPerMeter;
        if (worldXPx > _maxWorldXPx)
        {
            _maxWorldXPx = worldXPx;
        }

        _runDistanceM = System.Math.Max(0f, (_maxWorldXPx - _spawnWorldXPx) / PixelsPerMeter);
        if (_runDistanceM > _sessionBestM && !_dead)
        {
            _sessionBestM = _runDistanceM;
        }
    }

    /// <summary>清空并重新铺金币：首批出现在出生点前方。</summary>
    private void ResetCoins()
    {
        _coins.Clear();
        _coinsCollected = 0;
        _nextCoinXPx = _spawnWorldXPx + (CoinSpawnAheadPx * 0.5f);
        if (_coinsEnabled)
        {
            SpawnCoinsAhead();
        }
    }

    /// <summary>前方补金币，直到铺满视口右缘外 SpawnAhead 距离。</summary>
    private void SpawnCoinsAhead()
    {
        if (!_coinsEnabled)
        {
            return;
        }

        var limit = _scrollOriginPx + _plotWPx + CoinSpawnAheadPx;
        while (_nextCoinXPx < limit)
        {
            SpawnCoinGroup(_nextCoinXPx);
        }
    }

    /// <summary>在 xPx 处生成一组金币（1~3 枚）：每枚独立采样曲线高度，严格跟随曲线起伏。</summary>
    private void SpawnCoinGroup(float xPx)
    {
        var count = 1 + _coinRandom.Next(0, 3);
        for (var i = 0; i < count; i++)
        {
            var cx = xPx + (i * CoinArcStepPx);
            _coins.Add(new Vec2(cx, SampleSurfaceYPx(cx) + CoinHoverPx));
        }

        var gap = CoinGapMinPx
                  + ((CoinGapMaxPx - CoinGapMinPx) * (float)_coinRandom.NextDouble());
        _nextCoinXPx = xPx + (count * CoinArcStepPx) + gap;
    }

    /// <summary>每帧：前方补生成、车后回收、金币重锚定当前曲线、近距收集。</summary>
    private void UpdateCoins(Vec2 carPosM)
    {
        if (!_coinsEnabled)
        {
            return;
        }

        SpawnCoinsAhead();

        var carXPx = carPosM.X * PixelsPerMeter;
        var carYPx = carPosM.Y * PixelsPerMeter;
        var collectSq = CoinCollectRadiusPx * CoinCollectRadiusPx;
        var despawnXPx = _scrollOriginPx - CoinDespawnBehindPx;
        for (var i = _coins.Count - 1; i >= 0; i--)
        {
            if (_coins[i].X < despawnXPx)
            {
                _coins.RemoveAt(i);
                continue;
            }

            // 重锚定：曲线重同步/跳变后金币仍严格贴附当前曲线。
            _coins[i] = new Vec2(_coins[i].X, SampleSurfaceYPx(_coins[i].X) + CoinHoverPx);
            var dx = _coins[i].X - carXPx;
            var dy = _coins[i].Y - carYPx;
            if ((dx * dx) + (dy * dy) <= collectSq)
            {
                _coins.RemoveAt(i);
                _coinsCollected++;
            }
        }
    }

    /// <summary>窗口尺寸变化重建世界后：金币重新贴附新地形（越界丢弃），并续铺前方。</summary>
    private void ResampleCoinHeights()
    {
        if (!_coinsEnabled)
        {
            return;
        }

        if (_worldXPx.Count == 0)
        {
            _coins.Clear();
            _nextCoinXPx = _scrollOriginPx + _plotWPx;
            SpawnCoinsAhead();
            return;
        }

        for (var i = _coins.Count - 1; i >= 0; i--)
        {
            var x = _coins[i].X;
            if (x < _worldXPx[0] || x > _worldXPx[^1])
            {
                _coins.RemoveAt(i);
            }
            else
            {
                _coins[i] = new Vec2(x, SampleSurfaceYPx(x) + CoinHoverPx);
            }
        }

        _nextCoinXPx = System.Math.Max(_nextCoinXPx, _scrollOriginPx + _plotWPx);
        SpawnCoinsAhead();
    }

    public CarState? GetCarState()
    {
        if (_chassis is null || _plotWPx <= 0)
        {
            return null;
        }

        var p = _chassis.GetPosition();
        var angle = _chassis.GetAngle();
        var v = _chassis.GetLinearVelocity();
        var speedMps = MathF.Sqrt(v.X * v.X + v.Y * v.Y);
        var speedPx = speedMps * PixelsPerMeter;
        var worldXPx = p.X * PixelsPerMeter;
        // Match fit polyline column centers (inset + i + 0.5).
        // 跳变前馈：预测时刻内车提前 Δ（贴合跳变后背景），入账后自动取消。
        UpdateJumpPrediction();
        var renderOriginPx = _scrollOriginPx + (_jumpApplied ? _pendingJumpPx : 0f);
        var chassisXPx = _insetLeft + (worldXPx - renderOriginPx) + 0.5f;
        var worldYPx = p.Y * PixelsPerMeter;
        var yFromTop = CoordMapper.WorldYToFrameYFromTop(worldYPx, _insetTop, _plotHPx);
        // 可见金币 → frame 像素坐标（与底盘同一绘制空间）。
        var coins = new List<CoinView>(_coinsEnabled ? _coins.Count : 0);
        if (_coinsEnabled)
        {
            var coinLeftLimit = _insetLeft - 24f;
            var coinRightLimit = _insetLeft + _plotWPx + 24f;
            for (var i = 0; i < _coins.Count; i++)
            {
                var coinX = _insetLeft + (_coins[i].X - renderOriginPx) + 0.5f;
                if (coinX < coinLeftLimit || coinX > coinRightLimit)
                {
                    continue;
                }

                var coinYFromTop = CoordMapper.WorldYToFrameYFromTop(_coins[i].Y, _insetTop, _plotHPx);
                coins.Add(new CoinView(coinX, coinYFromTop));
            }
        }

        // Idle/game-over copy is owned by App + Localization (centered Figgle prompts).
        var hud = _dead
            ? string.Empty
            : _controlsDisabled
                ? FlipRecoverReady
                    ? string.Format(Locale.Culture, Strings.HudFlipRecoverReady, _runDistanceM)
                    : string.Format(
                        Locale.Culture,
                        Strings.HudFlipRecoverWait,
                        _runDistanceM,
                        System.Math.Max(0.0, (FlipRecoverDelayMs - (Environment.TickCount64 - _flipSinceMs)) / 1000.0))
                : string.Format(Locale.Culture, Strings.HudRacing, _runDistanceM, _sessionBestM);

        // TaskmgrPlayer ColorEdge RGB(12,125,187) as BGRA accent defaults.
        var ab = _terrain?.AccentB ?? 187;
        var ag = _terrain?.AccentG ?? 125;
        var ar = _terrain?.AccentR ?? 12;
        // Draw Y-down: wheel hub is below chassis by the axle length used at spawn.
        var axleYM = ChassisHalfH;
        var wheelSpin = _wheelBack?.GetAngle() ?? 0f;

        return new CarState(
            chassisX: chassisXPx,
            chassisYFromTop: yFromTop,
            angleRad: angle,
            wheelRadius: WheelRadius * PixelsPerMeter,
            wheelOffsetX: WheelOffsetX * PixelsPerMeter,
            wheelOffsetY: axleYM * PixelsPerMeter,
            wheelSpinRad: wheelSpin,
            halfWidth: ChassisHalfW * PixelsPerMeter,
            halfHeight: ChassisHalfH * PixelsPerMeter,
            pedal: _pedal,
            speedPxPerSec: speedPx,
            distanceMeters: _runDistanceM,
            bestDistanceMeters: _sessionBestM,
            accentB: ab,
            accentG: ag,
            accentR: ar,
            isDead: _dead,
            controlsDisabled: _controlsDisabled,
            isRunning: IsRunning,
            hud: hud,
            coins: coins,
            coinsEnabled: _coinsEnabled,
            coinsCollected: _coinsCollected);
    }

    private void FixedStep(float dt)
    {
        ApplyDrive(dt);
        try
        {
            _world!.Step(dt, 8, 3);
        }
        catch (IndexOutOfRangeException)
        {
            if (_terrain is not null && _cameraYPx is not null)
            {
                SeedWorldFromCamera();
                RebuildGroundFromWorld();
                SpawnVehicle();
            }
        }
    }

    private void EnsureWorld()
    {
        if (_world is not null)
        {
            return;
        }

        // Large AABB: world X grows with scrollOrigin over a long race.
        var aabb = new AABB();
        aabb.LowerBound.Set(-20f, -20f);
        aabb.UpperBound.Set(4000f, 80f);
        _world = new World(aabb, new Vec2(0, GravityY), doSleep: true);
        _world.SetContinuousPhysics(false);
        _world.SetWarmStarting(true);
    }

    private void CacheCameraSurface(HeightField field)
    {
        _plotWPx = field.PlotWidth;
        _plotHPx = field.Inset.ContentHeight(field.FrameHeight);
        _plotWM = _plotWPx / PixelsPerMeter;
        _plotHM = _plotHPx / PixelsPerMeter;
        _insetLeft = field.Inset.Left;
        _insetTop = field.Inset.Top;

        _cameraYPx = new float[_plotWPx];
        for (var i = 0; i < _plotWPx; i++)
        {
            _cameraYPx[i] = CoordMapper.FrameYFromTopToWorldY(field.YFromTop[i], field.Inset.Top, _plotHPx);
        }
    }

    private void SeedWorldFromCamera()
    {
        _worldXPx.Clear();
        _worldYPx.Clear();
        _scrollOriginPx = 0;
        _ = SyncViewportFromCamera();
    }

    /// <summary>
    /// Rewrite the visible strip as worldX = scrollOrigin + camCol from the live camera HF.
    /// Keeps a short left margin from the previous buffer for continuity. Returns mean |ΔY| px
    /// over the viewport (0 if first fill).
    /// </summary>
    private float SyncViewportFromCamera()
    {
        if (_cameraYPx is null || _plotWPx < 2)
        {
            return 0f;
        }

        var keepLeft = _scrollOriginPx - (_plotWPx * WorldMarginScreens);
        var newX = new List<float>(_plotWPx + 64);
        var newY = new List<float>(_plotWPx + 64);

        for (var i = 0; i < _worldXPx.Count; i++)
        {
            var x = _worldXPx[i];
            if (x >= keepLeft && x < _scrollOriginPx - 0.5f)
            {
                newX.Add(x);
                newY.Add(_worldYPx[i]);
            }
        }

        double sumAbs = 0;
        var cmp = 0;
        for (var cam = 0; cam < _plotWPx; cam++)
        {
            var worldX = _scrollOriginPx + cam;
            var y = _cameraYPx[cam];
            var oldY = SampleWorldYPx(worldX);
            if (!float.IsNaN(oldY))
            {
                sumAbs += MathF.Abs(y - oldY);
                cmp++;
            }

            newX.Add(worldX);
            newY.Add(y);
        }

        _worldXPx.Clear();
        _worldYPx.Clear();
        _worldXPx.AddRange(newX);
        _worldYPx.AddRange(newY);

        return cmp == 0 ? float.MaxValue : (float)(sumAbs / cmp);
    }

    private float SampleWorldYPx(float worldXPx)
    {
        if (_worldXPx.Count == 0)
        {
            return float.NaN;
        }

        if (worldXPx <= _worldXPx[0] || worldXPx >= _worldXPx[^1])
        {
            if (worldXPx < _worldXPx[0] - 1f || worldXPx > _worldXPx[^1] + 1f)
            {
                return float.NaN;
            }

            return worldXPx <= _worldXPx[0] ? _worldYPx[0] : _worldYPx[^1];
        }

        for (var i = 0; i < _worldXPx.Count - 1; i++)
        {
            if (worldXPx > _worldXPx[i + 1])
            {
                continue;
            }

            var x0 = _worldXPx[i];
            var x1 = _worldXPx[i + 1];
            var t = (x1 - x0) < 1e-3f ? 0f : (worldXPx - x0) / (x1 - x0);
            return _worldYPx[i] * (1 - t) + _worldYPx[i + 1] * t;
        }

        return float.NaN;
    }

    private void RebuildGroundFromWorld()
    {
        if (_world is null || _worldXPx.Count < 2)
        {
            return;
        }

        DestroyGround();

        var bd = new BodyDef();
        bd.Position.Set(0, 0);
        _ground = _world.CreateBody(bd);
        _ground.SetStatic();

        var n = _worldXPx.Count;
        var step = System.Math.Max(1, n / MaxTerrainSegments);
        for (var i0 = 0; i0 < n - 1; i0 += step)
        {
            var i1 = System.Math.Min(n - 1, i0 + step);
            var edge = new EdgeDef
            {
                Friction = 1.2f,
                Restitution = 0.0f,
            };
            edge.Vertex1.Set(_worldXPx[i0] / PixelsPerMeter, _worldYPx[i0] / PixelsPerMeter);
            edge.Vertex2.Set(_worldXPx[i1] / PixelsPerMeter, _worldYPx[i1] / PixelsPerMeter);
            _ground.CreateFixture(edge);
        }

        var x0 = _worldXPx[0] / PixelsPerMeter;
        var x1 = _worldXPx[^1] / PixelsPerMeter;
        AddEdge(x0, -1.5f, x1, -1.5f, 0.3f);

        // 右侧空气墙：防止加速冲出视口右缘（位置随滚动重建更新）。
        var wallX = (_scrollOriginPx + _plotWPx + RightWallMarginPx) / PixelsPerMeter;
        AddEdge(wallX, -1.5f, wallX, _plotHM + 1f, 0.1f);

        _chassis?.WakeUp();
        _wheelBack?.WakeUp();
        _wheelFront?.WakeUp();
    }

    private void AddEdge(float x0, float y0, float x1, float y1, float friction)
    {
        if (_ground is null)
        {
            return;
        }

        var edge = new EdgeDef { Friction = friction, Restitution = 0f };
        edge.Vertex1.Set(x0, y0);
        edge.Vertex2.Set(x1, y1);
        _ground.CreateFixture(edge);
    }

    private void SpawnVehicle() => SpawnVehicleAt(_plotWPx * 0.22f);

    /// <summary>
    /// 在指定视口 X（相对图表左缘）生成车辆。
    /// 精确贴合曲线：前后轮各自采样地形高度，底盘角取局部坡度角，
    /// 轮心落在坡度线上方（半径+间隙），再整体抬升消除初始穿入。
    /// </summary>
    private void SpawnVehicleAt(float spawnViewXPx)
    {
        if (_world is null || _worldXPx.Count < 2 || _plotWPx < 16)
        {
            return;
        }

        DestroyVehicle();

        var spawnXPx = _scrollOriginPx + spawnViewXPx;
        var spawnXM = spawnXPx / PixelsPerMeter;
        // 前后轮位置的地形采样 → 局部坡度角（保证出生姿态与曲线走向一致）。
        var backXM = spawnXM - WheelOffsetX;
        var frontXM = spawnXM + WheelOffsetX;
        var backSurfaceM = SampleSurfaceM(backXM);
        var frontSurfaceM = SampleSurfaceM(frontXM);
        var slopeRad = MathF.Atan2(frontSurfaceM - backSurfaceM, frontXM - backXM);

        var wheelYM = ((backSurfaceM + frontSurfaceM) * 0.5f) + WheelRadius + SpawnClearanceM;
        var chassisYM = wheelYM + ChassisHalfH;

        // 防初始重叠（凹槽/陡坡）：整车轮廓任一点穿入地形则整体上移，
        // 物理从“空中小落”开始，避免 Box2D 穿透修复猛弹/卡死。
        for (var iter = 0; iter < 8; iter++)
        {
            var pen = MaxAssemblyPenetrationM(spawnXM, chassisYM, slopeRad);
            if (pen <= 0f)
            {
                break;
            }

            var lift = pen + SpawnClearanceM;
            chassisYM += lift;
            wheelYM += lift;
        }

        var cos = MathF.Cos(slopeRad);
        var sin = MathF.Sin(slopeRad);

        var cbd = new BodyDef();
        cbd.Position.Set(spawnXM, chassisYM);
        cbd.Angle = slopeRad;
        _chassis = _world.CreateBody(cbd);
        var box = new PolygonDef
        {
            Density = 0.9f,
            Friction = 0.2f,
            Restitution = 0.0f,
        };
        box.SetAsBox(ChassisHalfW, ChassisHalfH);
        box.Filter.GroupIndex = -1;
        _chassis.CreateFixture(box);
        _chassis.SetMassFromShapes();
        _chassis.SetLinearDamping(0.02f);
        _chassis.SetAngularDamping(0.8f);

        // 轮心 = 底盘中心 + 坡度旋转的局部偏移 (∓WheelOffsetX, -ChassisHalfH)。
        _wheelBack = CreateWheel(
            spawnXM - (WheelOffsetX * cos) + (ChassisHalfH * sin),
            chassisYM - (WheelOffsetX * sin) - (ChassisHalfH * cos));
        _wheelFront = CreateWheel(
            spawnXM + (WheelOffsetX * cos) + (ChassisHalfH * sin),
            chassisYM + (WheelOffsetX * sin) - (ChassisHalfH * cos));
        // Hard axle: revolute pin + rotational motor (no vertical spring).
        _motorBack = CreateWheelMotor(_chassis, _wheelBack);
        _motorFront = CreateWheelMotor(_chassis, _wheelFront);
    }

    private Body CreateWheel(float x, float y)
    {
        var bd = new BodyDef();
        bd.Position.Set(x, y);
        var wheel = _world!.CreateBody(bd);
        var circle = new CircleDef
        {
            Radius = WheelRadius,
            Density = 1.1f,
            Friction = 1.3f,
            Restitution = 0.0f,
        };
        circle.Filter.GroupIndex = -1;
        wheel.CreateFixture(circle);
        wheel.SetMassFromShapes();
        wheel.SetLinearDamping(0.01f);
        wheel.SetAngularDamping(0.12f);
        return wheel;
    }

    private RevoluteJoint CreateWheelMotor(Body chassis, Body wheel)
    {
        var jd = new RevoluteJointDef();
        jd.Initialize(chassis, wheel, wheel.GetWorldCenter());
        jd.CollideConnected = false;
        jd.EnableMotor = true;
        jd.MaxMotorTorque = CoastMotorTorque;
        jd.MotorSpeed = 0f;
        return (RevoluteJoint)_world!.CreateJoint(jd);
    }

    private void ApplyDrive(float dt)
    {
        if (_dead || _chassis is null)
        {
            _pedal = 0f;
            SetWheelMotors(0f, 0f, CoastMotorTorque, CoastMotorTorque);
            return;
        }

        // 按住 W：油门随时长增大到 +1；按住 S：反向增大到 -1；
        // 松开（或同按视为空挡）：油门自动缓慢回落到 0，车速随之滑行衰减。
        if (_throttleKey && !_brakeKey)
        {
            _pedal = System.Math.Clamp(_pedal + (ThrottleRampPerSec * dt), -1f, 1f);
        }
        else if (_brakeKey && !_throttleKey)
        {
            _pedal = System.Math.Clamp(_pedal - (ThrottleRampPerSec * dt), -1f, 1f);
        }
        else
        {
            var decay = PedalDecayPerSec * dt;
            _pedal = MathF.Abs(_pedal) <= decay ? 0f : _pedal - (MathF.Sign(_pedal) * decay);
        }

        _chassis.WakeUp();
        _wheelBack?.WakeUp();
        _wheelFront?.WakeUp();

        var mag = MathF.Abs(_pedal);
        if (mag < 0.02f)
        {
            SetWheelMotors(0f, 0f, CoastMotorTorque, CoastMotorTorque);
            return;
        }

        // Box2D Y-up: negative wheel ω → +X forward; pedal>0 forward, pedal<0 reverse.
        var omega = -_pedal * DriveMotorSpeed;
        var torque = CoastMotorTorque + ((DriveMotorTorque - CoastMotorTorque) * mag);
        SetWheelMotors(omega, omega * FrontDriveBlend, torque, torque * FrontDriveBlend);
    }

    private void SetWheelMotors(float rearOmega, float frontOmega, float rearMaxTorque, float frontMaxTorque)
    {
        if (_motorBack is not null)
        {
            _motorBack.EnableMotor(true);
            _motorBack.SetMaxMotorTorque(rearMaxTorque);
            _motorBack.MotorSpeed = rearOmega;
        }

        if (_motorFront is not null)
        {
            _motorFront.EnableMotor(true);
            _motorFront.SetMaxMotorTorque(frontMaxTorque);
            _motorFront.MotorSpeed = frontOmega;
        }
    }

    private void UpdateScrollRate(int shiftPx)
    {
        var now = Environment.TickCount64;
        var dtSec = _lastScrollSampleMs > 0
            ? System.Math.Clamp((now - _lastScrollSampleMs) / 1000.0, 1.0 / 240.0, 0.5)
            : FixedDt;
        _lastScrollSampleMs = now;

        if (shiftPx <= 0)
        {
            _scrollPxPerSec += (0f - _scrollPxPerSec) * (ScrollSmooth * 0.5f);
            return;
        }

        var samplePxPerSec = (float)(shiftPx / dtSec);
        samplePxPerSec = System.Math.Clamp(samplePxPerSec, 0f, MaxScrollShiftPx / FixedDt);
        _scrollPxPerSec += (samplePxPerSec - _scrollPxPerSec) * ScrollSmooth;
    }

    /// <summary>外部喂入预测的下次更新时刻（QPC）。Δ 取最近一次实测跳变量。</summary>
    public void SetPredictedUpdate(TimeSpan nextUpdateTicks)
    {
        _nextUpdateTime = nextUpdateTicks;
        _pendingJumpPx = _lastJumpPx;
        _hasPrediction = true;
    }

    /// <summary>清除预测（无有效周期学习时）。</summary>
    public void ClearPrediction()
    {
        _hasPrediction = false;
        _jumpApplied = false;
    }

    /// <summary>每帧检查：预测时刻到达则预偏移，超时未入账则放弃。</summary>
    private void UpdateJumpPrediction()
    {
        if (!_hasPrediction)
        {
            return;
        }

        var now = QpcNow();
        if (!_jumpApplied && now >= _nextUpdateTime - JumpLeadWindow)
        {
            _jumpApplied = true;
        }
        else if (_jumpApplied && now > _nextUpdateTime + JumpTimeoutWindow)
        {
            // 预测超时未入账：取消偏移，避免车持续错位。
            _jumpApplied = false;
            _hasPrediction = false;
        }
    }

    private void ResetJumpPrediction()
    {
        _hasPrediction = false;
        _jumpApplied = false;
        _lastJumpPx = 0;
        _pendingJumpPx = 0;
    }

    private static TimeSpan QpcNow()
        => TimeSpan.FromTicks(Stopwatch.GetTimestamp() * TimeSpan.TicksPerSecond / Stopwatch.Frequency);

    private static int EstimateScrollShiftPx(float[]? previous, float[]? next, int previousPlotW)
    {
        if (previous is null
            || next is null
            || previous.Length < 16
            || next.Length < 16
            || previousPlotW < 16
            || System.Math.Abs(previous.Length - next.Length) > 8)
        {
            return 0;
        }

        var maxShift = System.Math.Min(MaxScrollShiftPx, System.Math.Min(previous.Length, next.Length) / 5);
        var bestShift = 0;
        var bestErr = float.MaxValue;
        var baselineErr = float.MaxValue;

        for (var s = 0; s <= maxShift; s++)
        {
            double err = 0;
            var n = 0;
            var limit = System.Math.Min(next.Length, previous.Length - s);
            for (var i = 0; i < limit; i += 2)
            {
                var d = next[i] - previous[i + s];
                err += d * d;
                n++;
            }

            if (n == 0)
            {
                continue;
            }

            var mean = (float)(err / n);
            if (s == 0)
            {
                baselineErr = mean;
            }

            if (mean < bestErr)
            {
                bestErr = mean;
                bestShift = s;
            }
        }

        if (bestShift == 0 || bestErr > baselineErr * 0.88f)
        {
            return 0;
        }

        return bestShift;
    }

    /// <summary>
    /// 翻车状态判定：底盘角度超过 90° 视为翻倒，仅用于提示（HUD 文案 + 车身变色），
    /// 不禁用油门（油门逻辑不再依赖 _controlsDisabled）。
    /// </summary>
    private void UpdateFlipState()
    {
        if (_chassis is null)
        {
            return;
        }

        var angle = _chassis.GetAngle();
        while (angle > MathF.PI)
        {
            angle -= MathF.Tau;
        }

        while (angle < -MathF.PI)
        {
            angle += MathF.Tau;
        }

        // 滞回：90° 触发翻车，60° 才解除——角度小幅摆动（85°~95° 抖动）不会重置回正计时。
        var flipped = _flipSinceMs > 0
            ? System.Math.Abs(angle) > FlipRecoverClearRad
            : System.Math.Abs(angle) > FlipAngleRad;
        _controlsDisabled = flipped;
        // 翻车计时：翻倒即开始计回正惩罚，回正（低于解除角）后清零。
        if (flipped)
        {
            if (_flipSinceMs == 0)
            {
                _flipSinceMs = Environment.TickCount64;
            }
        }
        else
        {
            _flipSinceMs = 0;
        }
    }

    /// <summary>翻车回正就绪：翻倒持续超过惩罚时间（供 HUD/输入侧查询）。</summary>
    public bool FlipRecoverReady
        => _flipSinceMs > 0 && Environment.TickCount64 - _flipSinceMs >= FlipRecoverDelayMs;

    /// <summary>
    /// 翻车回正：就绪后原地重生（与掉出底部同一逻辑），保留成绩。
    /// 用当前视口 X 作为重生点。
    /// </summary>
    public void TryFlipRecover()
    {
        if (!FlipRecoverReady)
        {
            return;
        }

        if (_chassis is not null)
        {
            var pos = _chassis.GetPosition();
            _respawnViewXPx = (pos.X * PixelsPerMeter) - _scrollOriginPx;
        }

        RespawnVehicle();
    }

    /// <summary>
    /// 底部传送重生：车掉出底部（物理 bug 卡穿地面线）或翻车回正时，回到掉落/翻车处上方，
    /// 保留本局距离/成绩——掉下去不是玩家失误，不应判失败。
    /// 重生位置 = 记录时的视口 X（可为负，clamp 到左失败线内；未记录则 22%）。
    /// </summary>
    private void RespawnVehicle()
    {
        if (_world is null || _worldXPx.Count < 2 || _plotWPx < 16)
        {
            return;
        }

        // 原地重生：用记录位置（可为负=左边界外，clamp 到失败线内）；未记录则 22%。
        var spawnViewXPx = !float.IsNaN(_respawnViewXPx)
            ? System.Math.Clamp(_respawnViewXPx, -LeftFailPx, _plotWPx)
            : _plotWPx * 0.22f;
        _respawnViewXPx = float.NaN;

        SpawnVehicleAt(spawnViewXPx);
        _pedal = 0;
        _dead = false;
        _controlsDisabled = false;
        _stepAccumulator = 0;
        _lastScrollSampleMs = Environment.TickCount64;
        _scrollPxPerSec = 0;
        ResetJumpPrediction();
        _flipSinceMs = 0;
    }

    /// <summary>
    /// 整车轮廓关键点（旋转到坡度姿态后）相对地形的最大穿入深度（&gt;0 表示穿入）。
    /// 采样：两轮最低点、底盘下沿两端与中点。
    /// </summary>
    private float MaxAssemblyPenetrationM(float centerX, float centerY, float angle)
    {
        var cos = MathF.Cos(angle);
        var sin = MathF.Sin(angle);
        var maxPen = 0f;

        // 轮心局部 (∓WheelOffsetX, -ChassisHalfH)，轮底再沿车身下方向量偏移。
        CheckLocal(-WheelOffsetX, -ChassisHalfH - WheelRadius);
        CheckLocal(WheelOffsetX, -ChassisHalfH - WheelRadius);
        // 底盘下沿两端 + 中点。
        CheckLocal(-ChassisHalfW, -ChassisHalfH);
        CheckLocal(ChassisHalfW, -ChassisHalfH);
        CheckLocal(0f, -ChassisHalfH);

        return maxPen;

        void CheckLocal(float lx, float ly)
        {
            var wx = centerX + (lx * cos) - (ly * sin);
            var wy = centerY + (lx * sin) + (ly * cos);
            var pen = SampleSurfaceM(wx) - wy;
            if (pen > maxPen)
            {
                maxPen = pen;
            }
        }
    }

    /// <summary>世界坐标（plot px）下采样地形表面高度（px，向上为正）。</summary>
    private float SampleSurfaceYPx(float worldXPx)
    {
        if (_worldXPx.Count == 0)
        {
            return _plotHPx * 0.2f;
        }

        if (worldXPx <= _worldXPx[0])
        {
            return _worldYPx[0];
        }

        if (worldXPx >= _worldXPx[^1])
        {
            return _worldYPx[^1];
        }

        for (var i = 0; i < _worldXPx.Count - 1; i++)
        {
            var x0 = _worldXPx[i];
            var x1 = _worldXPx[i + 1];
            if (worldXPx > x1)
            {
                continue;
            }

            var t = (x1 - x0) < 1e-3f ? 0f : (worldXPx - x0) / (x1 - x0);
            return _worldYPx[i] * (1 - t) + _worldYPx[i + 1] * t;
        }

        return _worldYPx[^1];
    }

    private float SampleSurfaceM(float worldXM)
    {
        if (_worldXPx.Count == 0)
        {
            return _plotHM * 0.2f;
        }

        return SampleSurfaceYPx(worldXM * PixelsPerMeter) / PixelsPerMeter;
    }

    private void DestroyVehicle()
    {
        if (_world is null)
        {
            _chassis = null;
            _wheelBack = null;
            _wheelFront = null;
            _motorBack = null;
            _motorFront = null;
            return;
        }

        DestroyJoint(ref _motorBack);
        DestroyJoint(ref _motorFront);

        if (_wheelBack is not null)
        {
            _world.DestroyBody(_wheelBack);
            _wheelBack = null;
        }

        if (_wheelFront is not null)
        {
            _world.DestroyBody(_wheelFront);
            _wheelFront = null;
        }

        if (_chassis is not null)
        {
            _world.DestroyBody(_chassis);
            _chassis = null;
        }
    }

    private void DestroyJoint<T>(ref T? joint) where T : Joint
    {
        if (joint is null || _world is null)
        {
            joint = null;
            return;
        }

        _world.DestroyJoint(joint);
        joint = null;
    }

    private void DestroyGround()
    {
        if (_world is null || _ground is null)
        {
            _ground = null;
            return;
        }

        _world.DestroyBody(_ground);
        _ground = null;
    }
}
