using System;
using System.Collections;
using System.Text.RegularExpressions;
using UnityEngine;
using Random = UnityEngine.Random;

public enum DustBehaviorType { PushThrough, Stop, Repel }

public class CosmicDust : MonoBehaviour {
    [Serializable]
    public struct DustVisualSettings {
        [Header("Sizing (prefab baseline)")]
        public Vector3 prefabReferenceScale; // authored prefab baseline (cell-driven scale overrides at runtime)
        public ParticleSystem particleSystem;

        [Header("Visual Footprint")]
        [Range(0.5f, 1.6f)] public float particleFootprintMul; // % of cell; prevents edge bleed

        [Header("PhaseStar Proximity")]
        public bool  starRemovesWithoutRegrow; // if false, it will regrow via generator
        public float starAlphaFadeBias;        // keep a little glow as it shrinks

        [Header("Fade")]
        [Min(0.01f)] public float fadeSeconds;
    }
    public Color CurrentTint => _currentTint;
    [Serializable]
    public struct DustInteractionSettings
    {
        [Header("Vehicle Interaction")]
        [Range(0.4f, 1f)] public float speedScale; // cap scale
        [Range(0.4f, 1f)] public float accelScale; // thrust scale

        [Header("Energy Drain (Deterrent)")]
        [Min(0f)] public float energyDrainPerSecond;
        public bool noDrainWhileBoosting;
    }

    [Serializable]
    public struct DustClearingSettings
    {
        [Header("Imprint (from MineNodes)")]
        [Range(0f, 1f)] public float hardness01; // 0 = soft/easy, 1 = hard/needs more boost

        [Header("Dust Clearing")]
        [Min(0.05f)] public float nonBoostSecondsToBreak;

        [Tooltip("Override delay (seconds) before temporary regrow into the SAME cell. -1 uses the phase default.")]
        public float temporaryRegrowDelaySeconds;
    }

    [SerializeField] private DustVisualSettings visual = new DustVisualSettings
    {
        prefabReferenceScale   = new Vector3(0.75f, 0.75f, 1f),
        particleFootprintMul   = 0.85f,
        starRemovesWithoutRegrow = false,
        starAlphaFadeBias      = 0.9f,
        fadeSeconds            = 0.25f
    };

    [SerializeField] private DustInteractionSettings interaction = new DustInteractionSettings
    {
        speedScale            = 0.8f,
        accelScale            = 0.8f,
        energyDrainPerSecond  = 0.25f,
        noDrainWhileBoosting  = true
    };

    [SerializeField] public DustClearingSettings clearing = new DustClearingSettings
    {
        hardness01                = 0f,
        nonBoostSecondsToBreak    = 2.5f,
        temporaryRegrowDelaySeconds = -1f
    };    
    private float _baseDrainPerSecond;

    public enum DustBehavior { ViscousSlow, SiltDissipate, StaticCling, CrossCurrent, Turbulent }
    [Header("Behavior")]
    public DustBehavior behavior = DustBehavior.ViscousSlow; // NEW
    [Range(0.2f,1f)] public float slowFactor = 0.7f;         // NEW (velocity multiplier while inside)
    [Range(0f,2f)]   public float slowDuration = 0.35f;       // NEW (seconds)
    [Range(0f,10f)]  public float lateralForce = 2.0f;        // NEW (CrossCurrent)
    [Range(0f,10f)]  public float turbulence = 0.0f;          // NEW (Wildcard micro-deflection)
    private Vector3 fullScale = Vector3.one;
    private Vector3 _targetBaseScale = Vector3.one;
    private Vector3 _baseLocalScale = Vector3.one;
    private float _baseWorldDiameter = 1f;
    [SerializeField] private Collider2D terrainCollider;
    [SerializeField] private int solidLayer = 0;       // Default or your "Dust" layer
    [SerializeField] private int nonBlockingLayer = 2; // Ignore Raycast or a custom non-blocking layer
    private BoxCollider2D _box;
    private float _nonBoostClearSeconds;
// Cached cell sizing so we can rebuild collider whenever scale changes.
    private float _cellWorldSize = 1f;
    private float _cellClearanceWorld = 0f;
    private float _footprintMul = 1.15f;
    private Color _baseColor;
    private float _baseSize;
    private float _baseAlpha;
    private bool _baseCaptured;
    private bool  _hasImprint;
    private Color _imprintBaseTint;
    private Color _imprintShadowTint;

