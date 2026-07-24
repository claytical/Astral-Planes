using System;
using UnityEngine;

public partial class CosmicDust : MonoBehaviour {

    private DustVisualTimings _timings;
    private bool _visualTimingsInitialized;
    private float _nextDustPluckTime = -999f;
    private Vehicle _currentPluckVehicle = null;
    // Ownership contract:
    // CosmicDust owns: visual fields (_currentTint, energy units, sprite scale, collision state).
    // CosmicDustGenerator owns: grid cell state (DustGridState), imprint dictionary.
    // CosmicDust never queries Generator directly; Generator drives CosmicDust via public API.
    // PrepareForReuse() resets CosmicDust's local fields; callers must also update the grid via SetCellState().

    public event Action<float> OnChargeChanged;
    public event Action<MusicalRole> OnRoleChanged;
    public event Action<bool> OnCollisionStateChanged;
    public event Action<Color> OnTintStateChanged;
    public event Action OnSpawnVisualRequested;
    public event Action<float> OnClearVisualRequested;

    // Interaction-mode contract (implemented by CosmicDustGenerator):
    // Carve: resistance-aware depletion used by Vehicle/DiscoveryTrackNode interactions.
    // Zap: discrete PhaseStar consume-and-clear path.
    // Both modes converge on shared clear/regrow visuals here (Dissipate/Hide + Begin),
    // while the generator controls mode flags (imprint mutation, regrow scheduling, void-grow exceptions, fade duration source).
    [SerializeField] public DustVisualSettings visual = new()
    {
        prefabReferenceScale   = new Vector3(0.75f, 0.75f, 1f),
    };

    [SerializeField] private DustInteractionSettings interaction = new()
    {
        energyDrainPerSecond  = 0.01f
    };

    [SerializeField] public DustClearingSettings clearing = new();

    [Header("Work / Preview (Boost Path)")]
    private Vector3 _initialLocalScale = Vector3.one;
    private bool _cachedInitialScale;
    private Vector3 _baseLocalScale = Vector3.one;
    private CosmicDustParticleEmissionSystem _particles;

    [SerializeField] public Collider2D terrainCollider;
    // Carve/disable must be authoritative, so we cache all colliders in the hierarchy.
    private Collider2D[] _cachedColliders;
    private BoxCollider2D _box;
    private CircleCollider2D _circle;
    private float _nonBoostClearSeconds;

    private DrumTrack _drumTrack;
    private CosmicDustGenerator gen;
    private CosmicDustVisualController _visualController;

    private bool _isBreaking;
    [SerializeField] private float _baseEmission = 1;

private GameFlowManager _gfm;
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
        _particles = new CosmicDustParticleEmissionSystem(() => visual.particleSystem, _baseEmission);

        // If the prefab has PlayOnAwake accidentally enabled, force idle so pool/prewarm won't explode.
// --- Particles: always visible/running, default emission on boot ---
        if (visual.particleSystem != null)
        {
            _particles.EnsureBaseParticleEmissionCaptured();

            // Ensure it's actually running (no Stop/Clear usage anywhere)
            _particles.EnsureParticlesPlaying();

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
            _particles.ApplyEmissionMultiplierImmediate(100f);

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
    public void InitializeVisuals(DustVisualTimings settings)
    {
        _timings = settings.Sanitized();
        _visualTimingsInitialized = true;
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

    public void SetTrackBundle(CosmicDustGenerator _dustGenerator, DrumTrack _drums)
    {
        gen = _dustGenerator;
        _drumTrack = _drums;
    }
}
