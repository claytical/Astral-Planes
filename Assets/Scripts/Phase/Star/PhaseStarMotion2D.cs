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

    /// <summary>Optional provider for vehicle world positions (for avoidance).</summary>
    public Func<IReadOnlyList<Vector2>> VehiclePositionsProvider;

    /// <summary>
    /// Optional sampler for dust density (0..1). Used only when edge-seeking is enabled.
    /// If null, will default to GameFlowManager.Instance.dustGenerator.SampleDensity01(pos).
    /// </summary>
    public Func<Vector2, float> DustDensitySampler;

    /// <summary>
    /// Optional external path step (kinematic override). If provided, will move by returned position each FixedUpdate.
    /// Signature: (currentPos, dt) -> nextPos.
    /// </summary>
    public Func<Vector2, float, Vector2> ExternalPathStep;

    // ============================================================
    // Inspector tuning (collapsed)
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
        [Tooltip("Max change in velocity per second.")]
        public float maxAccel = 4.0f;
    }

    [Serializable]
    private sealed class ScreenBoundsTuning
    {
        [Tooltip("If true, drive star by MovePosition (kinematic). If false, drive via velocity (dynamic).")]
        public bool kinematicMode = false;

        [Tooltip("Reflect drift a bit at edges (0..1).")]
        [Range(0f, 1f)]
        public float edgeBounce = 0.9f;

        [Tooltip("Keep it slightly inside the frame (world units).")]
        public float screenPadding = 0.5f;

        [Tooltip("Slows during focus mode (0.1..1).")]
        [Range(0.1f, 1f)]
        public float focusSpeedMul = 0.5f;
    }

    [Header("Motion Tuning")]
    [SerializeField] private ScreenBoundsTuning bounds = new ScreenBoundsTuning();
    [SerializeField] private VehicleAvoidanceTuning avoidance = new VehicleAvoidanceTuning();
    [SerializeField] private DustEdgeSeekingTuning edgeSeek = new DustEdgeSeekingTuning();
    [SerializeField] private SteeringTuning steering = new SteeringTuning();
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

    public void SetFocusMode(bool on) => _focus = on;

    public void Initialize(PhaseStarBehaviorProfile profile, PhaseStar star)
    {
        _profile = profile;

        // Drive enable/disable from PhaseStar events
        star.OnArmed += s => Enable(true);
        star.OnDisarmed += s => Enable(false);

        // Default dust sampler
        if (DustDensitySampler == null)
        {
            DustDensitySampler = pos =>
            {
                var gfm = GameFlowManager.Instance;
                var dust = gfm ? gfm.dustGenerator : null;
                if (dust == null) return 0f;
                return dust.SampleDensity01(pos);
            };
        }

        // Default vehicle provider: read from GameFlowManager.localPlayers[].plane
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

        // Ensure body type matches tuning (if someone flipped it in inspector)
        ApplyBodyType();
    }

    public void Enable(bool on)
    {
        _enabled = on;
        if (!_rb) return;

        _rb.linearVelocity = Vector2.zero;
        _rb.angularVelocity = 0f;

        _rb.constraints = on
            ? RigidbodyConstraints2D.FreezeRotation
            : RigidbodyConstraints2D.FreezeAll;
    }

    private void FixedUpdate()
    {
        if (!_enabled || _profile == null || !_rb)
        {
            if (verbose)
            {
                if (!_enabled) Debug.Log("[PhaseStarMotion2D] disabled");
                if (_profile == null) Debug.Log("[PhaseStarMotion2D] no profile");
            }
            return;
        }

        float dt = Time.fixedDeltaTime;

        float speed = Mathf.Max(0f, _profile.starDriftSpeed);
        float jitter = Mathf.Max(0f, _profile.starDriftJitter);
        if (_focus) speed *= bounds.focusSpeedMul;

        _rechooseTimer -= dt;
        if (_rechooseTimer <= 0f) PickNewDriftDir();

        // --- Drift base ----------------------------------------------------
        Vector2 j = Random.insideUnitCircle * jitter;
        Vector2 drift = _driftDir + j;
        if (drift.sqrMagnitude > 0.0001f) drift.Normalize();

        // --- Vehicle avoidance --------------------------------------------
        Vector2 avoid = ComputeVehicleAvoidance(_rb.position);

        // --- Dust edge seeking --------------------------------------------
        Vector2 edge = ComputeDustEdgeSeek(_rb.position);

        // --- Personality: arc around avoidance via orbitBias --------------
        Vector2 avoidTangent = (avoid.sqrMagnitude > 0.0001f)
            ? Vector2.Perpendicular(avoid).normalized * Mathf.Clamp01(_profile.orbitBias)
            : Vector2.zero;

        // Weighted blend in direction space
        Vector2 desiredDir = drift + avoid * avoidance.strength + edge * edgeSeek.strength + avoidTangent;
        if (desiredDir.sqrMagnitude < 0.0001f) desiredDir = drift;
        else desiredDir.Normalize();

        Vector2 desiredVel = desiredDir * speed;
        Vector2 curVel = _rb.linearVelocity;
        Vector2 newVel = Vector2.MoveTowards(curVel, desiredVel, steering.maxAccel * dt);

        // Optional external override in kinematic mode
        if (bounds.kinematicMode && ExternalPathStep != null)
        {
            var next = ExternalPathStep(_rb.position, dt);
            _rb.MovePosition(next);
        }
        else if (bounds.kinematicMode)
        {
            _rb.MovePosition(_rb.position + newVel * dt);
        }
        else
        {
            _rb.linearVelocity = newVel;
        }

        KeepInsideScreenAndBounce();

        OnVelocityChanged?.Invoke(bounds.kinematicMode ? newVel : _rb.linearVelocity);

        // Teleport spice (Wildcard)
        if (_profile.teleportChancePerSec > 0f &&
            Random.value < _profile.teleportChancePerSec * dt)
        {
            Vector2 off = Random.insideUnitCircle * 1.5f;
            _rb.position += off;
            OnVelocityChanged?.Invoke(bounds.kinematicMode ? newVel : _rb.linearVelocity);
        }
    }

    private void KeepInsideScreenAndBounce()
    {
        if (_cam == null) _cam = Camera.main;
        if (_cam == null) return;

        var pos = _rb.position;
        float z = 0f;

        var min = (Vector2)_cam.ViewportToWorldPoint(new Vector3(0f, 0f, z)) + Vector2.one * bounds.screenPadding;
        var max = (Vector2)_cam.ViewportToWorldPoint(new Vector3(1f, 1f, z)) - Vector2.one * bounds.screenPadding;

        bool hit = false;
        if (pos.x < min.x) { pos.x = min.x; _driftDir.x = Mathf.Abs(_driftDir.x) * bounds.edgeBounce; hit = true; }
        else if (pos.x > max.x) { pos.x = max.x; _driftDir.x = -Mathf.Abs(_driftDir.x) * bounds.edgeBounce; hit = true; }

        if (pos.y < min.y) { pos.y = min.y; _driftDir.y = Mathf.Abs(_driftDir.y) * bounds.edgeBounce; hit = true; }
        else if (pos.y > max.y) { pos.y = max.y; _driftDir.y = -Mathf.Abs(_driftDir.y) * bounds.edgeBounce; hit = true; }

        if (!hit) return;

        _driftDir.Normalize();

        // Cheap corrector for dynamic too
        if (bounds.kinematicMode) _rb.position = pos;
        else _rb.MovePosition(pos);
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
            Vector2 vpos = vehicles[i];
            Vector2 d = starPos - vpos;
            float r = d.magnitude;
            if (r <= 0.0001f) continue;
            if (r >= R) continue;

            Vector2 dir = d / r;
            float t = Mathf.Clamp01(1f - (r / R));
            float w = Mathf.Pow(t, p);
            sum += dir * w;
        }

        return sum;
    }

    private Vector2 ComputeDustEdgeSeek(Vector2 starPos)
    {
        var sampler = DustDensitySampler;
        if (sampler == null) return Vector2.zero;

        float e = Mathf.Max(0.01f, edgeSeek.sampleStep);
        float c = sampler(starPos);

        float dx = sampler(starPos + Vector2.right * e) - sampler(starPos - Vector2.right * e);
        float dy = sampler(starPos + Vector2.up * e) - sampler(starPos - Vector2.up * e);

        Vector2 grad = new Vector2(dx, dy) / (2f * e);
        float gm = grad.magnitude;
        if (gm < edgeSeek.gradMin) return Vector2.zero;

        Vector2 normal = grad / gm; // points toward increasing dust density

        // Seek a band around edgeSeek.band: too dusty => push outward, too empty => push inward.
        float bandErr = Mathf.Clamp(c - edgeSeek.band, -1f, 1f);
        Vector2 correction = -normal * bandErr;

        // Weight by gradient magnitude so it prefers strong edges.
        return correction * gm;
    }

    private void PickNewDriftDir()
    {
        Vector2 rnd = Random.insideUnitCircle.normalized;
        float bias = Mathf.Clamp01(_profile.orbitBias);

        _driftDir = Vector2.Lerp(rnd, Vector2.Perpendicular(rnd).normalized, bias).normalized;
        _rechooseTimer = Random.Range(1.2f, 2.4f);
    }

    // ============================================================
    // Legacy / optional: target nudge (kept for compatibility)
    // ============================================================
    public void SetTarget(Vector3 target, float curiosity = 1f)
    {
        StartCoroutine(MoveTowardTarget(target, curiosity));
    }

    private IEnumerator MoveTowardTarget(Vector3 target, float curiosity)
    {
        while (true)
        {
            Vector2 dir = (target - transform.position).normalized;
            _rb.AddForce(dir * curiosity * 0.5f, ForceMode2D.Force);
            yield return new WaitForSeconds(0.25f);
            if (Vector2.Distance(transform.position, target) < 1f)
                break;
        }
    }
}
