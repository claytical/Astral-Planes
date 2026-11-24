using System;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

[RequireComponent(typeof(Rigidbody2D))]
[DisallowMultipleComponent]
public sealed class PhaseStarMotion2D : MonoBehaviour
{
    public event Action<Vector2> OnVelocityChanged;

    private PhaseStar _star;

    public Func<IReadOnlyList<Vector2>> VehiclePositionsProvider;

    [Tooltip("Shared PhaseStar tuning (Resources/Config/PhaseStarConfig). Unassigned = that asset is loaded.")]
    [SerializeField] private PhaseStarConfig config;
    private PhaseStarConfig _cfg;
    private PhaseStarConfig Cfg => _cfg ??= PhaseStarConfig.OrDefault(config, this);
    // Shared tuning (PhaseStarConfig → 3. Motion). Dormant stars are ghosts: ships pass through,
    // get buffeted, and scatter the star elsewhere (ghost.*).
    private PhaseStarConfig.ScreenBoundsTuning bounds => Cfg.bounds;
    private PhaseStarConfig.VehicleAvoidanceTuning avoidance => Cfg.avoidance;
    private PhaseStarConfig.SteeringTuning steering => Cfg.steering;
    private PhaseStarConfig.NavigatorBlendTuning navBlend => Cfg.navBlend;
    private PhaseStarConfig.GhostDriftTuning ghost => Cfg.ghost;
    private float pushDecayPerSecond => Cfg.pushDecayPerSecond;
    private float frozenSettleDeceleration => Cfg.frozenSettleDeceleration;
    private bool enableUnstickImpulse => Cfg.enableUnstickImpulse;
    private float stuckThresholdSeconds => Cfg.stuckThresholdSeconds;
    private float unstickImpulseSpeed => Cfg.unstickImpulseSpeed;
    private float hardClampOvershootWorld => Cfg.hardClampOvershootWorld;
    private float softEdgeVelocityDamp => Cfg.softEdgeVelocityDamp;

    private Vector2 _pushVelocity = Vector2.zero;
    private float _speedMul = 1f;
    private float _stuckTimer;

    private bool _frozen;

    // Ghost drift: requested by PhaseStar while dormant; _ghostCollisionsIgnored tracks whether
    // vehicle<->star collisions are currently ignored (stays on until no ship overlaps, so the
    // star never turns solid inside a hull).
    private bool _ghostRequested;
    private bool _ghostCollisionsIgnored;
    private Collider2D[] _starColliders;
    private readonly List<Collider2D> _ghostIgnoredVehicleCols = new();
    private readonly List<Collider2D> _vehicleColScratch = new();

    public void SetGhostDrift(bool on) => _ghostRequested = on;

    private Vector2? _overrideTarget;
    public void SetOverrideTarget(Vector2? target) { _overrideTarget = target; }

    public void SetSpeedMultiplier(float mul) => _speedMul = Mathf.Max(0f, mul);

    public void SetFrozen(bool frozen)
    {
        if (_frozen == frozen) return;

        _frozen = frozen;
        _stuckTimer = 0f;
        _pushVelocity = Vector2.zero;

        if (frozen && _rb)
        {
            _rb.linearVelocity = Vector2.zero;
            _rb.angularVelocity = 0f;
            // Frozen = no self-steering, NOT immovable: a position-locked (FreezeAll) star acts as
            // an infinite-mass wall, and a ship overlapping or wedged against one gets pinned in
            // place (X-agong stuck-on-dormant-star bug). FixedUpdate settles shoves back to rest.
            _rb.constraints = RigidbodyConstraints2D.FreezeRotation;
        }
        else if (!frozen && _enabled && _rb)
        {
            _rb.constraints = RigidbodyConstraints2D.FreezeRotation;
        }
    }

    private GameFlowManager _gfm;
    private Rigidbody2D _rb;
    private PhaseStarBehaviorProfile _profile;
    private bool _enabled;
    private Vector2 _driftDir;
    private float _rechooseTimer;
    private Camera _cam;

