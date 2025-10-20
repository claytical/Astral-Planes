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

public class PhaseStar : MonoBehaviour
{
    // -------------------- Serialized config --------------------
    [Header("Profiles & Prefs")]
    [SerializeField] private SpawnStrategyProfile spawnStrategyProfile;
    [SerializeField] private PhaseStarBehaviorProfile behaviorProfile;

    [Header("Movement & Timing")]
    [SerializeField] private float shardFlightSeconds = 0.65f; // time to fly the node to its grid cell
    [SerializeField] private bool syncRotationToLoop = true;

    [Header("Subcomponents (optional)")]
    [SerializeField] private PhaseStarVisuals2D visuals;
    [SerializeField] private PhaseStarMotion2D motion;
    [SerializeField] private PhaseStarDustAffect dust;
    [SerializeField] private float _progressionMul = 1f; 
    // -------------------- Runtime references --------------------
    private DrumTrack _drum;
    private MusicalPhase _assignedPhase;
    [SerializeField] private bool _isInFocusMode = false;
    private int _previewVersion = 0;
    private int _directiveVersion = -1;
    private bool _lockPreviewTintUntilIdle;
    private Color _lockedTint;
    // -------------------- State & caches --------------------
    private PhaseStarState _state = PhaseStarState.WaitingForPoke;
    private enum VisualMode { Bright, Dim, Hidden }
    private bool _ejectionInFlight;
    private bool _awaitingBurstResolution;
    private bool _advanceStarted;
    private int  _spawnTicket;
    private Coroutine _retryCo;
    private int _lastPokeFrame = -999999;
    private MinedObjectSpawnDirective _cachedDirective;
    private InstrumentTrack _cachedTrack;
    private MineNode _activeNode;
    private int _binCycleOffset = 0;
    // Targets this star can feed (if provided by spawner)
    private readonly List<InstrumentTrack> _targets = new(4);
    private int _spawnSerial = 0;
// PhaseStar.cs (fields)
    private int _loopOffset = 0;        // which pair we’re showing this loop
    private int _lastBinIndex = -1;     // for wrap detection when binIndex goes from N-1 -> 0

    int NextSpawnSerial() => ++_spawnSerial;
    private List<WeightedMineNode> _phasePlan;
    private int _planPreviewIdx = -1;
    private int _targetPreviewIdx = -1;
    private int _planConsumeIdx;
    private int _lastBinCount      = 0;   // remember last bin count to re-sync if it changes
    private bool _rotateOnlyWhenIdle = true; // don't rotate colors while a node/collectables are active
    [SerializeField] private bool _oncePerLoop = false; // true = 1 change per loop
    [SerializeField] private float _minPreviewIntervalSec = 0.5f; // fastest allowed
    [SerializeField] private float _personalityMul = 1f;
    public event Action<PhaseStar> OnArmed;
    public event Action<PhaseStar> OnDisarmed;
    private bool _isArmed;

