using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Effects;
using Gameplay.Mining;
using UnityEngine;

public class PhaseStar : MonoBehaviour
{
    private enum PhaseStarState
    {
        IdleWandering,   // visible; waiting for a player poke
        ShardChase,      // hidden; shard/MineNode is active
        MissedCorrection,// visible; eject only missed notes for the burst track
        Completed        // all targeted tracks are perfect
    }
    public enum ExplosionCue { Contact, Eject, ChaseStart, ChaseEnd, PhaseAdvance }

    [System.Serializable]
    public struct ExplosionCueConfig {
        public ExplosionCue cue;
        public GameObject   vfxPrefab;  // optional (pool-friendly)
        public AudioClip    sfx;        // optional
        [Range(0.2f,3f)] public float   defaultScale;
    }
    [SerializeField] private ExplosionCueConfig[] explosionConfigs;
    private readonly Dictionary<ExplosionCue, ExplosionCueConfig> _explosionMap = new();

    public enum AntiColorMode { ComplementHue, InvertRGB }
    [SerializeField] private AntiColorMode antiMode = AntiColorMode.ComplementHue;
    [SerializeField, Range(0.0f, 2.0f)] private float antiSaturationScale = 1.05f;
    [SerializeField, Range(0.2f, 1.8f)] private float antiValueScale      = 1.05f;
    [SerializeField] private bool starSpritesUseTrackColor = true; // diamonds still show track color by default
    [SerializeField] Rigidbody2D rb;
    [SerializeField] private float maxFaceTurnDegPerSec = 12f; // clamp turn rate → woozy
    [SerializeField, Range(0f,1f)] private float faceVelocityAmount = 0.2f;

    [SerializeField] private float pairSwingDeg = 14f;     // how far they tilt away from each other
    [SerializeField] private float pairSwingHz  = 0.14f;   // slow sway
    [SerializeField] private float pairSeparation = 0.12f; // how far they push/pull (world units)
    [SerializeField] private float wobbleDeg    = 3f;      // tiny extra wobble
    [SerializeField] private float wobbleHz     = 0.12f;
    [SerializeField] private float[] spriteAngleOffsets; // e.g. [0, 22.5f, -22.5f]
    private float _smoothedFaceAngle, _faceAngularVel;
    private Vector3[] _baseLocalPos; 
    Vector2 driftDir;
    float driftRechooseTimer;
    [Header("Prefabs & Visuals")]
    public GameObject shardPickupPrefab; // MUST HAVE ShardPickup
    public ParticleSystem particleSystem;
    public float pulseSpeed = 1f;
    public float minAlpha = .1f;
    public float maxAlpha = .5f;
    [SerializeField] private float faceTurnRate = 10f;   // higher = snappier facing
    [SerializeField] private float spinDegPerSec = 45f;  // gentle precession
    private float _spin;
    [Header("Behavior")]
    private bool _behaviorApplied;
    
    // Reusable buffer for overlap queries (avoid GC)
    private static readonly Collider2D[] _dustHits = new Collider2D[128];
    public float missedSpawnStagger = 0.03f;

    [Header("Dust Interaction")]
    public float dustShrinkRadius = 6f;
    public float dustShrinkUnitsPerSecond = 1.2f;
    public LayerMask dustLayer;
    public AnimationCurve dustFalloff = AnimationCurve.Linear(0,1,1,0);

    [Header("MineNode Ejection")]
    [SerializeField] private GameObject mineNodeSpawnerPrefab; // wrapper spawner (required)
    [SerializeField] private int hitsRequired = 3;             // total shards/minenodes per star
    [SerializeField] private int maxMineNodesPerStar = 4;      // safety cap
    [SerializeField] private float shardFlightSeconds = 0.6f;  // fly time to grid cell
    [SerializeField] private AnimationCurve shardFlightEase = AnimationCurve.EaseInOut(0,0,1,1);

    private bool awaitingBurstResolution = false;
    private Dictionary<InstrumentTrack, List<(int step, int note, int duration, float velocity)>> _deferredMissed;
    private List<CosmicDust> _nearbyBuffer = new List<CosmicDust>(64);
    private int mineNodesEjected = 0;
    private MusicalPhase assignedPhase;

    // Wiring
    private DrumTrack drumTrack;
    private InstrumentTrackController controller;
    private MineNodeProgressionManager progressionManager;

    private bool firstPokeHandled = false;
// Correction/phase-end guards
    private bool _acceptingDefers = true;        // while true, WatchBurstThenResolve can add to _deferredMissed
    private bool _phaseAdvanceStarted = false;   // ensures FinishPhaseAndAdvance runs only once
    private HashSet<InstrumentTrack> _correctionsDone = new(); // avoid double processing per track

    // targets for this phase (choose up to 4)
    private List<InstrumentTrack> targetTracks = new();
    private HashSet<InstrumentTrack> perfectTracks = new();
    private int _startIndex = 0;
    private PhaseStarState state = PhaseStarState.IdleWandering;
    private ParticleSystem.MainModule _psMain;
    [SerializeField] private Transform[]      spritePivots;   // rotate these; can be the SR transforms

    // Visual caches/helpers
    [SerializeField] private SpriteRenderer[] starSprites;          // root SR (can be null in prefab; we’ll find it)
    [SerializeField] private ParticleSystem starParticles;       // child VFX
    private Renderer[] _allRenderers;
    private Collider2D[] _allColliders;
    private Vector3 _returnPos;

    [SerializeField] private SpawnStrategyProfile spawnStrategyProfile;
    public PhaseStarBehaviorProfile behaviorProfile;
    [SerializeField] private float _dustShrinkRadius;
    [SerializeField] private float _dustUnitsPerSec;
    [SerializeField] private AnimationCurve _dustFalloff;
    [SerializeField] private float _regrowDelayMul;
    [SerializeField] private bool _feedsDust;
    [SerializeField] private float shardScatterRadius;
    private bool _isVisible;
    private void Awake()
    {
        _explosionMap.Clear();
        foreach (var cfg in explosionConfigs) _explosionMap[cfg.cue] = cfg;
        ApplyBehavior();
    }
    private void FireExplosion(ExplosionCue cue, Vector2 worldPos, Color tint, float scale = 1f) {
        if (!_explosionMap.TryGetValue(cue, out var cfg)) return;

        if (cfg.vfxPrefab) {
            var go = Instantiate(cfg.vfxPrefab, worldPos, Quaternion.identity);
            go.transform.localScale *= (cfg.defaultScale > 0 ? cfg.defaultScale : 1f) * scale;

            // Try to tint common components
            if (go.TryGetComponent<ParticleSystem>(out var ps)) {
                var main = ps.main; main.startColor = tint; // simple tint
            }
            foreach (var sr in go.GetComponentsInChildren<SpriteRenderer>(true)) {
                var c = sr.color; c = new Color(tint.r, tint.g, tint.b, c.a); sr.color = c;
            }
            // auto-destroy if no pooling
            Destroy(go, 3f);
        }

        if (cfg.sfx) {
            AudioSource.PlayClipAtPoint(cfg.sfx, worldPos, 0.9f);
        }

        // Optional: light camera nudge/shake here if you want
        // CameraShaker.Instance?.Shake(cfg.defaultScale * 0.1f);
    }
// Fields
    private MinedObjectSpawnDirective _cachedDirective;
    private InstrumentTrack _cachedTrack;
    private Color _cachedPreviewColor;

