using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
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

    [SerializeField] private PhaseStarBehaviorProfile behaviorProfile;

    // -------------------- Profile-driven tuning (authoring surface) --------------------
    // PhaseStar no longer owns duplicated serialized fields for these knobs; they come from PhaseStarBehaviorProfile.
    // Defaults here are used only if behaviorProfile is missing.

    private bool StarKeepsDustClear => !behaviorProfile || behaviorProfile.starKeepsDustClear;

    private int StarKeepClearRadiusCells =>
        behaviorProfile ? Mathf.Max(0, behaviorProfile.starKeepClearRadiusCells) : 2;

    private bool SafetyBubbleEnabled => !behaviorProfile || behaviorProfile.enableSafetyBubble;
    private int SafetyBubbleRadiusCells => behaviorProfile ? Mathf.Max(0, behaviorProfile.safetyBubbleRadiusCells) : 4;

    private bool RotateSelectionOnLoopBoundary =>
        !behaviorProfile || behaviorProfile.rotateSelectionOnLoopBoundary;

    private int RotateEveryNLoops => behaviorProfile ? Mathf.Max(1, behaviorProfile.rotateEveryNLoops) : 1;

    private int CollectableClearTimeoutLoops => behaviorProfile ? behaviorProfile.collectableClearTimeoutLoops : 2;

    private float CollectableClearTimeoutSeconds =>
        behaviorProfile ? behaviorProfile.collectableClearTimeoutSeconds : 0f;

    // Visual-only: time to fly the node/shard to its grid cell (do not use as a difficulty lever)
    private float NodeFlightSeconds => behaviorProfile ? behaviorProfile.nodeFlightSeconds : 0.65f;

    private bool _previewInitialized;
    private Vector2 lastImpactDirection;

    [Header("Safety / Self-heal")]
    private int _awaitingCollectableClearSinceLoop = -1;

    private double _awaitingCollectableClearSinceDsp = -1.0;

    [Header("Subcomponents (optional)")] [SerializeField]
    private PhaseStarVisuals2D visuals;

    [SerializeField] private PhaseStarMotion2D motion;
    [SerializeField] private PhaseStarDustAffect dust;
    [SerializeField] private float _progressionMul = 1f;
    private List<float> _petalStartAngles = new();
    private List<float> _petalTargetAngles = new();
    private DrumTrack _drum;
    private bool _subscribedLoopBoundary;
    private MusicalPhase _assignedPhase;
    [SerializeField] private bool _isInFocusMode = false;
    private bool _lockPreviewTintUntilIdle;
    private Color _lockedTint;
    private List<PreviewShard> previewRing = new();
    private int currentShardIndex;
    private float beatInterval;
    private float _roleAdvanceInterval;
    private bool _isDisposing;
    private Transform activeShardVisual;
    private bool buildingPreview = false;
    private int _shardsEjectedCount; // how many shards have ejected so far

    private bool _awaitingLoopPhaseFinish;

    // Cached impact data for the next MineNode spawn
    Vector2 _lastImpactDir = Vector2.right;
    float _lastImpactStrength = 0f;

    // Optional clamp so crazy physics spikes don't blow things up
    const float MaxImpactStrength = 40f;
    private bool _awaitingCollectableClear;

    public event Action<MusicalRole, int, int> OnShardEjected;
    [SerializeField] private bool syncRotationToLoop = true;
    [SerializeField] private bool _tracePhaseStar = true;
    private float _loopDuration; // seconds (time authority)
    private int _lastLoopSeen = -1; // loop wrap detector

    [SerializeField] private Color bubbleTint = new Color(1f, 1f, 1f, 1f); // fill/edge tint (alpha handled by visuals)
    [SerializeField] private Color bubbleShardInnerTint = new Color(0.05f, 0.05f, 0.05f, 0.9f);

    private bool _bubbleActive;
    private float _bubbleRadiusWorld;

