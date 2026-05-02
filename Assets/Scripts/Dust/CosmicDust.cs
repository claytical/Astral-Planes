using System;
using System.Collections;
using System.Linq;
using UnityEngine;
using Random = UnityEngine.Random;

public partial class CosmicDust : MonoBehaviour {
    [Serializable]
    public struct DustVisualSettings {
        [Header("Sizing (prefab baseline)")]
        public Vector3 prefabReferenceScale; // authored prefab baseline (cell-driven scale overrides at runtime)
        public ParticleSystem particleSystem;

        [Header("Visual Footprint")]
        [Range(0.5f, 1.6f)] public float particleFootprintMul; // % of cell; prevents edge bleed
        
        public SpriteRenderer sprite;
    }
    [System.Serializable]
    public struct DustVisualTimings
    {
        [Min(0.01f)] public float regrowSpriteScaleInSeconds; // regrow sprite scale 0->1
        [Min(0.01f)] public float clearSpriteScaleOutSeconds; // clear sprite scale 1->0
        [Min(0.01f)] public float regrowParticleGrowInSeconds; // particle emission/alpha ramp on regrow
        [Min(0.01f)] public float clearFadeOutSeconds; // clear tint/alpha fade out (if used)

        public static DustVisualTimings Default => new DustVisualTimings
        {
            regrowSpriteScaleInSeconds = 0.20f,
            clearSpriteScaleOutSeconds = 0.20f,
            regrowParticleGrowInSeconds = 1.00f,
            clearFadeOutSeconds = 0.20f
        };

        public DustVisualTimings Sanitized()
        {
            var sanitized = this;
            sanitized.regrowSpriteScaleInSeconds = Mathf.Max(0.01f, sanitized.regrowSpriteScaleInSeconds);
            sanitized.clearSpriteScaleOutSeconds = Mathf.Max(0.01f, sanitized.clearSpriteScaleOutSeconds);
            sanitized.regrowParticleGrowInSeconds = Mathf.Max(0.01f, sanitized.regrowParticleGrowInSeconds);
            sanitized.clearFadeOutSeconds = Mathf.Max(0.01f, sanitized.clearFadeOutSeconds);
            return sanitized;
        }
    }

    private DustVisualTimings _timings;
    private bool _visualTimingsInitialized;
    private float _nextDustPluckTime = -999f;
    private Vehicle _currentPluckVehicle = null;
    // Energy unit backing fields. Charge01 is derived — do NOT write to it directly.
    private int _maxEnergyUnits = 1;
    private int _currentEnergyUnits = 1;
    public int maxEnergyUnits => _maxEnergyUnits;
    public int currentEnergyUnits => _currentEnergyUnits;
    public float Charge01 => (float)_currentEnergyUnits / Mathf.Max(1, _maxEnergyUnits);
    public MusicalRole Role { get; private set; } = MusicalRole.None;

    public event Action<float> OnChargeChanged;
    public event Action<MusicalRole> OnRoleChanged;
    public event Action<bool> OnCollisionStateChanged;
    public event Action<Color> OnTintStateChanged;
    public event Action OnSpawnVisualRequested;
    public event Action<float> OnClearVisualRequested;

// Authoritative/resting tint of this dust cell.
// This is what retinting, charge drain, role assignment, etc. should modify.
    public Color CurrentTint => _currentTint;

// Currently displayed tint on the sprite.
// Temporary pulses modify this, but must not overwrite _currentTint.
    private Color _displayTint = Color.white;
    [SerializeField] private Color _chargeColor = Color.white;
    [SerializeField] private Color _denyColor = Color.magenta;
    private bool _hasFeedbackColors = false;
    public bool regrowAlphaCapped = false;
    private const float kRegrowAlphaCap = 0.20f;
    [SerializeField] private float colliderDisabledAlpha = 0.08f;
    private const float kSolidAlphaFloor = .55f;
    [Serializable]
    public struct DustInteractionSettings
    {
        [Header("Energy Drain")]
        [Min(0f)] public float energyDrainPerSecond;
    }
    [Serializable]
    public struct DustClearingSettings
    {
        [Header("Imprint (from MineNodes)")]
        [Range(0f, 1f)] public float hardness01; // Legacy — kept for migration. Use carveResistance01 / drainResistance01.

        [Header("Two-Axis Resistance")]
        [Range(0f, 1f)] public float carveResistance01; // Vehicle plow resistance. 0=instant, 1=nearly indestructible.
        [Range(0f, 1f)] public float drainResistance01; // PhaseStar drain resistance. 0=drains fast, 1=very slow.
    }
    [SerializeField] private DustVisualSettings visual = new DustVisualSettings
    {
        prefabReferenceScale   = new Vector3(0.75f, 0.75f, 1f),
        particleFootprintMul   = 0.85f
    };