    private void PrepareNextDirective()
    {
        var phaseProfile = progressionManager?.GetProfileForPhase(assignedPhase);
        if (spawnStrategyProfile == null)
        {
            Debug.LogWarning("[PhaseStar] No SpawnStrategyProfile assigned — falling back to simple directive.");
            BuildFallbackDirective();  // see helper below
            RefreshPreviewFromCache();
            return;
        }

        _cachedDirective = spawnStrategyProfile.GetMinedObjectDirective(
            drumTrack?.trackController,
            assignedPhase,
            phaseProfile,
            drumTrack?.minedObjectPrefabRegistry,
            drumTrack?.nodePrefabRegistry,
            GameFlowManager.Instance?.noteSetFactory
        );

        _cachedTrack = _cachedDirective?.assignedTrack;

        if (_cachedDirective == null || _cachedTrack == null)
        {
            Debug.LogWarning("[PhaseStar] Strategy returned null directive or track — falling back.");
            BuildFallbackDirective();
        }
        else
        {
            Debug.Log($"cached directive: {_cachedDirective} cached tracK: {_cachedTrack} color : {_cachedPreviewColor} track color: {_cachedTrack.trackColor} dir color: {_cachedDirective.displayColor}");
        }

        // choose the preview color (prefer directive.displayColor if set)
        _cachedPreviewColor =
            (_cachedDirective != null && _cachedDirective.displayColor.a > 0f)
                ? _cachedDirective.displayColor
                : (_cachedTrack != null ? _cachedTrack.trackColor : ComputeAntiColor(GetDustPhaseColor()));

        RefreshPreviewFromCache();
    }

    private void BuildFallbackDirective()
    {
        _cachedTrack = PickNextTargetTrack();
        if (_cachedTrack == null)
        {
            _cachedDirective = null;
            return;
        }
        Debug.Log($"Fallback for {_cachedTrack}");
        var ns = GameFlowManager.Instance.noteSetFactory.Generate(_cachedTrack, assignedPhase);
        _cachedTrack.SetNoteSet(ns);

        _cachedDirective = new MinedObjectSpawnDirective {
            assignedTrack   = _cachedTrack,
            noteSet         = ns,
            minedObjectType = MinedObjectType.NoteSpawner,
            displayColor    = _cachedTrack.trackColor
        };
        _cachedPreviewColor = _cachedDirective.displayColor;
    }

// This is just a thin wrapper around your existing hooks
    private void RefreshPreviewFromCache()
    {
        // Sprites/diamonds from cached track color
        SetStarTint(_cachedPreviewColor, false);            // you already have SetStarTint(...)
        // Particles from phase/anti-phase (your existing helper)
        ConfigureInhalingParticles(ComputeAntiColor(GetDustPhaseColor()));

        // If you want the older preview flow too:
        RefreshVisuals(_cachedTrack);                // safe to call; it tints diamonds from track
    }

    private void OnEnable()
    {
        if (!_behaviorApplied) ApplyBehavior();
    }

    private void SetBehaviorProfile(PhaseStarBehaviorProfile profile, bool applyNow = true)
    {
        behaviorProfile = profile;
        if (applyNow) ApplyBehavior();
    }
    private void ApplyBehavior() { 
        if (!behaviorProfile) return;
            _dustShrinkRadius = behaviorProfile.dustShrinkRadius;
            _dustUnitsPerSec  = behaviorProfile.dustShrinkUnitsPerSec;
            _dustFalloff      = behaviorProfile.dustFalloff ?? AnimationCurve.Linear(0, 1, 1, 0);
            _feedsDust        = behaviorProfile.feedsDust;
            _regrowDelayMul   = Mathf.Max(0.1f, behaviorProfile.dustRegrowDelayMul);
            // CosmicDustGenerator.GlobalRegrowDelayMul = _regrowDelayMul;
           _behaviorApplied = true;
    }
    private Color GetDustPhaseColor()
{
    // If you have a single source of truth for dust color, use that here.
    // Fallback to your phase visual color:
    return ComputeAntiColor(_cachedPreviewColor);
}

// --- UTIL: compute the "anti" color ---
    private Color ComputeAntiColor(Color src)
{
    switch (antiMode)
    {
        case AntiColorMode.InvertRGB:
        {
            // Simple inverse (in gamma space) with slight value scaling
            var inv = new Color(1f - src.r, 1f - src.g, 1f - src.b, 1f);
            return AdjustSV(inv, 1f, antiValueScale);
        }
        case AntiColorMode.ComplementHue:
        default:
        {
            Color.RGBToHSV(src, out float h, out float s, out float v);
            h = Mathf.Repeat(h + 0.5f, 1f);                  // 180° hue shift
            s *= antiSaturationScale; v *= antiValueScale;
            var c = Color.HSVToRGB(Mathf.Clamp01(h), Mathf.Clamp01(s), Mathf.Clamp01(v));
            return EnsureContrast(c, src, 3.0f);             // make sure it visibly opposes dust
        }
    }
}

