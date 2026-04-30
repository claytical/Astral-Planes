using UnityEngine;
using UnityEngine.Serialization;

/// <summary>
/// Kite-steering movement for SuperNode: flees nearest Vehicle, stays within DrumTrack play-area bounds.
/// Designed to be chaseable (boost-reward) and fair within <= 1 loop.
/// </summary>
[DisallowMultipleComponent]
public class SuperNodeKiteSteering : MonoBehaviour
{
    [Header("Refs")]
    [SerializeField] private Rigidbody2D rb;
    [SerializeField] private SuperNode superNode;     // optional: to grab drumTrack
    [SerializeField] private DrumTrack drumTrack;     // preferred: authority for play area

    [Header("Kite Tuning")]
    [Tooltip("Top speed (must be < boosting vehicle chase speed).")]
    [SerializeField] private float maxSpeed = 6.0f;

    [Tooltip("Magnitude acceleration (units/sec^2).")]
    [FormerlySerializedAs("accel")]
    [SerializeField] private float accelerationUnitsPerSecondSq = 18.0f;

    [Tooltip("Turn rate cap (deg/sec). Higher = slipperier.")]
    [SerializeField] private float turnRateDegPerSec = 540f;

    [Tooltip("Additional damping applied each tick (stability + readability).")]
    [SerializeField] private float linearDamping = 2.5f;

    [Header("Evasion Shaping")]
    [SerializeField] private float pursuerPredictSeconds = 0.25f;
    [SerializeField] private float minFleeDistance = 0.5f;
    [SerializeField] private float fleeWeight = 1.0f;

    [Header("Noise (Subtle)")]
    [SerializeField] private float noiseWeight = 0.18f;
    [SerializeField] private float noiseFreq = 0.55f;

    [Header("Edge Repulsion (uses DrumTrack play area)")]
    [Tooltip("Start repelling before reaching the edge (world units).")]
    [SerializeField] private float edgeSoftnessWorld = 1.25f;

    [SerializeField] private float centerBiasWeight = 0.35f;

    [Header("Lifetime / Fairness")]
    [Tooltip("If true, set lifetimeSeconds from DrumTrack.GetLoopLengthInSeconds() on enable.")]
    [SerializeField] private bool bindLifetimeToLoop = true;

    [Tooltip("Fallback lifetime if DrumTrack is missing.")]
    [SerializeField] private float lifetimeSeconds = 4f;

    [Tooltip("In the last N seconds, slow slightly so it becomes catchable.")]
    [SerializeField] private float slowNearExpirySeconds = 0.5f;

    [SerializeField] private float expirySlowMultiplier = 0.75f;

    [Header("Vehicle Cache")]
    [SerializeField] private float vehicleRefreshInterval = 0.25f;

    private float _age;
    private float _vehicleRefreshTimer;
    private Vehicle[] _vehicles;

    private void Reset()
    {
        rb = GetComponent<Rigidbody2D>();
        superNode = GetComponent<SuperNode>();
    }

    private void Awake()
    {
        if (rb == null) rb = GetComponent<Rigidbody2D>();
        if (superNode == null) superNode = GetComponent<SuperNode>();

        // Basic RB settings for deterministic steering.
        if (rb != null)
        {
            rb.gravityScale = 0f;
            rb.linearDamping = 0f;        // we apply damping ourselves
            rb.angularDamping = 0f;
        }

        ResolveDrumTrack();
        RefreshVehicles();
    }

    private void OnEnable()
    {
        _age = 0f;
        _vehicleRefreshTimer = 0f;

        ResolveDrumTrack();

        if (bindLifetimeToLoop && drumTrack != null)
            lifetimeSeconds = Mathf.Max(0.05f, drumTrack.GetLoopLengthInSeconds());
    }

