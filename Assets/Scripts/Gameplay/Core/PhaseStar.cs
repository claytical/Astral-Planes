using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Gameplay.Mining;
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
    public bool collected;
    public Transform visual;

    public WeightedMineNode plan;   // NEW: the planned node for this petal
    public MusicalRole role;        // NEW: cached for convenience
}

public class PhaseStar : MonoBehaviour
{
    // -------------------- Serialized config --------------------
    [Header("Profiles & Prefs")]
    [SerializeField] private SpawnStrategyProfile spawnStrategyProfile;
    [SerializeField] private PhaseStarBehaviorProfile behaviorProfile;
    private bool _previewInitialized;
    [Header("Movement & Timing")]
    [SerializeField] private float shardFlightSeconds = 0.65f; // time to fly the node to its grid cell
    private Vector2 lastImpactDirection;
    [Header("Dust / Space")]
    [Tooltip("If enabled, the PhaseStar force-clears a small pocket of dust around itself (temporary), ensuring maneuvering space.")]
    [SerializeField] private bool starKeepsDustClear = true;

    [Tooltip("Radius in grid cells for the PhaseStar maneuvering pocket.")]
    [SerializeField] private int starKeepClearRadiusCells = 2;
    [Header("Safety / Self-heal")] 
    [Tooltip("If a MineNode resolves but no collectable burst ever materializes (or the last burst never reports cleared), " + "PhaseStar can become stuck in a waiting state. This is a self-heal timeout (in loop boundaries).")] 
    [SerializeField] private int collectableClearTimeoutLoops = 2;
    [Tooltip("Secondary timeout in seconds (DSP time). Used if loop counter is unavailable. Set <= 0 to disable.")]
    [SerializeField] private float collectableClearTimeoutSeconds = 0f;
    private int _awaitingCollectableClearSinceLoop = -1;
    private double _awaitingCollectableClearSinceDsp = -1.0;
    [Header("Subcomponents (optional)")]
    [SerializeField] private PhaseStarVisuals2D visuals;
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
    private int  _shardsEjectedCount;                   // how many shards have ejected so far
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
    private float _loopDuration;              // seconds (time authority)
    private int   _lastLoopSeen = -1;         // loop wrap detector

