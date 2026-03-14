using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Random = UnityEngine.Random;
[System.Serializable]
public class PhaseRecord
{
    public string ChapterId;          // stable authored ID from PhaseLibrary
    public int    PlaythroughIndex;   // 1 = first time, 2 = second, etc.
    public long   CompletedAtTicks;   // DateTime.UtcNow.Ticks — survives timezone changes
    public List<MotifSnapshot> Motifs = new();

    // Convenience
    public System.DateTime CompletedAt =>
        new System.DateTime(CompletedAtTicks, System.DateTimeKind.Utc);
}

// The full persistent garden — one instance, loaded at startup, saved on phase completion.
[Serializable]
public class GardenRecord
{
    public int              SaveVersion = 1;      // for future migration
    public List<string>     UnlockedChapterIds = new();
    public List<PhaseRecord> CompletedPhases    = new();

    // How many times a given chapter has been completed (derived, but cached for perf)
    public int PlaythroughCountFor(string chapterId) =>
        CompletedPhases.Count(r => r.ChapterId == chapterId);
}


public enum PhaseStarState
{
    WaitingForPoke = 0,
    PursuitActive  = 1,
    Completed      = 2,
    CheckingCompletion,
    BridgeInProgress
}
struct PreviewShard
{
    public Color color;
    public Color shadowColor;
    public bool collected;
    public Transform visual;
    public MusicalRole role;        // NEW: cached for convenience
}

public class PhaseStar : MonoBehaviour
{
    // -------------------- Serialized config --------------------
    [Header("Profiles & Prefs")] [SerializeField]
    private SpawnStrategyProfile spawnStrategyProfile;
    [SerializeField, Range(0f, 0.5f)] private float dominantRoleSwitchDelta = 0.10f;

    [SerializeField] private PhaseStarBehaviorProfile behaviorProfile;
    // In [Header("Charge Readiness")] section, alongside pokeChargeThreshold:
    [SerializeField, Range(0f, 1f)] private float shardReadyThreshold = 0.5f;

    // -------------------- Profile-driven tuning (authoring surface) --------------------
    // PhaseStar no longer owns duplicated serialized fields for these knobs; they come from PhaseStarBehaviorProfile.
    // Defaults here are used only if behaviorProfile is missing.
    [SerializeField] private GameObject superNodePrefab;  // rainbow shard prefab (collider + visual)
    [SerializeField] private SoloVoice soloVoice;         // assign in inspector or find at runtime
    [Header("Off-Screen Entry")]
    [Tooltip("World units outside the screen edge where the star spawns.")]
    [SerializeField] private float entryOffscreenMargin = 2f;
    [Tooltip("Once a role's charge reaches this value, it is considered 'tasted' and decay is floored.")]
    [SerializeField, Range(0f, 0.5f)] private float chargeTastedThreshold = 0.12f;

    [Tooltip("Minimum charge for a tasted role. Prevents full bleed-out so the star remembers prior feeding.")]
    [SerializeField, Range(0f, 0.3f)] private float chargeDecayFloor = 0.08f;
    [Tooltip("How close to the screen interior edge (world units) before the star is " +
             "considered 'arrived' and arms itself.")]
    [SerializeField] private float entryArriveThreshold = 1.5f;
    private readonly HashSet<MusicalRole> _tastedRoles = new();
    [Tooltip("Seconds to fade visuals in once inside the screen boundary.")]
    [SerializeField, Min(0f)] private float entryFadeInSeconds = 0.6f;
    [Header("Charge Readiness")]
    [SerializeField, Range(0f, 4f)] private float pokeChargeThreshold = 1.0f;
    private bool _cachedIsSuperNode = false;

    private bool StarKeepsDustClear => !behaviorProfile || behaviorProfile.starKeepsDustClear;

    private int StarKeepClearRadiusCells =>
        behaviorProfile ? Mathf.Max(0, behaviorProfile.starKeepClearRadiusCells) : 2;

    private bool SafetyBubbleEnabled => !behaviorProfile || behaviorProfile.enableSafetyBubble;
    private int SafetyBubbleRadiusCells => behaviorProfile ? Mathf.Max(0, behaviorProfile.safetyBubbleRadiusCells) : 4;

    private int CollectableClearTimeoutLoops => behaviorProfile ? behaviorProfile.collectableClearTimeoutLoops : 2;

    private float CollectableClearTimeoutSeconds =>
        behaviorProfile ? behaviorProfile.collectableClearTimeoutSeconds : 0f;
    
    private bool _previewInitialized;
    private Vector2 lastImpactDirection;

    [Header("Safety / Self-heal")]
    private int _awaitingCollectableClearSinceLoop = -1;

    private double _awaitingCollectableClearSinceDsp = -1.0;

    [Header("Subcomponents (optional)")] [SerializeField]
    private PhaseStarVisuals2D visuals;

    [SerializeField] private PhaseStarMotion2D motion;
    [SerializeField] private PhaseStarDustAffect dust;
    [SerializeField] private PhaseStarCravingNavigator cravingNavigator;

    private DrumTrack _drum;
    private bool _subscribedLoopBoundary;
    private MazeArchetype _assignedPhase;
    private bool _lockPreviewTintUntilIdle;
    private Color _lockedTint;
    private List<PreviewShard> previewRing = new();
    private int currentShardIndex;
    private float beatInterval;
    private float _roleAdvanceInterval;
    private bool _isDisposing;
    private bool _entryInProgress;
    private Transform activeShardVisual;
    private bool buildingPreview = false;
    private int _shardsEjectedCount; // how many shards have ejected so far

    private bool _awaitingLoopPhaseFinish;

    // Cached impact data for the next MineNode spawn
    Vector2 _lastImpactDir = Vector2.right;
    float _lastImpactStrength = 0f;

    // Optional clamp so crazy physics spikes don't blow things up
    const float MaxImpactStrength = 40f;
    [SerializeField, Min(0f)] private float disarmedPushScale = 0.6f;
    private bool _awaitingCollectableClear;

    [SerializeField] private bool _tracePhaseStar = true;
    private float _loopDuration; // seconds (time authority)

    [SerializeField] private Color bubbleTint = new Color(1f, 1f, 1f, 1f); // fill/edge tint (alpha handled by visuals)
    [SerializeField] private Color bubbleShardInnerTint = new Color(0.05f, 0.05f, 0.05f, 0.9f);

    private bool _bubbleActive;
    private float _bubbleRadiusWorld;

// Static “global query” (simple + reliable for Vehicle)
    private static bool s_bubbleActive;
    private static Vector2 s_bubbleCenter;
    private static float s_bubbleRadiusWorld;
    [Header("Charge (PASS 2)")] 
    [SerializeField] private float passiveChargeDecayPerSec = 0.02f;
    [SerializeField] private float dustToStarChargeMul = 1.0f;
    private readonly Dictionary<MusicalRole, float> _starCharge = new();

    [Header("Shard Visuals (Charge-Alpha + Sniffer)")]
    [Tooltip("Minimum alpha for a shard with zero charge — keeps it ghost-visible.")]
    [SerializeField, Range(0f, 0.5f)] private float shardMinAlpha = 0.08f;
    [Tooltip("Alpha ceiling while disarmed (collectables resolving). Keeps star de-emphasised.")]
    [SerializeField, Range(0f, 1f)]   private float shardDisarmedAlphaCeil = 0.22f;
    [Tooltip("How quickly shard alpha lerps toward its charge value each frame.")]
    [SerializeField, Range(1f, 20f)]  private float shardAlphaLerpSpeed = 6f;
    [Tooltip("How quickly each diamond rotates to face its sniffer direction (deg/sec).")]
    [SerializeField] private float shardSnifferTurnSpeed = 180f;
    [Tooltip("Duration of the shed-fly animation when a shard is ejected (seconds).")]
    [SerializeField] private float shardShedDuration = 0.35f;


    public enum DisarmReason
    {
        None = 0,

        // Star was hit; we are in the process of spawning/resolving a MineNode.
        NodeResolving,

        // There exist collectables in flight (global), so we must not allow another poke.
        CollectablesInFlight,

        //A track is staged to expand on the net loop boundary; don't allow another collision that could queue an additional burst/bin change in the same window
        ExpansionPending,

        // Star has no shards remaining and we’re awaiting bridge transition.
        AwaitBridge,

        // The bridge is actively running (coral/transition).
        Bridge
    }

    private DisarmReason _disarmReason = DisarmReason.None;

    // -------------------- State & caches --------------------
    private PhaseStarState _state = PhaseStarState.WaitingForPoke;

    private enum VisualMode
    {
        Bright,
        Dim,
        Hidden
    }

    private bool _ejectionInFlight;
    private bool _advanceStarted;
    private int _spawnTicket;
    private Coroutine _retryCo;
    private int _lastPokeFrame = -999999;
    private InstrumentTrack _cachedTrack;
    private MineNode _activeNode;
    private readonly List<InstrumentTrack> _targets = new(4);
    private List<MusicalRole> _phasePlanRoles;
    [SerializeField] private MotifProfile _assignedMotif; // optional: motif this star represents (motif system)

    public event Action<PhaseStar> OnArmed;
    public event Action<PhaseStar> OnDisarmed;
    private bool _isArmed;
    private int _baseSortingOrder;
    [SerializeField] private int _perPetalLayerStep;
    public MotifProfile AssignedMotif => _assignedMotif;