    private bool _isDespawned;
    private bool _shrinkingFromStar;
    private Coroutine  _fadeRoutine, _growInRoutine;
    private DrumTrack _drumTrack;
    private CosmicDustGenerator gen;
    private MusicalPhase phase;
    private float _growInOverride = -1f;
    private Color _currentTint = Color.white;
    [SerializeField] private int epochId;
    [SerializeField] private float stayForceEvery = 0.05f; // seconds
    private float _stayForceUntil;
    private bool _isBreaking;
    [SerializeField] private float _baseEmission = 1;

    void Awake() {
        if (visual.particleSystem == null)
            visual.particleSystem = GetComponent<ParticleSystem>() ?? GetComponentInChildren<ParticleSystem>(true);

        // If the prefab has PlayOnAwake accidentally enabled, force idle so pool/prewarm won't explode.
        if (visual.particleSystem != null)
        {
            // Make renderer visible but stop simulation.
            var emission = visual.particleSystem.emission;
            emission.enabled = false;

            visual.particleSystem.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            visual.particleSystem.Clear(true);

            // Optional: keep scaling stable in pooled scenarios
            var main = visual.particleSystem.main;
            main.playOnAwake = false;
            main.scalingMode = ParticleSystemScalingMode.Local;
            visual.particleSystem.transform.localScale = Vector3.one;
        }
        // Terrain collider lives on this same prefab (PolygonCollider2D recommended).
        // It contributes to the CompositeCollider2D on the DustPool root.
        if (terrainCollider == null) terrainCollider = GetComponent<Collider2D>();
        if (terrainCollider != null) terrainCollider.isTrigger = false;
        _box = GetComponent<BoxCollider2D>();
        // If the prefab/instance was saved at scale 0, use a sane fallback.
        float mag = transform.localScale.magnitude; 
        if (mag < 0.05f || mag > 20f) 
            transform.localScale = visual.prefabReferenceScale;
        _baseLocalScale = transform.localScale;
        _targetBaseScale = _baseLocalScale;
        fullScale = _baseLocalScale;
        float r = GetWorldRadius();
        _baseWorldDiameter = Mathf.Max(0.0001f, r * 2f);
        _baseDrainPerSecond = interaction.energyDrainPerSecond;
        // Belt-and-suspenders: make sure SpriteMask can’t clip us accidentally
        var psr = GetComponent<ParticleSystemRenderer>();
        if (psr) psr.maskInteraction = SpriteMaskInteraction.None;
        if (psr) psr.sortingFudge = 0f;
        ApplyParticleFootprint();
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
        ResetAndPlayParticles();
        // Visual reset
        SetTint(tint);
        //transform.localScale = fullScale;
        // Physics reset
        gameObject.layer = solidLayer;
        if (terrainCollider) if (terrainCollider != null) if (terrainCollider != null) terrainCollider.enabled = true;
    }
    public void SetVisualAlpha(float a)
    {
        if (visual.particleSystem == null) return;
        var main = visual.particleSystem.main;

        // Preserve tint but adjust alpha
        Color c = _currentTint; // however you store tint; otherwise keep a cached tint
        c.a = Mathf.Clamp01(a);
        main.startColor = c;

        // Optionally, stop emission entirely below cutoff
        var emission = visual.particleSystem.emission;
        emission.enabled = a > 0.001f;
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

// Root stays unit scale for collider determinism
        transform.localScale = Vector3.one;

// Apply phase scale to particle footprint only
        ApplyParticleFootprint();

        RebuildBoxColliderForCurrentScale();
        SyncParticlesToCollider();

// IMPORTANT: do not compound drain each time ConfigureForPhase runs
        interaction.energyDrainPerSecond = _baseDrainPerSecond * cfg.drainMul;

        // Motion / feel
        behavior       = cfg.behavior;
        slowFactor     = cfg.slowFactor;
        slowDuration   = cfg.slowDur;
        lateralForce   = cfg.lateral;
        turbulence     = cfg.turb;
    }

