using System;
using System.Collections;
using System.Linq;
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

        public SpriteRenderer sprite;
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
    private Vector3 _initialLocalScale = Vector3.one;
    private bool _cachedInitialScale;
    public enum DustBehavior { ViscousSlow, SiltDissipate, StaticCling, CrossCurrent, Turbulent }
    [Header("Behavior")]
    public DustBehavior behavior = DustBehavior.ViscousSlow; // NEW
    [Range(0.2f,1f)] public float slowFactor = 0.7f;         // NEW (velocity multiplier while inside)
    [Range(0f,2f)]   public float slowDuration = 0.35f;       // NEW (seconds)
    [Range(0f,10f)]  public float lateralForce = 2.0f;        // NEW (CrossCurrent)
    [Range(0f,10f)]  public float turbulence = 0.0f;          // NEW (Wildcard micro-deflection)
    private Vector3 _baseLocalScale = Vector3.one;
    [SerializeField] private bool _hasBoundCell;
    [SerializeField] private Vector2Int _boundCell;

    public bool HasBoundCell => _hasBoundCell;
    public Vector2Int BoundCell => _boundCell;
    [SerializeField] private Collider2D terrainCollider;
    [SerializeField] private int solidLayer = 0;       // Default or your "Dust" layer
    [SerializeField] private int nonBlockingLayer = 2; // Ignore Raycast or a custom non-blocking layer
    private BoxCollider2D _box;
    private float _nonBoostClearSeconds;
    private float _cellWorldSize = 1f;
    private float _cellClearanceWorld = 0f;
    private Color _baseColor;
    private float _baseSize;
    private float _baseAlpha;
    private bool _baseCaptured;
    private bool  _hasImprint;
    private Color _imprintBaseTint;
    private Color _imprintShadowTint;
    private int _prefabInitialLayer;
    private bool _prefabLayerCaptured;
    private Coroutine _poolFailsafeRoutine;

    private bool _isDespawned;
    private bool _shrinkingFromStar;
    private Coroutine  _fadeRoutine, _growInRoutine;
    private DrumTrack _drumTrack;
    private CosmicDustGenerator gen;
    private float _growInOverride = -1f;
    private Color _currentTint = Color.white;

    [Serializable]
    public struct DustTintSettings
    {
        [Header("Base")]
        public Color baseColor;

        [Header("Transient Channels")]
        public Color chargeColor;
        public Color denyColor;
        public Color shadowColor;

        [Header("Timing")]
        [Min(0.01f)] public float chargeFadeSeconds;
        [Min(0.01f)] public float denyFadeSeconds;
        [Min(0.01f)] public float shadowInSeconds;
        [Min(0.01f)] public float shadowOutSeconds;
    }

    [Header("Tint Channels (Base/Charge/Deny/Shadow)")]
    [SerializeField] private DustTintSettings tint = new DustTintSettings
    {
        baseColor = new Color(0.7f, 0.7f, 0.7f, 0.25f),
        chargeColor = new Color(1f, 1f, 1f, 0.95f),
        denyColor = new Color(0f, 0f, 0f, 0.55f),
        shadowColor = new Color(0.1f, 0.1f, 0.1f, 0.45f),
        chargeFadeSeconds = 0.35f,
        denyFadeSeconds = 0.35f,
        shadowInSeconds = 0.20f,
        shadowOutSeconds = 0.35f
    };

    private Color _baseTintRaw;
    private Color _baseTintVaried;
    private bool  _baseVarianceApplied;

    private float _growAlphaMul = 1f;

    private float _chargeW = 0f;
    private float _denyW   = 0f;
    private float _shadowW = 0f;
    private float _shadowTargetW = 0f;
    private bool  _chargeSticky = false;

    private float _chargeFadeOverride = -1f;
    private float _denyFadeOverride = -1f;

    [SerializeField] private int epochId;
    [SerializeField] private float stayForceEvery = 0.05f; // seconds
    private float _stayForceUntil;
    private bool _isBreaking;
    [SerializeField] private float _baseEmission = 1;

    void Awake() {
        if (!_cachedInitialScale)
        {
            
            _initialLocalScale = transform.localScale;
            if (_initialLocalScale == Vector3.zero) _initialLocalScale = Vector3.one;
            _cachedInitialScale = true;
        }
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
        if (!_prefabLayerCaptured)
        {
            _prefabInitialLayer = gameObject.layer;
            _prefabLayerCaptured = true;
        }

        // Terrain collider lives on this dust prefab (BoxCollider2D in your grid-maze).
        // In modern Unity 2D, contribution to a CompositeCollider2D is controlled via
        // Collider2D.compositeOperation (usedByComposite is obsolete).
        // IMPORTANT: this should reference the *tile* collider (the contributor), not the parent composite.
        if (terrainCollider == null) terrainCollider = GetComponent<BoxCollider2D>();
        if (terrainCollider == null) terrainCollider = GetComponent<Collider2D>(); // fallback (keeps older scenes alive)
        if (terrainCollider != null)
        {
            terrainCollider.isTrigger = false;
            // Default to contributing when active.
            terrainCollider.compositeOperation = Collider2D.CompositeOperation.Merge;
        }
        _box = GetComponent<BoxCollider2D>();
        // If the prefab/instance was saved at scale 0, use a sane fallback.
        float mag = transform.localScale.magnitude; 
        if (mag < 0.05f || mag > 20f) 
            transform.localScale = visual.prefabReferenceScale;
        _baseLocalScale = transform.localScale;
        float r = GetWorldRadius();
        _baseDrainPerSecond = interaction.energyDrainPerSecond;
        // Belt-and-suspenders: make sure SpriteMask can’t clip us accidentally
        var psr = GetComponent<ParticleSystemRenderer>();
        if (psr) psr.maskInteraction = SpriteMaskInteraction.None;
        if (psr) psr.sortingFudge = 0f;
        ApplyParticleFootprint();
    }
    public void BindToCell(Vector2Int cell, CosmicDustGenerator generator, DrumTrack drumTrack)
    {
        _boundCell = cell;
        _hasBoundCell = true;
        gen = generator;
        _drumTrack = drumTrack;
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
    private void EnsureParticleHierarchyActive()
    {
        if (visual.particleSystem == null) return;
        // Ensure the particle system GO and its parents are active up to this dust root.
        Transform t = visual.particleSystem.transform;
        while (t != null)
        {
            Debug.Log($"[PS-HIER] {t.name} activeSelf={t.gameObject.activeSelf} activeInHierarchy={t.gameObject.activeInHierarchy}");

            if (!t.gameObject.activeSelf)
                t.gameObject.SetActive(true);

            if (t == transform) break;
            t = t.parent;
        }
        var cols = GetComponentsInChildren<Collider2D>(true);
        Debug.Log($"[REGROWTH] colliders={cols.Length} enabled={string.Join(",", cols.Select(c=>c.enabled))}");
    }
    public void OnSpawnedFromPool(Color tint)
    {
        // Ensure this object is actually usable again
        if (!_cachedInitialScale)
        {
            _initialLocalScale = transform.localScale;
            if (_initialLocalScale == Vector3.zero) _initialLocalScale = Vector3.one;
            _cachedInitialScale = true;
        }

        // CRITICAL: DespawnToPoolInstant() scales to zero; we must undo that here.
        transform.localScale = _initialLocalScale;

        EnsureParticleHierarchyActive();
        ResetAndPlayParticles();

        // Visual reset
        SetTintResetTransients(tint);

        // Physics reset (your existing layer safety)
        int targetLayer = (solidLayer == 0) ? _prefabInitialLayer : solidLayer;
        gameObject.layer = targetLayer;

        // Ensure this tile contributes to the composite maze when active.
        if (terrainCollider != null)
        {
            terrainCollider.compositeOperation = Collider2D.CompositeOperation.Merge;
            terrainCollider.enabled = true;
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
    public void SetTrackBundle(CosmicDustGenerator _dustGenerator, DrumTrack _drums)
    {
        gen = _dustGenerator;
        _drumTrack = _drums;
            }
    
    private void Update()
    {
        // Drive transient tint channels with minimal tunables.
        float dt = Time.deltaTime;

        // Shadow ramps toward target.
        if (!Mathf.Approximately(_shadowW, _shadowTargetW))
        {
            float seconds = (_shadowTargetW > _shadowW)
                ? Mathf.Max(0.01f, tint.shadowInSeconds)
                : Mathf.Max(0.01f, tint.shadowOutSeconds);
            float step = dt / seconds;
            _shadowW = Mathf.MoveTowards(_shadowW, _shadowTargetW, step);
        }

        // Charge/deny decay.
        if (!_chargeSticky && _chargeW > 0f)
        {
            float seconds = (_chargeFadeOverride > 0f) ? _chargeFadeOverride : tint.chargeFadeSeconds;
            float decay = dt / Mathf.Max(0.01f, seconds);
            _chargeW = Mathf.Max(0f, _chargeW - decay);
        }
        if (_denyW > 0f)
        {
            float seconds = (_denyFadeOverride > 0f) ? _denyFadeOverride : tint.denyFadeSeconds;
            float decay = dt / Mathf.Max(0.01f, seconds);
            _denyW = Mathf.Max(0f, _denyW - decay);
        }

        // Re-apply only if something is active/moving.
        if (_chargeW > 0f || _denyW > 0f || !Mathf.Approximately(_shadowW, _shadowTargetW) || _shadowW > 0f || _growAlphaMul < 1f)
        {
            ApplyTint(ComputeTint());
        }
    }
public void SetTint(Color baseTint)
    {
        // Establish the *base* tint for this tile.
        // IMPORTANT: Do NOT clear transient overlays here.
        // The generator may retint/reblend base colors frequently (e.g., diffusion), and clearing
        // transients would erase charge/deny/shadow feedback before it can be perceived.
        _baseTintRaw = baseTint;
        _baseTintVaried = baseTint;
        _baseVarianceApplied = false;

        EnsureBaseVarianceApplied();
        ApplyTint(ComputeTint());
    }

    /// <summary>
    /// Set the base tint and explicitly clear transient overlays.
    /// Use this for pool reuse / hard resets only.
    /// </summary>
    public void SetTintResetTransients(Color baseTint)
    {
        _baseTintRaw = baseTint;
        _baseTintVaried = baseTint;
        _baseVarianceApplied = false;

        _chargeW = 0f;
        _denyW   = 0f;
        _shadowW = 0f;
        _shadowTargetW = 0f;
        _chargeSticky = false;
        _chargeFadeOverride = -1f;
        _denyFadeOverride   = -1f;

        EnsureBaseVarianceApplied();
        ApplyTint(ComputeTint());
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
                new GradientAlphaKey(pm.a * 0.75f,   0.85f),
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
        if (terrainCollider == null) return;

        // When disabled, remove contribution to the parent composite to avoid "ghost walls".
        // When enabled, re-add contribution.
        terrainCollider.compositeOperation = enabled
            ? Collider2D.CompositeOperation.Merge
            : Collider2D.CompositeOperation.None;
        terrainCollider.enabled = enabled;

        // If we have a generator reference, tell it to rebuild composite geometry.
        if (gen != null) gen.MarkCompositeDirty();
    }
    public void DissipateAndPoolVisualOnly(float fadeSeconds = 0.20f) {
        if (_isDespawned) return;
        
        _isDespawned = true; 
        // Ensure no collision / contribution during fade.
        SetTerrainColliderEnabled(false);
        var col = GetComponent<Collider2D>(); 
        if (col != null) col.enabled = false; 
        if (_poolFailsafeRoutine != null) { StopCoroutine(_poolFailsafeRoutine); _poolFailsafeRoutine = null; }

        float dur = Mathf.Max(0.01f, fadeSeconds);
        _fadeRoutine = StartCoroutine(FadeOutThenPoolVisualOnly(dur));
        _poolFailsafeRoutine = StartCoroutine(PoolFailsafeAfter(dur + 0.05f));

    }

    private void Visual_ChargeOnBoost(float appetite01)
    {
        // Prefer the unified tint-channel system (affects sprite + particles).
        PulseCharge(Mathf.Clamp01(appetite01), fadeSeconds: tint.chargeFadeSeconds, stickyUntilDestroyed: false);
    }

    private void Visual_DenyOnBump(float severity01)
    {
        PulseDeny(Mathf.Clamp01(severity01), fadeSeconds: tint.denyFadeSeconds);
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
            ApplyTintDirect(Color.Lerp(from, to, u));
            yield return null;
        }

        ApplyTintDirect(to);

        if (visual.particleSystem != null)
            visual.particleSystem.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        onComplete?.Invoke();
    }

    public void PrepareForReuse()
    {
        if (_fadeRoutine != null) { StopCoroutine(_fadeRoutine); _fadeRoutine = null; }
        if (_growInRoutine != null) { StopCoroutine(_growInRoutine); _growInRoutine = null; }
        if (_poolFailsafeRoutine != null) { StopCoroutine(_poolFailsafeRoutine); _poolFailsafeRoutine = null; }

        _isBreaking = false;
        _isDespawned = false;
        _nonBoostClearSeconds = 0f;
        _stayForceUntil = 0f;
        _shrinkingFromStar = false;
        _hasBoundCell = false;
        _boundCell = default;
        // Restore captured prefab/base scale instead of Vector3.one
        transform.localScale = (_baseLocalScale.sqrMagnitude > 0.0001f)
            ? _baseLocalScale
            : (visual.prefabReferenceScale.sqrMagnitude > 0.0001f ? visual.prefabReferenceScale : Vector3.one);

        if (terrainCollider != null) terrainCollider.enabled = true;

        var col = GetComponent<Collider2D>();
        if (col) col.enabled = true;

        if (visual.particleSystem != null)
        {
            var emission = visual.particleSystem.emission;
            emission.enabled = false;
            visual.particleSystem.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            visual.particleSystem.Clear(true);
        }

        _currentTint.a = 0.5f;
        SetTintResetTransients(_currentTint);

        clearing.hardness01 = 0f;
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
        Debug.Log($"[DUST] OnCollisionStay2D: {collision.gameObject.name} with hardness {clearing.hardness01}");
        // Optional: make dust affect the ship handling when physically contacting a tile.
        vehicle.EnterDustField(interaction.speedScale, interaction.accelScale);

        // --- Clearing rules ---
        if (vehicle.boosting)
        {
            // Boosting clears via generator carve. Dust tile only provides optional visual response.
            float damage01 = Mathf.Clamp01(vehicle.GetForceAsDamage() / 120f);
            float effective = damage01 * Mathf.Lerp(1.0f, 0.25f, clearing.hardness01);

            // Appetite proxy: reuse effective or compute from vehicle speed if you prefer.
            Debug.Log($"[DUST] Charging {collision.gameObject.name} with effective of {effective}");

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
        Debug.Log($"[REGROWTH] Reset Visual To Base");
    }

    

    private void EnsureBaseVarianceApplied()
    {
        if (_baseVarianceApplied) return;
        if (_baseTintRaw == default) _baseTintRaw = tint.baseColor;

        // Small deterministic-per-instance RGB variance for texture-like feel.
        // Keep alpha untouched; alpha is handled by the base tint and grow multiplier.
        float v = UnityEngine.Random.Range(-0.02f, 0.02f);
        _baseTintVaried = _baseTintRaw;
        _baseTintVaried.r = Mathf.Clamp01(_baseTintVaried.r + v);
        _baseTintVaried.g = Mathf.Clamp01(_baseTintVaried.g + v);
        _baseTintVaried.b = Mathf.Clamp01(_baseTintVaried.b + v);
        _baseVarianceApplied = true;
    }

    private Color ComputeTint()
    {
        EnsureBaseVarianceApplied();

        // Start from base.
        Color c = _baseTintVaried;

        // Shadow is a slow ramp.
        if (_shadowW > 0f)
            c = Color.Lerp(c, tint.shadowColor, _shadowW);

        // Deny is an immediate pulse that fades out.
        if (_denyW > 0f)
            c = Color.Lerp(c, tint.denyColor, _denyW);

        // Charge should win visually.
        if (_chargeW > 0f)
            c = Color.Lerp(c, tint.chargeColor, _chargeW);

        // Apply grow alpha multiplier (regrowth legibility).
        c.a *= Mathf.Clamp01(_growAlphaMul);
        return c;
    }

    private void ApplyTint(Color c)
    {
        _currentTint = c;
        if (visual.sprite != null) visual.sprite.color = c;
        if (visual.particleSystem != null) SetDustColorAllParticles(c);
    }

    // Direct apply for fade routines that should not mutate base/transient channels.
    private void ApplyTintDirect(Color c)
    {
        _currentTint = c;
        if (visual.sprite != null) visual.sprite.color = c;
        if (visual.particleSystem != null) SetDustColorAllParticles(c);
    }

    public void PulseCharge(float strength01 = 1f, float fadeSeconds = -1f, bool stickyUntilDestroyed = false)
    {
        strength01 = Mathf.Clamp01(strength01);
        _chargeW = Mathf.Max(_chargeW, strength01);
        _chargeSticky = stickyUntilDestroyed;
        if (fadeSeconds > 0f) _chargeFadeOverride = Mathf.Max(0.01f, fadeSeconds);
        ApplyTint(ComputeTint());
    }

    public void PulseDeny(float strength01 = 1f, float fadeSeconds = -1f)
    {
        strength01 = Mathf.Clamp01(strength01);
        _denyW = Mathf.Max(_denyW, strength01);
        if (fadeSeconds > 0f) _denyFadeOverride = Mathf.Max(0.01f, fadeSeconds);
        ApplyTint(ComputeTint());
    }

    public void SetShadowTarget(bool enabled)
    {
        _shadowTargetW = enabled ? 1f : 0f;
        ApplyTint(ComputeTint());
    }

    public void ClearTransientTint()
    {
        _chargeW = 0f;
        _denyW = 0f;
        _shadowW = 0f;
        _shadowTargetW = 0f;
        _chargeSticky = false;
        _chargeFadeOverride = -1f;
        _denyFadeOverride = -1f;
        ApplyTint(ComputeTint());
    }
public void SetCellSizeDrivenScale(float cellWorldSize, float footprintMul = 1.15f, float clearanceWorld = 0f)
    {
        _cellWorldSize       = Mathf.Max(0.001f, cellWorldSize);
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
        // Legacy entry point: variance is now applied to base tint once.
        EnsureBaseVarianceApplied();
        ApplyTint(ComputeTint());
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
            ApplyTintDirect(Color.Lerp(from, to, u));

            yield return null;
        }

        ApplyTintDirect(to);

        if (visual.particleSystem != null)
            visual.particleSystem.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        if (gen != null && _hasBoundCell)
        {
            gen.DespawnDustInstance(this);
        }
        else if (gen != null && _drumTrack != null)
        {
            // Fallback only; should be rare once binding is correct.
            gen.DespawnDustInstance(this);
        }
        else
        {
            gameObject.SetActive(false);
        }


    }
    private IEnumerator PoolFailsafeAfter(float seconds)
{
    yield return new WaitForSeconds(seconds);

    if (!_isDespawned) yield break;

    if (gameObject != null && gameObject.activeInHierarchy)
    {
        if (gen != null) gen.ReturnDustToPoolPublic(gameObject);
        else gameObject.SetActive(false);
    }

    _poolFailsafeRoutine = null;
}

    private void ResetAndPlayParticles()
    {
        if (visual.particleSystem == null) return;
        visual.particleSystem.gameObject.SetActive(true);
        visual.particleSystem.transform.parent.gameObject.SetActive(true);
        var r = visual.particleSystem.GetComponent<ParticleSystemRenderer>();
        if (r != null) r.enabled = true;

        var emission = visual.particleSystem.emission;
        emission.enabled = true;

        visual.particleSystem.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        visual.particleSystem.Simulate(0f, true, true, true);
        visual.particleSystem.Play(true);
    }
}