    [SerializeField] private DustInteractionSettings interaction = new DustInteractionSettings
    {
        energyDrainPerSecond  = 0.01f
    };

    [SerializeField] public DustClearingSettings clearing = new DustClearingSettings
    {
        hardness01                = 0
    };
    [Header("Shader Params")]
    [SerializeField] private bool useWorkShaderParams = true;
    private MaterialPropertyBlock _mpb;
    private float _workSigned01 = 0f;

    [Header("Work / Preview (Boost Path)")]
    private Coroutine _previewWorkRoutine;
    private Vector3 _initialLocalScale = Vector3.one;
    private bool _cachedInitialScale;
    private Vector3 _baseLocalScale = Vector3.one;
// ---- Particle visibility control ----
// We treat particle emission as an on/off module toggle and never "cache whatever the current rate is"
// while hidden (which can permanently cache 0 and cause particles to stay off).
    private ParticleSystem.MinMaxCurve[] _baseRateOverTime;
    private bool[] _baseEmissionEnabled;
    private bool _baseParticleEmissionCaptured;

// Captured from the prefab at runtime (root particle system) for preview effects.
    private ParticleSystem.MinMaxCurve _baseEmissionCurve;
    private bool _baseEmissionCurveCaptured;

    [SerializeField] public Collider2D terrainCollider;
    // Carve/disable must be authoritative, so we cache all colliders in the hierarchy.
    private Collider2D[] _cachedColliders;
    private BoxCollider2D _box;
    private CircleCollider2D _circle;
    private float _nonBoostClearSeconds;
    private float _cellWorldSize = 1f;
    private float _cellClearanceWorld = 0f;
    // The sprite animates to this scale (1.0 = exactly fills cell, >1.0 = overlap, <1.0 = gap).
    // Set by SetCellSizeDrivenScale via the generator's dustFootprintMul.
    private float _spriteScaleTarget = 1f;
    private Color _baseColor;
    private float _baseSize;
    private float _baseAlpha;
    private bool _baseCaptured;

    private bool _isDespawned;
    private bool _shrinkingFromStar;
    private Coroutine  _fadeRoutine, _growInRoutine;
    private DrumTrack _drumTrack;
    private CosmicDustGenerator gen;
    private CosmicDustVisualController _visualController;
    private float _growInOverride = -1f;

// Canonical/rest tint.
    private Color _currentTint = Color.white;

