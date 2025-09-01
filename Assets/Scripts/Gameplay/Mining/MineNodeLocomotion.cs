using UnityEngine;
using System.Linq;
[RequireComponent(typeof(Rigidbody2D))]
public class MineNodeLocomotion : MonoBehaviour
{
    public enum LocomotionMode { Idle, Evade, SeekDustHide, Frolic, OrbitTrack, PhasePulse }

    [Header("Steering")]
    public float baseMaxSpeed = 3.0f;
    public float baseMaxForce = 10f;
    public float wanderRadius = 1.75f;
    public float wanderJitter = 2.0f;
    public float evadeDistance = 6f;
    public float hideDistanceInsideDust = 0.75f;
    public LayerMask cosmicDustMask;   // set to your CosmicDust layer
    public string playerTag = "Player"; // or "Vehicle"

    [Header("Phase Multipliers")]
    public float establishSpeedMul = 0.8f;
    public float evolveSpeedMul     = 1.0f;
    public float intensifySpeedMul  = 1.2f;
    public float releaseSpeedMul    = 0.9f;
    public float wildcardSpeedMul   = 1.35f;
    public float popSpeedMul        = 1.1f;

    [Header("Track/Role Bias")]
    public float leadFrolicBias = 0.6f;     // higher -> more frolic
    public float bassHideBias   = 0.6f;     // higher -> prefers dust
    public float grooveEvadeBias = 0.6f;    // higher -> evades players
    public float harmonyOrbitBias = 0.6f;   // higher -> orbits home/track

    [Header("Assigned Track Influence")]
    public Vector2[] trackWaypoints; // optional: per-track “home” positions in world space

    private Rigidbody2D rb;
    private Vector2 wanderTarget;
    private Transform[] vehicles;
    private IMinedObjectDirective directive; // your interface/class that provides role/type/track
    private System.Func<string> getPhaseName; // plug in a delegate if you have a global

    // cache, runtime
    private LocomotionMode currentMode;
    private float maxSpeed, maxForce;

    void Awake()
    {
        rb = GetComponent<Rigidbody2D>();
        directive = GetComponentInChildren<IMinedObjectDirective>();
        vehicles = GameObject.FindGameObjectsWithTag(playerTag).Select(go => go.transform).ToArray();

        wanderTarget = Random.insideUnitCircle.normalized * Mathf.Max(0.1f, wanderRadius * 0.5f);
        RecomputeSpeedForce();
        ChooseMode();
    }

    void FixedUpdate()
    {
        // refresh vehicles if needed (players can join/leave)
        if (Time.frameCount % 60 == 0)
            vehicles = GameObject.FindGameObjectsWithTag(playerTag).Select(go => go.transform).ToArray();

        // react to current phase (swap modes or speed multipliers)
        RecomputeSpeedForce();
        MaybeRetargetMode();

        Vector2 steering = ComputeSteering(currentMode);
        steering = Vector2.ClampMagnitude(steering, maxForce);

        Vector2 vel = rb.linearVelocity + steering * Time.fixedDeltaTime;
        vel = Vector2.ClampMagnitude(vel, maxSpeed);
        rb.linearVelocity = vel;
    }

    // --- Behavior selection --------------------------------------------------

    void RecomputeSpeedForce()
    {
        float phaseMul = PhaseSpeedMultiplier(GetCurrentPhase());
        maxSpeed = baseMaxSpeed * phaseMul;
        maxForce = baseMaxForce * Mathf.Sqrt(phaseMul);
    }

    string GetCurrentPhase()
    {
        // If you have a singleton/queue, wire it here (e.g., MusicalPhaseQueue.Instance.CurrentPhaseName)
        // or set getPhaseName from outside. Fall back to "Evolve".
        if (getPhaseName != null) return getPhaseName();
        return "Evolve";
    }

    void ChooseMode()
    {
        var role = directive?.GetMusicalRole() ?? "Lead";
        var type = directive?.GetObjectType() ?? "NoteSpawner";
        int track = directive?.GetAssignedTrackIndex() ?? 0;
        string phase = GetCurrentPhase();

        // Simple rule table (feel free to extend/replace with ScriptableObject configs):
        // Role-led bias, modulated by phase & type.
        float r = Random.value;
        if (role == "Lead"    && r < leadFrolicBias)   currentMode = LocomotionMode.Frolic;
        else if (role == "Bass"   && r < bassHideBias)     currentMode = LocomotionMode.SeekDustHide;
        else if (role == "Groove" && r < grooveEvadeBias)  currentMode = LocomotionMode.Evade;
        else if (role == "Harmony"&& r < harmonyOrbitBias) currentMode = LocomotionMode.OrbitTrack;
        else currentMode = LocomotionMode.Frolic;

        // Type nudges:
        if (type == "TrackClear") currentMode = LocomotionMode.Evade;
        else if (type == "LoopExpansion") currentMode = LocomotionMode.Frolic;
        else if (type == "NoteSpawner") { /* keep role choice */ }

        // Phase accent:
        if (phase == "Wildcard") currentMode = LocomotionMode.Frolic;
        if (phase == "Release" && currentMode == LocomotionMode.Evade) currentMode = LocomotionMode.SeekDustHide;

        // Assigned track gravity occasionally wins:
        if (trackWaypoints != null && trackWaypoints.Length > 0 && Random.value < 0.15f)
            currentMode = LocomotionMode.OrbitTrack;
    }

