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
    WaitingForPoke   = 0
}
public partial class PhaseStar : MonoBehaviour
{
    // -------------------- Serialized config --------------------
    [Header("Profiles & Prefs")]

    [SerializeField] private PhaseStarBehaviorProfile behaviorProfile;

    // -------------------- Profile-driven tuning (authoring surface) --------------------
    // PhaseStar no longer owns duplicated serialized fields for these knobs; they come from PhaseStarBehaviorProfile.
    // Defaults here are used only if behaviorProfile is missing.
    [SerializeField] private GameObject superNodePrefab;  // rainbow shard prefab (collider + visual)
    private SoloVoice soloVoice;                          // runtime cache, resolved via FindAnyObjectByType

    private int CollectableClearTimeoutLoops => behaviorProfile ? behaviorProfile.collectableClearTimeoutLoops : 2;

    private float CollectableClearTimeoutSeconds =>
        behaviorProfile ? behaviorProfile.collectableClearTimeoutSeconds : 0f;

    private float ReadyRotSpeedMul => behaviorProfile ? behaviorProfile.readyRotSpeedMul : 2.5f;

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
    // Cached impact data for the next DiscoveryTrackNode spawn
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
    // Cleared in TryExitBurstHidden() when all burst notes have been committed.
    // All mutations should go through TryEnterBurstHidden() / TryExitBurstHidden()
    // so the coordinator is the single write path. The one exception is the hard
    // reset in EnterInMaze(), which resets all interaction state together.
    private bool _burstOffScreen { get => _interactionState.Interaction.BurstOffScreen; set => _interactionState.Interaction.BurstOffScreen = value; }

    [SerializeField] private bool _tracePhaseStar = true;

    [Header("Shard Visuals (Charge-Alpha)")]
    [Tooltip("Minimum alpha for a shard with zero charge — keeps it ghost-visible.")]
    [SerializeField, Range(0f, 0.5f)] private float shardMinAlpha = 0.08f;



    // -------------------- State & caches --------------------
    private PhaseStarState _state = PhaseStarState.WaitingForPoke;

    // Accumulator diamond rotation (deg, cumulative). Accumulator rotates at -accumulatorRotSpeed;
    // the scout rotates at +scoutRotSpeed in PhaseStarDustAffect. Opposite signs = opposite directions.
    [SerializeField, Range(10f, 360f)] private float accumulatorRotSpeed = 90f;
    [SerializeField] private float drainTiltDeg   = 5f;    // max tilt ± while draining
    [SerializeField] private float drainTiltSpeed  = 1.8f; // oscillation cycles/sec

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
    private int _spawnTicket;
    private int _lastPokeFrame = -999999;
    private InstrumentTrack _cachedTrack;
    private DiscoveryTrackNode _activeNode;
    private SuperNode _activeSuperNode;
    private readonly List<InstrumentTrack> _targets = new(4);
    private MusicalRole _attunedRole = MusicalRole.None;
    public MusicalRole AttunedRole => _attunedRole;
    public bool LastNodeWasSuperNode { get; private set; }
    public bool LastNodeWasExpired   { get; private set; }
    public bool LastNodeWasEscaped   { get; private set; }
    public bool LastNodeWasCaptured  { get; private set; }
    public bool HasLiveEjectionNode => _activeNode != null || _activeSuperNode != null;
    [SerializeField] private MotifProfile _assignedMotif; // optional: motif this star represents (motif system)

    public event Action<PhaseStar> OnDisarmed;
    public event Action<PhaseStar, MusicalRole> OnEjected;
    // Wired by StarPool: asked at poke time (role, isSuperNode) whether this ejection may
    // start. Only one sequence may be in flight at a time — StarPool's burst-identification
    // state is single-slot — and mine ejections additionally need unspent harvest budget.
    // Null (unmanaged star) means always allowed.
    public Func<MusicalRole, bool, bool> CanCommitEjection;
    // Fired when the Vehicle destroys the DiscoveryTrackNode/SuperNode — the burst is now spawning.
    // Safe to fire from a destroyed star (C# delegate, not Unity message).
    public event Action<PhaseStar, MusicalRole> OnMineNodeResolved;
    private bool _isArmed { get => _interactionState.Interaction.IsArmed; set => _interactionState.Interaction.IsArmed = value; }

    // -------------------- Lifecycle --------------------
    void Start()
    {
        _stateController ??= new PhaseStarStateController();
        _burstCoordinator ??= new PhaseStarBurstCoordinator();
        EnsurePreviewRing();
    }

    // Cells drained via ZapClearCellHeld since the last node spawn. The next DiscoveryTrackNode
    // "holds" this batch; it regrows when that node dies. Released directly if the star
    // is destroyed before a node spawns.
    private readonly List<Vector2Int> _heldDrainCells = new();

    public void RegisterHeldDrainCell(Vector2Int cell) => _heldDrainCells.Add(cell);

    private void ReleaseUnassignedDrainCells()
    {
        if (_heldDrainCells.Count == 0) return;
        ResolveGameFlowManager()?.dustGenerator?.ReleaseHeldCells(_heldDrainCells);
        _heldDrainCells.Clear();
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

    private bool _dustDeliveryWired;

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
            if (GameFlowManager.VerboseLogging) Debug.Log($"[PhaseStar] loop={_drum.GetLoopLengthInSeconds():0.##}s  targets={_targets?.Count ?? 0}");

        }

        // Stop any coroutine still running from a previous phase before reinitializing.
        StopManagedCoroutine(ref _waitForDustCo);

        // Reset attunement so the star can re-attune to the new phase's roles.
        _attunedRole = MusicalRole.None;

        // Reset zap-progress display for this new star.
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

        EnterDormantWaitState();
        LogState("Initialized+DormantWait");
    }

    public void PreAttuneTo(MusicalRole role)
    {
        if (role == MusicalRole.None || _attunedRole != MusicalRole.None) return;
        OnAttuned_SetRole(role);
    }

    void Update()
    {
        float dt = Time.deltaTime;

        UpdateStateRecovery();

        _accumulatorRotAngle += accumulatorRotSpeed * dt;

        ComputeDiamondRotation(dt, out float rotA, out bool aLocked, out float rotB, out bool bLocked);

        EnsureDormantSeedVisuals();
        UpdateDominantRole(dt);
        UpdateChargeVisuals(rotA, aLocked, rotB, bLocked);
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
        ReleaseUnassignedDrainCells();
        SafeUnsubscribeAll();
        CleanupManagedCoroutines();
    }
}
