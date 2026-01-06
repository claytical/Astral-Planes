using UnityEngine;
using System;
using System.Collections;
using Random = UnityEngine.Random;
using System.Collections.Generic;
[RequireComponent(typeof(Rigidbody2D))]
[DisallowMultipleComponent]
public sealed class PhaseStarMotion2D : MonoBehaviour
{
    public event Action<Vector2> OnVelocityChanged;
    public Func<IReadOnlyList<Vector2>> VehiclePositionsProvider;
    public Func<Vector2, float> DustDensitySampler;
    public Func<Vector2, float, Vector2> ExternalPathStep; 
    Rigidbody2D _rb;
    PhaseStarBehaviorProfile _profile;
    bool _enabled;
    Vector2 _driftDir;
    float _rechooseTimer;
    [SerializeField] bool kinematicMode = false;     // toggle in Inspector or at runtime
    [SerializeField] float edgeBounce = 0.9f;        // reflect drift a bit at edges
    [SerializeField] float screenPadding = 0.5f;     // keep it slightly inside the frame
    [SerializeField] float focusSpeedMul = 0.5f;     // slows during focus mode
    [Header("Vehicle Avoidance")] 
    [SerializeField] float avoidRadius = 3.0f;       // repulsion range (world units)
    [SerializeField] float avoidExponent = 2.0f;     // higher = sharper near players
    [SerializeField] float avoidStrength = 1.0f;     // weight of avoidance vs drift
    [Header("Dust Edge Seeking")]
    [SerializeField] float edgeSampleStep = 0.35f;   // finite diff step (world units)
    [SerializeField, Range(0f,1f)] float edgeBand = 0.5f; // target density band (0 empty .. 1 dust)
    [SerializeField] float edgeStrength = 1.0f;      // weight of edge seeking vs drift
    [SerializeField] float edgeGradMin = 0.02f;      // ignore very flat gradients (not near edge)
    [Header("Steering")]
    [SerializeField] float maxAccel = 4.0f;          // max change in velocity per second
    [SerializeField] bool verbose = false;
    bool _focus;  // set from PhaseStar
    Camera _cam;


    void Awake()
    {
        _rb = GetComponent<Rigidbody2D>();
        _rb.gravityScale = 0f;
        _rb.constraints  = RigidbodyConstraints2D.FreezeRotation;
        _cam = Camera.main;
        _driftDir = Random.insideUnitCircle.normalized;
        _rechooseTimer = 0f;

        ApplyBodyType();
    }

    public void SetFocusMode(bool on) => _focus = on;
    void KeepInsideScreenAndBounce()
    {
        if (_cam == null) _cam = Camera.main;
        if (_cam == null) return;

        var pos = _rb.position;
        float z = Mathf.Abs(_cam.transform.position.z);

        var min = (Vector2)_cam.ViewportToWorldPoint(new Vector3(0f, 0f, z)) + Vector2.one * screenPadding;
        var max = (Vector2)_cam.ViewportToWorldPoint(new Vector3(1f, 1f, z)) - Vector2.one * screenPadding;

        bool hit = false;
        if (pos.x < min.x) { pos.x = min.x; _driftDir.x = Mathf.Abs(_driftDir.x) * edgeBounce; hit = true; }
        else if (pos.x > max.x) { pos.x = max.x; _driftDir.x = -Mathf.Abs(_driftDir.x) * edgeBounce; hit = true; }

        if (pos.y < min.y) { pos.y = min.y; _driftDir.y = Mathf.Abs(_driftDir.y) * edgeBounce; hit = true; }
        else if (pos.y > max.y) { pos.y = max.y; _driftDir.y = -Mathf.Abs(_driftDir.y) * edgeBounce; hit = true; }

        if (hit)
        {
            _driftDir.Normalize();
            if (kinematicMode) _rb.position = pos;
            else _rb.MovePosition(pos); // cheap corrector for dynamic too
        }
    }
    void ApplyBodyType()
    {
        if (!_rb) return;
        _rb.bodyType = kinematicMode ? RigidbodyType2D.Kinematic : RigidbodyType2D.Dynamic;
        _rb.linearVelocity = Vector2.zero;
        _rb.angularVelocity = 0f;
        _rb.constraints = RigidbodyConstraints2D.FreezeRotation;
    }