    private PhaseStarCravingNavigator _navigator;
    private readonly List<Vector2> _vehiclePositionsScratch = new();

    private void Awake()
    {
        _rb = GetComponent<Rigidbody2D>();
        _rb.gravityScale = 0f;
        _rb.constraints = RigidbodyConstraints2D.FreezeRotation;

        _cam = Camera.main;
        _driftDir = Random.insideUnitCircle.normalized;
        _rechooseTimer = 0f;

        ApplyBodyType();
    }

    private void ApplyBodyType()
    {
        if (!_rb) return;
        _rb.bodyType = bounds.kinematicMode ? RigidbodyType2D.Kinematic : RigidbodyType2D.Dynamic;
        _rb.linearVelocity = Vector2.zero;
        _rb.angularVelocity = 0f;
        _rb.constraints = RigidbodyConstraints2D.FreezeRotation;
    }

    public void ApplyPushImpulse(Vector2 velocity)
    {
        _pushVelocity = velocity;
    }

    public void SetCravingNavigator(PhaseStarCravingNavigator navigator)
    {
        _navigator = navigator;
    }

    // Maze-aware navigation (StarNavigationParams on the motif's MazePatternConfig). The
    // profile is the star's personality; these stack on top for the current maze.
    private StarNavigationParams _nav = StarNavigationParams.Default;
    private PhaseStarNavMode _navMode = PhaseStarNavMode.Direct;
    private MazeNavGeometry _geom;

    private float EffDriftSpeed  => Mathf.Max(0f, _profile.starDriftSpeed)  * _nav.speedMultiplier;
    private float EffHungrySpeed => Mathf.Max(0f, _profile.starHungrySpeed) * _nav.hungrySpeedMultiplier;
    private float EffJitter      => Mathf.Max(0f, _profile.starDriftJitter) * _nav.jitterMultiplier;
    private float EffOrbitBias   => Mathf.Clamp01((_profile != null ? _profile.orbitBias : 0f) + _nav.orbitBiasOffset);

    // Pathing modes steer around walls, so a brief scrape on a corner is expected; always arm
    // the unstick nudge for them regardless of the prefab's enableUnstickImpulse.
    private bool UnstickEnabled => enableUnstickImpulse || StarNavigationParams.UsesPathing(_navMode);

    public void Initialize(
        PhaseStarBehaviorProfile profile,
        PhaseStar star,
        StarNavigationParams nav = null,
        PhaseStarNavMode navMode = PhaseStarNavMode.Direct,
        MazeNavGeometry geom = default)
    {
        _profile = profile;
        _enabled = true;
        _star = star;
        _nav = nav ?? StarNavigationParams.Default;
        _navMode = navMode == PhaseStarNavMode.Auto ? PhaseStarNavMode.Direct : navMode;
        _geom = geom;

        VehiclePositionsProvider ??= DefaultVehiclePositionsProvider;

        ApplyBodyType();
        Enable(true);
    }

    // Reuses _vehiclePositionsScratch instead of allocating a new List every call — this
    // runs every FixedUpdate via ComputeVehicleAvoidance, once per active star (up to 4
    // with all roles active).
    private IReadOnlyList<Vector2> DefaultVehiclePositionsProvider()
    {
        _vehiclePositionsScratch.Clear();
        if (_gfm == null) _gfm = GameFlowManager.Instance;
        if (_gfm == null || _gfm.localPlayers == null) return _vehiclePositionsScratch;

        for (int i = 0; i < _gfm.localPlayers.Count; i++)
        {
            var lp = _gfm.localPlayers[i];
            if (lp == null) continue;
            var v = lp.plane;
            if (v == null) continue;
            _vehiclePositionsScratch.Add((Vector2)v.transform.position);
        }
        return _vehiclePositionsScratch;
    }

