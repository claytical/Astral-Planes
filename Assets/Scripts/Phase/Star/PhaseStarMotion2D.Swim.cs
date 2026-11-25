using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

// Charging-star personality (PhaseStarConfig → swim). While dormant the star swims like a
// jellyfish in time with the music: on its role's stroke step (DrumTrack.OnStepChanged) the
// tendrils pull in, the star pushes off with an eased thrust along a gradually turning heading,
// then glides with the tendrils trailing. It is curious about ships (tendrils + heading lean
// toward one nearby) and drifts smoothly away from one that comes close. New stars swim in from off-screen to their spawn point (the entrance).
// The tendril drawing (PhaseStarDustAffect.Buds.cs) reads the public read-outs below.
public sealed partial class PhaseStarMotion2D
{
    private enum StrokePhase { None, Contracting, Relaxing }

    private PhaseStarConfig.SwimTuning swim => Cfg.swim;

    private DrumTrack _swimDrum;
    private bool _pendingStroke;
    private StrokePhase _strokePhase;
    private float _strokeT;

    private float _contract01;
    private float _flick01;
    private float _shy01;
    private float _lean01;
    private Vector2 _leanDir = Vector2.up;

    private Vector2 _swimHeading = Vector2.up;
    private float _swimHeadingTimer;
    private Vector2 _moveHeading = Vector2.up;   // what thrust follows; turns gradually
    private Vector2 _desiredOpen = Vector2.up;   // wall-aware desired heading (re-probed ~0.3 s)
    private float _reprobeTimer;
    private float _thrustT = -1f;                // < 0 = no thrust in progress
    private float _thrustDv;
    private Vector2 _repelDir;
    private float _repelDirTimer;

    private bool _entering;
    private Vector2 _entranceTarget;
    private float _entranceTimer;

    /// <summary>0..1: fraction of tendril length pulled in right now (stroke wind-up × the role's
    /// contractAmount, or curling up as a ship comes close).</summary>
    public float TendrilPullIn01
    {
        get
        {
            var style = swim.For(SwimRole);
            float amount = style != null ? style.contractAmount : 0.45f;
            return Mathf.Max(_contract01 * amount, _shy01 * 0.6f);
        }
    }
    /// <summary>0..1: decaying flick on beats that aren't this role's stroke steps.</summary>
    public float BeatFlick01 => _flick01;
    /// <summary>0..1: how strongly the tendrils lean toward a nearby ship (ShipLeanDir).</summary>
    public float ShipLean01 => _lean01;
    public Vector2 ShipLeanDir => _leanDir;
    /// <summary>Direction the tendrils stream while gliding (opposite the velocity).</summary>
    public Vector2 TrailDir { get; private set; } = Vector2.down;
    /// <summary>0..1: how much of the trail to apply (glide speed × trailStrength).</summary>
    public float Trail01 { get; private set; }
    /// <summary>Swimming in from off-screen to the spawn point.</summary>
    public bool IsEntering => _entering;

    /// <summary>
    /// Begin an entrance: the star (already placed off-screen) swims to <paramref name="target"/>
    /// on its stroke rhythm. Screen containment is skipped until it arrives.
    /// </summary>
    public void BeginEntrance(Vector2 target)
    {
        _entering = true;
        // PhaseStar.Update re-asserts ghost mode each frame, but FixedUpdate can run first on the
        // spawn frame — without this the entrance would be cancelled and the star clamped on-screen.
        _ghostRequested = true;
        _entranceTarget = target;
        _entranceTimer = 0f;
        // The swim-in crosses the play-area boundary and maze walls: a kinematic body glides
        // through them instead of wedging against the first collider it meets.
        if (_rb) _rb.bodyType = RigidbodyType2D.Kinematic;
        _pendingStroke = true; // first stroke right away so it doesn't sit outside the frame
        _moveHeading = (target - (_rb ? _rb.position : (Vector2)transform.position)).normalized;
    }

