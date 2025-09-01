using System.Collections;
using System.Linq;
using UnityEngine;

[RequireComponent(typeof(Rigidbody2D))]
public class MineNodeGaits : MonoBehaviour
{
    public LayerMask dustMask;
    public float senseRadius = 7f;
    public string vehicleTag = "Player"; // or "Vehicle"
    public Vector2[] trackWaypoints;     // optional “home” per track

    [Header("Impulse tuning")]
    public float wanderImpulse = 1.5f;
    public float evadeImpulse  = 2.2f;
    public float dustSeekImpulse = 1.8f;
    public float orbitImpulse  = 1.2f;
    public float maxSpeed      = 4.0f;   // clamp velocity cap
    public Vector2 impulseIntervalRange = new(0.25f, 0.6f);

    Rigidbody2D rb;
    IMinedObjectDirective directive;
    MineNodeDustInteractor dustFX;
    System.Func<string> getPhase; // progressionManager.GetCurrentPhaseName
    Transform[] vehicles;

    Coroutine gaitCo;

    void Awake()
    {
        rb = GetComponent<Rigidbody2D>();
        directive = GetComponentInChildren<IMinedObjectDirective>();
        dustFX = GetComponent<MineNodeDustInteractor>();
        vehicles = GameObject.FindGameObjectsWithTag(vehicleTag).Select(go => go.transform).ToArray();
    }

    void OnEnable()
    {
        ChooseGaitAndRun();
    }

    void FixedUpdate()
    {
        // simple global cap; environment/dust adds texture
        if (rb.linearVelocity.magnitude > maxSpeed)
            rb.linearVelocity = rb.linearVelocity.normalized * maxSpeed;

        if (Time.frameCount % 60 == 0)
            vehicles = GameObject.FindGameObjectsWithTag(vehicleTag).Select(go => go.transform).ToArray();
    }

    public void SetPhaseProvider(System.Func<string> phaseGetter)
    {
        getPhase = phaseGetter;
    }

    // ---------------- Gait selection (infrequent) ----------------

    void ChooseGaitAndRun()
    {
        if (gaitCo != null) StopCoroutine(gaitCo);

        string role  = directive?.GetMusicalRole() ?? "Lead";
        string type  = directive?.GetObjectType() ?? "NoteSpawner";
        int trackIdx = directive?.GetAssignedTrackIndex() ?? 0;
        string phase = getPhase != null ? getPhase() : "Evolve";

        // Simple table: role first, nudge by type/phase
        if (type == "TrackClear") gaitCo = StartCoroutine(EvadeBursts());
        else if (role == "Bass" || phase == "Release") gaitCo = StartCoroutine(HideInDustLoiter());
        else if (role == "Harmony") gaitCo = StartCoroutine(OrbitHome(trackIdx));
        else if (role == "Groove") gaitCo = StartCoroutine(EvadeBursts());
        else /* Lead / default */ gaitCo = StartCoroutine(WanderPuffs(phase));
    }

    // ---------------- Gaits (impulse coroutines) ----------------

    IEnumerator WanderPuffs(string phase)
    {
        float mul = phase == "Wildcard" ? 1.25f : phase == "Intensify" ? 1.15f : 1f;
        for (;;)
        {
            // mild bias toward nearest dust so nodes feel embedded
            Vector2 dir = Random.insideUnitCircle.normalized;
            var dust = Physics2D.OverlapCircle(rb.position, senseRadius, dustMask);
            if (dust != null && Random.value < 0.5f)
                dir = ((Vector2)dust.bounds.center - rb.position).normalized * 0.6f + dir * 0.4f;

            rb.AddForce(dir.normalized * (wanderImpulse * mul), ForceMode2D.Impulse);
            yield return new WaitForSeconds(Random.Range(impulseIntervalRange.x, impulseIntervalRange.y));
        }
    }

    IEnumerator EvadeBursts()
    {
        for (;;)
        {
            Transform t = NearestVehicle(out float d);
            Vector2 dir;
            if (t != null && d < senseRadius)
                dir = (rb.position - (Vector2)t.position).normalized; // flee
            else
                dir = Random.insideUnitCircle.normalized;             // roam if nobody near

            // if dust is close, occasionally veer toward edge for cover
            var dust = Physics2D.OverlapCircle(rb.position, senseRadius, dustMask);
            if (dust != null && Random.value < 0.35f)
                dir = Vector2.Lerp(dir, ((Vector2)dust.bounds.center - rb.position).normalized, 0.4f).normalized;

            rb.AddForce(dir * evadeImpulse, ForceMode2D.Impulse);
            yield return new WaitForSeconds(Random.Range(impulseIntervalRange.x, impulseIntervalRange.y));
        }
    }

    IEnumerator HideInDustLoiter()
    {
        for (;;)
        {
            var dust = Physics2D.OverlapCircle(rb.position, senseRadius, dustMask);
            Vector2 dir = dust != null
                ? ((Vector2)dust.bounds.center - rb.position).normalized            // go in
                : Random.insideUnitCircle.normalized;                               // wander to find dust

            rb.AddForce(dir * dustSeekImpulse, ForceMode2D.Impulse);

            // if we’re already inside dust, add tiny random pips to “settle”
            if (dustFX != null && dustFX.insideDust)
                rb.AddForce(Random.insideUnitCircle * 0.2f, ForceMode2D.Impulse);

            yield return new WaitForSeconds(Random.Range(impulseIntervalRange.x, impulseIntervalRange.y));
        }
    }

    IEnumerator OrbitHome(int trackIdx)
    {
        Vector2 home = (trackWaypoints != null && trackWaypoints.Length > 0)
            ? trackWaypoints[Mathf.Clamp(trackIdx, 0, trackWaypoints.Length - 1)]
            : (Vector2)transform.position;

        for (;;)
        {
            Vector2 toHome = home - rb.position;
            Vector2 tangent = Vector2.Perpendicular(toHome).normalized;
            Vector2 dir = (tangent * 0.7f + toHome.normalized * 0.3f).normalized;

            rb.AddForce(dir * orbitImpulse, ForceMode2D.Impulse);
            yield return new WaitForSeconds(Random.Range(impulseIntervalRange.x, impulseIntervalRange.y));
        }
    }

    // ---------------- Helpers ----------------

    Transform NearestVehicle(out float dist)
    {
        dist = float.PositiveInfinity;
        Transform best = null;
        foreach (var t in vehicles)
        {
            if (!t) continue;
            float d = Vector2.Distance(rb.position, t.position);
            if (d < dist) { dist = d; best = t; }
        }
        return best;
    }

    // Call this when the phase changes (from your progression manager)
    public void OnPhaseChanged() => ChooseGaitAndRun();
}

