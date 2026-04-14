using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Random = UnityEngine.Random;
[System.Serializable]
public class PhaseRecord
{
    public string PhaseId;            // stable authored ID from PhaseLibrary
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
    public List<string>     UnlockedPhaseIds = new();
    public List<PhaseRecord> CompletedPhases    = new();

    // How many times a given phase has been completed (derived, but cached for perf)
    public int PlaythroughCountFor(string phaseId) =>
        CompletedPhases.Count(r => r.PhaseId == phaseId);
}


public enum PhaseStarState
{
    Dormant        = -1,    // on-screen but inert, waiting for colored dust
    WaitingForPoke   = 0,
    Completed        = 2,
    BridgeInProgress
}
public class PhaseStar : MonoBehaviour
{
    // -------------------- Serialized config --------------------
    [Header("Profiles & Prefs")]

    [SerializeField] private PhaseStarBehaviorProfile behaviorProfile;
    [SerializeField, Range(0f, 1f)] private float shardReadyThreshold = 0.5f;

    // -------------------- Profile-driven tuning (authoring surface) --------------------
    // PhaseStar no longer owns duplicated serialized fields for these knobs; they come from PhaseStarBehaviorProfile.
    // Defaults here are used only if behaviorProfile is missing.
    [SerializeField] private GameObject superNodePrefab;  // rainbow shard prefab (collider + visual)
    [SerializeField] private SoloVoice soloVoice;         // assign in inspector or find at runtime
    [Header("Off-Screen Entry")]
    [Tooltip("World units outside the screen edge where the star spawns.")]
    [SerializeField] private float entryOffscreenMargin = 2f;
    [Tooltip("How close to the screen interior edge (world units) before the star is " +
             "considered 'arrived' and arms itself.")]
    [SerializeField] private float entryArriveThreshold = 1.5f;
    [Tooltip("Seconds to fade visuals in once inside the screen boundary.")]
    [SerializeField, Min(0f)] private float entryFadeInSeconds = 0.6f;
    [Tooltip("Seconds to drift at reduced speed after crossing the screen boundary, before stopping.")]
    [SerializeField, Min(0f)] private float entryDriftSeconds = 2.0f;
    [Tooltip("Speed fraction (0-1) during the post-entry drift settle.")]
    [SerializeField, Range(0f, 1f)] private float entryDriftSpeedMul = 0.4f;
    [Tooltip("Minimum world-unit inset from the top screen edge after drift settles. " +
             "Must be > diamond half-height so no shard clips the viewport.")]
    [SerializeField, Min(0.5f)] private float entrySettleInset = 2.5f;
    [Tooltip("Accumulator rotation speed multiplier when charge is ready (diamonds merged → faster spin).")]
    [SerializeField, Min(1f)] private float readyRotSpeedMul = 2.5f;
    [Header("Charge Readiness")]
    private bool _cachedIsSuperNode = false;
    
    private bool SafetyBubbleEnabled => !behaviorProfile || behaviorProfile.enableSafetyBubble;
    private int SafetyBubbleRadiusCells => behaviorProfile ? Mathf.Max(0, behaviorProfile.safetyBubbleRadiusCells) : 4;

    private int CollectableClearTimeoutLoops => behaviorProfile ? behaviorProfile.collectableClearTimeoutLoops : 2;

    private float CollectableClearTimeoutSeconds =>
        behaviorProfile ? behaviorProfile.collectableClearTimeoutSeconds : 0f;
    
    private bool _previewInitialized;

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
    private Color _lockedTint;
    // Single accumulator shard — flat fields replace the old previewRing list.
    private Color       _previewColor;
    private MusicalRole _previewRole;
    private Transform   _previewVisual;
    private Transform   _previewVisualB;   // second counter-rotating diamond
    private bool        _wasChargeReady;   // edge-detect: false→true triggers drift resume
    private bool _isDisposing;
    private bool _entryInProgress;
    private bool _buildingPreview;
    private int _shardsEjectedCount; // how many shards have ejected so far

    private bool _awaitingLoopPhaseFinish;
    private int  _bridgeWaitStartLoop = -1;

    // Cached impact data for the next MineNode spawn
    Vector2 _lastImpactDir = Vector2.right;
    float _lastImpactStrength = 0f;

    // Optional clamp so crazy physics spikes don't blow things up
    const float MaxImpactStrength = 40f;
    [SerializeField, Min(0f)] private float disarmedPushScale = 0.6f;
    private bool _awaitingCollectableClear;

    // True while the star is parked off-screen during a collectable burst.
    // Cleared in OnBurstNotesReleased() when all burst notes have been committed.
    private bool _burstOffScreen;
    private Coroutine _burstOffScreenWaitCo;

    [SerializeField] private bool _tracePhaseStar = true;
    private float _loopDuration; // seconds (time authority)


    [SerializeField] private Color bubbleTint = new Color(1f, 1f, 1f, 1f); // fill/edge tint (alpha handled by visuals)
    [SerializeField] private Color bubbleShardInnerTint = new Color(0.05f, 0.05f, 0.05f, 0.9f);

    private bool _bubbleActive;
    private float _bubbleRadiusWorld;
    // World-space center of the active bubble; set at activation and held fixed (gravity void uses MineNode capture position).
    private Vector2 _bubbleCenterWorld;

// Static “global query” (simple + reliable for Vehicle)
    private static bool s_bubbleActive;
    private static Vector2 s_bubbleCenter;
    private static float s_bubbleRadiusWorld;
    private readonly Dictionary<MusicalRole, float> _starCharge = new();

    [Header("Shard Visuals (Charge-Alpha)")]
    [Tooltip("Minimum alpha for a shard with zero charge — keeps it ghost-visible.")]
    [SerializeField, Range(0f, 0.5f)] private float shardMinAlpha = 0.08f;



    private enum DisarmReason
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

