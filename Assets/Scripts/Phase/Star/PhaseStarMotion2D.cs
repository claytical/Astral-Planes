using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

[RequireComponent(typeof(Rigidbody2D))]
[DisallowMultipleComponent]
public sealed class PhaseStarMotion2D : MonoBehaviour
{
    // ============================================================
    // Public API / hooks
    // ============================================================
    public event Action<Vector2> OnVelocityChanged;
    private PhaseStar _star;
    /// <summary>Optional provider for vehicle world positions (for avoidance).</summary>
    public Func<IReadOnlyList<Vector2>> VehiclePositionsProvider;

    /// <summary>
    /// Optional sampler for dust density (0..1). Used only when edge-seeking is enabled.
    /// </summary>
    public Func<Vector2, float> DustDensitySampler;

    // ============================================================
    // Inspector tuning
    // ============================================================
    [Serializable]
    private sealed class VehicleAvoidanceTuning
    {
        [Tooltip("Repulsion range (world units).")]
        public float radius = 3.0f;

        [Tooltip("Higher = sharper near players.")]
        public float exponent = 2.0f;

        [Tooltip("Weight of avoidance vs drift.")]
        public float strength = 1.0f;
    }

    [Serializable]
    private sealed class DustEdgeSeekingTuning
    {
        [Tooltip("Finite diff step (world units).")]
        public float sampleStep = 0.35f;

        [Range(0f, 1f)]
        [Tooltip("Target density band (0 empty .. 1 dust).")]
        public float band = 0.5f;

        [Tooltip("Weight of edge seeking vs drift.")]
        public float strength = 1.0f;

        [Tooltip("Ignore very flat gradients (not near an edge).")]
        public float gradMin = 0.02f;
    }

    [Serializable]
    private sealed class SteeringTuning
    {
        [Tooltip("Max velocity change per second during free drift.")]
        public float maxAccel = 4.0f;

        [Tooltip("Max velocity change per second while actively hunting a target. " +
                 "Should be significantly higher than maxAccel so the star commits hard.")]
        public float huntAccel = 18.0f;
    }

    [SerializeField, Min(0f)] private float pushDecayPerSecond = 5f;
    private Vector2 _pushVelocity = Vector2.zero;
    private float   _speedMul = 1f;

    public void SetSpeedMultiplier(float mul) => _speedMul = Mathf.Max(0f, mul);

    [Serializable]
    private sealed class ScreenBoundsTuning
    {
        public bool kinematicMode = false;

        [Range(0f, 1f)]
        public float edgeBounce = 0.9f;

        public float screenPadding = 0.5f;

        [Range(0.1f, 1f)]
        public float focusSpeedMul = 0.5f;
    }

    // NavigatorBlendTuning is kept for inspector serialization compat but is only
    // used during the fallback drift path (no hunt target). When the navigator has a
    // committed target, motion drives directly toward it — no blending.
    [Serializable]
    private sealed class NavigatorBlendTuning
    {
        [Tooltip("Weight of density/sniffer direction when falling back to drift (no hunt target active). " +
                 "Has no effect while a hunt target is locked — the star drives directly then.")]
        [Range(0f, 1f)]
        public float densityWeight = 0.6f;

        [Range(0f, 1f)]
        public float snifferWeight = 0.35f;
    }

    [Header("Motion Tuning")]
    [SerializeField] private ScreenBoundsTuning     bounds    = new();
    [SerializeField] private VehicleAvoidanceTuning avoidance = new();
    [SerializeField] private DustEdgeSeekingTuning  edgeSeek  = new();
    [SerializeField] private SteeringTuning         steering  = new();
    [SerializeField] private NavigatorBlendTuning   navBlend  = new();
    [SerializeField] private bool verbose = false;

    // ============================================================
    // State
    // ============================================================
    private Rigidbody2D _rb;
    private PhaseStarBehaviorProfile _profile;
    private bool _enabled;
    private bool _focus;
    private Vector2 _driftDir;
    private float _rechooseTimer;
    private Camera _cam;

    private PhaseStarCravingNavigator _navigator;

    // ============================================================
    // Unity
    // ============================================================
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
        if (DustDensitySampler == null)
        {
            DustDensitySampler = pos =>
            {
                var gfm  = GameFlowManager.Instance;
                var dust = gfm ? gfm.dustGenerator : null;
                return dust == null ? 0f : dust.SampleDensity01(pos);
            };
        }

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