    private Color AdjustSV(Color c, float sScale, float vScale)
{
    Color.RGBToHSV(c, out float h, out float s, out float v);
    s *= sScale; v *= vScale;
    return Color.HSVToRGB(h, Mathf.Clamp01(s), Mathf.Clamp01(v));
}

// Guarantee at least a basic contrast against the dust color (approx. WCAG-ish)
    private Color EnsureContrast(Color candidate, Color against, float minRatio)
{
    float L(Color x)
    {
        // sRGB relative luminance
        float ch(float u){ u = Mathf.GammaToLinearSpace(u); return (u <= 0.03928f)? u/12.92f : Mathf.Pow((u+0.055f)/1.055f, 2.4f); }
        float r = ch(x.r), g = ch(x.g), b = ch(x.b);
        return 0.2126f*r + 0.7152f*g + 0.0722f*b + 1e-6f;
    }
    float l1 = L(candidate), l2 = L(against);
    float ratio = (Mathf.Max(l1,l2)+0.05f) / (Mathf.Min(l1,l2)+0.05f);
    if (ratio >= minRatio) return candidate;

    // Nudge value up or down depending on which side we’re on
    bool brighten = l1 <= l2;
    for (int i=0; i<6 && ratio < minRatio; i++)
    {
        candidate = AdjustSV(candidate, 1f, brighten? 1.12f : 0.88f);
        l1 = L(candidate);
        ratio = (Mathf.Max(l1,l2)+0.05f) / (Mathf.Min(l1,l2)+0.05f);
    }
    return candidate;
}
    private void OrientDiamondsToVelocity(Vector2 drift)
{
    EnsureSpritesAndPivots();
    if (spritePivots == null || spritePivots.Length == 0) return;

    // Desired heading (-90 so the diamond “points” along +Y)
    float target = (drift.sqrMagnitude > 0.0004f)
        ? Mathf.Atan2(drift.y, drift.x) * Mathf.Rad2Deg - 90f
        : spritePivots[0].eulerAngles.z;

    // Gentle wobble around the faced angle
    float wobble = Mathf.Sin(Time.time * Mathf.PI * 2f * wobbleHz) * wobbleDeg;

    // Turn-rate cap (visible rotation)
    float perStep = Mathf.Max(1f, maxFaceTurnDegPerSec) * Time.fixedDeltaTime;

    for (int i = 0; i < spritePivots.Length; i++)
    {
        var t = spritePivots[i];
        if (!t) continue;

        float current = t.localEulerAngles.z;
        // include your per-sprite design offsets
        float desired = target + wobble + (spriteAngleOffsets != null && i < spriteAngleOffsets.Length ? spriteAngleOffsets[i] : 0f);

        float next = Mathf.MoveTowardsAngle(current, desired, perStep);
        t.localRotation = Quaternion.Euler(0f, 0f, next);

        // (optional) keep their initial local offsets, in case other code moves them
        if (_baseLocalPos != null && _baseLocalPos.Length > i && _baseLocalPos[i] != Vector3.zero)
            t.localPosition = _baseLocalPos[i];
    }
}

    private void EnsureSpritesAndPivots()
    {
        if (starSprites == null || starSprites.Length == 0)
            starSprites = GetComponentsInChildren<SpriteRenderer>(true);

        if (spritePivots == null || spritePivots.Length != starSprites.Length)
        {
            spritePivots = new Transform[starSprites.Length];
            for (int i = 0; i < starSprites.Length; i++)
                spritePivots[i] = starSprites[i] ? starSprites[i].transform : null;
        }
        if (_baseLocalPos == null || _baseLocalPos.Length != spritePivots.Length)
        {
            _baseLocalPos = new Vector3[spritePivots.Length];
            for (int i = 0; i < spritePivots.Length; i++)
                _baseLocalPos[i] = spritePivots[i] ? spritePivots[i].localPosition : Vector3.zero;
        }

        if (spriteAngleOffsets == null || spriteAngleOffsets.Length != starSprites.Length)
            spriteAngleOffsets = Enumerable.Repeat(0f, starSprites.Length).ToArray();
    }
    

    private void CacheAllVis()
    {
        _allRenderers = GetComponentsInChildren<Renderer>(true);
        _allColliders = GetComponentsInChildren<Collider2D>(true);
    }

    private void DisableAllCollidersAndRenderers()
    {
        if (_allRenderers == null || _allColliders == null) CacheAllVis();

        foreach (var r in _allRenderers)
            if (r)
                r.enabled = false;
        foreach (var c in _allColliders) if (c) c.enabled = false;

        var rb = GetComponent<Rigidbody2D>();
        _returnPos = transform.position;
        if (rb) { rb.linearVelocity = Vector2.zero; rb.angularVelocity = 0f; rb.constraints = RigidbodyConstraints2D.FreezeAll; }
    }

    private void EnableAllCollidersAndRenderers()
    {
        if (_allRenderers == null || _allColliders == null) CacheAllVis();

        foreach (var r in _allRenderers) if (r) r.enabled = true;
        foreach (var c in _allColliders) if (c) c.enabled = true;

        var rb = GetComponent<Rigidbody2D>();
        transform.position = _returnPos;
        if (rb) { rb.linearVelocity = Vector2.zero; rb.angularVelocity = 0f; rb.constraints = RigidbodyConstraints2D.FreezeRotation; }
    }

    public void SetSpawnStrategyProfile(SpawnStrategyProfile profile) => spawnStrategyProfile = profile;