    // Accumulator diamond rotation (deg, cumulative). Accumulator rotates at -accumulatorRotSpeed;
    // the scout rotates at +scoutRotSpeed in PhaseStarDustAffect. Opposite signs = opposite directions.
    [SerializeField, Range(10f, 360f)] private float accumulatorRotSpeed = 90f;
    private float _accumulatorRotAngle;
    [SerializeField] private float drainTiltDeg   = 5f;    // max tilt ± while draining
    [SerializeField] private float drainTiltSpeed  = 1.8f; // oscillation cycles/sec
    private float _accumulatorDrainTimer;

    [Header("Charge Display")]
    [Tooltip("Lerp speed for the visual charge fill (units/sec in 0-1 space). " +
             "Lower = slower, smoother rise from empty to full.")]
    [SerializeField, Min(0.1f)] private float chargeDisplayLerpSpeed = 2.5f;

    [Tooltip("Visual alpha threshold (0–1) at which the star starts moving and allows pokes. " +
             "Keeps locomotion and collision locked until the diamonds are visually at full opacity.")]
    [SerializeField, Range(0.8f, 1f)] private float readyDisplayThreshold = 0.99f;

    private float _displayedCharge01;

    private bool _ejectionInFlight;
    private bool _advanceStarted;
    private int _spawnTicket;
    private int _lastPokeFrame = -999999;
    private InstrumentTrack _cachedTrack;
    private MineNode _activeNode;
    private SuperNode _activeSuperNode;
    private readonly List<InstrumentTrack> _targets = new(4);
    private List<MusicalRole> _phasePlanRoles;
    [SerializeField] private MotifProfile _assignedMotif; // optional: motif this star represents (motif system)

    public event Action<PhaseStar> OnArmed;
    public event Action<PhaseStar> OnDisarmed;
    private bool _isArmed;
    private int _baseSortingOrder;

    public List<MusicalRole> GetMotifActiveRoles() =>
        _assignedMotif != null ? _assignedMotif.GetActiveRoles() : null;

    private int GetEffectiveNodesPerStar()
    {
        if (_assignedMotif != null)
        {
            // Each RoleMotifNoteSetConfig represents one bin/ejection.
            // Use whichever is larger so authored overrides can still increase the count,
            // but a mismatch never silently drops bins.
            int configCount = _assignedMotif.roleNoteConfigs?.Count ?? 0;
            int authored    = Mathf.Max(1, _assignedMotif.nodesPerStar);
            return Mathf.Max(configCount, authored);
        }
        return behaviorProfile != null ? behaviorProfile.nodesPerStar : 1;
    }

    private GameFlowManager gfm;


    // -------------------- Lifecycle --------------------
    void Start()
    {
        gfm = GameFlowManager.Instance;
        EnsurePreviewRing();
        if (!_buildingPreview)
        {
            InitializeTimingAndSpeeds();
        }
    }
    /// <summary>The role the star is currently accumulating charge for. Navigator uses this to target the right dust.</summary>
    public MusicalRole GetPreviewRole() => _previewRole;

    /// <summary>The primary (CW) diamond's Transform. Used by PhaseStarDustAffect to anchor
    /// the tentacle root to the diamond tip so it whips with the rotation.</summary>
    public Transform PrimaryDiamondTransform => _previewVisual;

    /// <summary>The secondary (CCW) diamond's Transform. Used by PhaseStarDustAffect for
    /// tentacle tips assigned to Diamond B (tipIndex 1 and 3).</summary>
    public Transform SecondaryDiamondTransform => _previewVisualB;

    // energyUnitsDelivered: raw energy units (or Charge01-fraction via legacy shim — both work).
    // No upper cap — _starCharge is now unbounded cumulative units.
    public void AddCharge(MusicalRole role, float energyUnitsDelivered)
    {
        if (role == MusicalRole.None) return;
        float add = Mathf.Max(0f, energyUnitsDelivered) * (behaviorProfile != null ? behaviorProfile.dustToStarChargeMul : 1f);
        if (add <= 0f) return;

        // [BALANCE-C] Diminishing returns: charge above fieldAvg * 1.5 accrues at half rate.
        float fieldAvg = (_starCharge.Count > 0) ? GetTotalCharge() / _starCharge.Count : 0f;
        _starCharge.TryGetValue(role, out float cur);
        if (cur > fieldAvg * 1.5f)
            add *= 0.5f;

        _starCharge[role] = cur + add;  // Unbounded — no Mathf.Min(1f) cap.
    }
    /// <summary>
    /// Returns 0..1 hunger for a specific role: 1 = starving (zero charge), 0 = fully charged.
    /// Used by the navigator to weight density steering toward under-charged roles.
    /// </summary>
    public float GetRoleHunger(MusicalRole role)
    {
        return 1f - GetChargeNormalized01(role);
    }

    /// <summary>True when the star already has enough charge to eject a MineNode of this role.</summary>
    public bool IsRoleReady(MusicalRole role)
    {
        if (role == MusicalRole.None) return false;
        _starCharge.TryGetValue(role, out float c);
        var rp = MusicalRoleProfileLibrary.GetProfile(role);
        float threshold = shardReadyThreshold * (rp != null ? rp.maxEnergyUnits : 1);
        return c >= threshold;
    }
    public static bool IsPointInsideSafetyBubble(Vector2 worldPos)
    {
        if (!s_bubbleActive) return false;
        return (worldPos - s_bubbleCenter).sqrMagnitude <= (s_bubbleRadiusWorld * s_bubbleRadiusWorld);
    }

    private bool IsChargeReady()
    {
        if (_previewRole == MusicalRole.None) return false;
        _starCharge.TryGetValue(_previewRole, out float c);
        var rp = MusicalRoleProfileLibrary.GetProfile(_previewRole);
        float threshold = shardReadyThreshold * (rp != null ? rp.maxEnergyUnits : 1);
        return c >= threshold;
    }
    public void EnterFromOffScreen(Vector2 targetWorldPos)
    {
        _entryInProgress = true;

        // Hide visuals and disable colliders until arrival.
        visuals?.HideAll();
        DisableColliders();

        // Ensure tentacles and armed state are off during entry and Dormant phase.
        // ArmNext fires in Initialize (before _entryInProgress is set), so we must
        // explicitly undo it here: tentacles must not run while the star is descending
        // or waiting for colored dust, or they drain all maze cells before the star can arm.
        dust?.SetTentaclesActive(false);
        _isArmed = false;
        _starCharge.Clear();
        _displayedCharge01 = 0f;

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

    // Always descend from the top edge.
    return new Vector2(Random.Range(min.x + 1f, max.x - 1f), max.y + margin);
}