    public void Enable(bool on)
    {
        _enabled = on;
        if (!_rb) return;

        _pushVelocity = Vector2.zero;
        _stuckTimer = 0f;

        if (!on)
        {
            _rb.linearVelocity = Vector2.zero;
            _rb.angularVelocity = 0f;
            _rb.constraints = RigidbodyConstraints2D.FreezeAll;
            return;
        }

        SoftContainInsideScreen();
        _rb.constraints = RigidbodyConstraints2D.FreezeRotation; // frozen stays pushable — see SetFrozen
    }

    private void FixedUpdate()
    {
        if (!_rb) return;
        SyncGhostCollisions();
        if (!_enabled || _profile == null) return;

        if (_frozen && _ghostRequested)
        {
            TickGhostDrift(Time.fixedDeltaTime);
            return;
        }

        if (_frozen)
        {
            // Let collisions shove it, then glide back to rest instead of snapping to zero.
            _rb.linearVelocity = Vector2.MoveTowards(_rb.linearVelocity, Vector2.zero,
                frozenSettleDeceleration * Time.fixedDeltaTime);
            return;
        }

        float dt = Time.fixedDeltaTime;
        float hunger = (_star != null) ? _star.GetHungerLevel() : 0f;
        float speed = Mathf.Lerp(EffDriftSpeed, EffHungrySpeed, hunger) * _speedMul;

        float jitter = EffJitter * _speedMul;

        Vector2 huntDir = _navigator != null ? _navigator.GetDensitySteerDir() : Vector2.zero;
        bool isHunting = huntDir.sqrMagnitude > 0.0001f;

        if (isHunting)
        {
            Vector2 desiredDir = ResolveDesiredHuntDirection(huntDir, _rb.position);
            Vector2 desiredVel = desiredDir * speed;
            Vector2 newVel = Vector2.MoveTowards(_rb.linearVelocity, desiredVel, steering.huntAccel * dt);
            newVel = ApplyPushVelocity(newVel, dt);
            ApplyVelocity(newVel, dt);
            HandleUnstickImpulse(huntDir, speed, dt);
            return;
        }

        Vector2 desiredDirDrift = ResolveDesiredDriftDirection(_rb.position, jitter);
        Vector2 desiredVelDrift = desiredDirDrift * speed;
        Vector2 curVel = _rb.linearVelocity;
        float driftAccel = (curVel.sqrMagnitude > speed * speed * 1.5f)
            ? steering.huntAccel
            : steering.maxAccel;

        Vector2 newVelDrift = Vector2.MoveTowards(curVel, desiredVelDrift, driftAccel * dt);
        newVelDrift = ApplyPushVelocity(newVelDrift, dt);
        ApplyVelocity(newVelDrift, dt);
        _stuckTimer = 0f;
    }

    private Vector2 ResolveDesiredHuntDirection(Vector2 huntDir, Vector2 currentPos)
    {
        Vector2 avoid = ComputeVehicleAvoidance(currentPos);
        Vector2 desiredDir = ShapeForMaze(huntDir, currentPos);

        if (avoid.sqrMagnitude > 0.0001f)
            desiredDir = (huntDir + avoid * avoidance.strength * avoidance.huntWeight).normalized;

        Vector2 edgeRepulsion = ComputeEdgeRepulsion(currentPos);
        if (edgeRepulsion.sqrMagnitude > 0.0001f)
            desiredDir = (desiredDir + edgeRepulsion).normalized;

        return desiredDir;
    }