    private GameFlowManager gfm;
    // -------------------- Lifecycle --------------------
    void Start()
    {
        gfm = GameFlowManager.Instance;
        EnsurePreviewRing();
        if (!buildingPreview)
        {
            InitializeTimingAndSpeeds();
        }
    }
    public void AddCharge(MusicalRole role, float dustChargeTaken)
    {
        if (role == MusicalRole.None) return;
        float add = Mathf.Max(0f, dustChargeTaken) * dustToStarChargeMul;
        if (add <= 0f) return;

        // [BALANCE-C] Diminishing returns: charge earned above field average accrues at half rate.
        float fieldAvg = (_starCharge.Count > 0) ? GetTotalCharge() / _starCharge.Count : 0f;
        _starCharge.TryGetValue(role, out float cur);
        if (cur > fieldAvg)
            add *= 0.5f;

        _starCharge[role] = Mathf.Min(1f, cur + add);
        if (_starCharge[role] >= chargeTastedThreshold)
            _tastedRoles.Add(role);
    }
    /// <summary>
    /// Returns 0..1 hunger for a specific role: 1 = starving (zero charge), 0 = fully charged.
    /// Used by the navigator to weight density steering toward under-charged roles.
    /// </summary>
    public float GetRoleHunger(MusicalRole role)
    {
        _starCharge.TryGetValue(role, out float c);
        return 1f - Mathf.Clamp01(c);
    }
    public static bool IsPointInsideSafetyBubble(Vector2 worldPos)
    {
        if (!s_bubbleActive) return false;
        return (worldPos - s_bubbleCenter).sqrMagnitude <= (s_bubbleRadiusWorld * s_bubbleRadiusWorld);
    }
    public bool IsChargeReady()
    {
        // Any single shard at or above the per-role threshold makes the star pokeable.
        if (previewRing == null) return false;
        for (int i = 0; i < previewRing.Count; i++)
        {
            _starCharge.TryGetValue(previewRing[i].role, out float c);
            if (c >= shardReadyThreshold) return true;
        }
        return false;
    }
    public void EnterFromOffScreen(Vector2 targetWorldPos)
    {
        _entryInProgress = true;

        // Hide visuals and disable colliders until arrival.
        visuals?.HideAll();
        DisableColliders();

        // Place star just outside a random screen edge.
        Vector2 offPos = PickOffScreenSpawnPoint();
        transform.position = (Vector3)offPos + Vector3.forward * transform.position.z;

        // Motion is already initialized by this point — enable it so the star drifts inward.
        if (motion != null) motion.Enable(true);

        StartCoroutine(Co_EntryApproach(targetWorldPos));
    }
    private Vector2 PickOffScreenSpawnPoint()
{
    var cam = Camera.main;
    if (cam == null) return Vector2.zero;

    float margin = Mathf.Max(0.5f, entryOffscreenMargin);
    const float z = 0f;

    Vector2 min = cam.ViewportToWorldPoint(new Vector3(0f, 0f, z));
    Vector2 max = cam.ViewportToWorldPoint(new Vector3(1f, 1f, z));

    // Pick a random edge: 0=left, 1=right, 2=bottom, 3=top
    int edge = Random.Range(0, 4);
    return edge switch
    {
        0 => new Vector2(min.x - margin, Random.Range(min.y, max.y)),
        1 => new Vector2(max.x + margin, Random.Range(min.y, max.y)),
        2 => new Vector2(Random.Range(min.x, max.x), min.y - margin),
        _ => new Vector2(Random.Range(min.x, max.x), max.y + margin),
    };
}

private IEnumerator Co_EntryApproach(Vector2 targetWorldPos)
{
    var cam = Camera.main;

    // Wait until the star is inside the screen boundary (with a small inset).
    while (true)
    {
        if (cam == null) cam = Camera.main;

        if (cam != null)
        {
            const float z = 0f;
            Vector2 sMin = cam.ViewportToWorldPoint(new Vector3(0f, 0f, z));
            Vector2 sMax = cam.ViewportToWorldPoint(new Vector3(1f, 1f, z));

            float threshold = Mathf.Max(0f, entryArriveThreshold);
            Vector2 p = transform.position;

            bool inside = p.x > sMin.x + threshold &&
                          p.x < sMax.x - threshold &&
                          p.y > sMin.y + threshold &&
                          p.y < sMax.y - threshold;

            if (inside) break;
        }

        yield return null;
    }

    // ── Arrived ──────────────────────────────────────────────
    _entryInProgress = false;

    // Fade visuals in.
    if (visuals != null && entryFadeInSeconds > 0.01f)
    {
        // ShowBright starts the visual; shardAlphaLerpSpeed will naturally
        // animate from 0→target over the next several frames — no extra
        // coroutine needed.  We just need to un-hide the renderers.
        visuals.ShowBright(ResolvePreviewColorByReadiness());
    }

    // Re-enable colliders and arm.
    EnableColliders();
    dust?.RefreshDustIgnore();
    ArmNext();
    LogState("EntryComplete+Armed");
}

    void OnEnable()
    {
        var drum = GameFlowManager.Instance != null ? GameFlowManager.Instance.activeDrumTrack : null;
    }
    
    private float GetTotalCharge()
    {
        float total = 0f;
        foreach (var kv in _starCharge)
            total += kv.Value;
        return total;
    }

    private bool TryGetDominantRole(out MusicalRole role, out int shardIndex, out float charge)
    {
        role = MusicalRole.None;
        shardIndex = -1;
        charge = 0f;

        if (previewRing == null || previewRing.Count == 0)
            return false;

        float best = float.MinValue;
        int bestIdx = -1;
        MusicalRole bestRole = MusicalRole.None;

        for (int i = 0; i < previewRing.Count; i++)
        {
            var r = previewRing[i].role;
            _starCharge.TryGetValue(r, out float c);
            if (c > best)
            {
                best = c;
                bestIdx = i;
                bestRole = r;
            }
        }

        if (bestIdx < 0)
            return false;

        role = bestRole;
        shardIndex = bestIdx;
        charge = Mathf.Max(0f, best);
        return true;
    }
    public void Initialize(
        DrumTrack drum,
        IEnumerable<InstrumentTrack> targets,
        PhaseStarBehaviorProfile profile,
        MazeArchetype assignedPhase,
        MotifProfile motif = null)
    {
// Safe, null-tolerant log:
        var roleNames = targets?
            .Select(t => t == null ? "null" : t.assignedRole.ToString()) // <-- note the ()
            .ToArray() ?? Array.Empty<string>();

        Trace($"Initialize: received targets={roleNames.Length} :: {string.Join(", ", roleNames)}");

        _assignedPhase = assignedPhase;
        _assignedMotif = motif;

        if (_assignedMotif != null)
        {
            // Motif-aware trace for later debugging; phase behavior remains unchanged.
            Trace($"Initialize: motifId={_assignedMotif.motifId}, displayName={_assignedMotif.displayName}");
        }

        behaviorProfile = profile != null ? profile : behaviorProfile;
        _targets.Clear();
        if (targets != null) _targets.AddRange(targets.Where(t => t));
        _drum = drum;
        if (_drum != null)
        {
            _drum.SetBinCount(1);
            WireBinSource(_drum);
            Debug.Log($"[PhaseStar] loop={_drum.GetLoopLengthInSeconds():0.##}s  targets={_targets?.Count ?? 0}");

        }

        spawnStrategyProfile?.ResetForNewStar();

        // Clear charge state for this new star.
        _starCharge.Clear();
        _tastedRoles.Clear();

        _shardsEjectedCount = 0;

        BuildPhasePlan(_assignedPhase, Mathf.Max(1, behaviorProfile.nodesPerStar));
        PrepareNextDirective();
        // ensure subcomponents are present if assigned
        if (!visuals) visuals = GetComponentInChildren<PhaseStarVisuals2D>(true);
        if (!motion) motion = GetComponentInChildren<PhaseStarMotion2D>(true);
        if (!dust) dust = GetComponentInChildren<PhaseStarDustAffect>(true);
        if (!cravingNavigator) cravingNavigator = GetComponentInChildren<PhaseStarCravingNavigator>(true);
        if (visuals) visuals.Initialize(behaviorProfile, this);
        if (motion) motion.Initialize(behaviorProfile, this);

        if (dust) dust.Initialize(behaviorProfile, this);
        if (drum != null && !_subscribedLoopBoundary)
        {
            drum.OnLoopBoundary += OnLoopBoundary_RearmIfNeeded;
            _subscribedLoopBoundary = true;
        }

        if (_entryInProgress)
        {
            LogState("Initialized+AwaitingEntry");
        } 
        else {
         ArmNext();
         LogState("Initialized+Armed");
        }
    }
    private float GetChargeReady01()
    {
        return Mathf.Clamp01(GetTotalCharge() / Mathf.Max(0.001f, pokeChargeThreshold));
    }