    // Entrance over (arrived, timed out or cancelled): back to a normal body. A star that never
    // reached its cell is placed on it so it can't end up inside a wall or the UI band.
    private void EndEntrance(bool snapToTarget)
    {
        if (!_entering) return;
        _entering = false;
        _swimHeadingTimer = 0f;
        if (!_rb) return;
        if (snapToTarget) _rb.position = _entranceTarget;
        ApplyBodyType();
    }

    private float SwimCellWorld()
    {
        if (_gfm == null) _gfm = GameFlowManager.Instance;
        var drum = _gfm != null ? _gfm.activeDrumTrack : null;
        return drum != null ? Mathf.Max(0.01f, drum.GetCellWorldSize()) : 1f;
    }

    private MusicalRole SwimRole => _star != null && _star.AttunedRole != MusicalRole.None
        ? _star.AttunedRole
        : MusicalRole.Harmony;

    // ── Drum step subscription ─────────────────────────────────────────────────

    private void EnsureSwimDrumSubscription()
    {
        if (_gfm == null) _gfm = GameFlowManager.Instance;
        var drum = _gfm != null ? _gfm.activeDrumTrack : null;
        if (drum == _swimDrum) return;
        if (_swimDrum != null) _swimDrum.OnStepChanged -= OnSwimStep;
        _swimDrum = drum;
        if (_swimDrum != null) _swimDrum.OnStepChanged += OnSwimStep;
    }

    private void UnsubscribeSwimDrum()
    {
        if (_swimDrum != null) _swimDrum.OnStepChanged -= OnSwimStep;
        _swimDrum = null;
    }

    private void OnDisable() => UnsubscribeSwimDrum();
    private void OnDestroy() => UnsubscribeSwimDrum();

    private void OnSwimStep(int step, int leaderSteps)
    {
        if (!GhostWanted && !_entering) return;
        var style = swim.For(SwimRole);
        if (style == null) return;

        int n = Mathf.Max(1, style.stepsPerStroke);
        int rel = ((step - style.stepOffset) % n + n) % n;
        if (rel == 0) _pendingStroke = true;
        else if (step % 4 == 0) _flick01 = 1f; // hears the beat even between strokes
    }

    // ── Per-tick swim (called from TickGhostDrift) ──────────────────────────────