// Static “global query” (simple + reliable for Vehicle)
    private static bool s_bubbleActive;
    private static Vector2 s_bubbleCenter;
    private static float s_bubbleRadiusWorld;

    public static bool IsPointInsideSafetyBubble(Vector2 worldPos)
    {
        if (!s_bubbleActive) return false;
        return (worldPos - s_bubbleCenter).sqrMagnitude <= (s_bubbleRadiusWorld * s_bubbleRadiusWorld);
    }

    private List<Transform> _petals = new(); // visuals previewRing[i].visual
    private List<float> _baseAngles = new(); // 0..90° spread
    private List<float> _turns = new(); // r[i] = 1/(i+1)
    private List<float> _omega = new(); // deg/sec = 360*r/T_loop

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

    [SerializeField] private float maxActiveDps = 360f; // clamp for comfort

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
    private int _spawnSerial = 0;
    int NextSpawnSerial() => ++_spawnSerial;
    private List<MusicalRole> _phasePlanRoles;
    private int _planConsumeIdx;
    [SerializeField] private MotifProfile _assignedMotif; // optional: motif this star represents (motif system)

    public event Action<PhaseStar> OnArmed;
    public event Action<PhaseStar> OnDisarmed;
    private bool _isArmed;
    private int _baseSortingOrder;
    [SerializeField] private int _perPetalLayerStep;
    [SerializeField] private int _activeTopBoost;
    [SerializeField] private float _spinEnvMul;
    private float _tweenWindow = 2f;
    public MotifProfile AssignedMotif => _assignedMotif;

    private GameFlowManager gfm;
    [SerializeField] private int nowLoop;

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

    public void Initialize(
        DrumTrack drum,
        IEnumerable<InstrumentTrack> targets,
        PhaseStarBehaviorProfile profile,
        MusicalPhase assignedPhase,
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

        _shardsEjectedCount = 0;

        BuildPhasePlan(_assignedPhase, Mathf.Max(1, behaviorProfile.nodesPerStar));
        PrepareNextDirective();
        // ensure subcomponents are present if assigned
        if (!visuals) visuals = GetComponentInChildren<PhaseStarVisuals2D>(true);
        if (!motion) motion = GetComponentInChildren<PhaseStarMotion2D>(true);
        if (!dust) dust = GetComponentInChildren<PhaseStarDustAffect>(true);
        if (visuals) visuals.Initialize(behaviorProfile, this);
        if (motion) motion.Initialize(behaviorProfile, this);
        if (dust) dust.Initialize(behaviorProfile, this);
        if (drum != null && !_subscribedLoopBoundary)
        {
            drum.OnLoopBoundary += OnLoopBoundary_RearmIfNeeded;
            _subscribedLoopBoundary = true;
        }

        ArmNext();
        LogState("Initialized+Armed");
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
                    phase, true
                );
            }
        }

        // rotate all petals continuously with harmonic ladder speeds
        float dt = Time.deltaTime;
        for (int i = 0; i < _petals.Count; i++)
        {
            var t = _petals[i];
            if (!t) continue;
            float dps = _omega.Count > i ? _omega[i] : 0f;
            t.Rotate(Vector3.forward, dps * dt, Space.Self);
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
        DeactivateSafetyBubble();
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

        Debug.Log(
            $"[PS:BURST_CLEARED] awaitClr(after)={_awaitingCollectableClear} -> {(noShardsRemain ? "BeginBridgeNow" : "ArmNext")}"
        );

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
        // If your wiring uses a different access path, swap this for whatever is correct in your project.
        var tc = gfm != null ? gfm.controller : null; // often InstrumentTrackController lives here
        return (tc != null && tc.AnyCollectablesInFlight());
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

        // Optional: also require a clean global gate if you want bridge to be impossible
        // until all collectables/expansions are done (usually desirable).
        if (AnyCollectablesInFlightGlobal())
        {
            DBG("BeginBridgeNow: BLOCKED (collectables in flight)");
            Disarm(DisarmReason.CollectablesInFlight, _lockedTint);
            return;
        }

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
        DBG(
            $"ArmNext: ENTER collectablesInFlight={AnyCollectablesInFlightGlobal()} expansionPending={AnyExpansionPendingGlobal()}");

        if (AnyCollectablesInFlightGlobal())
        {
            DBG("ArmNext: blocked by ExpansionPending -> Disarm:CollectablesInFlight");
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
            DBG("ArmNext: blocked by ExpansionPending -> Disarm:AwaitBridge");
            Disarm(DisarmReason.AwaitBridge);
            return;
        }

        _disarmReason = DisarmReason.None;
        _isArmed = true;
        SetRootPhysicsFrozen(false);
        PrepareNextDirective();
        EnableColliders();
        SetVisual(VisualMode.Bright, ResolvePreviewColor());
        OnArmed?.Invoke(this);
    }

    private void Disarm(DisarmReason reason, Color? tintOverride = null)
    {
        _isArmed = false;
        _disarmReason = reason;
        Debug.Log($"[PhaseStar] Disarm reason={reason} star={name}");
        DisableColliders();
        SetRootPhysicsFrozen(true);

        var tint = tintOverride ?? ResolvePreviewColor();

        switch (reason)
        {
            case DisarmReason.AwaitBridge:
            case DisarmReason.Bridge:
                SetVisual(VisualMode.Hidden, tint);
                break;

            case DisarmReason.NodeResolving:
            case DisarmReason.CollectablesInFlight:
            case DisarmReason.ExpansionPending:
            default:
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
            _lastLoopSeen = drums.completedLoops;
        }
        else
        {
            // Defensive default so preview math never divides by zero
            _loopDuration = 2f;
            _lastLoopSeen = 0;
        }

        // Build/refresh ω[i] from harmonic ladder
        _omega.Clear();
        for (int i = 0; i < _turns.Count; i++)
        {
            float w = (360f * _turns[i]) / _loopDuration; // deg/sec
            _omega.Add(Mathf.Min(maxActiveDps, w));
        }
    }

    private bool CanAdvancePhaseNow()
    {
        // Enforce "no skip capture"
        if (_activeNode != null) return false;
        if (_ejectionInFlight) return false;
        return true;
    }

    public void OnLoopBoundary_RearmIfNeeded()
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
            // If collectables exist, we're legitimately waiting.
            if (AnyCollectablesInFlightGlobal())
            {
                Debug.Log("[PS:LB/AWAIT] -> stay disarmed (awaitClr + CIF)");
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
            && !_awaitingCollectableClear
            && !AnyCollectablesInFlightGlobal()
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

        // --- Selection rotation at loop boundary (agency) ---
        // Only rotate when we are actually waiting for the next poke, i.e. armed + idle.
        if (RotateSelectionOnLoopBoundary && _state == PhaseStarState.WaitingForPoke && _isArmed
            && (RotateEveryNLoops <= 1 || (nowLoop % RotateEveryNLoops) == 0)
            && !_awaitingCollectableClear && !_awaitingLoopPhaseFinish && !_advanceStarted && !_ejectionInFlight &&
            HasShardsRemaining() && !AnyCollectablesInFlightGlobal() && !AnyExpansionPendingGlobal())
        {
            RotateHighlightedShardNow(1);
            DBG($"[PS:LB] Rotated offered shard at boundary. currentShardIndex={currentShardIndex}");
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

    private static int Mod(int x, int m)
    {
        if (m <= 0) return 0;
        int r = x % m;
        return r < 0 ? r + m : r;
    }

    private void RotateHighlightedShardNow(int steps)
    {
        if (previewRing == null || previewRing.Count == 0) return;
        currentShardIndex = Mod(currentShardIndex + steps, previewRing.Count);
        activeShardVisual = previewRing[currentShardIndex].visual;

        // Visual + cached directive must update together (WYSIWYG selection).
        UpdateLayering();
        UpdatePreviewTint();
        HighlightActive();
        PrepareNextDirective();
    }

    void BuildOrRefreshPreviewRing()
    {
        // 1) Collect petal transforms from your PreviewShard list
        _petals.Clear();
        foreach (var shard in previewRing)
            if (shard.visual)
                _petals.Add(shard.visual);

        int N = _petals.Count;
        if (N == 0) return;

        // 2) Base angles (0..90° evenly spaced)
        _baseAngles.Clear();
        float step = 90f / Mathf.Max(1, N - 1);
        for (int i = 0; i < N; i++) _baseAngles.Add(step * i);

        // 3) Harmonic ladder r[i] = 1/(i+1)
        _turns.Clear();
        for (int i = 0; i < N; i++) _turns.Add(1f / (i + 1f));

        // 4) Timing/speeds
        InitializeTimingAndSpeeds();

        // 5) Apply base rotation & deterministic layering
        for (int i = 0; i < N; i++)
        {
            var t = _petals[i];
            if (!t) continue;
            t.localRotation = Quaternion.Euler(0, 0, _baseAngles[i]);
            var sr = t.GetComponent<SpriteRenderer>();
            if (sr) sr.sortingOrder = _baseSortingOrder + (i * _perPetalLayerStep);
        }

        // Put currentShardIndex on top & set highlights/veil
        UpdateLayering();

        if (visuals && activeShardVisual)
        {
            visuals.SetVeilOnNonActive(new Color(1f, 1f, 1f, 0.25f), activeShardVisual);
            visuals.HighlightActive(activeShardVisual, ResolvePreviewColor(), .7f);
        }
    }

    private void EnsurePreviewRing()
    {
        if (_previewInitialized) return;
        _previewInitialized = true;

        RebuildPreviewRingForRemainingShards(keepCurrentIndex:false);
    }

    private void OnDisable()
    {
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

    private int _plannedShardCount = 0;

    private void BuildPhasePlan(MusicalPhase phase, int shardCount)
    {
        _phasePlanRoles = new List<MusicalRole>();
        if (!spawnStrategyProfile) return;

        int target = Mathf.Max(1, shardCount);
        _plannedShardCount = target;

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

        if (_drum == null || spawnStrategyProfile == null) return;

        MusicalRole role = GetPlannedRoleForHighlightedShard();

        InstrumentTrack track = FindTrackByRole(role);
        if (track == null) return;

        _cachedTrack = track;

        if (!_lockPreviewTintUntilIdle)
            UpdatePreviewTint(); // should read from previewRing[currentShardIndex]
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

// These angle arrays are legacy and unsafe once we shrink.
        // If you still have them referenced elsewhere, we can keep them, but they must be resized to match previewRing.
        _petalStartAngles.Clear();
        _petalTargetAngles.Clear();

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

// Prefer the MusicalRoleProfile authority when available.
// Fallback to baked trackColor to preserve legacy behavior.
            Color c   = track != null ? track.trackColor : Color.white;
            Color shadow = track != null ? track.TrackShadowColor : new Color(0.08f,0.08f,0.08f,1f);


 
            var shardGO = new GameObject($"PreviewShard_{i}_{role}");
            shardGO.transform.SetParent(transform);
            shardGO.transform.localPosition = Vector3.zero;
            shardGO.transform.localRotation = Quaternion.Euler(0f, 0f, ang);
            shardGO.transform.localScale = Vector3.one;

            var sr = shardGO.AddComponent<SpriteRenderer>();
            sr.sprite = visuals.diamond;
            sr.color = c;
            sr.sortingOrder = _baseSortingOrder + (i * _perPetalLayerStep);

            previewRing.Add(new PreviewShard
            {
                color = c,
                shadowColor = shadow,
                collected = false,
                visual = shardGO.transform,
                role = role
            });

            // If you still want these for smooth visual transitions, keep them sized to n.
            _petalStartAngles.Add(ang);
            _petalTargetAngles.Add(ang);
        }

        currentShardIndex = Mathf.Clamp(currentShardIndex, 0, previewRing.Count - 1);
        activeShardVisual = previewRing[currentShardIndex].visual;
        buildingPreview = false;

        // Ensure highlight + tint matches the new authoritative shard color
        UpdatePreviewTint();
        HighlightActive();
    }

    void UpdateLayering()
    {
        if ((_petals == null || _petals.Count == 0) && previewRing != null && previewRing.Count > 0)
        {
            _petals = previewRing.Where(s => s.visual).Select(s => s.visual).ToList();
        }

        if (_petals != null && _petals.Count == 0) return;

        for (int i = 0; i < _petals.Count; i++)
        {
            var sr = _petals[i] ? _petals[i].GetComponent<SpriteRenderer>() : null;
            if (!sr) continue;
            if (i == currentShardIndex) // active
                sr.sortingOrder = _baseSortingOrder + (_petals.Count * _perPetalLayerStep);
            else
                sr.sortingOrder = _baseSortingOrder + (i * _perPetalLayerStep);
        }
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
    
    void RemoveShardAt(int idx)
    {
        if (previewRing == null || previewRing.Count == 0) return;
        idx = Mathf.Clamp(idx, 0, previewRing.Count - 1);

        // destroy visual (if still alive)
        var s = previewRing[idx];
        if (s.visual) Destroy(s.visual.gameObject);

        // Remove from preview ring
        previewRing.RemoveAt(idx);

        // IMPORTANT: also remove the matching plan entry so indices remain aligned.
        if (_phasePlanRoles != null && idx >= 0 && idx < _phasePlanRoles.Count)
            _phasePlanRoles.RemoveAt(idx);

        // If empty, clear state
        if (previewRing.Count == 0)
        {
            currentShardIndex = 0;
            activeShardVisual = null;
            return;
        }

        // Snap active index to a valid slot
        currentShardIndex = Mathf.Min(idx, previewRing.Count - 1);
        activeShardVisual = previewRing[currentShardIndex].visual;

        // Re-layer + re-highlight + re-tint
        UpdateLayering();
        UpdatePreviewTint();
        HighlightActive();
    }
    
    void UpdatePreviewTint()
    {
        if (visuals == null) visuals = GetComponentInChildren<PhaseStarVisuals2D>(true);
        if (visuals == null) return;
        var color = ResolvePreviewColor();
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

        Color c = ResolvePreviewColor();
        float highlight = _isArmed ? 0.7f : 0.25f;
        float veilA     = _isArmed ? 0.25f : 0.55f;
        // “Dim means busy” is handled elsewhere; this is just the per-petal highlight.
        visuals.SetVeilOnNonActive(new Color(1f, 1f, 1f, veilA), activeShardVisual);
        visuals.HighlightActive(activeShardVisual, c, highlight);
    }

    private Color ResolvePreviewColor()
    {
        if (previewRing == null || previewRing.Count == 0) return Color.white;
        int idx = Mathf.Clamp(currentShardIndex, 0, previewRing.Count - 1);
        return previewRing[idx].color;
    }
    private Color ResolvePreviewShadowColor()
    {
        if (previewRing == null || previewRing.Count == 0) return new Color(0.08f, 0.08f, 0.08f, 1f);
        int idx = Mathf.Clamp(currentShardIndex, 0, previewRing.Count - 1);
        return previewRing[idx].shadowColor;
    }

    
    private void OnCollisionEnter2D(Collision2D coll)
    {
        if (AnyCollectablesInFlightGlobal())
        {
            // Optional: keep it disarmed while collectables exist, so we don’t accept pokes mid-burst.
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
        if (!coll.gameObject.TryGetComponent<Vehicle>(out _)) return;
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

        if (!_isArmed)
        {
            Trace("OnCollisionEnter2D: ignored poke because star is not armed");
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
            s_bubbleCenter = transform.position;
            EjectActivePreviewShardAndFlow(coll);
            return;
        }

        s_bubbleCenter = transform.position;
        EjectCachedDirectiveAndFlow(coll);
        return;
    }

    void ReseedAfterRemoval()
    {
        // 1) Re-collect petals (N changed)
        _petals.Clear();
        foreach (var shard in previewRing)
            if (shard.visual)
                _petals.Add(shard.visual);
        int N = _petals.Count;

        if (N == 0) return;

        // 2) Capture CURRENT angles as new baselines to avoid pops
        var currentAngles = new List<float>(N);
        for (int i = 0; i < N; i++)
            currentAngles.Add(_petals[i].localEulerAngles.z);

        // 3) Rebuild ladder turns and ω for new N
        _turns.Clear();
        for (int i = 0; i < N; i++) _turns.Add(1f / (i + 1f));
        InitializeTimingAndSpeeds();

        // 4) You can either keep the current angles (no snap) or re-spread to 0..90 and lerp over a short window.
        // Minimal: keep as is, then just fix layering.
        UpdateLayering();
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
        if (previewRing == null || previewRing.Count == 0) return;
        if (!HasShardsRemaining()) return;

        int shardIdx = Mathf.Clamp(currentShardIndex, 0, previewRing.Count - 1);
        var shard = previewRing[shardIdx];

        MusicalRole ejectedRole = shard.role;
        InstrumentTrack ejectedTrack = FindTrackByRole(ejectedRole);
        if (ejectedTrack == null)
        {
            Debug.LogError($"[PhaseStar] Missing track for ejected role={ejectedRole} (cannot spawn node).");
            return;
        }

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
        ActivateSafetyBubble();
        SpawnNodeCommon(contact, ejectedTrack);

        currentShardIndex = Mathf.Clamp(currentShardIndex, 0, Mathf.Max(0, remainingAfter - 1));
        RebuildPreviewRingForRemainingShards(keepCurrentIndex: true);
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

        bool isFinal = (_shardsEjectedCount >= Mathf.Max(1, behaviorProfile.nodesPerStar));
        Disarm(isFinal ? DisarmReason.AwaitBridge : DisarmReason.NodeResolving, _cachedTrack.trackColor);
        ActivateSafetyBubble();
        SpawnNodeCommon(contact, _cachedTrack);
    }

    private MineNode DirectSpawnMineNode(Vector3 spawnFrom, InstrumentTrack track, Color color)
    {
        if (track == null || _drum == null) return null;

        Vector2Int cell = _drum.GetRandomAvailableCell(); // ✅ DrumTrack wrapper
        if (cell.x < 0) return null;
        _drum.OccupySpawnCell(cell.x, cell.y, GridObjectType.Node);
        var worldPos = _drum.GridToWorldPosition(cell);
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

        node.Initialize(track, color, cell); // MineNode will spawn payload and register with DrumTrack, etc.
        return node;
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

        GameFlowManager.Instance?.BeginPhaseBridge(next, null, Color.white);

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

    // Freeze/unfreeze the *root* Rigidbody2D so the star always stops immediately on disarm,
    // even if PhaseStarMotion2D is on a child object or uses a different Rigidbody2D.
    private void SetRootPhysicsFrozen(bool frozen)
    {
        if (_isDisposing || this == null) return;

        var rb = GetComponent<Rigidbody2D>();
        if (!rb) return;

        if (frozen)
        {
            rb.linearVelocity = Vector2.zero;
            rb.angularVelocity = 0f;
            rb.constraints = RigidbodyConstraints2D.FreezeAll;
        }
        else
        {
            rb.constraints = RigidbodyConstraints2D.FreezeRotation;
        }
    }

    private int CountEnabledColliders()
    {
        if (_isDisposing || this == null) return 0;
        int n = 0;
        var cols = GetComponentsInChildren<Collider2D>(true);
        foreach (var c in cols)
            if (c && c.enabled)
                n++;
        return n;
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

        float cell = ComputeCellWorldSize();

        // +0.5f gives the bubble a little breathing room relative to discrete cells
        _bubbleRadiusWorld = (SafetyBubbleRadiusCells + 0.5f) * cell;

        _bubbleActive = true;

        s_bubbleActive = true;
        s_bubbleCenter = transform.position;
        s_bubbleRadiusWorld = _bubbleRadiusWorld;

        if (!visuals) visuals = GetComponentInChildren<PhaseStarVisuals2D>(true);
        visuals?.ShowSafetyBubble(_bubbleRadiusWorld, bubbleTint, bubbleShardInnerTint);
        // While the safety bubble is active, the star should not drift.
        motion?.Enable(false);
        SetRootPhysicsFrozen(true);
    }

    private void DeactivateSafetyBubble()
    {
        _bubbleActive = false;

        s_bubbleActive = false;
        s_bubbleCenter = Vector2.zero;
        s_bubbleRadiusWorld = 0f;

        if (!visuals) visuals = GetComponentInChildren<PhaseStarVisuals2D>(true);
        visuals?.HideSafetyBubble();
    }

    /// <summary>
    /// True if worldPos is inside any active PhaseStar safety bubble (Chebyshev radius in grid cells).
    /// </summary>
    public static bool IsWorldPosInsideAnySafetyBubble(Vector2 worldPos, MusicalPhase phase)
    {
        // Legacy compatibility shim: we now use a single static world-radius bubble.
        return IsPointInsideSafetyBubble(worldPos);
    }

}
    



    