    private Color ResolvePreviewColorByReadiness()
    {
        if (previewRing == null || previewRing.Count == 0) return Color.gray;

        int idx = Mathf.Clamp(currentShardIndex, 0, previewRing.Count - 1);
        Color role = previewRing[idx].color;

        float ready01 = GetChargeReady01();

        // start near neutral gray, then reveal the role color as charge rises
        Color gray = Color.Lerp(Color.black, Color.gray, 0.65f);
        Color c = Color.Lerp(gray, role, ready01);

        // preserve role alpha if you want, but color identity should emerge with readiness
        c.a = Mathf.Lerp(0.15f, role.a <= 0f ? 1f : role.a, ready01);
        return c;
    }
    private float ComputeCellWorldSize()
    {
        var drums = _drum != null ? _drum : GameFlowManager.Instance?.activeDrumTrack;
        if (!drums) return 1f;

        // Distance between neighboring grid cells in world space
        var a = drums.GridToWorldPosition(new Vector2Int(0, 0));
        var b = drums.GridToWorldPosition(new Vector2Int(1, 0));
        return Mathf.Max(0.0001f, Vector3.Distance(a, b));
    }
    private Vector2 GetNearestDustDirForRole(MusicalRole role, int radiusCells = 8)
    {
        var gfm = GameFlowManager.Instance;
        var gen = gfm != null ? gfm.dustGenerator : null;
        var drum = gfm != null ? gfm.activeDrumTrack : null;
        if (gen == null || drum == null) return Vector2.zero;

        Vector2Int center = drum.WorldToGridPosition(transform.position);

        float bestDistSq = float.MaxValue;
        Vector2 bestDir = Vector2.zero;

        for (int dy = -radiusCells; dy <= radiusCells; dy++)
        {
            for (int dx = -radiusCells; dx <= radiusCells; dx++)
            {
                Vector2Int gp = new Vector2Int(center.x + dx, center.y + dy);

                if (!gen.TryGetDustAt(gp, out var dust) || dust == null) continue;
                if (dust.Role != role) continue;

                Vector2 world = drum.GridToWorldPosition(gp);
                Vector2 dir = world - (Vector2)transform.position;
                float dSq = dir.sqrMagnitude;
                if (dSq < 0.0001f) continue;

                if (dSq < bestDistSq)
                {
                    bestDistSq = dSq;
                    bestDir = dir.normalized;
                }
            }
        }

        return bestDir;
    }
    void Update()
    {
        if (StarKeepsDustClear)
        {
            if (gfm.dustGenerator != null && gfm.activeDrumTrack != null)
            {
                var phase = gfm.phaseTransitionManager != null
                    ? gfm.phaseTransitionManager.currentPhase
                    : _assignedPhase;

                int radiusCells = _bubbleActive ? SafetyBubbleRadiusCells : StarKeepClearRadiusCells;

                gfm.dustGenerator.SetStarKeepClear(
                    gfm.activeDrumTrack.WorldToGridPosition(transform.position),
                    radiusCells,
                    phase, false
                );
            }
        }

// ---- Keep bubble center locked to star world position every frame ----
// Bubble is a gravity-void refuge zone and should track the drifting star cleanly.
        if (_bubbleActive)
        {
            s_bubbleCenter = transform.position;
            // Also nudge the bubble root so its shimmer particles stay centred.
            visuals?.UpdateBubblePosition(transform.position);
        }

        // ---- Charge-alpha: each shard's opacity = its role's accumulated charge ----
        // ---- Sniffer facing: each shard points toward the nearest dust of its role ----
        float dt = Time.deltaTime;

        // When disarmed the star de-emphasises: shards are muted, not broadcasting.
        // alphaScale ramps down to shardDisarmedAlphaCeil while collectables are resolving,
        // and back up to 1 once armed again.
        float alphaScale = _isArmed ? 1f : shardDisarmedAlphaCeil;

        if (previewRing != null && previewRing.Count > 0)
        {
            // Resolve dominant shard with hysteresis + soft switch delta (BALANCE-E).
            ResolveDominantRoleFromCharge();
            
            for (int i = 0; i < previewRing.Count; i++)
            {
                var shard = previewRing[i];
                if (!shard.visual) continue;

                var sr = shard.visual.GetComponent<SpriteRenderer>();

                // --- Alpha from charge, scaled by armed state ---
                if (sr != null)
                {
                    _starCharge.TryGetValue(shard.role, out float charge);
                    float t = Mathf.Clamp01(charge);

                    Color targetColor = Color.Lerp(
                        new Color(0.45f, 0.45f, 0.45f, 1f),
                        shard.color,
                        t
                    );

                    float targetAlpha = Mathf.Lerp(shardMinAlpha, alphaScale, t);

                    Color c = sr.color;
                    c.r = Mathf.Lerp(c.r, targetColor.r, shardAlphaLerpSpeed * dt);
                    c.g = Mathf.Lerp(c.g, targetColor.g, shardAlphaLerpSpeed * dt);
                    c.b = Mathf.Lerp(c.b, targetColor.b, shardAlphaLerpSpeed * dt);
                    c.a = Mathf.Lerp(c.a, targetAlpha, shardAlphaLerpSpeed * dt);
                    sr.color = c;
                }
 
                // --- Sniffer facing: rotate diamond toward nearest dust of its role ---
                Vector2 sniffDir = GetNearestDustDirForRole(shard.role, 8);
                if (sniffDir.sqrMagnitude > 0.0001f)
                {
                    float targetAngle = Mathf.Atan2(sniffDir.y, sniffDir.x) * Mathf.Rad2Deg - 90f;
                    float curAngle = shard.visual.localEulerAngles.z;
                    float newAngle = Mathf.MoveTowardsAngle(curAngle, targetAngle, shardSnifferTurnSpeed * dt);
                    shard.visual.localRotation = Quaternion.Euler(0f, 0f, newAngle);
                }
            }

            // --- Sorting order: highest charge on top ---
            UpdateShardSortingByCharge();
        }
        
        if (_starCharge.Count > 0 && passiveChargeDecayPerSec > 0f) { 
            float dec = passiveChargeDecayPerSec * dt;
            // [BALANCE-B] Dominant role decays 3× faster to compress the charge gap.
            TryGetDominantRole(out var dominantRoleNow, out _, out _);
            var keys = _starCharge.Keys.ToList();
            for (int i = 0; i < keys.Count; i++) {
                var r = keys[i];
                float cur = _starCharge[r];
                float roleDec = (r == dominantRoleNow) ? dec * 3f : dec;
                // [BALANCE-D] Decay floor: once a role has been tasted, charge
                // never drops below the floor. The star remembers what it ate.
                // Uses persistent _tastedRoles set (populated in AddCharge) so the
                // floor survives even after charge decays below the tasted threshold.
                float floor = _tastedRoles.Contains(r) ? chargeDecayFloor : 0f;
                _starCharge[r] = Mathf.Max(floor, cur - roleDec);
            }
        } 
        
    }

    private void SafeUnsubscribeAll()
    {
        // Unhook both places we subscribe OnLoopBoundary:
        if (_drum != null)
        {
            if (_subscribedLoopBoundary)
            {
                _drum.OnLoopBoundary -= OnLoopBoundary_RearmIfNeeded;
                _subscribedLoopBoundary = false;
            }
        }
    }

    bool AnyExpansionPendingGlobal()
    {
        var gfm = GameFlowManager.Instance;
        var tc = gfm != null ? gfm.controller : null;
        return (tc != null && tc.AnyExpansionPending());
    }

    private void SetVisual(VisualMode mode, Color tint)
    {
        if (!visuals) visuals = GetComponentInChildren<PhaseStarVisuals2D>(true);
        if (!visuals) return;
        switch (mode)
        {
            case VisualMode.Bright: visuals.ShowBright(tint); break;
            case VisualMode.Dim: visuals.ShowDim(tint); break;
            case VisualMode.Hidden: visuals.HideAll(); break;
        }
    }

    public void NotifyCollectableBurstCleared()
    {
        // This callback comes from InstrumentTrackController when the *global* collectable set is empty.
        // It is allowed to fire multiple times (per-track), so it must be idempotent and bridge-safe.
        if (_advanceStarted || _state == PhaseStarState.BridgeInProgress)
        {
            _awaitingCollectableClear = false;
            _awaitingCollectableClearSinceLoop = -1;
            _awaitingCollectableClearSinceDsp = -1.0;
            return;
        }

        bool cif = AnyCollectablesInFlightGlobal();
        bool ep = AnyExpansionPendingGlobal();

        Debug.Log(
            $"[PS:BURST_CLEARED] star={name} state={_state} armed={_isArmed} disarm={_disarmReason} " +
            $"awaitClr(before)={_awaitingCollectableClear} shards={_shardsEjectedCount}/{behaviorProfile.nodesPerStar} " +
            $"CIF={cif} EP={ep}"
        );

        _awaitingCollectableClear = false;
        _awaitingCollectableClearSinceLoop = -1;
        _awaitingCollectableClearSinceDsp = -1.0;

        // If we’re still not clean, do nothing (controller should not be calling us in this case, but stay defensive).
        if (AnyCollectablesInFlightGlobal() || AnyExpansionPendingGlobal())
        {
            Debug.LogWarning(
                $"[PS:BURST_CLEARED] IGNORE (still busy) star={name} CIF={AnyCollectablesInFlightGlobal()} EP={AnyExpansionPendingGlobal()}"
            );
            return;
        }

        bool noShardsRemain = _shardsEjectedCount >= Mathf.Max(1, behaviorProfile.nodesPerStar);

        // Final shard: start bridge immediately on last collected note.
        if (noShardsRemain)
        {
            BeginBridgeNow();
            return;
        }

        // Shards remain: re-arm so the star becomes hittable again.
        ArmNext();
    }

    private bool AnyCollectablesInFlightGlobal()
    {
        var gfm = GameFlowManager.Instance;
        if (gfm == null || gfm.controller == null || gfm.controller.tracks == null)
            return false;

        bool any = false;

        foreach (var t in gfm.controller.tracks)
        {
            if (t == null) continue;

            // Prune nulls defensively (destroyed objects can linger as null refs)
            if (t.spawnedCollectables != null)
                t.spawnedCollectables.RemoveAll(go => go == null);

            int count = (t.spawnedCollectables != null) ? t.spawnedCollectables.Count : 0;

            if (count > 0)
            {
                any = true;
                // Optional: leave this on until you confirm the fix
                Debug.Log($"[PS:CIF] track={t.name} spawnedCollectables={count}");
            }
        }

        return any;
    }

    private void BeginBridgeNow()
    {
        if (_advanceStarted) return;

        // HARD BLOCK: "no skip capture"
        if (_activeNode != null || _ejectionInFlight)
        {
            DBG(
                $"BeginBridgeNow: BLOCKED (outstanding node). activeNode={_activeNode?.name} ejectInFlight={_ejectionInFlight}");
            // Ensure we are not accidentally left in a bridge-ish state.
            _state = PhaseStarState.WaitingForPoke;
            Disarm(DisarmReason.NodeResolving, _lockedTint);
            return;
        }

        // CIF is intentionally not gated here: once the last node is captured (_activeNode == null),
        // remaining collectables from that node resolve independently of phase progression.
        // Blocking on CIF here caused the bridge to never start when notes took time to clear.

        if (AnyExpansionPendingGlobal())
        {
            DBG("BeginBridgeNow: BLOCKED (expansion pending)");
            Disarm(DisarmReason.ExpansionPending, _lockedTint);
            return;
        }

        _advanceStarted = true;
        _awaitingLoopPhaseFinish = false;
        _state = PhaseStarState.BridgeInProgress;
        Disarm(DisarmReason.Bridge, _lockedTint);
        StartCoroutine(CompleteAndAdvanceAsync());
    }