    private float _stayForceUntil;
    private bool _isBreaking;
    [SerializeField] private float _baseEmission = 1;
    private Coroutine _spriteScaleRoutine;
    private float _emissionMulCurrent = 1f;
    private Coroutine _emissionMulRoutine;

[Header("Deny Feedback")]
[SerializeField, Tooltip("Fallback deny pulse duration when no explicit pulse length is provided.")]
private float denyPulseDefaultSeconds = 0.25f;

// Single managed tint pulse lane (charge + deny both use this).
private Coroutine _tintPulseRoutine;
private int _tintPulseToken = 0;
private bool _tintPulseActive = false;
private Coroutine _jiggleRoutine;
    void Awake() {
        _visualController = GetComponent<CosmicDustVisualController>();
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
        
        // If the prefab has PlayOnAwake accidentally enabled, force idle so pool/prewarm won't explode.
// --- Particles: always visible/running, default emission on boot ---
        if (visual.particleSystem != null)
        {
            EnsureBaseParticleEmissionCaptured();

            // Ensure it's actually running (no Stop/Clear usage anywhere)
            EnsureParticlesPlaying();

            // Establish initial tint BEFORE emission so the first spawned particles aren't black.
            // Prefer authored sprite color if present.
            if (visual.sprite != null)
            {
                _currentTint = visual.sprite.color;
                _displayTint = _currentTint;
                _displayTint.a = kRegrowAlphaCap;
                _dustSpriteBaseLocalPos = visual.sprite.transform.localPosition;
            }
            
            // Apply tint to sprite + particle material/gradient.
            SetTint(_currentTint);

            // Start at default emission (mul = 1). Carved state will drive this to 0 later.
            ApplyEmissionMultiplierImmediate(100f);

            // Keep root particle transform stable in pooled scenarios
            visual.particleSystem.transform.localScale = Vector3.one;
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
        // Belt-and-suspenders: make sure SpriteMask can’t clip us accidentally
        var psr = GetComponent<ParticleSystemRenderer>();
        if (psr) psr.maskInteraction = SpriteMaskInteraction.None;
        if (psr) psr.sortingFudge = 0f;
        ApplyParticleFootprint();
        ApplyWorkShaderParamsParticlesOnly(roleColor: _currentTint, workSigned01: 0f);
    }

    void Update()
    {
        TickVehicleCompression();
    }
    /// <summary>
    /// Called by the generator when a cell becomes Solid. Ensures the sprite alpha
    /// is at least <paramref name="minAlpha"/> so the cell is never invisible-but-solid.
    /// </summary>
    public void EnsureMinSolidAlpha(float minAlpha)
    {
        if (_currentTint.a < minAlpha)
        {
            _currentTint.a = minAlpha;
            // Ensure at least 1 energy unit so the cell is solid.
            if (_currentEnergyUnits <= 0)
                _currentEnergyUnits = 1;
        }
        ApplyDisplayedTint(_currentTint);
        OnChargeChanged?.Invoke(Charge01);
    }
    public void InitializeVisuals(DustVisualTimings settings)
    {
        _timings = settings.Sanitized();
        _visualTimingsInitialized = true;
    }
    private IEnumerator ScaleSpriteRoutine(float from, float to, float seconds)
    {
        Vector3 a = Vector3.one * from;
        Vector3 b = Vector3.one * to;

        float t = 0f;
        seconds = Mathf.Max(0.01f, seconds);

        SetBaseSpriteScale(a);

        while (t < seconds)
        {
            t += Time.deltaTime;
            float u = Mathf.SmoothStep(0f, 1f, t / seconds);
            SetBaseSpriteScale(Vector3.Lerp(a, b, u));
            yield return null;
        }

        SetBaseSpriteScale(b);

        if (to > from) _growInOverride = -1f;
    }
    // Sprite alpha fades are presentation details (eg. regrow), and must respect the authored
    // tint alpha (eg. MusicalRoleProfile.baseColor.a). Do not overwrite _currentTint.a here.
    private float GetWorldRadius()
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

    // maxUnits: pass > 0 to update the cell's max energy units (from a role profile).
    // Pass -1 (default) to keep the current max.
    public void ApplyRoleAndCharge(MusicalRole r, Color roleColorRgb, float charge, int maxUnits = -1)
    {
        Role = r;
        OnRoleChanged?.Invoke(Role);
        if (maxUnits > 0) _maxEnergyUnits = maxUnits;
        SetEnergyUnits(Mathf.RoundToInt(Mathf.Clamp01(charge) * _maxEnergyUnits));
        float visibleAlpha = Mathf.Lerp(kSolidAlphaFloor, 1f, Charge01);
        roleColorRgb.a = visibleAlpha;
        // Alpha comes from the caller (roleProfile.baseAlpha via GetBaseColor()).
        // Do NOT floor it here — cells that aren't Solid yet must stay dim.
        // The generator enforces a visible-alpha floor when solidifying.
        SetBaseTint(roleColorRgb, applyImmediatelyIfNoPulse: false);
        OnTintStateChanged?.Invoke(_currentTint);
        OnChargeChanged?.Invoke(Charge01);
    }

    // Sets current energy units, clamped [0, max]. Updates tint alpha accordingly.
    public void SetEnergyUnits(int units)
    {
        _currentEnergyUnits = Mathf.Clamp(units, 0, _maxEnergyUnits);
        float visibleAlpha = Mathf.Lerp(kSolidAlphaFloor, 1f, Charge01);
        _currentTint.a = visibleAlpha;
        OnTintStateChanged?.Invoke(_currentTint);
        OnChargeChanged?.Invoke(Charge01);
    }

    // Decrements energy units by amount. Drives visual drain. Returns actual units removed.
    // When units hit 0, disables the terrain collider — but does NOT call the generator.
    // Callers are responsible for generator-level cleanup (ClearCell vs ResetDustToNoneInPlace).
    public int ChipEnergy(int amount)
    {
        int actual = Mathf.Min(_currentEnergyUnits, Mathf.Max(0, amount));
        if (actual <= 0) return 0;
        _currentEnergyUnits -= actual;

        // Lerp RGB toward gray as energy depletes, and drop alpha too.
        // At Charge01=1: full role color. At Charge01=0: gray at low alpha.
        Color gray = new Color(0.3f, 0.3f, 0.3f, 0f);
        Color full = _currentTint;
        full.a = 1f;
        Color drained = Color.Lerp(gray, full, Charge01);
        drained.a = Mathf.Lerp(0.05f, _currentTint.a, Charge01);

        _currentTint = drained;
        OnTintStateChanged?.Invoke(_currentTint);
        OnChargeChanged?.Invoke(Charge01);

        if (_currentEnergyUnits <= 0)
            SetTerrainColliderEnabled(false);

        return actual;
    }

    // Deprecated shim — converts a 0-1 charge fraction to integer units and delegates to ChipEnergy.
    // Does NOT call the generator on depletion — callers are responsible for post-depletion cleanup.
    private float DrainCharge(float amount)
    {
        if (amount <= 0f) return 0f;
        int chipAmount = Mathf.RoundToInt(amount * _maxEnergyUnits);
        // Guard against sub-unit precision: always chip at least 1 when amount is nonzero.
        if (chipAmount <= 0 && _currentEnergyUnits > 0) chipAmount = 1;
        int actual = ChipEnergy(chipAmount);
        return (float)actual / Mathf.Max(1, _maxEnergyUnits);
    }
    
    [Obsolete("Compatibility wrapper. Prefer visual-controller driven spawn presentation.")]
    public void Begin()
    {
        if (!gameObject.activeInHierarchy)
        {
            Debug.LogWarning($"[COSMIC DUST] Begin called while inactive on {name}", this);
            return;
        }

        if (OnSpawnVisualRequested != null)
        {
            OnSpawnVisualRequested.Invoke();
            return;
        }

        // Backstop for lifecycle ordering: if Begin is called before the visual controller
        // subscribes (or if no controller is present), still run the spawn visuals.
        RunSpawnVisuals(ResolveGrowDurationSeconds());
    }
    /// <summary>
    /// Syncs both the sprite renderer AND the particle system to _currentTint.
    /// Call after ApplyRoleAndCharge when the cell is already live (not growing in),
    /// e.g. after MineNode tinting. ApplyRoleAndCharge alone only updates the sprite;
    /// particles keep their birth color until explicitly refreshed.
    /// </summary>
    [Obsolete("Compatibility wrapper. Prefer state/event-driven updates via CosmicDustVisualController.")]
    public void SyncParticleColor()
    {
        if (useWorkShaderParams)
            ApplyWorkShaderParamsParticlesOnly(roleColor: _currentTint, workSigned01: 0f);
        else
            SetDustColorAllParticles(_currentTint);
    }
    public void SetTrackBundle(CosmicDustGenerator _dustGenerator, DrumTrack _drums)
    {
        gen = _dustGenerator;
        _drumTrack = _drums;
    }
    private void ApplyDisplayedTint(Color tint)
    {
        _displayTint = tint;

        if (visual.sprite != null)
        {
            Color applied = tint;
            if (regrowAlphaCapped)
                applied.a = Mathf.Min(applied.a, kRegrowAlphaCap);
            visual.sprite.color = applied;
        }
    }
    private void SetBaseTint(Color tint, bool applyImmediatelyIfNoPulse = true)
    {
        // Preserve _currentTint.a — it is authoritative charge state managed only by
        // DrainCharge, ApplyRoleAndCharge, and EnsureMinSolidAlpha. Callers of SetBaseTint
        // (diffusion, retinting) must not reset drain progress.
        tint.a = _currentTint.a;
        _currentTint = tint;

        if (!_tintPulseActive || applyImmediatelyIfNoPulse)
            ApplyDisplayedTint(_currentTint);
    }
    private void RestoreDisplayToBaseTint()
    {
        _tintPulseActive = false;
        ApplyDisplayedTint(_currentTint);
    }
    private void CancelTintPulse(bool restoreToBase)
    {
        _tintPulseToken++;

        if (_tintPulseRoutine != null)
        {
            StopCoroutine(_tintPulseRoutine);
            _tintPulseRoutine = null;
        }

        _tintPulseActive = false;

        if (restoreToBase)
            RestoreDisplayToBaseTint();
    }
    [Obsolete("Compatibility wrapper. Prefer state/event-driven tint updates via CosmicDustVisualController.")]
    public void SetTint(Color tint)
    {
        SetBaseTint(tint, applyImmediatelyIfNoPulse: true);
        OnTintStateChanged?.Invoke(_currentTint);

        // Particles: leave authored color/gradient/material alone.
        // (If you later want particle tinting, do it as an explicit opt-in path.)
    }
    private void SetDustColorAllParticles(Color target)
    {
        if (visual.particleSystem == null) return;

        var ps   = visual.particleSystem;
        var main = ps.main;
        var col  = ps.colorOverLifetime;

        col.enabled = true;
    }
    public void SetGrowInDuration(float seconds)
    {
        _growInOverride = Mathf.Max(0.05f, seconds);
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

        // Mirror collider state onto sprite alpha so non-interactive cells read as ghost/faint.
        if (visual.sprite != null)
        {
            Color c = _displayTint;
            if (enabled)
            {
                c.a = kRegrowAlphaCap;
            }
            else
            {
                c.a = colliderDisabledAlpha;
            }
            ApplyDisplayedTint(c);
        }

        // When enabling collision, ensure CircleCollider2D radius matches the current sprite footprint.
        if (enabled)
        {
            RebuildColliderForCurrentScale();
            SyncColliderRadiusToSprite();
        }

        OnCollisionStateChanged?.Invoke(enabled);
    }
    [Obsolete("Compatibility wrapper. Prefer visual-controller driven clear presentation.")]
    public void DissipateAndHideVisualOnly(float fadeSeconds = -1f)
    {
        if (_isDespawned) return;
        _isDespawned = true;

        float d = (fadeSeconds > 0f) ? fadeSeconds : _timings.clearSpriteScaleOutSeconds;
        if (OnClearVisualRequested != null)
            OnClearVisualRequested.Invoke(d);
        else
            RunClearVisuals(d);

        // Notify generator after the carve-out read, so it can finalize state bookkeeping.
        if (_fadeRoutine != null) { StopCoroutine(_fadeRoutine); _fadeRoutine = null; }
        _fadeRoutine = StartCoroutine(NotifyGeneratorAfter(d));
    }
    internal float ResolveGrowDurationSeconds()
    {
        if (!_visualTimingsInitialized)
            throw new InvalidOperationException($"CosmicDust '{name}' must be initialized with InitializeVisuals before Begin.");
        float growSeconds = (_growInOverride > 0f) ? _growInOverride : _timings.regrowSpriteScaleInSeconds;
        growSeconds = Mathf.Max(0.01f, growSeconds);
        _growInOverride = -1f;
        return growSeconds;
    }
    private IEnumerator NotifyGeneratorAfter(float seconds)
    {
        if (seconds > 0f) yield return new WaitForSeconds(seconds);

        if (gen != null) gen.OnDustVisualFadedOut(this);
        _fadeRoutine = null;
    }
    internal void ApplyTintVisual(Color tint)
    {
        ApplyDisplayedTint(tint);
        if (useWorkShaderParams)
            ApplyWorkShaderParamsParticlesOnly(roleColor: tint, workSigned01: _workSigned01);
        else
            SetDustColorAllParticles(tint);
        var explode = GetComponent<Explode>();
        if (explode != null) explode.SetTint(tint);
    }
    internal void RunSpawnVisuals(float growSeconds)
    {
        SetVisualsEnabled(true);
        SetColorVariance();
        ApplyParticleFootprint();
        CaptureBaseVisual();
        ResetSpriteScaleTo(0f);
        AnimateSpriteScale(0f, _spriteScaleTarget, growSeconds);
        EnsureParticlesPlaying();
        SetEmissionMultiplier(1f, seconds: growSeconds);
        ApplyTintVisual(_currentTint);
    }
    internal void RunClearVisuals(float fadeSeconds)
    {
        SetTerrainColliderEnabled(false);
        AnimateSpriteScale(1f, 0f, fadeSeconds);
        SetEmissionMultiplier(0f, seconds: fadeSeconds);
    }
    // Called by the generator when a Clearing -> Empty transition is finalized.
    // Keeps particles alive (no Stop/Clear), but ensures the cell is visually "carved".
    public void FinalizeClearedVisuals()
    {
        SetTerrainColliderEnabled(false);
        var dormantTint = _currentTint;
        dormantTint.a = 0.35f;
        ApplyDisplayedTint(dormantTint);
        SetVisualsEnabled(true);
        ResetSpriteScaleTo(0f);
        ApplyEmissionMultiplierImmediate(0f);
        
    }
    public void PrepareForReuse()
    {
        if (_fadeRoutine != null) { StopCoroutine(_fadeRoutine); _fadeRoutine = null; }
        if (_growInRoutine != null) { StopCoroutine(_growInRoutine); _growInRoutine = null; }
        if (_spriteScaleRoutine != null) { StopCoroutine(_spriteScaleRoutine); _spriteScaleRoutine = null; }
        if (_emissionMulRoutine != null) { StopCoroutine(_emissionMulRoutine); _emissionMulRoutine = null; }
        if (_jiggleRoutine != null) { StopCoroutine(_jiggleRoutine); _jiggleRoutine = null; }
        CancelTintPulse(restoreToBase: false);
        _isBreaking = false;
        regrowAlphaCapped = false;
        _isDespawned = false;
        _nonBoostClearSeconds = 0f;
        _stayForceUntil = 0f;
        _shrinkingFromStar = false;
        SetWorkSigned01(0f);

        // Restore captured prefab/base scale instead of Vector3.one
        transform.localScale = (_baseLocalScale.sqrMagnitude > 0.0001f)
            ? _baseLocalScale
            : (visual.prefabReferenceScale.sqrMagnitude > 0.0001f ? visual.prefabReferenceScale : Vector3.one);

        // Invisible until Begin() is called.
        SetTerrainColliderEnabled(false);
        SetVisualsEnabled(true);
        ResetSpriteScaleTo(0f);

        // Re-enable particle renderers (HideVisualsInstant disables them; PrepareForReuse must
        // restore them so Begin()'s emission ramp is actually visible).
        if (visual.particleSystem != null)
        {
            var systems = GetAllParticleSystems();
            if (systems != null)
                for (int _i = 0; _i < systems.Length; _i++)
                {
                    if (systems[_i] == null) continue;
                    var _r = systems[_i].GetComponent<ParticleSystemRenderer>();
                    if (_r != null) _r.enabled = true;
                }
        }

        // Keep particles running but quiet until Begin() restores default emission.
        EnsureParticlesPlaying();
        ApplyEmissionMultiplierImmediate(0f);
        var dormantTint = _currentTint;
        dormantTint.a = 0.35f;
        ApplyDisplayedTint(dormantTint);
        clearing.hardness01        = 0f;
        clearing.carveResistance01 = 0f;
        clearing.drainResistance01 = 0f;
        _maxEnergyUnits            = 1;
        _currentEnergyUnits        = 1;
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
            SetBaseTint(now, applyImmediatelyIfNoPulse: false);
            yield return null;
        }
        SetBaseTint(toTint, applyImmediatelyIfNoPulse: true);    }    
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
// ---- Particle emission control ----
// Particle systems should stay running to avoid abrupt "loop pops" caused by Stop/Clear.
// We only modulate emission rate (rateOverTime) via a multiplier.
    private void EnsureParticlesPlaying()
    {
        if (visual.particleSystem == null) return;

        var systems = GetAllParticleSystems();
        if (systems == null || systems.Length == 0) return;

        for (int i = 0; i < systems.Length; i++)
        {
            var ps = systems[i];
            if (ps == null) continue;

            // Do NOT touch main module settings or renderer flags here.
            // Only ensure the system is running so emission ramps are continuous.
            if (!ps.isPlaying)
                ps.Play(true);
        }
    }
    private void ApplyEmissionMultiplierImmediate(float mul01)
{
    if (visual.particleSystem == null) return;

    EnsureBaseParticleEmissionCaptured();
    EnsureParticlesPlaying();

    var systems = GetAllParticleSystems();
    if (systems == null || systems.Length == 0) return;

    float mul = Mathf.Max(0f, mul01);
    _emissionMulCurrent = mul;

    for (int i = 0; i < systems.Length; i++)
    {
        var ps = systems[i];
        if (ps == null) continue;

        var em = ps.emission;
        em.enabled = true;

        if (_baseRateOverTime != null && i < _baseRateOverTime.Length)
            em.rateOverTime = ScaleCurve(_baseRateOverTime[i], mul);
    }
}
    private IEnumerator LerpEmissionMultiplier(float from, float to, float seconds)
{
    seconds = Mathf.Max(0.01f, seconds);
    float t = 0f;

    while (t < seconds)
    {
        t += Time.deltaTime;
        float u = Mathf.SmoothStep(0f, 1f, t / seconds);
        ApplyEmissionMultiplierImmediate(Mathf.Lerp(from, to, u));
        yield return null;
    }

    ApplyEmissionMultiplierImmediate(to);
    _emissionMulRoutine = null;
}
    private void SetEmissionMultiplier(float targetMul, float seconds = 0f)
{
    targetMul = Mathf.Max(0f, targetMul);

    if (_emissionMulRoutine != null)
    {
        StopCoroutine(_emissionMulRoutine);
        _emissionMulRoutine = null;
    }

    if (seconds <= 0f)
    {
        ApplyEmissionMultiplierImmediate(targetMul);
        return;
    }

    _emissionMulRoutine = StartCoroutine(LerpEmissionMultiplier(_emissionMulCurrent, targetMul, seconds));
}

