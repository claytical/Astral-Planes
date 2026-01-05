using System;
using System.Collections;
using UnityEngine;
using Random = UnityEngine.Random;

public enum DustBehaviorType { PushThrough, Stop, Repel }

public class CosmicDust : MonoBehaviour
{
    [Header("Sizing (prefab baseline)")]
    public Vector3 prefabReferenceScale = new Vector3(0.75f, 0.75f, 1f); // <- tweak to your taste
    public ParticleSystem particleSystem;
    // === PhaseStar interaction ===
    [Header("Visual Footprint")]
    [Range(0.5f, 1f)]
    public float particleFootprintMul = 0.85f; // 85% of the cell; prevents edge bleed

    [Header("PhaseStar Proximity")]
    public bool starRemovesWithoutRegrow;   // if false, it will regrow via generator
    public float starAlphaFadeBias = 0.9f;         // keep a little glow as it shrinks
    [Header("Imprint (from MineNodes)")] 
    [Range(0f, 1f)] 
    public float hardness01 = 0f; // 0 = soft/easy, 1 = hard/needs more boost (semantic only for now)
    private BoxCollider2D _box;
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
// Cached cell sizing so we can rebuild collider whenever scale changes.
    private float _cellWorldSize = 1f;
    private float _cellClearanceWorld = 0f;
    private float _footprintMul = 1.15f;

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
    private Vector3 _targetBaseScale = Vector3.one;
    private Vector3 _baseLocalScale = Vector3.one;
    private float _baseWorldDiameter = 1f;
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
        if (particleSystem == null)
            particleSystem = GetComponent<ParticleSystem>() ?? GetComponentInChildren<ParticleSystem>(true);