    private void ArmNext()
    {
        if (AnyCollectablesInFlightGlobal())
        {
            Disarm(DisarmReason.CollectablesInFlight);
            return;
        }

        if (AnyExpansionPendingGlobal())
        {
            DBG("ArmNext: blocked by ExpansionPending -> Disarm:ExpansionPendingGlobal");
            Disarm(DisarmReason.ExpansionPending);
            return;
        }

        if (!HasShardsRemaining())
        {
            Disarm(DisarmReason.AwaitBridge);
            return;
        }
        _disarmReason = DisarmReason.None;
        _isArmed = true;

        // Bubble is a gravity-void refuge — deactivate when returning to armed/ready state.
        DeactivateSafetyBubble();

        // Pokeability no longer controls locomotion.
        EnableColliders();
        dust?.RefreshDustIgnore();
        SetVisual(VisualMode.Bright, ResolvePreviewColorByReadiness());
        OnArmed?.Invoke(this);
    }
    private void Disarm(DisarmReason reason, Color? tintOverride = null)
    {
        _isArmed = false;
        _disarmReason = reason;
        Debug.Log($"[PhaseStar] Disarm reason={reason} star={name}");

        var tint = tintOverride ?? ResolvePreviewColorByReadiness();

        switch (reason)
        {
            case DisarmReason.AwaitBridge:
            case DisarmReason.Bridge:
                // True shutdown: disable colliders so the star doesn't interact while
                // the bridge plays out. Motion may stop separately in the bridge flow.
                DisableColliders();
                SetVisual(VisualMode.Hidden, tint);
                break;

            case DisarmReason.NodeResolving:
            case DisarmReason.CollectablesInFlight:
            case DisarmReason.ExpansionPending:
            default:
                // Keep colliders enabled during ordinary disarm so the star stays
                // bounded by the physical boundary walls while it continues to drift.
                // OnCollisionEnter2D already guards against pokes when !_isArmed,
                // and preserving collider state keeps Physics2D.IgnoreCollision dust
                // pairs intact (Unity clears them when a collider is disabled/re-enabled).
                SetVisual(VisualMode.Dim, tint);
                break;
        }

        OnDisarmed?.Invoke(this);
    }
    void InitializeTimingAndSpeeds()
    {
        // Prefer the DrumTrack that actually spawned this star.
        // Fall back to the globally active drum track if needed.
        var drums = _drum;
        if (!drums)
            drums = GameFlowManager.Instance?.activeDrumTrack;

        if (drums)
        {
            // Use the EFFECTIVE loop length (extended loop, not just clip length)
            _loopDuration = Mathf.Max(0.001f, drums.GetLoopLengthInSeconds());
        }
        else
        {
            // Defensive default so preview math never divides by zero
            _loopDuration = 2f;
        }
        // _omega / harmonic spin ladder is retired; shard facing is now sniffer-driven.
    }

    private bool CanAdvancePhaseNow()
    {
        // Enforce "no skip capture"
        if (_activeNode != null) return false;
        if (_ejectionInFlight) return false;
        return true;
    }

    private void OnLoopBoundary_RearmIfNeeded()
    {
        bool cif = AnyCollectablesInFlightGlobal();
        bool ep = AnyExpansionPendingGlobal();

        Debug.Log(
            $"[PS:LB] star={name} state={_state} armed={_isArmed} disarm={_disarmReason} " +
            $"awaitClr={_awaitingCollectableClear} awaitLoopFinish={_awaitingLoopPhaseFinish} advStarted={_advanceStarted} " +
            $"shards={_shardsEjectedCount}/{behaviorProfile.nodesPerStar} hasRem={HasShardsRemaining()} " +
            $"activeNode={(_activeNode ? _activeNode.name : "null")} ejectInFlight={_ejectionInFlight} " +
            $"CIF={cif} EP={ep}"
        );

        if (_isDisposing || this == null) return;

        // ------------------------------------------------------------
        // 1) Awaiting collectable clear (post-node-resolution latch)
        // ------------------------------------------------------------
        if (_awaitingCollectableClear)
        {
            // If collectables exist AND the last node is still live, wait.
            // But if the node is already captured (_activeNode == null), fall through to
            // bridge logic even with stray collectables still in flight.
            if (AnyCollectablesInFlightGlobal() && (_activeNode != null || _ejectionInFlight))
            {
                Debug.Log("[PS:LB/AWAIT] -> stay disarmed (awaitClr + CIF + active node)");
                Disarm(DisarmReason.NodeResolving, _lockedTint);
                return;
            }

            // No collectables in flight, but we're still "awaiting".
            // We must eventually recover; otherwise the PhaseStar can deadlock (commonly on last shard).
            var drums = _drum != null
                ? _drum
                : (GameFlowManager.Instance != null ? GameFlowManager.Instance.activeDrumTrack : null);

            bool timedOut = false;

            // Loop-based timeout
            if (CollectableClearTimeoutLoops > 0 && drums != null)
            {
                int nowLoop = drums.completedLoops;
                if (_awaitingCollectableClearSinceLoop < 0)
                    _awaitingCollectableClearSinceLoop = nowLoop;

                int waitedLoops = nowLoop - _awaitingCollectableClearSinceLoop;
                if (waitedLoops >= CollectableClearTimeoutLoops)
                    timedOut = true;

                Debug.Log(
                    $"[PS:LB/AWAIT.LOOPS] nowLoop={nowLoop} sinceLoop={_awaitingCollectableClearSinceLoop} waitedLoops={waitedLoops} loopsTimeout={CollectableClearTimeoutLoops} -> timedOut={timedOut}");

            }

            Debug.Log(
                $"[PhaseStar][Await] nowLoop={(drums != null ? drums.completedLoops : -1)} " +
                $"sinceLoop={_awaitingCollectableClearSinceLoop} " +
                $"waited={(drums != null && _awaitingCollectableClearSinceLoop >= 0 ? drums.completedLoops - _awaitingCollectableClearSinceLoop : -1)} " +
                $"loopsTimeout={CollectableClearTimeoutLoops} " +
                $"dspNow={AudioSettings.dspTime:F2} sinceDsp={_awaitingCollectableClearSinceDsp:F2} secTimeout={CollectableClearTimeoutSeconds:F2} " +
                $"collectablesInFlight={AnyCollectablesInFlightGlobal()} activeNode={(_activeNode != null)}"
            );

            // Seconds-based timeout (optional)
            if (!timedOut && CollectableClearTimeoutSeconds > 0f)
            {
                DBG("LoopBoundary: awaitingCollectableClear seconds-check running");
                double nowDsp = AudioSettings.dspTime;
                if (_awaitingCollectableClearSinceDsp < 0.0)
                    _awaitingCollectableClearSinceDsp = nowDsp;

                if ((nowDsp - _awaitingCollectableClearSinceDsp) >= CollectableClearTimeoutSeconds)
                    timedOut = true;
                Debug.Log(
                    $"[PS:LB/AWAIT.SECS] nowDsp={nowDsp:F2} sinceDsp={_awaitingCollectableClearSinceDsp:F2} secTimeout={CollectableClearTimeoutSeconds:F2} -> timedOut={timedOut}");
            }

            Debug.Log(
                $"[PS:LB/AWAIT] nowLoop={(drums != null ? drums.completedLoops : -1)} " +
                $"sinceLoop={_awaitingCollectableClearSinceLoop} loopsTimeout={CollectableClearTimeoutLoops} " +
                $"sinceDsp={_awaitingCollectableClearSinceDsp:F2} secTimeout={CollectableClearTimeoutSeconds:F2} " +
                $"CIF={AnyCollectablesInFlightGlobal()} EP={AnyExpansionPendingGlobal()} activeNode={(_activeNode != null)}"
            );

            // If we have not timed out, stay disarmed and keep waiting.
            if (!timedOut)
            {
                Debug.Log($"[PS:LB/AWAIT] -> continue waiting (not timed out) hasRem={HasShardsRemaining()}");
                Disarm(DisarmReason.NodeResolving, _lockedTint);
                return;
            }

            // Timeout recovery: clear latch and proceed immediately.
            Debug.LogWarning(
                $"[PhaseStar][Timeout] AwaitingCollectableClear timed out but no collectables are in flight. " +
                $"Forcing recovery. star={name} shardsEjected={_shardsEjectedCount}/{behaviorProfile.nodesPerStar}"
            );
            Debug.LogWarning(
                $"[PS:LB/RECOVERY] clearing awaitClr latch (before) awaitClr={_awaitingCollectableClear} sinceLoop={_awaitingCollectableClearSinceLoop} sinceDsp={_awaitingCollectableClearSinceDsp:F2}");

            _awaitingCollectableClear = false;
            _awaitingCollectableClearSinceLoop = -1;
            _awaitingCollectableClearSinceDsp = -1.0;

            bool noShardsRemain =
                (previewRing == null || previewRing.Count == 0) ||
                (_shardsEjectedCount >= Mathf.Max(1, behaviorProfile.nodesPerStar));
            int prCount = (previewRing != null ? previewRing.Count : -1);
            int nps = Mathf.Max(1, behaviorProfile.nodesPerStar);

            Debug.LogWarning(
                $"[PS:LB/RECOVERY] timedOut={timedOut} shardsEjected={_shardsEjectedCount}/{nps} " +
                $"previewRingCount={prCount} noShardsRemain={noShardsRemain} " +
                $"CIF={AnyCollectablesInFlightGlobal()} EP={AnyExpansionPendingGlobal()}"
            );
            if (!CanAdvancePhaseNow())
            {
                DBG(
                    $"[PS:LB/RECOVERY] block; outstanding node. activeNode={_activeNode?.name} ejectionInFlight={_ejectionInFlight}");
                Disarm(DisarmReason.NodeResolving, _lockedTint);
                return;
            }

            if (noShardsRemain)
            {
                Debug.Log($"[PS:LB] Begin Bridge");
                BeginBridgeNow();
                return;
            }

            Debug.Log($"[PS:LB] Arm Next");
            ArmNext();
            return;
        }

        // ------------------------------------------------------------
        // 2) Global gate checks
        // ------------------------------------------------------------
        if (AnyCollectablesInFlightGlobal())
        {
            Debug.Log($"[PS:LB] AnyCollectablesInFlightGlobal True");
            Disarm(DisarmReason.CollectablesInFlight, _lockedTint);
            return;
        }

        if (AnyExpansionPendingGlobal())
        {
            Debug.Log($"[PS:LB] Any Expanding Global True");
            Disarm(DisarmReason.ExpansionPending, _lockedTint);
            return;
        }

        // ------------------------------------------------------------
        // 3) Deterministic bridge trigger (end-of-star)
        // ------------------------------------------------------------
        // If the star is complete, we should bridge on the next clean loop boundary// even if a specific latch flag was not set (prevents “stuck not bridging”).bool shardsComplete = behaviorProfile != null && behaviorProfile.nodesPerStar > 0 && _shardsEjectedCount >= behaviorProfile.nodesPerStar;
        bool noShardsRemain0 = (previewRing == null || previewRing.Count == 0) ||
                               (_shardsEjectedCount >= Mathf.Max(1, behaviorProfile.nodesPerStar));
        // HARD BLOCK: if a MineNode is still active (not captured/resolved), we cannot advance.
        // This enforces "no skip capture" as a player choice.
        if (_activeNode != null || _ejectionInFlight)
        {
            DBG(
                $"LoopBoundary: block bridge; outstanding node. activeNode={_activeNode?.name} ejectionInFlight={_ejectionInFlight}");
            return;
        }

        if (!_advanceStarted && _state != PhaseStarState.BridgeInProgress && !HasShardsRemaining() && noShardsRemain0
            && _activeNode == null && !_ejectionInFlight
            && !AnyExpansionPendingGlobal())
        {
            Debug.LogWarning(
                $"[PS:LB] FORCE_BRIDGE shardsEjected={_shardsEjectedCount}/{behaviorProfile.nodesPerStar} " +
                $"preview={(previewRing != null ? previewRing.Count : -1)} armed={_isArmed} state={_state} " +
                $"awaitClr={_awaitingCollectableClear} awaitLoopFinish={_awaitingLoopPhaseFinish}"
            );
            BeginBridgeNow();
            return;
        }

        LogState("LoopBoundary entry");

        // ------------------------------------------------------------
        // 3) Bridge logic (phase completion)
        // ------------------------------------------------------------
        if (_advanceStarted)
        {
            Debug.Log("[PS:LB] Advance Started");
            return;
        }

        if (_state == PhaseStarState.BridgeInProgress)
        {
            if (!CanAdvancePhaseNow())
            {
                DBG(
                    $"[PS:LB] BridgeInProgress but blocked; outstanding node. activeNode={_activeNode?.name} ejectionInFlight={_ejectionInFlight}");
                // Stay in BridgeInProgress if you want, but DO NOT start the coroutine.
                // I recommend reverting to WaitingForPoke to avoid a stuck bridge state:
                _state = PhaseStarState.WaitingForPoke;
                Disarm(DisarmReason.NodeResolving, _lockedTint);
                return;
            }

            _advanceStarted = true;
            _awaitingLoopPhaseFinish = false;
            DBG("[PS:LB] Bridge In Progress");
            StartCoroutine(CompleteAndAdvanceAsync());
            return;
        }

        if (_awaitingLoopPhaseFinish)
        {
            if (!CanAdvancePhaseNow())
            {
                DBG(
                    $"[PS:LB] AwaitLoopPhaseFinish but blocked; outstanding node. activeNode={_activeNode?.name} ejectionInFlight={_ejectionInFlight}");
                // Consume nothing; keep waiting.
                Disarm(DisarmReason.NodeResolving, _lockedTint);
                return;
            }

            DBG("[PS:LB] Await Loop Phase Finish");
            _advanceStarted = true;
            _awaitingLoopPhaseFinish = false;
            _state = PhaseStarState.BridgeInProgress;
            Disarm(DisarmReason.Bridge, _lockedTint);
            Trace("LoopBoundary → Begin bridge");
            StartCoroutine(CompleteAndAdvanceAsync());
            return;
        }

        // ------------------------------------------------------------
        // 4) Normal re-arm path
        // ------------------------------------------------------------
        if (!_isArmed)
        {
            // If the plan is fully completed, stay quiet and let the bridge path take over.
            if (_shardsEjectedCount >= behaviorProfile.nodesPerStar && behaviorProfile.nodesPerStar > 0)
            {
                Debug.Log($"[PS:LB] -> Not Armed, Ejected Shards ");
                return;
            }

            DBG("[PS:LB] -> Armed, Ejected Shards");
            ArmNext();
        }
        else
        {
            DBG("[PS:LB] -> No need to arm");
        }

    }
    