    private IEnumerator Co_EntryApproach(Vector2 targetWorldPos)
{
    var cam = Camera.main;
    
    // ── Arrived ──────────────────────────────────────────────
    _entryInProgress = false;

    // Drift slowly for a moment before settling.
    motion?.SetSpeedMultiplier(entryDriftSpeedMul);
    visuals?.ShowDim(Color.gray);
    yield return new WaitForSeconds(entryDriftSeconds);

    // Clamp so no shard can remain at or above the viewport top after drift.
    motion?.ClampToScreenTop(entrySettleInset);
    // Drift slowly toward the grid center while waiting for the player to carve colored dust.
    motion?.SetOverrideTarget(ComputeDormantRestPosition());
    motion?.SetSpeedMultiplier(0.35f);
    cravingNavigator?.SetActive(false);

    // Re-enable colliders; enter dormant state until colored dust exists.
    EnableColliders();
    _state = PhaseStarState.Dormant;
    StartCoroutine(Co_WaitForColoredDust());
    LogState("EntryComplete+Dormant");
}

    private Vector2 ComputeDormantRestPosition()
    {
        var drum = _drum != null ? _drum : GameFlowManager.Instance?.activeDrumTrack;
        if (drum == null) return transform.position;
        int gw = drum.GetSpawnGridWidth();
        int gh = drum.GetSpawnGridHeight();
        return drum.GridToWorldPosition(new Vector2Int(gw / 2, gh / 2));
    }

    private IEnumerator Co_WaitForColoredDust()
    {
        // Always poll — re-fetch gen each iteration in case it isn't ready yet.
        // The original single-fetch bug: if gen was null at coroutine start,
        // "while (gen != null && ...)" exited immediately and the star armed prematurely.
        while (true)
        {
            yield return new WaitForSeconds(0.5f);
            var gen = GameFlowManager.Instance?.dustGenerator;
            if (gen != null && gen.HasAnyDustWithRole())
                break;
        }

        _state = PhaseStarState.WaitingForPoke;
        cravingNavigator?.SetActive(true);
        ArmNext();
        LogState("DormantWake+Armed");
    }

    private float GetTotalCharge()
    {
        float total = 0f;
        foreach (var kv in _starCharge)
            total += kv.Value;
        return total;
    }

    // Returns 0-1: how close a role's charge is to its ready threshold.
    // Used for visual lerps and hunger calculations. Normalizes against role's maxEnergyUnits.
    private float GetChargeNormalized01(MusicalRole role)
    {
        _starCharge.TryGetValue(role, out float c);
        var rp = MusicalRoleProfileLibrary.GetProfile(role);
        float threshold = shardReadyThreshold * (rp != null ? rp.maxEnergyUnits : 1);
        return Mathf.Clamp01(c / Mathf.Max(0.001f, threshold));
    }
    public bool GetDominantRoleRaw(out MusicalRole role, out float rawCharge, out float threshold)
    {
        role = MusicalRole.None;
        rawCharge = 0f;
        threshold = 1f;

        float bestCharge = 0f;
        foreach (var kv in _starCharge)
        {
            if (kv.Value > bestCharge)
            {
                bestCharge = kv.Value;
                role = kv.Key;
            }
        }

        if (role == MusicalRole.None)
            return false;

        rawCharge = bestCharge;
        var rp = MusicalRoleProfileLibrary.GetProfile(role);
        threshold = shardReadyThreshold * (rp != null ? rp.maxEnergyUnits : 1f);
        return true;
    }

    public bool HasDominantRoleEjectable()
    {
        return GetDominantRoleRaw(out _, out float rawCharge, out float threshold) &&
               rawCharge >= threshold;
    }