    public void Initialize(PhaseStarBehaviorProfile profile, PhaseStar star)
    {
        _profile = profile;

        // Drive enable/disable from PhaseStar events
        star.OnArmed += s => { Enable(true);  };
        star.OnDisarmed += s => { Enable(false); };
// Default vehicle provider: read from GameFlowManager.localPlayers[].plane
// // This supports 1–4 players naturally and ignores unlaunched players (plane == null).
        if (DustDensitySampler == null) { 
            DustDensitySampler = (pos) => { 
                var gfm = GameFlowManager.Instance; 
                var dust = gfm ? gfm.dustGenerator : null; 
                if (dust == null) return 0f; 
                return dust.SampleDensity01(pos);
            };
        }
        VehiclePositionsProvider ??= () =>
        {
            var gfm = GameFlowManager.Instance;
            if (gfm == null || gfm.localPlayers == null) return Array.Empty<Vector2>();
            // Avoid allocations if you care: you can pool this list later.
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
    }

    public void Enable(bool on)
    {
        _enabled = on;
        if (!_rb) return;

        if (!on)
        {
            _rb.linearVelocity = Vector2.zero;
            _rb.angularVelocity = 0f;
            _rb.constraints = RigidbodyConstraints2D.FreezeAll;
        }
        else
        {
            _rb.linearVelocity = Vector2.zero;
            _rb.angularVelocity = 0f;
            _rb.constraints = RigidbodyConstraints2D.FreezeRotation;
        }
    }

    void FixedUpdate()
    {
        if (!_enabled || _profile == null || !_rb) { 
            if (verbose) { 
                if (!_enabled) Debug.Log("[Motion2D] disabled"); 
                if (_profile == null) Debug.Log("[Motion2D] no profile");
            } 
            return;
        }
        float speed  = Mathf.Max(0f, _profile.starDriftSpeed);
        float jitter = Mathf.Max(0f, _profile.starDriftJitter);
        if (_focus) speed *= Mathf.Clamp(focusSpeedMul, 0.1f, 1f); // slow in focus

        _rechooseTimer -= Time.fixedDeltaTime;
        if (_rechooseTimer <= 0f) PickNewDriftDir();

// --- Drift base ----------------------------------------------------
    Vector2 j = Random.insideUnitCircle * jitter;
    Vector2 drift = (_driftDir + j);
    if (drift.sqrMagnitude > 0.0001f) drift.Normalize(); 
    // --- Vehicle avoidance (multi-player vector field) -----------------
    Vector2 avoid = ComputeVehicleAvoidance(_rb.position);
    // --- Dust edge seeking (prefer boundary band) ----------------------
    Vector2 edge = ComputeDustEdgeSeek(_rb.position);
    // --- Personality: arc around avoidance via orbitBias ---------------
    Vector2 avoidTangent = (avoid.sqrMagnitude > 0.0001f)
        ? Vector2.Perpendicular(avoid).normalized * Mathf.Clamp01(_profile.orbitBias)
        : Vector2.zero;
        // Weighted blend in direction space
            Vector2 desiredDir = drift + avoid * avoidStrength + edge * edgeStrength + avoidTangent;
            if (desiredDir.sqrMagnitude < 0.0001f) 
                desiredDir = drift; // fallback
            else
            {
                desiredDir.Normalize();        
            }
        Vector2 desiredVel = desiredDir * speed;
        // Steering limit: change velocity gradually for "body language"
        Vector2 curVel = kinematicMode ? _rb.linearVelocity : _rb.linearVelocity; 
        Vector2 newVel = Vector2.MoveTowards(curVel, desiredVel, maxAccel * Time.fixedDeltaTime);
        // Allow an external path step to override position directly (kinematic)
        if (kinematicMode && ExternalPathStep != null) { 
            var next = ExternalPathStep(_rb.position, Time.fixedDeltaTime); 
            _rb.MovePosition(next);
        }
        else if (kinematicMode) { 
            _rb.MovePosition(_rb.position + newVel * Time.fixedDeltaTime);
        }
        else { 
            _rb.linearVelocity = newVel;
        }
        KeepInsideScreenAndBounce();

        OnVelocityChanged?.Invoke(kinematicMode ? newVel : _rb.linearVelocity);
        // Teleport spice (Wildcard)
        if (_profile.teleportChancePerSec > 0f &&
            UnityEngine.Random.value < _profile.teleportChancePerSec * Time.fixedDeltaTime)
        {
            Vector2 off = UnityEngine.Random.insideUnitCircle * 1.5f;
            _rb.position += off;
            OnVelocityChanged?.Invoke(kinematicMode ? newVel : _rb.linearVelocity);        }
    }
    Vector2 ComputeVehicleAvoidance(Vector2 starPos) { 
        var provider = VehiclePositionsProvider; 
        if (provider == null) return Vector2.zero; 
        var vehicles = provider(); 
        if (vehicles == null || vehicles.Count == 0) return Vector2.zero;
        float R = Mathf.Max(0.01f, avoidRadius); 
        float p = Mathf.Max(0.1f, avoidExponent);
        Vector2 sum = Vector2.zero; 
        for (int i = 0; i < vehicles.Count; i++) { 
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
    Vector2 ComputeDustEdgeSeek(Vector2 starPos) { 
        var sampler = DustDensitySampler; 
        if (sampler == null) return Vector2.zero; 
        float e = Mathf.Max(0.01f, edgeSampleStep); 
        float c  = sampler(starPos); 
        float dx = sampler(starPos + Vector2.right * e) - sampler(starPos - Vector2.right * e); 
        float dy = sampler(starPos + Vector2.up    * e) - sampler(starPos - Vector2.up    * e); 
        Vector2 grad = new Vector2(dx, dy) / (2f * e);
        float gm = grad.magnitude; 
        if (gm < edgeGradMin) return Vector2.zero; // not near an edge
        Vector2 normal = grad / gm; // points toward increasing dust density
        // Seek a band around edgeBand: if we're "too dusty" push outward, too empty push inward.
        float bandErr = Mathf.Clamp(c - edgeBand, -1f, 1f); 
        // Want to correct toward the band => move opposite direction of error along normal
        Vector2 correction = -normal * bandErr; 
        // Weight by gradient magnitude so it prefers strong edges.
        return correction * gm;
    }
    public void SetTarget(Vector3 target, float curiosity = 1f) {
        StartCoroutine(MoveTowardTarget(target, curiosity));
    }

    private IEnumerator MoveTowardTarget(Vector3 target, float curiosity) {
        while (true) {
            Vector2 dir = (target - transform.position).normalized;
            _rb.AddForce(dir * curiosity * 0.5f, ForceMode2D.Force);
            yield return new WaitForSeconds(0.25f);
            if (Vector2.Distance(transform.position, target) < 1f)
                break;
        }
    }

    void PickNewDriftDir()
    {
        Vector2 rnd = Random.insideUnitCircle.normalized;
        float bias  = Mathf.Clamp01(_profile.orbitBias);
        _driftDir   = Vector2.Lerp(rnd, Vector2.Perpendicular(rnd).normalized, bias).normalized;
        _rechooseTimer = UnityEngine.Random.Range(1.2f, 2.4f);
    }
}
