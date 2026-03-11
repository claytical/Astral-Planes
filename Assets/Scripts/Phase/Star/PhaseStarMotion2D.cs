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

    /// <summary>
    /// Optional external path step (kinematic override). If provided, moves by returned position each FixedUpdate.
    /// Signature: (currentPos, dt) -> nextPos.
    /// Not used in the density-navigator system (dynamic mode only), kept for compatibility.
    /// </summary>
    public Func<Vector2, float, Vector2> ExternalPathStep;

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

    [Serializable]
    private sealed class NavigatorBlendTuning
    {
        [Tooltip("Weight of density-spoke direction vs free drift. Higher = star hunts dust more aggressively.")]
        [Range(0f, 1f)]
        public float densityWeight = 0.6f;

        [Tooltip("Weight of dominant-shard sniffer direction vs free drift. Steers toward what the star is draining.")]
        [Range(0f, 1f)]
        public float snifferWeight = 0.35f;
    }

    [Header("Motion Tuning")]
    [SerializeField] private ScreenBoundsTuning    bounds    = new();
    [SerializeField] private VehicleAvoidanceTuning avoidance = new();
    [SerializeField] private DustEdgeSeekingTuning  edgeSeek  = new();
    [SerializeField] private SteeringTuning          steering  = new();
    [SerializeField] private NavigatorBlendTuning    navBlend  = new();
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

    /// <summary>
    /// Optional density navigator. When set, its density and sniffer directions
    /// are blended into steering each FixedUpdate.
    /// Set via SetCravingNavigator() from PhaseStar.Initialize.
    /// </summary>
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

    public void SetFocusMode(bool on) => _focus = on;

    /// <summary>
    /// Wire the density navigator so its directions are blended into steering each tick.
    /// Call from PhaseStar.Initialize after cravingNavigator.Initialize.
    /// </summary>
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
        {
            // Snap back inside screen before releasing constraints
            KeepInsideScreenAndBounce();
        }
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

        float dt    = Time.fixedDeltaTime;
      
        float hunger = (_star != null) ? _star.GetHungerLevel() : 0f;
        float speed  = Mathf.Lerp(
            Mathf.Max(0f, _profile.starDriftSpeed),
            Mathf.Max(0f, _profile.starHungrySpeed),
            hunger
        );        float jitter = Mathf.Max(0f, _profile.starDriftJitter);
        if (_focus) speed *= bounds.focusSpeedMul;

        _rechooseTimer -= dt;
        if (_rechooseTimer <= 0f) PickNewDriftDir();

        // --- Drift base ---
        Vector2 j     = Random.insideUnitCircle * jitter;
        Vector2 drift = _driftDir + j;
        if (drift.sqrMagnitude > 0.0001f) drift.Normalize();

        // --- Vehicle avoidance ---
        Vector2 avoid = ComputeVehicleAvoidance(_rb.position);

        // --- Orbit bias ---
        Vector2 avoidTangent = (avoid.sqrMagnitude > 0.0001f)
            ? Vector2.Perpendicular(avoid).normalized * Mathf.Clamp01(_profile.orbitBias)
            : Vector2.zero;

        // --- Density navigator blend ---
        // Two navigator inputs, each mixed independently:
        //   1) densityDir   — toward the densest dust region (star seeks food)
        //   2) snifferDir   — toward the nearest dust of the dominant role (star steers toward what it drains)
        Vector2 navContrib = Vector2.zero;
        if (_navigator != null)
        {
            Vector2 densityDir  = _navigator.GetDensitySteerDir();
            Vector2 snifferDir  = _navigator.GetDominantSnifferDir();

            if (densityDir.sqrMagnitude > 0.0001f)
                navContrib += densityDir * navBlend.densityWeight;

            if (snifferDir.sqrMagnitude > 0.0001f)
                navContrib += snifferDir * navBlend.snifferWeight;
        }

        // --- Compose desired direction ---
        // Nav contribution overrides drift when present; avoidance always applies on top.
        Vector2 baseDrift = navContrib.sqrMagnitude > 0.0001f
            ? Vector2.Lerp(drift, navContrib.normalized, Mathf.Clamp01(navContrib.magnitude))
            : drift;

        Vector2 desiredDir = baseDrift
                             + avoid    * avoidance.strength
                             + avoidTangent;

        if (desiredDir.sqrMagnitude < 0.0001f) desiredDir = drift;
        else desiredDir.Normalize();

        Vector2 desiredVel = desiredDir * speed;
        Vector2 curVel     = _rb.linearVelocity;
        Vector2 newVel     = Vector2.MoveTowards(curVel, desiredVel, steering.maxAccel * dt);

        // Optional external kinematic override (compat path — not used by density nav)
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

    // ============================================================
    // Legacy compat
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
            if (Vector2.Distance(transform.position, target) < 1f) break;
        }
    }
}