    public void Initialize(
        DrumTrack drum,
        IEnumerable<InstrumentTrack> targets,
        PhaseStarBehaviorProfile profile,
        MotifProfile motif = null)
    {
// Safe, null-tolerant log:
        var roleNames = targets?
            .Select(t => t == null ? "null" : t.assignedRole.ToString()) // <-- note the ()
            .ToArray() ?? Array.Empty<string>();

        Trace($"Initialize: received targets={roleNames.Length} :: {string.Join(", ", roleNames)}");
        
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

        // Clear charge state for this new star.
        _starCharge.Clear();
        _displayedCharge01 = 0f;

        _shardsEjectedCount = 0;

        BuildPhasePlan(GetEffectiveNodesPerStar());
        PrepareNextDirective();
        // ensure subcomponents are present if assigned
        if (!visuals) visuals = GetComponentInChildren<PhaseStarVisuals2D>(true);
        if (!motion) motion = GetComponentInChildren<PhaseStarMotion2D>(true);
        if (!dust) dust = GetComponentInChildren<PhaseStarDustAffect>(true);
        if (!cravingNavigator) cravingNavigator = GetComponentInChildren<PhaseStarCravingNavigator>(true);
        if (visuals) visuals.Initialize(behaviorProfile, this);
        if (motion) motion.Initialize(behaviorProfile, this);
        if (cravingNavigator) cravingNavigator.Initialize(this, motion, behaviorProfile);
        if (motion && cravingNavigator) motion.SetCravingNavigator(cravingNavigator);

        if (dust)
            dust.Initialize(behaviorProfile, this);
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
    private Color ResolvePreviewColorByReadiness()
    {
        if (_previewRole == MusicalRole.None) return Color.gray;

        float ready01 = GetChargeNormalized01(_previewRole);
        Color gray = Color.Lerp(Color.gray, Color.lightGray, 0.65f);
        Color c = Color.Lerp(gray, _previewColor, ready01);
        c.a = 1f;
        return c;
    }

    void Update()
{
    if (_bubbleActive)
    {
        s_bubbleCenter = _bubbleCenterWorld;
        visuals?.UpdateBubblePosition(_bubbleCenterWorld);
    }

    float dt = Time.deltaTime;

    _accumulatorRotAngle += accumulatorRotSpeed * dt;

    float rotA, rotB;
    bool aLocked = false, bLocked = false;

    if (dust != null)
    {
        dust.GetDiamondLockState(0, out aLocked, out float aLockDeg, out bool aDraining);
        dust.GetDiamondLockState(1, out bLocked, out float bLockDeg, out bool bDraining);

        if (aDraining || bDraining)
            _accumulatorDrainTimer += dt;
        else
            _accumulatorDrainTimer = 0f;

        float sway = Mathf.Sin(_accumulatorDrainTimer * drainTiltSpeed * Mathf.PI * 2f) * drainTiltDeg;

        rotA = aLocked ? aLockDeg + (aDraining ? sway : 0f) : _accumulatorRotAngle;
        rotB = bLocked ? bLockDeg + (bDraining ? sway : 0f) : -_accumulatorRotAngle;
    }
    else
    {
        rotA = _accumulatorRotAngle;
        rotB = -_accumulatorRotAngle;
    }

    // Passive decay first.
    float passiveDecay = behaviorProfile != null ? behaviorProfile.passiveChargeDecayPerSec : 0f;
    if (_starCharge.Count > 0 && passiveDecay > 0f)
    {
        float dec = passiveDecay * dt;
        var keys = _starCharge.Keys.ToList();
        for (int i = 0; i < keys.Count; i++)
        {
            var r = keys[i];
            _starCharge[r] = Mathf.Max(0f, _starCharge[r] - dec);
        }
    }

    // Dominant-role tracking after decay.
    if (GetDominantRoleRaw(out var dominantRole, out float dominantRawCharge, out float dominantThreshold))
    {
        if (dominantRole != _previewRole)
        {
            _previewRole = dominantRole;

            var rp = MusicalRoleProfileLibrary.GetProfile(dominantRole);
            _previewColor = rp != null
                ? new Color(rp.dustColors.baseColor.r,
                            rp.dustColors.baseColor.g,
                            rp.dustColors.baseColor.b,
                            1f)
                : Color.white;

            _cachedTrack = FindTrackByRole(dominantRole);
            visuals?.ResetDualDiamondVisualState();
        }

        float dominantCharge01 = Mathf.Clamp01(dominantRawCharge / Mathf.Max(0.001f, dominantThreshold));
        _displayedCharge01 = Mathf.Lerp(_displayedCharge01, dominantCharge01, dt * chargeDisplayLerpSpeed);
    }
    else
    {
        _previewRole = MusicalRole.None;
        _displayedCharge01 = Mathf.Lerp(_displayedCharge01, 0f, dt * chargeDisplayLerpSpeed);
    }

    bool dominantReady = HasDominantRoleEjectable();

    if (_previewVisual != null)
    {
        visuals?.UpdateDualDiamonds(
            _previewColor,
            _displayedCharge01,
            rotA, aLocked,
            rotB, bLocked,
            dominantReady,
            readyRotSpeedMul);
    }

    if (_isArmed &&
        !_burstOffScreen &&
        _disarmReason == DisarmReason.None &&
        GetTotalCharge() < 0.01f &&
        !(dust?.HasActiveTentacles ?? false))
    {
        var genCheck = GameFlowManager.Instance?.dustGenerator;
        if (genCheck != null && !genCheck.HasAnyDustWithRole())
        {
            Disarm(DisarmReason.None);
            motion?.SetOverrideTarget(ComputeDormantRestPosition());
            motion?.SetSpeedMultiplier(0.35f);
            cravingNavigator?.SetActive(false);
            _state = PhaseStarState.Dormant;
            StartCoroutine(Co_WaitForColoredDust());
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

    public void NotifyCollectableBurstCleared(bool hadNotes = true)
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
            $"CIF={cif} EP={ep} hadNotes={hadNotes}"
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

        // If the player committed zero notes for this burst, roll back the shard so they must retry.
        // This prevents bridging to the next motif when no notes were ever placed for this role.
        if (!hadNotes)
        {
            _shardsEjectedCount = Mathf.Max(0, _shardsEjectedCount - 1);
            // Clear loop-phase flags that may have been set on the final-shard ejection path.
            _awaitingLoopPhaseFinish = false;
            _bridgeWaitStartLoop = -1;
            Debug.Log($"[PS:BURST_CLEARED] hadNotes=false — rolling back shard. _shardsEjectedCount={_shardsEjectedCount}");
            RebuildPreviewRingForRemainingShards(keepCurrentIndex: false);
            PrepareNextDirective();
            OnBurstNotesReleased();
            return;
        }

        bool noShardsRemain = _shardsEjectedCount >= GetEffectiveNodesPerStar();

        // Final shard: defer bridge by one full loop after the note lands in the track.
        // _bridgeWaitStartLoop is intentionally left -1 here; it will be stamped at the
        // FIRST loop boundary where _awaitingLoopPhaseFinish is observed, ensuring the
        // wait is measured from a loop boundary rather than from collection time.
        if (noShardsRemain)
        {
            _awaitingLoopPhaseFinish = true;
            _bridgeWaitStartLoop = -1;
            Disarm(DisarmReason.AwaitBridge, _lockedTint);
            return;
        }

        // Shards remain: return from off-screen and re-arm when colored dust is available.
        OnBurstNotesReleased();
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

            // Prune destroyed + inactive objects so pooled/reused collectables
            // that have been returned to their pool don't keep the gate locked.
            if (t.spawnedCollectables != null)
                t.spawnedCollectables.RemoveAll(go => go == null || !go.activeInHierarchy);

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
    
    private void ArmNext()
    {
        if (_activeNode != null || _activeSuperNode != null) return;
        if (_awaitingCollectableClear) return;
        if (_burstOffScreen) return;

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

        DeactivateSafetyBubble();

        EnableColliders();
        dust?.SetTentaclesActive(true);

        motion?.SetOverrideTarget(null);
        motion?.SetSpeedMultiplier(1f);

        SetVisual(VisualMode.Bright, ResolvePreviewColorByReadiness());
        OnArmed?.Invoke(this);
    }
    // Teleport the star off-screen and freeze it for the duration of a burst.
    // Guarded — safe to call repeatedly; only executes on the first call per burst.
    private void MoveOffScreenForBurst()
    {
        if (_burstOffScreen) return;
        _burstOffScreen = true;

        Vector2 offPos = PickOffScreenSpawnPoint();
        transform.position = (Vector3)offPos + Vector3.forward * transform.position.z;
        motion?.Enable(false);
        visuals?.HideAll();
        Debug.Log($"[PhaseStar] Burst in flight — moved off-screen to {offPos}");
    }

    // Called when all burst notes have been committed to the loop (or discarded).
    // Checks for colored dust and re-enters the play area if any exists.
    private void OnBurstNotesReleased()
    {
        if (!_burstOffScreen)
        {
            ArmNext();
            return;
        }

        _burstOffScreen = false;
        if (_burstOffScreenWaitCo != null)
        {
            StopCoroutine(_burstOffScreenWaitCo);
            _burstOffScreenWaitCo = null;
        }

        // Zero all accumulated charge so the star must drain energy before it can
        // eject another MineNode after returning to the play area.
        _starCharge.Clear();
        _displayedCharge01 = 0f;

        var gen = GameFlowManager.Instance?.dustGenerator;
        bool hasDust = gen != null && gen.HasAnyDustWithRole();
        Debug.Log($"[PhaseStar] Burst notes released — charges reset, hasDust={hasDust}");

        if (hasDust)
            EnterFromOffScreen(transform.position);
        else
            _burstOffScreenWaitCo = StartCoroutine(Co_WaitForDustThenReturn());
    }

    // Polls off-screen until colored dust exists, then re-enters the play area.
    private IEnumerator Co_WaitForDustThenReturn()
    {
        while (true)
        {
            yield return new WaitForSeconds(0.5f);
            var gen = GameFlowManager.Instance?.dustGenerator;
            if (gen != null && gen.HasAnyDustWithRole()) break;
        }
        _burstOffScreenWaitCo = null;
        EnterFromOffScreen(transform.position);
    }

    private void Disarm(DisarmReason reason, Color? tintOverride = null)
    {
        _isArmed = false;
        _disarmReason = reason;
        Debug.Log($"[PhaseStar] Disarm reason={reason} star={name}");

        DisableColliders();

        // CollectablesInFlight: move off-screen for the burst duration.
        if (reason == DisarmReason.CollectablesInFlight)
            MoveOffScreenForBurst();

        // Scout should not be visible while disarmed.
        dust?.SetTentaclesActive(false);

        // Suppress the dim visual when the star is parked off-screen.
        // NodeResolving / AwaitBridge: star is hidden (MineNode is alive); use Hidden.
        // Also hide completely if a MineNode/SuperNode is still live, regardless of reason
        // (e.g. ExpansionPending fires on a loop boundary while a node is active).
        // All other reasons: show dim so the star is faintly visible while waiting.
        if (!_burstOffScreen)
        {
            bool nodeIsAlive = _activeNode != null || _activeSuperNode != null;
            bool hideCompletely = reason == DisarmReason.NodeResolving
                               || reason == DisarmReason.AwaitBridge
                               || nodeIsAlive;
            SetVisual(hideCompletely ? VisualMode.Hidden : VisualMode.Dim,
                      tintOverride ?? ResolvePreviewColorByReadiness());
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
        if (_activeSuperNode != null) return false;
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
            if (AnyCollectablesInFlightGlobal() && (_activeNode != null || _activeSuperNode != null || _ejectionInFlight))
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

            bool noShardsRemain = _shardsEjectedCount >= GetEffectiveNodesPerStar();
            int nps = GetEffectiveNodesPerStar();

            Debug.LogWarning(
                $"[PS:LB/RECOVERY] timedOut={timedOut} shardsEjected={_shardsEjectedCount}/{nps} " +
                $"previewRole={_previewRole} noShardsRemain={noShardsRemain} " +
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
                // Route through the loop-wait so the player hears at least one full loop
                // of the placed notes before the bridge fires, same as the normal path.
                Debug.Log($"[PS:LB] Begin Bridge (via loop-wait)");
                _awaitingLoopPhaseFinish = true;
                _bridgeWaitStartLoop    = -1;
                Disarm(DisarmReason.AwaitBridge, _lockedTint);
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
            if (_displayedCharge01 >= readyDisplayThreshold)
            {
                Debug.Log($"[PS:LB] EP true but star is ready — holding armed");
                return; // stay armed; player can poke as soon as expansion clears
            }
            Debug.Log($"[PS:LB] Any Expanding Global True");
            Disarm(DisarmReason.ExpansionPending, _lockedTint);
            return;
        }

        // ------------------------------------------------------------
        // 3) Deterministic bridge trigger (end-of-star)
        // ------------------------------------------------------------
        // If the star is complete, we should bridge on the next clean loop boundary
        // even if a specific latch flag was not set (prevents “stuck not bridging”).
        int _nps = GetEffectiveNodesPerStar();
        bool shardsComplete = _nps > 0 && _shardsEjectedCount >= _nps;
        bool noShardsRemain0 = (_previewRole == MusicalRole.None) ||
                               (_shardsEjectedCount >= _nps);
        // HARD BLOCK: if a MineNode or SuperNode is still active, we cannot advance.
        // This enforces "no skip capture" as a player choice.
        if (_activeNode != null || _activeSuperNode != null || _ejectionInFlight)
        {
            DBG(
                $"LoopBoundary: block bridge; outstanding node. activeNode={_activeNode?.name} superNode={_activeSuperNode?.name} ejectionInFlight={_ejectionInFlight}");
            return;
        }

        // ------------------------------------------------------------
        // 3a) Loop-wait bridge gate (checked before FORCE_BRIDGE so once the flag
        //     is set it is always processed without FORCE_BRIDGE re-evaluating it)
        // ------------------------------------------------------------
        if (_awaitingLoopPhaseFinish)
        {
            var drumsLB = _drum ?? GameFlowManager.Instance?.activeDrumTrack;

            // First boundary: stamp so the wait is measured from a loop boundary,
            // not from the mid-loop moment the note was placed.
            if (_bridgeWaitStartLoop < 0 && drumsLB != null)
            {
                _bridgeWaitStartLoop = drumsLB.completedLoops;
                Disarm(DisarmReason.AwaitBridge, _lockedTint);
                return;
            }

            // Wait one full loop after the stamp.
            if (_bridgeWaitStartLoop >= 0 && drumsLB != null
                && drumsLB.completedLoops - _bridgeWaitStartLoop < 1)
            {
                Disarm(DisarmReason.AwaitBridge, _lockedTint);
                return;
            }

            if (!CanAdvancePhaseNow())
            {
                DBG($"[PS:LB] AwaitLoopPhaseFinish blocked; activeNode={_activeNode?.name} ejection={_ejectionInFlight}");
                Disarm(DisarmReason.NodeResolving, _lockedTint);
                return;
            }

            DBG("[PS:LB] Await Loop Phase Finish → fire bridge");
            _advanceStarted          = true;
            _awaitingLoopPhaseFinish = false;
            _bridgeWaitStartLoop     = -1;
            _state = PhaseStarState.BridgeInProgress;
            Disarm(DisarmReason.Bridge, _lockedTint);
            Trace("LoopBoundary → Begin bridge");
            StartCoroutine(CompleteAndAdvanceAsync());
            return;
        }

        // ------------------------------------------------------------
        // 3b) FORCE_BRIDGE safety net: star complete but flag not yet set
        //     (e.g. manual-release path where NotifyCollectableBurstCleared
        //      didn't fire). Arms the loop-wait for one more boundary pass.
        // ------------------------------------------------------------
        if (!_advanceStarted && _state != PhaseStarState.BridgeInProgress && !HasShardsRemaining() && noShardsRemain0
            && _activeNode == null && _activeSuperNode == null && !_ejectionInFlight
            && !_awaitingCollectableClear   // don't bridge while collectables are still outstanding
            && !AnyExpansionPendingGlobal())
        {
            Debug.LogWarning(
                $"[PS:LB] FORCE_BRIDGE shardsEjected={_shardsEjectedCount}/{behaviorProfile.nodesPerStar} " +
                $"previewRole={_previewRole} armed={_isArmed} state={_state} " +
                $"awaitClr={_awaitingCollectableClear} awaitLoopFinish={_awaitingLoopPhaseFinish}"
            );
            _awaitingLoopPhaseFinish = true;
            _bridgeWaitStartLoop     = -1;
            Disarm(DisarmReason.AwaitBridge, _lockedTint);
            return;
        }

        LogState("LoopBoundary entry");

        // ------------------------------------------------------------
        // 3c) Bridge already in progress
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
                DBG($"[PS:LB] BridgeInProgress blocked; activeNode={_activeNode?.name} ejection={_ejectionInFlight}");
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

        // ------------------------------------------------------------
        // 4) Normal re-arm path
        // ------------------------------------------------------------
        if (!_isArmed)
        {
            // If the plan is fully completed, stay quiet and let the bridge path take over.
            if (_shardsEjectedCount >= GetEffectiveNodesPerStar() && GetEffectiveNodesPerStar() > 0)
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
                  $"shards={_shardsEjectedCount}/{behaviorProfile?.nodesPerStar} preview={(_previewVisual != null ? 1 : 0)} " +
                  $"activeNode={(_activeNode != null ? _activeNode.name : "null")} lockedTint={_lockedTint}");
    }
    
    void BuildOrRefreshPreviewRing()
    {
        if (_previewVisual != null)
        {
            _previewVisual.localRotation = Quaternion.identity;
            var sr = _previewVisual.GetComponent<SpriteRenderer>();
            if (sr) { var c = sr.color; c.a = shardMinAlpha; sr.color = c; }
        }
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
    }

    private void OnDestroy()
    {
        _isDisposing = true;
        SafeUnsubscribeAll();
    }

    private void WireBinSource(DrumTrack drum)
    {
        _drum = drum;
        if (_drum == null) return;
        InitializeTimingAndSpeeds();
        BuildOrRefreshPreviewRing();
    }
    
    private void BuildPhasePlan(int shardCount)
    {
        _phasePlanRoles = new List<MusicalRole>();

        var activeRoles = _assignedMotif?.GetActiveRoles();
        if (activeRoles == null || activeRoles.Count == 0)
            activeRoles = new List<MusicalRole> { MusicalRole.Bass, MusicalRole.Harmony, MusicalRole.Lead, MusicalRole.Groove };

        int target = Mathf.Max(1, shardCount);
        for (int i = 0; i < target; i++)
            _phasePlanRoles.Add(activeRoles[(_shardsEjectedCount + i) % activeRoles.Count]);

    }

    private bool HasShardsRemaining() => _shardsEjectedCount < GetEffectiveNodesPerStar();

    private int GetRemainingShardCount()
    {
        int total = Mathf.Max(0, GetEffectiveNodesPerStar());
        int rem = total - Mathf.Max(0, _shardsEjectedCount);
        return Mathf.Clamp(rem, 0, total);
    }

    private void RebuildPreviewRingForRemainingShards(bool keepCurrentIndex = true)
    {
        if (behaviorProfile == null || visuals == null) return;

        if (_previewVisual != null)
        {
            // Detach from hierarchy before destroying so GetComponentsInChildren
            // can never find this pending-destroy GO in the same frame.
            _previewVisual.SetParent(null);
            Destroy(_previewVisual.gameObject);
            _previewVisual = null;
        }

        int remaining = GetRemainingShardCount();
        if (remaining <= 0) return;

        BuildPhasePlan(remaining);
        BuildPreviewRing();
    }
    private void PrepareNextDirective()
    {
        Trace("PrepareNextDirective() begin");

        _cachedTrack = null;
        _cachedIsSuperNode = false;
        
        if (_drum == null) return;

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
            if (factory != null)
                planned = factory.Generate(track, _assignedMotif);
        }
        
        // SuperNode only when the track is fully expanded AND repeating the same NoteSet adds no new coverage.
        _cachedIsSuperNode = (planned != null && _cachedTrack != null) && ShouldSpawnSuperNodeForTrack(_cachedTrack);
    }
    void BuildPreviewRing()
{
    _buildingPreview = true;

    if (_previewVisual != null)
    {
        _previewVisual.SetParent(null);
        Destroy(_previewVisual.gameObject);
        _previewVisual = null;
    }

    if (_previewVisualB != null)
    {
        _previewVisualB.SetParent(null);
        Destroy(_previewVisualB.gameObject);
        _previewVisualB = null;
    }

    _wasChargeReady = false;

    if (_baseSortingOrder == 0)
    {
        var baseSr = GetComponentInChildren<SpriteRenderer>(true);
        _baseSortingOrder = baseSr ? baseSr.sortingOrder : 2000;
    }

    if (_phasePlanRoles == null || _phasePlanRoles.Count == 0 || visuals == null)
    {
        _buildingPreview = false;
        visuals?.InvalidateShardCache();
        return;
    }

    // BuildPreviewRing is now VISUAL ONLY.
    // It should not mutate hidden-role promotion in CosmicDustGenerator.
    // The active ecology must come from motif setup + carving/regrowth, not from preview state.
    var role = _phasePlanRoles[0];
    var track = FindTrackByRole(role);

    Color roleColor;
    var roleProfile = MusicalRoleProfileLibrary.GetProfile(role);
    if (roleProfile != null)
    {
        roleColor = new Color(
            roleProfile.dustColors.baseColor.r,
            roleProfile.dustColors.baseColor.g,
            roleProfile.dustColors.baseColor.b,
            1f);
    }
    else if (track != null)
    {
        roleColor = new Color(
            track.trackColor.r,
            track.trackColor.g,
            track.trackColor.b,
            1f);
    }
    else
    {
        roleColor = Color.white;
    }

    // Seed preview values for the initial visual state only.
    // Runtime dominant-role logic in Update() is still allowed to replace these later.
    _previewRole = role;
    _previewColor = roleColor;

    var go = new GameObject($"PreviewShard_0_{role}");
    go.transform.SetParent(transform);
    go.transform.localPosition = Vector3.zero;
    go.transform.localRotation = Quaternion.identity;
    go.transform.localScale = Vector3.one;

    var sr = go.AddComponent<SpriteRenderer>();
    sr.sprite = visuals.diamond;
    sr.color = new Color(0.45f, 0.45f, 0.45f, shardMinAlpha);
    sr.sortingOrder = _baseSortingOrder;

    if (!_isArmed && (_disarmReason == DisarmReason.NodeResolving || _disarmReason == DisarmReason.AwaitBridge))
        sr.enabled = false;

    _previewVisual = go.transform;

    var goB = new GameObject($"PreviewShardB_0_{role}");
    goB.transform.SetParent(transform);
    goB.transform.localPosition = Vector3.zero;
    goB.transform.localRotation = Quaternion.identity;
    goB.transform.localScale = Vector3.one;

    var srB = goB.AddComponent<SpriteRenderer>();
    srB.sprite = visuals.diamond;
    srB.color = new Color(0.45f, 0.45f, 0.45f, shardMinAlpha);
    srB.sortingOrder = _baseSortingOrder;

    if (!_isArmed && (_disarmReason == DisarmReason.NodeResolving || _disarmReason == DisarmReason.AwaitBridge))
        srB.enabled = false;

    _previewVisualB = goB.transform;

    _buildingPreview = false;

    visuals?.InvalidateShardCache();
    visuals?.BindDualDiamondRenderers(_previewVisual, _previewVisualB);
    visuals?.ResetDualDiamondVisualState();
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
        return _previewRole != MusicalRole.None ? _previewRole
            : (_phasePlanRoles != null && _phasePlanRoles.Count > 0 ? _phasePlanRoles[0] : MusicalRole.Bass);
    }
    public void SetGravityVoidSafetyBubbleActive(bool active, Vector3 center = default)
    {
        Debug.Log($"[BUBBLE] SetGravityVoidSafetyBubbleActive active={active} star={name} frame={Time.frameCount}");
        if (active) ActivateSafetyBubble(center);
        else DeactivateSafetyBubble();
    }

    public int GetSafetyBubbleRadiusCells() => SafetyBubbleRadiusCells;

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
            Trace("OnCollisionEnter2D: expansion pending — ignoring poke, star stays armed");
            return; // don't disarm; player can retry once expansion clears
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
        if (!HasDominantRoleEjectable())
        {
            Trace("OnCollisionEnter2D: ignored poke — dominant role not ejectable yet");
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
        if (_previewRole != MusicalRole.None)
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
        visuals?.EjectParticles(behaviorProfile?.ejectionPrefab);

        Color spawnTint = _previewVisual != null ? _previewColor : usedTrack.trackColor;
        _lockedTint = spawnTint;

        var node = DirectSpawnMineNode(contactPoint, usedTrack, spawnTint);
        if (node == null)
        {
            _ejectionInFlight = false;
            _activeNode = null;
            Disarm(DisarmReason.NodeResolving, spawnTint);
            return;
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
            // Non-final shard: move off-screen immediately so the star isn't visible
            // while collectables are in flight or being released by the vehicle.
            // Final shard: stay hidden via AwaitBridge — bridge sequence owns visibility.
            if (HasShardsRemaining())
            {
                MoveOffScreenForBurst();
                Disarm(DisarmReason.NodeResolving, spawnTint);
            }
            else
            {
                Disarm(DisarmReason.AwaitBridge, spawnTint);
            }
            LogState("OnResolved");
        };

        CollectionSoundManager.Instance?.PlayPhaseStarImpact(usedTrack, usedTrack.GetCurrentNoteSet(), 0.8f);
        PrepareNextDirective();
        Trace("SpawnNodeCommon: end");
    }

    void EjectActivePreviewShardAndFlow(Collision2D coll)
    {
        if (behaviorProfile == null || visuals == null) return;
        if (!HasShardsRemaining()) return;

        if (!GetDominantRoleRaw(out MusicalRole ejectedRole, out float rawCharge, out float threshold))
            return;

        if (rawCharge < threshold)
            return;

        InstrumentTrack ejectedTrack = FindTrackByRole(ejectedRole);
        if (ejectedTrack == null)
        {
            Debug.LogError($"[PhaseStar] Missing track for ejected role={ejectedRole} (cannot spawn node).");
            return;
        }

        // Consume only the dominant role’s charge for the ejection.
        _starCharge[ejectedRole] = Mathf.Max(0f, rawCharge - threshold);
        _displayedCharge01 = 0f;
        var contact = coll.GetContact(0).point;
        var starPos = (Vector2)transform.position;
        var vehiclePos = coll.rigidbody != null ? coll.rigidbody.position : contact;

        Vector2 incoming = (starPos - vehiclePos);
        _lastImpactDir = (incoming.sqrMagnitude > 0.0001f) ? incoming.normalized : Vector2.right;
        _lastImpactStrength = Mathf.Clamp(coll.relativeVelocity.magnitude, 0f, MaxImpactStrength);

        _shardsEjectedCount++;
        int remainingAfter = GetRemainingShardCount();
        bool isFinalShardEjection = (remainingAfter <= 0);

        // Hide PhaseStar immediately — the MineNode IS the diamond visually.
        // Zero frames of overlap between the diamond and the freshly-ejected node.
        visuals?.HideAll();
        dust?.ResetTentacles();

        Disarm(isFinalShardEjection ? DisarmReason.AwaitBridge : DisarmReason.NodeResolving,
            ejectedTrack.trackColor);

        Debug.Log($"[MNDBG] EjectActive: contact={contact}, role={ejectedTrack.assignedRole}");
        if (ShouldSpawnSuperNodeForTrack(ejectedTrack))
            SpawnSuperNodeCommon(contact, ejectedTrack);
        else
            SpawnNodeCommon(contact, ejectedTrack);

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
        if (_previewRole == MusicalRole.None) return 1f;
        return 1f - GetChargeNormalized01(_previewRole);
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

        // Initialize component
        var sn = go.GetComponent<SuperNode>();
        if (sn == null)
        {
            Debug.LogError("[PhaseStar] SuperNode prefab missing SuperNode component.");
            return;
        }
        sn.Initialize(soloVoice, _drum, targetTrack);

        Color spawnTint = targetTrack != null ? targetTrack.trackColor : Color.white;
        _activeSuperNode = sn;
        sn.OnResolved += () =>
        {
            _activeSuperNode = null;
            if (_state == PhaseStarState.BridgeInProgress || _advanceStarted) return;
            _awaitingCollectableClear = true;
            _awaitingCollectableClearSinceLoop = (_drum != null)
                ? _drum.completedLoops
                : (GameFlowManager.Instance?.activeDrumTrack?.completedLoops ?? -1);
            _awaitingCollectableClearSinceDsp = AudioSettings.dspTime;
            if (HasShardsRemaining())
            {
                MoveOffScreenForBurst();
                Disarm(DisarmReason.NodeResolving, spawnTint);
            }
            else
            {
                Disarm(DisarmReason.AwaitBridge, spawnTint);
            }
            LogState("OnSuperNodeResolved");
        };

        PrepareNextDirective();
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

        bool isFinal = (_shardsEjectedCount >= GetEffectiveNodesPerStar());
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
 Debug.Log($"[MineNode] Initializing track {track.name} with {track.assignedRole}");
        node.Initialize(track, noteSet, color, cell, diamondSprite: visuals?.diamond);
        return node;
    }
    private int CurrentEntropyForSelection() {
        return 0;
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

        GameFlowManager.Instance?.BeginMotifBridge("PhaseStar/CompleteAdvanceAsync");

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
        string targRole = _previewRole != MusicalRole.None ? _previewRole.ToString() : "-";

    }

    private void ActivateSafetyBubble(Vector3 center = default)
    {
        if (!SafetyBubbleEnabled) return;
        Debug.Log($"[BUBBLE] ActivateSafetyBubble star={name} frame={Time.frameCount}");

        var drumsForBubble = _drum != null ? _drum : GameFlowManager.Instance?.activeDrumTrack;
        float cell = drumsForBubble != null ? drumsForBubble.GetCellWorldSize() : 1f;

        // +0.5f gives the bubble a little breathing room relative to discrete cells
        _bubbleRadiusWorld = (SafetyBubbleRadiusCells + 0.5f) * cell;

        _bubbleActive = true;

        // Use provided center (e.g. MineNode capture position for gravity void),
        // falling back to star position for non-void calls.
        _bubbleCenterWorld = (center != default) ? (Vector2)center : (Vector2)transform.position;

        s_bubbleActive = true;
        s_bubbleCenter = _bubbleCenterWorld;
        s_bubbleRadiusWorld = _bubbleRadiusWorld;

        if (!visuals) visuals = GetComponentInChildren<PhaseStarVisuals2D>(true);
        visuals?.ShowSafetyBubble(_bubbleRadiusWorld, bubbleTint, bubbleShardInnerTint, _bubbleCenterWorld);

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
    



    