    private void FixedUpdate()
    {
        _age += Time.fixedDeltaTime;

        // Lightweight refresh, avoids Find every frame.
        _vehicleRefreshTimer -= Time.fixedDeltaTime;
        if (_vehicleRefreshTimer <= 0f)
        {
            RefreshVehicles();
            _vehicleRefreshTimer = vehicleRefreshInterval;
        }

        if (rb == null) return;
        if (_vehicles == null || _vehicles.Length == 0) return;

        ResolveDrumTrack();
        if (drumTrack == null) return;

        // Determine play bounds from DrumTrack authority.
        if (!TryGetWorldBounds(out Rect bounds))
            return;

        // Closest pursuer.
        Vector2 pos = rb.position;
        Vehicle closest = null;
        float bestSqr = float.PositiveInfinity;

        for (int i = 0; i < _vehicles.Length; i++)
        {
            var v = _vehicles[i];
            if (v == null) continue;

            float dSqr = ((Vector2)v.transform.position - pos).sqrMagnitude;
            if (dSqr < bestSqr)
            {
                bestSqr = dSqr;
                closest = v;
            }
        }
        if (closest == null) return;

        // Predict pursuer (makes it feel "smart" without cheating).
        Vector2 pursuerPos = (Vector2)closest.transform.position;
        var pursuerRb = closest.GetComponent<Rigidbody2D>();
        Vector2 pursuerVel = pursuerRb != null ? pursuerRb.linearVelocity : Vector2.zero;

        pursuerPos += pursuerVel * pursuerPredictSeconds;

        Vector2 away = pos - pursuerPos;
        float dist = Mathf.Max(minFleeDistance, away.magnitude);
        Vector2 fleeDir = away / dist;

        // Smooth noise.
        float t = Time.time;
        Vector2 noise = new Vector2(
            Mathf.PerlinNoise(0.0f, t * noiseFreq) - 0.5f,
            Mathf.PerlinNoise(1.0f, t * noiseFreq) - 0.5f
        );
        if (noise.sqrMagnitude > 0.0001f) noise.Normalize();

        // Edge repulsion via center bias (stronger near edges).
        Vector2 center = bounds.center;
        Vector2 toCenter = center - pos;
        Vector2 centerDir = toCenter.sqrMagnitude > 0.0001f ? toCenter.normalized : Vector2.zero;

        float edgeFactor01 = SuperNodeSteeringMath.EdgeFactor01(pos, bounds, edgeSoftnessWorld);
        Vector2 boundsBias = centerDir * edgeFactor01;

        // Combine desired heading.
        Vector2 desiredDir =
            fleeDir * fleeWeight +
            noise * noiseWeight +
            boundsBias * centerBiasWeight;

        if (desiredDir.sqrMagnitude < 0.0001f)
            desiredDir = fleeDir;
        desiredDir.Normalize();

        // Near-expiry slowdown (optional, improves perceived fairness).
        float effectiveMaxSpeed = maxSpeed;
        float timeLeft = Mathf.Max(0f, lifetimeSeconds - _age);
        if (timeLeft <= slowNearExpirySeconds)
            effectiveMaxSpeed *= expirySlowMultiplier;

        Vector2 desiredVel = desiredDir * effectiveMaxSpeed;

        // Turn-rate-limited steering.
        Vector2 vel = rb.linearVelocity;
        Vector2 newVel = SuperNodeSteeringMath.SteerTowards(vel, desiredVel, turnRateDegPerSec, accelerationUnitsPerSecondSq, Time.fixedDeltaTime);

        // Damping.
        newVel = Vector2.Lerp(newVel, Vector2.zero, linearDamping * Time.fixedDeltaTime);

        rb.linearVelocity = newVel;

        // Hard clamp to play area (prevents corner-traps / out-of-bounds).
        SuperNodeSteeringMath.ClampToBounds(rb, bounds);

        // Lifetime end: let SuperNode handle destruction semantics if desired.
        if (_age >= lifetimeSeconds)
        {
            // If you want movement component to self-despawn even when not triggered, do it here.
            // Destroy(gameObject);
        }
    }

    private void ResolveDrumTrack()
    {
        if (drumTrack != null) return;

        // Prefer SuperNode’s assigned DrumTrack.
        if (superNode != null && superNode.drumTrack != null)
        {
            drumTrack = superNode.drumTrack;
            return;
        }

        // Fallback to active drum track.
        var gfm = GameFlowManager.Instance;
        if (gfm != null && gfm.activeDrumTrack != null)
        {
            drumTrack = gfm.activeDrumTrack;
            return;
        }

        // Last resort.
        drumTrack = FindAnyObjectByType<DrumTrack>();
    }

    private bool TryGetWorldBounds(out Rect bounds)
    {
        bounds = default;
        if (drumTrack == null) return false;

        // DrumTrack.TryGetPlayAreaWorld is public in your file.
        if (!drumTrack.TryGetPlayAreaWorld(out var area))
            return false;

        bounds = Rect.MinMaxRect(area.left, area.bottom, area.right, area.top);
        return bounds.width > 0.01f && bounds.height > 0.01f;
    }

    private void RefreshVehicles()
    {
        // In newer Unity you can use FindObjectsByType. This is simple + robust for now.
        // If you already keep a live vehicle list in GameFlowManager, swap to that.
        _vehicles = FindObjectsOfType<Vehicle>(includeInactive: false);
    }

}
