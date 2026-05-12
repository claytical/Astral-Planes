using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
[Serializable]
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
    [Tooltip("Accumulator rotation speed multiplier when charge is ready (diamonds merged → faster spin).")]
    [SerializeField, Min(1f)] private float readyRotSpeedMul = 2.5f;
    [Header("Charge Readiness")]
    private bool _cachedIsSuperNode = false;
    
    private int CollectableClearTimeoutLoops => behaviorProfile ? behaviorProfile.collectableClearTimeoutLoops : 2;

    private float CollectableClearTimeoutSeconds =>
        behaviorProfile ? behaviorProfile.collectableClearTimeoutSeconds : 0f;
    
    private bool _previewInitialized;

    // _awaitingCollectableClear: true while we are blocked waiting for in-flight collectables
    // to land before re-arming the star. Two independent timers enforce a timeout so a
    // stalled or destroyed collectable can't lock the star forever:
    //   • SinceLoop — counts loop boundaries elapsed (reliable in sync with music tempo).
    //   • SinceDsp  — counts real wall-clock DSP seconds (catches edge cases where loop
    //                 boundaries stop firing, e.g. if DrumTrack is paused mid-flight).
    // Both timers start on the first loop boundary that arrives while waiting. -1 means unset.
    private int _awaitingCollectableClearSinceLoop = -1;
    private double _awaitingCollectableClearSinceDsp = -1.0;

    [Header("Subcomponents (optional)")] [SerializeField]
    private PhaseStarVisuals2D visuals;

    [SerializeField] private PhaseStarMotion2D motion;
    [SerializeField] private PhaseStarDustAffect dust;
    [SerializeField] private PhaseStarCravingNavigator cravingNavigator;

    private GameFlowManager _gfm;
    private DrumTrack _drum;
    private bool _subscribedLoopBoundary;
    private Color _lockedTint;
    // Single accumulator shard — flat fields replace the old previewRing list.
    private Color       _previewColor;
    private MusicalRole _previewRole;
    private Transform   _previewVisual;
    private Transform   _previewVisualB;   // second counter-rotating diamond
    private bool _isDisposing;
    private readonly PhaseStarInteractionState _interactionState = new();
    private bool _entryInProgress { get => _interactionState.Interaction.EntryInProgress; set => _interactionState.Interaction.EntryInProgress = value; }
    private bool _buildingPreview;
    // Cached impact data for the next MineNode spawn
    Vector2 _lastImpactDir = Vector2.right;
    float _lastImpactStrength = 0f;

    // Optional clamp so crazy physics spikes don't blow things up
    const float MaxImpactStrength = 40f;
    [SerializeField, Min(0f)] private float disarmedPushScale = 0.6f;
    private bool _awaitingCollectableClear { get => _interactionState.Interaction.AwaitingCollectableClear; set => _interactionState.Interaction.AwaitingCollectableClear = value; }
    private bool _hasReceivedEnergy { get => _interactionState.ChargeVisual.HasReceivedEnergy; set => _interactionState.ChargeVisual.HasReceivedEnergy = value; }   // set true on first drain delivery; drives gray→role color lerp
    private PhaseStarDisarmReason _disarmReason
    {
        get => (PhaseStarDisarmReason)_interactionState.Interaction.DisarmReason;
        set => _interactionState.Interaction.DisarmReason = (int)value;
    }

    // True while the star is parked off-screen during a collectable burst.
    // Cleared in OnBurstNotesReleased() when all burst notes have been committed.
    // All mutations should go through TryEnterBurstHidden() / TryExitBurstHidden()
    // so the coordinator is the single write path. The one exception is the hard
    // reset in EnterInMaze(), which resets all interaction state together.
    private bool _burstOffScreen { get => _interactionState.Interaction.BurstOffScreen; set => _interactionState.Interaction.BurstOffScreen = value; }
    private Coroutine _waitForDustCo;

    [SerializeField] private bool _tracePhaseStar = true;

    private readonly Dictionary<MusicalRole, float> _starCharge = new();
    private IPhaseStarChargeModel _chargeModel;
    private IPhaseStarStateController _stateController;
    private IPhaseStarBurstCoordinator _burstCoordinator;
    [SerializeField] private bool allowConcurrentDormantCharging = true;

    [Header("Shard Visuals (Charge-Alpha)")]
    [Tooltip("Minimum alpha for a shard with zero charge — keeps it ghost-visible.")]
    [SerializeField, Range(0f, 0.5f)] private float shardMinAlpha = 0.08f;



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

    [Tooltip("Minimum diamond scale while tentacles are actively drawing/draining, so shards bloom out of the particle field before charge is visible.")]
    [SerializeField, Range(0f, 1f)] private float tentacleBloomMinScale = 0.22f;
    [Tooltip("Baseline star seed scale shown during dormant dust-calling so the particle force exists before tentacles finish charging.")]
    [SerializeField, Range(0f, 0.5f)] private float dormantSeedScale = 0.08f;

    private float _displayedCharge01 { get => _interactionState.ChargeVisual.DisplayedCharge01; set => _interactionState.ChargeVisual.DisplayedCharge01 = value; }

    private bool _dormantSeedVisualPrimed { get => _interactionState.ChargeVisual.DormantSeedVisualPrimed; set => _interactionState.ChargeVisual.DormantSeedVisualPrimed = value; }

    private bool _ejectionInFlight;
    private bool _pendingDormantActivation;
    private int _spawnTicket;
    private int _lastPokeFrame = -999999;
    private InstrumentTrack _cachedTrack;
    private MineNode _activeNode;
    private SuperNode _activeSuperNode;
    private readonly List<InstrumentTrack> _targets = new(4);
    private MusicalRole _attunedRole = MusicalRole.None;
    public MusicalRole AttunedRole => _attunedRole;
    public bool LastNodeWasSuperNode { get; private set; }
    public bool LastNodeWasExpired   { get; private set; }
    public bool LastNodeWasEscaped   { get; private set; }
    public bool LastNodeWasCaptured  { get; private set; }
    [SerializeField] private MotifProfile _assignedMotif; // optional: motif this star represents (motif system)

    public event Action<PhaseStar> OnArmed;
    public event Action<PhaseStar> OnDisarmed;
    public event Action<PhaseStar, MusicalRole> OnEjected;
    public event Action<PhaseStar> OnBurstRolledBack;
    // Fired when the Vehicle destroys the MineNode/SuperNode — the burst is now spawning.
    // Safe to fire from a destroyed star (C# delegate, not Unity message).
    public event Action<PhaseStar, MusicalRole> OnMineNodeResolved;
    public event Action<PhaseStar, MusicalRole, Vector2Int> OnTentacleZapResolvedEvent;
    private bool _isArmed { get => _interactionState.Interaction.IsArmed; set => _interactionState.Interaction.IsArmed = value; }
    private int _baseSortingOrder;

    public List<MusicalRole> GetMotifActiveRoles() =>
        _assignedMotif != null ? _assignedMotif.GetActiveRoles() : null;

    // -------------------- Lifecycle --------------------
    void Start()
    {
        _chargeModel ??= new PhaseStarChargeModel(_starCharge);
        _stateController ??= new PhaseStarStateController();
        _burstCoordinator ??= new PhaseStarBurstCoordinator();
        EnsurePreviewRing();
    }
    public void AddCharge(MusicalRole role, float energyUnitsDelivered)
    {
        _chargeModel ??= new PhaseStarChargeModel(_starCharge);
        _chargeModel.AddCharge(role, energyUnitsDelivered, behaviorProfile != null ? behaviorProfile.dustToStarChargeMul : 1f, _attunedRole);
    }
    /// <summary>
    /// Returns 0..1 hunger for a specific role: 1 = starving (zero charge), 0 = fully charged.
    /// Used by the navigator to weight density steering toward under-charged roles.
    /// </summary>
    public float GetRoleHunger(MusicalRole role)
    {
        _chargeModel ??= new PhaseStarChargeModel(_starCharge);
        return _chargeModel.GetRoleHunger(role);
    }

    public static bool IsPointInsideSafetyBubble(Vector2 worldPos) => false;

    public bool IsPointInsideMyBubble(Vector2 worldPos) => false;

    public void EnterInMaze(Vector2 worldPos)
    {
        EnsureSubcomponents();
        StopManagedCoroutine(ref _waitForDustCo);

        _entryInProgress = false;
        _state = PhaseStarState.Dormant;

        transform.position = (Vector3)worldPos + Vector3.forward * transform.position.z;

        visuals?.HideAll();
        DisableColliders();
        dust?.SetTentaclesActive(false);
        cravingNavigator?.SetActive(false);
        _isArmed = false;
        _disarmReason = PhaseStarDisarmReason.None;
        _burstOffScreen = false;
        _awaitingCollectableClear = false;
        _starCharge.Clear();
        _displayedCharge01 = 0f;
        TransitionZapState(ZapProgressState.Seeking, _previewRole, "phase-reset-enter-maze");

        if (motion != null)
        {
            motion.Enable(true);
            motion.SetFrozen(true);
            motion.SetSpeedMultiplier(0f);
            motion.SetOverrideTarget(null);
        }

        EnterDormantWaitState();
        LogState("EnterInMaze+Dormant");
    }
    
    private Vector2 ComputeDormantRestPosition()
    {
        var drum = TryResolveContext(out var resolvedDrum, out _) ? resolvedDrum : null;
        if (drum == null) return transform.position;
        int gw = drum.GetSpawnGridWidth();
        int gh = drum.GetSpawnGridHeight();
        return drum.GridToWorldPosition(new Vector2Int(gw / 2, gh / 2));
    }

    private void EnterDormantWaitState()
    {
        _entryInProgress = false;
        _state = PhaseStarState.Dormant;
        _isArmed = false;
        _disarmReason = PhaseStarDisarmReason.None;

        visuals?.HideSafetyBubble();
        visuals?.ToggleShardRenderers(true);

        float seedScale = Mathf.Max(0f, dormantSeedScale);
        Vector3 seed = Vector3.one * seedScale;
        if (visuals != null) visuals.transform.localScale = seed;
        if (_previewVisual != null) _previewVisual.localScale = seed;
        if (_previewVisualB != null) _previewVisualB.localScale = seed;
        visuals?.ShowDim(ResolvePreviewColorByReadiness());
        _dormantSeedVisualPrimed = true;

        DisableColliders();
        _hasReceivedEnergy = false;

        // Tentacles + navigator active during Dormant — they drain dust to build charge.
        dust?.SetTentaclesActive(true);
        cravingNavigator?.SetActive(true);

        // Stay pinned in place while charging.
        motion?.SetFrozen(true);
        motion?.SetOverrideTarget(null);

        if (_waitForDustCo == null)
            _waitForDustCo = StartCoroutine(Co_WaitForColoredDust());
    }

    private void TransitionDormantToActive()
    {
        if (_entryInProgress || _state != PhaseStarState.Dormant)
            return;

        bool zapReady =
            (_requiredZapNoteSetAvailable && _plannedEjectionDescriptor.IsValid && zappedCount >= requiredZapCount) ||
            _zapProgressState == ZapProgressState.WaitingForRetract ||
            _zapProgressState == ZapProgressState.ReadyLatched;

        if (!zapReady)
        {
            if (_tracePhaseStar)
                Debug.Log($"[PhaseStar] TransitionDormantToActive blocked (not zap-ready). state={_state} zapState={_zapProgressState} zapped={zappedCount}/{requiredZapCount} descriptorValid={_plannedEjectionDescriptor.IsValid} requiredSetAvailable={_requiredZapNoteSetAvailable}", this);
            return;
        }

        StopManagedCoroutine(ref _waitForDustCo);
        _pendingDormantActivation = true;

        // If we are already latched/retracted, don't force WaitingForRetract again.
        // Re-entering WaitingForRetract here can leave the star in a non-ejectable state
        // when no additional retract event is emitted.
        bool alreadyRetracted = _zapProgressState == ZapProgressState.ReadyLatched || (dust != null && !dust.HasActiveTentacles);
        if (alreadyRetracted)
            return;

        TransitionZapState(ZapProgressState.WaitingForRetract, _requiredZapRole, "dormant-threshold-hit");
        dust?.BeginRetractionForActiveTentacles();
    }

    private void FinalizeDormantToActiveAfterRetract(bool force = false)
    {
        if ((!_pendingDormantActivation && !force) || _state != PhaseStarState.Dormant)
            return;

        _pendingDormantActivation = false;
        _state = PhaseStarState.WaitingForPoke;
        _dormantSeedVisualPrimed = false;

        // Star earned free movement after all tentacles are fully retracted.
        motion?.SetFrozen(false);
        dust?.SetTentaclesActive(false);
        cravingNavigator?.SetActive(true);

        // If paused for a sibling's node cycle, defer arming to Resume(). The state has
        // already advanced to WaitingForPoke here, so CanArm() will pass when Resume()
        // calls ArmNext(). Calling ArmNext() now would fail (CIF from the sibling's burst)
        // and cascade into HideInPlaceForBurst(), forcing an unnecessary dormant reset.
        if (_disarmReason != PhaseStarDisarmReason.SiblingActive)
            ArmNext();

        LogState("DormantWake+Armed");
    }

    private IEnumerator Co_WaitForColoredDust()
    {
        while (true)
        {
            yield return new WaitForSeconds(0.5f);

            bool hasDust = HasColoredDustAvailable();
            bool hasDelivery = _hasReceivedEnergy;
            bool hasEjectable = HasDominantRoleEjectable();

            if (_tracePhaseStar)
            {
                Debug.Log($"[PhaseStar] Dormant wait precheck hasDust={hasDust} hasDelivery={hasDelivery} hasEjectable={hasEjectable}", this);
            }

            // Wake on stable acquisition preconditions only.
            // Ejection readiness remains gated by poke/eject code paths.
            if (hasDust && hasDelivery)
                break;
        }

        _waitForDustCo = null;
        TransitionDormantToActive();
    }

    private bool HasColoredDustAvailable()
    {
        return TryResolveContext(out _, out var dustGen) && dustGen.HasAnyDustWithRole();
    }

    private void StopManagedCoroutine(ref Coroutine co)
    {
        if (co == null) return;
        StopCoroutine(co);
        co = null;
    }

    private GameFlowManager ResolveGameFlowManager()
    {
        _gfm ??= GameFlowManager.Instance;
        return _gfm;
    }

    private bool TryResolveContext(out DrumTrack drum, out CosmicDustGenerator dustGen)
    {
        var gfm = ResolveGameFlowManager();
        drum = _drum != null ? _drum : gfm?.activeDrumTrack;
        dustGen = gfm?.dustGenerator;
        return drum != null && dustGen != null;
    }

    private PhaseStarInteractionSnapshot BuildInteractionSnapshot(bool anyCollectablesInFlight = false, bool anyExpansionPending = false)
    {
        return new PhaseStarInteractionSnapshot(
            _state,
            _waitForDustCo != null,
            _activeNode != null || _activeSuperNode != null,
            anyCollectablesInFlight,
            anyExpansionPending,
            ZapProgress01 >= 1f,
            _interactionState.Interaction);
    }

    private bool TryEnterBurstHidden()
    {
        _burstCoordinator ??= new PhaseStarBurstCoordinator();
        bool burstOffScreen = _burstOffScreen;
        bool changed = _burstCoordinator.TryEnterBurstHidden(ref burstOffScreen);
        _burstOffScreen = burstOffScreen;
        return changed;
    }

    private bool TryExitBurstHidden()
    {
        _burstCoordinator ??= new PhaseStarBurstCoordinator();
        bool burstOffScreen = _burstOffScreen;
        bool changed = _burstCoordinator.TryExitBurstHidden(ref burstOffScreen);
        _burstOffScreen = burstOffScreen;
        return changed;
    }

    private float GetTotalCharge()
    {
        _chargeModel ??= new PhaseStarChargeModel(_starCharge);
        return _chargeModel.GetTotalCharge();
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

    private bool GetDominantRoleRaw(out MusicalRole role, out float rawCharge, out float threshold)
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

    private bool HasDominantRoleEjectable() =>
        GetDominantRoleRaw(out _, out float rawCharge, out float threshold) && rawCharge >= threshold;
    private bool IsEjectionReady()
    {
        if (!_plannedEjectionDescriptor.IsValid)
        {
            if (!_missingDescriptorWarned)
            {
                Debug.LogWarning($"[PhaseStar:Zap] planned ejection descriptor missing; readiness blocked. role={_requiredZapRole} track={_plannedEjectionDescriptor.track?.name ?? "null"}");
                _missingDescriptorWarned = true;
            }
            return false;
        }

        _missingDescriptorWarned = false;
        return _zapProgressState == ZapProgressState.ReadyLatched && HasDominantRoleEjectable();
    }
    private NoteSet ResolvePlannedNoteSet(InstrumentTrack track)
    {
        if (track == null) return null;
        ResolveGameFlowManager();
        int entropy = CurrentEntropyForSelection();
        return _gfm != null ? _gfm.GenerateNotes(track, entropy) : null;
    }
    private struct PlannedEjectionDescriptor
    {
        public MusicalRole role;
        public InstrumentTrack track;
        public NoteSet noteSet;
        public int requiredZapCount;

        public bool IsValid => role != MusicalRole.None && track != null && requiredZapCount > 0;
    }

    private PlannedEjectionDescriptor _plannedEjectionDescriptor;
    private bool _missingDescriptorWarned;

    private static bool PlannedDescriptorEquals(in PlannedEjectionDescriptor a, in PlannedEjectionDescriptor b)
    {
        return a.role == b.role &&
               ReferenceEquals(a.track, b.track) &&
               ReferenceEquals(a.noteSet, b.noteSet) &&
               a.requiredZapCount == b.requiredZapCount;
    }

    private bool TryRefreshRequiredZapCountForPlannedRole(
        MusicalRole role,
        InstrumentTrack track,
        bool resetCurrentZapCount,
        string reason)
    {
        _requiredZapRole = role;
        _requiredZapNoteSetAvailable = false;

        PlannedEjectionDescriptor previousDescriptor = _plannedEjectionDescriptor;
        PlannedEjectionDescriptor nextDescriptor = default;
        nextDescriptor.role = role;
        nextDescriptor.track = track;

        if (track == null)
        {
            requiredZapCount = int.MaxValue;
            _plannedEjectionDescriptor = nextDescriptor;
            Debug.LogWarning($"[PhaseStar:Zap] missing track for required zap refresh. role={role} reason={reason}");
            return false;
        }

        NoteSet planned = ResolvePlannedNoteSet(track);
        if (planned == null)
        {
            requiredZapCount = int.MaxValue;
            _plannedEjectionDescriptor = nextDescriptor;
            Debug.LogWarning($"[PhaseStar:Zap] planned NoteSet unavailable; blocking readiness. role={role} track={track.name} reason={reason}");
            return false;
        }

        int noteCount;
        if (!TryResolveAuthoritativeZapCount(role, track, out noteCount))
        {
            int persistentTemplateCount = planned.persistentTemplate != null ? planned.persistentTemplate.Count : 0;
            int distinctStepCount = planned.GetStepList()?.Distinct().Count() ?? 0;
            int noteListCount = planned.GetNoteList()?.Count ?? 0;
            noteCount = Mathf.Max(persistentTemplateCount, Mathf.Max(distinctStepCount, noteListCount));
        }

        if (_currentBurstRequiredZaps > 0)
            noteCount = Mathf.Max(noteCount, _currentBurstRequiredZaps);

        nextDescriptor.noteSet = planned;
        nextDescriptor.requiredZapCount = Mathf.Max(1, noteCount);

        _requiredZapNoteSetAvailable = nextDescriptor.IsValid;
        requiredZapCount = nextDescriptor.requiredZapCount;

        bool descriptorChanged = !PlannedDescriptorEquals(previousDescriptor, nextDescriptor);
        _plannedEjectionDescriptor = nextDescriptor;

        if (resetCurrentZapCount)
            zappedCount = 0;

        if (resetCurrentZapCount)
        {
            TransitionZapState(ZapProgressState.Seeking, role, $"refresh:{reason}");
        }
        else if (_zapProgressState == ZapProgressState.Seeking || _zapProgressState == ZapProgressState.Zapping)
        {
            TransitionZapState(ZapProgressState.Zapping, role, $"refresh:{reason}");
        }
        Debug.Log($"[PhaseStar:Zap] refreshed role={_requiredZapRole} requiredZaps={requiredZapCount} currentZaps={zappedCount} changed={descriptorChanged} reason={reason}");
        return true;
    }
    private void PrimeZapRequirementForRole(MusicalRole role, InstrumentTrack track)
    {
        TryRefreshRequiredZapCountForPlannedRole(role, track, resetCurrentZapCount: true, reason: "prime");
    }


    private bool _dustDeliveryWired;

    // Zap progress state machine.
    // Seeking          → star has no zaps yet and is accumulating.
    // DormantNotSeeking → a sibling star holds the coordinator lock; this star suspends
    //                    progression without losing its counters. Enters only when
    //                    allowConcurrentDormantCharging is false.
    // Zapping          → at least one zap delivered, still accumulating toward threshold.
    // WaitingForRetract → threshold met; tentacles are retracting before readiness is latched.
    // ReadyLatched      → all zaps confirmed + tentacles retracted; poke can now eject a node.
    // Ejecting          → node spawn in flight.
    private enum ZapProgressState { Seeking, DormantNotSeeking, Zapping, WaitingForRetract, ReadyLatched, Ejecting }
    private ZapProgressState _zapProgressState = ZapProgressState.Seeking;
    private ZapProgressState _preservedZapProgressStateBeforeCoordinatorLock = ZapProgressState.Seeking;
    private bool _coordinatorLockOwnedByOtherStar;

    // Zap count authority chain (highest to lowest priority):
    //   1. _currentBurstRequiredZaps — set by DirectSpawnMineNode() from the actual node payload;
    //      overrides everything else because the node is already spawned.
    //   2. TryResolveAuthoritativeZapCount() — motif/phase authored count from the NoteSet.
    //   3. NoteSet cardinality fallback (Max of persistentTemplate, distinct steps, note list).
    // requiredZapCount stores the resolved value; _currentBurstRequiredZaps is the burst-phase
    // floor so tentacle pool sizing stays stable even if requiredZapCount refreshes mid-cycle.
    private int zappedCount;
    private int requiredZapCount = 1;
    private int _currentBurstRequiredZaps = 0;
    public int RequiredZapCount => Mathf.Max(1, requiredZapCount);
    public int RemainingZapCount => Mathf.Max(0, RequiredZapCount - Mathf.Max(0, zappedCount));
    public float ZapProgress01 => Mathf.Clamp01((float)Mathf.Max(0, zappedCount) / Mathf.Max(1, RequiredZapCount));
    public int GetDesiredTentacleCount()
    {
        // Take the max of both counts: requiredZapCount may still be catching up from a
        // refresh probe on the first frame after a burst spawn.
        return Mathf.Max(1, Mathf.Max(RequiredZapCount, _currentBurstRequiredZaps));
    }
    private MusicalRole _requiredZapRole = MusicalRole.None;
    private bool _requiredZapNoteSetAvailable;
    private Vector2Int _lastResolvedZapCell;
    private MusicalRole _lastResolvedZapRole = MusicalRole.None;

    private bool MayAcquireDustTargets()
    {
        bool zapStateAllowsAcquire =
            _zapProgressState == ZapProgressState.Seeking ||
            _zapProgressState == ZapProgressState.Zapping;
        bool globallyGatedOrDisarmed =
            _disarmReason != PhaseStarDisarmReason.None ||
            _state != PhaseStarState.Dormant ||
            _coordinatorLockOwnedByOtherStar;
        return zapStateAllowsAcquire && !globallyGatedOrDisarmed;
    }

    private void ApplyDustAcquisitionPolicy(string reason)
    {
        bool enabled = MayAcquireDustTargets();
        dust?.SetAcquisitionEnabled(enabled, $"{reason}|zap={_zapProgressState}|state={_state}|disarm={_disarmReason}");
    }
    private void TransitionZapState(ZapProgressState next, MusicalRole role, string reason)
    {
        if (_zapProgressState == next) return;
        var prev = _zapProgressState;
        _zapProgressState = next;
        ApplyDustAcquisitionPolicy($"zap-transition:{reason}");
        bool acquisitionEnabled = MayAcquireDustTargets();
        Debug.Log($"[PhaseStar:ZapState] {prev}->{next} role={role} zappedCount={zappedCount} requiredZapCount={requiredZapCount} acquisitionEnabled={acquisitionEnabled} reason={reason} interaction=({_interactionState.ToDebugString()})");
    }

    /// <summary>
    /// Called when a global coordinator lock is held by a different star.
    /// Non-owner stars suspend dormant seeking/ready progression without clearing
    /// zap counters or latch-related state.
    /// </summary>
    public void OnCoordinatorLockOwnedByAnotherStar()
    {
        if (allowConcurrentDormantCharging)
            return;

        if (_state != PhaseStarState.Dormant)
            return;

        bool canSuspend =
            _zapProgressState == ZapProgressState.Seeking ||
            _zapProgressState == ZapProgressState.Zapping ||
            _zapProgressState == ZapProgressState.WaitingForRetract ||
            _zapProgressState == ZapProgressState.ReadyLatched;

        if (!canSuspend)
            return;

        _coordinatorLockOwnedByOtherStar = true;
        _preservedZapProgressStateBeforeCoordinatorLock = _zapProgressState;
        TransitionZapState(ZapProgressState.DormantNotSeeking, _requiredZapRole, "coordinator-lock-owned-by-other");
    }

    /// <summary>
    /// Called when the coordinator lock is released and the owner's cooldown event fires.
    /// Restores dormant progression based on preserved readiness state.
    /// </summary>
    public void OnCoordinatorLockReleasedAfterOwnerCooldown()
    {
        if (allowConcurrentDormantCharging)
            return;

        _coordinatorLockOwnedByOtherStar = false;

        if (_state != PhaseStarState.Dormant)
            return;

        if (_zapProgressState == ZapProgressState.ReadyLatched)
        {
            // Readiness was achieved while suspended. Now that the lock/cooldown is clear,
            // proceed through the normal dormant->active wake path.
            TransitionDormantToActive();
            return;
        }

        if (_zapProgressState != ZapProgressState.DormantNotSeeking)
            return;

        var restore = _preservedZapProgressStateBeforeCoordinatorLock;
        bool wasPreservedReady = restore == ZapProgressState.ReadyLatched || restore == ZapProgressState.WaitingForRetract;
        var next = wasPreservedReady ? ZapProgressState.WaitingForRetract : ZapProgressState.Seeking;
        TransitionZapState(next, _requiredZapRole, "coordinator-lock-released-owner-cooldown");

        if (next == ZapProgressState.WaitingForRetract)
            TransitionDormantToActive();
    }

    private void EnsureSubcomponents()
    {
        if (!visuals) visuals = GetComponentInChildren<PhaseStarVisuals2D>(true);
        if (!motion) motion = GetComponentInChildren<PhaseStarMotion2D>(true);
        if (!dust)
        {
            dust = GetComponentInChildren<PhaseStarDustAffect>(true);
            _dustDeliveryWired = false;
        }
        if (!_dustDeliveryWired && dust != null)
        {
            dust.onDelivery += OnDustDelivery;
            dust.OnAllTentaclesRetracted += OnAllTentaclesRetracted;
            _dustDeliveryWired = true;
        }
        if (!cravingNavigator) cravingNavigator = GetComponentInChildren<PhaseStarCravingNavigator>(true);
    }

    private void CleanupManagedCoroutines()
    {
        StopManagedCoroutine(ref _waitForDustCo);
    }
    private void OnDustDelivery(MusicalRole role, float deliveredUnits)
    {
        _hasReceivedEnergy = true;
        if (deliveredUnits <= 0f || role == MusicalRole.None) return;
        if (_zapProgressState == ZapProgressState.Seeking)
            TransitionZapState(ZapProgressState.Zapping, role, "first-delivery");
    }

    public void OnTentacleZapResolved(MusicalRole role, Vector2Int targetCell)
    {
        if (role == MusicalRole.None) return;

        // Once readiness has been reached for this cycle, ignore late/extra resolves so
        // additional tentacles cannot keep extending the cycle.
        if (_zapProgressState == ZapProgressState.WaitingForRetract ||
            _zapProgressState == ZapProgressState.ReadyLatched ||
            _zapProgressState == ZapProgressState.Ejecting)
            return;

        // Canonical zap progress path: increment exactly once per confirmed dust clear.
        zappedCount++;
        _lastResolvedZapRole = role;
        _lastResolvedZapCell = targetCell;

        if (!_requiredZapNoteSetAvailable || !_plannedEjectionDescriptor.IsValid)
        {
            // Self-heal: derive descriptor from the role that actually resolved a zap.
            // Without this, stars can become ReadyLatched with an invalid descriptor
            // and remain unejectable while acquisition stays disabled.
            var resolvedTrack = FindTrackByRole(role);
            if (resolvedTrack != null)
            {
                TryRefreshRequiredZapCountForPlannedRole(
                    role,
                    resolvedTrack,
                    resetCurrentZapCount: false,
                    reason: "zap-resolved-descriptor-repair");
            }
            Debug.LogWarning($"[PhaseStar:ZapResolved] missing planned ejection descriptor; readiness blocked. role={role} track={_plannedEjectionDescriptor.track?.name ?? "null"}");
        }

        bool readyNow = zappedCount >= requiredZapCount;
        if (readyNow)
        {
            TransitionZapState(ZapProgressState.WaitingForRetract, role, "count-threshold-met");
            dust?.SetAcquisitionEnabled(false, "waiting-for-retract-threshold-met");
            dust?.BeginRetractionForActiveTentacles();

            if (_state == PhaseStarState.Dormant && !_pendingDormantActivation && !_coordinatorLockOwnedByOtherStar)
                TransitionDormantToActive();
        }

        OnTentacleZapResolvedEvent?.Invoke(this, role, targetCell);
        Debug.Log($"[PhaseStar:ZapResolved] role={role} targetCell={targetCell} requiredZaps={requiredZapCount} currentZaps={zappedCount} ready={readyNow}");
    }

    private void OnAllTentaclesRetracted()
    {
        if (_pendingDormantActivation)
            FinalizeDormantToActiveAfterRetract();
        else if (_state == PhaseStarState.Dormant &&
                 _zapProgressState == ZapProgressState.WaitingForRetract &&
                 !_coordinatorLockOwnedByOtherStar)
            FinalizeDormantToActiveAfterRetract(force: true);

        if (_zapProgressState != ZapProgressState.WaitingForRetract)
            return;

        // Safety: only latch readiness if zap requirements are truly satisfied.
        bool canLatchReady = zappedCount >= requiredZapCount;
        if (!canLatchReady)
        {
            MusicalRole fallbackRole = _requiredZapRole != MusicalRole.None ? _requiredZapRole : _previewRole;
            var fallback = zappedCount > 0 ? ZapProgressState.Zapping : ZapProgressState.Seeking;
            TransitionZapState(fallback, fallbackRole, "retract-without-required-zaps");
            return;
        }

        MusicalRole latchedRole = _requiredZapRole != MusicalRole.None ? _requiredZapRole : _previewRole;

        // Descriptor must be valid before latching readiness, otherwise the star can
        // stop acquiring dust and never become ejectable.
        if (!_plannedEjectionDescriptor.IsValid || !_requiredZapNoteSetAvailable)
        {
            MusicalRole repairRole = latchedRole != MusicalRole.None ? latchedRole : _lastResolvedZapRole;
            if (repairRole != MusicalRole.None)
            {
                var repairTrack = FindTrackByRole(repairRole);
                if (repairTrack != null)
                {
                    TryRefreshRequiredZapCountForPlannedRole(
                        repairRole,
                        repairTrack,
                        resetCurrentZapCount: false,
                        reason: "retract-descriptor-repair");
                }
            }
        }

        if (!_plannedEjectionDescriptor.IsValid || !_requiredZapNoteSetAvailable)
        {
            MusicalRole fallbackRole = _lastResolvedZapRole != MusicalRole.None ? _lastResolvedZapRole : latchedRole;
            var fallback = zappedCount > 0 ? ZapProgressState.Zapping : ZapProgressState.Seeking;
            TransitionZapState(fallback, fallbackRole, "retract-descriptor-invalid");
            dust?.SetAcquisitionEnabled(true, "retract-descriptor-invalid-resume-acquire");
            return;
        }

        TransitionZapState(ZapProgressState.ReadyLatched, latchedRole, "all-tentacles-retracted");
        dust?.SetAcquisitionEnabled(false, "ready-latched-keep-disabled");
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
            // Do NOT call SetBinCount here — committed bin count is owned by
            // ArmCohortsOnLoopBoundary and ResyncLeaderBinsNow. Resetting to 1 on
            // every star spawn would clobber the expanded 2-bin state mid-loop.
            WireBinSource(_drum);
            Debug.Log($"[PhaseStar] loop={_drum.GetLoopLengthInSeconds():0.##}s  targets={_targets?.Count ?? 0}");

        }

        // Stop any coroutine still running from a previous phase before reinitializing.
        StopManagedCoroutine(ref _waitForDustCo);

        // Reset attunement so the star can re-attune to the new phase's roles.
        _attunedRole = MusicalRole.None;

        // Clear charge state for this new star.
        _starCharge.Clear();
        _displayedCharge01 = 0f;
        TransitionZapState(ZapProgressState.Seeking, _previewRole, "phase-reset-initialize");

        EnsureSubcomponents();
        if (visuals) visuals.Initialize();
        if (motion) motion.Initialize(behaviorProfile, this);
        if (cravingNavigator) cravingNavigator.Initialize(this, behaviorProfile);
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
        else
        {
            EnterDormantWaitState();
            LogState("Initialized+DormantWait");
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

        float ready01 = _zapProgressState == ZapProgressState.ReadyLatched ? 1f : GetChargeNormalized01(_previewRole);
        Color gray = Color.Lerp(Color.gray, Color.lightGray, 0.65f);
        Color c = Color.Lerp(gray, _previewColor, ready01);
        c.a = 1f;
        return c;
    }

    void Update()
    {
        float dt = Time.deltaTime;

        UpdateStateRecovery();

        _accumulatorRotAngle += accumulatorRotSpeed * dt;

        // Compute diamond rotation angles and lock state for this frame.
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

        EnsureDormantSeedVisuals();
        UpdateDominantRole(dt);
        UpdateChargeVisuals(rotA, aLocked, rotB, bLocked);
    }

    // Handles latched-but-dormant recovery and pending-activation finalization each frame.
    private void UpdateStateRecovery()
    {
        if (_state == PhaseStarState.Dormant &&
            _zapProgressState == ZapProgressState.ReadyLatched &&
            !_pendingDormantActivation &&
            !_coordinatorLockOwnedByOtherStar)
        {
            TransitionDormantToActive();
        }

        if (_pendingDormantActivation && (dust == null || !dust.HasActiveTentacles))
            FinalizeDormantToActiveAfterRetract();
    }

    // Tracks which role currently has the highest charge, refreshes the planned ejection
    // descriptor when the dominant role or its track changes, and updates the charge display lerp.
    private void UpdateDominantRole(float dt)
    {
        if (GetDominantRoleRaw(out var dominantRole, out _, out _))
        {
            if (dominantRole != _previewRole)
            {
                _previewRole = dominantRole;
                _previewColor = ResolveRoleColor(dominantRole);
                _cachedTrack = FindTrackByRole(dominantRole);
                if (zappedCount == 0)
                    TryRefreshRequiredZapCountForPlannedRole(dominantRole, _cachedTrack, resetCurrentZapCount: false, reason: "dominant-role-switch");
                visuals?.ResetDualDiamondVisualState();
            }
            else
            {
                InstrumentTrack latestTrack = FindTrackByRole(dominantRole);
                bool trackChanged = !ReferenceEquals(latestTrack, _plannedEjectionDescriptor.track);
                if (trackChanged)
                {
                    _cachedTrack = latestTrack;
                    if (zappedCount == 0)
                        TryRefreshRequiredZapCountForPlannedRole(dominantRole, latestTrack, resetCurrentZapCount: false, reason: "track-availability-change");
                }
            }

            _displayedCharge01 = Mathf.Lerp(_displayedCharge01, ZapProgress01, dt * chargeDisplayLerpSpeed);
        }
        else
        {
            _previewRole = MusicalRole.None;
            _displayedCharge01 = Mathf.Lerp(_displayedCharge01, 0f, dt * chargeDisplayLerpSpeed);
        }
    }

    // Drives diamond rendering and the charge-scale/body-color visuals each frame.
    // Must be called after UpdateDominantRole() and after diamond rotation angles are computed.
    // Note: UpdateDualDiamonds resets diamond localScale to 1, so the charge-scale block
    // below must run after it to win for the Dormant phase.
    private void UpdateChargeVisuals(float rotA, bool aLocked, float rotB, bool bLocked)
    {
        bool dominantReady = IsEjectionReady();

        if (_previewVisual != null && _disarmReason != PhaseStarDisarmReason.SiblingActive)
        {
            visuals?.UpdateDualDiamonds(
                _previewColor,
                _displayedCharge01,
                rotA, aLocked,
                rotB, bLocked,
                dominantReady,
                readyRotSpeedMul);
        }

        // Scale 0→1 as charge builds. _previewVisual and _previewVisualB are children of
        // PhaseStar (not of visuals), so they must be scaled explicitly here.
        // Sqrt curve: front-loads growth so small charge values are perceptible.
        // e.g. 10% charge → 32% scale, 25% charge → 50% scale, 100% → 100%.
        if ((_state == PhaseStarState.Dormant ||
             (_state == PhaseStarState.WaitingForPoke && ZapProgress01 < 1f))
            && !_burstOffScreen)
        {
            float visualScale01 = Mathf.Max(dormantSeedScale, Mathf.Sqrt(_displayedCharge01));
            if (dust != null && dust.HasActiveTentacles)
                visualScale01 = Mathf.Max(visualScale01, tentacleBloomMinScale);

            Vector3 chargeScale = Vector3.one * visualScale01;
            if (visuals != null) visuals.transform.localScale = chargeScale;
            if (_previewVisual != null)  _previewVisual.localScale  = chargeScale;
            if (_previewVisualB != null) _previewVisualB.localScale = chargeScale;

            if (visualScale01 > 0.001f && _disarmReason != PhaseStarDisarmReason.SiblingActive)
            {
                visuals?.ToggleShardRenderers(true);
                Color roleColor = _previewRole != MusicalRole.None ? _previewColor : Color.gray;
                visuals?.LerpBodyColor(roleColor, _displayedCharge01);
            }
        }
        else if (visuals != null && !_burstOffScreen && _disarmReason != PhaseStarDisarmReason.SiblingActive)
        {
            Color bodyColor = _previewRole != MusicalRole.None ? _previewColor : Color.gray;
            visuals.LerpBodyColor(bodyColor, _displayedCharge01);
        }
    }
    private void EnsureDormantSeedVisuals()
    {
        if (visuals == null) return;
        if (_state != PhaseStarState.Dormant) return;
        if (_burstOffScreen) return;
        if (_disarmReason == PhaseStarDisarmReason.SiblingActive) return;

        if (!_dormantSeedVisualPrimed)
        {
            visuals.ShowDim(ResolvePreviewColorByReadiness());
            _dormantSeedVisualPrimed = true;
        }

        float minScale = Mathf.Max(0f, dormantSeedScale);
        if (visuals.transform.localScale.x < minScale)
            visuals.transform.localScale = Vector3.one * minScale;
    }

    private void SafeUnsubscribeAll()
    {
        if (dust != null && _dustDeliveryWired)
        {
            dust.onDelivery -= OnDustDelivery;
            dust.OnAllTentaclesRetracted -= OnAllTentaclesRetracted;
            _dustDeliveryWired = false;
        }

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
        ResolveGameFlowManager();
        var tc = _gfm != null ? _gfm.controller : null;
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
    
    private bool AnyCollectablesInFlightGlobal()
    {
        var gfm = ResolveGameFlowManager();
        bool unified = gfm != null && gfm.AnyCollectablesInFlightGlobal();
        bool legacy = LegacyAnyCollectablesInFlightGlobal(gfm);

        if (legacy != unified)
            Debug.LogWarning($"[ASSERT:CIF] PhaseStar mismatch legacy={legacy} unified={unified} star={name}");

        return unified;
    }

    private static bool LegacyAnyCollectablesInFlightGlobal(GameFlowManager gfm)
    {
        if (gfm == null || gfm.controller == null || gfm.controller.tracks == null)
            return false;
        foreach (var t in gfm.controller.tracks)
        {
            if (t == null) continue;
            if (t.spawnedCollectables != null)
                t.spawnedCollectables.RemoveAll(go => go == null || !go.activeInHierarchy);
            if (t.spawnedCollectables != null && t.spawnedCollectables.Count > 0)
                return true;
        }
        return false;
    }
    
    private void ArmNext()
    {
        _stateController ??= new PhaseStarStateController();
        if (!_stateController.CanArm(BuildInteractionSnapshot())) return;

        if (AnyCollectablesInFlightGlobal())
        {
            Disarm(PhaseStarDisarmReason.CollectablesInFlight);
            return;
        }

        // Mirror ShouldDisarmForGlobalGates(): a fully-charged star is exempt from the
        // expansion-pending gate. Blocking it here would leave it permanently un-armable
        // while expansion is active (the loop boundary's "EP + ready = hold armed" guard
        // also returns early, so the re-arm path in section 3 is never reached).
        if (AnyExpansionPendingGlobal() && ZapProgress01 < 1f)
        {
            DBG("ArmNext: blocked by ExpansionPending -> Disarm:ExpansionPendingGlobal");
            Disarm(PhaseStarDisarmReason.ExpansionPending);
            return;
        }

        _disarmReason = PhaseStarDisarmReason.None;
        _isArmed = true;

        DeactivateSafetyBubble();

        EnableColliders();
        dust?.SetTentaclesActive(false);

        motion?.SetOverrideTarget(null);
        motion?.SetSpeedMultiplier(1f);

        if (visuals != null && ZapProgress01 >= 1f)
            visuals.transform.localScale = Vector3.one;
        SetVisual(VisualMode.Bright, ResolvePreviewColorByReadiness());
        OnArmed?.Invoke(this);
    }
    public void Pause()
    {
        Disarm(PhaseStarDisarmReason.SiblingActive);
        motion?.SetFrozen(true);
    }

    public void Resume()
    {
        if (_isDisposing) return;
        _disarmReason = PhaseStarDisarmReason.None;
        if (_burstOffScreen)
        {
            if (TryExitBurstHidden())
                motion?.Enable(true);
            EnterDormantWaitState();
            return;
        }
        motion?.SetFrozen(false);
        motion?.Enable(true);
        if (IsEjectionReady())
        {
            ArmNext();
            return;
        }

        // When resumed after a sibling MineNode flow, a non-ready dormant star must
        // re-enter dormant wait so tentacle acquisition restarts. Showing dim alone
        // leaves tentacles disabled and the star appears stuck despite valid dust.
        if (_state == PhaseStarState.Dormant)
        {
            EnterDormantWaitState();
            return;
        }

        if (!_isArmed)
        {
            ArmNext();
            if (!_isArmed)
                EnterDormantWaitState();
            return;
        }

        else
            visuals?.ShowDim(ResolvePreviewColorByReadiness());
    }

    public void PreAttuneTo(MusicalRole role)
    {
        if (role == MusicalRole.None || _attunedRole != MusicalRole.None) return;
        OnAttuned_SetRole(role);
    }

    // Hide the star in place for the duration of a burst — it stays at its world position.
    // Guarded — safe to call repeatedly; only executes on the first call per burst.
    private void HideInPlaceForBurst()
    {
        if (!TryEnterBurstHidden()) return;

        StopManagedCoroutine(ref _waitForDustCo);

        // Stay at current world position — do NOT teleport off-screen.
        motion?.Enable(false);
        motion?.SetFrozen(true);
        visuals?.HideAll();
        dust?.SetTentaclesActive(false);
        DisableColliders();
        Debug.Log($"[PhaseStar] Burst in flight — hidden in place at {transform.position}");
    }
    
    private void RelocateToAvailableCell()
    {
        var drum = TryResolveContext(out var resolvedDrum, out _) ? resolvedDrum : null;
        if (drum == null) return;
        Vector2Int cell = drum.GetRandomAvailableCell();
        if (cell.x < 0) return;
        Vector2 worldPos = drum.GridToWorldPosition(cell);
        transform.position = (Vector3)worldPos + Vector3.forward * transform.position.z;
    }
    
    private void Disarm(PhaseStarDisarmReason reason, Color? tintOverride = null)
    {
        _isArmed = false;
        _disarmReason = reason;
        Debug.Log($"[PhaseStar] Disarm reason={reason} star={name}");

        DisableColliders();

        // CollectablesInFlight: move off-screen for the burst duration.
        if (reason == PhaseStarDisarmReason.CollectablesInFlight)
            HideInPlaceForBurst();

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
            bool hideCompletely = reason == PhaseStarDisarmReason.NodeResolving
                               || reason == PhaseStarDisarmReason.AwaitBridge
                               || nodeIsAlive;
            SetVisual(hideCompletely ? VisualMode.Hidden : VisualMode.Dim,
                      tintOverride ?? ResolvePreviewColorByReadiness());
        }

        OnDisarmed?.Invoke(this);
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
            $"awaitClr={_awaitingCollectableClear} " +
            $"activeNode={(_activeNode ? _activeNode.name : null)} ejectInFlight={_ejectionInFlight} CIF={cif} EP={ep}");

        if (_isDisposing || this == null) return;
        if (_disarmReason == PhaseStarDisarmReason.SiblingActive) return;

        // ------------------------------------------------------------
        // 1) Awaiting collectable clear (post-node-resolution latch)
        // ------------------------------------------------------------
        if (_awaitingCollectableClear)
        {
            if (AnyCollectablesInFlightGlobal() && (_activeNode != null || _activeSuperNode != null || _ejectionInFlight))
            {
                Debug.Log("[PS:LB/AWAIT] -> stay disarmed (awaitClr + CIF + active node)");
                Disarm(PhaseStarDisarmReason.NodeResolving, _lockedTint);
                return;
            }

            ResolveGameFlowManager();
            var drums = _drum != null ? _drum : _gfm?.activeDrumTrack;

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
                Disarm(PhaseStarDisarmReason.NodeResolving, _lockedTint);
                return;
            }

            Debug.LogWarning($"[PhaseStar][Timeout] AwaitingCollectableClear timed out. Forcing recovery. star={name}");

            _awaitingCollectableClear = false;
            _awaitingCollectableClearSinceLoop = -1;
            _awaitingCollectableClearSinceDsp = -1.0;

            if (!CanAdvancePhaseNow())
            {
                Disarm(PhaseStarDisarmReason.NodeResolving, _lockedTint);
                return;
            }

            Debug.Log($"[PS:LB] Recovery -> Dormant wait");
            EnterDormantWaitState();
            return;
        }

        // ------------------------------------------------------------
        // 2) Global gate checks
        // ------------------------------------------------------------
        bool anyCollectables = AnyCollectablesInFlightGlobal();
        bool anyExpansion = AnyExpansionPendingGlobal();
        bool shouldDisarmForGate = _stateController?.ShouldDisarmForGlobalGates(BuildInteractionSnapshot(anyCollectables, anyExpansion)) ?? false;

        if (shouldDisarmForGate)
        {
            if (anyCollectables)
                Debug.Log($"[PS:LB] AnyCollectablesInFlightGlobal True");
            else
                Debug.Log($"[PS:LB] Any Expanding Global True");

            Disarm(anyCollectables ? PhaseStarDisarmReason.CollectablesInFlight : PhaseStarDisarmReason.ExpansionPending, _lockedTint);
            return;
        }

        // Only hold when already armed. Without the _isArmed check, a fully-charged
        // but un-armed star (e.g. just resumed after a sibling's cycle) would skip section
        // 3 and never reach ArmNext().
        if (anyExpansion && ZapProgress01 >= 1f && _isArmed)
        {
            Debug.Log($"[PS:LB] EP true but star is ready — holding armed");
            return;
        }

        LogState("LoopBoundary entry");

        // ------------------------------------------------------------
        // 3) Normal re-arm path
        // ------------------------------------------------------------
        if (!_isArmed)
        {
            // Stale burst-hide: all global gates cleared, so _burstOffScreen from a prior
            // CIF-triggered hide is safe to reset. Restore motion before arming.
            // This must run before the WaitingForPoke branch so CanArm() sees BurstOffScreen=false.
            if (_burstOffScreen)
            {
                if (TryExitBurstHidden())
                {
                    motion?.Enable(true);
                    motion?.SetFrozen(false);
                }
            }

            DBG("[PS:LB] -> re-arm");
            if (_state == PhaseStarState.WaitingForPoke)
            {
                ArmNext();
            }
            else
            {
                // Only re-enter dormant wait if not already committed to a retract/latch sequence.
                // EnterDormantWaitState() calls SetTentaclesActive(true), which re-enables
                // acquisition and starts a new grow/drain/retract cycle — preventing
                // ReadyLatched from ever being reached until all matching dust is exhausted.
                // The recovery guard in UpdateStateRecovery() and OnAllTentaclesRetracted()
                // will advance the star once retractions finish.
                bool retractOrLatchInProgress =
                    _zapProgressState == ZapProgressState.WaitingForRetract ||
                    _zapProgressState == ZapProgressState.ReadyLatched ||
                    _pendingDormantActivation;

                if (retractOrLatchInProgress)
                {
                    DBG("[PS:LB] -> retract/latch committed, skip dormant re-enter");
                    return;
                }

                EnterDormantWaitState();
            }
        }
        else
        {
            // If thresholds changed (e.g. motif/phase swap) while this star stayed armed,
            // we can end up armed-but-not-ejectable with tentacles off, which deadlocks poke flow.
            // Drop back into dormant so charge collection can resume.
            if (!HasDominantRoleEjectable())
            {
                DBG("[PS:LB] armed but dominant role not ejectable -> returning to dormant");
                Disarm(PhaseStarDisarmReason.None, _lockedTint);
                EnterDormantWaitState();
                return;
            }

            DBG("[PS:LB] -> No need to arm");
        }
    }
    
    private void DBG(string msg)
    {
        Debug.Log($"[PSDBG] {msg} :: star={name} state={_state} interaction=({_interactionState.ToDebugString()}) " +
                  $"preview={(_previewVisual != null ? 1 : 0)} " +
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
        ResolveGameFlowManager();
        var drum = _gfm?.activeDrumTrack;
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
        NoteSet planned = ResolvePlannedNoteSet(track);
        
        // SuperNode only when the track is fully expanded AND repeating the same NoteSet adds no new coverage.
        _cachedIsSuperNode = (planned != null && _cachedTrack != null) && ShouldSpawnSuperNodeForTrack(_cachedTrack);
        PrimeZapRequirementForRole(role, track);
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

    if (!_isArmed && (_disarmReason == PhaseStarDisarmReason.NodeResolving || _disarmReason == PhaseStarDisarmReason.AwaitBridge))
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

    if (!_isArmed && (_disarmReason == PhaseStarDisarmReason.NodeResolving || _disarmReason == PhaseStarDisarmReason.AwaitBridge))
        srB.enabled = false;

    _previewVisualB = goB.transform;

    _buildingPreview = false;

    visuals?.InvalidateShardCache();
    visuals?.BindDualDiamondRenderers(_previewVisual, _previewVisualB);
    visuals?.ResetDualDiamondVisualState();
}
    private InstrumentTrack FindTrackByRole(MusicalRole role)
    {
        ResolveGameFlowManager();
        var controller = _gfm?.controller;
        if (controller == null || controller.tracks == null) return null;

        foreach (var t in controller.tracks)
            if (t != null && t.assignedRole == role)
                return t;

        return null;
    }
    private MusicalRole GetPlannedRoleForHighlightedShard()
    {
        // Stable role identity per star: once attuned, always plan/eject for that role.
        if (_attunedRole != MusicalRole.None) return _attunedRole;
        if (_previewRole != MusicalRole.None) return _previewRole;

        // Fallback must come from motif-authored active roles, not a hardcoded musical role.
        // Some motifs intentionally omit Bass.
        var motifRoles = _assignedMotif?.GetActiveRoles();
        if (motifRoles != null)
        {
            for (int i = 0; i < motifRoles.Count; i++)
            {
                var role = motifRoles[i];
                if (role == MusicalRole.None) continue;
                if (FindTrackByRole(role) != null) return role;
            }
        }

        // Last-resort: pick any track role currently available.
        ResolveGameFlowManager();
        var tracks = _gfm?.controller?.tracks;
        if (tracks != null)
        {
            for (int i = 0; i < tracks.Length; i++)
            {
                var t = tracks[i];
                if (t == null || t.assignedRole == MusicalRole.None) continue;
                return t.assignedRole;
            }
        }

        return MusicalRole.None;
    }
    public void SetGravityVoidSafetyBubbleActive(bool active, Vector3 center = default)
    {
        // Safety bubble visuals/gameplay removed.
    }

    public int GetSafetyBubbleRadiusCells() => 0;

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
            Disarm(PhaseStarDisarmReason.CollectablesInFlight, _lockedTint);
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
        if (!IsEjectionReady())
        {
            // Keep descriptor role synchronized to the authoritative dominant role before
            // attempting recovery latch. Prevents ReadyLatched+role=None deadlocks.
            if (GetDominantRoleRaw(out var dominantRoleForDescriptor, out _, out _))
            {
                var dominantTrack = FindTrackByRole(dominantRoleForDescriptor);
                if (dominantTrack != null)
                {
                    TryRefreshRequiredZapCountForPlannedRole(
                        dominantRoleForDescriptor,
                        dominantTrack,
                        resetCurrentZapCount: false,
                        reason: "collision-recovery-refresh");
                }
            }

            // Recovery path: if charge already crossed the dominant-role threshold but
            // zap state did not relatch (e.g. post-node flow edge cases), relatch now
            // so a valid poke still ejects a node.
            if (HasDominantRoleEjectable() && GetDominantRoleRaw(out var dominantRole, out _, out _))
            {
                TransitionZapState(ZapProgressState.ReadyLatched, dominantRole, "collision-recovery-latch");
            }
            else
            {
                Trace("OnCollisionEnter2D: ignored poke — dominant role not ejectable yet");
                return;
            }
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
            Disarm(PhaseStarDisarmReason.NodeResolving, _lockedTint);
            return;

        }
        if (_previewRole != MusicalRole.None)
        {
            if (_disarmReason == PhaseStarDisarmReason.SiblingActive) return;
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
        LastNodeWasEscaped   = false;
        LastNodeWasCaptured  = false;
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
            Disarm(PhaseStarDisarmReason.NodeResolving, spawnTint);
            return;
        }

        _activeNode = node;
        _ejectionInFlight = false;

        bool handledResolve = false;

        node.OnResolved += (_, outcome) =>
        {
            if (ticket != _spawnTicket) return;
            if (handledResolve) return;
            handledResolve = true;

            _activeNode = null;

            LastNodeWasCaptured = outcome == MineNodeOutcome.Captured;
            LastNodeWasEscaped  = outcome == MineNodeOutcome.Escaped;
            LastNodeWasExpired  = outcome == MineNodeOutcome.Expired;

            // Fire before any Unity component access — safe even when the star
            // GameObject was already destroyed by DestroyStarAfterDelay.
            OnMineNodeResolved?.Invoke(this, _attunedRole);

            // Guard Unity component access: star may be destroyed if the player
            // took longer than starExitDuration to kill the node.
            if (this == null) return;

            if (_state == PhaseStarState.BridgeInProgress) return;

            _awaitingCollectableClear = true;
            ResolveGameFlowManager();
            _awaitingCollectableClearSinceLoop = (_drum != null)
                ? _drum.completedLoops
                : (_gfm?.activeDrumTrack?.completedLoops ?? -1);
            _awaitingCollectableClearSinceDsp = AudioSettings.dspTime;
            HideInPlaceForBurst();
            Disarm(PhaseStarDisarmReason.NodeResolving, spawnTint);
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

        Disarm(PhaseStarDisarmReason.NodeResolving, ejectedTrack.trackColor);

        Debug.Log($"[MNDBG] EjectActive: contact={contact}, role={ejectedTrack.assignedRole}");
        TransitionZapState(ZapProgressState.Ejecting, ejectedRole, "spawn-start");
        if (ShouldSpawnSuperNodeForTrack(ejectedTrack))
            SpawnSuperNodeCommon(contact, ejectedTrack);
        else
            SpawnNodeCommon(contact, ejectedTrack);
        if (_activeNode != null || _activeSuperNode != null)
        {
            TransitionZapState(ZapProgressState.Seeking, ejectedRole, "ejection-succeeded");
            dust?.SetAcquisitionEnabled(true, "post-eject-new-cycle");
        }

        // Keep the star live after ejection. NodeResolving + loop-boundary gates control
        // dormancy/rearm while the MineNode is active; disposing here would block that flow.
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
        Color spawnTint = targetTrack != null ? targetTrack.trackColor : Color.white;

        // Build shard list: all MotifProfile role-matched tracks except the initiating (maxed) track.
        var activeRoles = _assignedMotif?.GetActiveRoles() ?? new System.Collections.Generic.List<MusicalRole>();
        var ctrl        = _gfm?.controller;
        var shardTracks = new System.Collections.Generic.List<InstrumentTrack>();
        if (ctrl?.tracks != null)
            foreach (var t in ctrl.tracks)
                if (t != null && t != targetTrack && activeRoles.Contains(t.assignedRole))
                    shardTracks.Add(t);

        var alternateProg = _assignedMotif?.alternateChordProgressionProfile;
        sn.Initialize(soloVoice, _drum, targetTrack, shardTracks, alternateProg);
        go.GetComponent<Explode>()?.SetTint(spawnTint);

        _activeSuperNode = sn;
        sn.OnResolved += () =>
        {
            _activeSuperNode = null;

            // Fire before any Unity component access — safe even when star is destroyed.
            OnMineNodeResolved?.Invoke(this, _attunedRole);

            if (this == null) return;

            if (_state == PhaseStarState.BridgeInProgress) return;
            _awaitingCollectableClear = true;
            ResolveGameFlowManager();
            _awaitingCollectableClearSinceLoop = (_drum != null)
                ? _drum.completedLoops
                : (_gfm?.activeDrumTrack?.completedLoops ?? -1);
            _awaitingCollectableClearSinceDsp = AudioSettings.dspTime;
            HideInPlaceForBurst();
            Disarm(PhaseStarDisarmReason.NodeResolving, spawnTint);
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

        Disarm(PhaseStarDisarmReason.NodeResolving, _cachedTrack.trackColor);
        ActivateSafetyBubble();
        if (_cachedIsSuperNode)
            SpawnSuperNodeCommon(contact, _cachedTrack);
        else
            SpawnNodeCommon(contact, _cachedTrack);

        // Do not mark disposing on ejection; this star continues orchestrating post-node
        // dormant recharge and subsequent tentacle/ready cycles.
        OnEjected?.Invoke(this, _attunedRole);
    }
    private bool ShouldSpawnSuperNodeForTrack(InstrumentTrack track)
    {
        if (track == null) return false;

        int maxBins = Mathf.Max(1, track.maxLoopMultiplier);
        bool fullyExpanded = track.loopMultiplier >= maxBins;

        if (!fullyExpanded)
        {
            Debug.Log(
                $"[SuperNodeGate] NO: not fully expanded. " +
                $"track={track.name} role={track.assignedRole} loopMul={track.loopMultiplier} maxBins={maxBins}"
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
        ResolveGameFlowManager();
        var noteSet = _gfm != null ? _gfm.GenerateNotes(track, entropy) : null;
        if (!TryResolveAuthoritativeZapCount(track.assignedRole, track, out _currentBurstRequiredZaps))
            _currentBurstRequiredZaps = Mathf.Max(1, GetNoteSetNoteCount(noteSet));
 Debug.Log($"[MineNode] Initializing track {track.name} with {track.assignedRole}");
        node.Initialize(track, noteSet, color, cell, diamondSprite: visuals?.diamond);
        return node;
    }
    private bool TryResolveAuthoritativeZapCount(MusicalRole role, InstrumentTrack track, out int noteCount)
    {
        noteCount = 0;
        if (_assignedMotif == null || track == null || role == MusicalRole.None)
            return false;

        int totalBins = Mathf.Max(1, track.maxLoopMultiplier);
        // Zap objective corresponds to authored motif payload for the role.
        var cfg = _assignedMotif.GetConfigForRoleAtBin(role, 0, totalBins);
        if (cfg == null) return false;

        if (cfg.riff != null && cfg.riff.riff.events != null && cfg.riff.riff.events.Count > 0)
        {
            noteCount = cfg.riff.riff.events.Count;
            return true;
        }

        return false;
    }

    private static int GetNoteSetNoteCount(NoteSet noteSet)
    {
        if (noteSet == null) return 0;
        int persistentTemplateCount = noteSet.persistentTemplate != null ? noteSet.persistentTemplate.Count : 0;
        int distinctStepCount = noteSet.GetStepList()?.Distinct().Count() ?? 0;
        int noteListCount = noteSet.GetNoteList()?.Count ?? 0;
        return Mathf.Max(persistentTemplateCount, Mathf.Max(distinctStepCount, noteListCount));
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
            $"[PhaseStar][{where}] state={_state} interaction=({_interactionState.ToDebugString()}) " +
            $"role={targetRole} attunedRole={_attunedRole} charge={GetTotalCharge():0.00}");
    }

    private void ActivateSafetyBubble(Vector3 center = default) { }
    private void DeactivateSafetyBubble() { }
}
    



    