    private void DBG(string msg)
    {
        Debug.Log($"[PSDBG] {msg} :: star={name} state={_state} armed={_isArmed} advStarted={_advanceStarted} " +
                  $"awaitCollectClear={_awaitingCollectableClear} awaitLoopFinish={_awaitingLoopPhaseFinish} " +
                  $"shards={_shardsEjectedCount}/{behaviorProfile?.nodesPerStar} preview={(previewRing != null ? previewRing.Count : -1)} " +
                  $"activeNode={(_activeNode != null ? _activeNode.name : "null")} lockedTint={_lockedTint}");
    }
    
    void BuildOrRefreshPreviewRing()
    {
        int N = previewRing.Count;
        if (N == 0) return;

        // Set each shard's initial rotation to point upward (sniffers will take over in Update).
        // Apply sorting order by ascending index; UpdateShardSortingByCharge() in Update will reorder by charge.
        for (int i = 0; i < N; i++)
        {
            var t = previewRing[i].visual;
            if (!t) continue;
            t.localRotation = Quaternion.Euler(0f, 0f, 0f); // face up initially
            var sr = t.GetComponent<SpriteRenderer>();
            if (sr)
            {
                sr.sortingOrder = _baseSortingOrder + (i * _perPetalLayerStep);
                // Start at minimum alpha; will lerp up as charge accumulates
                var c = sr.color;
                c.a = shardMinAlpha;
                sr.color = c;
            }
        }

        // Initialise timing (still needed for _loopDuration etc.)
        InitializeTimingAndSpeeds();
    }

    private void EnsurePreviewRing()
    {
        if (_previewInitialized) return;
        _previewInitialized = true;

        RebuildPreviewRingForRemainingShards(keepCurrentIndex:false);
    }

    private void OnDisable()
    {
        var drum = GameFlowManager.Instance != null ? GameFlowManager.Instance.activeDrumTrack : null;
        SafeUnsubscribeAll();
        if (!StarKeepsDustClear) return;
        var gen = gfm != null ? gfm.dustGenerator : null;
        if (gen == null) return;

        var phase = gfm.phaseTransitionManager != null ? gfm.phaseTransitionManager.currentPhase : _assignedPhase;
        gen.ClearStarKeepClear(phase);
        
    }

    private void OnDestroy()
    {
        _isDisposing = true;
        SafeUnsubscribeAll();
    }

    public void WireBinSource(DrumTrack drum)
    {
        _drum = drum;
        if (_drum == null) return;
        UpdatePreviewTint();
        InitializeTimingAndSpeeds();
        BuildOrRefreshPreviewRing();
    }
    
    private void BuildPhasePlan(MazeArchetype phase, int shardCount)
    {
        _phasePlanRoles = new List<MusicalRole>();
        if (!spawnStrategyProfile) return;

        int target = Mathf.Max(1, shardCount);

        for (int i = 0; i < target; i++)
        {
            // “always balanced” model: rebuild from the authored strategy order,
            // repeated/cropped to the CURRENT remaining shard count.
            MusicalRole role = spawnStrategyProfile.PeekRoleAtOffset(i, target);
            _phasePlanRoles.Add(role);
        }

        Trace($"BuildPhasePlan: planned {_phasePlanRoles.Count}/{target} roles (phase={phase})");
    }

    private bool HasShardsRemaining() => _shardsEjectedCount < behaviorProfile.nodesPerStar;

    private int GetRemainingShardCount()
    {
        if (behaviorProfile == null) return 0;
        int total = Mathf.Max(0, behaviorProfile.nodesPerStar);
        int rem = total - Mathf.Max(0, _shardsEjectedCount);
        return Mathf.Clamp(rem, 0, total);
    }