        _rb.linearVelocity = Vector2.zero;
        _rb.angularVelocity = 0f;
        if (on)
            KeepInsideScreenAndBounce();

        _rb.constraints = on
            ? RigidbodyConstraints2D.FreezeRotation
            : RigidbodyConstraints2D.FreezeAll;
    }

    private void FixedUpdate()
    {
        if (!_enabled || _profile == null || !_rb) return;

        float dt = Time.fixedDeltaTime;

        float hunger = (_star != null) ? _star.GetHungerLevel() : 0f;
        float speed  = Mathf.Lerp(
            Mathf.Max(0f, _profile.starDriftSpeed),
            Mathf.Max(0f, _profile.starHungrySpeed),
            hunger
        ) * _speedMul;
        float jitter = Mathf.Max(0f, _profile.starDriftJitter) * _speedMul;
        if (_focus) speed *= bounds.focusSpeedMul;

        // ---------------------------------------------------------------
        // HUNT MODE: navigator has a committed target — drive directly at it.
        // Avoidance still applies so the star doesn't clip through vehicles,
        // but drift, jitter, and blend weights are bypassed entirely.
        // ---------------------------------------------------------------
        Vector2 huntDir = _navigator != null ? _navigator.GetDensitySteerDir() : Vector2.zero;
        bool isHunting  = huntDir.sqrMagnitude > 0.0001f;

        if (isHunting)
        {
            Vector2 avoid      = ComputeVehicleAvoidance(_rb.position);
            Vector2 desiredDir = huntDir;

            // Allow avoidance to deflect but not fully override —
            // the star should push past vehicles to reach its target.
            if (avoid.sqrMagnitude > 0.0001f)
                desiredDir = (huntDir + avoid * avoidance.strength * 0.4f).normalized;

            Vector2 edgeRepulsion = ComputeEdgeRepulsion(_rb.position);
            if (edgeRepulsion.sqrMagnitude > 0.0001f)
                desiredDir = (desiredDir + edgeRepulsion).normalized;

            Vector2 desiredVel = desiredDir * speed;
            Vector2 newVel     = Vector2.MoveTowards(
                _rb.linearVelocity, desiredVel, steering.huntAccel * dt);

            if (_pushVelocity.sqrMagnitude > 0.0001f)
            {
                newVel       += _pushVelocity;
                _pushVelocity = Vector2.MoveTowards(
                    _pushVelocity, Vector2.zero, pushDecayPerSecond * dt);
            }

            if (bounds.kinematicMode)
                _rb.MovePosition(_rb.position + newVel * dt);
            else
                _rb.linearVelocity = newVel;

            KeepInsideScreenAndBounce();
            OnVelocityChanged?.Invoke(bounds.kinematicMode ? newVel : _rb.linearVelocity);
            return; // skip drift path entirely
        }

        // ---------------------------------------------------------------
        // DRIFT MODE: no hunt target — original weighted-blend behavior.
        // Used when the star is disarmed, all dust is gray, or briefly
        // between targets while the next BFS fires.
        // ---------------------------------------------------------------
        _rechooseTimer -= dt;
        if (_rechooseTimer <= 0f) PickNewDriftDir();

        Vector2 j     = Random.insideUnitCircle * jitter;
        Vector2 drift = _driftDir + j;
        if (drift.sqrMagnitude > 0.0001f) drift.Normalize();

        Vector2 avoidDrift = ComputeVehicleAvoidance(_rb.position);

        Vector2 avoidTangent = (avoidDrift.sqrMagnitude > 0.0001f)
            ? Vector2.Perpendicular(avoidDrift).normalized * Mathf.Clamp01(_profile.orbitBias)
            : Vector2.zero;

        // Fallback nav blend (sniffer directions only — no committed target)
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

        Vector2 desiredDirDrift = baseDrift
                                  + avoidDrift    * avoidance.strength
                                  + avoidTangent;

        if (desiredDirDrift.sqrMagnitude < 0.0001f) desiredDirDrift = drift;
        else desiredDirDrift.Normalize();

        Vector2 edgeRepulsionDrift = ComputeEdgeRepulsion(_rb.position);
        desiredDirDrift += edgeRepulsionDrift;
        if (desiredDirDrift.sqrMagnitude > 0.0001f) desiredDirDrift.Normalize();

        Vector2 desiredVelDrift = desiredDirDrift * speed;
        Vector2 curVel          = _rb.linearVelocity;
        Vector2 newVelDrift     = Vector2.MoveTowards(curVel, desiredVelDrift, steering.maxAccel * dt);

        if (_pushVelocity.sqrMagnitude > 0.0001f)
        {
            newVelDrift   += _pushVelocity;
            _pushVelocity  = Vector2.MoveTowards(
                _pushVelocity, Vector2.zero, pushDecayPerSecond * dt);
        }

        if (bounds.kinematicMode)
            _rb.MovePosition(_rb.position + newVelDrift * dt);
        else
            _rb.linearVelocity = newVelDrift;

        KeepInsideScreenAndBounce();
        OnVelocityChanged?.Invoke(bounds.kinematicMode ? newVelDrift : _rb.linearVelocity);

        if (_profile.teleportChancePerSec > 0f &&
            Random.value < _profile.teleportChancePerSec * dt)
        {
            Vector2 off = Random.insideUnitCircle * 1.5f;
            _rb.position += off;
            OnVelocityChanged?.Invoke(bounds.kinematicMode ? newVelDrift : _rb.linearVelocity);
        }
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

    private void KeepInsideScreenAndBounce()
    {
        if (_cam == null) _cam = Camera.main;
        if (_cam == null) return;

        var    pos = _rb.position;
        const float z = 0f;

        var min = (Vector2)_cam.ViewportToWorldPoint(new Vector3(0f, 0f, z)) + Vector2.one * bounds.screenPadding;
        var max = (Vector2)_cam.ViewportToWorldPoint(new Vector3(1f, 1f, z)) - Vector2.one * bounds.screenPadding;

        bool hit = false;
        if      (pos.x < min.x) { pos.x = min.x; _driftDir.x =  Mathf.Abs(_driftDir.x) * bounds.edgeBounce; hit = true; }
        else if (pos.x > max.x) { pos.x = max.x; _driftDir.x = -Mathf.Abs(_driftDir.x) * bounds.edgeBounce; hit = true; }

        if      (pos.y < min.y) { pos.y = min.y; _driftDir.y =  Mathf.Abs(_driftDir.y) * bounds.edgeBounce; hit = true; }
        else if (pos.y > max.y) { pos.y = max.y; _driftDir.y = -Mathf.Abs(_driftDir.y) * bounds.edgeBounce; hit = true; }

        if (!hit) return;
        _driftDir.Normalize();

        Vector2 vel = _rb.linearVelocity;
        if (pos.x <= min.x && vel.x < 0f) vel.x = 0f;
        if (pos.x >= max.x && vel.x > 0f) vel.x = 0f;
        if (pos.y <= min.y && vel.y < 0f) vel.y = 0f;
        if (pos.y >= max.y && vel.y > 0f) vel.y = 0f;
        _rb.linearVelocity = vel;

        if (bounds.kinematicMode) _rb.position  = pos;
        else                       _rb.MovePosition(pos);
    }

    private Vector2 ComputeVehicleAvoidance(Vector2 starPos)
    {
        var provider = VehiclePositionsProvider;
        if (provider == null) return Vector2.zero;

        var vehicles = provider();
        if (vehicles == null || vehicles.Count == 0) return Vector2.zero;

        float R = Mathf.Max(0.01f, avoidance.radius);
        float p = Mathf.Max(0.1f,  avoidance.exponent);

        Vector2 sum = Vector2.zero;
        for (int i = 0; i < vehicles.Count; i++)
        {
            Vector2 d = starPos - vehicles[i];
            float   r = d.magnitude;
            if (r <= 0.0001f || r >= R) continue;

            Vector2 dir = d / r;
            float   t   = Mathf.Clamp01(1f - (r / R));
            sum += dir * Mathf.Pow(t, p);
        }

        return sum;
    }

    private void PickNewDriftDir()
    {
        Vector2 rnd  = Random.insideUnitCircle.normalized;
        float   bias = Mathf.Clamp01(_profile != null ? _profile.orbitBias : 0f);
        _driftDir      = Vector2.Lerp(rnd, Vector2.Perpendicular(rnd).normalized, bias).normalized;
        _rechooseTimer = Random.Range(1.2f, 2.4f);
    }
}
