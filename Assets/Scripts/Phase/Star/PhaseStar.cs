using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
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
    [Tooltip("Shared PhaseStar tuning (Resources/Config/PhaseStarConfig). Unassigned = that asset is loaded.")]
    [SerializeField] private PhaseStarConfig config;
    private PhaseStarConfig _cfg;
    private PhaseStarConfig Cfg => _cfg ??= PhaseStarConfig.OrDefault(config, this);
    // Shared tuning (PhaseStarConfig → 1. Charge & Shards)
    private float disarmedPushScale => Cfg.disarmedPushScale;
    private float accumulatorRotSpeed => Cfg.accumulatorRotSpeed;
    private float drainTiltDeg => Cfg.drainTiltDeg;
    private float drainTiltSpeed => Cfg.drainTiltSpeed;
    private float chargeDisplayLerpSpeed => Cfg.chargeDisplayLerpSpeed;
    private float tentacleActivationDelay => Cfg.tentacleActivationDelay;

    [SerializeField] private GameObject superNodePrefab;  // rainbow shard prefab (collider + visual)
    private SoloVoice soloVoice;                          // runtime cache, resolved via FindAnyObjectByType

    private int CollectableClearTimeoutLoops => behaviorProfile ? behaviorProfile.collectableClearTimeoutLoops : 2;

    private float ReadyRotSpeedMul => behaviorProfile ? behaviorProfile.readyRotSpeedMul : 2.5f;

    [Header("Subcomponents (optional)")] [SerializeField]
    private PhaseStarVisuals2D visuals;

    [SerializeField] private PhaseStarMotion2D motion;
    [SerializeField] private PhaseStarDustAffect dust;

    /// <summary>
    /// The star still needs notes and is on show: its idle tendrils (one per note) are visible.
    /// Covers charging and paused/asleep (dimmed); not armed, ejecting, burst-hidden or blocked.
    /// </summary>
    public bool ShowsHungerTendrils =>
        !_isArmed && !_burstOffScreen && !_isDisposing &&
        _disarmReason != PhaseStarDisarmReason.NodeResolving &&
        requiredZapCount > 0 && requiredZapCount != int.MaxValue &&
        RemainingZapCount > 0;

    /// <summary>Asleep or paused for a sibling: tendrils dim.</summary>
    public bool IsDimmedVisual => _sleepVisual || _disarmReason == PhaseStarDisarmReason.SiblingActive;

    /// <summary>Tutorial callout: makes this star's count buds glow and pulse.</summary>
    public void SetBudHighlight(bool on) { if (dust != null) dust.SetBudHighlight(on); }

    /// <summary>World distance from the star's centre to its idle tendril tips.</summary>
    public float TendrilReachWorld => dust != null ? dust.TendrilReachWorld() : 0.5f;
    [SerializeField] private PhaseStarCravingNavigator cravingNavigator;

    private GameFlowManager _gfm;
    private DrumTrack _drum;
    private bool _subscribedLoopBoundary;
    private Color _lockedTint;
    // Single accumulator shard — flat fields replace the old previewRing list.
    private Color       _previewColor;
    private MusicalRole _previewRole;
    private Transform   _previewVisual;
    private bool _isDisposing;
    private readonly PhaseStarInteractionState _interactionState = new();
    // Cached impact data for the next DiscoveryTrackNode spawn
    Vector2 _lastImpactDir = Vector2.right;
    float _lastImpactStrength = 0f;

    // Optional clamp so crazy physics spikes don't blow things up
    const float MaxImpactStrength = 40f;
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

    // -------------------- State & caches --------------------
    private PhaseStarState _state = PhaseStarState.WaitingForPoke;

    // Accumulator diamond rotation (deg, cumulative). Accumulator rotates at -accumulatorRotSpeed;
    // the scout rotates at +scoutRotSpeed in PhaseStarDustAffect. Opposite signs = opposite directions.

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
    public PhaseStarMotion2D Motion => motion;
    public PhaseStarDustAffect DustAffect => dust;
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

        // Maze-aware navigation: the motif's maze decides HOW the star moves through it; the
        // behavior profile stays the personality (speed/jitter) and is scaled by these.
        // No motif/maze (e.g. interstitial) → defaults = legacy straight-line behavior.
        var maze = _assignedMotif != null ? _assignedMotif.mazePattern : null;
        var nav = maze != null && maze.starNavigation != null ? maze.starNavigation : StarNavigationParams.Default;
        var navMode = nav.ResolveMode(maze != null ? maze.patternType : MazePatternType.FullFill);
        if (maze == null && nav.mode == PhaseStarNavMode.Auto) navMode = PhaseStarNavMode.Direct;
        var navGeom = MazeNavGeometry.Build(maze, drum, GameFlowManager.Instance?.dustGenerator);
        Trace($"Initialize: nav mode={navMode} maze={(maze != null ? maze.name : "none")}");

        EnsureSubcomponents();
        if (visuals) visuals.Initialize();
        if (motion) motion.Initialize(behaviorProfile, this, nav, navMode, navGeom);
        if (cravingNavigator) cravingNavigator.Initialize(this, behaviorProfile, nav, navMode);
        if (motion && cravingNavigator) motion.SetCravingNavigator(cravingNavigator);

        if (dust)
        {
            dust.Initialize(behaviorProfile, this, nav, navMode);
            dust.OnAttuned -= OnAttuned_SetRole; // re-Initialize must not double-subscribe
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

        // Dormant (charging) stars are faint: ships pass through them as a drifting ghost
        // (turbulence + scatter) instead of hitting an easy-to-miss wall. See PhaseStarMotion2D.
        motion?.SetGhostDrift(_state == PhaseStarState.Dormant && !_burstOffScreen && !_isDisposing);

        _accumulatorRotAngle += accumulatorRotSpeed * dt * (_sleepVisual ? kSleepRotationMul : 1f);

        float rotA = ComputeDiamondRotation(dt);

        EnsureDormantSeedVisuals();
        UpdateDominantRole(dt);
        UpdateChargeVisuals(dt, rotA);
        UpdateSleepVisual();
        TickSuperNodeTell(dt);
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

    // Live-star registry: Cosmic Block doorways/watchdog keep every on-screen star reachable
    // (CosmicDustGenerator.Blocks.cs) — a walled-off star could otherwise dead-end the motif.
    private static readonly HashSet<PhaseStar> s_live = new HashSet<PhaseStar>();
    public static IReadOnlyCollection<PhaseStar> Live => s_live;

    /// <summary>True while the star is parked off-screen for a note burst (not a reach target).</summary>
    public bool IsParkedOffScreen => _burstOffScreen;

    private void OnEnable() => s_live.Add(this);

    private void OnDisable()
    {
        s_live.Remove(this);
        SafeUnsubscribeAll();
        CleanupManagedCoroutines();
    }

    private void OnDestroy()
    {
        s_live.Remove(this);
        _isDisposing = true;
        ReleaseUnassignedDrainCells();
        SafeUnsubscribeAll();
        CleanupManagedCoroutines();
        DestroySuperRingMaterial();
    }
}