    private void SetVisualsEnabled(bool enabled)
{
    // In the simplified model, we only toggle sprite visibility.
    // Particles stay enabled; their "presence" is driven by emission rate (SetEmissionMultiplier).
    if (visual.sprite != null)
        visual.sprite.enabled = enabled;
}
    public void HideVisualsInstant()
    {
        // Stop any running routines that might fight this.
        if (_fadeRoutine != null) { StopCoroutine(_fadeRoutine); _fadeRoutine = null; }
        if (_growInRoutine != null) { StopCoroutine(_growInRoutine); _growInRoutine = null; }
        if (_spriteScaleRoutine != null) { StopCoroutine(_spriteScaleRoutine); _spriteScaleRoutine = null; }
        if (_emissionMulRoutine != null) { StopCoroutine(_emissionMulRoutine); _emissionMulRoutine = null; }
        // Jiggle resumes after bridge root-reactivation and overwrites the zero scale set below.
        // Must be stopped here (not just in PrepareForReuse) because HideVisualsInstant is called
        // for solid cells that may never go through PrepareForReuse if they aren't in the new maze.
        if (_jiggleRoutine != null) { StopCoroutine(_jiggleRoutine); _jiggleRoutine = null; }
        CancelTintPulse(restoreToBase: false);

        // Disable collisions immediately.
        SetTerrainColliderEnabled(false);

        // Hide sprite (authoritative for "solid" dust).
        // Use SetBaseSpriteScale(zero) instead of directly writing transform.localScale so that
        // _dustSpriteBaseVisualScale is also zeroed. TickVehicleCompression (Update) reads
        // _dustSpriteBaseVisualScale and writes it to srt.localScale every frame; if only the
        // transform is zeroed here, TickVehicleCompression will immediately restore a non-zero
        // scale after RestoreNonCoralRenderersAfterBridge re-enables the SpriteRenderer.
        if (visual.sprite != null)
        {
            SetBaseSpriteScale(Vector3.zero);
            visual.sprite.enabled = false;
        }

        // For pooling / true hiding, stop particles completely.
        if (visual.particleSystem != null)
        {
            var systems = GetAllParticleSystems();
            if (systems != null)
            {
                for (int i = 0; i < systems.Length; i++)
                {
                    var ps = systems[i];
                    if (ps == null) continue;

                    var em = ps.emission;
                    em.enabled = false;

                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    ps.Clear(true);

                    var r = ps.GetComponent<ParticleSystemRenderer>();
                    if (r != null) r.enabled = false;
                }
            }
        }
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
/*
        Vector3 size = c.bounds.size;

        var shape = visual.particleSystem.shape;
        if (shape.enabled)
            shape.scale = new Vector3(size.x, size.y, 1f);
  */
    }
    private void TriggerChargeTintPulse()
{
    const float kFadeIn  = 0.03f;
    const float kFadeOut = 0.05f;

    CancelTintPulse(restoreToBase: false);

    _tintPulseToken++;
    int token = _tintPulseToken;
    _tintPulseRoutine = StartCoroutine(ChargeTintPulseRoutine(token, kFadeIn, kFadeOut));
}