        // If the prefab has PlayOnAwake accidentally enabled, force idle so pool/prewarm won't explode.
        if (particleSystem != null)
        {
            // Make renderer visible but stop simulation.
            var emission = particleSystem.emission;
            emission.enabled = false;

            particleSystem.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            particleSystem.Clear(true);

            // Optional: keep scaling stable in pooled scenarios
            var main = particleSystem.main;
            main.playOnAwake = false;
            main.scalingMode = ParticleSystemScalingMode.Local;
            particleSystem.transform.localScale = Vector3.one;
        }
        // Terrain collider lives on this same prefab (PolygonCollider2D recommended).
        // It contributes to the CompositeCollider2D on the DustPool root.
        if (terrainCollider == null) terrainCollider = GetComponent<Collider2D>();
        if (terrainCollider != null) terrainCollider.isTrigger = false;
        _box = GetComponent<BoxCollider2D>();
        // If the prefab/instance was saved at scale 0, use a sane fallback.
        float mag = transform.localScale.magnitude; 
        if (mag < 0.05f || mag > 20f) 
            transform.localScale = prefabReferenceScale;
        _baseLocalScale = transform.localScale;
        _targetBaseScale = _baseLocalScale;
        fullScale = _baseLocalScale;
        float r = GetWorldRadius();
        _baseWorldDiameter = Mathf.Max(0.0001f, r * 2f);
        _accomodationTarget = fullScale;
        _phaseDrainPerSecond = energyDrainPerSecond;
        // Belt-and-suspenders: make sure SpriteMask can’t clip us accidentally
        var psr = GetComponent<ParticleSystemRenderer>();
        if (psr) psr.maskInteraction = SpriteMaskInteraction.None;
        if (psr) psr.sortingFudge = 0f;
        ApplyParticleFootprint();
    }
    private void SyncParticlesToCollider()
    {
        if (particleSystem == null || _box == null) return;

        // World size of the box collider:
        // WorldSize = localSize * lossyScale
        Vector2 world = new Vector2(
            _box.size.x * Mathf.Abs(transform.lossyScale.x),
            _box.size.y * Mathf.Abs(transform.lossyScale.y)
        );

        var shape = particleSystem.shape;

        if (shape.enabled)
        {
            shape.scale = new Vector3(world.x, world.y, 1f);
        }

        // If Start Size is authored in big units, clamp it to something proportional.
        // This prevents an authored "10" from dwarfing a 1-unit cell.
        var main = particleSystem.main;
        main.startSize = Mathf.Min(main.startSize.constant, Mathf.Min(world.x, world.y) *.5f);
        Debug.Log($"[DUST:SYNC] boxWorld=({world.x:F3},{world.y:F3}) psScale={particleSystem.transform.localScale} mode={particleSystem.main.scalingMode} shapeScale={particleSystem.shape.scale}", this);

    }

    private void ApplyParticleFootprint()
    {
        if (particleSystem == null) return;

        // Ensure PS scales with parent, but then we shrink the PS itself to create margin.
        var main = particleSystem.main;
//        main.scalingMode = ParticleSystemScalingMode.Hierarchy;

        particleSystem.transform.localScale = new Vector3(particleFootprintMul*2f, particleFootprintMul*2f, 1f);
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
        //TODO: Attack vehicle
    }
}
    public void DissipateAndPoolVisualOnly(float fadeSeconds = 0.20f) {
            if (_isDespawned) return;
            _isDespawned = true; 
            // Ensure no collision during fade.
        if (terrainCollider != null) terrainCollider.enabled = false;
        var col = GetComponent<Collider2D>(); 
        if (col != null) col.enabled = false; 
        if (_fadeRoutine != null) { StopCoroutine(_fadeRoutine); _fadeRoutine = null; } 
        _fadeRoutine = StartCoroutine(FadeOutThenPoolVisualOnly(Mathf.Max(0.01f, fadeSeconds)));
        }

        private IEnumerator FadeOutThenPoolVisualOnly(float duration) {
            if (particleSystem != null) 
                particleSystem.Stop(true, ParticleSystemStopBehavior.StopEmitting); 
            float t = 0f;
            Color from = _currentTint;
            Color to = _currentTint; to.a = 0f;
            Vector3 s0 = transform.localScale;
            Vector3 s1 = s0 * 0.85f;
            while (t < duration) { 
                t += Time.deltaTime;
                float u = Mathf.SmoothStep(0f, 1f, t / duration); 
                transform.localScale = Vector3.Lerp(s0, s1, u); 
                SetTint(Color.Lerp(from, to, u)); 
                yield return null; 
            }
            
            SetTint(to);
            if (particleSystem != null) 
                particleSystem.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear); 
            if (gen != null) gen.ReturnDustToPoolPublic(gameObject);
            else gameObject.SetActive(false);
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
            return 1f; // sane fallback

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
        //transform.localScale = fullScale;
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
    public void SetVisualAlpha(float a)
    {
        if (particleSystem == null) return;
        var main = particleSystem.main;

        // Preserve tint but adjust alpha
        Color c = _currentTint; // however you store tint; otherwise keep a cached tint
        c.a = Mathf.Clamp01(a);
        main.startColor = c;

        // Optionally, stop emission entirely below cutoff
        var emission = particleSystem.emission;
        emission.enabled = a > 0.001f;
    }

    public void Begin()
    {
        SetColorVariance();

        transform.localScale = fullScale;

        ResetAndPlayParticles();
        ApplyParticleFootprint();
        _growInRoutine = StartCoroutine(GrowIn());
    }

    private void DisableInteractionImmediately() { 
        // Stop affecting vehicles instantly (even if particles linger)
        if (terrainCollider) if (terrainCollider != null) if (terrainCollider != null) terrainCollider.enabled = false; 
        var col = GetComponent<Collider2D>(); 
        if (col) col.enabled = false; 
        gameObject.layer = nonBlockingLayer;
        if (gen != null) gen.NotifyCompositeDirty();
    }
    private IEnumerator GrowIn()
    {
        float duration = (_growInOverride > 0f)
            ? _growInOverride
            : 5f;

        if (particleSystem == null || duration <= 0f) yield break;
        
        var main = particleSystem.main; 
        Color baseCol = _currentTint;
        float t = 0f; 
        while (t < duration) { 
            t += Time.deltaTime; 
            float a = Mathf.SmoothStep(0f, 1f, t / duration); 
            var c = new Color(baseCol.r, baseCol.g, baseCol.b, a); 
            main.startColor = c; 
            yield return null;
        }
        // Ensure fully visible at end
        main.startColor = new Color(baseCol.r, baseCol.g, baseCol.b, 1f);
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
        fullScale = _targetBaseScale * cfg.scaleMul;
        transform.localScale = fullScale;
        ApplyParticleFootprint();
        RebuildBoxColliderForCurrentScale();
        SyncParticlesToCollider();
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
    public void SetCellSizeDrivenScale(float cellWorldSize, float footprintMul = 1.15f, float clearanceWorld = 0f)
    {
        _cellWorldSize       = Mathf.Max(0.001f, cellWorldSize);
        _footprintMul        = Mathf.Max(0.05f, footprintMul);
        _cellClearanceWorld  = Mathf.Max(0f, clearanceWorld);

        // This is the *authoritative* base scale for this tile (before phase mul).
        float s = _cellWorldSize * _footprintMul;
        prefabReferenceScale = new Vector3(s, s, 1f);

        // Critical: update the phase base that ConfigureForPhase multiplies.
        _targetBaseScale = prefabReferenceScale;

        // Default fullScale to base (phase may modify it after).
        fullScale = _targetBaseScale;
        _accomodationTarget = fullScale;

        if (_growInRoutine == null)
            transform.localScale = fullScale;
        ApplyParticleFootprint();
        RebuildBoxColliderForCurrentScale();
        SyncParticlesToCollider();
    }
    private void RebuildBoxColliderForCurrentScale()
    {
        if (_box == null) return;

        // Desired solid fill in world space inside the cell.
        float desiredWorld = Mathf.Clamp(_cellWorldSize - _cellClearanceWorld, 0.001f, _cellWorldSize);

        // Our current local scale (what Unity multiplies collider size by).
        // Use current transform localScale (not prefabReferenceScale), because phase mul may be applied.
        float sx = Mathf.Max(0.0001f, Mathf.Abs(transform.localScale.x));
        float sy = Mathf.Max(0.0001f, Mathf.Abs(transform.localScale.y));

        // Convert world size -> local collider size per axis.
        _box.size   = new Vector2(desiredWorld / sx, desiredWorld / sy);
        _box.offset = Vector2.zero;
    }

    public void SetTerrainColliderEnabled(bool enabled)
    {
        if (terrainCollider != null)
            terrainCollider.enabled = enabled;
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
                new GradientAlphaKey(0f,          0f),    // invisible at birth
                new GradientAlphaKey(tint.a * 0.65f, 0.08f), // ramp up quickly
                new GradientAlphaKey(tint.a * 0.65f, 0.55f), // hold most of lifetime
                new GradientAlphaKey(tint.a * 0.35f, 0.85f), // late dissipation
                new GradientAlphaKey(0f,          1f),    // vanish
            }
        );

        col.color = new ParticleSystem.MinMaxGradient(grad);

    }
    
    public void DespawnGracefully(float fadeSeconds = 0.25f)
    {
        if (_isDespawned) return;
        _isDespawned = true;

        // Open the corridor immediately.
        if (terrainCollider != null) terrainCollider.enabled = false;
        if (gen != null) gen.NotifyCompositeDirty();
        // Kill any ongoing visual routines from prior state.
        if (_fadeRoutine != null) { StopCoroutine(_fadeRoutine); _fadeRoutine = null; }
        if (_growInRoutine != null) { StopCoroutine(_growInRoutine); _growInRoutine = null; }

        _fadeRoutine = StartCoroutine(FadeOutThenPool(fadeSeconds));
    }

    private IEnumerator FadeOutThenPool(float duration)
    {
        // Let already-emitted particles remain; stop emission only.
        if (particleSystem != null)
            particleSystem.Stop(true, ParticleSystemStopBehavior.StopEmitting);

        float t = 0f;
        Color from = _currentTint;
        Color to   = _currentTint; to.a = 0f;

        Vector3 startScale = transform.localScale;
        Vector3 endScale   = startScale * 0.85f; // subtle shrink; optional

        while (t < duration)
        {
            t += Time.deltaTime;
            float u = Mathf.SmoothStep(0f, 1f, t / Mathf.Max(0.0001f, duration));

            transform.localScale = Vector3.Lerp(startScale, endScale, u);
            SetTint(Color.Lerp(from, to, u));

            yield return null;
        }

        SetTint(to);

        if (particleSystem != null)
            particleSystem.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        // Hand back to generator/pool via your existing path.
        if (_drumTrack != null && gen != null)
        {
            var gridPos = _drumTrack.WorldToGridPosition(transform.position);
            gen.DespawnDustAt(gridPos);
        }
        else
        {
            gameObject.SetActive(false);
        }
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
        else { DespawnGracefully(); } 
    }
    public void PrepareForPool()
    {
        // Stop coroutines
        if (_accomodateRoutine != null) { StopCoroutine(_accomodateRoutine); _accomodateRoutine = null; }
        if (_regrowRoutine     != null) { StopCoroutine(_regrowRoutine);     _regrowRoutine     = null; }
        if (_fadeRoutine       != null) { StopCoroutine(_fadeRoutine);       _fadeRoutine       = null; }
        if (_growInRoutine     != null) { StopCoroutine(_growInRoutine);     _growInRoutine     = null; }

        // Disable collider so it can't contribute to composite or collide invisibly
        var col = GetComponent<Collider2D>();
        if (col) col.enabled = false;

        if (particleSystem != null)
        {
            var emission = particleSystem.emission;
            emission.enabled = false;
            particleSystem.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            particleSystem.Clear(true);
        }

        _isDespawned = true;
    }
    private void ResetAndPlayParticles()
    {
        if (particleSystem == null) return;

        var r = particleSystem.GetComponent<ParticleSystemRenderer>();
        if (r != null) r.enabled = true;

        var emission = particleSystem.emission;
        emission.enabled = true;

        particleSystem.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        particleSystem.Simulate(0f, true, true, true);
        particleSystem.Play(true);
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

        fullScale = _targetBaseScale; 
        _accomodationTarget = fullScale;
        transform.localScale = fullScale;
        if (terrainCollider != null) terrainCollider.enabled = true;

        if (particleSystem != null)
        {
            var emission = particleSystem.emission;
            emission.enabled = false;
            particleSystem.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            particleSystem.Clear(true);
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
        DespawnGracefully();
    }
}