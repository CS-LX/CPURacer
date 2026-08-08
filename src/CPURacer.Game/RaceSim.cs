using System.Collections.Generic;
using Box2DX.Collision;
using Box2DX.Common;
using Box2DX.Dynamics;
using CPURacer.Capture;
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
    private const float ChassisHalfH = 0.18f;
    private const float WheelRadius = 0.22f;
    private const float WheelOffsetX = 0.42f;

    // Pedal ∈ [-1,1]: W ramps up, S ramps down (through 0 into reverse).
    private const float ThrottleRampPerSec = 1.35f;
    // Revolute motor at |pedal|=1 (rad/s, N·m). Negative ω → +X (forward).
    private const float DriveMotorSpeed = 28f;
    private const float DriveMotorTorque = 95f;
    private const float CoastMotorTorque = 0.5f;
    private const float FrontDriveBlend = 0.85f;

    private const float FlipAngleRad = 1.35f;
    private const int MaxTerrainSegments = 120;
    private const float WorldMarginScreens = 0.35f;
    private const int MaxScrollShiftPx = 24;
    private const float ScrollSmooth = 0.25f;

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

    private bool _throttleKey;
    private bool _brakeKey;
    /// <summary>Continuous pedal in [-1, 1]. +1 full forward, -1 full reverse.</summary>
    private float _pedal;
    private bool _dead;
    private bool _controlsDisabled;
    private float _spawnWorldXPx;
    private float _maxWorldXPx;
    private float _runDistanceM;
    private float _sessionBestM;
    private string _deathReason = "";

    public bool IsRunning { get; private set; }

    public bool IsDead => _dead;

    /// <summary>Smoothed scroll of the viewport through world space (plot px/s).</summary>
    public float ScrollPxPerSec => _scrollPxPerSec;

    public float DistanceMeters => _runDistanceM;

    public float BestDistanceMeters => _sessionBestM;

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
        _pedal = 0;
        _dead = false;
        _controlsDisabled = false;
        IsRunning = true;
        _lastScrollSampleMs = Environment.TickCount64;
        _scrollPxPerSec = 0;
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
        _dead = false;
        _controlsDisabled = false;
        _stepAccumulator = 0;
        _scrollPxPerSec = 0;
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
        _pedal = 0;
        _dead = false;
        _controlsDisabled = false;
        IsRunning = true;
        _lastScrollSampleMs = Environment.TickCount64;
        _scrollPxPerSec = 0;
    }

    private void ResetRunStats()
    {
        _spawnWorldXPx = _scrollOriginPx + (_plotWPx * 0.22f);
        _maxWorldXPx = _spawnWorldXPx;
        _runDistanceM = 0;
        _deathReason = "";
    }

    /// <summary>W = ramp pedal forward, S = ramp pedal backward (through idle into reverse).</summary>
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
            _lastScrollSampleMs = Environment.TickCount64;
            return;
        }

        var shiftPx = EstimateScrollShiftPx(previousCamera, _cameraYPx, previousPlotW);
        UpdateScrollRate(shiftPx);

        if (shiftPx > 0)
        {
            _scrollOriginPx += shiftPx;
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
        UpdateDistance(_chassis.GetPosition());
        if (IsOutOfView(_chassis.GetPosition()))
        {
            _dead = true;
            IsRunning = false;
            _deathReason = "驶出赛道";
            if (_runDistanceM > _sessionBestM)
            {
                _sessionBestM = _runDistanceM;
            }

            SetWheelMotors(0f, 0f, 0f, 0f);
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
        var chassisXPx = _insetLeft + (worldXPx - _scrollOriginPx) + 0.5f;
        var worldYPx = p.Y * PixelsPerMeter;
        var yFromTop = CoordMapper.WorldYToFrameYFromTop(worldYPx, _insetTop, _plotHPx);
        var hud = _dead
            ? $"Game Over · {_deathReason} · {_runDistanceM:0.0}m (best {_sessionBestM:0.0}m) — Space"
            : _controlsDisabled
                ? $"Flipped · {_runDistanceM:0.0}m — Space"
                : $"{_runDistanceM:0.0}m  best {_sessionBestM:0.0}m";

        // TaskmgrPlayer ColorEdge RGB(12,125,187) as BGRA accent defaults.
        var ab = _terrain?.AccentB ?? (byte)187;
        var ag = _terrain?.AccentG ?? (byte)125;
        var ar = _terrain?.AccentR ?? (byte)12;
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
            hud: hud);
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

    private void SpawnVehicle()
    {
        if (_world is null || _worldXPx.Count < 2 || _plotWPx < 16)
        {
            return;
        }

        DestroyVehicle();

        var spawnXPx = _scrollOriginPx + (_plotWPx * 0.22f);
        var spawnXM = spawnXPx / PixelsPerMeter;
        var surfaceYM = SampleSurfaceM(spawnXM);
        // Snug hard-axle spawn: wheel sits on surface; chassis hangs ChassisHalfH above hub.
        var wheelYM = surfaceYM + WheelRadius + 0.004f;
        var chassisYM = wheelYM + ChassisHalfH;

        var cbd = new BodyDef();
        cbd.Position.Set(spawnXM, chassisYM);
        cbd.Angle = 0;
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

        _wheelBack = CreateWheel(spawnXM - WheelOffsetX, wheelYM);
        _wheelFront = CreateWheel(spawnXM + WheelOffsetX, wheelYM);
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
            Friction = 1.8f,
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
        if (_controlsDisabled || _dead || _chassis is null)
        {
            _pedal = 0f;
            SetWheelMotors(0f, 0f, CoastMotorTorque, CoastMotorTorque);
            return;
        }

        // Hold W: pedal → +1; hold S: pedal → -1; neither: keep current (无级保持).
        if (_throttleKey && !_brakeKey)
        {
            _pedal = System.Math.Clamp(_pedal + (ThrottleRampPerSec * dt), -1f, 1f);
        }
        else if (_brakeKey && !_throttleKey)
        {
            _pedal = System.Math.Clamp(_pedal - (ThrottleRampPerSec * dt), -1f, 1f);
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
        samplePxPerSec = System.Math.Clamp(samplePxPerSec, 0f, MaxScrollShiftPx / (float)FixedDt);
        _scrollPxPerSec += (samplePxPerSec - _scrollPxPerSec) * ScrollSmooth;
    }

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

        _controlsDisabled = System.Math.Abs(angle) > FlipAngleRad;
    }

    private bool IsOutOfView(Vec2 centerM)
    {
        const float marginPx = 12f;
        var viewXPx = (centerM.X * PixelsPerMeter) - _scrollOriginPx;
        var yPx = centerM.Y * PixelsPerMeter;
        return viewXPx < -marginPx
               || viewXPx > _plotWPx + marginPx
               || yPx < -marginPx
               || yPx > _plotHPx + marginPx * 4;
    }

    private float SampleSurfaceM(float worldXM)
    {
        if (_worldXPx.Count == 0)
        {
            return _plotHM * 0.2f;
        }

        var xPx = worldXM * PixelsPerMeter;
        if (xPx <= _worldXPx[0])
        {
            return _worldYPx[0] / PixelsPerMeter;
        }

        if (xPx >= _worldXPx[^1])
        {
            return _worldYPx[^1] / PixelsPerMeter;
        }

        // Buffer is sorted by world X; linear scan is fine for ~1–2 screens.
        for (var i = 0; i < _worldXPx.Count - 1; i++)
        {
            var x0 = _worldXPx[i];
            var x1 = _worldXPx[i + 1];
            if (xPx > x1)
            {
                continue;
            }

            var t = (x1 - x0) < 1e-3f ? 0f : (xPx - x0) / (x1 - x0);
            var y = _worldYPx[i] * (1 - t) + _worldYPx[i + 1] * t;
            return y / PixelsPerMeter;
        }

        return _worldYPx[^1] / PixelsPerMeter;
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