    public void Begin()
    {
        SetColorVariance();
        ResetAndPlayParticles();
        ApplyParticleFootprint();
        CaptureBaseVisual();

        _growInRoutine = StartCoroutine(GrowIn());
    }
    public void SetGrowInDuration(float seconds) { _growInOverride = Mathf.Max(0.05f, seconds); }
    public void SetTrackBundle(CosmicDustGenerator _dustGenerator, DrumTrack _drums, MusicalPhase _phase)
    {
        gen = _dustGenerator;
        _drumTrack = _drums;
        phase = _phase;
    }
    public void SetTint(Color tint)
    {
        if (!visual.particleSystem) return;

        _currentTint = tint;
        SetDustColorAllParticles(tint);
    }

    private void SetDustColorAllParticles(Color target)
    {
        if (visual.particleSystem == null) return;

        var ps   = visual.particleSystem;
        var main = ps.main;
        var col  = ps.colorOverLifetime;

        col.enabled = true;

        // Premultiply for premultiplied-alpha material.
        Color pm = Premultiply(target);

        var g = new Gradient();
        g.SetKeys(
            new[] {
                // Color keys: RGB only. Keep alpha at 1 so alpha is controlled solely by alpha keys.
                new GradientColorKey(new Color(pm.r, pm.g, pm.b, 1f), 0f),
                new GradientColorKey(new Color(pm.r, pm.g, pm.b, 1f), 1f)
            },
            new[] {
                // Alpha curve: your shape (fast rise, sustain, fade out)
                new GradientAlphaKey(0f,             0f),
                new GradientAlphaKey(pm.a * 0.65f,   0.08f),
                new GradientAlphaKey(pm.a * 0.65f,   0.55f),
                new GradientAlphaKey(pm.a * 0.35f,   0.85f),
                new GradientAlphaKey(0f,             1f),

            }
        );

        col.color = new ParticleSystem.MinMaxGradient(g);

        // Also set startColor for newly emitted particles.
        main.startColor = pm;
    }


    private static Color Premultiply(Color c)
    {
        c.r *= c.a;
        c.g *= c.a;
        c.b *= c.a;
        return c;
    }

    private void DespawnGracefully(float fadeSeconds = 0.25f)
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
    
    public void SetTerrainColliderEnabled(bool enabled)
    {
        if (terrainCollider != null)
            terrainCollider.enabled = enabled;
    }
    // CosmicDust.cs
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
    public void Visual_ChargeOnBoost(float appetite01)
    {
        if (visual.particleSystem == null) return;
        if (!_baseCaptured) CaptureBaseVisual();

        float a = Mathf.Clamp01(appetite01);

        var ps   = visual.particleSystem;
        var main = ps.main;

        // Base hue: whatever the dust currently is (phase tint or imprint tint)
        Color baseCol = _baseColor; // from CaptureBaseVisual
        baseCol.a = _baseAlpha;

        // Hue-preserving brighten toward white
        Color chargeCol = Color.Lerp(baseCol, Color.white, Mathf.Lerp(0.55f, 0.85f, a));
        chargeCol.a = _baseAlpha;

        // RGB-only brightness boost (alpha unchanged)
        chargeCol = MulRgb(chargeCol, Mathf.Lerp(1.05f, 1.25f, a));

        // Optional: slight size increase
        main.startSize = _baseSize * Mathf.Lerp(1.05f, 1.25f, a);

        // Optional: emission bump (keep conservative at first)
        var emission = ps.emission;
        emission.rateOverTime = _baseEmission * Mathf.Lerp(1.0f, 1.15f, a);

        SetDustColorAllParticles(chargeCol);
    }

