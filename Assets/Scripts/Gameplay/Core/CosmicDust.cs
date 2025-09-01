// HexagonShield.cs

using System.Collections;
using UnityEngine;

public class CosmicDust : MonoBehaviour
{
    [Header("Sizing (safe fallback)")]
    public Vector3 referenceScale = new Vector3(0.75f, 0.75f, 1f); // <- tweak to your taste
// === PhaseStar interaction ===
    [Header("PhaseStar Proximity")]
    public bool starRemovesWithoutRegrow = true;   // if false, it will regrow via generator
    public float starAlphaFadeBias = 0.9f;         // keep a little glow as it shrinks
    private bool shrinkingFromStar = false;
    private Coroutine growInRoutine;               // to cancel grow-in while shrinking

    public enum CosmicDustType { Friendly, Depleting }
    public CosmicDustType shieldType = CosmicDustType.Friendly;
    public enum DustBehavior { ViscousSlow, SiltDissipate, StaticCling, CrossCurrent, Turbulent }
    [Header("Dust Field")]
    [Range(0f,1.5f)] public float slowBrake = 0.4f;   // extra braking force
    [Range(0.4f, 1f)] public float speedScale = 0.8f;   // cap scale
    [Range(0.4f, 1f)] public float accelScale = 0.8f;   // thrust scale
    public bool dissipateOnExit = false;

    private float baseStartSize = 1f;         // particle reset
    private float baseStartLifetime = 1f;
    private bool cachedPS = false;
    [Header("Behavior")]
    public DustBehavior behavior = DustBehavior.ViscousSlow; // NEW
    [Range(0.2f,1f)] public float slowFactor = 0.7f;         // NEW (velocity multiplier while inside)
    [Range(0f,2f)]   public float slowDuration = 0.35f;       // NEW (seconds)
    [Range(0f,10f)]  public float lateralForce = 2.0f;        // NEW (CrossCurrent)
    [Range(0f,10f)]  public float turbulence = 0.0f;          // NEW (Wildcard micro-deflection)
    public bool dissipateOnHit = false;                       // NEW (Silt cloud)
    public bool particleSwellOnHit = true;                    // NEW
    private bool hasDissipated = false;                       // NEW
    private SpriteRenderer sr;
    private Color depletingColor = new Color(1f, 0.2f, 0.2f, 0.2f);
    public SpriteRenderer baseSprite;
    public ParticleSystem particleSystem;
    //public SpriteRenderer halo;
    public float amplitude = 1f; // drift radius
    public float speed = 0.5f;     // speed of drift
    public Vector2 offset;
    private float alphaBreathingOffset = 0;
    private static readonly float ChainRadius = 1.5f;
    private DrumTrack drumTrack;
    private float originalAlpha;
    private Vector3 fullScale;
    private Vector3 velocity;
    private Vector3 baseScale;
// Add at top-level of CosmicDust
    [System.Serializable]
    public struct DustTuning {
        public float speedScale;       // multiply ship terminalVelocity (0.5–1.0)
        public float accelScale;       // multiply ship thrust/force (0.5–1.0)
        public float extraDamping;     // added linear damping on ship (0–2)
        public float drainPerSecond;   // energy/sec if Depleting
        public float puffScale;        // particle expansion multiplier on enter
    }

// Per-phase defaults (tweak to taste)
    public DustTuning establish = new(){ speedScale=.90f, accelScale=.95f, extraDamping=.10f, drainPerSecond=0f,   puffScale=1.2f };
    public DustTuning evolve    = new(){ speedScale=.85f, accelScale=.90f, extraDamping=.20f, drainPerSecond=0f,   puffScale=1.3f };
    public DustTuning intensify = new(){ speedScale=.60f, accelScale=.75f, extraDamping=.60f, drainPerSecond=.7f,  puffScale=1.5f };
    public DustTuning release   = new(){ speedScale=.80f, accelScale=.85f, extraDamping=.25f, drainPerSecond=0f,   puffScale=1.25f };
    public DustTuning wildcard  = new(){ speedScale=.70f, accelScale=.80f, extraDamping=.35f, drainPerSecond=.3f,  puffScale=1.4f };
    public DustTuning pop       = new(){ speedScale=.90f, accelScale=.95f, extraDamping=.10f, drainPerSecond=0f,   puffScale=1.6f };