    private IEnumerator ChargeTintPulseRoutine(int token, float fadeIn, float fadeOut)
{
    _tintPulseActive = true;

    Color chargeTint = _hasFeedbackColors ? _chargeColor : Color.white;
    chargeTint.a = _currentTint.a;

    Color startTint = _displayTint;
    float t = 0f;

    while (t < fadeIn)
    {
        if (token != _tintPulseToken) yield break;

        t += Time.deltaTime;
        float a = (fadeIn <= 0f) ? 1f : Mathf.Clamp01(t / fadeIn);

        // Rebuild target each frame so live drain alpha is respected.
        Color liveChargeTint = chargeTint;
        liveChargeTint.a = _currentTint.a;

        ApplyDisplayedTint(Color.Lerp(startTint, liveChargeTint, a));
        yield return null;
    }

    startTint = _displayTint;
    t = 0f;

    while (t < fadeOut)
    {
        if (token != _tintPulseToken) yield break;

        t += Time.deltaTime;
        float a = (fadeOut <= 0f) ? 1f : Mathf.Clamp01(t / fadeOut);

        // Fade back toward live base tint so ongoing charge drain is respected.
        ApplyDisplayedTint(Color.Lerp(startTint, _currentTint, a));
        yield return null;
    }

    if (token == _tintPulseToken)
    {
        RestoreDisplayToBaseTint();
        _tintPulseRoutine = null;
    }
}