    private void TickSwim(float dt)
    {
        float cell = SwimCellWorld();
        var style = swim.For(SwimRole);
        Vector2 pos = _rb.position;
        Vector2 vel = _rb.linearVelocity;
        float drag = Mathf.Max(0.05f, style != null ? style.glideDrag : 1f);

        _flick01 = Mathf.MoveTowards(_flick01, 0f, dt / 0.25f);

        if (_entering)
        {
            _entranceTimer += dt;
            bool arrived = (_entranceTarget - pos).magnitude <= swim.entranceArriveCells * cell;
            if (arrived || _entranceTimer >= swim.entranceMaxSeconds)
            {
                EndEntrance(snapToTarget: !arrived);
                return; // ApplyBodyType zeroed the velocity; resume swimming next tick
            }
        }

        float repel01 = SenseShips(pos, cell, dt, out Vector2 awayFromShip);

        // Desired heading: toward the spawn point while entering; otherwise a slow wander kept
        // off the screen edges and bent toward a curious-range ship, then made wall-aware.
        _swimHeadingTimer -= dt;
        if (_swimHeadingTimer <= 0f) PickSwimHeading();

        Vector2 desired;
        if (_entering)
        {
            desired = _entranceTarget - pos;
        }
        else
        {
            desired = _swimHeading + ComputeEdgeRepulsion(pos);
            if (desired.sqrMagnitude > 0.0001f) desired.Normalize();
            if (_lean01 > 0f)
                desired = Vector2.Lerp(desired, _leanDir, swim.curiousHeadingBlend * _lean01);

            _reprobeTimer -= dt;
            if (_reprobeTimer <= 0f || _pendingStroke)
            {
                _reprobeTimer = 0.3f;
                _desiredOpen = ChooseOpenHeading(pos, desired, cell, out _);
            }
            desired = _desiredOpen;
        }
        if (desired.sqrMagnitude < 0.0001f) desired = _moveHeading;

        // Gradual turning: the body's heading swings toward the desired one at a capped rate.
        float turnDeg = swim.turnDegreesPerSecond * (_entering ? 3f : 1f) * dt;
        _moveHeading = RotateToward(_moveHeading, desired.normalized, turnDeg);

        // Stroke cycle: contract → eased thrust → relax.
        if (_pendingStroke)
        {
            _pendingStroke = false;
            if (_strokePhase != StrokePhase.Contracting)
            {
                _strokePhase = StrokePhase.Contracting;
                _strokeT = 0f;
            }
        }

        float cs = Mathf.Max(0.02f, swim.contractSeconds);
        switch (_strokePhase)
        {
            case StrokePhase.Contracting:
                _strokeT += dt;
                _contract01 = Mathf.Max(_contract01, Mathf.SmoothStep(0f, 1f, _strokeT / cs));
                if (_strokeT >= cs)
                {
                    StartThrust(pos, style, cell, drag);
                    _strokePhase = StrokePhase.Relaxing;
                    _strokeT = 0f;
                }
                break;
            case StrokePhase.Relaxing:
                _strokeT += dt;
                float relax = Mathf.Max(cs * 3f, swim.thrustSeconds);
                _contract01 = 1f - Mathf.SmoothStep(0f, 1f, _strokeT / relax);
                if (_strokeT >= relax)
                {
                    _strokePhase = StrokePhase.None;
                    _contract01 = 0f;
                }
                break;
            default:
                _contract01 = Mathf.MoveTowards(_contract01, 0f, dt / cs);
                break;
        }

        vel = TickThrust(vel, pos, cell, dt);

        // Ship nearby: a smooth push away down an open direction (no darts).
        if (repel01 > 0f && !_entering)
        {
            _repelDirTimer -= dt;
            if (_repelDirTimer <= 0f)
            {
                _repelDirTimer = 0.25f;
                _repelDir = ChooseOpenHeading(pos, awayFromShip, cell, out float repelRoom);
                if (repelRoom < swim.minStrokeRoomCells * cell) _repelDir = Vector2.zero;
            }
            vel = AddCapped(vel, _repelDir * (swim.repelAccel * repel01 * dt), swim.maxSwimSpeed * 1.5f);
        }

        // Glide: everything bleeds off exponentially.
        vel *= Mathf.Exp(-drag * dt);
        _rb.linearVelocity = vel;

        // Smoothed so a bump off a wall doesn't flip the whole tendril trail in one frame.
        float speed = vel.magnitude;
        if (speed > 0.05f)
        {
            Vector2 trail = Vector2.Lerp(TrailDir, -vel / speed, 1f - Mathf.Exp(-6f * dt));
            if (trail.sqrMagnitude > 0.0001f) TrailDir = trail.normalized;
        }
        Trail01 = Mathf.Clamp01(speed / Mathf.Max(0.05f, swim.trailFullSpeed)) * swim.trailStrength;
    }

    // Decide this stroke's total speed gain; TickThrust spreads it over thrustSeconds.
    private void StartThrust(Vector2 pos, PhaseStarConfig.RoleSwimStyle style, float cell, float drag)
    {
        float dv = style != null ? style.strokeImpulse : 0.7f;
        if (_entering)
        {
            dv *= swim.entranceImpulseMul;
        }
        else
        {
            if (_star != null && _star.DustAffect != null && _star.DustAffect.HasActiveTentacles)
                dv *= swim.vineHoldImpulseMul; // holding on to a vine: barely moves

            // Only as hard as the room ahead allows (an exp-drag glide travels speed/drag), so the
            // star glides to a stop short of the wall. Boxed in → pulse in place.
            float room = OpenRoomAhead(pos, _moveHeading, cell);
            if (room < swim.minStrokeRoomCells * cell) { _thrustT = -1f; return; }
            dv = Mathf.Min(dv, room * drag * 0.7f);
            _swimHeading = _desiredOpen; // keep following the corridor it found
        }
        _thrustDv = dv;
        _thrustT = 0f; // a new stroke restarts the envelope rather than stacking
    }