    private Vector2 ResolveDesiredDriftDirection(Vector2 currentPos, float jitter)
    {
        if (_overrideTarget.HasValue)
        {
            Vector2 toTarget = _overrideTarget.Value - currentPos;
            if (toTarget.sqrMagnitude < 0.5f * 0.5f)
            {
                _overrideTarget = null;
            }
            else
            {
                _driftDir = toTarget.normalized;
                _rechooseTimer = 1f;
            }
        }

        _rechooseTimer -= Time.fixedDeltaTime;
        if (_rechooseTimer <= 0f)
            PickNewDriftDir();

        Vector2 j = Random.insideUnitCircle * jitter;
        Vector2 drift = _driftDir + j;
        if (drift.sqrMagnitude > 0.0001f) drift.Normalize();

        Vector2 avoidDrift = ComputeVehicleAvoidance(currentPos);
        Vector2 avoidTangent = (avoidDrift.sqrMagnitude > 0.0001f)
            ? Vector2.Perpendicular(avoidDrift).normalized * EffOrbitBias
            : Vector2.zero;

        Vector2 navContrib = Vector2.zero;
        if (_navigator != null)
        {
            Vector2 snifferDir = _navigator.GetDominantSnifferDir();
            if (snifferDir.sqrMagnitude > 0.0001f)
                navContrib += snifferDir * navBlend.snifferWeight;
        }

        Vector2 baseDrift = navContrib.sqrMagnitude > 0.0001f
            ? navContrib.normalized
            : drift;
        baseDrift = ShapeForMaze(baseDrift, currentPos);

        Vector2 desiredDirDrift = baseDrift + avoidDrift * avoidance.strength + avoidTangent;

        if (desiredDirDrift.sqrMagnitude < 0.0001f)
            desiredDirDrift = drift;
        else
            desiredDirDrift.Normalize();

        Vector2 edgeRepulsionDrift = ComputeEdgeRepulsion(currentPos);
        desiredDirDrift += edgeRepulsionDrift;
        if (desiredDirDrift.sqrMagnitude > 0.0001f)
            desiredDirDrift.Normalize();

        return desiredDirDrift;
    }

    // Per-maze direction shaping (before vehicle avoidance / edge repulsion).
    //   LaneGlide: snap toward whichever lane axis is closest to the desired heading.
    //   RingOrbit: add a swirl around the ring centre, faded out when the heading is radial
    //              (i.e. the path is threading a gap between rings).
    private Vector2 ShapeForMaze(Vector2 dir, Vector2 pos)
    {
        if (dir.sqrMagnitude < 0.0001f) return dir;

        switch (_navMode)
        {
            case PhaseStarNavMode.LaneGlide when _geom.HasLaneAxis:
            {
                Vector2 axis = _geom.LaneAxis;
                if (_geom.HasSecondLaneAxis &&
                    Mathf.Abs(Vector2.Dot(dir, _geom.LaneAxis2)) > Mathf.Abs(Vector2.Dot(dir, axis)))
                    axis = _geom.LaneAxis2;

                float sign = Vector2.Dot(dir, axis) >= 0f ? 1f : -1f;
                Vector2 shaped = Vector2.Lerp(dir, axis * sign, _nav.laneAlignStrength);
                return shaped.sqrMagnitude > 0.0001f ? shaped.normalized : dir;
            }

            case PhaseStarNavMode.RingOrbit when _geom.HasRingCentre:
            {
                Vector2 radial = pos - _geom.RingCentre;
                if (radial.sqrMagnitude < 0.0001f) return dir;
                radial.Normalize();

                Vector2 tangent = Vector2.Perpendicular(radial);
                float sign = Vector2.Dot(dir, tangent) >= 0f ? 1f : -1f;
                float tangentialness = 1f - Mathf.Abs(Vector2.Dot(dir, radial));
                Vector2 shaped = dir + tangent * sign * _nav.ringTangentialStrength * tangentialness;
                return shaped.sqrMagnitude > 0.0001f ? shaped.normalized : dir;
            }

            default:
                return dir;
        }
    }

    private Vector2 ApplyPushVelocity(Vector2 baseVelocity, float dt)
    {
        if (_pushVelocity.sqrMagnitude <= 0.0001f)
            return baseVelocity;

        Vector2 result = baseVelocity + _pushVelocity;
        _pushVelocity = Vector2.MoveTowards(_pushVelocity, Vector2.zero, pushDecayPerSecond * dt);
        return result;
    }