    private void TriggerDenyTintPulse(float seconds = -1f)
{
    float dur = (seconds > 0f) ? seconds : denyPulseDefaultSeconds;
    if (dur <= 0f) return;

    CancelTintPulse(restoreToBase: false);

    _tintPulseToken++;
    int token = _tintPulseToken;
    _tintPulseRoutine = StartCoroutine(DenyTintPulseRoutine(token, dur));
}

    private IEnumerator DenyTintPulseRoutine(int token, float seconds)
{
    if (seconds <= 0f) yield break;

    _tintPulseActive = true;

    Color denyTint = _hasFeedbackColors ? _denyColor : Color.black;
    denyTint.a = _currentTint.a;

    float fadeIn  = Mathf.Clamp(seconds * 0.25f, 0.03f, 0.10f);
    float fadeOut = Mathf.Clamp(seconds * 0.35f, 0.05f, 0.14f);
    float hold    = Mathf.Max(0f, seconds - fadeIn - fadeOut);

    Color startTint = _displayTint;
    float t = 0f;

    while (t < fadeIn)
    {
        if (token != _tintPulseToken) yield break;

        t += Time.deltaTime;
        float a = (fadeIn <= 0f) ? 1f : Mathf.Clamp01(t / fadeIn);

        Color liveDenyTint = denyTint;
        liveDenyTint.a = _currentTint.a;

        ApplyDisplayedTint(Color.Lerp(startTint, liveDenyTint, a));
        yield return null;
    }

    if (hold > 0f)
    {
        float end = Time.time + hold;
        while (Time.time < end)
        {
            if (token != _tintPulseToken) yield break;

            Color liveDenyTint = denyTint;
            liveDenyTint.a = _currentTint.a;

            ApplyDisplayedTint(liveDenyTint);
            yield return null;
        }
    }

    startTint = _displayTint;
    t = 0f;

    while (t < fadeOut)
    {
        if (token != _tintPulseToken) yield break;

        t += Time.deltaTime;
        float a = (fadeOut <= 0f) ? 1f : Mathf.Clamp01(t / fadeOut);

        ApplyDisplayedTint(Color.Lerp(startTint, _currentTint, a));
        yield return null;
    }

    if (token == _tintPulseToken)
    {
        RestoreDisplayToBaseTint();
        _tintPulseRoutine = null;
    }
}
    private void TriggerJiggle()
{
    if (_jiggleRoutine != null)
        StopCoroutine(_jiggleRoutine);
    _jiggleRoutine = StartCoroutine(JiggleRoutine());
}