    // Sine-envelope thrust: a(t) = dv·π/(2T)·sin(πt/T) integrates to exactly dv, easing in and out.
    private Vector2 TickThrust(Vector2 vel, Vector2 pos, float cell, float dt)
    {
        if (_thrustT < 0f) return vel;
        float T = Mathf.Max(0.05f, swim.thrustSeconds);
        _thrustT += dt;
        if (_thrustT >= T) { _thrustT = -1f; return vel; }

        if (!_entering && OpenRoomAhead(pos, _moveHeading, cell) < swim.minStrokeRoomCells * cell)
        {
            _thrustT = -1f; // turning into a wall: stop pushing, just glide
            return vel;
        }

        float a = _thrustDv * Mathf.PI / (2f * T) * Mathf.Sin(Mathf.PI * _thrustT / T);
        Vector2 add = _moveHeading * (a * dt);
        return _entering ? vel + add : AddCapped(vel, add, swim.maxSwimSpeed);
    }

    // Soft cap: thrust that would push past maxSpeed is dropped (never brakes a faster glide).
    private static Vector2 AddCapped(Vector2 vel, Vector2 add, float maxSpeed)
    {
        Vector2 next = vel + add;
        float before = vel.magnitude;
        float after = next.magnitude;
        if (after > maxSpeed && after > before)
            next = next.normalized * Mathf.Max(before, maxSpeed);
        return next;
    }

    private static Vector2 RotateToward(Vector2 from, Vector2 to, float maxDegrees)
    {
        if (from.sqrMagnitude < 0.0001f) return to;
        float a = Mathf.Atan2(from.y, from.x) * Mathf.Rad2Deg;
        float b = Mathf.Atan2(to.y, to.x) * Mathf.Rad2Deg;
        float r = Mathf.MoveTowardsAngle(a, b, maxDegrees) * Mathf.Deg2Rad;
        return new Vector2(Mathf.Cos(r), Mathf.Sin(r));
    }

    private float OpenRoomAhead(Vector2 pos, Vector2 dir, float cell)
    {
        if (_gfm == null) _gfm = GameFlowManager.Instance;
        var drum = _gfm != null ? _gfm.activeDrumTrack : null;
        float maxDist = Mathf.Max(1f, swim.strokeProbeCells) * cell;
        return drum != null ? OpenDistance(drum, pos, dir, cell, maxDist) : maxDist;
    }

    private static readonly Vector2[] kProbeDirs = BuildProbeDirs(16);

    private static Vector2[] BuildProbeDirs(int n)
    {
        var dirs = new Vector2[n];
        for (int i = 0; i < n; i++)
        {
            float a = i * Mathf.PI * 2f / n;
            dirs[i] = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
        }
        return dirs;
    }

    /// <summary>
    /// The probe direction that best matches <paramref name="desired"/> while having open room
    /// (no dust/blocks, inside the grid) ahead. <paramref name="room"/> = open distance (world).
    /// </summary>
    private Vector2 ChooseOpenHeading(Vector2 pos, Vector2 desired, float cell, out float room)
    {
        room = 0f;
        if (_gfm == null) _gfm = GameFlowManager.Instance;
        var drum = _gfm != null ? _gfm.activeDrumTrack : null;
        if (drum == null || desired.sqrMagnitude < 0.0001f)
        {
            room = swim.strokeProbeCells * cell;
            return desired.sqrMagnitude > 0.0001f ? desired.normalized : _swimHeading;
        }
        desired.Normalize();

        float maxDist = Mathf.Max(1f, swim.strokeProbeCells) * cell;
        float bestScore = float.NegativeInfinity;
        Vector2 best = desired;
        float bestRoom = 0f;
        for (int i = 0; i < kProbeDirs.Length; i++)
        {
            Vector2 d = kProbeDirs[i];
            float align = Vector2.Dot(d, desired);
            if (align < -0.2f) continue; // never reverse on a stroke; the next heading pick can
            float r = OpenDistance(drum, pos, d, cell, maxDist);
            if (r < swim.minStrokeRoomCells * cell) continue;
            float score = align + 0.6f * (r / maxDist);
            if (score > bestScore) { bestScore = score; best = d; bestRoom = r; }
        }
        room = bestRoom;
        return best;
    }

