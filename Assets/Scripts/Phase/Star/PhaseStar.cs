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
             "considered 'arrived' and transitions into its dormant on-screen wait.")]
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
    private bool _isDisposing;
    private bool _entryInProgress;
    private bool _buildingPreview;
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
    private Coroutine _entryApproachCo;
    private Coroutine _waitForDustCo;

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
        Bridge,

        // A sibling Star has ejected and its MineNode is being processed.
        // Star freezes in place, goes gray, disables collision.
        SiblingActive
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
    private int _spawnTicket;
    private float _ejectableTimer;
    private int _lastPokeFrame = -999999;
    private InstrumentTrack _cachedTrack;
    private MineNode _activeNode;
    private SuperNode _activeSuperNode;
    private readonly List<InstrumentTrack> _targets = new(4);
    private MusicalRole _attunedRole = MusicalRole.None;
    public MusicalRole AttunedRole => _attunedRole;
    public bool LastNodeWasSuperNode { get; private set; }
    public bool LastNodeWasExpired   { get; private set; }
    [SerializeField] private MotifProfile _assignedMotif; // optional: motif this star represents (motif system)

    public event Action<PhaseStar> OnArmed;
    public event Action<PhaseStar> OnDisarmed;
    public event Action<PhaseStar, MusicalRole> OnEjected;
    public event Action<PhaseStar> OnBurstRolledBack;
    // Fired when the Vehicle destroys the MineNode/SuperNode — the burst is now spawning.
    // Safe to fire from a destroyed star (C# delegate, not Unity message).
    public event Action<PhaseStar, MusicalRole> OnMineNodeResolved;
    private bool _isArmed;
    private int _baseSortingOrder;

    public List<MusicalRole> GetMotifActiveRoles() =>
        _assignedMotif != null ? _assignedMotif.GetActiveRoles() : null;

    // -------------------- Lifecycle --------------------
    void Start()
    {
        EnsurePreviewRing();
        if (!_buildingPreview)
        {
            RefreshLoopDuration();
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
        if (_attunedRole != MusicalRole.None && role != _attunedRole) return;
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

    public bool IsPointInsideMyBubble(Vector2 worldPos)
    {
        return _bubbleActive && (worldPos - _bubbleCenterWorld).sqrMagnitude <= _bubbleRadiusWorld * _bubbleRadiusWorld;
    }

    public void EnterFromOffScreen(Vector2 targetWorldPos)
    {
        EnsureSubcomponents();
        StopManagedCoroutine(ref _entryApproachCo);
        StopManagedCoroutine(ref _waitForDustCo);

        _entryInProgress = true;
        _state = PhaseStarState.Dormant;

        // Hide visuals and disable colliders until arrival.
        visuals?.HideAll();
        DisableColliders();

        // Ensure tentacles and armed state are off during entry and Dormant phase.
        dust?.SetTentaclesActive(false);
        cravingNavigator?.SetActive(false);
        _isArmed = false;
        _disarmReason = DisarmReason.None;
        _burstOffScreen = false;
        _awaitingCollectableClear = false;
        _ejectableTimer = 0f;
        _starCharge.Clear();
        _displayedCharge01 = 0f;

        Vector2 offPos = PickOffScreenSpawnPoint();
        transform.position = (Vector3)offPos + Vector3.forward * transform.position.z;

        if (motion != null)
        {
            motion.Enable(true);
            motion.SetSpeedMultiplier(1f);
            motion.SetOverrideTarget(targetWorldPos);
        }

        _entryApproachCo = StartCoroutine(Co_EntryApproach(targetWorldPos));
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
        visuals?.ShowDim(Color.gray);

        float arriveThresholdSq = entryArriveThreshold * entryArriveThreshold;
        float failSafeUntil = Time.time + 8f;

        while (Time.time < failSafeUntil)
        {
            Vector2 delta = targetWorldPos - (Vector2)transform.position;
            if (delta.sqrMagnitude <= arriveThresholdSq)
                break;
            yield return null;
        }

        if (entryFadeInSeconds > 0f)
            yield return new WaitForSeconds(entryFadeInSeconds);

        motion?.SetSpeedMultiplier(entryDriftSpeedMul);
        motion?.SetOverrideTarget(ComputeDormantRestPosition());

        if (entryDriftSeconds > 0f)
            yield return new WaitForSeconds(entryDriftSeconds);

        motion?.ClampToScreenTop(entrySettleInset);

        _entryInProgress = false;
        _entryApproachCo = null;

        EnterDormantWaitState();
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

    private void EnterDormantWaitState()
    {
        StopManagedCoroutine(ref _entryApproachCo);

        _entryInProgress = false;
        _state = PhaseStarState.Dormant;
        _isArmed = false;
        _disarmReason = DisarmReason.None;

        visuals?.ShowDim(Color.gray);
        EnableColliders();
        dust?.SetTentaclesActive(false);
        cravingNavigator?.SetActive(false);
        EnterDormantMotionPose();

        if (_waitForDustCo == null)
            _waitForDustCo = StartCoroutine(Co_WaitForColoredDust());
    }

    private void TransitionDormantToActive()
    {
        if (_entryInProgress || _state != PhaseStarState.Dormant)
            return;

        StopManagedCoroutine(ref _waitForDustCo);
        _state = PhaseStarState.WaitingForPoke;
        cravingNavigator?.SetActive(true);
        ArmNext();
        LogState("DormantWake+Armed");
    }

    private IEnumerator Co_WaitForColoredDust()
    {
        while (true)
        {
            yield return new WaitForSeconds(0.5f);
            if (HasColoredDustAvailable())
                break;
        }

        _waitForDustCo = null;
        TransitionDormantToActive();
    }

    private bool HasColoredDustAvailable()
    {
        var gen = GameFlowManager.Instance?.dustGenerator;
        return gen != null && gen.HasAnyDustWithRole();
    }

    private void StopManagedCoroutine(ref Coroutine co)
    {
        if (co == null) return;
        StopCoroutine(co);
        co = null;
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


    private void EnsureSubcomponents()
    {
        if (!visuals) visuals = GetComponentInChildren<PhaseStarVisuals2D>(true);
        if (!motion) motion = GetComponentInChildren<PhaseStarMotion2D>(true);
        if (!dust) dust = GetComponentInChildren<PhaseStarDustAffect>(true);
        if (!cravingNavigator) cravingNavigator = GetComponentInChildren<PhaseStarCravingNavigator>(true);
    }

    private void CleanupManagedCoroutines()
    {
        StopManagedCoroutine(ref _entryApproachCo);
        StopManagedCoroutine(ref _waitForDustCo);
        StopManagedCoroutine(ref _burstOffScreenWaitCo);
    }

    private void EnterDormantMotionPose()
    {
        motion?.SetOverrideTarget(ComputeDormantRestPosition());
        motion?.SetSpeedMultiplier(0.35f);
    }

    private Color ResolveRoleColor(MusicalRole role, InstrumentTrack fallbackTrack = null)
    {
        var roleProfile = MusicalRoleProfileLibrary.GetProfile(role);
        if (roleProfile != null)
        {
            return new Color(
                roleProfile.dustColors.baseColor.r,
                roleProfile.dustColors.baseColor.g,
                roleProfile.dustColors.baseColor.b,
                1f);
        }

        if (fallbackTrack != null)
        {
            return new Color(
                fallbackTrack.trackColor.r,
                fallbackTrack.trackColor.g,
                fallbackTrack.trackColor.b,
                1f);
        }

        return Color.white;
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

        EnsureSubcomponents();
        if (visuals) visuals.Initialize(behaviorProfile, this);
        if (motion) motion.Initialize(behaviorProfile, this);
        if (cravingNavigator) cravingNavigator.Initialize(this, motion, behaviorProfile);
        if (motion && cravingNavigator) motion.SetCravingNavigator(cravingNavigator);

        if (dust)
        {
            dust.Initialize(behaviorProfile, this);
            dust.OnAttuned += OnAttuned_SetRole;
        }
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
    private void OnAttuned_SetRole(MusicalRole role)
    {
        if (_attunedRole != MusicalRole.None) return;
        _attunedRole = role;
        cravingNavigator?.SetAttunedRole(role);
        dust?.SetAttunedRole(role);
        Debug.Log($"[PhaseStar] Attuned to {role}");
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

            _previewColor = ResolveRoleColor(dominantRole);

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

    // Armed timeout: if fully charged but not ejected, reset and restart the drain cycle.
    if (dominantReady && _isArmed && _disarmReason == DisarmReason.None && !_burstOffScreen)
    {
        _ejectableTimer += dt;
        float timeout = behaviorProfile != null ? behaviorProfile.armedTimeoutSeconds : 0f;
        if (timeout > 0f && _ejectableTimer >= timeout)
        {
            _ejectableTimer = 0f;
            _starCharge.Clear();
            _displayedCharge01 = 0f;
            Debug.Log($"[PhaseStar] Armed timeout — resetting charge and re-entering dormant cycle.");
            EnterDormantWaitState();
        }
    }
    else
    {
        _ejectableTimer = 0f;
    }

    if (_previewVisual != null && _disarmReason != DisarmReason.SiblingActive)
    {
        visuals?.UpdateDualDiamonds(
            _previewColor,
            _displayedCharge01,
            rotA, aLocked,
            rotB, bLocked,
            dominantReady,
            readyRotSpeedMul);
    }

    // Continuously lerp body color between dim and role color using charge level.
    if (visuals != null && !_burstOffScreen && _disarmReason != DisarmReason.SiblingActive)
    {
        Color bodyColor = _previewRole != MusicalRole.None ? _previewColor : Color.gray;
        visuals.LerpBodyColor(bodyColor, _displayedCharge01);
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
            cravingNavigator?.SetActive(false);
            EnterDormantWaitState();
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
        if (_state == PhaseStarState.BridgeInProgress)
        {
            _awaitingCollectableClear = false;
            _awaitingCollectableClearSinceLoop = -1;
            _awaitingCollectableClearSinceDsp = -1.0;
            return;
        }

        Debug.Log(
            $"[PS:BURST_CLEARED] star={name} state={_state} armed={_isArmed} disarm={_disarmReason} " +
            $"awaitClr(before)={_awaitingCollectableClear} CIF={AnyCollectablesInFlightGlobal()} hadNotes={hadNotes}"
        );

        _awaitingCollectableClear = false;
        _awaitingCollectableClearSinceLoop = -1;
        _awaitingCollectableClearSinceDsp = -1.0;

        if (AnyCollectablesInFlightGlobal() || AnyExpansionPendingGlobal())
        {
            Debug.LogWarning($"[PS:BURST_CLEARED] IGNORE (still busy) star={name}");
            return;
        }

        if (!hadNotes)
        {
            Debug.Log($"[PS:BURST_CLEARED] hadNotes=false — rolling back.");
            OnBurstRolledBack?.Invoke(this);
            OnBurstNotesReleased();
            return;
        }

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
        if (_state != PhaseStarState.WaitingForPoke) return;
        if (_entryInProgress) return;
        if (_waitForDustCo != null) return;
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
    public void Pause()
    {
        Disarm(DisarmReason.SiblingActive);
        motion?.SetFrozen(true);
        visuals?.ShowDim(Color.gray);
    }

    public void Resume()
    {
        if (_isDisposing) return;
        _disarmReason = DisarmReason.None;
        if (_burstOffScreen)
        {
            _burstOffScreen = false;
            EnterFromOffScreen(ComputeDormantRestPosition());
            return;
        }
        motion?.SetFrozen(false);
        motion?.Enable(true);
        if (HasDominantRoleEjectable())
            ArmNext();
        else
            visuals?.ShowDim(ResolvePreviewColorByReadiness());
    }

    public void PreAttuneTo(MusicalRole role)
    {
        if (role == MusicalRole.None || _attunedRole != MusicalRole.None) return;
        OnAttuned_SetRole(role);
    }

    // Teleport the star off-screen and freeze it for the duration of a burst.
    // Guarded — safe to call repeatedly; only executes on the first call per burst.
    private void MoveOffScreenForBurst()
    {
        if (_burstOffScreen) return;

        StopManagedCoroutine(ref _entryApproachCo);
        StopManagedCoroutine(ref _waitForDustCo);

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

        bool hasDust = HasColoredDustAvailable();
        Debug.Log($"[PhaseStar] Burst notes released — charges reset, hasDust={hasDust}");

        Vector2 returnTarget = ComputeDormantRestPosition();
        if (hasDust)
            EnterFromOffScreen(returnTarget);
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
        EnterFromOffScreen(ComputeDormantRestPosition());
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
    private void RefreshLoopDuration()
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
            "$[PS:LB] star={name} state={_state} armed={_isArmed} disarm={_disarmReason} " +
            $"awaitClr={_awaitingCollectableClear} " +
            $"activeNode={(_activeNode ? _activeNode.name : null)} ejectInFlight={_ejectionInFlight} CIF={cif} EP={ep}");

        if (_isDisposing || this == null) return;
        if (_disarmReason == DisarmReason.SiblingActive) return;

        // ------------------------------------------------------------
        // 1) Awaiting collectable clear (post-node-resolution latch)
        // ------------------------------------------------------------
        if (_awaitingCollectableClear)
        {
            if (AnyCollectablesInFlightGlobal() && (_activeNode != null || _activeSuperNode != null || _ejectionInFlight))
            {
                Debug.Log("[PS:LB/AWAIT] -> stay disarmed (awaitClr + CIF + active node)");
                Disarm(DisarmReason.NodeResolving, _lockedTint);
                return;
            }

            var drums = _drum != null
                ? _drum
                : (GameFlowManager.Instance != null ? GameFlowManager.Instance.activeDrumTrack : null);

            bool timedOut = false;

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

            if (!timedOut && CollectableClearTimeoutSeconds > 0f)
            {
                double nowDsp = AudioSettings.dspTime;
                if (_awaitingCollectableClearSinceDsp < 0.0)
                    _awaitingCollectableClearSinceDsp = nowDsp;

                if ((nowDsp - _awaitingCollectableClearSinceDsp) >= CollectableClearTimeoutSeconds)
                    timedOut = true;
            }

            if (!timedOut)
            {
                Debug.Log($"[PS:LB/AWAIT] -> continue waiting (not timed out)");
                Disarm(DisarmReason.NodeResolving, _lockedTint);
                return;
            }

            Debug.LogWarning($"[PhaseStar][Timeout] AwaitingCollectableClear timed out. Forcing recovery. star={name}");

            _awaitingCollectableClear = false;
            _awaitingCollectableClearSinceLoop = -1;
            _awaitingCollectableClearSinceDsp = -1.0;

            if (!CanAdvancePhaseNow())
            {
                Disarm(DisarmReason.NodeResolving, _lockedTint);
                return;
            }

            Debug.Log($"[PS:LB] Recovery -> Dormant wait");
            EnterDormantWaitState();
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
                return;
            }
            Debug.Log($"[PS:LB] Any Expanding Global True");
            Disarm(DisarmReason.ExpansionPending, _lockedTint);
            return;
        }

        LogState("LoopBoundary entry");

        // ------------------------------------------------------------
        // 3) Normal re-arm path
        // ------------------------------------------------------------
        if (!_isArmed)
        {
            DBG("[PS:LB] -> re-arm");
            if (_state == PhaseStarState.WaitingForPoke)
                ArmNext();
            else
                EnterDormantWaitState();
        }
        else
        {
            DBG("[PS:LB] -> No need to arm");
        }
    }
    
    private void DBG(string msg)
    {
        Debug.Log($"[PSDBG] {msg} :: star={name} state={_state} armed={_isArmed} " +
                  $"awaitCollectClear={_awaitingCollectableClear} preview={(_previewVisual != null ? 1 : 0)} " +
                  $"activeNode={(_activeNode != null ? _activeNode.name : "null")} lockedTint={_lockedTint}");
    }
    

    private void EnsurePreviewRing()
    {
        if (_previewInitialized) return;
        _previewInitialized = true;

        BuildPreviewRing();
    }


    private void OnDisable()
    {
        var drum = GameFlowManager.Instance != null ? GameFlowManager.Instance.activeDrumTrack : null;
        SafeUnsubscribeAll();
        CleanupManagedCoroutines();
    }

    private void OnDestroy()
    {
        _isDisposing = true;
        SafeUnsubscribeAll();
        CleanupManagedCoroutines();
    }

    private void WireBinSource(DrumTrack drum)
    {
        _drum = drum;
        if (_drum == null) return;
        RefreshLoopDuration();
        EnsurePreviewRing();
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

    if (_baseSortingOrder == 0)
    {
        var baseSr = GetComponentInChildren<SpriteRenderer>(true);
        _baseSortingOrder = baseSr ? baseSr.sortingOrder : 2000;
    }

    if (visuals == null)
    {
        _buildingPreview = false;
        return;
    }

    // Role is determined at runtime via attunement; start gray until the star drains its first colored dust.
    var role = _previewRole;
    var track = role != MusicalRole.None ? FindTrackByRole(role) : null;
    Color roleColor = role != MusicalRole.None ? ResolveRoleColor(role, track) : Color.gray;
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
        return _previewRole != MusicalRole.None ? _previewRole : MusicalRole.Bass;
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

        // Disarmed push: bypass the CIF/EP gates so the vehicle always shoves the star.
        if (!_isArmed
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
            if (_disarmReason == DisarmReason.SiblingActive) return;
            EjectActivePreviewShardAndFlow(coll);
            return;
        }

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
        LastNodeWasSuperNode = false;
        LastNodeWasExpired   = false;
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

            if (!resolvedNode.WasCaptured)
                LastNodeWasExpired = true;

            // Fire before any Unity component access — safe even when the star
            // GameObject was already destroyed by DestroyStarAfterDelay.
            OnMineNodeResolved?.Invoke(this, _attunedRole);

            // Guard Unity component access: star may be destroyed if the player
            // took longer than starExitDuration to kill the node.
            if (this == null) return;

            if (_state == PhaseStarState.BridgeInProgress) return;

            _awaitingCollectableClear = true;
            _awaitingCollectableClearSinceLoop = (_drum != null)
                ? _drum.completedLoops
                : (GameFlowManager.Instance?.activeDrumTrack?.completedLoops ?? -1);
            _awaitingCollectableClearSinceDsp = AudioSettings.dspTime;
            MoveOffScreenForBurst();
            Disarm(DisarmReason.NodeResolving, spawnTint);
            LogState("OnResolved");
        };

        CollectionSoundManager.Instance?.PlayPhaseStarImpact(usedTrack, usedTrack.GetCurrentNoteSet(), 0.8f);
        PrepareNextDirective();
        Trace("SpawnNodeCommon: end");
    }

    void EjectActivePreviewShardAndFlow(Collision2D coll)
    {
        if (behaviorProfile == null || visuals == null) return;

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

        visuals?.HideAll();
        dust?.ResetTentacles();

        Disarm(DisarmReason.NodeResolving, ejectedTrack.trackColor);

        Debug.Log($"[MNDBG] EjectActive: contact={contact}, role={ejectedTrack.assignedRole}");
        if (ShouldSpawnSuperNodeForTrack(ejectedTrack))
            SpawnSuperNodeCommon(contact, ejectedTrack);
        else
            SpawnNodeCommon(contact, ejectedTrack);

        _isDisposing = true;
        OnEjected?.Invoke(this, ejectedRole);
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
        LastNodeWasSuperNode = true;
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

            // Fire before any Unity component access — safe even when star is destroyed.
            OnMineNodeResolved?.Invoke(this, _attunedRole);

            if (this == null) return;

            if (_state == PhaseStarState.BridgeInProgress) return;
            _awaitingCollectableClear = true;
            _awaitingCollectableClearSinceLoop = (_drum != null)
                ? _drum.completedLoops
                : (GameFlowManager.Instance?.activeDrumTrack?.completedLoops ?? -1);
            _awaitingCollectableClearSinceDsp = AudioSettings.dspTime;
            MoveOffScreenForBurst();
            Disarm(DisarmReason.NodeResolving, spawnTint);
            LogState("OnSuperNodeResolved");
        };

        PrepareNextDirective();
    }

    void EjectCachedDirectiveAndFlow(Collision2D coll)
    {
        var contact = coll.GetContact(0).point;
        var starPos = (Vector2)transform.position;
        var vehiclePos = coll.rigidbody != null ? coll.rigidbody.position : contact;

        _lastImpactDir = (starPos - vehiclePos).normalized;
        _lastImpactStrength = Mathf.Clamp(coll.relativeVelocity.magnitude, 0f, MaxImpactStrength);

        Disarm(DisarmReason.NodeResolving, _cachedTrack.trackColor);
        ActivateSafetyBubble();
        if (_cachedIsSuperNode)
            SpawnSuperNodeCommon(contact, _cachedTrack);
        else
            SpawnNodeCommon(contact, _cachedTrack);

        _isDisposing = true;
        OnEjected?.Invoke(this, _attunedRole);
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

        string targetRole = _previewRole != MusicalRole.None ? _previewRole.ToString() : "-";
        Debug.Log(
            $"[PhaseStar][{where}] state={_state} armed={_isArmed} entry={_entryInProgress} burstOff={_burstOffScreen} " +
            $"awaitClr={_awaitingCollectableClear} disarm={_disarmReason} " +
            $"role={targetRole} attunedRole={_attunedRole} charge={GetTotalCharge():0.00}");
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

        if (!visuals) visuals = GetComponentInChildren<PhaseStarVisuals2D>(true);
        visuals?.HideSafetyBubble();
    }
}
    



    