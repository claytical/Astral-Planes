using System.Collections;
using UnityEngine;
public enum DustBehaviorType { PushThrough, Stop, Repel }
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
    [Header("Energy Drain (Deterrent)")] 
    [Tooltip("Energy drained per second while the vehicle remains inside this dust (when not boosting).")] 
    [Min(0f)] public float energyDrainPerSecond = .25f;
    [Tooltip("If true, boosting will NOT drain energy (boosting is the 'broom' mechanic).")] 
    public bool noDrainWhileBoosting = true;
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
    [SerializeField] private float fadeSeconds = 0.25f;
    [SerializeField] private Vector3 fullScale = Vector3.one; // set in Awake/Begin()
    [SerializeField] private Collider2D hitbox;
    private float _uiBottomY = float.NaN;
    [SerializeField] private int solidLayer = 0;       // Default or your "Dust" layer
    [SerializeField] private int nonBlockingLayer = 2; // Ignore Raycast or a custom non-blocking layer

    private bool _isDespawned;
    private bool _shrinkingFromStar;
    private Vector3 _velocity, _baseScale, _accomodationTarget;
    private Coroutine _accomodateRoutine, _regrowRoutine, _fadeRoutine, _growInRoutine;
    private DrumTrack _drumTrack;
    private float _growInOverride = -1f;
    private Color _currentTint = Color.white;
    [SerializeField] private int epochId;
    [SerializeField] private float stayForceEvery = 0.05f; // seconds
    private float _stayForceUntil;
    private float _phaseDrainPerSecond = 1f;

    private static class DustDestroyStats
    {
        public static int ManualBreaks, StarPrunes, VehicleEats, Lifetimes, Debugs;
    }
    
    public CosmicDust(ParticleSystem particleSystem)
    {
        this.particleSystem = particleSystem;
    }
    private float UiBottomY()
    {
        if (!float.IsNaN(_uiBottomY)) return _uiBottomY;
        var gfm = GameFlowManager.Instance;
        if (gfm && gfm.controller && gfm.controller.noteVisualizer != null)
        {
            _uiBottomY = gfm.activeDrumTrack.GetDustBandBottomCenterY();
            Debug.Log($"[GRID DUST] UI bottom using top Y: {_uiBottomY}");
        }
        else
        { 
            _uiBottomY = Camera.main.ViewportToWorldPoint(new Vector3(0, 0, -Camera.main.transform.position.z)).y; // fallback
            Debug.Log($"[GRID DUST] UI bottom using z Y: {_uiBottomY}");
            
        }
        return _uiBottomY;
    }

    void Awake() {
        // If the prefab/instance was saved at scale 0, use a sane fallback.
        if (transform.localScale.sqrMagnitude < 1e-6f)
            transform.localScale = referenceScale;

        // OLD (causes overlap):
        // fullScale = transform.localScale * 2f; // your intended “final size”

        // NEW: keep dust at the same scale the grid used to compute tileDiameterWorld
        fullScale = transform.localScale;
        _accomodationTarget = fullScale;
        _phaseDrainPerSecond = energyDrainPerSecond;
        // Belt-and-suspenders: make sure SpriteMask can’t clip us accidentally
        var psr = GetComponent<ParticleSystemRenderer>();
        if (psr) psr.maskInteraction = SpriteMaskInteraction.None;
        if (psr) psr.sortingFudge = 0f;
    }

    public float GetWorldRadius()
    {
        if (!hitbox)
            return 0.5f; // sane fallback

        // Prefer CircleCollider2D if that’s what hitbox actually is.
        if (hitbox is CircleCollider2D circle)
        {
            // Assume uniform scale on X/Y; lossyScale.x is fine.
            return circle.radius * Mathf.Abs(transform.lossyScale.x);
        }

        // Fallback for non-circle colliders: approximate from bounds.
        var bounds = hitbox.bounds;
        // Use the larger extent to be safe.
        return Mathf.Max(bounds.extents.x, bounds.extents.y);
    }

    public void OnSpawnedFromPool(Color tint)
    {
        // Visual reset
        SetTint(tint);
        transform.localScale = fullScale;
        // Physics reset
        gameObject.layer = solidLayer;
        if (hitbox) hitbox.enabled = true;
    }

    public void Begin()
    {
        SetColorVariance();
        transform.localScale = Vector3.zero;
        _growInRoutine = StartCoroutine(GrowIn());
        if (particleSystem)
        {
            StartCoroutine(ParticleAlphaFadeIn(0.5f));
            particleSystem.Clear(true);
            particleSystem.Play(true);
        }
    }

    void LateUpdate()
    {
        var gen = GameFlowManager.Instance?.dustGenerator;
        if (gen == null) return;

        Vector2 flow = gen.SampleFlowAtWorld(transform.position);
        flow.y = 0f;
        if (flow.sqrMagnitude > 0.00001f)
            transform.position += (Vector3)(flow * Time.deltaTime);

        var t   = transform;
        var cam = Camera.main;
        if (!cam) return;

        var dt = GameFlowManager.Instance?.activeDrumTrack;
        if (dt == null) return;

        var playArea = dt.GetPlayAreaWorld();
        var pos      = t.position;

        float pad = dt.gridPadding;
        float bottomLimit = playArea.bottom + pad;
        float topLimit    = playArea.top    - pad;

        pos.y = Mathf.Clamp(pos.y, bottomLimit, topLimit);
        t.position = pos;
    }
    private void DisableInteractionImmediately() { 
        // Stop affecting vehicles instantly (even if particles linger)
        if (hitbox) hitbox.enabled = false; 
        var col = GetComponent<Collider2D>(); 
        if (col) col.enabled = false; 
        gameObject.layer = nonBlockingLayer;
    }
    private IEnumerator GrowIn() {
        float duration = (_growInOverride > 0f)
            ? _growInOverride
            : Random.Range(5f, 20f); // keep your old fallback if override not set

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
        yield return new WaitForSeconds(0.2f); // delay long enough for Explode effect
        GameFlowManager.Instance.dustGenerator.TriggerRegrowth(gridPos, GameFlowManager.Instance.phaseTransitionManager.currentPhase);
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
    // One switch → one config object → one assignment block.
    var cfg = phase switch
    {
        MusicalPhase.Establish => new PhaseDustConfig(
            scaleMul:   0.25f,
            drainMul:   0.50f,
            behavior:   DustBehavior.SiltDissipate,
            slowFactor: 0.8f,
            slowDur:    0.25f,
            lateral:    0f,
            turb:       0f
        ),

        MusicalPhase.Evolve => new PhaseDustConfig(
            scaleMul:   1.00f,
            drainMul:   1.00f,
            behavior:   DustBehavior.CrossCurrent,
            slowFactor: 0.9f,
            slowDur:    0.2f,
            lateral:    2.5f,
            turb:       0.25f
        ),

        MusicalPhase.Intensify => new PhaseDustConfig(
            scaleMul:   1.20f,
            drainMul:   1.60f,
            behavior:   DustBehavior.StaticCling,
            slowFactor: 0.5f,
            slowDur:    0.5f,
            lateral:    0.5f,
            turb:       0.4f
        ),

        MusicalPhase.Release => new PhaseDustConfig(
            scaleMul:   1.00f,
            drainMul:   0.90f,
            behavior:   DustBehavior.SiltDissipate,
            slowFactor: 0.85f,
            slowDur:    0.2f,
            lateral:    0f,
            turb:       0f
        ),

        MusicalPhase.Wildcard => new PhaseDustConfig(
            scaleMul:   1.10f,
            drainMul:   1.25f,
            behavior:   DustBehavior.Turbulent,
            slowFactor: 0.7f,
            slowDur:    0.4f,
            lateral:    1.2f,
            turb:       2.0f
        ),

        MusicalPhase.Pop => new PhaseDustConfig(
            scaleMul:   0.95f,
            drainMul:   0.75f,
            behavior:   DustBehavior.ViscousSlow,
            slowFactor: 0.6f,
            slowDur:    0.3f,
            lateral:    0f,
            turb:       0.2f
        ),

        _ => new PhaseDustConfig(
            scaleMul:   1.0f,
            drainMul:   1.0f,
            behavior:   DustBehavior.SiltDissipate,
            slowFactor: 0.85f,
            slowDur:    0.2f,
            lateral:    0f,
            turb:       0f
        )
    };

    // Scale
    fullScale = referenceScale * cfg.scaleMul;

    // Drain (per-phase)
    _phaseDrainPerSecond = energyDrainPerSecond * cfg.drainMul;

    // Motion / feel
    behavior       = cfg.behavior;
    slowFactor     = cfg.slowFactor;
    slowDuration   = cfg.slowDur;
    lateralForce   = cfg.lateral;
    turbulence     = cfg.turb;
}