    // March half a cell at a time until dust, a block, or the grid edge.
    private static float OpenDistance(DrumTrack drum, Vector2 pos, Vector2 dir, float cell, float maxDist)
    {
        int w = drum.GetSpawnGridWidth(), h = drum.GetSpawnGridHeight();
        float stepLen = cell * 0.5f;
        Vector2Int startCell = drum.WorldToGridPosition(pos);
        for (float t = stepLen; t <= maxDist; t += stepLen)
        {
            Vector2Int c = drum.WorldToGridPosition(pos + dir * t);
            if (c == startCell) continue;
            if (c.x < 0 || c.y < 0 || c.x >= w || c.y >= h) return Mathf.Max(0f, t - stepLen);
            if (drum.HasDustAt(c)) return Mathf.Max(0f, t - cell * 0.75f); // stop half a body short
        }
        return maxDist;
    }

    private void PickSwimHeading()
    {
        Vector2 rnd = Random.insideUnitCircle;
        if (rnd.sqrMagnitude < 0.0001f) rnd = Vector2.up;
        rnd.Normalize();
        // Mostly keep going the same way — a jellyfish meanders, it doesn't zig-zag.
        _swimHeading = Vector2.Lerp(_swimHeading, rnd, 0.65f).normalized;
        if (_swimHeading.sqrMagnitude < 0.0001f) _swimHeading = rnd;
        _swimHeadingTimer = Random.Range(swim.headingRechooseSeconds.x, swim.headingRechooseSeconds.y);
    }

    // Nearest ship: tendrils lean toward it inside the curious radius; inside the repel radius
    // the star curls progressively and returns a 0..1 push-away strength ((1 − d/R)²).
    private float SenseShips(Vector2 pos, float cell, float dt, out Vector2 awayFromShip)
    {
        awayFromShip = Vector2.zero;
        float leanTarget = 0f, shyTarget = 0f, repel01 = 0f;

        var provider = VehiclePositionsProvider;
        var ships = !_entering && provider != null ? provider() : null;
        if (ships != null && ships.Count > 0)
        {
            float best = float.MaxValue;
            Vector2 toBest = Vector2.zero;
            for (int i = 0; i < ships.Count; i++)
            {
                Vector2 d = ships[i] - pos;
                float m = d.sqrMagnitude;
                if (m < best) { best = m; toBest = d; }
            }

            float dist = Mathf.Sqrt(best);
            float curiousR = swim.curiousRadiusCells * cell;
            float repelR = Mathf.Min(swim.repelRadiusCells * cell, curiousR);
            if (dist < curiousR)
            {
                Vector2 dir = dist > 0.0001f ? toBest / dist : Random.insideUnitCircle.normalized;
                awayFromShip = -dir;
                _leanDir = dir;
                if (dist < repelR)
                {
                    float close01 = 1f - dist / Mathf.Max(0.0001f, repelR);
                    shyTarget = close01;
                    repel01 = close01 * close01;
                }
                else
                {
                    leanTarget = Mathf.Clamp01((curiousR - dist) / Mathf.Max(0.0001f, curiousR - repelR));
                }
            }
        }

        _lean01 = Mathf.MoveTowards(_lean01, leanTarget, dt / 0.5f);
        _shy01  = Mathf.MoveTowards(_shy01, shyTarget, dt / 0.5f);
        return repel01;
    }