    private DustTuning CurrentTuning() {
        switch (drumTrack.currentPhase) {
            case MusicalPhase.Establish:  return establish;
            case MusicalPhase.Evolve:     return evolve;
            case MusicalPhase.Intensify:  return intensify;
            case MusicalPhase.Release:    return release;
            case MusicalPhase.Wildcard:   return wildcard;
            case MusicalPhase.Pop:        return pop;
            default:                      return establish;
        }
    }

    void Awake() {
        // If the prefab/instance was saved at scale 0, use a sane fallback.
        if (transform.localScale.sqrMagnitude < 1e-6f)
            transform.localScale = referenceScale;

        fullScale = transform.localScale * 2f; // your intended “final size”
        alphaBreathingOffset = Random.Range(0f, 1f);
        if (particleSystem != null) {
            var main = particleSystem.main;
            baseStartSize = main.startSizeMultiplier;
            baseStartLifetime = main.startLifetimeMultiplier;
            cachedPS = true;
        }
    }
    private IEnumerator GrowIn()
    {
        float tGrow = 0f;
        float duration = Random.Range(5, 20);
        
        while (tGrow < duration)
        {
            tGrow += Time.deltaTime;
            float s = Mathf.SmoothStep(0f, 1f, tGrow / duration);
            transform.localScale = fullScale * s;
            yield return null;
        }
        transform.localScale = fullScale; // Ensure exact final size
    }


    public void Begin()
    {
        SetColorVariance();
        // ✅ Start alpha fade-in
        if (baseSprite != null)
        {
            Color c = baseSprite.color;
            originalAlpha = c.a;
            c.a = 0f;
            baseSprite.color = c;
        }
        transform.localScale = Vector3.zero;
        growInRoutine = StartCoroutine(GrowIn());
        StartCoroutine(FadeInAlpha(targetAlpha: originalAlpha));

    }
    // Called repeatedly by PhaseStar while in range
public void ShrinkByPhaseStar(float unitsPerSecond)
{
    if (shrinkingFromStar) { DoShrink(unitsPerSecond); return; }

    // first entry: cancel any ongoing grow-in so it doesn't fight the shrink
    if (growInRoutine != null) { StopCoroutine(growInRoutine); growInRoutine = null; }
    shrinkingFromStar = true;

    DoShrink(unitsPerSecond);
}

private void DoShrink(float unitsPerSecond)
{
    Vector3 s = transform.localScale;
    float step = unitsPerSecond * Time.deltaTime;
    float nx = Mathf.Max(0f, s.x - step);
    float ny = Mathf.Max(0f, s.y - step);
    transform.localScale = new Vector3(nx, ny, s.z);

    // fade sprite proportional to remaining size
    if (baseSprite != null && fullScale.x > 0.0001f)
    {
        float t = Mathf.Clamp01(nx / fullScale.x);
        Color c = baseSprite.color;
        c.a = Mathf.Lerp(0f, originalAlpha * starAlphaFadeBias, t);
        baseSprite.color = c;
    }

    // optional: dim particles as it shrinks
    if (particleSystem != null)
    {
        var main = particleSystem.main;
        main.startSizeMultiplier = Mathf.Max(0.05f, main.startSizeMultiplier * (nx + 0.001f) / (s.x + 0.001f));
    }

    // reached ~zero? remove cleanly (with or without regrow)
    if (nx <= 0.01f || ny <= 0.01f)
        DestroyFromPhaseStar();
}

private void DestroyFromPhaseStar()
{
    // free cell + remove from generator map, just like BreakHexagon
    if (drumTrack != null)
    {
        Vector2Int gridPos = drumTrack.WorldToGridPosition(transform.position);
        drumTrack.FreeSpawnCell(gridPos.x, gridPos.y);                              // frees grid slot
        drumTrack.hexMazeGenerator.RemoveHex(gridPos);                              // remove map entry
        if (!starRemovesWithoutRegrow)
            StartCoroutine(TriggerDelayedRegrowth(gridPos));                        // only if you want regrowth
    }

    // let particles finish, then destroy
    if (particleSystem != null)
    {
        particleSystem.Stop(true, ParticleSystemStopBehavior.StopEmitting);
        StartCoroutine(WaitForParticlesThenDestroy());                              // graceful destroy
    }
    else
    {
        Destroy(gameObject);
    }
}

