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

    [Serializable]
    private sealed class VehicleAvoidanceTuning
    {
        public float radius = 3.0f;
        public float exponent = 2.0f;
        public float strength = 1.0f;
    }


    [Serializable]
    private sealed class SteeringTuning
    {
        public float maxAccel = 4.0f;
        public float huntAccel = 18.0f;
    }

    [SerializeField, Min(0f)] private float pushDecayPerSecond = 5f;

    [Header("Anti-Jump")]
    [SerializeField] private bool enableUnstickImpulse = false;
    [SerializeField, Min(0.05f)] private float stuckThresholdSeconds = 0.22f;
    [SerializeField, Min(0f)] private float unstickImpulseSpeed = 6f;
    [SerializeField, Min(0f)] private float hardClampOvershootWorld = 1.0f;
    [SerializeField, Min(0f)] private float softEdgeVelocityDamp = 8f;

    private Vector2 _pushVelocity = Vector2.zero;
    private float _speedMul = 1f;
    private float _stuckTimer;

    private bool _frozen;

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
        }
    }

    [Serializable]
    private sealed class ScreenBoundsTuning
    {
        public bool kinematicMode = false;
        public float screenPadding = 0.5f;
    }

    [Serializable]
    private sealed class NavigatorBlendTuning
    {
        [Range(0f, 1f)] public float snifferWeight = 0.35f;
    }

    [Header("Motion Tuning")]
    [SerializeField] private ScreenBoundsTuning bounds = new();
    [SerializeField] private VehicleAvoidanceTuning avoidance = new();
    [SerializeField] private SteeringTuning steering = new();
    [SerializeField] private NavigatorBlendTuning navBlend = new();

    private Rigidbody2D _rb;
    private PhaseStarBehaviorProfile _profile;
    private bool _enabled;
    private Vector2 _driftDir;
    private float _rechooseTimer;
    private Camera _cam;

    private PhaseStarCravingNavigator _navigator;

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

    public void Initialize(PhaseStarBehaviorProfile profile, PhaseStar star)
    {
        _profile = profile;
        _enabled = true;
        _star = star;


        VehiclePositionsProvider ??= () =>
        {
            var gfm = GameFlowManager.Instance;
            if (gfm == null || gfm.localPlayers == null) return Array.Empty<Vector2>();

            var list = new List<Vector2>(gfm.localPlayers.Count);
            for (int i = 0; i < gfm.localPlayers.Count; i++)
            {
                var lp = gfm.localPlayers[i];
                if (lp == null) continue;
                var v = lp.plane;
                if (v == null) continue;
                list.Add((Vector2)v.transform.position);
            }
            return list;
        };

        ApplyBodyType();
        Enable(true);
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
        _rb.constraints = RigidbodyConstraints2D.FreezeRotation;
    }

    private void FixedUpdate()
    {
        if (!_enabled || _profile == null || !_rb) return;

        if (_frozen)
        {
            _rb.linearVelocity = Vector2.zero;
            return;
        }

        float dt = Time.fixedDeltaTime;
        float hunger = (_star != null) ? _star.GetHungerLevel() : 0f;
        float speed = Mathf.Lerp(
            Mathf.Max(0f, _profile.starDriftSpeed),
            Mathf.Max(0f, _profile.starHungrySpeed),
            hunger
        ) * _speedMul;

        float jitter = Mathf.Max(0f, _profile.starDriftJitter) * _speedMul;

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
        Vector2 desiredDir = huntDir;

        if (avoid.sqrMagnitude > 0.0001f)
            desiredDir = (huntDir + avoid * avoidance.strength * 0.4f).normalized;

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
            ? Vector2.Perpendicular(avoidDrift).normalized * Mathf.Clamp01(_profile.orbitBias)
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
        if (!enableUnstickImpulse || bounds.kinematicMode || unstickImpulseSpeed <= 0.001f)
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

        const float z = 0f;
        var min = (Vector2)_cam.ViewportToWorldPoint(new Vector3(0f, 0f, z));
        var max = (Vector2)_cam.ViewportToWorldPoint(new Vector3(1f, 1f, z));

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

        const float z = 0f;
        Vector2 min = (Vector2)_cam.ViewportToWorldPoint(new Vector3(0f, 0f, z)) + Vector2.one * bounds.screenPadding;
        Vector2 max = (Vector2)_cam.ViewportToWorldPoint(new Vector3(1f, 1f, z)) - Vector2.one * bounds.screenPadding;

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

    private void PickNewDriftDir()
    {
        Vector2 rnd = Random.insideUnitCircle.normalized;
        float bias = Mathf.Clamp01(_profile != null ? _profile.orbitBias : 0f);
        _driftDir = Vector2.Lerp(rnd, Vector2.Perpendicular(rnd).normalized, bias).normalized;
        _rechooseTimer = Random.Range(1.2f, 2.4f);
    }

    public void ClampToScreenTop(float topInset)
    {
        if (_cam == null) _cam = Camera.main;
        if (_cam == null || _rb == null) return;

        float topY = ((Vector2)_cam.ViewportToWorldPoint(new Vector3(0.5f, 1f, 0f))).y;
        var pos = _rb.position;
        if (pos.y > topY - topInset)
        {
            pos.y = topY - topInset;
            _rb.linearVelocity = new Vector2(_rb.linearVelocity.x, 0f);
            _rb.position = pos;
        }
    }
}