    private void ApplyVelocity(Vector2 velocity, float dt)
    {
        if (bounds.kinematicMode)
            _rb.MovePosition(_rb.position + velocity * dt);
        else
            _rb.linearVelocity = velocity;

        SoftContainInsideScreen();
        OnVelocityChanged?.Invoke(bounds.kinematicMode ? velocity : _rb.linearVelocity);
    }

    private void HandleUnstickImpulse(Vector2 huntDir, float speed, float dt)
    {
        if (!UnstickEnabled || bounds.kinematicMode || unstickImpulseSpeed <= 0.001f)
        {
            _stuckTimer = 0f;
            return;
        }

        bool isStuck = _rb.linearVelocity.sqrMagnitude < (speed * 0.12f) * (speed * 0.12f);
        if (!isStuck)
        {
            _stuckTimer = 0f;
            return;
        }

        _stuckTimer += dt;
        if (_stuckTimer < stuckThresholdSeconds)
            return;

        _stuckTimer = 0f;
        Vector2 perp = Vector2.Perpendicular(huntDir);
        if (Random.value > 0.5f) perp = -perp;
        Vector2 nudge = (perp * 0.75f + huntDir * 0.25f).normalized * unstickImpulseSpeed;
        ApplyPushImpulse(nudge);
    }

    private Vector2 ComputeEdgeRepulsion(Vector2 pos)
    {
        if (_cam == null) _cam = Camera.main;
        if (_cam == null) return Vector2.zero;

        // Unzoomed gameplay frame — a presentation zoom must not squeeze stars (GameplayFrame).
        var min = (Vector2)GameplayFrame.ViewportToWorld(Vector2.zero);
        var max = (Vector2)GameplayFrame.ViewportToWorld(Vector2.one);

        float margin = bounds.screenPadding + 1.5f;
        Vector2 push = Vector2.zero;

        float dL = pos.x - min.x;
        if (dL < margin) push.x += Mathf.Pow(1f - Mathf.Clamp01(dL / margin), 2f);

        float dR = max.x - pos.x;
        if (dR < margin) push.x -= Mathf.Pow(1f - Mathf.Clamp01(dR / margin), 2f);

        float dB = pos.y - min.y;
        if (dB < margin) push.y += Mathf.Pow(1f - Mathf.Clamp01(dB / margin), 2f);

        float dT = max.y - pos.y;
        if (dT < margin) push.y -= Mathf.Pow(1f - Mathf.Clamp01(dT / margin), 2f);

        return push * 3.0f;
    }

    private void SoftContainInsideScreen()
    {
        if (_cam == null) _cam = Camera.main;
        if (_cam == null || _rb == null) return;

        Vector2 min = (Vector2)GameplayFrame.ViewportToWorld(Vector2.zero) + Vector2.one * bounds.screenPadding;
        Vector2 max = (Vector2)GameplayFrame.ViewportToWorld(Vector2.one) - Vector2.one * bounds.screenPadding;

        Vector2 pos = _rb.position;

        if (bounds.kinematicMode)
        {
            bool changed = false;

            if (pos.x < min.x) { pos.x = min.x; changed = true; }
            else if (pos.x > max.x) { pos.x = max.x; changed = true; }

            if (pos.y < min.y) { pos.y = min.y; changed = true; }
            else if (pos.y > max.y) { pos.y = max.y; changed = true; }

            if (changed) _rb.position = pos;
            return;
        }

        Vector2 vel = _rb.linearVelocity;

        if (pos.x < min.x)
        {
            vel.x = Mathf.Max(vel.x, 0f);
            if (min.x - pos.x > hardClampOvershootWorld)
                pos.x = min.x;
        }
        else if (pos.x > max.x)
        {
            vel.x = Mathf.Min(vel.x, 0f);
            if (pos.x - max.x > hardClampOvershootWorld)
                pos.x = max.x;
        }

        if (pos.y < min.y)
        {
            vel.y = Mathf.Max(vel.y, 0f);
            if (min.y - pos.y > hardClampOvershootWorld)
                pos.y = min.y;
        }
        else if (pos.y > max.y)
        {
            vel.y = Mathf.Min(vel.y, 0f);
            if (pos.y - max.y > hardClampOvershootWorld)
                pos.y = max.y;
        }

        vel = Vector2.MoveTowards(_rb.linearVelocity, vel, softEdgeVelocityDamp * Time.fixedDeltaTime);
        _rb.linearVelocity = vel;
        _rb.position = pos;
    }