    private IEnumerator FadeInAlpha(float targetAlpha)
    {
        if (baseSprite == null) yield break;
        float duration = 0.5f;
        float t = 0f;

        Color color = baseSprite.color;
        float finalAlpha = originalAlpha;// store the intended final alpha
        color.a = 0f;
        baseSprite.color = color;

        while (t < duration)
        {
            t += Time.deltaTime;
            float a = Mathf.Lerp(0f, finalAlpha, t / duration);
            color.a = a;
            baseSprite.color = color;
            yield return null;
        }

        color.a = finalAlpha;
        baseSprite.color = color;
    }

    void SetColorVariance()
    {
        Color color = baseSprite.color;
        float variation = Random.Range(-0.02f, 0.02f);
        color.r += variation;
        color.g += variation;
        color.b += variation;
        baseSprite.color = color;

        // Optional: Apply glow material tweaks here
    }
    private void SetParticleColor(Color c)
    {
        if (particleSystem == null) return;

        var main = particleSystem.main;
        main.startColor = new ParticleSystem.MinMaxGradient(c);
    }

    public void TriggerRippleEffect()
    {
        foreach (CosmicDust dust in FindObjectsOfType<CosmicDust>())
        {
            float dist = Vector2.Distance(transform.position, dust.transform.position);
            float delay = dist * 0.05f;
        }
    }

    public void SetDrumTrack(DrumTrack track)
    {
        drumTrack = track;
    }

    public void ShiftToPhaseColor(MusicalPhaseProfile profile, float duration)
    {
        StartCoroutine(PhaseColorLerpRoutine(profile.visualColor, duration));
    }

    private IEnumerator PhaseColorLerpRoutine(Color phaseColor, float duration)
    {
        if (baseSprite == null) yield break;

        Color startColor = baseSprite.color;
        float t = 0f;

        while (t < duration)
        {
            t += Time.deltaTime;
            Color lerped = Color.Lerp(startColor, phaseColor, t / duration);
            baseSprite.color = lerped;
            SetParticleColor(lerped);
            yield return null;
        }

        baseSprite.color = phaseColor;
        SetParticleColor(phaseColor);

    }

    public void SetPhaseColor(MusicalPhase phase)
    {
        Color phaseColor = phase switch
        {
            MusicalPhase.Establish => new Color(0.2f, 1f, 1f, 0.25f),     // Cyan / Gentle
            MusicalPhase.Evolve    => new Color(0.4f, 0.8f, 1f, 0.25f),     // Blue / Reflective
            MusicalPhase.Intensify => new Color(1f, 0.3f, 0.3f, 0.3f),     // Red / Bold
            MusicalPhase.Release   => new Color(1f, 0.8f, 0.4f, 0.25f),     // Amber / Fading
            MusicalPhase.Wildcard  => new Color(1f, 1f, 1f, 0.3f),         // White shimmer
            MusicalPhase.Pop       => new Color(1f, 0.6f, 1f, 0.25f),       // Pinkish
            _                      => new Color(0.5f, 0.5f, 0.5f, 0.2f),   // Default gray
        };

        if (baseSprite != null)
        {
            if (shieldType == CosmicDustType.Friendly)
            {
                baseSprite.color = phaseColor;
                SetParticleColor(phaseColor);
            }
            else
            {
                baseSprite.color = depletingColor;
            }
        }
        Debug.Log($"Configuring New Phase: {phase}");
        ConfigureForPhase(phase); // NEW: tie feel to phase
    }