    public void Visual_DenyOnBump(float severity01)
    {
        if (visual.particleSystem == null) return;
        if (!_baseCaptured) CaptureBaseVisual();

        float s = Mathf.Clamp01(severity01);

        var main = visual.particleSystem.main;

        // Deny target: role-authored shadow tint if imprinted; otherwise darken toward black.
        Color denyTarget = _hasImprint ? _imprintShadowTint : Color.black;
        denyTarget.a = _baseAlpha;

        Color baseCol = _baseColor;
        baseCol.a = _baseAlpha;

        // Blend toward deny target
        Color denyCol = Color.Lerp(baseCol, denyTarget, s);
        denyCol.a = _baseAlpha;

        // RGB-only darken (optional; tune)
        denyCol = MulRgb(denyCol, Mathf.Lerp(0.90f, 0.65f, s));

        SetDustColorAllParticles(denyCol);

        // Optional: size response on deny (define it; don't use "size")
        main.startSize = _baseSize * Mathf.Lerp(1.00f, 1.12f, s);
    }

    private void BeginFadeOutVisualOnly(float duration, System.Action onComplete = null)
    {
        if (_fadeRoutine != null) StopCoroutine(_fadeRoutine);
        _fadeRoutine = StartCoroutine(FadeOutVisualOnly(duration, onComplete));
    }
    private static Color MulRgb(Color c, float mul)
    {
        return new Color(c.r * mul, c.g * mul, c.b * mul, c.a);
    }

    private IEnumerator FadeOutVisualOnly(float duration, System.Action onComplete)
    {
        // Stop new particles, but don’t clear until the end (your current semantics)
        if (visual.particleSystem != null)
            visual.particleSystem.Stop(true, ParticleSystemStopBehavior.StopEmitting);

        float t = 0f;
        Color from = _currentTint;
        Color to   = _currentTint; to.a = 0f;

        Vector3 s0 = transform.localScale;
        Vector3 s1 = s0 * 0.85f;

        // Optional: if you want “stop blocking immediately”
        SetTerrainColliderEnabled(false);

        while (t < duration)
        {
            t += Time.deltaTime;
            float u = Mathf.SmoothStep(0f, 1f, t / Mathf.Max(0.0001f, duration));
            transform.localScale = Vector3.Lerp(s0, s1, u);
            SetTint(Color.Lerp(from, to, u));
            yield return null;
        }

        SetTint(to);

        if (visual.particleSystem != null)
            visual.particleSystem.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        onComplete?.Invoke();
    }

    public void PrepareForReuse()
    {
        // Stop any lingering coroutines from prior life
        if (_fadeRoutine       != null) { StopCoroutine(_fadeRoutine);       _fadeRoutine       = null; }
        if (_growInRoutine     != null) { StopCoroutine(_growInRoutine);     _growInRoutine     = null; }

        _isBreaking = false;
        _isDespawned = false;
        _nonBoostClearSeconds = 0f;
        _stayForceUntil = 0f;
        _shrinkingFromStar = false;
        transform.localScale = Vector3.one;
        if (terrainCollider != null) terrainCollider.enabled = true;

        if (visual.particleSystem != null)
        {
            var emission = visual.particleSystem.emission;
            emission.enabled = false;
            visual.particleSystem.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            visual.particleSystem.Clear(true);
        }


        _currentTint.a = 0.5f;
        SetTint(_currentTint);

        clearing.hardness01 = 0f;

        var col = GetComponent<Collider2D>();
        if (col) col.enabled = true;

    }

    public void ApplyImprint(Color baseTint, Color shadowTint, float hardness01)
    {
        _hasImprint         = true;
        _imprintBaseTint     = baseTint;
        _imprintShadowTint   = shadowTint;

        SetTint(baseTint);
        clearing.hardness01 = Mathf.Clamp01(hardness01);

        // Optional but recommended: if your charge/deny uses captured base values
        // recapture after tint so _baseColor/_baseAlpha reflect the imprint.
        if (_baseCaptured) CaptureBaseVisual();
    }