    void MaybeRetargetMode()
    {
        // small chance to re-evaluate (prevents getting stuck & adds life)
        if (Random.value < 0.01f) ChooseMode();
    }

    float PhaseSpeedMultiplier(string phase)
    {
        switch (phase)
        {
            case "Establish":  return establishSpeedMul;
            case "Evolve":     return evolveSpeedMul;
            case "Intensify":  return intensifySpeedMul;
            case "Release":    return releaseSpeedMul;
            case "Wildcard":   return wildcardSpeedMul;
            case "Pop":        return popSpeedMul;
            default:           return 1f;
        }
    }

    // --- Steering ------------------------------------------------------------

    Vector2 ComputeSteering(LocomotionMode mode)
    {
        switch (mode)
        {
            case LocomotionMode.Evade:
                return EvadeNearestVehicle();
            case LocomotionMode.SeekDustHide:
                return SeekDustAndHide();
            case LocomotionMode.Frolic:
                return Wander();
            case LocomotionMode.OrbitTrack:
                return OrbitAssignedTrack();
            case LocomotionMode.PhasePulse:
                return PhasePulse();
            default:
                return Vector2.zero;
        }
    }

    Vector2 EvadeNearestVehicle()
    {
        Transform target = NearestVehicle(out float dist);
        if (target == null || dist > evadeDistance) return Wander();

        // flee
        Vector2 desired = (Vector2)(rb.position - (Vector2)target.position).normalized * maxSpeed;
        return desired - rb.linearVelocity;
    }

    Vector2 SeekDustAndHide()
    {
        // find nearest dust collider
        Collider2D dust = Physics2D.OverlapCircle(rb.position, evadeDistance, cosmicDustMask);
        if (dust == null) return Wander();

        Vector2 dustCenter = dust.bounds.ClosestPoint(rb.position); // approx center-ish
        float inside = Vector2.Distance(rb.position, dustCenter);

        // seek dust edge/center depending on distance
        Vector2 desired = (dustCenter - rb.position).normalized * maxSpeed;
        Vector2 steer = desired - rb.linearVelocity;

        // once “inside”, slow and add small jitter to feel hidden/alive
        if (inside < hideDistanceInsideDust)
        {
            steer *= 0.25f;
            steer += Random.insideUnitCircle * (maxForce * 0.15f);
        }
        return steer;
    }

    Vector2 Wander()
    {
        // jitter wander target in local space
        wanderTarget += Random.insideUnitCircle * (wanderJitter * Time.fixedDeltaTime);
        wanderTarget = wanderTarget.normalized * wanderRadius;

        Vector2 worldTarget = rb.position + wanderTarget;
        Vector2 desired = (worldTarget - rb.position).normalized * maxSpeed;
        return desired - rb.linearVelocity;
    }

    Vector2 OrbitAssignedTrack()
    {
        int i = Mathf.Clamp(directive?.GetAssignedTrackIndex() ?? 0, 0, (trackWaypoints?.Length ?? 1) - 1);
        if (trackWaypoints == null || trackWaypoints.Length == 0) return Wander();

        Vector2 home = trackWaypoints[i];
        // circle around home
        Vector2 toHome = (home - rb.position);
        Vector2 tangent = Vector2.Perpendicular(toHome).normalized;

        Vector2 desired = (tangent * maxSpeed * 0.7f) + (toHome.normalized * maxSpeed * 0.3f);
        return desired - rb.linearVelocity;
    }

    Vector2 PhasePulse()
    {
        // if you have downbeats or ticks, modulate thrust
        float pulse = 0.5f + 0.5f * Mathf.Sin(Time.time * 2.0f);
        return Random.insideUnitCircle.normalized * (maxForce * pulse * 0.5f);
    }

    Transform NearestVehicle(out float dist)
    {
        dist = float.PositiveInfinity;
        Transform best = null;
        foreach (var t in vehicles)
        {
            if (t == null) continue;
            float d = Vector2.Distance(rb.position, t.position);
            if (d < dist) { dist = d; best = t; }
        }
        return best;
    }

    // --- Public hooks --------------------------------------------------------

    public void SetPhaseProvider(System.Func<string> phaseGetter)
    {
        getPhaseName = phaseGetter;
    }
}
