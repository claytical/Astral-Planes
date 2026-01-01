using System.Collections;
using UnityEngine;
public enum DustBehaviorType { PushThrough, Stop, Repel }
public enum DustClearKind { Temporary, Permanent }

public class CosmicDust : MonoBehaviour
{
    [Header("Sizing (safe fallback)")]
    public Vector3 referenceScale = new Vector3(0.75f, 0.75f, 1f); // <- tweak to your taste
    public ParticleSystem particleSystem;
    // === PhaseStar interaction ===

    [Header("PhaseStar Proximity")]
    public bool starRemovesWithoutRegrow;   // if false, it will regrow via generator
    public float starAlphaFadeBias = 0.9f;         // keep a little glow as it shrinks
    [Header("Imprint (from MineNodes)")] 
    [Range(0f, 1f)] 
    public float hardness01 = 0f; // 0 = soft/easy, 1 = hard/needs more boost (semantic only for now)

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
    [Header("Dust Clearing")]
    [Tooltip("Seconds of sustained non-boost contact before this dust tile breaks (temporary clear). Higher = more wall-like.")]
    [Min(0.05f)] public float nonBoostSecondsToBreak = 2.5f;

    [Tooltip("Override delay (seconds) before temporary regrow into the SAME cell. -1 uses the phase default.")]
    public float temporaryRegrowDelaySeconds = -1f;

    private float _nonBoostClearSeconds;

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
    [SerializeField] private Collider2D terrainCollider;
    [SerializeField] private int solidLayer = 0;       // Default or your "Dust" layer
    [SerializeField] private int nonBlockingLayer = 2; // Ignore Raycast or a custom non-blocking layer

    private bool _isDespawned;
    private bool _shrinkingFromStar;
    private Vector3 _velocity, _baseScale, _accomodationTarget;
    private Coroutine _accomodateRoutine, _regrowRoutine, _fadeRoutine, _growInRoutine;
    private DrumTrack _drumTrack;
    private CosmicDustGenerator gen;
    private MusicalPhase phase;
    private float _growInOverride = -1f;
    private Color _currentTint = Color.white;
    [SerializeField] private int epochId;
    [SerializeField] private float stayForceEvery = 0.05f; // seconds
    private float _stayForceUntil;
    private float _phaseDrainPerSecond = 1f;
    private bool _isBreaking;


    public CosmicDust(ParticleSystem particleSystem)
    {
        this.particleSystem = particleSystem;
    }

    void Awake() {
        // Terrain collider lives on this same prefab (PolygonCollider2D recommended).
        // It contributes to the CompositeCollider2D on the DustPool root.
        if (terrainCollider == null) terrainCollider = GetComponent<Collider2D>();
        if (terrainCollider != null) terrainCollider.isTrigger = false;

        // If the prefab/instance was saved at scale 0, use a sane fallback.
        if (transform.localScale.sqrMagnitude < 1e-6f)
            transform.localScale = referenceScale;

        fullScale = transform.localScale;
        _accomodationTarget = fullScale;
        _phaseDrainPerSecond = energyDrainPerSecond;
        // Belt-and-suspenders: make sure SpriteMask can’t clip us accidentally
        var psr = GetComponent<ParticleSystemRenderer>();
        if (psr) psr.maskInteraction = SpriteMaskInteraction.None;
        if (psr) psr.sortingFudge = 0f;
    }
private void OnCollisionStay2D(Collision2D collision)
{
    if (_isDespawned || _isBreaking) return;
    if (gen == null || _drumTrack == null) return;

    var vehicle = collision.collider != null ? collision.collider.GetComponent<Vehicle>() : null;
    if (vehicle == null) return;

    // Optional: make dust affect the ship handling when physically contacting a tile.
    vehicle.EnterDustField(speedScale, accelScale);

    // --- Clearing rules ---
    if (vehicle.boosting)
    {
        // Boosting should be required to clear. Harder dust resists.
        // Use force proxy you already authored on Vehicle.
        float damage01 = Mathf.Clamp01(vehicle.GetForceAsDamage() / 120f);
        float effective = damage01 * Mathf.Lerp(1.0f, 0.25f, hardness01); // hard dust reduces effect

        // Require some meaningful impact to pop a tile; tune later.
        if (effective >= 0.35f)
        {
            BreakTemporarily();
        }

        // While boosting, we do not accumulate “non-boost grind” time.
        _nonBoostClearSeconds = 0f;
        return;
    }

    // Not boosting: grind timer (wall-like behavior), scaled by hardness.
    float hardnessMul = Mathf.Lerp(1.0f, 2.25f, hardness01); // hard dust takes longer to break
    _nonBoostClearSeconds += Time.fixedDeltaTime / hardnessMul;

    if (_nonBoostClearSeconds >= Mathf.Max(0.05f, nonBoostSecondsToBreak))
    {
        BreakTemporarily();
    }
}

private void OnCollisionExit2D(Collision2D collision)
{
    // Reset grind timer when the vehicle stops pressing this tile.
    var vehicle = collision.collider != null ? collision.collider.GetComponent<Vehicle>() : null;
    if (vehicle == null) return;

    _nonBoostClearSeconds = 0f;
}

private void BreakTemporarily()
{
    if (_isBreaking) return;
    _isBreaking = true;

    Vector2Int gridPos = _drumTrack.WorldToGridPosition(transform.position);

    // Remove tile immediately (to pool), and schedule regrow through generator.
    // We use existing generator scheduling; per-tile override is handled by temporaryRegrowDelaySeconds.
    gen.DespawnDustAt(gridPos);

    // If you support per-tile override, request regrow with that delay; otherwise rely on existing regrow request flow.
    if (temporaryRegrowDelaySeconds >= 0f)
    {
        // This requires a generator API; if you already have RequestRegrowCellAt, call it here.
        // gen.RequestRegrowCellAt(gridPos, phase, temporaryRegrowDelaySeconds, refreshIfPending: true);
    }
}