    private Vector2 ComputeVehicleAvoidance(Vector2 starPos)
    {
        var provider = VehiclePositionsProvider;
        if (provider == null) return Vector2.zero;

        var vehicles = provider();
        if (vehicles == null || vehicles.Count == 0) return Vector2.zero;

        float R = Mathf.Max(0.01f, avoidance.radius);
        float p = Mathf.Max(0.1f, avoidance.exponent);

        Vector2 sum = Vector2.zero;
        for (int i = 0; i < vehicles.Count; i++)
        {
            Vector2 d = starPos - vehicles[i];
            float r = d.magnitude;
            if (r <= 0.0001f || r >= R) continue;

            Vector2 dir = d / r;
            float t = Mathf.Clamp01(1f - (r / R));
            sum += dir * Mathf.Pow(t, p);
        }

        return sum;
    }

    // ---------------- Dormant ghost drift ----------------

    private bool GhostWanted => _ghostRequested && _frozen && _enabled;

    private void SyncGhostCollisions()
    {
        if (GhostWanted)
        {
            // Re-applied every tick: picks up ships that joined/respawned, and Unity can drop a
            // pair's ignore state when either collider is disabled/re-enabled (the star toggles
            // its colliders on state changes). A handful of pairs — cheap.
            SetVehicleCollisionsIgnored(true);
            return;
        }

        if (!_ghostCollisionsIgnored) return;

        // Leaving ghost mode: don't go solid while a ship is inside the star — keep scattering
        // away until clear, then restore collisions.
        if (AnyVehicleOverlapping(out var v))
        {
            ScatterFrom(v);
            return;
        }
        SetVehicleCollisionsIgnored(false);
    }

    private void TickGhostDrift(float dt)
    {
        // Ships passing through: buffet them and fling the star somewhere else.
        CollectVehicleColliders();
        for (int i = 0; i < _vehicleColScratch.Count; i++)
        {
            var vc = _vehicleColScratch[i];
            if (!vc || !vc.enabled || vc.isTrigger) continue;
            if (!OverlapsStar(vc)) continue;
            var vehicle = vc.GetComponentInParent<Vehicle>();
            if (vehicle == null) continue;
            vehicle.ApplyTurbulence(ghost.turbulence01, ghost.turbulenceSeconds);
            ScatterFrom(vehicle);
        }

        // Slow wander; anything faster (a scatter) bleeds off at the settle rate.
        _rechooseTimer -= dt;
        if (_rechooseTimer <= 0f) PickNewDriftDir();

        Vector2 dir = _driftDir + ComputeEdgeRepulsion(_rb.position);
        if (dir.sqrMagnitude > 0.0001f) dir.Normalize();
        Vector2 target = dir * Mathf.Max(0f, ghost.driftSpeed);

        Vector2 cur = _rb.linearVelocity;
        float accel = cur.sqrMagnitude > target.sqrMagnitude * 2.25f + 0.01f
            ? frozenSettleDeceleration
            : ghost.driftAccel;
        _rb.linearVelocity = Vector2.MoveTowards(cur, target, accel * dt);
        SoftContainInsideScreen();
        OnVelocityChanged?.Invoke(_rb.linearVelocity);
    }