    public IEnumerator RetintOver(float seconds, Color toTint)
    {
        if (visual.particleSystem == null) yield break;

        var main = visual.particleSystem.main;
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
    public void DespawnToPoolInstant()
    { 
        DisableInteractionImmediately();
        // If you do NOT want lingering particles when pooled:
        if (visual.particleSystem != null) { 
            visual.particleSystem.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear); 
            visual.particleSystem.Clear(true);
        }
        var cHide = _currentTint; cHide.a = 0f;
        SetTint(cHide);
        transform.localScale = Vector3.zero;

        _isDespawned = true;
        DespawnGracefully();
    }    
    private void SyncParticlesToCollider()
    {
        if (visual.particleSystem == null || _box == null) return;
        // World size of the box collider:
        // WorldSize = localSize * lossyScale
        Vector2 world = new Vector2(
            _box.size.x * Mathf.Abs(transform.lossyScale.x),
            _box.size.y * Mathf.Abs(transform.lossyScale.y)
        );

        var shape = visual.particleSystem.shape;

        if (shape.enabled)
        {
            shape.scale = new Vector3(world.x, world.y, 1f);
        }

    }
    private void ApplyParticleFootprint()
    {
        if (visual.particleSystem == null) return;

        // Keep PS transform stable; drive footprint via startSize.
        visual.particleSystem.transform.localScale = Vector3.one;

        var main = visual.particleSystem.main;

        float mul = Mathf.Clamp(visual.particleFootprintMul, 0.05f, 2.0f);
        float size = _cellWorldSize * mul;
        main.startSize = size;

    }

    private void CaptureBaseVisual()
    {
        if (visual.particleSystem == null) return;
        var main = visual.particleSystem.main;

        _baseColor = main.startColor.color;
        _baseSize  = main.startSize.constant;
        _baseAlpha = _baseColor.a;
        _baseCaptured = true;
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

        if (visual.particleSystem == null || duration <= 0f) yield break;
        
        var main = visual.particleSystem.main; 
        Color baseCol = _currentTint;
        float t = 0f; 
        while (t < duration) { 
            t += Time.deltaTime; 
            float a = Mathf.SmoothStep(0f, 1f, t / duration); 
            var c = new Color(baseCol.r, baseCol.g, baseCol.b, baseCol.a * a);
            SetDustColorAllParticles(c);
            yield return null;
        }
        // Ensure fully visible at end
        SetDustColorAllParticles(baseCol);

    }
    public void DissipateVisualOnly(float duration)
    {
        duration = Mathf.Max(0.01f, duration);

        if (_fadeRoutine != null)
            StopCoroutine(_fadeRoutine);

        _fadeRoutine = StartCoroutine(FadeOutVisualOnly(duration));
    }
    private IEnumerator FadeOutVisualOnly(float duration)
    {
        if (visual.particleSystem != null)
            visual.particleSystem.Stop(true, ParticleSystemStopBehavior.StopEmitting);

        float t = 0f;

        Color from = _currentTint;
        Color to = _currentTint;
        to.a = 0f;

        Vector3 s0 = transform.localScale;
        Vector3 s1 = s0 * 0.85f;

        while (t < duration)
        {
            t += Time.deltaTime;
            float u = Mathf.SmoothStep(0f, 1f, t / duration);
            SetTint(Color.Lerp(from, to, u));

            yield return null;
        }

        SetTint(to);

        if (visual.particleSystem != null)
            visual.particleSystem.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        // Intentionally do NOT pool or deactivate here.
        // The generator will RemoveActiveAt(...) and return to pool at end-of-fade.
    }
    private IEnumerator FadeOutThenPoolVisualOnly(float duration)
    {
        BeginFadeOutVisualOnly(duration, () =>
        {
            if (gen != null) gen.ReturnDustToPoolPublic(gameObject);
            else gameObject.SetActive(false);
        });

        // Wait until the fade completes (if your calling code expects a coroutine)
        yield return new WaitForSeconds(duration);
    }
    private void OnCollisionStay2D(Collision2D collision)
    {
        if (_isDespawned || _isBreaking) return;
        if (gen == null || _drumTrack == null) return;

        var vehicle = collision.collider != null ? collision.collider.GetComponent<Vehicle>() : null;
        if (vehicle == null) return;

        // Optional: make dust affect the ship handling when physically contacting a tile.
        vehicle.EnterDustField(interaction.speedScale, interaction.accelScale);

        // --- Clearing rules ---
        if (vehicle.boosting)
        {
            // Boosting clears via generator carve. Dust tile only provides optional visual response.
            float damage01 = Mathf.Clamp01(vehicle.GetForceAsDamage() / 120f);
            float effective = damage01 * Mathf.Lerp(1.0f, 0.25f, clearing.hardness01);

            // Appetite proxy: reuse effective or compute from vehicle speed if you prefer.
            Visual_ChargeOnBoost(effective);

            _nonBoostClearSeconds = 0f;
            return;
        }


        // Not boosting: grind timer (wall-like behavior), scaled by hardness.
        // Not boosting: dust denies energy.
        // Harder dust drains more and “denies” more strongly.
        float hardnessMul = Mathf.Lerp(1.0f, 2.25f, clearing.hardness01);

        // Accumulate “grind” time for severity only (not for breaking).
        _nonBoostClearSeconds += Time.fixedDeltaTime;

        // Drain rate (energy/sec) scaled by hardness.
        float drainPerSec = Mathf.Max(0f, interaction.energyDrainPerSecond);
        float drain = drainPerSec * hardnessMul * Time.fixedDeltaTime;

        vehicle.DrainEnergy(drain);

        // Severity is based on “how long you keep denying energy” on this tile.
        // We can normalize by your existing nonBoostSecondsToBreak as a convenient tuning knob.
        float denom = Mathf.Max(0.05f, clearing.nonBoostSecondsToBreak);
        float severity01 = Mathf.Clamp01(_nonBoostClearSeconds / denom);

// Visual denial shift
        Visual_DenyOnBump(severity01);

// IMPORTANT: no breaking/clearing here.

    }
    private void OnCollisionExit2D(Collision2D collision) {
        // Reset grind timer when the vehicle stops pressing this tile.
        var vehicle = collision.collider != null ? collision.collider.GetComponent<Vehicle>() : null;
        if (vehicle == null) return;

        _nonBoostClearSeconds = 0f;
        ResetVisualToBase();
    }
    