    public void BreakHexagon(SoundEffectMood mood)
    {
        CollectionSoundManager.Instance?.PlayEffect(SoundEffectPreset.Dust);
        if (drumTrack != null)
        {
            Vector2Int gridPos = drumTrack.WorldToGridPosition(transform.position);
            drumTrack.FreeSpawnCell(gridPos.x, gridPos.y);
            drumTrack.hexMazeGenerator.RemoveHex(gridPos);
            StartCoroutine(TriggerDelayedRegrowth(gridPos));
        }

        if (particleSystem != null)
        {
            StartCoroutine(WaitForParticlesThenDestroy());
        }
        else
        {
            Destroy(gameObject);
        }
    }
    private IEnumerator WaitForParticlesThenDestroy()
    {
        particleSystem.Stop(true, ParticleSystemStopBehavior.StopEmitting);

        // Wait until all particles have disappeared
        while (particleSystem.IsAlive(true))
        {
            yield return null;
        }

        Destroy(gameObject);
    }

    private IEnumerator TriggerDelayedRegrowth(Vector2Int gridPos)
    {
        yield return new WaitForSeconds(0.5f); // delay long enough for Explode effect
        drumTrack.hexMazeGenerator.TriggerRegrowth(gridPos, drumTrack.currentPhase);
    }
    // PHASE → BEHAVIOR mapping (call this when phase changes or on spawn)
    public void ConfigureForPhase(MusicalPhase phase)
    {
        switch (phase)
        {
            case MusicalPhase.Establish:
                behavior = DustBehavior.ViscousSlow;
                slowFactor = 0.8f; slowDuration = 0.25f;
                lateralForce = 0f; turbulence = 0f; dissipateOnHit = false;
                break;
            case MusicalPhase.Evolve:
                behavior = DustBehavior.CrossCurrent;
                slowFactor = 0.9f; slowDuration = 0.2f;
                lateralForce = 2.5f; turbulence = 0.25f; dissipateOnHit = false;
                break;
            case MusicalPhase.Intensify:
                behavior = DustBehavior.StaticCling;
                slowFactor = 0.5f; slowDuration = 0.5f;
                lateralForce = 0.5f; turbulence = 0.4f; dissipateOnHit = false;
                break;
            case MusicalPhase.Release:
                behavior = DustBehavior.SiltDissipate;
                slowFactor = 0.85f; slowDuration = 0.2f;
                lateralForce = 0f; turbulence = 0f; dissipateOnHit = true;
                break;
            case MusicalPhase.Wildcard:
                behavior = DustBehavior.Turbulent;
                slowFactor = 0.7f; slowDuration = 0.4f;
                lateralForce = 1.2f; turbulence = 2.0f; dissipateOnHit = Random.value < 0.3f;
                break;
            case MusicalPhase.Pop:
                behavior = DustBehavior.ViscousSlow;
                slowFactor = 0.6f; slowDuration = 0.3f;
                lateralForce = 0f; turbulence = 0.2f; dissipateOnHit = true;
                break;
        }
        Debug.Log($"Chose Behavior: {behavior}");
    }

    /*
    private void OnCollisionEnter2D(Collision2D coll)
    {
        if (coll.gameObject.TryGetComponent<Vehicle>(out var vehicle))
        {
            Vector2 impactDir = coll.relativeVelocity.sqrMagnitude > 0.001f
                ? coll.relativeVelocity.normalized : (Vector2)(vehicle.transform.position - transform.position).normalized;

            AbsorbDiamondGhost(impactDir);

            // ENERGY penalty only if Depleting (you already do this)
            if (shieldType == CosmicDustType.Depleting)
            {
                vehicle.ConsumeEnergy(1f);
            }

            // Feel driver
            if (dissipateOnHit && !hasDissipated)
            {
                StartCoroutine(DissipateCloud()); // NEW
            }

            StartCoroutine(ApplyDustEffect(vehicle, impactDir)); // NEW
            if (particleSwellOnHit) SwellParticlesOnce(); // NEW
        }
    }
*/
    private void OnTriggerEnter2D(Collider2D other) {
        if (!other.TryGetComponent<Vehicle>(out var v)) return;
        v.EnterDustField(speedScale, accelScale);  // new in Vehicle.cs (next section)

        // drive the per-behavior feel (lateral nudge, turbulence, cling)
        if (v.TryGetComponent<Rigidbody2D>(out var rb))
        {
            Vector2 dir = rb.linearVelocity.sqrMagnitude > 0.0001f
                ? rb.linearVelocity.normalized
                : (Vector2)(v.transform.position - transform.position).normalized;

            StartCoroutine(ApplyDustEffect(v, dir));  // <- uses your behavior switches
        }

        // Particle puff as a pulse (no accumulation)
        if (cachedPS) StartCoroutine(ParticleSwellPulse(1.5f, 0.3f));
    }

