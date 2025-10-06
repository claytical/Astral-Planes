using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class CosmicDust : MonoBehaviour
{
    [Header("Sizing (safe fallback)")]
    public Vector3 referenceScale = new Vector3(0.75f, 0.75f, 1f); // <- tweak to your taste
    public ParticleSystem particleSystem;
    // === PhaseStar interaction ===

    [Header("PhaseStar Proximity")]
    public bool starRemovesWithoutRegrow;   // if false, it will regrow via generator
    public float starAlphaFadeBias = 0.9f;         // keep a little glow as it shrinks
    
    [Header("Dust Field")]
    [Range(0f,1.5f)] public float slowBrake = 0.4f;   // extra braking force
    [Range(0.4f, 1f)] public float speedScale = 0.8f;   // cap scale
    [Range(0.4f, 1f)] public float accelScale = 0.8f;   // thrust scale
    public enum DustBehavior { ViscousSlow, SiltDissipate, StaticCling, CrossCurrent, Turbulent }
    public bool dissipateOnExit;

    [Header("Accommodation (vehicle)")]
    [Range(0.3f, 1f)] public float minScaleFactor = 0.55f;   // never shrink below 55% of full
    [Range(0f, 0.5f)] public float shrinkStep = 0.12f;        // how much each ENTRY tries to shave off
    [Range(0.05f, 1.5f)] public float shrinkEaseTime = 0.25f; // ease time to reach the new target
    [Range(0f, 3f)] public float regrowDelay = 0.6f;          // wait before regrowing
    [Range(0.1f, 6f)] public float regrowTime = 2.0f;         // ease time back to full

    [Header("Behavior")]
    public DustBehavior behavior = DustBehavior.ViscousSlow; // NEW
    [Range(0.2f,1f)] public float slowFactor = 0.7f;         // NEW (velocity multiplier while inside)
    [Range(0f,2f)]   public float slowDuration = 0.35f;       // NEW (seconds)
    [Range(0f,10f)]  public float lateralForce = 2.0f;        // NEW (CrossCurrent)
    [Range(0f,10f)]  public float turbulence = 0.0f;          // NEW (Wildcard micro-deflection)
    public SpriteRenderer baseSprite;
    [SerializeField] private float fadeSeconds = 0.25f;
    [SerializeField] private Vector3 fullScale = Vector3.one; // set in Awake/Begin()

    private bool _shrinkingFromStar;
    private Vector3 _velocity, _baseScale, _accomodationTarget;
    private Coroutine _accomodateRoutine, _regrowRoutine, _fadeRoutine, _growInRoutine;
    private SpriteRenderer _sr, _cachedSr;
    private List<SpriteRenderer> _childSRs;
    private DrumTrack _drumTrack;
    private float _originalAlpha, _growInOverride = -1f;
    [SerializeField] private int epochId;

    private static class DustDestroyStats
    {
        public static int ManualBreaks, StarPrunes, VehicleEats, Lifetimes, Debugs;
    }
    
    public CosmicDust(ParticleSystem particleSystem)
    {
        this.particleSystem = particleSystem;
    }
    void Awake() {
        if (_cachedSr == null) _cachedSr = GetComponent<SpriteRenderer>();
        if (_childSRs == null)
            _childSRs = new List<SpriteRenderer>(GetComponentsInChildren<SpriteRenderer>(includeInactive: true));

        // If the prefab/instance was saved at scale 0, use a sane fallback.
        if (transform.localScale.sqrMagnitude < 1e-6f)
            transform.localScale = referenceScale;

        fullScale = transform.localScale * 2f; // your intended “final size”
        _accomodationTarget = fullScale;

    }
    public void Begin()
    {
        SetColorVariance();
        // ✅ Start alpha fade-in
        if (baseSprite != null)
        {
            Color c = baseSprite.color;
            _originalAlpha = c.a;
            c.a = 0f;
            baseSprite.color = c;
        }
        transform.localScale = Vector3.zero;
        _growInRoutine = StartCoroutine(GrowIn());
        StartCoroutine(FadeInAlpha(targetAlpha: _originalAlpha));

    }
    private IEnumerator GrowIn() {
        float duration = (_growInOverride > 0f)
            ? _growInOverride
            : Random.Range(5f, 20f); // keep your old fallback if override not set
        Debug.Log($"[DUST] GrowIn duration={duration:0.00}s for {name}");

        float t = 0f;
        while (t < duration) {
            t += Time.deltaTime;
            float s = Mathf.SmoothStep(0f, 1f, t / duration);
            transform.localScale = fullScale * s;
            yield return null;
        }
        
        transform.localScale = fullScale;
    }
    private IEnumerator TriggerDelayedRegrowth(Vector2Int gridPos)
    {
        yield return new WaitForSeconds(0.5f); // delay long enough for Explode effect
        GameFlowManager.Instance.activeDrumTrack.hexMazeGenerator.TriggerRegrowth(gridPos, GameFlowManager.Instance.phaseTransitionManager.currentPhase);
    }
    private IEnumerator RegrowAfterDelay()
    {
        yield return new WaitForSeconds(regrowDelay);
        // If a later entry pushed the target even smaller, wait for that ease to finish
        if (_accomodateRoutine != null) yield return _accomodateRoutine;

        // Ease back up to full
        yield return ScaleTo(fullScale, regrowTime);
        _regrowRoutine = null;
    }

    public void ConfigureForPhase(MusicalPhase phase)
    {
        float s = phase switch {
            MusicalPhase.Establish  => 0.85f,
            MusicalPhase.Evolve     => 1.00f,
            MusicalPhase.Intensify  => 1.20f,
            MusicalPhase.Release    => 1.00f,
            MusicalPhase.Wildcard   => 1.10f,
            MusicalPhase.Pop        => 0.95f,
            _ => 1.0f
        };
        fullScale = referenceScale * s; // existing field
        switch (phase)
        {
            case MusicalPhase.Establish:
                behavior = DustBehavior.ViscousSlow;
                slowFactor = 0.8f; slowDuration = 0.25f;
                lateralForce = 0f; turbulence = 0f;
                
                break;
            case MusicalPhase.Evolve:
                behavior = DustBehavior.CrossCurrent;
                slowFactor = 0.9f; slowDuration = 0.2f;
                lateralForce = 2.5f; turbulence = 0.25f; 
                
                break;
            case MusicalPhase.Intensify:
                behavior = DustBehavior.StaticCling;
                slowFactor = 0.5f; slowDuration = 0.5f;
                lateralForce = 0.5f; turbulence = 0.4f; 
                
                break;
            case MusicalPhase.Release:
                behavior = DustBehavior.SiltDissipate;
                slowFactor = 0.85f; slowDuration = 0.2f;
                lateralForce = 0f; turbulence = 0f; 
                
                break;
            case MusicalPhase.Wildcard:
                behavior = DustBehavior.Turbulent;
                slowFactor = 0.7f; slowDuration = 0.4f;
                lateralForce = 1.2f; turbulence = 2.0f;
                
                break;
            case MusicalPhase.Pop:
                behavior = DustBehavior.ViscousSlow;
                slowFactor = 0.6f; slowDuration = 0.3f;
                lateralForce = 0f; turbulence = 0.2f;
                
                break;
        }
        Debug.Log($"Chose Behavior: {behavior}");
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
                rb.AddForce(-rb.linearVelocity * (0.5f * Time.deltaTime), ForceMode2D.Force);
            }

            yield return null;
        }
    }

    public void SetEpoch(int id) => epochId = id;
    public int GetEpoch() => epochId;
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
    public void SetGrowInDuration(float seconds) { _growInOverride = Mathf.Max(0.05f, seconds); }
    public void SetDrumTrack(DrumTrack track)
    {
        _drumTrack = track;
    }
    public void SetColor(Color color)
    {
        baseSprite.color = color;
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
            baseSprite.color = phaseColor;
            SetParticleColor(phaseColor);
        }

    }
    private void SetParticleColor(Color c)
    {
        if (particleSystem == null) return;

        var main = particleSystem.main;
        main.startColor = new ParticleSystem.MinMaxGradient(c);
    }

    private void AccommodateToVehicle()
    {
        // If PhaseStar is actively shrinking this hex, don't fight it.
        if (_shrinkingFromStar) return;

        // Cancel a pending regrow so we can stack multiple entries sanely
        if (_regrowRoutine != null) { StopCoroutine(_regrowRoutine); _regrowRoutine = null; }

        // Compute a new target factor: current factor minus a step, clamped to a floor
        float currentFactor = Mathf.Clamp01(transform.localScale.x / Mathf.Max(0.0001f, fullScale.x));
        float targetFactor  = Mathf.Max(minScaleFactor, currentFactor - shrinkStep);
        _accomodationTarget  = fullScale * targetFactor;

        // Ease down to the new target
        if (_accomodateRoutine != null) StopCoroutine(_accomodateRoutine);
        _accomodateRoutine = StartCoroutine(ScaleTo(_accomodationTarget, shrinkEaseTime));

        // Schedule a smooth regrow after a delay
        _regrowRoutine = StartCoroutine(RegrowAfterDelay());
    }

    private IEnumerator FadeInAlpha(float targetAlpha)
    {
        if (baseSprite == null) yield break;
        float duration = 0.5f;
        float t = 0f;

        Color color = baseSprite.color;
        _originalAlpha = targetAlpha;
        float finalAlpha = _originalAlpha;// store the intended final alpha
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
    public void StartFadeAndScaleDown(float duration)
    {
        if (_fadeRoutine != null) StopCoroutine(_fadeRoutine);
        _fadeRoutine = StartCoroutine(FadeAndScaleDown(duration));
    }
    private IEnumerator FadeAndScaleDown(float duration)
    {
        // Cancel any ongoing growth/shrink coroutines here if you keep handles to them.
        // e.g., if (growRoutine != null) { StopCoroutine(growRoutine); growRoutine = null; }

        float t = 0f;
        Vector3 startScale = transform.localScale;
        Color[] startColors = null;

        // Collect all renderers to fade (self + children)
        var renderers = new List<SpriteRenderer>();
        if (_cachedSr != null) renderers.Add(_cachedSr);
        if (_childSRs != null) renderers.AddRange(_childSRs);

        if (renderers.Count > 0)
        {
            startColors = new Color[renderers.Count];
            for (int i = 0; i < renderers.Count; i++)
                if (renderers[i] != null) startColors[i] = renderers[i].color;
        }

        while (t < duration)
        {
            t += Time.deltaTime;
            float u = Mathf.SmoothStep(0f, 1f, t / duration);

            // Scale down to zero
            transform.localScale = Vector3.Lerp(startScale, Vector3.zero, u);

            // Fade alpha
            if (renderers.Count > 0)
            {
                for (int i = 0; i < renderers.Count; i++)
                {
                    var sr = renderers[i];
                    if (sr == null) continue;
                    if (startColors != null)
                    {
                        Color c = startColors[i];
                        c.a = Mathf.Lerp(startColors[i].a, 0f, u);
                        sr.color = c;
                    }
                }
            }

            yield return null;
        }

        // Ensure we end invisible and tiny
        transform.localScale = Vector3.zero;
        if (renderers.Count > 0)
        {
            for (int i = 0; i < renderers.Count; i++)
            {
                var sr = renderers[i];
                if (sr == null) continue;
                var c = sr.color; c.a = 0f; sr.color = c;
            }
        }

        // Remove the object
        Destroy(gameObject);
    }

    private IEnumerator ScaleTo(Vector3 target, float duration)
    {
        Vector3 start = transform.localScale;
        float t = 0f;
        while (t < duration)
        {
            t += Time.deltaTime;
            float e = Mathf.SmoothStep(0f, 1f, t / duration);
            transform.localScale = Vector3.Lerp(start, target, e);
            UpdateSpriteAlphaByScale();
            yield return null;
        }
        transform.localScale = target;
        UpdateSpriteAlphaByScale();
    }
    private void UpdateSpriteAlphaByScale()
    {
        if (baseSprite == null || fullScale.x <= 0.0001f) return;
        float t = Mathf.Clamp01(transform.localScale.x / fullScale.x);
        // Match the PhaseStar fade logic feel, but keep a small floor so it’s visible.
        Color c = baseSprite.color;
        c.a = Mathf.Lerp(0.1f, _originalAlpha, t); // 0.1 floor prevents “invisible walls”
        baseSprite.color = c;
    }

    public void ShrinkByPhaseStar(float unitsPerSecond)
{
    if (_shrinkingFromStar) { DoShrink(unitsPerSecond); return; }

    // first entry: cancel any ongoing grow-in so it doesn't fight the shrink
    if (_growInRoutine != null) { StopCoroutine(_growInRoutine); _growInRoutine = null; }
    _shrinkingFromStar = true;

    DoShrink(unitsPerSecond);
}
    private void DoShrink(float unitsPerSecond) {
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
        c.a = Mathf.Lerp(0f, _originalAlpha * starAlphaFadeBias, t);
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
    
    private void BreakHexagon()
    {
        CollectionSoundManager.Instance?.PlayEffect(SoundEffectPreset.Dust);
        if (_drumTrack != null)
        {
            Vector2Int gridPos = _drumTrack.WorldToGridPosition(transform.position);
            _drumTrack.FreeSpawnCell(gridPos.x, gridPos.y);
            _drumTrack.hexMazeGenerator.RemoveHex(gridPos);
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

    private void DestroyFromPhaseStar()
{
    DustDestroyStats.StarPrunes++;
    StartCoroutine(FadeAndDestroy(1));
    // free cell + remove from generator map, just like BreakHexagon
    if (_drumTrack != null)
    {
        Vector2Int gridPos = _drumTrack.WorldToGridPosition(transform.position);
        _drumTrack.FreeSpawnCell(gridPos.x, gridPos.y);                              // frees grid slot
        _drumTrack.hexMazeGenerator.RemoveHex(gridPos);                              // remove map entry
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
    private IEnumerator FadeAndDestroy(float strength01)
    {
        // strength01 lets closer tiles fade faster (optional)
        float dur = Mathf.Lerp(fadeSeconds * 0.3f, fadeSeconds, Mathf.Clamp01(strength01));

        // snapshot start
        Color c0 = baseSprite ? baseSprite.color : Color.white;
        Vector3 s0 = transform.localScale;
        if (s0.sqrMagnitude < 1e-6f) s0 = fullScale; // safety if spawned at 0

        float t = 0f;
        while (t < dur)
        {
            t += Time.deltaTime;
            float u = Mathf.Clamp01(t / dur);

            if (baseSprite)
            {
                var c = c0; c.a = Mathf.Lerp(c0.a, 0f, u);
                baseSprite.color = c;
            }
            transform.localScale = Vector3.Lerp(s0, Vector3.zero, u);
            yield return null;
        }

        Destroy(gameObject);
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

    
    private void OnTriggerEnter2D(Collider2D other) {
        if (!other.TryGetComponent<Vehicle>(out var v)) return;
        v.EnterDustField(speedScale, accelScale);  // new in Vehicle.cs (next section)

        // drive the per-behavior feel (lateral nudge, turbulence, cling)
        if (v.TryGetComponent<Rigidbody2D>(out var rb))
        {
            Vector2 dir = rb.linearVelocity.sqrMagnitude > 0.0001f
                ? rb.linearVelocity.normalized
                : (v.transform.position - transform.position).normalized;

            StartCoroutine(ApplyDustEffect(v, dir));  // <- uses your behavior switches
        }
        Debug.Log($"Accomodating to Vehicle: {v}");
        AccommodateToVehicle();
        // Particle puff as a pulse (no accumulation)
//        if (cachedPS) StartCoroutine(ParticleSwellPulse(1.5f, 0.3f));
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
    }
    private void OnTriggerExit2D(Collider2D other)
    {
        if (!other.TryGetComponent<Vehicle>(out var v)) return;

        v.ExitDustField(); // <<< restore env scales

        if (dissipateOnExit)
            BreakHexagon();
    }
    
}