    private void ScatterFrom(Vehicle vehicle)
    {
        if (vehicle == null || vehicle.rb == null) return;
        Vector2 away = _rb.position - vehicle.rb.position;
        away = away.sqrMagnitude > 0.0001f ? away.normalized : Random.insideUnitCircle.normalized;

        Vector2 shipVel = vehicle.rb.linearVelocity;
        Vector2 dir = shipVel.sqrMagnitude > 0.01f
            ? Vector2.Lerp(away, shipVel.normalized, ghost.scatterFollowsShip)
            : away;
        if (dir.sqrMagnitude < 0.0001f) dir = away;
        dir.Normalize();

        Vector2 scatter = dir * Mathf.Max(ghost.scatterSpeed, shipVel.magnitude * 0.5f);
        if (_rb.linearVelocity.sqrMagnitude < scatter.sqrMagnitude)
            _rb.linearVelocity = scatter;
        _driftDir = dir; // keep wandering the way it was knocked
        _rechooseTimer = Random.Range(1.2f, 2.4f);
    }

    private void CacheStarColliders()
    {
        if (_starColliders == null || _starColliders.Length == 0)
            _starColliders = GetComponentsInChildren<Collider2D>(true);
    }

    private void CollectVehicleColliders()
    {
        _vehicleColScratch.Clear();
        if (_gfm == null) _gfm = GameFlowManager.Instance;
        if (_gfm == null || _gfm.localPlayers == null) return;
        for (int i = 0; i < _gfm.localPlayers.Count; i++)
        {
            var lp = _gfm.localPlayers[i];
            var v = lp != null ? lp.plane : null;
            if (v == null) continue;
            v.GetComponentsInChildren(true, _tmpCols);
            _vehicleColScratch.AddRange(_tmpCols);
        }
    }
    private readonly List<Collider2D> _tmpCols = new();

    private void SetVehicleCollisionsIgnored(bool ignore)
    {
        CacheStarColliders();
        if (ignore)
        {
            CollectVehicleColliders();
            for (int i = 0; i < _vehicleColScratch.Count; i++)
            {
                var vc = _vehicleColScratch[i];
                if (!vc) continue;
                for (int s = 0; s < _starColliders.Length; s++)
                    if (_starColliders[s]) Physics2D.IgnoreCollision(_starColliders[s], vc, true);
                if (!_ghostIgnoredVehicleCols.Contains(vc)) _ghostIgnoredVehicleCols.Add(vc);
            }
            _ghostCollisionsIgnored = true;
            return;
        }

        for (int i = 0; i < _ghostIgnoredVehicleCols.Count; i++)
        {
            var vc = _ghostIgnoredVehicleCols[i];
            if (!vc) continue;
            for (int s = 0; s < _starColliders.Length; s++)
                if (_starColliders[s]) Physics2D.IgnoreCollision(_starColliders[s], vc, false);
        }
        _ghostIgnoredVehicleCols.Clear();
        _ghostCollisionsIgnored = false;
    }

    private bool OverlapsStar(Collider2D other)
    {
        CacheStarColliders();
        for (int s = 0; s < _starColliders.Length; s++)
        {
            var sc = _starColliders[s];
            if (!sc || !sc.enabled || sc.isTrigger) continue;
            // Geometric query — works even while the pair's collisions are ignored.
            if (sc.Distance(other).isOverlapped) return true;
        }
        return false;
    }

    private bool AnyVehicleOverlapping(out Vehicle vehicle)
    {
        vehicle = null;
        for (int i = 0; i < _ghostIgnoredVehicleCols.Count; i++)
        {
            var vc = _ghostIgnoredVehicleCols[i];
            if (!vc || !vc.enabled || vc.isTrigger) continue;
            if (!OverlapsStar(vc)) continue;
            vehicle = vc.GetComponentInParent<Vehicle>();
            if (vehicle != null) return true;
        }
        return false;
    }

    private void PickNewDriftDir()
    {
        Vector2 rnd = Random.insideUnitCircle.normalized;
        float bias = EffOrbitBias;
        _driftDir = Vector2.Lerp(rnd, Vector2.Perpendicular(rnd).normalized, bias).normalized;
        _rechooseTimer = Random.Range(1.2f, 2.4f);
    }

}