    private void OnTriggerStay2D(Collider2D other)
    {
        if (!other.TryGetComponent<Vehicle>(out var v)) return;
        if (!other.TryGetComponent<Rigidbody2D>(out var rb)) return;

        // Clamp top speed while inside
        float max = v.terminalVelocity * speedScale;
        Vector2 vel = rb.linearVelocity; // used elsewhere in your codebase
        if (vel.magnitude > max)
            rb.linearVelocity = vel.normalized * max; // hard cap while inside

        // Add a gentle continuous counter-force to feel “thick”
        rb.AddForce(-vel.normalized * slowBrake, ForceMode2D.Force);

        // Continuous energy drain for depleting dust
        if (shieldType == CosmicDustType.Depleting)
            v.ConsumeEnergy(Time.deltaTime);
    }

    private void OnTriggerExit2D(Collider2D other)
    {
        if (!other.TryGetComponent<Vehicle>(out var v)) return;

        v.ExitDustField(); // <<< restore env scales

        if (dissipateOnExit)
            BreakHexagon(SoundEffectMood.Friendly);
    }

    private IEnumerator SwellAndFadeSprite() {
        if (baseSprite == null) yield break;
        float t = 0f, dur = 0.25f;
        var start = transform.localScale;
        var end   = start * 1.05f;
        while (t < dur) { t += Time.deltaTime; transform.localScale = Vector3.Lerp(start,end,t/dur); yield return null; }
        t = 0f;
        while (t < dur) { t += Time.deltaTime; transform.localScale = Vector3.Lerp(end,start,t/dur); yield return null; }
    }
    private IEnumerator ParticleSwellPulse(float scale = 1.5f, float dur = 0.3f) {
        if (!cachedPS) yield break;
        var main = particleSystem.main;
        float t = 0f;
        while (t < dur) { // swell
            t += Time.deltaTime;
            float e = Mathf.SmoothStep(0,1,t/dur);
            main.startSizeMultiplier = Mathf.Lerp(baseStartSize, baseStartSize*scale, e);
            yield return null;
        }
        t = 0f;
        while (t < dur) { // relax
            t += Time.deltaTime;
            float e = Mathf.SmoothStep(0,1,t/dur);
            main.startSizeMultiplier = Mathf.Lerp(baseStartSize*scale, baseStartSize, e);
            yield return null;
        }
        main.startSizeMultiplier = baseStartSize; // hard reset
    }

    private IEnumerator ApplyDustEffect(Vehicle vehicle, Vector2 impactDir)
    {
        // Grab RB directly so we don't need Vehicle changes
        if (!vehicle.TryGetComponent<Rigidbody2D>(out var rb)) yield break;

        float t = 0f;
        float dur = slowDuration;
        float minVelFactor = Mathf.Clamp01(slowFactor);

        // Cross-current lateral pulse once at entry
        if (behavior == DustBehavior.CrossCurrent && lateralForce > 0f)
        {
            // perpendicular to motion
            Vector2 v = rb.linearVelocity;
            Vector2 side = new Vector2(-v.y, v.x).normalized;
            rb.AddForce(side * lateralForce, ForceMode2D.Impulse);
        }

        while (t < dur)
        {
            t += Time.deltaTime;
            // Ease: stronger at start, fades out
            float k = 1f - Mathf.SmoothStep(0f, 1f, t / dur);
            float factor = Mathf.Lerp(1f, minVelFactor, k);

            rb.linearVelocity *= factor;

            // Turbulence wobble
            if (behavior == DustBehavior.Turbulent && turbulence > 0f && rb.linearVelocity.sqrMagnitude > 0.01f)
            {
                Vector2 noise = Random.insideUnitCircle.normalized * (turbulence * 0.05f);
                rb.AddForce(noise, ForceMode2D.Force);
            }

            // Static cling = add linear drag temporarily
            if (behavior == DustBehavior.StaticCling)
            {
                rb.AddForce(-rb.linearVelocity * 0.5f * Time.deltaTime, ForceMode2D.Force);
            }

            yield return null;
        }
    }