    private void RebuildPreviewRingForRemainingShards(bool keepCurrentIndex = true)
    {
        if (behaviorProfile == null || visuals == null) return;
        int remaining = GetRemainingShardCount();

        if (remaining <= 0)
        {
            for (int i = 0; i < previewRing.Count; i++)
            {
                var v = previewRing[i].visual;
                if (v) Destroy(v.gameObject);
            }
            previewRing.Clear();
            activeShardVisual = null;
            return;
        }

        if (!keepCurrentIndex) currentShardIndex = 0;
        currentShardIndex = Mathf.Clamp(currentShardIndex, 0, remaining - 1);

        BuildPhasePlan(_assignedPhase, remaining);
        BuildPreviewRing();
        BuildOrRefreshPreviewRing();
        UpdatePreviewTint();
    }
    private void PrepareNextDirective()
    {
        Trace("PrepareNextDirective() begin");

        _cachedTrack = null;
        _cachedIsSuperNode = false;
        
        if (_drum == null || spawnStrategyProfile == null) return;

        MusicalRole role = GetPlannedRoleForHighlightedShard();

        InstrumentTrack track = FindTrackByRole(role);
        if (track == null) return;

        _cachedTrack = track;

        // --- Decide what NoteSet a "normal" node would use ---
        // Primary: repeat whatever the track is already using.
        NoteSet planned = track.GetCurrentNoteSet();

        // Optional fallback: if your system expects a note set to exist,
        // generate one in the same way HarmonyDirector does.
        if (planned == null)
        {
            var gfm = GameFlowManager.Instance;
            var factory = gfm != null ? gfm.phaseTransitionManager.noteSetFactory : null;
            var phase = (gfm != null && gfm.phaseTransitionManager != null)
                ? gfm.phaseTransitionManager.currentPhase
                : MazeArchetype.Establish;

            if (factory != null)
                planned = factory.Generate(track, _assignedMotif);
        }
        
        // --- Saturation decision ---
        // Use repeating check if you only want super nodes on "same NoteSet again".
// --- Saturation decision ---
// SuperNode only when the track is fully expanded AND repeating the same NoteSet would add no new coverage.
        if (planned != null)
        {
            int maxBins = Mathf.Max(1, track.maxLoopMultiplier);
            bool atMaxBins = track.loopMultiplier >= maxBins;

            _cachedIsSuperNode = (_cachedTrack != null) && ShouldSpawnSuperNodeForTrack(_cachedTrack);


        }
        else
        {
            _cachedIsSuperNode = false;
        }

        // If you want super nodes anytime the set would add no new step coverage:
        // _cachedIsSuperNode = track.IsSaturatedForNoteSet(planned);

        if (!_lockPreviewTintUntilIdle)
            UpdatePreviewTint();
    }
    void BuildPreviewRing()
    {
        buildingPreview = true;

        // Always-balanced model rebuilds the ring often; destroy prior visuals to avoid leaks.
        for (int i = 0; i < previewRing.Count; i++)
        {
            var v = previewRing[i].visual;
            if (v) Destroy(v.gameObject);
        }
        previewRing.Clear();

        if (_baseSortingOrder == 0)
        {
            var baseSr = GetComponentInChildren<SpriteRenderer>(true);
            _baseSortingOrder = baseSr ? baseSr.sortingOrder : 2000;
            if (_perPetalLayerStep <= 0) _perPetalLayerStep = 1;
        }

        // Ensure we have a plan to visualize.
        if (_phasePlanRoles == null || _phasePlanRoles.Count != behaviorProfile.nodesPerStar)
        {
            // If you have _assignedPhase or equivalent, pass it; otherwise pass the active phase you already cache.
            BuildPhasePlan(_assignedPhase, Mathf.Max(1, behaviorProfile.nodesPerStar));
        }

        int n = (_phasePlanRoles != null) ? _phasePlanRoles.Count : 0;
        if (n <= 0)
        {
            currentShardIndex = 0;
            activeShardVisual = null;
            buildingPreview = false;
            return;
        }

        var angles = visuals.GetPetalAngles(n);

        for (int i = 0; i < n; i++)
        {
            float ang = angles[Mathf.Clamp(i, 0, angles.Length - 1)];

            // Plan entry for THIS petal index
            var role = _phasePlanRoles[i];
            // Color MUST come from the role’s track, not a cycling palette.
            var track = FindTrackByRole(role);

            // Role color is the lerp TARGET stored in the shard struct.
            // Fall back to MusicalRoleProfileLibrary, then a visible default so it's never invisible.
            Color roleColor;
            var roleProfile = MusicalRoleProfileLibrary.GetProfile(role);
            if (roleProfile != null)
                roleColor = new Color(roleProfile.dustColors.baseColor.r, roleProfile.dustColors.baseColor.g, roleProfile.dustColors.baseColor.b, 1f);
            else if (track != null)
                roleColor = new Color(track.trackColor.r, track.trackColor.g, track.trackColor.b, 1f);
            else
                roleColor = Color.white;

            Color startGray = new Color(0.45f, 0.45f, 0.45f, shardMinAlpha);
            Color shadow = track != null ? track.TrackShadowColor : new Color(0.08f,0.08f,0.08f,1f);

 
            var shardGO = new GameObject($"PreviewShard_{i}_{role}");
            shardGO.transform.SetParent(transform);
            shardGO.transform.localPosition = Vector3.zero;
            shardGO.transform.localRotation = Quaternion.Euler(0f, 0f, ang);
            shardGO.transform.localScale = Vector3.one;

            var sr = shardGO.AddComponent<SpriteRenderer>();
            sr.sprite = visuals.diamond;

// Keep previewRing.color as the true target role color,
// but start the visible shard in gray.
            sr.color = new Color(0.45f, 0.45f, 0.45f, shardMinAlpha);
            sr.sortingOrder = _baseSortingOrder + (i * _perPetalLayerStep);
            
            previewRing.Add(new PreviewShard
            {
                color = roleColor,
                shadowColor = shadow,
                collected = false,
                visual = shardGO.transform,
                role = role
            });

        }

        currentShardIndex = Mathf.Clamp(currentShardIndex, 0, previewRing.Count - 1);
        activeShardVisual = previewRing[currentShardIndex].visual;
        buildingPreview = false;

        // Ensure highlight + tint matches the new authoritative shard color
        UpdatePreviewTint();
        HighlightActive();
    }

    /// <summary>
    /// Re-sorts shard sorting orders so the highest-charge role sits on top.
    /// Called every frame from Update after alpha is applied.
    /// </summary>
    private void UpdateShardSortingByCharge()
    {
        if (previewRing == null || previewRing.Count == 0) return;
        if (_baseSortingOrder == 0 && previewRing.Count > 0)
        {
            var baseSr = GetComponentInChildren<SpriteRenderer>(true);
            _baseSortingOrder = baseSr ? baseSr.sortingOrder : 2000;
            if (_perPetalLayerStep <= 0) _perPetalLayerStep = 1;
        }

        // Build a charge-sorted index list (descending: highest charge = lowest list index = highest sort order)
        // We only need to sort once per frame, and previewRing is small (≤4 items).
        int n = previewRing.Count;
        // Simple insertion sort on small N
        var order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;
        for (int i = 1; i < n; i++)
        {
            int key = order[i];
            _starCharge.TryGetValue(previewRing[key].role, out float keyCharge);
            int j = i - 1;
            while (j >= 0)
            {
                _starCharge.TryGetValue(previewRing[order[j]].role, out float jCharge);
                if (jCharge <= keyCharge) break;
                order[j + 1] = order[j];
                j--;
            }
            order[j + 1] = key;
        }
        // order[n-1] = index of highest-charge shard → gets top sorting order
        for (int rank = 0; rank < n; rank++)
        {
            int shardIdx = order[rank];
            var sr = previewRing[shardIdx].visual?.GetComponent<SpriteRenderer>();
            if (sr == null) continue;
            sr.sortingOrder = _baseSortingOrder + (rank * _perPetalLayerStep);
        }

        // Sorting order only — dominance is resolved by ResolveDominantRoleFromCharge()
        // which applies hysteresis and soft switch delta (BALANCE-E).
    }

    private InstrumentTrack FindTrackByRole(MusicalRole role)
    {
        var controller = GameFlowManager.Instance?.controller;
        if (controller == null || controller.tracks == null) return null;

        foreach (var t in controller.tracks)
            if (t != null && t.assignedRole == role)
                return t;

        return null;
    }
    private MusicalRole GetPlannedRoleForHighlightedShard()
    {
        if (_phasePlanRoles == null || _phasePlanRoles.Count == 0) return MusicalRole.Bass;

        int idx = Mathf.Clamp(currentShardIndex, 0, _phasePlanRoles.Count - 1);
        return _phasePlanRoles[idx];
    }
    void UpdatePreviewTint()
    {
        if (visuals == null) visuals = GetComponentInChildren<PhaseStarVisuals2D>(true);
        if (visuals == null) return;
        var color = ResolvePreviewColorByReadiness();
        var shadow = ResolvePreviewShadowColor();
        visuals.SetPreviewTint(color, shadow);
        
        if (activeShardVisual) HighlightActive();
    }
    private void HighlightActive()
    {
        if (visuals == null) visuals = GetComponentInChildren<PhaseStarVisuals2D>(true);
        if (visuals == null) return;

        if (previewRing == null || previewRing.Count == 0 || activeShardVisual == null)
        {
            // No active shard to highlight; just clear veil state if you have a method for it.
            return;
        }

        Color c = ResolvePreviewColorByReadiness();
        float ready01 = Mathf.Clamp01(GetTotalCharge() / Mathf.Max(0.001f, pokeChargeThreshold));
        float highlight = Mathf.Lerp(0.06f, 0.7f, ready01);
        float veilA     = Mathf.Lerp(0.08f, 0.25f, ready01);
        // “Dim means busy” is handled elsewhere; this is just the per-petal highlight.
        visuals.SetVeilOnNonActive(new Color(1f, 1f, 1f, veilA), activeShardVisual);
        visuals.HighlightActive(activeShardVisual, c, highlight);
    }

    private void ResolveDominantRoleFromCharge()
    {
        if (previewRing == null || previewRing.Count == 0) return;

        int bestIdx = -1;
        float bestCharge = -1f;

        for (int i = 0; i < previewRing.Count; i++)
        {
            _starCharge.TryGetValue(previewRing[i].role, out float c);
            if (c > bestCharge)
            {
                bestCharge = c;
                bestIdx = i;
            }
        }

        if (bestIdx < 0) return;

        if (currentShardIndex < 0 || currentShardIndex >= previewRing.Count)
        {
            currentShardIndex = bestIdx;
            activeShardVisual = previewRing[currentShardIndex].visual;
            return;
        }

        _starCharge.TryGetValue(previewRing[currentShardIndex].role, out float curCharge);
        if (bestIdx != currentShardIndex)
        {
            // [BALANCE-E] Soften the switch delta when the challenger is starving.
            // A role the star hasn't fed on yet should flip dominance easily.
            float effectiveDelta = dominantRoleSwitchDelta;
            if (bestCharge < chargeTastedThreshold)
            {
                // Challenger is freshly encountered — almost no hysteresis needed.
                effectiveDelta *= 0.15f;
            }

            if (bestCharge >= curCharge + effectiveDelta)
            {
                currentShardIndex = bestIdx;
                activeShardVisual = previewRing[currentShardIndex].visual;
            }
        }
    }
    public void SetGravityVoidSafetyBubbleActive(bool active)
    {
        Debug.Log($"[BUBBLE] SetGravityVoidSafetyBubbleActive active={active} star={name} frame={Time.frameCount}");
        if (active) ActivateSafetyBubble();
        else DeactivateSafetyBubble();
    }