    // -------------------- Lifecycle --------------------
    public void Initialize(DrumTrack drum, IEnumerable<InstrumentTrack> targets, PhaseStarBehaviorProfile profile, MusicalPhase assignedPhase) {

// Safe, null-tolerant log:
        var roleNames = targets?
            .Select(t => t == null ? "null" : t.assignedRole.ToString())  // <-- note the ()
            .ToArray() ?? Array.Empty<string>();

        Debug.Log($"[PhaseStar] Initialize: received targets={roleNames.Length} :: {string.Join(", ", roleNames)}");

        _assignedPhase = assignedPhase;
        behaviorProfile = profile != null ? profile : behaviorProfile;
        ApplyRotationFromProfile();
        _targets.Clear();
        if (targets != null) _targets.AddRange(targets.Where(t => t));
        _drum = drum; 
        if (_drum != null) {
            int bins = ComputeBinCountForBehavior();
            _drum.SetBinCount(bins); 
            _lastBinCount = bins;
            WireBinSource(_drum);
            Debug.Log($"[PhaseStar] loop={_drum.GetLoopLengthInSeconds():0.##}s  minPrev={_minPreviewIntervalSec:0.##}s  targets={_targets?.Count ?? 0}  bins={bins}");
            
        }
        spawnStrategyProfile?.ResetForNewStar();

        BuildPhasePlan(_assignedPhase);
        PrepareNextDirective();

        _state = PhaseStarState.WaitingForPoke;
        SetInteractableAndAppearance(true,VisualMode.Bright);
        EnterFocusMode();
        UpdatePreviewTint();
        // ensure subcomponents are present if assigned
        if (!visuals) visuals = GetComponentInChildren<PhaseStarVisuals2D>(true);
        if (!motion)  motion  = GetComponentInChildren<PhaseStarMotion2D>(true);
        if (!dust)    dust    = GetComponentInChildren<PhaseStarDustAffect>(true);
        if (visuals) visuals.Initialize(behaviorProfile, this);   // tints, dim/bright, hooks motion vel
        if (motion)  motion.Initialize(behaviorProfile, this);    // sets _profile and subscribes OnArmed
        if (dust)    dust.Initialize(behaviorProfile, this);
        OnArmed?.Invoke(this);
        _binCycleOffset = 0;
        _loopOffset = 0;
        _lastBinIndex = -1;
    }
    public void SetSpawnStrategyProfile(SpawnStrategyProfile profile, bool rebuild = true)
    {
        spawnStrategyProfile = profile;
        if (!rebuild) return;

        // Reset any per-star state the profile tracks
        spawnStrategyProfile?.ResetForNewStar();

        // Rebuild plan and cache the next directive so the star is ready immediately
        BuildPhasePlan(_assignedPhase);
        PrepareNextDirective();
    }
    private void OnDisable()
    {
        UnwireBinSource();
    }
    public void WireBinSource(DrumTrack drum)
    {
        if (_drum != null) {
            _drum.OnBinChanged    -= HandleBinChanged;
            _drum.OnLoopBoundary  -= HandleLoopBoundary;   // NEW
        }
        _drum = drum;
        if (_drum == null) return;

        _drum.OnBinChanged   += HandleBinChanged;
        _drum.OnLoopBoundary += HandleLoopBoundary;       // NEW

        _targetPreviewIdx = 0;
        UpdatePreviewTint();
    }

    private void UnwireBinSource()
    {
        if (_drum != null) {
            _drum.OnBinChanged   -= HandleBinChanged;
            _drum.OnLoopBoundary -= HandleLoopBoundary;    // NEW
        }
        _drum = null;
    }

    private void HandleLoopBoundary()
    {
        // Advance once per loop when we aren’t using per-bin changes (i.e., bins==1)
        if (_rotateOnlyWhenIdle && _state != PhaseStarState.WaitingForPoke) return;
        if (_targets == null || _targets.Count == 0) return;

        if (_oncePerLoop || _lastBinCount == 1)
        {
            _targetPreviewIdx = (_targetPreviewIdx + 1) % _targets.Count;
            // invalidate cache so collision rebuilds against the new target
            _cachedDirective = null;
            _cachedTrack     = null;
            UpdatePreviewTint();
        }
        else
        {
            // per-bin mode: advance an offset once per loop so we eventually visit all tracks
            _binCycleOffset = (_binCycleOffset + 1) % Mathf.Max(1, _targets.Count);
        }
    }

    public void SetProgressionMul(float mul) { _progressionMul = mul; }

    private void HandleBinChanged(int binIndex, int binCount)
    {
        _lastBinCount = binCount;
        if (_rotateOnlyWhenIdle && _state != PhaseStarState.WaitingForPoke) return;
        if (_targets == null || _targets.Count == 0) return;
        if (binCount <= 0) return;

        // Detect loop wrap (e.g., binIndex: 1 -> 0 when binCount == 2)
        if (_lastBinIndex >= 0 && binIndex < _lastBinIndex)
        {
            // move to the next pair on each new loop
            _loopOffset = (_loopOffset + 2) % Mathf.Max(1, _targets.Count);
            // If you prefer changing only one target per loop, use +1 instead of +2.
        }
        _lastBinIndex = binIndex;

        if (_oncePerLoop || binCount == 1)
        {
            _targetPreviewIdx = (_targetPreviewIdx + 1) % _targets.Count;
        }
        else
        {
            // keep per-loop speed the same, but select which targets appear this loop
            // map current bin to a position within the "pair" shown this loop
            int slotsThisLoop = Mathf.Min(binCount, _targets.Count); // e.g., 2
            int posInLoop     = Mathf.FloorToInt((binIndex / (float)binCount) * slotsThisLoop); // 0..slots-1

            _targetPreviewIdx = (_loopOffset + posInLoop) % _targets.Count;
        }

        _cachedDirective = null;
        _cachedTrack     = null;
        _previewVersion++;
        UpdatePreviewTint();

        // optional tracing
        // Debug.Log($"[PhaseStar] bin {binIndex+1}/{binCount}, loopOffset={_loopOffset}, idx={_targetPreviewIdx}, role={_targets[_targetPreviewIdx].assignedRole}");
    }