    public float GetWorldRadius()
    {
        if (!terrainCollider)
            return 0.5f; // sane fallback

        // Prefer CircleCollider2D if that’s what terrainCollider actually is.
        if (terrainCollider is CircleCollider2D circle)
        {
            // Assume uniform scale on X/Y; lossyScale.x is fine.
            return circle.radius * Mathf.Abs(transform.lossyScale.x);
        }

        // Fallback for non-circle colliders: approximate from bounds.
        var bounds = terrainCollider.bounds;
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
        if (terrainCollider) if (terrainCollider != null) if (terrainCollider != null) terrainCollider.enabled = true;
    }
    public void SetFootprintFromCellSize(float cellWorldSize, float footprintMul)
    {
        float s = Mathf.Max(0.001f, cellWorldSize) * Mathf.Max(0.1f, footprintMul);
        // If this is sprite-based dust, you may want non-uniform scaling; start uniform.
        transform.localScale = new Vector3(s, s, 1f);
    }

    public void Begin()
    {
        SetColorVariance();

        // Start invisible; GrowIn will scale up.
        transform.localScale = Vector3.zero;

        if (particleSystem != null)
        {
            // Ensure no emission during grow.
            particleSystem.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        }

        _growInRoutine = StartCoroutine(GrowIn());
    }

    private void DisableInteractionImmediately() { 
        // Stop affecting vehicles instantly (even if particles linger)
        if (terrainCollider) if (terrainCollider != null) if (terrainCollider != null) terrainCollider.enabled = false; 
        var col = GetComponent<Collider2D>(); 
        if (col) col.enabled = false; 
        gameObject.layer = nonBlockingLayer;
    }
    private IEnumerator GrowIn()
    {
        float duration = (_growInOverride > 0f)
            ? _growInOverride
            : 5f;

        float t = 0f;
        while (t < duration)
        {
            t += Time.deltaTime;
            float s = Mathf.SmoothStep(0f, 1f, t / duration);
            transform.localScale = fullScale * s;
            yield return null;
        }

        transform.localScale = fullScale;

        if (particleSystem != null)
        {
            // Start emission only once the dust is at full size.
            particleSystem.Play(true);
            StartCoroutine(ParticleAlphaFadeIn(0.5f));
        }
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
    public void SetCellSizeDrivenScale(float cellWorldSize, float footprintMul = 1.15f)
    {
        float s = Mathf.Max(0.001f, cellWorldSize) * Mathf.Max(0.05f, footprintMul);

        // Authoritative base size for this dust tile.
        referenceScale = new Vector3(s, s, 1f);

        // In your current design, fullScale is the “settled” target size.
        // Keep it equal to referenceScale so GrowIn lands exactly on the cell-driven footprint.
        fullScale = referenceScale;

        // Keep accommodation consistent.
        _accomodationTarget = fullScale;

        // If we’re not currently doing a GrowIn, update scale immediately so pooled regrows match.
        if (_growInRoutine == null)
            transform.localScale = referenceScale;
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
    public void SetTrackBundle(CosmicDustGenerator _dustGenerator, DrumTrack _drums, MusicalPhase _phase)
    {
        gen = _dustGenerator;
        _drumTrack = _drums;
        phase = _phase;
    }
    public void StartFadeAndScaleDown(float duration)
    {
        if (_fadeRoutine != null) StopCoroutine(_fadeRoutine);
        _fadeRoutine = StartCoroutine(ParticleFadeAndScaleDown(duration));
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
         
        if (_drumTrack != null) { 
            var gridPos = _drumTrack.WorldToGridPosition(transform.position); 
            gen.DespawnDustAt(gridPos); 
        }
        else { gameObject.SetActive(false); } 
    }
    public void PrepareForReuse()
    {
        // Stop any lingering coroutines from prior life
        if (_accomodateRoutine != null) { StopCoroutine(_accomodateRoutine); _accomodateRoutine = null; }
        if (_regrowRoutine     != null) { StopCoroutine(_regrowRoutine);     _regrowRoutine     = null; }
        if (_fadeRoutine       != null) { StopCoroutine(_fadeRoutine);       _fadeRoutine       = null; }
        if (_growInRoutine     != null) { StopCoroutine(_growInRoutine);     _growInRoutine     = null; }

        _isBreaking = false;
        _isDespawned = false;
        _nonBoostClearSeconds = 0f;
        _stayForceUntil = 0f;
        _shrinkingFromStar = false;

        _accomodationTarget = fullScale = referenceScale;
        transform.localScale = referenceScale;

        if (terrainCollider != null) terrainCollider.enabled = true;

        if (particleSystem != null)
        {
            // Option A: no particle emission during reuse reset.
            // This prevents mass "popping" when pooled dust tiles are re-enabled.
            particleSystem.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        }

        _currentTint.a = 0.5f;
        SetTint(_currentTint);

        hardness01 = 0f;

        var col = GetComponent<Collider2D>();
        if (col) col.enabled = true;

        // reset any per-dust flags your logic uses
        dissipateOnExit = false;
    }

    public void ApplyImprint(Color tint, float hardness) {
        SetTint(tint); 
        hardness01 = Mathf.Clamp01(hardness);
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
}