    private Color ResolvePreviewShadowColor()
    {
        if (previewRing == null || previewRing.Count == 0) return new Color(0.08f, 0.08f, 0.08f, 1f);
        int idx = Mathf.Clamp(currentShardIndex, 0, previewRing.Count - 1);
        return previewRing[idx].shadowColor;
    }
    private void OnCollisionEnter2D(Collision2D coll)
    {
        if (!coll.gameObject.TryGetComponent<Vehicle>(out _)) return;

        // Disarmed push: bypass the CIF/EP gates so the vehicle always shoves the star
        // while it is waiting between ejections. Collectables are in-flight during this
        // window, so the old order (CIF check first) was preventing the push entirely.
        if (!_isArmed
            && HasShardsRemaining()
            && _state == PhaseStarState.WaitingForPoke
            && !_ejectionInFlight)
        {
            HandleDisarmedVehicleHit(coll);
            return;
        }

        if (AnyCollectablesInFlightGlobal())
        {
            Disarm(DisarmReason.CollectablesInFlight, _lockedTint);
            Trace("OnCollisionEnter2D: ignored poke because collectables are still in flight");
            return;
        }

        if (AnyExpansionPendingGlobal())
        {
            Disarm(DisarmReason.ExpansionPending, _lockedTint);
            Trace("OnCollisionEnter2D: ignored poke because an expansion is pending");
            return;
        }

        // --- Safety & gating ---
        if (!HasShardsRemaining()) return;
        if (_state != PhaseStarState.WaitingForPoke)
        {
            Trace($"OnCollision: ignored, state={_state}");
            return;
        }

        if (_ejectionInFlight)
        {
            Trace("OnCollision: ignored, busy flags");
            return;
        }
        if (!IsChargeReady())
        {
            Trace("OnCollisionEnter2D: ignored poke because charge threshold not met");
            return;
        }
        if (_activeNode != null)
        {
            Trace("OnCollision: ignored, activeNode != null");
            return;
        }

        if (Time.frameCount == _lastPokeFrame)
        {
            Trace("OnCollision: ignored, same frame");
            return;
        }

        _lastPokeFrame = Time.frameCount;

        if (_cachedTrack == null)
            PrepareNextDirective();
        // --- Handle missing directive fallback ---
        if (_cachedTrack == null)
        {
            Trace("OnCollision: no directive/track → disarm and wait");
            Disarm(DisarmReason.NodeResolving, _lockedTint);
            return;

        }
        if (previewRing != null && previewRing.Count > 0)
        {
            if (_bubbleActive) s_bubbleCenter = transform.position;
            EjectActivePreviewShardAndFlow(coll);
            return;
        }

        if (_bubbleActive) s_bubbleCenter = transform.position;
        EjectCachedDirectiveAndFlow(coll);
    }

    private void HandleDisarmedVehicleHit(Collision2D coll)
    {
        if (motion != null)
        {
            Vector2 starPos   = transform.position;
            Vector2 pushDir   = coll.contacts.Length > 0
                ? (starPos - (Vector2)coll.contacts[0].point).normalized
                : ((starPos - (Vector2)coll.transform.position).normalized);
            float   pushSpeed = Mathf.Clamp(coll.relativeVelocity.magnitude * disarmedPushScale,
                                             0f, MaxImpactStrength);
            motion.ApplyPushImpulse(pushDir * pushSpeed);
        }
        visuals?.FlashReject();
    }

    void SpawnNodeCommon(Vector2 contactPoint, InstrumentTrack usedTrack)
    {
        int ticket = ++_spawnTicket;
        _ejectionInFlight = true;
        visuals?.EjectParticles();

        var shard = (previewRing != null && previewRing.Count > 0)
            ? previewRing[Mathf.Clamp(currentShardIndex, 0, previewRing.Count - 1)]
            : default;

        Color spawnTint   = shard.visual != null ? shard.color : usedTrack.trackColor;
        Color shadowTint  = shard.visual != null ? shard.shadowColor : usedTrack.TrackShadowColor;

        _lockedTint = spawnTint;
        _lockPreviewTintUntilIdle = true;
        visuals?.SetPreviewTint(spawnTint, shadowTint);

        var node = DirectSpawnMineNode(contactPoint, usedTrack, spawnTint);
        if (node == null)
        {
            _ejectionInFlight = false;
            _activeNode = null;
            Disarm(DisarmReason.NodeResolving, spawnTint);
            return;
        }

        // Close previous corridor on spawn
        var gen = gfm != null ? gfm.dustGenerator : null;
        if (gen != null)
        {
            var phase = (gfm.phaseTransitionManager != null) ? gfm.phaseTransitionManager.currentPhase : _assignedPhase;
            gen.RegrowPreviousCorridorOnNewNodeSpawn(phase);
        }

        _activeNode = node;
        _ejectionInFlight = false;

        bool handledResolve = false;

        node.OnResolved += (resolvedNode) =>
        {
            if (ticket != _spawnTicket) return;
            if (handledResolve) return;
            handledResolve = true;

            _activeNode = null;

            if (_state == PhaseStarState.BridgeInProgress || _advanceStarted) return;

            _awaitingCollectableClear = true;
            _awaitingCollectableClearSinceLoop = (_drum != null)
                ? _drum.completedLoops
                : (GameFlowManager.Instance?.activeDrumTrack?.completedLoops ?? -1);
            _awaitingCollectableClearSinceDsp = AudioSettings.dspTime;
            // If this was the final shard, keep the star hidden (AwaitBridge) rather than
            // flashing back to Dim (NodeResolving), which would make an empty-shard star visible.
            var postResolveReason = HasShardsRemaining() ? DisarmReason.NodeResolving : DisarmReason.AwaitBridge;
            Disarm(postResolveReason, spawnTint);
            LogState("OnResolved");
        };

        CollectionSoundManager.Instance?.PlayPhaseStarImpact(usedTrack, usedTrack.GetCurrentNoteSet(), 0.8f);
        PrepareNextDirective();
        Trace("SpawnNodeCommon: end");
    }

    void EjectActivePreviewShardAndFlow(Collision2D coll)
    {
        if (behaviorProfile == null || visuals == null) return;
        if (previewRing == null || previewRing.Count == 0) return;
        if (!HasShardsRemaining()) return;

        // At ejection, use the highest-charge shard directly — no hysteresis.
        // This matches the sort order (highest on top) that the player reads as "what will fire."
        int shardIdx = 0;
        float bestEjectCharge = -1f;
        for (int i = 0; i < previewRing.Count; i++)
        {
            _starCharge.TryGetValue(previewRing[i].role, out float c);
            if (c > bestEjectCharge) { bestEjectCharge = c; shardIdx = i; }
        }
        // Snap the highlight to match so the visual is consistent post-eject.
        currentShardIndex = shardIdx;
        activeShardVisual = previewRing[shardIdx].visual;
        var shard = previewRing[shardIdx];

        MusicalRole ejectedRole = shard.role;
        InstrumentTrack ejectedTrack = FindTrackByRole(ejectedRole);
        if (ejectedTrack == null)
        {
            Debug.LogError($"[PhaseStar] Missing track for ejected role={ejectedRole} (cannot spawn node).");
            return;
        }

        _starCharge[ejectedRole] = 0f;
        _tastedRoles.Remove(ejectedRole); // ejection spends the charge; role must re-earn its floor
        var contact = coll.GetContact(0).point;
        var starPos = (Vector2)transform.position;
        var vehiclePos = coll.rigidbody != null ? coll.rigidbody.position : contact;

        Vector2 incoming = (starPos - vehiclePos);
        _lastImpactDir = (incoming.sqrMagnitude > 0.0001f) ? incoming.normalized : Vector2.right;
        _lastImpactStrength = Mathf.Clamp(coll.relativeVelocity.magnitude, 0f, MaxImpactStrength);

        _shardsEjectedCount++;
        int remainingAfter = GetRemainingShardCount();
        bool isFinalShardEjection = (remainingAfter <= 0);

        Disarm(isFinalShardEjection ? DisarmReason.AwaitBridge : DisarmReason.NodeResolving,
            ejectedTrack.trackColor);

        Debug.Log($"[MNDBG] EjectActive: contact={contact}, role={ejectedTrack.assignedRole}");
        if (ShouldSpawnSuperNodeForTrack(ejectedTrack))
            SpawnSuperNodeCommon(contact, ejectedTrack);
        else
            SpawnNodeCommon(contact, ejectedTrack);

        currentShardIndex = Mathf.Clamp(currentShardIndex, 0, Mathf.Max(0, remainingAfter - 1));
        RebuildPreviewRingForRemainingShards(keepCurrentIndex: true);
        PrepareNextDirective();
    }
    /// <summary>
    /// 0 = satiated (at least one shard is above shardReadyThreshold).
    /// 1 = starving (no shard has crossed the threshold).
    /// Used by PhaseStarMotion2D to scale drift speed.
    /// </summary>
    public float GetHungerLevel()
    {
        if (previewRing == null || previewRing.Count == 0) return 1f;
        for (int i = 0; i < previewRing.Count; i++)
        {
            _starCharge.TryGetValue(previewRing[i].role, out float c);
            if (c >= shardReadyThreshold) return 0f;
        }
        // Partial hunger: lerp based on how close the best shard is to threshold
        float best = 0f;
        for (int i = 0; i < previewRing.Count; i++)
        {
            _starCharge.TryGetValue(previewRing[i].role, out float c);
            if (c > best) best = c;
        }
        return 1f - Mathf.Clamp01(best / Mathf.Max(0.001f, shardReadyThreshold));
    }
    private void SpawnSuperNodeCommon(Vector2 contactWorld, InstrumentTrack targetTrack)
    {
        if (superNodePrefab == null)
        {
            Debug.LogError("[PhaseStar] superNodePrefab is null.");
            return;
        }

        if (soloVoice == null)
        {
            soloVoice = FindAnyObjectByType<SoloVoice>();
            if (soloVoice == null)
            {
                Debug.LogError("[PhaseStar] SoloVoice not found.");
                return;
            }
        }

        // Spawn
        var go = Instantiate(superNodePrefab, contactWorld, Quaternion.identity);
//        _activeNode = go;

        // Initialize component
        var sn = go.GetComponent<SuperNode>();
        if (sn == null)
        {
            Debug.LogError("[PhaseStar] SuperNode prefab missing SuperNode component.");
            return;
        }
        sn.Initialize(soloVoice, _drum);

    }