    private void BuildPhasePlan(MusicalPhase phase)
    {
        _phasePlan = new List<WeightedMineNode>();
        _planPreviewIdx = _planConsumeIdx = 0;

        if (!spawnStrategyProfile) return;

        // Fill a small rolling queue (strict phase gate handled by profile.PickNext)
        for (int i = 0; i < 8; i++)
        {
            var node = spawnStrategyProfile.PickNext(phase, -1f);
            if (node != null) _phasePlan.Add(node);
        }
    }
    private void RefillPlanIfLow()
    {
        if (!spawnStrategyProfile) return;
        if (_phasePlan == null) _phasePlan = new List<WeightedMineNode>();
        if (_phasePlan.Count >= 6) return;

        int toAdd = 8 - _phasePlan.Count;
        for (int i = 0; i < toAdd; i++)
        {
            var node = spawnStrategyProfile.PickNext(_assignedPhase, -1f);
            if (node != null) _phasePlan.Add(node);
        }
    }
    private void AdvanceConsumeCursor()
    {
        if (_phasePlan == null || _phasePlan.Count == 0) return;
        _planConsumeIdx++;
        if (_planConsumeIdx >= _phasePlan.Count) _planConsumeIdx = 0;
        RefillPlanIfLow();
    }


    private void PrepareNextDirective() {
        _cachedDirective = null;
        _cachedTrack     = null;

        if (_drum == null || spawnStrategyProfile == null) return;
        int previewVerAtStart = _previewVersion; 
        // Prefer from rolling plan (consume cursor), else ask profile once
        WeightedMineNode planEntry = null;
        if (_phasePlan != null && _phasePlan.Count > 0)
            planEntry = _phasePlan[_planConsumeIdx];
        else
            planEntry = spawnStrategyProfile.PickNext(_assignedPhase, -1f);

        if (planEntry == null) return;

        // --- Choose the target track (bin-driven preview first; fallback to role-based pick) ---
        InstrumentTrack track = null;

        // 1) Bin-driven preview target (WYSIWYG)
        if (_targets != null && _targets.Count > 0 && _targetPreviewIdx >= 0)
        {
            track = _targets[Mathf.Clamp(_targetPreviewIdx, 0, _targets.Count - 1)];
        }

        // 2) Fallback to your existing role/legality picker
        if (track == null)
            track = PickTargetTrackFor(planEntry);

        if (track == null) return; // still nothing to target

        // --- Resolve prefabs via registries on DrumTrack ---
        var nodePrefab    = _drum?.nodePrefabRegistry?.GetPrefab(planEntry.minedObjectType, planEntry.trackModifierType);
        var payloadPrefab = _drum?.minedObjectPrefabRegistry?.GetPrefab(planEntry.minedObjectType, planEntry.trackModifierType);
        if (nodePrefab == null || payloadPrefab == null)
        {
            Debug.LogWarning($"[PhaseStar] Missing prefab(s) for {planEntry.minedObjectType} / {planEntry.trackModifierType}");
            return;
        }

        // --- Generate a NoteSet for spawners (NoteSpawner only) ---
        NoteSet noteSet = null;
        if (planEntry.minedObjectType == MinedObjectType.NoteSpawner)
        {
            var gfm    = GameFlowManager.Instance;
            var phase  = gfm?.phaseTransitionManager?.currentPhase ?? _assignedPhase;
            var factory= gfm?.noteSetFactory;
            int entropy = NextSpawnSerial();
            

            try {
                noteSet = factory != null ? factory.Generate(track, phase, entropy) : null;
                if (noteSet == null)
                    Debug.LogWarning($"[PhaseStar] No NoteSet generated for {phase}/{track.assignedRole}. Check RolePhaseNoteSetConfig & Library.");
            }
            catch (System.Exception ex) {
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
        _directiveVersion = previewVerAtStart;   
        // Keep the on-screen preview in sync with what will spawn next
        if(!_lockPreviewTintUntilIdle)
            UpdatePreviewTint();
    }

    private void SetStarPresence(VisualMode mode, Color tint)
    {
        if (visuals == null) visuals = GetComponentInChildren<PhaseStarVisuals2D>(true);
        if (visuals == null) return;

        switch (mode)
        {
            case VisualMode.Bright:  visuals.ShowBright(tint);  break;
            case VisualMode.Dim:     visuals.ShowDim(tint);     break;
            case VisualMode.Hidden:  visuals.HideAll();         break;
        }
    }

    private void SetInteractableAndAppearance(bool interactable, VisualMode mode)
    {
        SetInteractable(interactable);
        SetStarPresence(mode, ResolvePreviewColor());

        // ❌ remove the position freeze; just keep rotation frozen so Motion2D can move it
        var rb = GetComponent<Rigidbody2D>();
        if (rb)
            rb.constraints = RigidbodyConstraints2D.FreezeRotation; // (no FreezePosition)
    }
    private void UpdatePreviewTint()
    {
        if (visuals == null) visuals = GetComponentInChildren<PhaseStarVisuals2D>(true);
        if (visuals == null) return;

        var t = (_targets != null && _targets.Count > 0)
            ? _targets[Mathf.Clamp(_targetPreviewIdx, 0, _targets.Count - 1)]
            : null;
        var color = (t != null) ? t.trackColor : Color.white;
        visuals.SetPreviewTint(color);
    }
    private int ComputeBinCountForBehavior()
    {
        if (_drum == null) return 1;
        float loopSec = Mathf.Max(0.01f, _drum.GetLoopLengthInSeconds()); // 2.0s now
        float fastest = Mathf.Max(0.25f, _minPreviewIntervalSec);         // e.g., 0.5s cap

        // Base interval: PerBin aims for one step per target across the loop; OncePerLoop is the loop itself
        float baseInterval = _oncePerLoop ? loopSec : loopSec / Mathf.Max(1, _targets?.Count ?? 1);

        // Apply behavior & progression multipliers ( >1 = faster, <1 = slower )
        float mul = Mathf.Max(0.01f, _personalityMul * _progressionMul);
        float desired = baseInterval / mul;

        // Never faster than cap
        desired = Mathf.Max(fastest, desired);

        // Turn interval into an integer bin count for DrumTrack
        int bins = Mathf.Clamp(Mathf.RoundToInt(loopSec / desired), 1, 64);

        // If PerBin and we intended a “color wheel”, snap bins to target count when close
        if (!_oncePerLoop && _targets != null)
        {
            int tgt = Mathf.Max(1, _targets.Count);
            if (Mathf.Abs(bins - tgt) <= 1) bins = tgt;
        }

        return bins;
    }

    private Color ResolvePreviewColor()
    {
        // 1) Bin-driven rotating target
        if (_targets != null && _targets.Count > 0 && _targetPreviewIdx >= 0)
        {
            var t = _targets[Mathf.Clamp(_targetPreviewIdx, 0, _targets.Count - 1)];
            if (t != null) return t.trackColor;
        }

        // 2) Try plan entry’s role → pick a target by role (optional if you keep this path)
        WeightedMineNode preview = null;
        if (_phasePlan != null && _phasePlan.Count > 0)
            preview = _phasePlan[Mathf.Clamp(_planPreviewIdx, 0, _phasePlan.Count - 1)];
        if (preview != null)
        {
            var tByRole = PickTargetTrackFor(preview);
            if (tByRole != null) return tByRole.trackColor;
        }

        // 3) Fall back to any target or white
        var any = _targets != null ? _targets.FirstOrDefault(tt => tt) : null;
        return any ? any.trackColor : Color.white;
    }
    private void ApplyRotationFromProfile()
    {
        if (behaviorProfile == null) return;

        // Map profile → local fields used by ComputeBinCountForBehavior()
        _oncePerLoop            = (behaviorProfile.rotationMode == RotationMode.OncePerLoop);
        _minPreviewIntervalSec  = Mathf.Max(0.01f, behaviorProfile.minPreviewIntervalSec);

        // Optional: include BPM and personality speed mul if you want progression variance
        float bpmMul            = behaviorProfile.bpmToSpeedMul != null 
            ? behaviorProfile.bpmToSpeedMul.Evaluate(_drum?.drumLoopBPM ?? 120f)
            : 1f;
        _personalityMul         = Mathf.Max(0.01f, behaviorProfile.personalitySpeedMul * bpmMul);
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
        if (!coll.gameObject.TryGetComponent<Vehicle>(out _)) return;
        if (_state != PhaseStarState.WaitingForPoke) return;
        if (_ejectionInFlight || _awaitingBurstResolution) return;
        if (_activeNode != null) return;
        if (Time.frameCount == _lastPokeFrame) return;
           _lastPokeFrame = Time.frameCount;
        if (_cachedDirective == null || _cachedTrack == null || _directiveVersion != _previewVersion)
            if (_targets != null && _targets.Count > 0 && _targetPreviewIdx >= 0)
            {
                var desired = _targets[Mathf.Clamp(_targetPreviewIdx, 0, _targets.Count - 1)];
                if (_cachedTrack != desired)
                {
                    _cachedDirective = null;
                    _cachedTrack     = null;
                    PrepareNextDirective();   // builds against current _targetPreviewIdx
                }
            }
        if (_cachedDirective == null || _cachedTrack == null)
        {
            StartCoroutine(CompleteAndAdvanceAsync());
            return;
        }

        var contact = coll.GetContact(0).point;

        int ticket = ++_spawnTicket;
        _ejectionInFlight = true;
        _state = PhaseStarState.PursuitActive;
        visuals.EjectParticles();
        SetInteractableAndAppearance(false, VisualMode.Dim);
        SetStarPresence(VisualMode.Dim, Color.white);

           // IMPORTANT: capture the directive/track actually used for THIS node
        var usedDirective = _cachedDirective;
        var usedTrack     = _cachedTrack;
        _lockedTint = usedDirective.displayColor;
        _lockPreviewTintUntilIdle = true;
        if(visuals) visuals.SetPreviewTint(_lockedTint);
        var node = DirectSpawnMineNode( contact, usedDirective, usedTrack);
        if (node == null)
        {
            if (_retryCo != null) StopCoroutine(_retryCo);
            _retryCo = StartCoroutine(RetrySpawnWhenCellFrees(ticket, contact));
            return;
        }
        else
        {
             
            NoteSet previewSet = usedTrack.GetActiveNoteSet() != null ? usedTrack.GetActiveNoteSet() : null; 
            if (usedTrack == null) {
                var ctrl = GameFlowManager.Instance != null ? GameFlowManager.Instance.controller : null; 
                usedTrack = ctrl != null ? ctrl.GetAmbientContextTrack() : null; 
                previewSet = ctrl != null ? ctrl.GetGlobalContextNoteSet() : null;
            } 
            CollectionSoundManager.Instance?.PlayPhaseStarImpact(usedTrack, previewSet, .8f);            
        }

        _activeNode = node;
        _ejectionInFlight = false;

        node.OnResolved += (kind, dir) =>
        {
            _activeNode = null;
            StartCoroutine(HandleNodeResolvedThenWatchBurst(usedTrack));
        };

        // Pre-pick the next directive for instant re-arm later
        AdvanceConsumeCursor();
        PrepareNextDirective();
        EnterFocusMode();
    }
    private IEnumerator RetrySpawnWhenCellFrees(int ticket, Vector3 from)
    {
        if (_state != PhaseStarState.PursuitActive) yield break;
        const float timeout = 3f;
        const float step = 0.15f;
        float t = 0f;

        while (t < timeout)
        {
            if (ticket != _spawnTicket) yield break;
            if (_activeNode != null) yield break;

            var node = DirectSpawnMineNode(from, _cachedDirective, _cachedTrack);
            if (node != null)
            {
                _activeNode = node;
                _ejectionInFlight = false;
                _retryCo = null;

                node.OnResolved += (kind, dir) =>
                {
                    var usedTrack = _cachedTrack; 
                    _activeNode = null;
                    StartCoroutine(HandleNodeResolvedThenWatchBurst(usedTrack));
                };
                _state = PhaseStarState.WaitingForPoke;
                _lockPreviewTintUntilIdle = false;
                PrepareNextDirective();
                UpdatePreviewTint();
                SetInteractable(true);
                SetStarPresence(VisualMode.Bright, ResolvePreviewColor());
                    //AdvanceConsumeCursor();
                yield break;
            }

            t += step;
            yield return new WaitForSeconds(step);
        }

        _retryCo = null;
        _ejectionInFlight = false;
        StartCoroutine(CompleteAndAdvanceAsync());
    }
    private MineNode DirectSpawnMineNode(Vector3 spawnFrom, MinedObjectSpawnDirective directive, InstrumentTrack track)
    {
        if (directive == null || track == null || _drum == null) return null;

        Vector2Int cell = _drum.GetRandomAvailableCell();      // ✅ DrumTrack wrapper
        if (cell.x < 0) return null;

        Vector3 targetPos = _drum.GridToWorldPosition(cell);   // ✅ DrumTrack wrapper
        directive.spawnCell = cell;

        var go   = Instantiate(directive.prefab, spawnFrom, Quaternion.identity);
        var node = go.GetComponent<MineNode>();
        if (!node) { Destroy(go); return null; }

        // color shell immediately so it never flashes white
        var sr = go.GetComponentInChildren<SpriteRenderer>();
        if (sr) sr.color = directive.displayColor;

        node.Initialize(directive); // MineNode will spawn payload and register with DrumTrack, etc.

        StartCoroutine(MoveNodeToTarget(node, targetPos, shardFlightSeconds, onLanded: null));
        return node;
    }
    public void EnterFocusMode()
    {
        _isInFocusMode = true;
        SetStarPresence(VisualMode.Dim, ResolvePreviewColor()); // stays on-screen, just dim
        // remain "armed": dust affect & motion continue to run
    }

    public void ExitFocusMode()
    {
        _isInFocusMode = false;
        SetStarPresence(VisualMode.Bright, ResolvePreviewColor());
    }
    private IEnumerator HandleNodeResolvedThenWatchBurst(InstrumentTrack track)
    {
        _awaitingBurstResolution = true;
        _state = PhaseStarState.CheckingCompletion;
        SetInteractableAndAppearance(false, VisualMode.Dim);
        // Wait for all collectables on this track to be collected/cleared
        while (track != null && track.spawnedCollectables != null && track.spawnedCollectables.Any(go => go))
            yield return null;

        _awaitingBurstResolution = false;
        bool complete = AllTracksHaveNotes();
        if (complete)
        {
            _state = PhaseStarState.BridgeInProgress;
            SetInteractableAndAppearance(false, VisualMode.Hidden);
            yield return CompleteAndAdvanceAsync();
        }
        else
        {
            _state = PhaseStarState.WaitingForPoke;
            _lockPreviewTintUntilIdle = false;
            PrepareNextDirective();
            UpdatePreviewTint();
            SetInteractable(true);
            SetStarPresence(VisualMode.Bright, ResolvePreviewColor());
            ExitFocusMode();
        }
    }
    private bool AllTracksHaveNotes()
    {
        var pool = _targets.Count > 0
            ? _targets
            : (GameFlowManager.Instance?._activeTracks?.ToList() ?? new List<InstrumentTrack>());

        if (pool.Count == 0) return false;

        foreach (var t in pool)
        {
            if (t == null) return false;
            var notes = t.GetPersistentLoopNotes();
            if (notes == null || notes.Count == 0) return false;
        }
        return true;
    }
    private IEnumerator CompleteAndAdvanceAsync()
    {
        if (_advanceStarted) yield break;
        _advanceStarted = true;

        _state = PhaseStarState.Completed;
        SetInteractableAndAppearance(false, VisualMode.Hidden);

        if (_drum) _drum.isPhaseStarActive = false;

        var next = GameFlowManager.Instance?.progressionManager?.ComputeNextPhase() ?? _assignedPhase;
        GameFlowManager.Instance?.BeginPhaseBridge(next, null, Color.white);

        yield return null;
        Destroy(gameObject);
    }
    private void SetInteractable(bool on)
    {
        if (_isArmed == on) return;
        _isArmed = on;
        
        // Enable/disable poke colliders
        var cols = GetComponentsInChildren<Collider2D>(true);
        foreach (var t in cols)
            if (t) t.enabled = on;

        // Leave RB constraints to Motion2D; we never hard-freeze here
        var rb = GetComponent<Rigidbody2D>();
        if (rb) rb.constraints = RigidbodyConstraints2D.FreezeRotation;
    }
    private IEnumerator MoveNodeToTarget(MineNode node, Vector3 to, float seconds, Action onLanded)
    {
        if (node == null) yield break;
        var tr = node.transform;
        var from = tr.position;

        float t = 0f, dur = Mathf.Max(0.0001f, seconds);
        while (t < 1f && node != null)
        {
            t += Time.unscaledDeltaTime / dur;
            float u = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t));
            tr.position = Vector3.LerpUnclamped(from, to, u);
            yield return null;
        }

        if (node != null) tr.position = to;
        onLanded?.Invoke();
    }
}

    