    public void ResetVisualToBase()
    {
        if (visual.particleSystem == null) return;
        if (!_baseCaptured) return;

        var main = visual.particleSystem.main;
        SetDustColorAllParticles(_baseColor);
        main.startSize  = _baseSize;
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
        transform.localScale = Vector3.one;
        ApplyParticleFootprint();
        RebuildBoxColliderForCurrentScale();
        SyncParticlesToCollider();
    }
    private void RebuildBoxColliderForCurrentScale()
    {
        if (_box == null) return;

        float desiredWorld = Mathf.Clamp(_cellWorldSize - _cellClearanceWorld, 0.001f, _cellWorldSize);

        // With transform.localScale == 1, local size == world size.
        _box.size   = new Vector2(desiredWorld, desiredWorld);
        _box.offset = Vector2.zero;
    }

    private void SetColorVariance()
    {
        var c = _currentTint;
        float variation = Random.Range(-.02f, .02f);
        c.r = Mathf.Clamp01(c.r + variation);
        c.g = Mathf.Clamp01(c.g + variation);
        c.b = Mathf.Clamp01(c.b + variation);
        SetTint(c);
    }
    private IEnumerator FadeOutThenPool(float duration)
    {
        // Let already-emitted particles remain; stop emission only.
        if (visual.particleSystem != null)
            visual.particleSystem.Stop(true, ParticleSystemStopBehavior.StopEmitting);

        float t = 0f;
        Color from = _currentTint;
        Color to   = _currentTint; to.a = 0f;

        Vector3 startScale = transform.localScale;
        Vector3 endScale   = startScale * 0.85f; // subtle shrink; optional

        while (t < duration)
        {
            t += Time.deltaTime;
            float u = Mathf.SmoothStep(0f, 1f, t / Mathf.Max(0.0001f, duration));
            SetTint(Color.Lerp(from, to, u));

            yield return null;
        }

        SetTint(to);

        if (visual.particleSystem != null)
            visual.particleSystem.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

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
    private void ResetAndPlayParticles()
    {
        if (visual.particleSystem == null) return;

        var r = visual.particleSystem.GetComponent<ParticleSystemRenderer>();
        if (r != null) r.enabled = true;

        var emission = visual.particleSystem.emission;
        emission.enabled = true;

        visual.particleSystem.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        visual.particleSystem.Simulate(0f, true, true, true);
        visual.particleSystem.Play(true);
    }

}