    // ======= ENTRYPOINT =======
    public void Initialize(
        DrumTrack track,
        MineNodeProgressionManager manager,
        IEnumerable<InstrumentTrack> targets,
        bool armFirstPokeCommit,
        PhaseStarBehaviorProfile profile)
    {
        // 0) Core refs
        behaviorProfile = profile;
        drumTrack = track;
        controller = drumTrack.trackController;
        progressionManager = manager;
        assignedPhase = drumTrack.currentPhase;
        SetBehaviorProfile(profile);
        if (behaviorProfile == null) return;
        pulseSpeed = behaviorProfile.particlePulseSpeed;
        minAlpha = behaviorProfile.starAlphaMin;
        maxAlpha = behaviorProfile.starAlphaMax;
        shardScatterRadius = behaviorProfile.scatterRadius;
        missedSpawnStagger = behaviorProfile.ejectionStagger;
        dustShrinkRadius = behaviorProfile.dustShrinkRadius;
        dustShrinkUnitsPerSecond = behaviorProfile.dustShrinkUnitsPerSec;
        dustFalloff = behaviorProfile.dustFalloff;
        TintToPhaseColor();
        // hitsRequired governs “nodes per star”
        int nodes = Mathf.Max(1, behaviorProfile.nodesPerStar);
        typeof(PhaseStar)
            .GetField("hitsRequired",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.SetValue(this, nodes);
        typeof(PhaseStar)
            .GetField("maxMineNodesPerStar",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.SetValue(this, Mathf.Max(nodes, 4));

    

    // 2) Physics & drift
    rb = GetComponent<Rigidbody2D>();
    if (rb) rb.constraints = RigidbodyConstraints2D.FreezeRotation;
    PickNewDriftDir();

    // 3) Global flags
    drumTrack.isPhaseStarActive       = true;
    progressionManager.phaseStarActive= true;

    // 4) Visual prerequisites (so preview can safely tint)
    _psMain = particleSystem != null ? particleSystem.main : default;
    EnsureSpritesAndPivots();

    // 5) Target list setup (order BEFORE planning/preview)
    targetTracks = targets?
        .Where(t => t != null)
        .Distinct()
        .Take(4)
        .OrderBy(_ => UnityEngine.Random.value) // shuffle to avoid bias
        .ToList()
        ?? new List<InstrumentTrack>();

    _startIndex = (targetTracks.Count > 0)
        ? UnityEngine.Random.Range(0, targetTracks.Count)
        : 0;

    perfectTracks.Clear();
    foreach (var t in targetTracks) t.ResetPerfectionFlag();

    // 6) PLAN ONCE → cache directive/track/color
    PrepareNextDirective();

    // 7) PREVIEW ONCE → paint from the cache (no re-pick)
    PreviewNextTargetVisual();

    // 8) Finalize state
    SetStarVisible(true);
    state = PhaseStarState.IdleWandering;
    StartCoroutine(EntrancePulse());
}

    void PickNewDriftDir()
    {
        // small bias toward arcs if you like
        Vector2 rnd = UnityEngine.Random.insideUnitCircle.normalized;
        float orbitBias = behaviorProfile ? behaviorProfile.orbitBias : 0f;
        driftDir = Vector2.Lerp(rnd, Vector2.Perpendicular(rnd).normalized, orbitBias).normalized;
        driftRechooseTimer = UnityEngine.Random.Range(1.2f, 2.4f);
    }

    private void MarkStarInactive()
    {
        if (drumTrack) drumTrack.isPhaseStarActive = false;
        if (progressionManager) progressionManager.phaseStarActive = false;
    }

    private void PreviewNextTargetVisual()
    {
        RefreshVisuals(_cachedTrack);
    }
// Already present & used by Preview
    private void RefreshVisuals(InstrumentTrack nextTrack)
    {
        var dustPhaseColor = GetDustPhaseColor();
        var anti = ComputeAntiColor(dustPhaseColor);

        ConfigureInhalingParticles(anti);

        EnsureSpritesAndPivots();
        if (starSprites != null && starSprites.Length > 0)
        {
            var baseCol = starSpritesUseTrackColor && nextTrack != null
                ? nextTrack.trackColor      // ✅ preview diamonds from the cached track
                : anti;                      // or anti if no track

            for (int i = 0; i < starSprites.Length; i++)
            {
                var sr = starSprites[i]; if (!sr) continue;
                var c = baseCol; c.a = sr.color.a;  // preserve alpha pulse
                sr.color = c;
            }
        }
    }

    private void ConfigureInhalingParticles(Color phaseColor)
    {
        if (!particleSystem) return;
        var main = particleSystem.main;
        main.simulationSpace = ParticleSystemSimulationSpace.Local;
        main.loop = true;
        main.startSpeed = new ParticleSystem.MinMaxCurve(-3f); // inward
        main.startLifetime = new ParticleSystem.MinMaxCurve(1.2f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.1f);
        main.startColor = phaseColor;

        var em = particleSystem.emission;
        em.rateOverTime = 28f;

        var sh = particleSystem.shape;
        sh.shapeType = ParticleSystemShapeType.Circle;
        sh.radius = 1.5f;
        sh.radiusThickness = 0.1f; // thin ring
        sh.arc = 360f;
        sh.arcMode = ParticleSystemShapeMultiModeValue.Loop;

        var col = particleSystem.colorOverLifetime;
        col.enabled = true;
        var grad = new Gradient();
        grad.SetKeys(
            new[]{ new GradientColorKey(phaseColor, 0f), new GradientColorKey(phaseColor, 1f) },
            new[]{ new GradientAlphaKey(1f, 0f),        new GradientAlphaKey(0f, 1f) }
        );
        col.color = grad;

        var size = particleSystem.sizeOverLifetime;
        size.enabled = true;
        var curve = new AnimationCurve(new Keyframe(0,1f), new Keyframe(1f,0f));
        size.size = new ParticleSystem.MinMaxCurve(1f, curve);

        var noise = particleSystem.noise;
        noise.enabled = true;
        noise.strength = 0.08f;
        noise.frequency = 0.22f;
        noise.scrollSpeed = 0.1f;

        var trails = particleSystem.trails;
        trails.enabled = true;
        trails.lifetime = 0.3f;
        trails.minVertexDistance = 0.05f;
        //trails.widthOverTrail = 0.03f;
    }
    
    private IEnumerator EntrancePulse() { yield return null; }
    private Color GetAntiPhaseColor() => ComputeAntiColor(GetPhaseColor()); // from your earlier anti-color helper
    private Color GetPhaseColor() => behaviorProfile.starColor;

    void FixedUpdate()
    {
        if (rb == null) return;
        if (particleSystem != null)
        {
            var c = _psMain.startColor.color;
            c.a = Mathf.Lerp(minAlpha, maxAlpha, 0.5f + 0.5f * Mathf.Sin(Time.time * pulseSpeed));
            _psMain.startColor = c;
        }
        EnsureSpritesAndPivots();
        // only drift when idle & visible
        bool anySpriteVisible = starSprites != null && starSprites.Any(s => s && s.enabled);
        bool canDrift = (state == PhaseStarState.IdleWandering) && anySpriteVisible;
        if (!canDrift) { rb.linearVelocity = Vector2.zero; return; }
        float speed   = behaviorProfile ? behaviorProfile.starDriftSpeed  : 0.6f;
        float jitter  = behaviorProfile ? behaviorProfile.starDriftJitter : 0.15f;

        // re-pick heading occasionally
        driftRechooseTimer -= Time.fixedDeltaTime;
        if (driftRechooseTimer <= 0f) PickNewDriftDir();

        // a little steering noise
        Vector2 j = UnityEngine.Random.insideUnitCircle * jitter;
        Vector2 v = (driftDir + j).normalized * speed;

        rb.linearVelocity = v;
//        AlignAndSpinToVelocity(v);
        //UpdateDiamonds(v);
        OrientDiamondsToVelocity(v);
        PulseSpriteAlpha();
        // optional blink/teleport spice (Wildcard)
        if (behaviorProfile && behaviorProfile.teleportChancePerSec > 0f)
        {
            if (UnityEngine.Random.value < behaviorProfile.teleportChancePerSec * Time.fixedDeltaTime)
            {
                // simple blink: small offset; clamp to play area if needed
                Vector2 off = UnityEngine.Random.insideUnitCircle * 1.5f;
                rb.position += off;
            }
        }
        ShrinkNearbyDust();
    }

    // ======= COLLISION → SHARD CHASE =======
    private void OnCollisionEnter2D(Collision2D coll)
    {
        if (!coll.gameObject.TryGetComponent(out Vehicle _)) return;
        var track = PickNextTargetTrack();
        if (track == null)
        {
            state = PhaseStarState.Completed;                  // <-- be explicit
            StartCoroutine(FinishPhaseAndAdvance());
            return;
        }
        if (state == PhaseStarState.Completed) return;
        if (state != PhaseStarState.IdleWandering) return;

        var contactPoint = coll.GetContact(0).point;

        if (_cachedDirective == null || _cachedTrack == null)
            PrepareNextDirective();
        if (_cachedDirective == null || _cachedTrack == null)
        {
            StartCoroutine(FinishPhaseAndAdvance());
            return;
        }

        // Route through the single source of truth
        SpawnColoredMineNode(mineNodesEjected, contactPoint, _cachedDirective, _cachedTrack, _cachedPreviewColor);

        mineNodesEjected++;
        if (!firstPokeHandled) firstPokeHandled = true;
    }

private IEnumerator EnsureEnabledNextFrame(GameObject go)
{
    yield return null;
    if (go && !go.activeSelf) go.SetActive(true);
}

    private InstrumentTrack PickNextTargetTrack()
    {
        if (targetTracks == null || targetTracks.Count == 0) return null;

        int n = targetTracks.Count;
        for (int i = 0; i < n; i++)
        {
            int idx = (_startIndex + i) % n;
            var t = targetTracks[idx];
            if (!perfectTracks.Contains(t)) return t;
        }
        return null;
    }

    public void NotifyShardBurstComplete(InstrumentTrack track)
    {
        if (_phaseAdvanceStarted) return;
        
        StartCoroutine(WatchBurstThenResolve(track));
    }

    private IEnumerator WatchBurstThenResolve(InstrumentTrack track)
    {
        // 1) wait while ghost/pattern is emitting (global flag)
        float start = Time.time, hardTimeout = 12f;
        while (GameFlowManager.Instance != null && GameFlowManager.Instance.ghostCycleInProgress)
        {
            if (Time.time - start > hardTimeout) break;
            yield return null;
        }

        // 2) then wait until this track has no live collectables
        while (track != null && track.spawnedCollectables.Any(go => go != null))
        {
            if (Time.time - start > hardTimeout) break;
            yield return null;
        }

        // Optional: force-clean any stragglers if we timed out
        if (Time.time - start > hardTimeout && track != null)
        {
            Debug.LogWarning("[PhaseStar] Burst timeout — forcing collectable cleanup.");
            ForceCleanupTrackCollectables(track);
        }

        yield return new WaitForSeconds(0.05f);

        var missed = track.GetMissedGhostPayloads();

        // Only defer if we’re still accepting; ignore late adds once phase end starts
        if (_acceptingDefers && missed != null && missed.Count > 0)
        {
            _deferredMissed ??= new Dictionary<InstrumentTrack, List<(int step, int note, int duration, float velocity)>>();
            _deferredMissed[track] = missed;
        }
        perfectTracks.Add(track);
        Debug.Log($"Adding perfect {track}. {perfectTracks.Count} perfect tracks.");
        if (perfectTracks.Count >= targetTracks.Count)
        {
            
            if (!_phaseAdvanceStarted)
            {
                state = PhaseStarState.Completed;

                var from = assignedPhase; // the phase this star belongs to
                var to   = progressionManager.GetNextPhase(); // new helper
                var color = progressionManager.ResolveNextPhaseStarColor(to);
                Debug.Log($"Moving to next phase {to}");
                // Pass the actual perfect set to seed the bridge
                progressionManager.BeginBridgeToNextPhase(from, to, new List<InstrumentTrack>(perfectTracks), color);

                StartCoroutine(FinishPhaseAndAdvance()); // keep your existing cleanup path
            }
            yield break;
        }

        // Rearm visuals
        TintToPhaseColor();
        state = PhaseStarState.IdleWandering;
        awaitingBurstResolution = false;
        SetStarVisible(true);
        PreviewNextTargetVisual();
    }
    private void PulseSpriteAlpha()
    {
        EnsureSpritesAndPivots();
        if (starSprites == null) return;

        float lo = Mathf.Min(minAlpha, maxAlpha);
        float hi = Mathf.Max(minAlpha, maxAlpha);
        float t  = 0.5f + 0.5f * Mathf.Sin(Time.time * (pulseSpeed * 0.6f));
        float a  = Mathf.Lerp(lo, hi, t);

        for (int i = 0; i < starSprites.Length; i++)
        {
            var sr = starSprites[i];
            if (!sr) continue;
            var c = sr.color; c.a = a; sr.color = c;
        }
    }

    private void ForceCleanupTrackCollectables(InstrumentTrack track)
    {
        if (track == null) return;

        // Take a snapshot so we’re not iterating a list that’s being mutated by OnDestroyed
        var snapshot = new List<GameObject>(track.spawnedCollectables);

        for (int i = snapshot.Count - 1; i >= 0; i--)
        {
            var go = snapshot[i];
            if (!go) continue;

            // Stop any local particle effects
            var ps = go.GetComponentsInChildren<ParticleSystem>(true);
            for (int p = 0; p < ps.Length; p++)
                if (ps[p]) ps[p].Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            // Destroy will trigger Collectable.OnDestroy -> OnDestroyed -> track.OnCollectableDestroyed
            Destroy(go);
        }

        // Give Unity a frame to run destroy callbacks, then scrub anything left
        StartCoroutine(_ScrubTrackListNextFrame(track));
    }

    private IEnumerator _ScrubTrackListNextFrame(InstrumentTrack track)
    {
        yield return null; // let OnDestroyed run
        if (track != null)
            track.spawnedCollectables.RemoveAll(go => go == null);
    }

    private IEnumerator SpawnMissedForTrack(
    InstrumentTrack track,
    List<(int step, int note, int duration, float velocity)> missed)
{
    if (track == null || missed == null || missed.Count == 0)
        yield break;

    // Dedupe by (step,note) so we don’t spawn more than intended
    var seen = new HashSet<(int step, int note)>();
    var localSpawned = new List<GameObject>();

    foreach (var m in missed)
    {
        if (!seen.Add((m.step, m.note))) continue; // skip duplicates

        Vector2 off = UnityEngine.Random.insideUnitCircle * 1.4f;
        Vector3 pos = transform.position + new Vector3(off.x, off.y, 0f);

        var go = Instantiate(track.collectablePrefab, pos, Quaternion.identity, track.collectableParent);
        if (go && go.TryGetComponent(out Collectable c))
        {
            // PhaseStar.cs (inside SpawnMissedForTrack loop, after you create 'c')
            int targetStep = m.step;

// Create or get the ribbon marker for this step
            var markerGO = track.controller.noteVisualizer.PlacePersistentNoteMarker(track, targetStep);
            if (markerGO != null)
            {
                // ensure marker starts gray; MarkerLight will flip it on arrival
                var ml = markerGO.GetComponent<MarkerLight>();
                if (ml == null) ml = markerGO.AddComponent<MarkerLight>();
                ml.SetGrey(new Color(1f,1f,1f,0.25f)); // muted/gray

                // attach tether from this collectable to the marker
                c.AttachTether(markerGO.transform, track.trackColor);
            }

// Ensure the collectable uses the tethered completion
            c.OnCollected += (d, force) =>
            {
                // NOTE: DO NOT destroy here; let the tether pulse complete first:
                c.HandleCollectedWithTether(() => track.OnCollectableCollected(c, targetStep, d, force));
            };
            c.OnDestroyed += () => track.SendMessage("OnCollectableDestroyed", c);

            c.energySprite.color = track.trackColor;

            var ns = track.GetCurrentNoteSet();
            // steps list is only used to draw vines / visuals; it does NOT place in loop
            c.Initialize(m.note, m.duration, track, ns, ns != null ? ns.GetStepList() : new List<int>());

            // Do NOT call the track again from OnCollected (Collectable already does that)

            // Null-safe bookkeeping on teardown
            c.OnDestroyed += () =>
            {
                if (track && track.gameObject)
                {
                    try
                    {
                        track.gameObject.SendMessage("OnCollectableDestroyed", c, SendMessageOptions.DontRequireReceiver);
                    }
                    catch (MissingReferenceException) { /* track gone */ }
                }
            };

            // Keep track of only what WE spawned for this correction
            localSpawned.Add(go);

            // Optional one-shot audition
            track.PlayNote(m.note, m.duration, Mathf.Clamp(m.velocity, 60, 120));
        }

        if (missedSpawnStagger > 0f)
            yield return new WaitForSeconds(missedSpawnStagger);
    }

    // Wait for just the local correction pickups to resolve, with a timeout
    float waitStart = Time.time;
    float maxWait   = Mathf.Max(2f, drumTrack.GetLoopLengthInSeconds() * 1.25f);

    bool AnyAlive()
    {
        for (int i = localSpawned.Count - 1; i >= 0; i--)
        {
            var go = localSpawned[i];
            if (!go) { localSpawned.RemoveAt(i); continue; }
            // If GO exists but got reparented / pooled, still check activeInHierarchy
            if (go.activeInHierarchy) return true;
        }
        return false;
    }

    while (AnyAlive() && Time.time - waitStart < maxWait)
        yield return null;

    if (AnyAlive())
    {
        Debug.LogWarning("[PhaseStar] Correction timeout — forcing cleanup of local pickups.");
        // Only clean up what we spawned; let OnDestroyed handle track list removal
        for (int i = 0; i < localSpawned.Count; i++)
            if (localSpawned[i]) Destroy(localSpawned[i]);
        yield return null; // allow callbacks to run
    }

    // Final scrub (harmless if already cleaned)
    track.spawnedCollectables.RemoveAll(go => go == null);
}

    private void OnDestroy()
{
    GameFlowManager.Instance.harmony.RequestSystemChordAdvance(1);
    MarkStarInactive();
}

    private IEnumerator FinishPhaseAndAdvance()
{
    // 🔒 Run once
    if (_phaseAdvanceStarted) yield break;
    _phaseAdvanceStarted = true;

    state = PhaseStarState.Completed;
    // Stop taking new defers immediately
    _acceptingDefers = false;
    FireExplosion(ExplosionCue.PhaseAdvance, transform.position, GetAntiPhaseColor(), 1.2f);
    float loop = drumTrack.GetLoopLengthInSeconds();

    // 1) Transition beat
    progressionManager?.BeginMazeTransitionForNextPhase(loop * 0.4f);
    yield return new WaitForSeconds(Mathf.Min(0.6f, loop * 0.4f));

    // 2) Take a snapshot of what we have and clear the source up front
    var toProcess = (_deferredMissed != null)
        ? _deferredMissed.ToList()
        : new List<KeyValuePair<InstrumentTrack, List<(int step, int note, int duration, float velocity)>>>();
    _deferredMissed?.Clear();

    // 3) Single end-of-phase correction pass (each track at most once)
    foreach (var kv in toProcess)
    {
        var track  = kv.Key;
        var missed = kv.Value;

        if (track == null || missed == null || missed.Count == 0) continue;
        if (!_correctionsDone.Add(track)) continue; // already corrected this track

        SetStarVisible(true);
        RefreshVisuals(track);
        // after checking remaining collectables:
        switch (behaviorProfile?.expirePolicy ?? ExpirePolicy.WaitForAll)
        {
            case ExpirePolicy.WaitForAll:
                // current behavior: wait until none remain
                break;

            case ExpirePolicy.ExpireAndAdvance:
                // force cleanup and move on
                ForceCleanupTrackCollectables(track); // you already have this helper
                break;

            case ExpirePolicy.RecycleAsShards:
                ForceCleanupTrackCollectables(track);
                if (behaviorProfile.allowShardRecyclingWhenMissed)
                {
                    // emit shard pickups near star or along last known collectable positions
                    // choose helpful/havoc by behaviorProfile.shardHavocChance
                    // (reuse your ShardPickup prefab path)
                }
                break;
        }

        yield return StartCoroutine(SpawnMissedForTrack(track, missed));

        track.ResetGhostPhaseTracking();
    }

    // 4) IMPORTANT: mark this star inactive BEFORE asking for a new one
    MarkStarInactive();
    yield return null; // let flags propagate this frame

    // 5) Ask progression to spawn the next star
    if (GameFlowManager.Instance == null)
    {
        progressionManager?.SpawnNextPhaseStarWithoutLoopChange();
    }

    // 6) Safety: wait briefly for the new star to appear; if it doesn't, re-arm this one
    float wait = 0f, maxWait = 1.0f; // one second is plenty
    bool newStarSeen = false;
    while (wait < maxWait)
    {
        // Heuristic: the manager should flip drumTrack.isPhaseStarActive back to true
        if (drumTrack != null && drumTrack.isPhaseStarActive) { newStarSeen = true; break; }
        wait += Time.deltaTime;
        yield return null;
    }

// PhaseStar.cs inside FinishPhaseAndAdvance timeout path
    bool bridgeActive = GameFlowManager.Instance != null && GameFlowManager.Instance.IsBridgeActive;
    if (!newStarSeen)
    {
        if (bridgeActive)
        {
            // Bridge owns spawning; keep waiting a bit longer instead of re-arming
            yield return new WaitForSeconds(0.5f);
            // optional: add another check/loop here, or just exit quietly
            yield break;
        }

        // no bridge, fallback re-arm
        Debug.LogWarning("[PhaseStar] Next star failed to spawn in time; re-arming current star.");
        state = PhaseStarState.IdleWandering;
        SetStarVisible(true);
        _phaseAdvanceStarted = false;
        yield break;
    }

    // 7) Destroy self once the next star is active
    Destroy(gameObject);
}


private void SpawnColoredMineNode(
    int index,
    Vector3 spawnFrom,
    MinedObjectSpawnDirective directive,
    InstrumentTrack track,
    Color previewColor)
{
    if (drumTrack == null || mineNodeSpawnerPrefab == null) return;

    // Lock directive to preview choice
    directive.assignedTrack = track;
    directive.displayColor  = previewColor;
    if (directive.role == default && track != null)
    {
        directive.role = track.assignedRole;   // Bass, Groove, Harmony, Lead
    }
    if (directive.roleProfile == null && directive.role != default)
    {
        directive.roleProfile = MusicalRoleProfileLibrary.GetProfile(directive.role);
    }

    if (directive.noteSet == null)
    {
        var ns = GameFlowManager.Instance.noteSetFactory.Generate(track, assignedPhase);
        track.SetNoteSet(ns);
        directive.noteSet = ns;
    }

    Vector2Int cell = drumTrack.GetRandomAvailableCell();
    if (cell.x < 0) return;

    Vector3 targetPos = drumTrack.GridToWorldPosition(cell);

    // Wrapper (visual shard carrier) + spawner
    GameObject wrapper = Instantiate(mineNodeSpawnerPrefab, spawnFrom, Quaternion.identity);
    var spawner = wrapper.GetComponent<MineNodeSpawner>();
    if (!spawner) { Destroy(wrapper); return; }

    spawner.SetDrumTrack(drumTrack);
    spawner.resolvedColor = previewColor;
    if (directive.noteSet == null)
    {
        var ns = GameFlowManager.Instance.noteSetFactory.Generate(directive.assignedTrack, assignedPhase);
        directive.noteSet = ns;
        // If other systems expect the track to know its current set:
        directive.assignedTrack.SetNoteSet(ns);
        Debug.Log($"null noteset");
    }
    else 
    {
        Debug.Log($"noteset {directive.noteSet}");
    }
    // Spawn node at target cell (node may come back inactive; that’s fine)
    Debug.Log($"Directive in PhaseStar: {directive}");
    var node = spawner.SpawnNode(cell, directive);
    if (!node) { Destroy(wrapper); return; }
    node.OnResolved += (kind, dir) =>
    {
        // This routine should internally wait on any note motion
        // and only then re-arm/show the star.
        StartCoroutine(ClearThenSpawnAndRearm(track, dir.noteSet));
    };
    InitializeChildFromDirective(node.gameObject, directive);
    // Make sure the node has its color BEFORE hiding
    // --- COLOR + POSITION FIRST (prevents white-on-reveal and teleport glitches)
    ApplyNodeColor(node, previewColor);          // tint actual renderers now
    var nodeGO = node.gameObject;
    node.transform.position = targetPos;         // ensure it sits at destination
    nodeGO.SetActive(false);                     // hide during the shard flight

// --- STATE GUARDS (prevents phase advance/despawn mid-flight)
    state = PhaseStarState.ShardChase;
    awaitingBurstResolution = true;
    DisableAllCollidersAndRenderers();
    SetStarVisible(false);

    StartCoroutine(
        MoveShardToTarget(
            spawnFrom,
            targetPos,
            wrapper,           // your shard wrapper
            nodeGO,
            shardFlightSeconds,
            onLanded: () => {  // <-- replaces node.OnResolved usage
                awaitingBurstResolution = true;
                // plan + repaint the next preview now
                PrepareNextDirective();
                PreviewNextTargetVisual();
            }
        )
    );
}


private void InitializeChildFromDirective(GameObject child, MinedObjectSpawnDirective dir)
{
    // 1) Resolve role profile deterministically:
    // preference: directive.roleProfile → track.assignedRole → directive.role
    MusicalRoleProfile profile = dir.roleProfile
                                 ?? (dir.assignedTrack != null
                                     ? MusicalRoleProfileLibrary.GetProfile(dir.assignedTrack.assignedRole)
                                     : null)
                                 ?? MusicalRoleProfileLibrary.GetProfile(dir.role);

    // 2) Base component: enum role + shared fields
    var baseMO = child.GetComponent<MinedObject>();
    if (baseMO != null)
    {
        baseMO.assignedTrack   = dir.assignedTrack;
        baseMO.minedObjectType = dir.minedObjectType;
        baseMO.musicalRole     = dir.role;           // enum (Bass/Lead/Harmony/…)
        // (If you added a roleProfile field on MinedObject, assign it here too.)
    }

    // 3) NoteSpawner component: expects a PROFILE object
    var spawnerMO = child.GetComponent<NoteSpawnerMinedObject>();
    if (spawnerMO != null)
    {
        spawnerMO.assignedTrack   = dir.assignedTrack;
        spawnerMO.musicalRole     = profile;         // profile object, not enum
        spawnerMO.selectedNoteSet = dir.noteSet;     // must be non-null (see #4)
    }

    // 4) Color: lock to directive.displayColor (fallback to track color)
    var tint = (dir.displayColor.a > 0f)
        ? dir.displayColor
        : (dir.assignedTrack != null ? dir.assignedTrack.trackColor : Color.white);

    // Apply to the actual renderer used by the prefab
    var trackVisual = child.GetComponent<TrackItemVisual>();
    if (trackVisual != null) trackVisual.trackColor = tint;

    foreach (var sr in child.GetComponentsInChildren<SpriteRenderer>(true))
    {
        var c = tint; c.a = sr.color.a; // preserve existing alpha
        sr.color = c;
    }
}

    private IEnumerator ClearThenSpawnAndRearm(InstrumentTrack track, NoteSet noteSet)
    {
        if (track != null)
        {
            // Clear loop & markers (uses your existing visualizer hookup).
            // If your ClearLoopedNotes signature differs, call the variant you have.
            track.ClearLoopedNotes(TrackClearType.Remix);
            foreach (var go in track.spawnedCollectables.ToList())
                if (go) Destroy(go);
            track.spawnedCollectables.Clear();
        }

        // Give the visualizer a frame to delete markers/tethers cleanly.
        yield return null;

        // Fresh burst for this node
        if (track != null && noteSet != null)
        {
            track.SpawnCollectableBurst(noteSet);
        }

        // Wait until all collectables from this burst are gone, then re-arm the star.
        yield return StartCoroutine(WatchTrackThenRearm(track));
    }

// PhaseStar.cs
    private IEnumerator WatchTrackThenRearm(InstrumentTrack track)
    {
        float start = Time.time, timeout = 8f;
        while (track != null && track.spawnedCollectables.Any(go => go != null))
        {
            if (Time.time - start > timeout) break;
            yield return null;
        }
        yield return new WaitForSeconds(0.05f);

        // ✅ NEW: mark this burst as completed so the "perfect" path can run
        NotifyShardBurstComplete(track);

        StartCoroutine(RearmStarNoBurst());
    }


    private IEnumerator NodeResolutionWatchdog(MineNode node, InstrumentTrack track)
    {
        while (node != null && node.gameObject != null)
        {
            if (node == null || node.gameObject == null) break;
            yield return null;
        }
        yield return null;
        NotifyShardBurstComplete(track);
    }
    
    private IEnumerator RearmStarNoBurst()
    {
        yield return null;
        awaitingBurstResolution = false;
        state = PhaseStarState.IdleWandering;
        SetStarVisible(true);
        PreviewNextTargetVisual();
    }
    

    private void TintToPhaseColor()
    {
        if (particleSystem == null) return;
        if (behaviorProfile != null)
        {
            var col = behaviorProfile.starColor;
            var m = particleSystem.main; m.startColor = col;
        }
    }

    private void SetStarVisible(bool visible)
    {
        _isVisible = visible;
        if (visible)
        {
            EnableAllCollidersAndRenderers();
            if (state == PhaseStarState.IdleWandering) PreviewNextTargetVisual();
        }
        else
        {
            DisableAllCollidersAndRenderers();
        }
    }

    
    private void SetStarTint(Color c, bool enableObjects = true)
    {
        enableObjects &= _isVisible;  // <-- don't re-enable while hidden

        for (int i = 0; i < starSprites.Length; i++)
        {
            if (!enableObjects) continue;
            var sr = starSprites[i];
            if (!sr) continue;
            sr.enabled = true; // safe: only runs if _isVisible
            var cc = c; cc.a = sr.color.a;
            sr.color = cc;
        }
/*
        if (!starParticles) starParticles = GetComponentInChildren<ParticleSystem>(true);
        if (starParticles)
        {
            if (enableObjects && !starParticles.gameObject.activeSelf)
                starParticles.gameObject.SetActive(true);
            var m = starParticles.main;
            m.startColor = c;
        }
        */
    }
    void ShrinkNearbyDust() {
               // Let DarkStar-style profiles preserve dust.
        if (behaviorProfile != null && behaviorProfile.feedsDust) return;
        var gen = FindAnyObjectByType<CosmicDustGenerator>();
        // Small “spawn grace” so new tiles don’t vanish instantly.
        if (gen != null && gen.IsInRegrowGrace(0.8f)) return;
        
        float radius = (behaviorProfile != null ? behaviorProfile.dustShrinkRadius : dustShrinkRadius);
        float units  = (behaviorProfile != null ? behaviorProfile.dustShrinkUnitsPerSec : dustShrinkUnitsPerSecond);
        var fall     = (behaviorProfile != null && behaviorProfile.dustFalloff != null) ? behaviorProfile.dustFalloff : dustFalloff;
        if (radius <= 0f || units <= 0f) return;
        var filter = new ContactFilter2D { useTriggers = true };
        if (dustLayer.value != 0) { filter.useLayerMask = true; filter.SetLayerMask(dustLayer); }
        int count = Physics2D.OverlapCircle((Vector2)transform.position, radius, filter, _dustHits);
        for (int i = 0; i < count; i++) { 
            var col = _dustHits[i]; 
            if (!col || !col.enabled) continue;
            var dust = col.GetComponent<CosmicDust>() 
                       ?? col.GetComponentInParent<CosmicDust>() 
                       ?? col.GetComponentInChildren<CosmicDust>();
            if (!dust || !dust.isActiveAndEnabled) continue;
            // Optionally ignore tiles from the just-started epoch.
            if (gen != null && dust.GetEpoch() == gen.GetCurrentEpoch() && gen.GetEpochAge() < 0.5f) 
                continue;
            float d = Vector2.Distance(transform.position, dust.transform.position); 
            float t = Mathf.Clamp01(d / Mathf.Max(0.001f, radius)); 
            float rate = units * fall.Evaluate(1f - t); 
            dust.ShrinkByPhaseStar(rate); // smooth shrink; dust handles self-destroy
        }
    }
// Tiny utility to apply the chosen color immediately (prevents white nodes)
    private static void ApplyNodeColor(MineNode node, Color c)
    {
        // If MineNode exposes a method, prefer that. Otherwise set SpriteRenderers manually.
        var srs = node.GetComponentsInChildren<SpriteRenderer>(true);
        Debug.Log($"Found {srs.Length} sprite renderers");
        foreach (var sr in srs)
        {
            Debug.Log($"{sr.name}");
            if (!sr) continue;
            var col = c; col.a = sr.color.a; // preserve existing alpha
            sr.color = col;
            Debug.Log($"Applied color {c} to node {node.name}. Sprite Renderer is now {sr.color}");
        }
        
    }

    private IEnumerator MoveShardToTarget(
        Vector3 from,
        Vector3 to,
        GameObject wrapper,
        GameObject nodeGO,
        float seconds,
        System.Action onLanded // NEW
    ){
        if (!nodeGO) yield break;

        var tr = wrapper ? wrapper.transform : null;
        if (!tr || seconds <= 0f) {
            if (nodeGO && !nodeGO.activeSelf) nodeGO.SetActive(true);
            if (wrapper) Destroy(wrapper);
            onLanded?.Invoke();           // fire completion
            yield break;
        }

        tr.position = from;
        float t = 0f, dur = Mathf.Max(0.0001f, seconds);

        while (t < 1f && wrapper && nodeGO) {
            t += Time.unscaledDeltaTime / dur;
            float u = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t));
            tr.position = Vector3.LerpUnclamped(from, to, u);
            yield return null;
        }

        if (wrapper) tr.position = to;
        if (nodeGO && !nodeGO.activeSelf) nodeGO.SetActive(true);
        if (wrapper) Destroy(wrapper);

        onLanded?.Invoke();               // re-arm the star after reveal
    }

}
