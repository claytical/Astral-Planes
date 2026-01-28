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
    [System.Serializable]
    public struct DustVisualTimings
    {
        [Min(0.01f)] public float spriteScaleInSeconds;   // Circle scale 0->1
        [Min(0.01f)] public float spriteScaleOutSeconds;  // Circle scale 1->0
        [Min(0.01f)] public float particleGrowInSeconds;  // particle alpha ramp
        [Min(0.01f)] public float fadeOutSeconds;         // tint/alpha fade out (if used)
    }

    [SerializeField] private DustVisualTimings _timings = new DustVisualTimings
    {
        spriteScaleInSeconds = 0.20f,
        spriteScaleOutSeconds = 0.20f,
        particleGrowInSeconds = 1.00f,
        fadeOutSeconds = 0.20f
    };


    public Color CurrentTint => _currentTint;
    [SerializeField] private Color _chargeColor = Color.white;
    [SerializeField] private Color _denyColor = Color.violetRed;
    private bool _hasFeedbackColors = false;
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
        fadeSeconds            = 1f
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
    [Header("Shader Params")]
    [SerializeField] private bool useWorkShaderParams = true;
    private static readonly int _RoleColorId = Shader.PropertyToID("_RoleColor");
    private static readonly int _WorkId = Shader.PropertyToID("_Work");
    private MaterialPropertyBlock _mpb;
    private ParticleSystemRenderer _psRenderer;
    private float _workSigned01 = 0f;
    private float _baseDrainPerSecond;
    [Header("Work / Preview (Boost Path)")]
    [SerializeField] private float previewWorkHoldSeconds = 0.10f;
    private Coroutine _previewWorkRoutine;
    private int _previewWorkToken = 0;
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

    [SerializeField] public Collider2D terrainCollider;

    // Some prefab variants ended up with colliders on children (or multiple colliders).
    // Carve/disable must be authoritative, so we cache all colliders in the hierarchy.
    private Collider2D[] _cachedColliders;
    [SerializeField] private int solidLayer = 0;       // Default or your "Dust" layer
    [SerializeField] private int nonBlockingLayer = 2; // Ignore Raycast or a custom non-blocking layer
    private BoxCollider2D _box;
    private CircleCollider2D _circle;
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

    private bool _isDespawned;
    private bool _shrinkingFromStar;
    private Coroutine  _fadeRoutine, _growInRoutine;
    private DrumTrack _drumTrack;
    private CosmicDustGenerator gen;
    private float _growInOverride = -1f;
    private Color _currentTint = Color.white;
    [SerializeField] private int epochId;
    [SerializeField] private float stayForceEvery = 0.05f; // seconds
    private float _stayForceUntil;
    private bool _isBreaking;
    [SerializeField] private float _baseEmission = 1;
    private Coroutine _spriteScaleRoutine;
    void Awake() {
        if (!_cachedInitialScale)
        {
            
            _initialLocalScale = transform.localScale;
            if (_initialLocalScale == Vector3.zero) _initialLocalScale = Vector3.one;
            _cachedInitialScale = true;
        }
        if (visual.particleSystem == null)
            visual.particleSystem = GetComponent<ParticleSystem>() ?? GetComponentInChildren<ParticleSystem>(true);
// Cache renderer + MPB for shader parameters (avoid material instancing).
        if (_mpb == null) _mpb = new MaterialPropertyBlock();
        
        if (visual.particleSystem != null) 
            _psRenderer = visual.particleSystem.GetComponent<ParticleSystemRenderer>();
        // If the prefab has PlayOnAwake accidentally enabled, force idle so pool/prewarm won't explode.

        if (visual.particleSystem != null)
        {
            // Capture authored emission settings (root + children) before we shut anything off.
            EnsureBaseParticleEmissionCaptured();

            // Force idle so pool/prewarm won't explode, and ensure everything starts hidden.
            var systems = GetAllParticleSystems();
            if (systems != null)
            {
                for (int i = 0; i < systems.Length; i++)
                {
                    var ps = systems[i];
                    if (ps == null) continue;

                    var main = ps.main;
                    main.playOnAwake = false;
                    main.scalingMode = ParticleSystemScalingMode.Local;

                    var emission = ps.emission;
                    emission.enabled = false;

                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    ps.Clear(true);
                }
            }

            // Keep root particle transform stable in pooled scenarios
            visual.particleSystem.transform.localScale = Vector3.one;
        }
        if (!_prefabLayerCaptured)
        {
            _prefabInitialLayer = gameObject.layer;
            _prefabLayerCaptured = true;
        }

        // Terrain collider typically lives on this same prefab (PolygonCollider2D recommended),
        // but some prefab variants place colliders on children.
        if (terrainCollider == null) terrainCollider = GetComponent<Collider2D>();
        if (terrainCollider == null) terrainCollider = GetComponentInChildren<Collider2D>(true);
        if (terrainCollider != null) terrainCollider.isTrigger = false;

        // Cache all colliders so we can disable collisions even if terrainCollider is not assigned
        // (or if there are multiple colliders involved in contact).
        _cachedColliders = GetComponentsInChildren<Collider2D>(true);
        _box = GetComponent<BoxCollider2D>();
        // CircleCollider2D support (grid-of-circles dust tiles)
        _circle = terrainCollider as CircleCollider2D;
        if (_circle == null) _circle = GetComponent<CircleCollider2D>();
        if (_circle == null) _circle = GetComponentInChildren<CircleCollider2D>(true);
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
        ApplyWorkShaderParamsParticlesOnly(roleColor: _currentTint, workSigned01: 0f);
    }
    public void PreviewBoostWork(float effective01, float holdSeconds = -1f)
    {
        if (_isDespawned || _isBreaking) return;

        Visual_ChargeOnBoost(Mathf.Clamp01(effective01));

        float hold = (holdSeconds > 0f) ? holdSeconds : previewWorkHoldSeconds;
        _previewWorkToken++;
        int token = _previewWorkToken;

        if (_previewWorkRoutine != null)
        {
            StopCoroutine(_previewWorkRoutine);
            _previewWorkRoutine = null;
        }
        _previewWorkRoutine = StartCoroutine(ClearPreviewWorkAfter(token, hold));
    }

    private IEnumerator ClearPreviewWorkAfter(int token, float holdSeconds)
    {
        if (holdSeconds > 0f)
            yield return new WaitForSeconds(holdSeconds);

        if (token != _previewWorkToken)
            yield break;

        ResetVisualToBase();
        _previewWorkRoutine = null;
    }
    public void SetVisualTimings(DustVisualTimings t)
    {
        // Defensive clamps so bad inspector values can’t break everything.
        t.spriteScaleInSeconds  = Mathf.Max(0.01f, t.spriteScaleInSeconds);
        t.spriteScaleOutSeconds = Mathf.Max(0.01f, t.spriteScaleOutSeconds);
        t.particleGrowInSeconds = Mathf.Max(0.01f, t.particleGrowInSeconds);
        t.fadeOutSeconds        = Mathf.Max(0.01f, t.fadeOutSeconds);
        _timings = t;
    }


    private IEnumerator ScaleSpriteRoutine(float from, float to, float seconds)
    {
        Vector3 a = Vector3.one * from;
        Vector3 b = Vector3.one * to;

        float t = 0f;
        seconds = Mathf.Max(0.01f, seconds);

        visual.sprite.transform.localScale = a;

        while (t < seconds)
        {
            t += Time.deltaTime;
            float u = Mathf.SmoothStep(0f, 1f, t / seconds);
            visual.sprite.transform.localScale = Vector3.Lerp(a, b, u);
            yield return null;
        }

        visual.sprite.transform.localScale = b;
    }


    // Sprite alpha fades are presentation details (eg. regrow), and must respect the authored
    // tint alpha (eg. MusicalRoleProfile.baseColor.a). Do not overwrite _currentTint.a here.
    private void SetSpriteAlphaOnly(float a01)
    {
        if (visual.sprite == null) return;
        var c = visual.sprite.color;
        c.a = Mathf.Clamp01(a01);
        visual.sprite.color = c;
    }

    private void BeginFadeInSpriteAlpha(float durationSeconds, float targetAlpha)
    {
        if (visual.sprite == null) return;
        if (_fadeRoutine != null) { StopCoroutine(_fadeRoutine); _fadeRoutine = null; }
        _fadeRoutine = StartCoroutine(FadeSpriteAlphaRoutine(0f, Mathf.Clamp01(targetAlpha), durationSeconds));
    }

    private IEnumerator FadeSpriteAlphaRoutine(float from, float to, float seconds)
    {
        seconds = Mathf.Max(0.01f, seconds);
        float t = 0f;
        while (t < seconds)
        {
            t += Time.deltaTime;
            float u = Mathf.Clamp01(t / seconds);
            SetSpriteAlphaOnly(Mathf.Lerp(from, to, u));
            yield return null;
        }
        SetSpriteAlphaOnly(to);
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

    public void ConfigureForPhase(MazeArchetype phase)
    {
        // One switch → one config object → one assignment block.
        var cfg = phase switch
        {
            MazeArchetype.Establish => new PhaseDustConfig(
                scaleMul:   0.25f,
                drainMul:   0.50f,
                behavior:   DustBehavior.SiltDissipate,
                slowFactor: 0.8f,
                slowDur:    0.25f,
                lateral:    0f,
                turb:       0f
            ),

            MazeArchetype.Evolve => new PhaseDustConfig(
                scaleMul:   1.00f,
                drainMul:   1.00f,
                behavior:   DustBehavior.CrossCurrent,
                slowFactor: 0.9f,
                slowDur:    0.2f,
                lateral:    2.5f,
                turb:       0.25f
            ),

            MazeArchetype.Intensify => new PhaseDustConfig(
                scaleMul:   1.20f,
                drainMul:   1.60f,
                behavior:   DustBehavior.StaticCling,
                slowFactor: 0.5f,
                slowDur:    0.5f,
                lateral:    0.5f,
                turb:       0.4f
            ),

            MazeArchetype.Release => new PhaseDustConfig(
                scaleMul:   1.00f,
                drainMul:   0.90f,
                behavior:   DustBehavior.SiltDissipate,
                slowFactor: 0.85f,
                slowDur:    0.2f,
                lateral:    0f,
                turb:       0f
            ),

            MazeArchetype.Wildcard => new PhaseDustConfig(
                scaleMul:   1.10f,
                drainMul:   1.25f,
                behavior:   DustBehavior.Turbulent,
                slowFactor: 0.7f,
                slowDur:    0.4f,
                lateral:    1.2f,
                turb:       2.0f
            ),

            MazeArchetype.Pop => new PhaseDustConfig(
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

        RebuildColliderForCurrentScale();
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
        SetVisualsEnabled(true);
        SetColorVariance();
        ResetAndPlayParticles();
        ApplyParticleFootprint();
        CaptureBaseVisual();

        // Rule #2: regrow should fade sprite alpha back to 1 (not pop).
        SetSpriteAlphaOnly(0f);

        ResetSpriteScaleTo(0f);
        AnimateSpriteScale(0f, 1f); // uses _timings.spriteScaleInSeconds
        // Fade back to the authored tint alpha (not a hardcoded value).
        BeginFadeInSpriteAlpha(_timings.spriteScaleInSeconds, _currentTint.a);

        _growInRoutine = StartCoroutine(GrowIn());
    }

    public void SetTrackBundle(CosmicDustGenerator _dustGenerator, DrumTrack _drums)
    {
        gen = _dustGenerator;
        _drumTrack = _drums;
            }
    public void SetTint(Color tint)
    {
        _currentTint = tint;
        if (visual.sprite != null) 
            visual.sprite.color = tint;
        
        if (!visual.particleSystem) return;
        
        // For particles we still want "base hue" to match tint, but interaction feedback modifies particles.
        if (useWorkShaderParams) { 
            // Keep particle vertex/gradient neutral; hue comes from _RoleColor on the particle material.
            SetDustColorAllParticles(new Color(1f, 1f, 1f, tint.a));
            ApplyWorkShaderParamsParticlesOnly(roleColor: tint, workSigned01: _workSigned01);
        }
        else { 
            // Legacy: bake hue into gradient
            SetDustColorAllParticles(tint);
        }
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

    public void SetGrowInDuration(float seconds) { _growInOverride = Mathf.Max(0.05f, seconds); }
    private static Color Premultiply(Color c)
    {
        c.r *= c.a;
        c.g *= c.a;
        c.b *= c.a;
        return c;
    }

    public void SetTerrainColliderEnabled(bool enabled)
    {
        // Prefer cached colliders so we reliably toggle whatever collider is actually producing contact.
        if (_cachedColliders == null || _cachedColliders.Length == 0)
            _cachedColliders = GetComponentsInChildren<Collider2D>(true);

        // Determine current effective state (defensive: prefab defaults can drift from our expectations).
        bool currentlyEnabled = false;
        if (_cachedColliders != null)
        {
            for (int i = 0; i < _cachedColliders.Length; i++)
            {
                var c = _cachedColliders[i];
                if (c != null && c.enabled) { currentlyEnabled = true; break; }
            }
        }
        if (terrainCollider != null && terrainCollider.enabled)
            currentlyEnabled = true;

        // If already in the desired state, do nothing.
        if (currentlyEnabled == enabled)
            return;

        if (_cachedColliders != null)
        {
            for (int i = 0; i < _cachedColliders.Length; i++)
            {
                var c = _cachedColliders[i];
                if (c != null) c.enabled = enabled;
            }
        }

        // Maintain legacy field behavior too.
        if (terrainCollider != null) terrainCollider.enabled = enabled;

        // When enabling collision, ensure CircleCollider2D radius matches the current sprite footprint.
        if (enabled)
        {
            RebuildColliderForCurrentScale();
            SyncColliderRadiusToSprite();
        }

    }
    public void DissipateAndHideVisualOnly(float fadeSeconds = -1) {
        if (_isDespawned) return;
        _isDespawned = true; 
        // Ensure no collision during fade.
        SetTerrainColliderEnabled(false);
        SetVisualsEnabled(false);
        float d = (fadeSeconds > 0f) ? fadeSeconds : _timings.spriteScaleOutSeconds;
        AnimateSpriteScale(1f, 0f, d);
        if (_fadeRoutine != null) { StopCoroutine(_fadeRoutine); _fadeRoutine = null; } 
        _fadeRoutine = StartCoroutine(FadeOutThenPoolVisualOnly(d));
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

        // Optional: if you want “stop blocking immediately”
        SetTerrainColliderEnabled(false);

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

        onComplete?.Invoke();
    }

    public void PrepareForReuse()
    {
        if (_fadeRoutine != null) { StopCoroutine(_fadeRoutine); _fadeRoutine = null; }
        if (_growInRoutine != null) { StopCoroutine(_growInRoutine); _growInRoutine = null; }
        if (_spriteScaleRoutine != null) { StopCoroutine(_spriteScaleRoutine); _spriteScaleRoutine = null; }
        SetVisualsEnabled(true);
        ResetSpriteScaleTo(0f);
        _isBreaking = false;
        _isDespawned = false;
        _nonBoostClearSeconds = 0f;
        _stayForceUntil = 0f;
        _shrinkingFromStar = false;
        SetWorkSigned01(0f);
        // Restore captured prefab/base scale instead of Vector3.one
        transform.localScale = (_baseLocalScale.sqrMagnitude > 0.0001f)
            ? _baseLocalScale
            : (visual.prefabReferenceScale.sqrMagnitude > 0.0001f ? visual.prefabReferenceScale : Vector3.one);

        SetTerrainColliderEnabled(false);
        if (visual.particleSystem != null)
        {
            var emission = visual.particleSystem.emission;
            emission.enabled = false;
            visual.particleSystem.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            visual.particleSystem.Clear(true);
        }

        _currentTint.a = 0f;
        SetTint(_currentTint);

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

// ---- Particle visibility control ----
// We treat particle emission as an on/off module toggle and never "cache whatever the current rate is"
// while hidden (which can permanently cache 0 and cause particles to stay off).
private ParticleSystem.MinMaxCurve[] _baseRateOverTime;
private bool[] _baseEmissionEnabled;
private bool _baseParticleEmissionCaptured;

// Captured from the prefab at runtime (root particle system) for preview effects.
private ParticleSystem.MinMaxCurve _baseEmissionCurve;
private bool _baseEmissionCurveCaptured;

private ParticleSystem[] GetAllParticleSystems()
{
    if (visual.particleSystem == null) return null;
    return visual.particleSystem.GetComponentsInChildren<ParticleSystem>(true);
}

private void EnsureBaseParticleEmissionCaptured()
{
    var systems = GetAllParticleSystems();
    if (systems == null || systems.Length == 0) return;

    if (_baseParticleEmissionCaptured && _baseRateOverTime != null &&
        _baseEmissionEnabled != null &&
        _baseRateOverTime.Length == systems.Length &&
        _baseEmissionEnabled.Length == systems.Length)
        return;

    _baseRateOverTime = new ParticleSystem.MinMaxCurve[systems.Length];
    _baseEmissionEnabled = new bool[systems.Length];

    for (int i = 0; i < systems.Length; i++)
    {
        var ps = systems[i];
        if (ps == null) continue;

        var em = ps.emission;
        _baseEmissionEnabled[i] = em.enabled;
        _baseRateOverTime[i] = em.rateOverTime;

        // Capture a single "base emission" curve for preview effects from the root system.
        if (!_baseEmissionCurveCaptured && ps == visual.particleSystem)
        {
            _baseEmissionCurve = em.rateOverTime;

            // Maintain the legacy scalar for any code paths that still use it.
            // Prefer constant if possible; otherwise fall back to max constant.
            try
            {
                if (_baseEmissionCurve.mode == ParticleSystemCurveMode.Constant)
                    _baseEmission = _baseEmissionCurve.constant;
                else if (_baseEmissionCurve.mode == ParticleSystemCurveMode.TwoConstants)
                    _baseEmission = _baseEmissionCurve.constantMax;
            }
            catch { /* defensive */ }

            _baseEmissionCurveCaptured = true;
        }
    }

    _baseParticleEmissionCaptured = true;
}

private static ParticleSystem.MinMaxCurve ScaleCurve(ParticleSystem.MinMaxCurve c, float mul)
{
    // Scale without destroying the authored curve modes.
    switch (c.mode)
    {
        case ParticleSystemCurveMode.Constant:
            return new ParticleSystem.MinMaxCurve(c.constant * mul);

        case ParticleSystemCurveMode.TwoConstants:
            return new ParticleSystem.MinMaxCurve(c.constantMin * mul, c.constantMax * mul);

        case ParticleSystemCurveMode.Curve:
            return new ParticleSystem.MinMaxCurve(c.curveMultiplier * mul, c.curve);

        case ParticleSystemCurveMode.TwoCurves:
            return new ParticleSystem.MinMaxCurve(c.curveMultiplier * mul, c.curveMin, c.curveMax);

        default:
            return c;
    }
}

public void SetVisualsEnabled(bool enabled)
{
    if (visual.sprite != null)
        visual.sprite.enabled = enabled;

    if (visual.particleSystem == null)
        return;

    EnsureBaseParticleEmissionCaptured();

    var systems = GetAllParticleSystems();
    if (systems == null || systems.Length == 0)
        return;

    if (enabled)
    {
        for (int i = 0; i < systems.Length; i++)
        {
            var ps = systems[i];
            if (ps == null) continue;

            var em = ps.emission;

            // Restore authored emission state + rate (do NOT "restore last seen", which might be 0).
            if (_baseEmissionEnabled != null && i < _baseEmissionEnabled.Length)
                em.enabled = _baseEmissionEnabled[i];
            else
                em.enabled = true;

            if (_baseRateOverTime != null && i < _baseRateOverTime.Length)
                em.rateOverTime = _baseRateOverTime[i];

            ps.Clear(true);
            ps.Play(true);
        }
    }
    else
    {
        for (int i = 0; i < systems.Length; i++)
        {
            var ps = systems[i];
            if (ps == null) continue;

            var em = ps.emission;
            em.enabled = false;

            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            ps.Clear(true);
        }
    }
	}
    public void HideVisualsInstant() {
     // Stop any running sprite scale/fade routines that might fight this.
     if (_fadeRoutine != null) { StopCoroutine(_fadeRoutine); _fadeRoutine = null; }
     if (_growInRoutine != null) { StopCoroutine(_growInRoutine); _growInRoutine = null; }
     if (_spriteScaleRoutine != null) { StopCoroutine(_spriteScaleRoutine); _spriteScaleRoutine = null; }
    
     // Make fully invisible.
     var c = _currentTint;
     c.a = 0f;
     SetTint(c);
    
     if (visual.sprite != null)
         visual.sprite.transform.localScale = Vector3.zero;
     SetTerrainColliderEnabled(false);
     SetVisualsEnabled(false);
    }

    private void SyncParticlesToCollider()
    {
        if (visual.particleSystem == null) return;

        // Prefer circle collider, then box, then any collider reference.
        Collider2D c = (Collider2D)_circle;
        if (c == null) c = (Collider2D)_box;
        if (c == null) c = terrainCollider;
        if (c == null)
        {
            if (_cachedColliders == null || _cachedColliders.Length == 0)
                _cachedColliders = GetComponentsInChildren<Collider2D>(true);
            if (_cachedColliders != null && _cachedColliders.Length > 0)
                c = _cachedColliders[0];
        }
        if (c == null) return;

        // Use collider bounds in WORLD space to drive particle shape scale.
        Vector3 size = c.bounds.size;

        var shape = visual.particleSystem.shape;
        if (shape.enabled)
            shape.scale = new Vector3(size.x, size.y, 1f);
    }
    private void ApplyParticleFootprint()
    {
        if (visual.particleSystem == null) return;
        
        var main = visual.particleSystem.main;

        float mul = Mathf.Clamp(visual.particleFootprintMul, 0.05f, 2.0f);
        float size = _cellWorldSize * mul;
        main.startSize = size;

    }

    private void CaptureBaseVisual()
    {
        if (visual.particleSystem == null) return;
        EnsureBaseParticleEmissionCaptured();
        var main = visual.particleSystem.main;

        _baseColor = main.startColor.color;
        _baseSize  = main.startSize.constant;
        _baseAlpha = _baseColor.a;
        _baseCaptured = true;
        
    }

    private IEnumerator GrowIn()
    {

        float duration = (_growInOverride > 0f)
            ? _growInOverride
            : _timings.particleGrowInSeconds;

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
// ------------- TIMING HELPERS -------------
    private float ResolveScaleSeconds(float from, float to, float seconds)
    {
        if (seconds > 0f) return seconds;

        // Default based on direction.
        // Assumes your struct has these fields; if names differ, align them to your actual DustVisualTimings.
        return (to >= from) ? _timings.spriteScaleInSeconds : _timings.spriteScaleOutSeconds;
    }

    private float ResolveFadeOutSeconds(float seconds)
    {
        if (seconds > 0f) return seconds;
        return _timings.fadeOutSeconds;
    }

// ------------- SCALE -------------
    private void AnimateSpriteScale(float from, float to, float seconds = -1f)
    {
        float dur = ResolveScaleSeconds(from, to, seconds);

        if (_spriteScaleRoutine != null)
            StopCoroutine(_spriteScaleRoutine);

        _spriteScaleRoutine = StartCoroutine(ScaleSpriteRoutine(from, to, dur));
    }

    private void ResetSpriteScaleTo(float s)
    {
        if (visual.sprite == null) return;
        visual.sprite.transform.localScale = Vector3.one * s; // your current semantics are fine【turn20file14†CosmicDust.cs†L93-L96】
    }

// ------------- FADE -------------
    private void BeginFadeOutVisualOnly(float duration = -1f, System.Action onComplete = null)
    {
        float dur = ResolveFadeOutSeconds(duration);

        if (_fadeRoutine != null) StopCoroutine(_fadeRoutine);
        _fadeRoutine = StartCoroutine(FadeOutVisualOnly(dur, onComplete));
    }

    private IEnumerator FadeOutThenPoolVisualOnly(float duration = -1f)
    {
        float dur = ResolveFadeOutSeconds(duration);

        // run the fade as a coroutine we can truly await
        yield return FadeOutVisualOnly(dur, onComplete: null);

        // after fade completes, notify generator (or hide locally)
        if (gen != null) gen.OnDustVisualFadedOut(this);
        else HideVisualsInstant();
    }
    private void OnCollisionStay2D(Collision2D collision)
    {
        if (_isDespawned || _isBreaking) return;
        if (gen == null || _drumTrack == null) return;

        var vehicle = collision.collider != null ? collision.collider.GetComponent<Vehicle>() : null;
        if (vehicle == null) return; 
        // --- Clearing rules ---
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
    private void Visual_DenyOnBump(float severity01)
    {
        if (visual.particleSystem == null) return;
        if (!_baseCaptured) CaptureBaseVisual();

        float s = Mathf.Clamp01(severity01);

        var ps = visual.particleSystem;
        var main = ps.main;

        // Particle tint: blend from base -> denyColor. Keep alpha consistent.
        Color baseCol = _currentTint;
        baseCol.a = _baseAlpha;

        Color target = _hasFeedbackColors ? _denyColor : Color.black;
        target.a = _baseAlpha;

        Color denyCol = Color.Lerp(baseCol, target, s);
        SetDustColorAllParticles(denyCol);

        // Optional size response on deny
        main.startSize = _baseSize * Mathf.Lerp(1.00f, 1.12f, s);
    }
    private void ApplyWorkShaderParamsParticlesOnly(Color roleColor, float workSigned01)
    {
        // Shader is retired; interpret workSigned01 as:
        //  0 -> base tint (roleColor)
        // >0 -> charge tint
        // <0 -> deny tint

        if (visual.particleSystem == null) return;
        if (!_baseCaptured) CaptureBaseVisual();

        float w = Mathf.Clamp(workSigned01, -1f, 1f);

        Color baseCol = roleColor;
        baseCol.a = _baseAlpha;

        if (Mathf.Abs(w) < 0.0001f)
        {
            SetDustColorAllParticles(baseCol);
            return;
        }

        if (w > 0f)
        {
            Color target = _hasFeedbackColors ? _chargeColor : Color.white;
            target.a = _baseAlpha;
            SetDustColorAllParticles(Color.Lerp(baseCol, target, w));
        }
        else
        {
            Color target = _hasFeedbackColors ? _denyColor : Color.black;
            target.a = _baseAlpha;
            SetDustColorAllParticles(Color.Lerp(baseCol, target, -w));
        }
    }
    private void SetWorkSigned01(float workSigned01)
    {
        // Retained for callers like ResetVisualToBase().
        ApplyWorkShaderParamsParticlesOnly(roleColor: _currentTint, workSigned01: workSigned01);
    }
    private void Visual_ChargeOnBoost(float appetite01)
    {
        if (visual.particleSystem == null) return;
        if (!_baseCaptured) CaptureBaseVisual();

        float a = Mathf.Clamp01(appetite01);

        var ps = visual.particleSystem;
        var main = ps.main;

        // Particle tint: blend from base -> chargeColor. Keep alpha consistent.
        Color baseCol = _currentTint;
        baseCol.a = _baseAlpha;

        Color target = _hasFeedbackColors ? _chargeColor : Color.white;
        target.a = _baseAlpha;

        Color chargeCol = Color.Lerp(baseCol, target, a);
        SetDustColorAllParticles(chargeCol);

        // Optional size/emission response (keep as-is)
        main.startSize = _baseSize * Mathf.Lerp(1.05f, 1.25f, a);

        var emission = ps.emission;
        EnsureBaseParticleEmissionCaptured();
        float mul = Mathf.Lerp(1.0f, 1.15f, a);
        if (_baseEmissionCurveCaptured)
            emission.rateOverTime = ScaleCurve(_baseEmissionCurve, mul);
        else
            emission.rateOverTime = _baseEmission * mul;
    }
    public void SetFeedbackColors(Color chargeColor, Color denyColor)
    {
        _chargeColor = chargeColor;
        _denyColor = denyColor;
        _hasFeedbackColors = true;
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
        // Reset shader param #2 back to neutral.
        SetWorkSigned01(0f);
        if (visual.sprite != null) 
            visual.sprite.color = _currentTint;
        if (visual.particleSystem == null) return;
        if (!_baseCaptured) return;
        var main = visual.particleSystem.main;
        main.startSize = _baseSize;
        
        // Restore emission if we captured it.
        var emission = visual.particleSystem.emission;
        EnsureBaseParticleEmissionCaptured();
        if (_baseEmissionCurveCaptured)
            emission.rateOverTime = _baseEmissionCurve;
        else
            emission.rateOverTime = _baseEmission;
        Debug.Log($"[REGROWTH] Reset Visual To Base");
    }

    public void SetCellSizeDrivenScale(float cellWorldSize, float footprintMul = 1.15f, float clearanceWorld = 0f)
    {
        _cellWorldSize       = Mathf.Max(0.001f, cellWorldSize);
        _cellClearanceWorld  = Mathf.Max(0f, clearanceWorld);
//        transform.localScale = Vector3.one;
        ApplyParticleFootprint();
        RebuildColliderForCurrentScale();
        SyncParticlesToCollider();
    }
    private void RebuildColliderForCurrentScale()
    {
        // Keep legacy BoxCollider2D support, but prefer CircleCollider2D for the new grid-of-circles approach.
        // If neither is present, we do nothing.
        if (_circle != null)
        {
            // For circle tiles, radius is driven by the sprite footprint (Rule #3).
            SyncColliderRadiusToSprite();
            return;
        }

        if (_box == null) return;

        float desiredWorld = Mathf.Clamp(_cellWorldSize - _cellClearanceWorld, 0.001f, _cellWorldSize);

        // Convert desired WORLD size into LOCAL size accounting for lossy scale.
        // This prevents oversized colliders when the dust root or prefab is scaled.
        float sx = Mathf.Max(0.0001f, Mathf.Abs(transform.lossyScale.x));
        float sy = Mathf.Max(0.0001f, Mathf.Abs(transform.lossyScale.y));

        _box.size   = new Vector2(desiredWorld / sx, desiredWorld / sy);
        _box.offset = Vector2.zero;
    }

    private void SyncColliderRadiusToSprite()
    {
        // Rule #3: collider radius should match the size of the visual sprite.
        if (_circle == null || visual.sprite == null) return;

        // Sprite bounds are in world space and already include current transform scale.
        float worldRadius = Mathf.Max(0.0001f, Mathf.Max(visual.sprite.bounds.extents.x, visual.sprite.bounds.extents.y));

        // Convert world radius to collider-local radius (CircleCollider2D.radius is in the collider's local space).
        float sx = Mathf.Max(0.0001f, Mathf.Abs(_circle.transform.lossyScale.x));
        float sy = Mathf.Max(0.0001f, Mathf.Abs(_circle.transform.lossyScale.y));
        float lossy = Mathf.Max(sx, sy);

        _circle.radius = worldRadius / lossy;
        _circle.offset = Vector2.zero;
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