    private void SwellParticlesOnce()
    {
        if (particleSystem == null) return;
        var main = particleSystem.main;
        float s0 = main.startSizeMultiplier;
        float t0 = main.startLifetimeMultiplier;

        // transient swell
        StartCoroutine(ParticleSwellRoutine(s0, t0));
    }

    private IEnumerator ParticleSwellRoutine(float baseSize, float baseLife)
    {
        var main = particleSystem.main;
        float t = 0f;
        float dur = 0.35f;
        while (t < dur)
        {
            t += Time.deltaTime;
            float e = Mathf.SmoothStep(0f, 1f, t / dur);
            main.startSizeMultiplier = Mathf.Lerp(baseSize, baseSize * 1.6f, e);
            main.startLifetimeMultiplier = Mathf.Lerp(baseLife, baseLife * 1.4f, e);
            yield return null;
        }
        // relax back
        t = 0f;
        while (t < dur)
        {
            t += Time.deltaTime;
            float e = Mathf.SmoothStep(0f, 1f, t / dur);
            main.startSizeMultiplier = Mathf.Lerp(baseSize * 1.6f, baseSize, e);
            main.startLifetimeMultiplier = Mathf.Lerp(baseLife * 1.4f, baseLife, e);
            yield return null;
        }
        main.startSizeMultiplier = baseSize;
        main.startLifetimeMultiplier = baseLife;
    }

    private IEnumerator DissipateCloud()
    {
        hasDissipated = true;
        // stop interacting
        if (TryGetComponent<Collider2D>(out var col)) col.enabled = false;

        // expand + fade out
        Vector3 start = transform.localScale;
        Vector3 end   = start * 1.6f;
        float t = 0f, dur = 0.35f;

        Color c = baseSprite.color;
        float a0 = c.a;

        while (t < dur)
        {
            t += Time.deltaTime;
            float e = t / dur;
            transform.localScale = Vector3.Lerp(start, end, Mathf.SmoothStep(0f,1f,e));
            c.a = Mathf.Lerp(a0, 0f, e);
            baseSprite.color = c;
            yield return null;
        }

        if (particleSystem != null)
        {
            StartCoroutine(WaitForParticlesThenDestroy());
            particleSystem.Stop(true, ParticleSystemStopBehavior.StopEmitting);
        }
        else
        {
            Destroy(gameObject);
        }
    }
    public void SwitchType(CosmicDustType newType)
    {
        switch (newType)
        {
            case CosmicDustType.Friendly:
                shieldType = CosmicDustType.Friendly;
                baseSprite.color = new Color(1f, 1f, 1f, 0.15f);
                break;
           case CosmicDustType.Depleting:
                baseSprite.color = depletingColor;
                shieldType = CosmicDustType.Depleting;
                break;
        }
    }

    public void AbsorbDiamondGhost(Vector2 impactDirection)
    {
        Vector2 worldImpactPoint = transform.position + (Vector3)impactDirection;
        Vector2Int center = drumTrack.WorldToGridPosition(worldImpactPoint);
        if (drumTrack.hexMazeGenerator != null)
        {
            if (CollectionSoundManager.Instance != null)
            {
                StartCoroutine(drumTrack.hexMazeGenerator.BreakSelfThenNeighbors(this, SoundEffectMood.Friendly, center, 2, .2f));
            }
            else
            {
                StartCoroutine(drumTrack.hexMazeGenerator.BreakSelfThenNeighbors(this, SoundEffectMood.Friendly, center, 2, .2f));
            }
        }

        TriggerRippleEffect();
    }


}