    void EjectCachedDirectiveAndFlow(Collision2D coll)
    {
        var contact = coll.GetContact(0).point;
        // Compute impact direction & strength
        var starPos = (Vector2)transform.position;
        var vehiclePos = coll.rigidbody != null ? coll.rigidbody.position : contact;

        _lastImpactDir = (starPos - vehiclePos).normalized;
        _lastImpactStrength = Mathf.Clamp(coll.relativeVelocity.magnitude, 0f, MaxImpactStrength);
        _shardsEjectedCount++;

        bool isFinal = (_shardsEjectedCount >= Mathf.Max(1, behaviorProfile.nodesPerStar));
        Disarm(isFinal ? DisarmReason.AwaitBridge : DisarmReason.NodeResolving, _cachedTrack.trackColor);
        ActivateSafetyBubble();
        if (_cachedIsSuperNode)
            SpawnSuperNodeCommon(contact, _cachedTrack);
        else
            SpawnNodeCommon(contact, _cachedTrack);
    }
    private bool ShouldSpawnSuperNodeForTrack(InstrumentTrack track)
    {
        if (track == null) return false;

        // --- Interpret "fully expanded" in a bin-count safe way ---
        // If your loopMultiplier is already 1..maxBins, this works.
        // If it's 0..(maxBins-1), this also works because we treat it as "bins = loopMultiplier + 1".
        int maxBins = Mathf.Max(1, track.maxLoopMultiplier);

        // Try to interpret loopMultiplier robustly
        int binsIfMultiplierIsCount = Mathf.Max(1, track.loopMultiplier);
        int binsIfMultiplierIsIndex = Mathf.Max(1, track.loopMultiplier + 1);

        bool fullyExpanded =
            (binsIfMultiplierIsCount >= maxBins) ||
            (binsIfMultiplierIsIndex >= maxBins);

        if (!fullyExpanded)
        {
            Debug.Log(
                $"[SuperNodeGate] NO: not fully expanded. " +
                $"track={track.name} role={track.assignedRole} loopMul={track.loopMultiplier} maxBins={maxBins} " +
                $"bins(count)={binsIfMultiplierIsCount} bins(index+1)={binsIfMultiplierIsIndex}"
            );
            return false;
        }

        // --- Must have a current note set ---
        var planned = track.GetCurrentNoteSet();
        if (planned == null)
        {
            Debug.Log(
                $"[SuperNodeGate] NO: planned noteSet is null. " +
                $"track={track.name} role={track.assignedRole} loopMul={track.loopMultiplier} maxBins={maxBins}"
            );
            return false;
        }

        // --- The actual differentiator: repeating set is already saturated ---
        bool saturated = track.IsSaturatedForRepeatingNoteSet(planned);

        Debug.Log(
            $"[SuperNodeGate] {(saturated ? "YES" : "NO")}: " +
            $"track={track.name} role={track.assignedRole} loopMul={track.loopMultiplier} maxBins={maxBins} " +
            $"saturated={saturated}"
        );

        return saturated;
    }
    private MineNode DirectSpawnMineNode(Vector3 spawnFrom, InstrumentTrack track, Color color)
    {
        if (track == null || _drum == null) return null;

        Vector2Int cell = _drum.GetRandomAvailableCell(); // ✅ DrumTrack wrapper
        if (cell.x < 0) return null;
        _drum.OccupySpawnCell(cell.x, cell.y, GridObjectType.Node);
        var go = Instantiate(_drum.mineNodePrefab, spawnFrom, Quaternion.identity);
        var node = go.GetComponent<MineNode>();
        if (!node)
        {
            Destroy(go);
            _drum.FreeSpawnCell(cell.x, cell.y);            
            return null;
        }

        // color shell immediately so it never flashes white
        var sr = go.GetComponentInChildren<SpriteRenderer>();
        if (sr) sr.color = color;
        var rb = go.GetComponent<Rigidbody2D>();
        if (rb != null && _lastImpactDir.sqrMagnitude > 0.0001f && _lastImpactStrength > 0f)
        {
            rb.linearVelocity = _lastImpactDir * _lastImpactStrength;
        }
        int entropy = CurrentEntropyForSelection();
        var noteSet = GameFlowManager.Instance != null ? GameFlowManager.Instance.GenerateNotes(track, entropy) : null;
        node.Initialize(track, noteSet, color, cell);        
        return node;
    }
    private int CurrentEntropyForSelection() {
        // Minimal: shard selection index. Replace with per-role toggles later if desired.
        return currentShardIndex;
    }
    private IEnumerator CompleteAndAdvanceAsync()
    {
        Trace("Bridge: enter");
        _advanceStarted = true;
        _awaitingLoopPhaseFinish = false;
        Disarm(DisarmReason.Bridge);
        if (_drum) _drum.isPhaseStarActive = false;

        // Decide the next phase based on the PhaseStar's assigned phase.
        // The star itself encodes where we’re going next.
        var next = _assignedPhase;

        Trace($"BeginPhaseBridge → next={next}");
        GameFlowManager.Instance?.BeginMotifBridge(next, "PhaseStar/CompleteAdvanceAsync");

        // --- Wait for the bridge to start (be defensive) ---
        float start = Time.time;
        const float maxStartWait = 2.0f;
        while (GameFlowManager.Instance &&
               !GameFlowManager.Instance.GhostCycleInProgress &&
               (Time.time - start) < maxStartWait)
        {
            yield return null;
        }

        if (!(GameFlowManager.Instance && GameFlowManager.Instance.GhostCycleInProgress))
        {
            Debug.LogWarning("[PhaseStar] Bridge never started; aborting advancement safely.");
            _state = PhaseStarState.Completed;
            try
            {
                if (_drum) _drum._star = null;
            }
            catch
            {
            }

            Destroy(gameObject);
            yield break; // once destroyed, bail out immediately
        }


        Trace("Bridge started (GhostCycleInProgress = true)");

        // --- Decide a safe timeout, once, using DrumTrack’s new loop helpers ---
        float timeoutSec = 0f;
        if (_drum)
        {
            timeoutSec = _drum.GetTimeToLoopEnd(); // uses effective loop length internally
            if (timeoutSec <= 0f)
                timeoutSec = _loopDuration > 0f ? _loopDuration : 2f;
        }
        else
        {
            timeoutSec = _loopDuration > 0f ? _loopDuration : 2f;
        }

        float startedAt = Time.time;
        while (GameFlowManager.Instance &&
               GameFlowManager.Instance.GhostCycleInProgress &&
               (Time.time - startedAt) < timeoutSec)
        {
            yield return null;
        }

        if (GameFlowManager.Instance && GameFlowManager.Instance.GhostCycleInProgress)
        {
            Debug.LogWarning("[PhaseStar] Bridge timed out; forcing completion to avoid soft-lock.");
            // We don’t try to force the GFM flag here; we just exit gracefully.
        }

        _state = PhaseStarState.Completed;
        _isDisposing = true;
        SafeUnsubscribeAll();
        try
        {
            if (_drum) _drum._star = null;
        }
        catch
        {
        }

        Destroy(gameObject);
        yield break;
    }

    private void EnableColliders()
    {
        if (_isDisposing || this == null) return;
        var cols = GetComponentsInChildren<Collider2D>(true);
        foreach (var c in cols)
        {
            if (!c) continue;
            c.enabled = true;
        }
    }

    private void DisableColliders()
    {
        if (_isDisposing || this == null) return;
        var cols = GetComponentsInChildren<Collider2D>(true);
        foreach (var c in cols)
        {
            if (!c) continue;
            c.enabled = false;
        }
    }

    private void Trace(string msg)
    {
        if (_tracePhaseStar)
            Debug.Log($"[PhaseStar] {msg}");
    }

    private void LogState(string where)
    {
        if (_isDisposing || this == null || !_tracePhaseStar) return;
        string targRole =
            (previewRing != null && previewRing.Count > 0)
                ? previewRing[currentShardIndex].role.ToString()
                : "-";

    }

    private void ActivateSafetyBubble()
    {
        if (!SafetyBubbleEnabled) return;
        Debug.Log($"[BUBBLE] ActivateSafetyBubble star={name} frame={Time.frameCount}");

        float cell = ComputeCellWorldSize();

        // +0.5f gives the bubble a little breathing room relative to discrete cells
        _bubbleRadiusWorld = (SafetyBubbleRadiusCells + 0.5f) * cell;

        _bubbleActive = true;

        s_bubbleActive = true;
        s_bubbleCenter = transform.position;
        s_bubbleRadiusWorld = _bubbleRadiusWorld;

        if (!visuals) visuals = GetComponentInChildren<PhaseStarVisuals2D>(true);
        visuals?.ShowSafetyBubble(_bubbleRadiusWorld, bubbleTint, bubbleShardInnerTint);

        // IMPORTANT:
        // Safety bubble is now a gravity-void refuge zone only.
        // It does NOT freeze PhaseStar motion or root physics.
    }
    private void DeactivateSafetyBubble()
    {
        if (!_bubbleActive) return; // already off — no log spam

        Debug.Log($"[BUBBLE] DeactivateSafetyBubble star={name} frame={Time.frameCount}");
        _bubbleActive = false;

        s_bubbleActive = false;
        s_bubbleCenter = Vector2.zero;
        s_bubbleRadiusWorld = 0f;

        if (!visuals) visuals = GetComponentInChildren<PhaseStarVisuals2D>(true);
        visuals?.HideSafetyBubble();
    }
}
    



    