    private IEnumerator JiggleRoutine()
{
    const float duration  = 0.28f;
    const float frequency = 28f;   // oscillations per second
    const float amplitude = 0.18f; // peak scale offset

    Vector3 baseScale = _dustSpriteBaseVisualScale;
    float elapsed = 0f;

    while (elapsed < duration)
    {
        elapsed += Time.deltaTime;
        float decay  = 1f - Mathf.Clamp01(elapsed / duration);
        float offset = Mathf.Sin(elapsed * frequency * Mathf.PI * 2f) * amplitude * decay;
        SetBaseSpriteScale(baseScale * (1f + offset));
        yield return null;
    }

    SetBaseSpriteScale(baseScale);
    _jiggleRoutine = null;
}

    private void ApplyParticleFootprint()
    {
        if (visual.particleSystem == null) return;
        
        var main = visual.particleSystem.main;

        float mul = Mathf.Clamp(visual.particleFootprintMul, 0.05f, 2.0f);
        float size = _cellWorldSize * mul;
//        main.startSize = size;

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
// ------------- TIMING HELPERS -------------
    private float ResolveScaleSeconds(float from, float to, float seconds)
    {
        if (seconds > 0f) return seconds;

        // PASS 2: generator overrides regrow duration (bin-time radiance)
        if (to > from && _growInOverride > 0f)
            return Mathf.Max(0.01f, _growInOverride);

        return (to >= from) ? _timings.regrowSpriteScaleInSeconds : _timings.clearSpriteScaleOutSeconds;
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
        SetBaseSpriteScale(Vector3.one * s);
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
    public void SetFeedbackColors(Color chargeColor, Color denyColor)
    {
        _chargeColor = chargeColor;
        _denyColor = denyColor;
        _hasFeedbackColors = true;
    }
    private void OnDisable()
    {
        CancelTintPulse(restoreToBase: true);
    }
    private void ResetVisualToBase()
    {
        CancelTintPulse(restoreToBase: true);

        // Reset shader param #2 back to neutral.
        SetWorkSigned01(0f);

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
    }
    public void SetCellSizeDrivenScale(float cellWorldSize, float footprintMul = 1.15f, float clearanceWorld = 0f)
    {
        _cellWorldSize       = Mathf.Max(0.001f, cellWorldSize);
        _cellClearanceWorld  = Mathf.Max(0f, clearanceWorld);
        // footprintMul drives how large the sprite is relative to the cell tile.
        // 1.0 = exactly touches neighbour, >1.0 = overlaps, <1.0 = gap.
        _spriteScaleTarget   = Mathf.Max(0.01f, footprintMul);
        ApplyParticleFootprint();
        RebuildColliderForCurrentScale();
        SyncParticlesToCollider();
    }
    private void RebuildColliderForCurrentScale()
    {
        if (_box == null) return;

        float desiredWorld = Mathf.Clamp(_cellWorldSize - _cellClearanceWorld, 0.001f, _cellWorldSize);

        // The box collider must represent exactly one cell in physics space,
        // regardless of the sprite's visual footprint (dustFootprintMul).
        //
        // IMPORTANT: if the box is on the same transform as the sprite,
        // the sprite scale-in animation (to _spriteScaleTarget, typically 1.15)
        // will inflate the box's world footprint AFTER this method runs.
        // Compensate by dividing out the target sprite scale so the final
        // world size lands at exactly desiredWorld once the animation completes.
        float spriteScale = Mathf.Max(0.01f, _spriteScaleTarget);

        float sx = Mathf.Max(0.0001f, Mathf.Abs(transform.lossyScale.x));
        float sy = Mathf.Max(0.0001f, Mathf.Abs(transform.lossyScale.y));

        _box.size = new Vector2(
            desiredWorld / (sx * spriteScale),
            desiredWorld / (sy * spriteScale)
        );
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

}