    // ── Soft bump with ships ────────────────────────────────────────────────────
    // Collisions stay ignored (ghost) so a ship can never be pinned, but neither passes through:
    // within a skin of contact the two are sprung apart. The star yields most; the ship loses
    // its closing speed and gets a gentle shove back.
    private readonly List<Collider2D> _bumpShipCols = new();

    private void TickSoftBump(float dt)
    {
        if (_entering) return; // swimming in over walls and UI; nothing to bump yet
        if (_gfm == null) _gfm = GameFlowManager.Instance;
        if (_gfm == null || _gfm.localPlayers == null) return;
        CacheStarColliders();

        float cell = SwimCellWorld();
        float skin = swim.bumpSkinCells * cell;
        Vector2 starPos = _rb.position;

        for (int p = 0; p < _gfm.localPlayers.Count; p++)
        {
            var lp = _gfm.localPlayers[p];
            var vehicle = lp != null ? lp.plane : null;
            if (vehicle == null || vehicle.rb == null) continue;

            // Closest gap between any star collider and any hull collider (negative = overlap).
            vehicle.GetComponentsInChildren(false, _bumpShipCols);
            float gap = float.MaxValue;
            for (int i = 0; i < _bumpShipCols.Count; i++)
            {
                var vc = _bumpShipCols[i];
                if (!vc || !vc.enabled || vc.isTrigger) continue;
                for (int k = 0; k < _starColliders.Length; k++)
                {
                    var sc = _starColliders[k];
                    if (!sc || !sc.enabled || sc.isTrigger) continue;
                    gap = Mathf.Min(gap, sc.Distance(vc).distance);
                }
            }
            float pen = skin - gap;
            if (gap == float.MaxValue || pen <= 0f) continue;
            float pen01 = pen / Mathf.Max(0.01f, cell);

            Vector2 shipVel = vehicle.rb.linearVelocity;
            Vector2 n = starPos - vehicle.rb.position;            // ship → star
            if (n.sqrMagnitude < 0.0001f)
                n = shipVel.sqrMagnitude > 0.0001f ? shipVel : Random.insideUnitCircle;
            n.Normalize();

            // Star: sprung away, and it keeps drifting that way.
            _rb.linearVelocity += n * (swim.bumpStarAccel * pen01 * dt);
            _swimHeading = n;
            _swimHeadingTimer = Random.Range(swim.headingRechooseSeconds.x, swim.headingRechooseSeconds.y);

            // Ship: closing speed soaked up, plus a soft shove back proportional to overlap.
            float closing = Vector2.Dot(shipVel - _rb.linearVelocity, n);
            if (closing > 0f)
                shipVel -= n * (closing * Mathf.Clamp01(swim.bumpShipDamping * dt));
            shipVel -= n * (swim.bumpShipAccel * pen01 * dt);
            vehicle.rb.linearVelocity = shipVel;

            if (ghost.turbulence01 > 0f)
                vehicle.ApplyTurbulence(ghost.turbulence01, ghost.turbulenceSeconds);
        }
    }

    // Outside swim mode the read-outs relax so tendrils never freeze mid-stroke.
    private void RelaxSwim(float dt)
    {
        _pendingStroke = false;
        _strokePhase = StrokePhase.None;
        _thrustT = -1f;
        _contract01 = Mathf.MoveTowards(_contract01, 0f, dt / 0.2f);
        _flick01 = Mathf.MoveTowards(_flick01, 0f, dt / 0.25f);
        _lean01 = Mathf.MoveTowards(_lean01, 0f, dt / 0.4f);
        _shy01 = Mathf.MoveTowards(_shy01, 0f, dt / 0.4f);
        Trail01 = Mathf.MoveTowards(Trail01, 0f, dt / 0.3f);
    }
}