// Keep this inside CosmicDust (e.g., near ConfigureForPhase).
private readonly struct PhaseDustConfig
{
    public readonly float scaleMul;
    public readonly float drainMul;
    public readonly DustBehavior behavior;
    public readonly float slowFactor;
    public readonly float slowDur;
    public readonly float lateral;
    public readonly float turb;

    public PhaseDustConfig(
        float scaleMul,
        float drainMul,
        DustBehavior behavior,
        float slowFactor,
        float slowDur,
        float lateral,
        float turb)
    {
        this.scaleMul   = scaleMul;
        this.drainMul   = drainMul;
        this.behavior   = behavior;
        this.slowFactor = slowFactor;
        this.slowDur    = slowDur;
        this.lateral    = lateral;
        this.turb       = turb;
    }
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

    void SetColorVariance()
    {
        var c = _currentTint;
        float variation = Random.Range(-.02f, .02f);
        c.r = Mathf.Clamp01(c.r + variation);
        c.g = Mathf.Clamp01(c.g + variation);
        c.b = Mathf.Clamp01(c.b + variation);
        SetTint(c);
    }
    public void SetGrowInDuration(float seconds) { _growInOverride = Mathf.Max(0.05f, seconds); }
    public void SetDrumTrack(DrumTrack track)
    {
        _drumTrack = track;
    }

    public void SetTint(Color tint)
    {
        if (!particleSystem) return;

        // Keep the caller's alpha; ParticleAlphaFadeIn will animate it when needed.
        _currentTint = tint;

        var main = particleSystem.main;
        main.startColor = tint;

        // If you want a subtle lifetime fade, set it once; otherwise disable it for solid visibility
        var col = particleSystem.colorOverLifetime;
        col.enabled = true;

        // Build a *visible* gradient (soft in/out), not near-zero most of the time.
        var grad = new Gradient();
        grad.SetKeys(
            new[]
            {
                new GradientColorKey(new Color(tint.r, tint.g, tint.b), 0f),
                new GradientColorKey(new Color(tint.r, tint.g, tint.b), 1f),
            },
            new[]
            {
                new GradientAlphaKey(0f,   0f),   // quick fade-on
                new GradientAlphaKey(tint.a, 0.1f),
                new GradientAlphaKey(tint.a, 0.9f),
                new GradientAlphaKey(0f,   1f),   // quick fade-off
            }
        );
        col.color = new ParticleSystem.MinMaxGradient(grad);
    }


public IEnumerator RetintOver(float seconds, Color toTint)
{
    if (particleSystem == null) yield break;

    var main = particleSystem.main;
    Color from = (main.startColor.mode == ParticleSystemGradientMode.Color)
        ? main.startColor.color
        : Color.white;

    float t = 0f;
    while (t < seconds)
    {
        t += Time.deltaTime;
        float u = Mathf.SmoothStep(0f, 1f, t / Mathf.Max(0.0001f, seconds));
        Color now = Color.Lerp(from, toTint, u);
        // Apply each step (affects new and in-flight particles)
        SetTint(now);
        yield return null;
    }
    SetTint(toTint);
}

// --- Utilities ---


    public void StartFadeAndScaleDown(float duration)
    {
        if (_fadeRoutine != null) StopCoroutine(_fadeRoutine);
        _fadeRoutine = StartCoroutine(ParticleFadeAndScaleDown(duration));
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
    private IEnumerator ParticleAlphaFadeIn(float duration) { 
        if (particleSystem == null) yield break; 
        // fade alpha from 0 → 1 by rebuilding startColor each step
        float t = 0f; 
        Color from = _currentTint; from.a = 0f; 
        Color to   = _currentTint; to.a = .2f; 
        while (t < duration) { 
            t += Time.deltaTime; 
            float u = Mathf.SmoothStep(0f, 1f, t / Mathf.Max(0.0001f, duration)); 
            var now = Color.Lerp(from, to, u); 
            SetTint(now); 
            yield return null;
        } 
        SetTint(to);
    }
    private IEnumerator ParticleFadeAndScaleDown(float duration) { 
        float t = 0f; 
        Vector3 startScale = transform.localScale; 
        Color from = _currentTint; Color to = _currentTint; to.a = 0f; 
        while (t < duration) { 
            t += Time.deltaTime; 
            float u = Mathf.SmoothStep(0f, 1f, t / duration);
            transform.localScale = Vector3.Lerp(startScale, Vector3.zero, u); 
            // fade particle tint alpha
            SetTint(Color.Lerp(from, to, u)); 
            yield return null; 
        } 
        // ensure invisible & tiny
        transform.localScale = Vector3.zero; 
        SetTint(to); 
        // hand off to generator to return to pool
        var dt = GameFlowManager.Instance?.activeDrumTrack; 
        if (dt != null) { 
            var gridPos = dt.WorldToGridPosition(transform.position); 
            GameFlowManager.Instance.dustGenerator.DespawnDustAt(gridPos); 
        }
        else { gameObject.SetActive(false); } 
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
            yield return null;
        }
        transform.localScale = target;
    }

    private void BreakHexagon()
    {
        CollectionSoundManager.Instance?.PlayEffect(SoundEffectPreset.Dust);
        if (_drumTrack != null)
        {
            Vector2Int gridPos = _drumTrack.WorldToGridPosition(transform.position);
            _drumTrack.FreeSpawnCell(gridPos.x, gridPos.y);
            GameFlowManager.Instance.dustGenerator.DespawnDustAt(gridPos);
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

    public void DestroyFromPhaseStar()
    {
        DustDestroyStats.StarPrunes++;
        // One path only: fade then POOL (not Destroy)
        StartCoroutine(FadeAndPool(strength01: 1f));
    }
    private IEnumerator FadeAndPool(float strength01)
    {
        if (_isDespawned) yield break; // guard against double calls
        _isDespawned = true;
        DisableInteractionImmediately();
        // 2) Free cell NOW (don’t wait until after the fade)
        var dt = GameFlowManager.Instance?.activeDrumTrack;
        Vector2Int gridPos = default;
        bool hadGrid = false;
        if (dt != null)
        {
            gridPos = dt.WorldToGridPosition(transform.position);
            //dt.FreeSpawnCell(gridPos.x, gridPos.y); // free occupancy immediately
            hadGrid = true;
        }

        // 3) Visual-only fade
        float dur = Mathf.Lerp(fadeSeconds * 0.3f, fadeSeconds, Mathf.Clamp01(strength01));
        Vector3 s0 = transform.localScale.sqrMagnitude < 1e-6f ? fullScale : transform.localScale;
        float t = 0f;
        while (t < dur)
        {
            t += Time.deltaTime;
            float u = Mathf.Clamp01(t / dur);
            transform.localScale = Vector3.Lerp(s0, Vector3.zero, u);
            yield return null;
        }

        // 4) Tell generator it can forget this tile (idempotent)
        if (hadGrid)
            GameFlowManager.Instance.dustGenerator.DespawnDustAt(gridPos);

        // 5) Return to pool
        DespawnToPoolInstant();
    }

    public void PrepareForReuse()
    {
        // Stop any lingering coroutines from prior life
        if (_accomodateRoutine != null) { StopCoroutine(_accomodateRoutine); _accomodateRoutine = null; }
        if (_regrowRoutine     != null) { StopCoroutine(_regrowRoutine);     _regrowRoutine     = null; }
        if (_fadeRoutine       != null) { StopCoroutine(_fadeRoutine);       _fadeRoutine       = null; }
        if (_growInRoutine     != null) { StopCoroutine(_growInRoutine);     _growInRoutine     = null; }

        _shrinkingFromStar = false;
        _accomodationTarget = fullScale = referenceScale;
        transform.localScale = referenceScale;

        if (particleSystem != null)
        {
            particleSystem.Clear(true);
            particleSystem.Play(true);
        }
        _currentTint.a = .5f;
        SetTint(_currentTint);
        var col = GetComponent<Collider2D>();
        if (col) col.enabled = true;

        // reset any per-dust flags your logic uses
        dissipateOnExit = false;
    }

    public void DespawnToPoolInstant()
    { 
        DisableInteractionImmediately();
        // If you do NOT want lingering particles when pooled:
        if (particleSystem != null) { 
            particleSystem.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear); 
            particleSystem.Clear(true);
        }
        var cHide = _currentTint; cHide.a = 0f;
        SetTint(cHide);
        transform.localScale = Vector3.zero;

        _isDespawned = true;
        gameObject.SetActive(false);
    }

    private IEnumerator WaitForParticlesThenDestroy()
    {
        DisableInteractionImmediately();
        particleSystem.Stop(true, ParticleSystemStopBehavior.StopEmitting);

        // Wait until all particles have disappeared
        while (particleSystem.IsAlive(true))
        {
            yield return null;
        }
        var dt = GameFlowManager.Instance?.activeDrumTrack;
        if (dt != null) { 
            var gridPos = dt.WorldToGridPosition(transform.position); 
            GameFlowManager.Instance.dustGenerator.DespawnDustAt(gridPos);
        }
        else { gameObject.SetActive(false); }
    }
    private void OnTriggerEnter2D(Collider2D other) {
        if (!other.TryGetComponent<Vehicle>(out var v)) return;
        v.EnterDustField(speedScale, accelScale);  

        // drive the per-behavior feel (lateral nudge, turbulence, cling)
        if (v.TryGetComponent<Rigidbody2D>(out var rb))
        {
            Vector2 dir = rb.linearVelocity.sqrMagnitude > 0.0001f
                ? rb.linearVelocity.normalized
                : (v.transform.position - transform.position).normalized;

            StartCoroutine(ApplyDustEffect(v, dir));  // <- uses your behavior switches
        }
        
        AccommodateToVehicle();
    }
    private void OnTriggerStay2D(Collider2D other)
    {
        if (!other.TryGetComponent<Vehicle>(out var v)) return;

        // 1. ENERGY DRAIN — every physics step

        float drain = energyDrainPerSecond * Time.deltaTime;
        if (drain > 0f)
        {
            v.DrainEnergy(drain, $"CosmicDust/{behavior}");
        }

        // 2. BOOST CLEAR (optional)
        if (v.boosting)
        {
            BreakHexagon();
            return;
        }

        // 3. THROTTLED PHYSICS EFFECTS
        if (Time.time < _stayForceUntil) return;
        _stayForceUntil = Time.time + stayForceEvery;

        if (!other.TryGetComponent<Rigidbody2D>(out var rb)) return;

        float max = v.GetMaxSpeed() * speedScale;
        if (rb.linearVelocity.magnitude > max)
            rb.linearVelocity = rb.linearVelocity.normalized * max;
    }

    private void OnTriggerExit2D(Collider2D other)
    {
        if (!other.TryGetComponent<Vehicle>(out var v)) return;

        v.ExitDustField(); // <<< restore env scales

        if (dissipateOnExit)
            BreakHexagon();
    }
    
}