    private List<Transform> _petals = new();  // visuals previewRing[i].visual
    private List<float> _baseAngles = new();  // 0..90° spread
    private List<float> _turns = new();       // r[i] = 1/(i+1)
    private List<float> _omega = new();       // deg/sec = 360*r/T_loop
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
    private enum VisualMode { Bright, Dim, Hidden }
    private bool _ejectionInFlight;
    private bool _advanceStarted;
    private int  _spawnTicket;
    private Coroutine _retryCo;
    private int _lastPokeFrame = -999999;
    private MinedObjectSpawnDirective _cachedDirective;
    private InstrumentTrack _cachedTrack;
    private MineNode _activeNode;
    private readonly List<InstrumentTrack> _targets = new(4);
    private int _spawnSerial = 0;
    int NextSpawnSerial() => ++_spawnSerial;
    private List<WeightedMineNode> _phasePlan;
    private int _planConsumeIdx;
    [SerializeField]
    private MotifProfile _assignedMotif;   // optional: motif this star represents (motif system)

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
    // -------------------- Lifecycle --------------------
    void Start()
    {
        gfm   = GameFlowManager.Instance;
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
            .Select(t => t == null ? "null" : t.assignedRole.ToString())  // <-- note the ()
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
        if (_drum != null) {
            _drum.SetBinCount(1); 
            WireBinSource(_drum);
            Debug.Log($"[PhaseStar] loop={_drum.GetLoopLengthInSeconds():0.##}s  targets={_targets?.Count ?? 0}");
            
        }
        spawnStrategyProfile?.ResetForNewStar();

        BuildPhasePlan(_assignedPhase);
        PrepareNextDirective();
         // ensure subcomponents are present if assigned
        if (!visuals) visuals = GetComponentInChildren<PhaseStarVisuals2D>(true);
        if (!motion)  motion  = GetComponentInChildren<PhaseStarMotion2D>(true);
        if (!dust)    dust    = GetComponentInChildren<PhaseStarDustAffect>(true);
        if (visuals) visuals.Initialize(behaviorProfile, this); 
        if (motion)  motion.Initialize(behaviorProfile, this);
        if (dust)    dust.Initialize(behaviorProfile, this);
        if (drum != null && !_subscribedLoopBoundary) { 
            drum.OnLoopBoundary += OnLoopBoundary_RearmIfNeeded; 
            _subscribedLoopBoundary = true;
        }
        ArmNext(); 
        LogState("Initialized+Armed");
    }
    void Update()
    {
        if (starKeepsDustClear)
        {
            

            if (gfm.dustGenerator != null && gfm.activeDrumTrack != null)
            {
                var phase = gfm.phaseTransitionManager != null ? gfm.phaseTransitionManager.currentPhase : _assignedPhase;
                gfm.dustGenerator.SetStarKeepClear(gfm.activeDrumTrack.WorldToGridPosition(transform.position), starKeepClearRadiusCells, phase);
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
            // diamonds repeat visually every 180°, but you can leave wrap to Unity’s euler (harmless)
        }

        // loop wrap → switch shard exactly at the boundary
        var drums = GameFlowManager.Instance?.activeDrumTrack;
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
    bool AnyExpansionPendingGlobal() { 
        var gfm = GameFlowManager.Instance; 
        var tc = gfm != null ? gfm.controller : null; 
        return (tc != null && tc.AnyExpansionPending());
    }
    
    private void SetVisual(VisualMode mode, Color tint) {
        if (!visuals) visuals = GetComponentInChildren<PhaseStarVisuals2D>(true);
        if (!visuals) return;
        switch (mode)
        {
           case VisualMode.Bright:  visuals.ShowBright(tint); break;
            case VisualMode.Dim:     visuals.ShowDim(tint);    break;
            case VisualMode.Hidden:  visuals.HideAll();        break;
        }
    } 
    public void NotifyCollectableBurstCleared()
    {
        _awaitingCollectableClear = false;
        _awaitingCollectableClearSinceLoop = -1; 
        _awaitingCollectableClearSinceDsp  = -1.0;

        bool noShardsRemain =
            (previewRing == null || previewRing.Count == 0) ||
            (_shardsEjectedCount >= Mathf.Max(1, behaviorProfile.nodesPerStar));

        // Final shard: start bridge immediately on last collected note.
        if (noShardsRemain)
        {
            BeginBridgeNow();
            return;
        }

        // Shards remain: we are no longer busy, so the star should become hittable again.
        // This must restore visuals + colliders deterministically.
        ArmNext();
    }

    private bool AnyCollectablesInFlightGlobal()
    {
        var gfm = GameFlowManager.Instance;
        // If your wiring uses a different access path, swap this for whatever is correct in your project.
        var tc = gfm != null ? gfm.controller : null; // often InstrumentTrackController lives here
        return (tc != null && tc.AnyCollectablesInFlight());
    }
    private void BeginBridgeNow() {
        if (_advanceStarted) return; 
        _advanceStarted = true; 
        _awaitingLoopPhaseFinish = false; 
        _state = PhaseStarState.BridgeInProgress; 
        Disarm(DisarmReason.Bridge, _lockedTint); 
        StartCoroutine(CompleteAndAdvanceAsync()); 
    }
    private void ArmNext()
    {
        if (AnyCollectablesInFlightGlobal()) { Disarm(DisarmReason.CollectablesInFlight); return; }
        if (AnyExpansionPendingGlobal())    { Disarm(DisarmReason.ExpansionPending);    return; }
        if (!HasShardsRemaining())          { Disarm(DisarmReason.AwaitBridge); return; }

        _disarmReason = DisarmReason.None;
        _isArmed = true;
        PrepareNextDirective();
        EnableColliders();
        SetVisual(VisualMode.Bright, ResolvePreviewColor());
        OnArmed?.Invoke(this);
    }
    private void Disarm(DisarmReason reason, Color? tintOverride = null)
    {
        _isArmed = false;
        _disarmReason = reason;

        DisableColliders();

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
    public void OnLoopBoundary_RearmIfNeeded()
    {
        if (_isDisposing || this == null) return; 
        if (_awaitingCollectableClear)
        {
         // If collectables exist, we're legitimately waiting.
            if (AnyCollectablesInFlightGlobal()) {
                Disarm(DisarmReason.NodeResolving, _lockedTint);
                return;
            }
            // No collectables in flight, but we're still "awaiting" → this is the deadlock case.
            // Self-heal after a short timeout.
            var drums = _drum != null ? _drum : (GameFlowManager.Instance != null ? GameFlowManager.Instance.activeDrumTrack : null);

            bool timedOut = false;

  /*
   if (collectableClearTimeoutLoops > 0 && drums != null) {
                int nowLoop = drums.completedLoops;
                if (_awaitingCollectableClearSinceLoop < 0)
                    _awaitingCollectableClearSinceLoop = nowLoop;

                int waitedLoops = nowLoop - _awaitingCollectableClearSinceLoop;
                if (waitedLoops >= collectableClearTimeoutLoops)
                    timedOut = true;
            }
*/
  Debug.Log($"[PhaseStar][Await] nowLoop={(drums!=null?drums.completedLoops:-1)} " +
            $"sinceLoop={_awaitingCollectableClearSinceLoop} " +
            $"waited={(drums!=null && _awaitingCollectableClearSinceLoop>=0 ? drums.completedLoops - _awaitingCollectableClearSinceLoop : -1)} " +
            $"loopsTimeout={collectableClearTimeoutLoops} " +
            $"dspNow={AudioSettings.dspTime:F2} sinceDsp={_awaitingCollectableClearSinceDsp:F2} secTimeout={collectableClearTimeoutSeconds:F2} " +
            $"collectablesInFlight={AnyCollectablesInFlightGlobal()} activeNode={( _activeNode!=null)}");

            if (!timedOut && collectableClearTimeoutSeconds > 0f) {
                double nowDsp = AudioSettings.dspTime;
                if (_awaitingCollectableClearSinceDsp < 0.0)
                    _awaitingCollectableClearSinceDsp = nowDsp;

                if ((nowDsp - _awaitingCollectableClearSinceDsp) >= collectableClearTimeoutSeconds)
                    timedOut = true;
            }

            if (!timedOut) {
                Disarm(DisarmReason.NodeResolving, _lockedTint);
                return;
            }

            Debug.LogWarning(
                $"[PhaseStar][Timeout] AwaitingCollectableClear timed out but no collectables are in flight. " +
                $"Forcing recovery. star={name} shardsEjected={_shardsEjectedCount}/{behaviorProfile.nodesPerStar}");

            // Force-clear waiting state.
            _awaitingCollectableClear = false;
            _awaitingCollectableClearSinceLoop = -1;
            _awaitingCollectableClearSinceDsp  = -1.0;

            // Decide what to do next.
            bool noShardsRemain =
                (previewRing == null || previewRing.Count == 0) ||
                (_shardsEjectedCount >= Mathf.Max(1, behaviorProfile.nodesPerStar));

            if (noShardsRemain)
            {
                BeginBridgeNow();
                return;
            }

            ArmNext();
            return;
        }

        if (AnyCollectablesInFlightGlobal())
        {
            Disarm(DisarmReason.CollectablesInFlight, _lockedTint);
            return;
        }
        if (AnyExpansionPendingGlobal()) { 
            Disarm(DisarmReason.ExpansionPending, _lockedTint); 
            return;
        }        
        Trace("OnLoopBoundary_RearmIfNeeded()"); 
        LogState("LoopBoundary entry");
                // If we already started the bridge, ignore further loop boundaries.
        if (_advanceStarted) return;
                // If we're in BridgeInProgress but didn't kick the coroutine yet, do it now once.
        if (_state == PhaseStarState.BridgeInProgress) {
            _advanceStarted = true; 
            _awaitingLoopPhaseFinish = false; // consume any stray await flags
            Trace("LoopBoundary → Bridge was in-progress, starting CompleteAndAdvanceAsync()");
            StartCoroutine(CompleteAndAdvanceAsync()); return;
        }
        // Normal path: final burst resolved; begin the bridge exactly once.
        if (_awaitingLoopPhaseFinish) {
            _advanceStarted = true;           // guard reentry
            _awaitingLoopPhaseFinish = false; // consume the await
            _state = PhaseStarState.BridgeInProgress; 
            Disarm(DisarmReason.Bridge, _lockedTint); 
            Trace("LoopBoundary → Begin bridge"); 
            StartCoroutine(CompleteAndAdvanceAsync()); 
            return;
        }     
        

        // Normal re-arm path when not waiting for bridge.
        if (!_isArmed)
        {
            // If the plan is fully completed, stay quiet and let the bridge path take over.
            if (_shardsEjectedCount >= behaviorProfile.nodesPerStar && behaviorProfile.nodesPerStar > 0)
                return;

            // Re-arm for the next poke
            ArmNext();
        }
    }
    void BuildOrRefreshPreviewRing()
    {
        // 1) Collect petal transforms from your PreviewShard list
        _petals.Clear();
        foreach (var shard in previewRing) if (shard.visual) _petals.Add(shard.visual);

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

        BuildPreviewRing();             // build data-only
        BuildOrRefreshPreviewRing();    // then apply visuals & layering exactly once
    }
    private void OnDisable()
    {
         SafeUnsubscribeAll();
        if (!starKeepsDustClear) return;
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
    private void BuildPhasePlan(MusicalPhase phase)
    {
        _phasePlan = new List<WeightedMineNode>();
        if (!spawnStrategyProfile) return;

        int target = Mathf.Max(1, behaviorProfile.nodesPerStar);
        int safety = target * 10; // avoid infinite loops if PickNext can’t satisfy constraints

        while (_phasePlan.Count < target && safety-- > 0)
        {
            var node = spawnStrategyProfile.PickNext(phase, -1f);
            if (node != null) _phasePlan.Add(node);
        }

        Trace($"BuildPhasePlan: planned {_phasePlan.Count}/{target} nodes (phase={phase})");
    }
    private bool HasShardsRemaining() => _shardsEjectedCount < behaviorProfile.nodesPerStar;
    private void PrepareNextDirective() {
        Trace("PrepareNextDirective() begin");
        _cachedDirective = null;
        _cachedTrack     = null;

        if (_drum == null || spawnStrategyProfile == null) return;
         
        WeightedMineNode planEntry = GetPlanEntryForHighlightedShard();
        if (planEntry == null) return;

        // 1) Bin-driven preview target (WYSIWYG)
        // 2) Fallback to your existing role/legality picker
        InstrumentTrack track = FindTrackByRole(planEntry.role);
        if (track == null) track = PickTargetTrackFor(planEntry); // defensive fallback
        if (track == null) return; // still nothing to target

        // --- Resolve prefabs via registries on DrumTrack ---
        var nodePrefab    = _drum?.nodePrefabRegistry?.GetPrefab(planEntry.minedObjectType, planEntry.trackModifierType);
        var payloadPrefab = _drum?.minedObjectPrefabRegistry?.GetPrefab(planEntry.minedObjectType, planEntry.trackModifierType);
        if (nodePrefab == null || payloadPrefab == null)
        {
            Trace($"PrepareNextDirective: MISSING prefab(s) for {planEntry.minedObjectType} / {planEntry.trackModifierType}");            return;
        }

        // --- Generate a NoteSet for spawners (NoteSpawner only) ---
        NoteSet noteSet = null;
        if (planEntry.minedObjectType == MinedObjectType.NoteSpawner)
        {
            var gfm    = GameFlowManager.Instance;
            var phase  = gfm?.phaseTransitionManager?.currentPhase ?? _assignedPhase;
            int entropy = NextSpawnSerial();
            
            try
            {
                        if (_assignedMotif != null)
                        {
                            Debug.Log($"[NOTESET] Using {_assignedMotif}");
                            // Motif-aware path: use motif-specific NoteSet generation
                            noteSet = gfm.GenerateNotes(track);
                        }
                        else
                        {
                            Debug.Log($"[NOTESET] No phase to fallback on {_assignedPhase}");

                        }

                        if (noteSet == null)
                        {
                            Debug.LogWarning(
                                $"[PhaseStar] No NoteSet generated for role {track.assignedRole}. " +
                                "Check RoleMotifNoteSetConfig / MotifProfile or legacy RolePhaseNoteSetConfig & Library.");
                        }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[PhaseStar] NoteSetFactory exception: {ex.Message}");
                noteSet = null; // fall back: spawn node without seeded phrase
            }

        }

        // --- Build directive (MineNode shell + payload, plus display color) ---
        var d = new MinedObjectSpawnDirective
        {
            minedObjectType   = planEntry.minedObjectType,
            role              = planEntry.role,
            assignedTrack     = track,
            trackModifierType = planEntry.trackModifierType,
            remixUtility      = null,          // (optional) fill from a phase recipe if/when you add it
            noteSet           = noteSet,       // only for NoteSpawner
            prefab            = nodePrefab,    // MineNode shell
            minedObjectPrefab = payloadPrefab, // payload
            displayColor      = track.trackColor,
            spawnCell         = default        // filled at spawn
        };

        _cachedDirective = d;
        _cachedTrack     = track;
        // Keep the on-screen preview in sync with what will spawn next
        if(!_lockPreviewTintUntilIdle)
            UpdatePreviewTint();
    }
    void BuildPreviewRing()
    {
        buildingPreview = true;
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
        if (_phasePlan == null || _phasePlan.Count != behaviorProfile.nodesPerStar)
        {
            // If you have _assignedPhase or equivalent, pass it; otherwise pass the active phase you already cache.
            BuildPhasePlan(_assignedPhase);
        }

        int n = (_phasePlan != null) ? _phasePlan.Count : 0;
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
            var planEntry = _phasePlan[i];
            var role      = planEntry.role;

            // Color MUST come from the role’s track, not a cycling palette.
            var track = FindTrackByRole(role);
            Color c   = track != null ? track.trackColor : Color.white;

            var shardGO = new GameObject($"PreviewShard_{i}_{role}");
            shardGO.transform.SetParent(transform);
            shardGO.transform.localPosition = Vector3.zero;
            shardGO.transform.localRotation = Quaternion.Euler(0f, 0f, ang);
            shardGO.transform.localScale    = Vector3.one;

            var sr = shardGO.AddComponent<SpriteRenderer>();
            sr.sprite = visuals.diamond;
            sr.color  = c;
            sr.sortingOrder = _baseSortingOrder + (i * _perPetalLayerStep);

            previewRing.Add(new PreviewShard
            {
                color = c,
                collected = false,
                visual = shardGO.transform,

                // NEW: bind petal to plan entry
                plan = planEntry,
                role = role
            });

            // If you still want these for smooth visual transitions, keep them sized to n.
            _petalStartAngles.Add(ang);
            _petalTargetAngles.Add(ang);
        }

        currentShardIndex = 0;
        activeShardVisual = previewRing[0].visual;
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
    private WeightedMineNode GetPlanEntryForHighlightedShard()
    {
        if (previewRing == null || previewRing.Count == 0) return null;
        int idx = Mathf.Clamp(currentShardIndex, 0, previewRing.Count - 1);
        return previewRing[idx].plan;
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
        if (_phasePlan != null && idx >= 0 && idx < _phasePlan.Count)
            _phasePlan.RemoveAt(idx);

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
    void AdvanceActiveShard()
{
    if (previewRing.Count == 0 || previewRing == null) return;
    for (int i = 0; i < previewRing.Count; i++) 
        _petalStartAngles[i] = previewRing[i].visual.localEulerAngles.z;
    // compute the next target angles for the "flower" layout
     var nextAngles = visuals.GetPetalAngles(previewRing.Count); 
     for (int i = 0; i < previewRing.Count; i++) 
         _petalTargetAngles[i] = nextAngles[Mathf.Clamp(i, 0, nextAngles.Length - 1)];
    // Dim old
    UpdateLayering();
    UpdatePreviewTint();
    HighlightActive();
    if (visuals) { 
        visuals.SetVeilOnNonActive(new Color(1f,1f,1f,0.20f), activeShardVisual); 
    }
}
    void UpdatePreviewTint() {
        if (visuals == null) visuals = GetComponentInChildren<PhaseStarVisuals2D>(true);
        if (visuals == null) return;
        var color = ResolvePreviewColor();
        visuals.SetPreviewTint(color);
        if(activeShardVisual) HighlightActive();
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

        // “Dim means busy” is handled elsewhere; this is just the per-petal highlight.
        visuals.SetVeilOnNonActive(new Color(1f, 1f, 1f, 0.25f), activeShardVisual);
        visuals.HighlightActive(activeShardVisual, c, 0.7f);
    }
    private Color ResolvePreviewColor()
    {
        if (previewRing == null || previewRing.Count == 0) return Color.white;
        int idx = Mathf.Clamp(currentShardIndex, 0, previewRing.Count - 1);
        return previewRing[idx].color;
    }
    private InstrumentTrack PickTargetTrackFor(WeightedMineNode entry)
{
    if (_targets == null || _targets.Count == 0) return null;

    // Prefer role match if entry.role is set; else first valid target
    var byRole = (entry.role != 0)
        ? _targets.FirstOrDefault(t => t && t.assignedRole == entry.role)
        : null;

    return byRole ?? _targets.FirstOrDefault(t => t);
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
    if (AnyExpansionPendingGlobal()) { 
        Disarm(DisarmReason.ExpansionPending, _lockedTint); 
        Trace("OnCollisionEnter2D: ignored poke because an expansion is pending"); 
        return;
    }
    // --- Safety & gating ---
    if (!HasShardsRemaining()) return;
    if (!coll.gameObject.TryGetComponent<Vehicle>(out _)) return;
    if (_state != PhaseStarState.WaitingForPoke) { Trace($"OnCollision: ignored, state={_state}"); return; } 
    if (_ejectionInFlight) { Trace("OnCollision: ignored, busy flags"); return; } 
    if (!_isArmed)
    {
        Trace("OnCollisionEnter2D: ignored poke because star is not armed");
        return;
    }

    if (_activeNode != null) { Trace("OnCollision: ignored, activeNode != null"); return; } 
    if (Time.frameCount == _lastPokeFrame) { Trace("OnCollision: ignored, same frame"); return; }
    _lastPokeFrame = Time.frameCount;

    if (_cachedDirective == null || _cachedTrack == null) 
        PrepareNextDirective();
    // --- Handle missing directive fallback ---
    if (_cachedDirective == null || _cachedTrack == null)
    {
        Trace("OnCollision: no directive/track → disarm and wait");
        Disarm(DisarmReason.NodeResolving, _lockedTint);
        return;

    }
// After the directive/track null checks succeed, just before Trace("OnCollision: spawning...")
    Debug.Log($"[MNDBG] PhaseStar hit: relVel={coll.relativeVelocity.magnitude:F2}, " +
              $"contact={coll.GetContact(0).point}, state={_state}, " +
              $"cachedRole={_cachedTrack?.assignedRole}, color={ColorUtility.ToHtmlStringRGB(_cachedDirective.displayColor)}");

    if (previewRing != null && previewRing.Count > 0)
    {
        EjectActivePreviewShardAndFlow(coll);
        return;
    }
    EjectCachedDirectiveAndFlow(coll);
    return;
}
    void ReseedAfterRemoval()
    {
        // 1) Re-collect petals (N changed)
        _petals.Clear();
        foreach (var shard in previewRing) if (shard.visual) _petals.Add(shard.visual);
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
    bool ResolveDirectivePrefab(ref MinedObjectSpawnDirective directive, InstrumentTrack track, out string error)
    {
        error = null;
        // If your directive already has a prefab reference, we’re good
        if (directive.prefab != null) return true;

        // Choose the correct registry by type; default to NoteSpawner for roles
        var type = directive.minedObjectType == MinedObjectType.NoteSpawner
                    ? MinedObjectType.NoteSpawner
                    : directive.minedObjectType;

        // Try role-bound prefab first
        var roleProfile = MusicalRoleProfileLibrary.GetProfile(track.assignedRole);
        GameObject prefab = null;

        switch (type)
        {
            case MinedObjectType.NoteSpawner:
                prefab = _drum.nodePrefabRegistry.GetPrefab(MinedObjectType.NoteSpawner, TrackModifierType.Remix);
                break;
            case MinedObjectType.TrackUtility:
                prefab = _drum.minedObjectPrefabRegistry.GetPrefab(directive.minedObjectType, TrackModifierType.Remix);
                break;
            default:
                prefab = _drum.minedObjectPrefabRegistry.GetSpawnerPrefab();
                break;
        }
        if (prefab == null)
        {
            error = $"ResolveDirectivePrefab failed: no prefab for type={type}, role={track.assignedRole}, utility={directive.minedObjectType}";
            return false;
        }

        directive.prefab = prefab;
        return true;
    }
    void SpawnNodeCommon(Vector2 contactPoint, MinedObjectSpawnDirective usedDirective, InstrumentTrack usedTrack)
    {
        Trace($"SpawnNodeCommon: begin (role={usedTrack?.assignedRole}, color={ColorUtility.ToHtmlStringRGB(usedDirective.displayColor)})");
        int ticket = ++_spawnTicket; 
        _ejectionInFlight = true; 
        visuals?.EjectParticles();
        _lockedTint = usedDirective.displayColor; 
        _lockPreviewTintUntilIdle = true;
        visuals?.SetPreviewTint(_lockedTint);
        var node = DirectSpawnMineNode(contactPoint, usedDirective, usedTrack);
        if (node == null)
        {
            _ejectionInFlight = false;
            _activeNode = null;

            var prefabName = usedDirective != null && usedDirective.prefab != null
                ? usedDirective.prefab.name
                : "(null prefab)";

            Debug.LogError($"[PhaseStar] SpawnNodeCommon failed: could not spawn MineNode. " +
                           $"prefab={prefabName} type={usedDirective?.minedObjectType} mod={usedDirective?.trackModifierType} role={usedDirective?.role}");

            Disarm(DisarmReason.NodeResolving, _lockedTint);
            return;
        }
        // When a new node spawns, close the previous corridor (reverse regrowth).
            
        var gen = gfm != null ? gfm.dustGenerator : null;
        if (gen != null) {
            var phase = (gfm.phaseTransitionManager != null) ? gfm.phaseTransitionManager.currentPhase : _assignedPhase;
            gen.RegrowPreviousCorridorOnNewNodeSpawn(phase);
        }
        
        _activeNode = node; 
        _ejectionInFlight = false; 
        node.OnResolved += (kind, dir) => { 
            if (_state == PhaseStarState.BridgeInProgress || _advanceStarted) return;
            _activeNode = null;
            _awaitingCollectableClear = true;
            _awaitingCollectableClearSinceLoop = (_drum != null) ? _drum.completedLoops : -1; 
            _awaitingCollectableClearSinceDsp  = AudioSettings.dspTime;
            
            bool allShardsEjected = (_shardsEjectedCount >= Mathf.Max(1, behaviorProfile.nodesPerStar)); 
            if (allShardsEjected)
            {
                Disarm(DisarmReason.NodeResolving, _lockedTint);
                Trace("OnResolved: FINAL MineNode resolved → waiting for final collectables");
            }
            else
            {
                // Do NOT force ArmNext here; remain disarmed until collectables clear.
                Disarm(DisarmReason.NodeResolving, _lockedTint);
                Trace("OnResolved: MineNode resolved → waiting for collectables");
                _awaitingCollectableClear = true;
                _awaitingCollectableClearSinceLoop = (_drum != null ? _drum.completedLoops : (GameFlowManager.Instance?.activeDrumTrack?.completedLoops ?? -1));
                _awaitingCollectableClearSinceDsp  = AudioSettings.dspTime;
                
            }

            LogState("OnResolved");
        };
        
        CollectionSoundManager.Instance?.PlayPhaseStarImpact(usedTrack, usedTrack.GetCurrentNoteSet(), 0.8f);
        PrepareNextDirective();
        Trace("SpawnNodeCommon: end");
    }
    void EjectActivePreviewShardAndFlow(Collision2D coll)
    {
        if (!HasShardsRemaining()) return;

        // consume one shard visually (Model 1: highlighted shard is authoritative)
        int shardIdx = Mathf.Clamp(currentShardIndex, 0, previewRing.Count - 1);
        var shard = previewRing[shardIdx];

        if (shard.visual)
        {
            var sr = shard.visual.GetComponent<SpriteRenderer>();
            if (sr) sr.color = behaviorProfile.inactiveShardTint;
        }

        // Remove shard first so previewRing reflects reality immediately.
        RemoveShardAt(shardIdx);
        ReseedAfterRemoval();

        // Count the ejection immediately (keeps "remaining shards" checks consistent downstream).
        _shardsEjectedCount++;

        // If we have no shards left, we are now in the "final collectables must clear" waiting state.
        // The PhaseStar should NOT be visible during that final collection window.
        bool isFinalShardEjection = (previewRing == null || previewRing.Count == 0) ||
                                   (_shardsEjectedCount >= Mathf.Max(1, behaviorProfile.nodesPerStar));

        if (_cachedDirective == null || _cachedTrack == null)
            PrepareNextDirective();

        if (_cachedDirective == null || _cachedTrack == null)
        {
            Debug.LogError("[PhaseStar] Missing directive or track at eject time.");
            return;
        }

        var contact = coll.GetContact(0).point;

        // Compute impact direction & strength for MineNode
        var starPos    = (Vector2)transform.position;
        var vehiclePos = coll.rigidbody != null ? coll.rigidbody.position : contact;

        // From vehicle toward star; this is the "incoming" direction
        _lastImpactDir = (starPos - vehiclePos).normalized;

        // Use relative velocity magnitude as impact strength
        _lastImpactStrength = Mathf.Clamp(coll.relativeVelocity.magnitude, 0f, MaxImpactStrength);

        Disarm(isFinalShardEjection ? DisarmReason.AwaitBridge : DisarmReason.NodeResolving,
            _cachedDirective.displayColor);

        Debug.Log($"[MNDBG] EjectActive: contact={contact}, role={_cachedTrack.assignedRole}, " +
                  $"type={_cachedDirective.minedObjectType}, color={ColorUtility.ToHtmlStringRGB(_cachedDirective.displayColor)}");

        SpawnNodeCommon(contact, _cachedDirective, _cachedTrack);
    }
    void EjectCachedDirectiveAndFlow(Collision2D coll)
    {
        var contact = coll.GetContact(0).point;
    // Compute impact direction & strength
        var starPos    = (Vector2)transform.position;
        var vehiclePos = coll.rigidbody != null ? coll.rigidbody.position : contact;

        _lastImpactDir = (starPos - vehiclePos).normalized;
        _lastImpactStrength = Mathf.Clamp(coll.relativeVelocity.magnitude, 0f, MaxImpactStrength);

        // Ensure cached directive/track has a prefab
        if (!ResolveDirectivePrefab(ref _cachedDirective, _cachedTrack, out var err))
        {
            Debug.LogError($"PhaseStar: {err}");
            return;
        }

        _shardsEjectedCount++;

        bool isFinal = (_shardsEjectedCount >= Mathf.Max(1, behaviorProfile.nodesPerStar));
        Disarm(isFinal ? DisarmReason.AwaitBridge : DisarmReason.NodeResolving,
            _cachedDirective.displayColor);
        SpawnNodeCommon(contact, _cachedDirective, _cachedTrack);
    }
    private MineNode DirectSpawnMineNode(Vector3 spawnFrom, MinedObjectSpawnDirective directive, InstrumentTrack track)
    {
        if (directive == null || track == null || _drum == null) return null;

        Vector2Int cell = _drum.GetRandomAvailableCell();      // ✅ DrumTrack wrapper
        if (cell.x < 0) return null;

        Vector3 targetPos = _drum.GridToWorldPosition(cell);   // ✅ DrumTrack wrapper
        directive.spawnCell = cell;
        Debug.Log($"[MNDBG] DirectSpawnMineNode: spawnFrom={spawnFrom}, gridCell={cell}, " +
                  $"role={directive.role}, track={track.name}");

        var go   = Instantiate(directive.prefab, spawnFrom, Quaternion.identity);
        var node = go.GetComponent<MineNode>();
        if (!node) { Destroy(go); return null; }
        Debug.Log($"[MNDBG] MineNode GO instantiated: go={go.name}, node={node.name}, role={directive.role}");
        // color shell immediately so it never flashes white
        var sr = go.GetComponentInChildren<SpriteRenderer>();
        if (sr) sr.color = directive.displayColor;
        var rb = go.GetComponent<Rigidbody2D>();
        if (rb != null && _lastImpactDir.sqrMagnitude > 0.0001f && _lastImpactStrength > 0f)
        {
            rb.linearVelocity = _lastImpactDir * _lastImpactStrength;
        }

        node.Initialize(directive); // MineNode will spawn payload and register with DrumTrack, etc.

//        StartCoroutine(MoveNodeToTarget(node, targetPos, shardFlightSeconds, onLanded: null));
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
            try { if (_drum) _drum._star = null; } catch {}
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

        _state       = PhaseStarState.Completed;
        _isDisposing = true;
        SafeUnsubscribeAll();
        try { if (_drum) _drum._star = null; } catch {}
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
    private int CountEnabledColliders() {
        if (_isDisposing || this == null) return 0;
        int n = 0; 
        var cols = GetComponentsInChildren<Collider2D>(true); 
        foreach (var c in cols) if (c && c.enabled) n++; 
        return n;
    }
    private void Trace(string msg) {
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
}

    