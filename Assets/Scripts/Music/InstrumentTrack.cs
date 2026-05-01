using System;
using UnityEngine;
using MidiPlayerTK;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
[System.Serializable]
public class AscensionCohort
{

    // Hidden in inspector — debug aid only; runtime truth is in stepsRemaining (HashSet).
    [HideInInspector] [SerializeField] private List<int> stepsRemainingSerialized = new();

    // Runtime truth
    [System.NonSerialized] public HashSet<int> stepsRemaining;

    public bool armed;

    public void Clear()
    {
        armed = false;
        stepsRemainingSerialized.Clear();
        stepsRemaining?.Clear();
    }

    public void SetSteps(IEnumerable<int> steps)
    {
        if (stepsRemaining == null) stepsRemaining = new HashSet<int>();
        else stepsRemaining.Clear();

        stepsRemainingSerialized.Clear();

        foreach (var s in steps)
        {
            if (stepsRemaining.Add(s))
                stepsRemainingSerialized.Add(s);
        }
    }
}

public class InstrumentTrack : MonoBehaviour, IExpansionHost
{
    private TrackExpansionController _expansionCtrl;
    string IExpansionHost.TrackName => name;
    int  IExpansionHost.LoopMultiplier     => loopMultiplier;
    int  IExpansionHost.MaxLoopMultiplier  => maxLoopMultiplier;
    int  IExpansionHost.TotalSteps         => _totalSteps;
    int  IExpansionHost.BinSize            => BinSize();

    void IExpansionHost.SetLoopMultiplier(int v) => loopMultiplier = v;
    void IExpansionHost.SetTotalSteps(int v)     => _totalSteps = v;

    void IExpansionHost.ResetStepCursors()
    {
        _lastLocalStep = -1;
        _lastBarIndex  = -1;
    }

    void IExpansionHost.SetBinAllocated(int bin, bool v) => SetBinAllocated(bin, v);
    void IExpansionHost.SetBinFilled(int bin, bool v)    => SetBinFilled(bin, v);
    void IExpansionHost.EnsureBinList()                  => EnsureBinList();

    int  IExpansionHost.PickRandomExistingBinForDensity() => PickRandomExistingBinForDensity();
    void IExpansionHost.EnqueueNextFrame(Action a)        => EnqueueNextFrame(a);

    void IExpansionHost.ResyncLeaderBinsNow()            => controller?.ResyncLeaderBinsNow();
    void IExpansionHost.EndGravityVoidForPendingExpand()  => controller?.EndGravityVoidForPendingExpand(this);

    void IExpansionHost.RecomputeAllTrackLayouts()
    {
        if (controller?.tracks == null || controller.noteVisualizer == null) return;
        foreach (var t in controller.tracks)
            if (t != null) controller.noteVisualizer.RecomputeTrackLayout(t);
    }

    void IExpansionHost.MarkGhostPaddingOnVisualizer(int oldTotal, int addedSteps) =>
        controller?.noteVisualizer?.MarkGhostPadding(this, oldTotal, addedSteps);

    void IExpansionHost.CanonicalizeTrackMarkersOnVisualizer(int burstId) =>
        controller?.noteVisualizer?.CanonicalizeTrackMarkers(this, currentBurstId);

    void IExpansionHost.UpdateControllerVisualizer() => controller?.UpdateVisualizer();

    int IExpansionHost.GetControllerMaxActiveLoopMultiplier() =>
        controller != null ? controller.GetMaxActiveLoopMultiplier() : 1;

    int IExpansionHost.GetControllerMaxLoopMultiplier() =>
        controller != null ? controller.GetMaxLoopMultiplier() : 1;

    void IExpansionHost.SpawnBurstNow(
        NoteSet noteSet, int maxToSpawn, int burstId,
        Vector3? originWorld, Vector3? repelFromWorld,
        float burstImpulse, float spreadAngleDeg,
        float spawnJitterRadius, BurstPlacementMode placementMode,
        int trapSearchRadiusCells, int trapBufferCells, int forcedTargetBin)
    {
        SpawnCollectableBurst(
            noteSet, maxToSpawn, burstId,
            originWorld, repelFromWorld,
            burstImpulse, spreadAngleDeg,
            spawnJitterRadius, placementMode,
            trapSearchRadiusCells, trapBufferCells,
            forcedTargetBin);
    }

    [Header("Track Settings")]
    public Color trackColor;
    public GameObject collectablePrefab; // Prefab to spawn
    public Transform collectableParent; // Parent object for organization
    public AscensionCohort ascensionCohort;
    [Header("Musical Role Assignment")]
    public MusicalRole assignedRole;
    public int lowestAllowedNote = 36; // 🎵 Lowest MIDI note allowed for this track
    public int highestAllowedNote = 84; // 🎵 Highest MIDI note
    public InstrumentTrackController controller; // 🎛️ Reference to main controller
    public MidiStreamPlayer midiStreamPlayer; // Plays MIDI notes
    public DrumTrack drumTrack;
    private int channel;
    private int preset;
    private int bank;

    public int Preset => preset;
    public int Bank   => bank;
    public int authoredRootMidi;
    public int loopMultiplier = 1;
    public int maxLoopMultiplier = 4;
    [Header("Ascension Fuse")]
    [Tooltip("How many extended loops markers for this track take to reach the line of ascension after a burst is armed.")]
    public int ascendLoopCount = 4;
    [SerializeField]
    private int ascensionLoopsPerExtraBin = 2;
    private NoteSet[] _binNoteSets;
    [Header("Harmony")]
    [Tooltip("If enabled, notes are treated as authored relative to chord index 0 (the 'I' chord), then root-shifted by the current chord before quantization. This makes progressions like I–IV–V change the bass/lead pitch even when the authored notes are static.")]
    public bool rootShiftNotesByChord = true;
    public List<GameObject> spawnedCollectables = new List<GameObject>(); // Track all spawned Collectables
    private int _currentBurstRemaining = 0;
    private bool _currentBurstArmed = false;
    private NoteSet _currentNoteSet;
    private Boundaries _boundaries;
    private readonly List<(int stepIndex, int note, int duration, float velocity, int authoredRootMidi)> persistentLoopNotes = new List<(int stepIndex, int note, int duration, float velocity, int authoredRootMidi)>();    List<GameObject> _spawnedNotes = new();
    private int _totalSteps = -1;
    private int _lastLocalStep = -1;
    private int _lastBarIndex  = -1;
    private int _nextBurstId = 0;
    private readonly Dictionary<int, float> _noteCommitTimes = new(); // stepIndex -> Time.time at commit
    private readonly Dictionary<int,int> _burstRemaining = new(); // burstId -> remaining
    private readonly Dictionary<int,int> _burstTotalSpawned = new(); // burstId -> total spawned
    private readonly Dictionary<int,int> _burstCollected    = new(); // burstId -> collected count
    private bool _pendingCollapse;
    private int  _collapseTargetMultiplier = 1;
    private bool _hookedBoundaryForCollapse;
    private bool _ascendQueued;
    private readonly Dictionary<int, HashSet<int>> _burstSteps = new(); // burstId -> steps for that burst
    private int? _pendingLoopMultiplier;   // supports expand or collapse
    public int currentBurstId;
    [SerializeField] private List<bool> _binFilled = new();
    // Wall-clock time (Time.time) at which each bin was marked filled.
    // -1 means not yet filled. Sized and reset in parallel with _binFilled.
    private float[] _binCompletionTime;
    private bool _waitingForDrumReady;
    [SerializeField] private int _maxBins = 4;                // keep in sync with your loop multiplier
    [SerializeField] private List<int> _binFillOrder = null;  // 0 = unfilled; 1,2,3,... = ordinal it was filled
    [SerializeField] private List<int> _binChordIndex = null; // -1 = unassigned; else index into ChordProgressionProfile.chordSequence
    [SerializeField] private int _nextFillOrdinal = 1;
    private readonly Dictionary<int, int> _burstLeaderBinsBeforeWrite = new(); // burstId -> leaderBins
    private readonly Dictionary<int, int> _burstWroteBin             = new(); // burstId -> targetBin (cursor bin)
    private readonly Dictionary<int, int> _burstTargetBin            = new(); // burstId -> allocated bin (for rollback on 0-note discard)
    private readonly Dictionary<Collectable, Action> _destroyHandlers = new();

    // ---- Composition Mode: step-sequenced burst spawning ----
    private struct PendingCompositionLaunch
    {
        public int absStep;
        public int note;
        public int duration;
        public Vector3 originWorld;
        public Vector3 targetWorld;
        public Vector2Int targetCell;
        public bool cellHasDust;
        public NoteSet noteSet;
        public int burstId;
    }
    private readonly List<PendingCompositionLaunch> _pendingCompositionLaunches = new();
    private bool _compositionStepListenerActive;
    [SerializeField] private ParticleSystem compositionSpawnEffectPrefab;
    private ParticleSystem _compositionSpawnEffect;
    [SerializeField] private bool[] binAllocated;
    [SerializeField] private int _binCursor = 0;    // counts bins allocated on this track, including silent skips
    [Header("Components")]
    [SerializeField] private MidiVoice midiVoice;
    [SerializeField] private LoopPattern loopPattern;
// Scratch buffer to avoid allocations each step.
    private readonly List<(int note, int duration, float velocity01)> _tmpStepNotes = new();
    [SerializeField] private Color trackShadowColor = new Color(0.08f,0.08f,0.08f,1f);
    public Color TrackShadowColor => trackShadowColor;

    [System.Serializable]
    struct PendingBurst
    {
        public NoteSet noteSet;
        public int maxToSpawn;
        public int burstId;
        public Vector3? originWorld;
        public Vector3? repelFromWorld;
        public float burstImpulse;
        public float spreadAngleDeg;
        public int intendedTargetBin;

        public float spawnJitterRadius;
        public BurstPlacementMode placementMode;
        public int trapSearchRadiusCells;
        public int trapBufferCells;
    }

    public enum BurstPlacementMode
    {
        Free = 0,

        // Prefer cells that currently have dust (and are not permanently clear),
        // biased near originWorld.x. Falls back to any free cell.
        TrappedInDustNearOrigin = 1
    }

    [SerializeField]
    bool _loopCacheDirtyPending;   // authored changes happened
    bool _loopCacheDirtyCommitted; // safe to rebuild
    int  _lastCommittedBar = -1;
    int  _lastCommittedBoundarySerial = -1;
    [SerializeField] private LayerMask spawnBlockedMask; // set to include Vehicle + PhaseStar
    [SerializeField] private int spawnPickMaxTries = 80;
    [SerializeField] [Range(0f, 1f)] private float spawnColumnBandFraction = 0.25f; // fraction of grid width to search per step column band

    // ---- LoopPattern bridge (no state duplication) ----
    internal bool LoopCacheDirtyPending
    {
        get => _loopCacheDirtyPending;
        set => _loopCacheDirtyPending = value;
    }

// Your existing private struct LoopNote is private; we need a public-shaped one
// that matches it so LoopPattern can build the cache without reflection.
    internal struct LoopNotePublic
    {
        public int bin;
        public int localStep;
        public int note;
        public int duration;
        public float velocity;
        public int authoredRootMidi;
    }

    internal List<(int stepIndex, int note, int duration, float velocity, int authoredRootMidi)> MutablePersistentLoopNotes => persistentLoopNotes;
// IMPORTANT: change _loopNotes to be List<LoopNotePublic> (one-time type swap)
    internal List<LoopNotePublic> MutableLoopNotes => _loopNotes;
    [SerializeField]
    private List<LoopNotePublic> _loopNotes = new();
    
    private readonly List<int> _scratchSteps = new List<int>(1);
    private void SetBinCursor(int v) => _binCursor = Mathf.Max(0, v);
    public  int  GetBinCursor()              => Mathf.Max(0, _binCursor);
    public  void AdvanceBinCursor(int by=1)  => _binCursor = Mathf.Max(0, _binCursor + Mathf.Max(1,by));
    private void ResetBinCursor()            => _binCursor = 0;
    public event Action<InstrumentTrack,int,int> OnAscensionCohortCompleted; // (track, start, end)
    public event Action<InstrumentTrack, int, bool> OnCollectableBurstCleared; // (track, burstId, hadNotes)

    private void OnDisable()
    {
        Debug.LogWarning(
            $"[TRACK:LIFECYCLE] DISABLE name={name} " +
            $"goActiveSelf={gameObject.activeSelf} " +
            $"goActiveInHierarchy={gameObject.activeInHierarchy} " +
            $"componentEnabled={enabled}\n" +
            Environment.StackTrace);
        _expansionCtrl?.UnhookExpandBoundary();
        if (controller != null)
            controller.EndGravityVoidForPendingExpand(this);
    }
    private void OnEnable()
    {
        Debug.Log(
            $"[TRACK:LIFECYCLE] ENABLE name={name} " +
            $"goActiveSelf={gameObject.activeSelf} " +
            $"goActiveInHierarchy={gameObject.activeInHierarchy} " +
            $"componentEnabled={enabled}");
    }
    private void OnTransformParentChanged()
    {
        Debug.LogWarning(
            $"[TRACK:LIFECYCLE] PARENT_CHANGED name={name} " +
            $"parent={(transform.parent != null ? transform.parent.name : "null")} " +
            $"goActiveSelf={gameObject.activeSelf} " +
            $"goActiveInHierarchy={gameObject.activeInHierarchy}");
    }
    private void OnDestroy()
    {
        _expansionCtrl?.UnhookExpandBoundary();
        if (controller != null)
            controller.EndGravityVoidForPendingExpand(this);    }

    private readonly List<Action> _nextFrameQueue = new();
    private void EnqueueNextFrame(Action a) => _nextFrameQueue.Add(a);
    public int BinSize() => drumTrack != null ? drumTrack.totalSteps : 16;
    public int BinIndexForStep(int step) => Mathf.Clamp(step / BinSize(), 0, Mathf.Max(0, maxLoopMultiplier - 1));
    /// <summary>
    /// Store the pre-generated NoteSet for a specific bin.
    /// Called by PhaseTransitionManager during motif setup.
    /// </summary>
    public void SetNoteSetForBin(int binIndex, NoteSet noteSet){
        if (_binNoteSets == null || _binNoteSets.Length != maxLoopMultiplier)
            _binNoteSets = new NoteSet[maxLoopMultiplier];

        if (binIndex >= 0 && binIndex < _binNoteSets.Length)
            _binNoteSets[binIndex] = noteSet;
    }

    /// <summary>
    /// Returns the pre-generated NoteSet for binIndex, falling back to _currentNoteSet.
    /// </summary>
    public NoteSet GetNoteSetForBin(int binIndex){
        if (_binNoteSets != null && binIndex >= 0 && binIndex < _binNoteSets.Length)
        {
            var ns = _binNoteSets[binIndex];
            if (ns != null) return ns;
        }
        return _currentNoteSet;
    }
    
    /// <summary>
    /// Returns the authored MIDI note for the given absolute step by looking up
    /// the NoteSet for the bin that owns that step. Falls back to authoredRootMidi
    /// if no NoteSet or no event exists at that local step.
    /// </summary>
    public int GetAuthoredNoteAtAbsStep(int absStep)
    {
        int binSz = BinSize();
        if (binSz <= 0) return authoredRootMidi;
        int binIndex = absStep / binSz;
        int localStep = absStep % binSz;
        var noteSet = GetNoteSetForBin(binIndex);
        if (noteSet != null)
            return noteSet.GetNoteForPhaseAndRole(this, localStep);
        return authoredRootMidi;
    }

    /// <summary>
    /// Instantly overwrites the entire track with perfect authored notes for every bin,
    /// following each bin's chord progression. Called by SuperNode on collision.
    /// </summary>
    public void InstantFillAllBins()
    {
        int binSz = BinSize();
        int bins  = Mathf.Max(1, loopMultiplier);

        // Clear all existing notes first
        for (int b = 0; b < bins; b++)
            ClearBinNotesKeepAllocated(b);

        // Write authored notes bin-by-bin
        for (int b = 0; b < bins; b++)
        {
            var ns = GetNoteSetForBin(b);
            if (ns == null) continue;

            int binStart = b * binSz;

            if (ns.persistentTemplate != null && ns.persistentTemplate.Count > 0)
            {
                // Riff-authoritative: step is local to the bin (0..binSz-1)
                foreach (var (step, note, dur, vel, authoredRoot) in ns.persistentTemplate)
                {
                    int absStep = binStart + step;
                    AddNoteToLoop(absStep, note, dur, vel, lightMarkerNow: true, authoredRoot);
                }
            }
            else
            {
                // Generative fallback
                var steps = ns.GetStepList();
                foreach (int localStep in steps)
                {
                    int absStep = binStart + localStep;
                    int note    = ns.GetNoteForPhaseAndRole(this, localStep);
                    AddNoteToLoop(absStep, note, 120, 1.0f, lightMarkerNow: true, authoredRootMidi);
                }
            }

            SetBinFilled(b, true);
        }

        _loopCacheDirtyPending = true;
    }

    public bool IsStepInFilledBin(int step)
    {
        EnsureBinList();

        int binSize = Mathf.Max(1, BinSize());
        int b = step / binSize; // NOTE: no modulo wrap

        return b >= 0 && b < _binFilled.Count && _binFilled[b];
    }
    
    public bool IsExpansionPending => _expansionCtrl?.IsExpansionPending ?? false;
    public List<(int stepIndex, int note, int duration, float velocity, int authoredRootMidi)> GetPersistentLoopNotes() { // Source-of-truth accessor: keep visuals + controller logic stable.
        return persistentLoopNotes;
    }

    /// <summary>
    /// Returns the Time.time value recorded when the note at <paramref name="stepIndex"/> was committed
    /// to the persistent loop. Returns -1 if no commit time is recorded for that step.
    /// </summary>
    public float GetNoteCommitTime(int stepIndex) =>
        _noteCommitTimes.TryGetValue(stepIndex, out var t) ? t : -1f;
    private bool IsOpenOrPermanentCell(CosmicDustGenerator dustGen, Vector2Int gp) {
        if (dustGen == null) return true;
        if (dustGen.IsPermanentlyClearCell(gp)) return true;
        return !dustGen.HasDustAt(gp);
    }
    private bool HasTrapBuffer(CosmicDustGenerator dustGen, Vector2Int gp, int gridW, int gridH, int bufferCells) {
    if (bufferCells <= 0) return true;

    for (int dx = -bufferCells; dx <= bufferCells; dx++)
    {
        for (int dy = -bufferCells; dy <= bufferCells; dy++)
        {
            int x = gp.x + dx;
            int y = gp.y + dy;

            if (x < 0 || y < 0 || x >= gridW || y >= gridH) 
                continue;

            if (IsOpenOrPermanentCell(dustGen, new Vector2Int(x, y)))
                return false;
        }
    }
    return true;
}

/// <summary>
/// Builds a near-to-far ring-ordered list of candidate cells that are:
/// - dust present
/// - not permanently clear
/// - free (no collectable occupant)
/// - not within trapBufferCells of open/permanent space
/// </summary>
    private List<Vector2Int> BuildTrappedCandidatesNearOrigin(
    CosmicDustGenerator dustGen,
    DrumTrack dt,
    Vector3 originWorld,
    int gridW,
    int gridH,
    int trapSearchRadiusCells,
    int trapBufferCells)
{
    var candidates = new List<Vector2Int>(256);
    if (dustGen == null || dt == null) return candidates;

    Vector2Int oc = dt.WorldToGridPosition(originWorld);

    int rMax = Mathf.Clamp(trapSearchRadiusCells, 0, Mathf.Max(gridW, gridH));
    for (int r = 0; r <= rMax; r++)
    {
        int xMin = oc.x - r;
        int xMax = oc.x + r;
        int yMin = oc.y - r;
        int yMax = oc.y + r;

        // Perimeter only (ring)
        for (int x = xMin; x <= xMax; x++)
        {
            TryAdd(x, yMin);
            TryAdd(x, yMax);
        }
        for (int y = yMin + 1; y <= yMax - 1; y++)
        {
            TryAdd(xMin, y);
            TryAdd(xMax, y);
        }
    }

    return candidates;

    void TryAdd(int x, int y)
    {
        // NOTE: This is the only local function in this helper.
        // If you want *zero* local functions anywhere, I can rewrite this into a private static method.
        if (x < 0 || y < 0 || x >= gridW || y >= gridH) return;

        var gp = new Vector2Int(x, y);

        if (dustGen.IsPermanentlyClearCell(gp)) return;
        if (!dustGen.HasDustAt(gp)) return;
        if (!Collectable.IsCellFreeStatic(gp)) return;
        if (!HasTrapBuffer(dustGen, gp, gridW, gridH, trapBufferCells)) return;

        candidates.Add(gp);
    }
}

    public void RefreshRoleColorsFromProfile(MusicalRoleProfile overrideProfile = null)
    {
        var prof = overrideProfile ?? MusicalRoleProfileLibrary.GetProfile(assignedRole);
        if (prof == null) return;

        trackColor       = prof.GetBaseColor();
        trackShadowColor = prof.GetShadowColor();
        preset           = prof.midiPreset;
        midiVoice?.SetPreset(prof.midiPreset);
    }

    void Awake()
    {
        if (!midiVoice) midiVoice = GetComponent<MidiVoice>();
        if (!loopPattern) loopPattern = GetComponent<LoopPattern>() ?? gameObject.AddComponent<LoopPattern>();
        _expansionCtrl = new TrackExpansionController(this);
        _expansionCtrl?.Bind(drumTrack);
        var awakeProf = MusicalRoleProfileLibrary.GetProfile(assignedRole);
        if (midiVoice != null)
        {
            midiVoice.Bind(
                midiStreamPlayer,
                drumTrack,
                RemainingActiveWindowSec,
                awakeProf?.midiPreset ?? 0
            );
        }

        RefreshRoleColorsFromProfile(awakeProf);
    }
    void Start() {
        if (controller == null)
        {
            Debug.LogError($"{gameObject.name} - No InstrumentTrackController assigned!");
            return;
        }

        if (drumTrack == null)
        {
            Debug.Log("No drumtrack assigned!");
            return;
        }
        _waitingForDrumReady = true;
        if (_totalSteps <= 0) _totalSteps = BinSize(); 
        InitializeBinChords(maxLoopMultiplier);
    }

    void Update() {
        // One-shot replacement for WaitForDrumTrackStartTime()

        if (_waitingForDrumReady)
        {
            bool ready =
                drumTrack != null &&
                drumTrack.GetLoopLengthInSeconds() > 0f &&
                ((drumTrack.leaderStartDspTime > 0.0) || (drumTrack.startDspTime != 0));

            if (ready)
            {
                _totalSteps = drumTrack.totalSteps * loopMultiplier;
                _waitingForDrumReady = false;
            }
            else return;
        }

        if (drumTrack == null) return;
        if (_nextFrameQueue.Count > 0)
        {
            Debug.Log($"[TRK:NEXTFRAME_RUN] track={name} queueCount={_nextFrameQueue.Count} waitingForDrum={_waitingForDrumReady}");
            var count = _nextFrameQueue.Count; // snapshot to avoid reentrancy issues
            for (int i = 0; i < count; i++)
            {
                try { _nextFrameQueue[i]?.Invoke(); }
                catch (System.Exception e) { Debug.LogException(e); }
            }
            _nextFrameQueue.RemoveRange(0, count);
        }
        _expansionCtrl?.Tick(Time.deltaTime);
        // - Drum loop stays the bar clock (binSize steps).
        // - We do NOT stretch bar time when bins increase.
// ----- TRANSPORT (single authority) -----
        if (controller == null) return;

        var tf = controller.GetTransportFrame();

// Defensive clamp: never allow negative barIndex to drive barStart math.
        int barIndex = tf.barIndex;
        int playheadBin = tf.playheadBin;
        int boundarySerial = tf.boundarySerial;
        
        if (barIndex < 0)
        {
            // If this ever happens again, do the safest thing:
            // treat as start of loop so we don't compute barStart in the past.
            barIndex = 0;
            playheadBin = 0;
        }


// Reset step cursor on bar change within the leader loop.
// boundarySerial handles full-loop resets; this closes the gap where
// targetCurLocal == _lastLocalStep at a bin transition (< guard misses ==).
        if (barIndex != _lastBarIndex)
        {
            _lastLocalStep = -1;
            _lastBarIndex  = barIndex;
        }

// Normalize playheadBin into [0, leaderBins-1].
// ----- CLOCK (single authority: DSP) -----
        double dspNow = AudioSettings.dspTime;

        float clipLen;
        try { clipLen = drumTrack.GetClipLengthInSeconds(); }
        catch
        {
            clipLen = (drumTrack.drumAudioSource != null && drumTrack.drumAudioSource.clip != null)
                ? drumTrack.drumAudioSource.clip.length
                : 0f;
        }
        if (clipLen <= 0f) return;

        int drumSteps = Mathf.Max(1, drumTrack.totalSteps);
        int binSize   = Mathf.Max(1, BinSize());

        double start = (drumTrack.leaderStartDspTime > 0.0) ? drumTrack.leaderStartDspTime : drumTrack.startDspTime;
        if (start <= 0.0) return;

        if (dspNow < start)
        {
            // Don't manufacture a bar/bin; just wait.
            return;
        }

        double barStart = start + (double)barIndex * clipLen;

        double transportStart = (drumTrack.leaderStartDspTime > 0.0) ? drumTrack.leaderStartDspTime : drumTrack.startDspTime;
        double localStart     = drumTrack.startDspTime;


// ----- BAR BOUNDARY COMMIT (cache + step reset) -----
// ----- BOUNDARY COMMIT (cache + step reset) -----
// IMPORTANT:
// barIndex is no longer a reliable monotonic boundary signal once DrumTrack
// advances leaderStartDspTime every effective loop. Use DrumTrack's boundarySerial
// as the authority for cache commits.
        if (boundarySerial != _lastCommittedBoundarySerial)
        {
            _lastCommittedBoundarySerial = boundarySerial;
            _lastCommittedBar = barIndex; // debug / inspector only

            // Always rebuild at boundary: picks up any loopMultiplier/binSize change
            // that ArmCohortsOnLoopBoundary may have applied since the last mid-loop rebuild.
            RebuildLoopCache_FORCE();
            _loopCacheDirtyPending = false;

            // Reset step cursor so step 0 is eligible in the new bar/bin window
            _lastLocalStep = -1;
        }
// ----- STEP INDEX (DSP-derived) -----
        int curStep = GetDspStepIndexInBar(dspNow, barStart, clipLen, drumSteps);
        int targetCurLocal = ((curStep % binSize) + binSize) % binSize;

// ----- PLAYBACK (catch-up deterministically) -----
        // Audio must follow the committed leader bins (transport), not the UI's visual bins.
//        int leaderBins = Mathf.Max(1, controller.GetMaxActiveLoopMultiplier());
        // Committed leader bins can lag briefly during re-arm/collapse transitions.
        // Never allow the transport span used for bin selection to be smaller than this track's own bins,
        // or multi-bin tracks can get squashed into bin 0 (I-I instead of I-II).
        int leaderBins = Mathf.Max(1, Mathf.Max(controller.GetCommittedLeaderBins(), Mathf.Max(1, loopMultiplier)));
// Normalize transport/progression bins to the effective leader span.
        playheadBin = WrapIndex(playheadBin, leaderBins);
        int progressionBin = WrapIndex(barIndex, leaderBins);

// Play every missed step exactly once, in order.
        // Guard: if the target wrapped below the last-played step without a bar change
        // (float precision edge at bar boundary), reset the cursor so no steps are skipped.
        if (targetCurLocal < _lastLocalStep)
            _lastLocalStep = -1;

        int startStep = _lastLocalStep + 1;
        if (startStep < 0) startStep = 0;

        for (int s = startStep; s <= targetCurLocal; s++)
        {
            int local = ((s % binSize) + binSize) % binSize;
            // Use progression bin for deterministic harmonic traversal at boundaries.
            PlayLoopedNotesInBin(progressionBin, local, leaderBins);
        }

        _lastLocalStep = targetCurLocal;
        
        for (int i = spawnedCollectables.Count - 1; i >= 0; i--) {
            var obj = spawnedCollectables[i];
            if (obj == null)
            {
                spawnedCollectables.RemoveAt(i); // 💥 clean up dead reference
                continue;
            }
        }
    }
    private static int WrapIndex(int i, int n)
    {
        if (n <= 0) return 0;
        int m = i % n;
        return (m < 0) ? (m + n) : m;
    }

    private int GetDspStepIndexInBar(double dspNow, double barStartDsp, float clipLen, int drumSteps)
    {
        // Clamp inputs defensively
        if (clipLen <= 0f) return 0;
        drumSteps = Mathf.Max(1, drumSteps);

        double t = dspNow - barStartDsp;
        // wrap into [0, clipLen)
        t = t % clipLen;
        if (t < 0.0) t += clipLen;

        double stepDur = clipLen / drumSteps;
        int step = (int)System.Math.Floor(t / stepDur);

        // hard clamp in case of numerical edge at exact end-of-clip
        if (step < 0) step = 0;
        if (step >= drumSteps) step = drumSteps - 1;
        return step;
    }
    private void RebuildLoopCache_FORCE()
    {
        _loopNotes.Clear();

        int binSize = Mathf.Max(1, BinSize());
        int maxBins = Mathf.Max(1, maxLoopMultiplier);
        int maxStepExclusive = maxBins * binSize;

        for (int i = 0; i < persistentLoopNotes.Count; i++)
        {
            var (stepIndex, note, duration, velocity, authoredRootMidi) = persistentLoopNotes[i];
            if (stepIndex < 0 || stepIndex >= maxStepExclusive)
                continue;

            int bin   = stepIndex / binSize;
            int local = stepIndex % binSize;

            _loopNotes.Add(new LoopNotePublic
            {
                bin = bin,
                localStep = local,
                note = note,
                duration = duration,
                velocity = velocity,
                authoredRootMidi = authoredRootMidi
            });

        }
    }

    void PlayLoopedNotesInBin(int playheadBin, int localStep, int leaderBins)
    {
        // LoopPattern owns cache rebuild; InstrumentTrack just requests notes.
        if (loopPattern == null)
        {
            Debug.LogWarning($"[TRK] loopPattern missing on {name}; cannot play loop notes.");
            return;
        }

        // Simple Case A rule:
        // - DrumTrack defines the global playheadBin (0..leaderBins-1)
        // - This track only has content for bins 0..(loopMultiplier-1)
        // - If playheadBin exceeds that, remain silent until the loop wraps.
        int trackBins = Mathf.Max(1, loopMultiplier);
        if (playheadBin < 0 || playheadBin >= trackBins)
        {
            return;
        }
        if (!IsBinFilled(playheadBin))
        {
            return;
        }

        int trackBin = playheadBin;
        float gain = 1f;

        if (localStep == 0)
        {
            int chordIdx = Harmony_GetChordIndexForBin(trackBin);
            var hd = GameFlowManager.Instance?.harmony;
            if (hd != null && hd.TryGetChordAt(chordIdx, out var c))
                Debug.Log($"[CHORD][TRK][Play] track={name} playheadBin={playheadBin} trackBin={trackBin} loopMul={loopMultiplier} chordIdx={chordIdx} chordRoot={c.rootNote}");
            else
                Debug.Log($"[CHORD][TRK][Play] track={name} playheadBin={playheadBin} trackBin={trackBin} loopMul={loopMultiplier} chordIdx={chordIdx} chordRoot=<na>");
        }

        // Ask LoopPattern for notes at this bin/step (velocity returned already gain-scaled per your API).
        loopPattern.GetNotesAt(this, trackBin, localStep, gain, _tmpStepNotes);

        for (int i = 0; i < _tmpStepNotes.Count; i++)
        {
            var (note, duration, vel127f) = _tmpStepNotes[i];
            int vel127 = Mathf.Clamp(Mathf.RoundToInt(vel127f), 1, 127);
            PlayNote127(note, duration, vel127);
        }
    }

    private void InitializeBinChords(int maxBins)
    {
        _binFillOrder = new List<int>(new int[maxBins]);
        _binChordIndex = new List<int>(Enumerable.Repeat(-1, maxBins));
        _nextFillOrdinal = 1;
    }
    private void EnsureBinList()
    {
        int want = Mathf.Max(1, maxLoopMultiplier);

        if (_binFilled == null)
            _binFilled = new List<bool>(want);

        while (_binFilled.Count < want)
            _binFilled.Add(false);

        if (_binFilled.Count > want)
            _binFilled.RemoveRange(want, _binFilled.Count - want); 

        // Keep _binCompletionTime in sync with _binFilled size.
        if (_binCompletionTime == null || _binCompletionTime.Length != want)
        {
            var old = _binCompletionTime;
            _binCompletionTime = new float[want];
            for (int i = 0; i < want; i++)
                _binCompletionTime[i] = (old != null && i < old.Length) ? old[i] : -1f;
        }

        if (binAllocated == null || binAllocated.Length != maxLoopMultiplier) {
            var old = binAllocated; 
            binAllocated = new bool[maxLoopMultiplier]; 
            if (old != null) {
                int n = Mathf.Min(old.Length, binAllocated.Length); 
                for (int i = 0; i < n; i++) binAllocated[i] = old[i];
            }
        }
        Harmony_Bins_EnsureSize(_binFilled.Count);
    }
    public bool IsBinAllocated(int bin) {
        if (binAllocated == null) return false; 
        if (bin < 0 || bin >= binAllocated.Length) return false;
        return binAllocated[bin];
    }
    private void SetBinAllocated(int bin, bool v) { 
        EnsureBinList(); 
        if (bin < 0 || bin >= binAllocated.Length) return; 
        binAllocated[bin] = v;
    }

    private int HighestFilledBinIndex()
    {
        EnsureBinList();
        for (int i = _binFilled.Count - 1; i >= 0; i--)
            if (_binFilled[i]) return i;
        return -1;
    }

    private int HighestAllocatedBinIndex()
    {
        EnsureBinList();
        int hi = GetHighestAllocatedBin(); // already respects binAllocated[]
        return hi;
    }

    /// <summary>
    /// Track span should be derived from ALLOCATED bins (timeline stability),
    /// not FILLED bins (content). Filled bins control silence vs sound.
    /// </summary>
    private int EffectiveLoopBins()
    {
        int maxBins = Mathf.Max(1, maxLoopMultiplier);

        int hiAlloc = HighestAllocatedBinIndex();
        int binsFromAlloc = Mathf.Clamp(hiAlloc + 1, 1, maxBins);

        int hiFill = HighestFilledBinIndex();
        int binsFromFill = Mathf.Clamp(hiFill + 1, 1, maxBins);

        // While expanding/mapping, never let the span contract.
        if (_expansionCtrl != null && _expansionCtrl.IsExpandingAndMapping)
            return Mathf.Max(binsFromAlloc, Mathf.Max(1, loopMultiplier));
        // IMPORTANT: span = allocation (stable). Fill just makes bins audible or silent.
        return Mathf.Max(1, binsFromAlloc);
    }

    public bool TryGetBurstSteps(int burstId, out HashSet<int> steps)
    {
        steps = null;
        return _burstSteps != null && _burstSteps.TryGetValue(burstId, out steps) && steps != null && steps.Count > 0;
    }

    /// <summary>
    /// Returns true if any burst with outstanding notes (collectables not yet committed or
    /// discarded) has at least one step in [stepStart, stepEnd).
    /// Used by NoteAscensionDirector to defer loop collapse when notes are in transit.
    /// </summary>
    public bool HasOutstandingNotesInRange(int stepStart, int stepEnd)
    {
        if (_burstRemaining == null || _burstSteps == null) return false;
        foreach (var kv in _burstRemaining)
        {
            if (kv.Value <= 0) continue;
            if (!_burstSteps.TryGetValue(kv.Key, out var steps) || steps == null) continue;
            foreach (int step in steps)
                if (step >= stepStart && step < stepEnd) return true;
        }
        return false;
    }
    private int PickRandomExistingBinForDensity() { 
        // Only choose among bins that currently exist on THIS track.
        int binsAvailable = Mathf.Clamp(loopMultiplier, 1, Mathf.Max(1, maxLoopMultiplier));
        // Prefer bins that already contain notes, so density accumulates in meaningful places.
        // Fall back to any existing bin if none are marked filled yet.
        List<int> candidates = new System.Collections.Generic.List<int>(binsAvailable);
        for (int b = 0; b < binsAvailable; b++) 
            if (IsBinFilled(b)) candidates.Add(b);
        if (candidates.Count == 0) { 
            for (int b = 0; b < binsAvailable; b++) 
                candidates.Add(b); 
        } 
        return candidates[UnityEngine.Random.Range(0, candidates.Count)];
    }    
    private void SyncSpanFromBins()
    {
        // Span is allocation-driven; do not shrink here.
        int want = EffectiveLoopBins();
        if (want > loopMultiplier)
            loopMultiplier = want;

        _totalSteps = loopMultiplier * BinSize();
    }

    private void SetBinFilled(int bin, bool filled)
    {
        EnsureBinList();
        if (bin < 0 || bin >= _binFilled.Count) return;
        if (_binFilled[bin] == filled) return;

        _binFilled[bin] = filled;

        // Record wall-clock completion time for the coral visualizer.
        if (filled && _binCompletionTime != null && bin < _binCompletionTime.Length)
            _binCompletionTime[bin] = Time.time;

        // Commit the audible span to match filled bins.
        SyncSpanFromBins();
    }
    private float BaseLoopSeconds() => drumTrack != null ? drumTrack.GetLoopLengthInSeconds() : 0f;
    private int   LeaderMultiplier() => Mathf.Max(1, controller?.GetMaxLoopMultiplier() ?? 1);
    private int   MyMultiplier()     => Mathf.Max(1, loopMultiplier);
    private float TimeSinceStart() =>
        drumTrack != null ? (float)(AudioSettings.dspTime - drumTrack.startDspTime) : 0f;
    private float LeaderLengthSec() =>
        BaseLoopSeconds() * LeaderMultiplier();
    private float TimeInLeader() {
        float L = LeaderLengthSec();
        if (L <= 0f) return 0f;
        float t = TimeSinceStart();
        return t % L;
    }
    public int ForceDestroyCollectablesInFlight(string reason)
    {
        if (spawnedCollectables == null || spawnedCollectables.Count == 0) return 0;

        int destroyed = 0;

        // Copy so we can safely mutate the list while destroying
        var copy = new List<GameObject>(spawnedCollectables);

        for (int i = 0; i < copy.Count; i++)
        {
            var go = copy[i];
            if (go == null) continue;

            // Best-effort log: which burst is this thing from?
            var c = go.GetComponent<Collectable>();
            int bid = c != null ? c.burstId : -999;

            Debug.LogWarning($"[FORCE-DESPawn] {name} destroying collectable go={go.name} bid={bid} reason={reason}");
            UnityEngine.Object.Destroy(go);
            destroyed++;
        }

        spawnedCollectables.Clear();
        // IMPORTANT: don’t leave the controller stuck because remaining counts still think something is pending.
        // If you track remaining per-burst, clear that too.
        _burstRemaining?.Clear();
        _burstSteps?.Clear();
        _burstTargetBin?.Clear();

        // Clear any pending composition-mode launches and unsubscribe the step listener.
        _pendingCompositionLaunches.Clear();
        if (_compositionStepListenerActive && drumTrack != null)
        {
            drumTrack.OnStepChanged -= OnCompositionStepFired;
            _compositionStepListenerActive = false;
        }
        if (_compositionSpawnEffect != null)
        {
            _compositionSpawnEffect.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            _compositionSpawnEffect = null;
        }

        return destroyed;
    }
    private float RemainingActiveWindowSec() {
        float my = BaseLoopSeconds() * MyMultiplier();
        float L  = LeaderLengthSec();
        if (L <= 0f) return float.MaxValue;
        float tin = TimeInLeader();
        return Mathf.Max(0f, my - tin);
    }
    private void UnhookCollapseBoundary() {
        if (!_hookedBoundaryForCollapse || drumTrack == null) return;
        drumTrack.OnLoopBoundary -= OnDrumDownbeat_CommitCollapse;
        _hookedBoundaryForCollapse = false;
    }
    private void OnDrumDownbeat_CommitCollapse()
    {
        if (!_pendingCollapse) { UnhookCollapseBoundary(); return; }

        int newMult = Mathf.Clamp(_collapseTargetMultiplier, 1, loopMultiplier);
        if (newMult != loopMultiplier)
        {
            loopMultiplier = newMult;

            // Clear allocation/filled flags above the collapsed width so EffectiveLoopBins won't re-grow.
            EnsureBinList();
            for (int b = newMult; b < maxLoopMultiplier; b++)
            {
                SetBinAllocated(b, false);
                if (b >= 0 && b < _binFilled.Count) _binFilled[b] = false;
                if (_binCompletionTime != null && b < _binCompletionTime.Length) _binCompletionTime[b] = -1f;
                Harmony_OnBinEmptied(b);
            }

            // Use this track's bin size rather than DrumTrack.totalSteps.
            // DrumTrack.totalSteps can represent a different timing grid (e.g. 16),
            // while InstrumentTrack bins may be authored at a smaller width (e.g. 8).
            // If we multiply by DrumTrack.totalSteps during collapse, notes in trimmed
            // bins can remain inside _totalSteps and survive pruning, causing
            // stale harmony/marker artifacts at the right edge after loop contraction.
            _totalSteps = BinSize() * loopMultiplier;

            // Remove any loop notes that are now outside the audible window
            persistentLoopNotes.RemoveAll(t => t.stepIndex >= _totalSteps);

            _loopCacheDirtyPending = true;
            RecomputeBinsFromLoop();

            // ---- VISUAL AUTHORITY (subtractive-safe) ----
            // 1) snap the grid to the new leader width immediately (prevents stale X mapping)
            if (controller != null && controller.noteVisualizer != null && drumTrack != null)
            {
                int leaderSteps = drumTrack.GetLeaderSteps();
                if (leaderSteps <= 0) leaderSteps = _totalSteps;

                controller.noteVisualizer.RequestLeaderGridChange(leaderSteps);

                // 2) prune any stale loop-owned markers and re-add missing ones
                controller.noteVisualizer.ForceSyncMarkersToPersistentLoop(this);
            }

            // Let controller update other tracks if needed (hash-driven).
            controller?.UpdateVisualizer();

            // Sync DrumTrack._binCount so audio transport and committed-leader queries agree.
            controller?.ResyncLeaderBinsNow();
        }

        _pendingCollapse = false;
        UnhookCollapseBoundary();
        _lastLocalStep = -1;
        _lastBarIndex  = -1;
    }

    /// <summary>
    /// Requests that this track's loop shrink by one bin at the next loop boundary.
    /// Safe to call multiple times — ignored if already at minimum or a collapse is pending.
    /// </summary>
    public void RequestLoopCollapseByOne()
    {
        if (loopMultiplier <= 1 || _pendingCollapse) return;
        _collapseTargetMultiplier = loopMultiplier - 1;
        _pendingCollapse = true;
        if (!_hookedBoundaryForCollapse && drumTrack != null)
        {
            drumTrack.OnLoopBoundary += OnDrumDownbeat_CommitCollapse;
            _hookedBoundaryForCollapse = true;
        }
    }

    private void Harmony_Bins_EnsureSize(int want)
{
    if (want <= 0) want = 1;

    // Fill-order list
    if (_binFillOrder == null)
    {
        _binFillOrder = new List<int>(want);
        for (int i = 0; i < want; i++) _binFillOrder.Add(0);
    }
    else if (_binFillOrder.Count != want)
    {
        if (_binFillOrder.Count < want)
        {
            int add = want - _binFillOrder.Count;
            for (int i = 0; i < add; i++) _binFillOrder.Add(0);
        }
        else
        {
            _binFillOrder.RemoveRange(want, _binFillOrder.Count - want);
        }
    }

    // Chord-index list
    if (_binChordIndex == null)
    {
        _binChordIndex = new List<int>(want);
        for (int i = 0; i < want; i++) _binChordIndex.Add(-1);
    }
    else if (_binChordIndex.Count != want)
    {
        if (_binChordIndex.Count < want)
        {
            int add = want - _binChordIndex.Count;
            for (int i = 0; i < add; i++) _binChordIndex.Add(-1);
        }
        else
        {
            _binChordIndex.RemoveRange(want, _binChordIndex.Count - want);
        }
    }

    // _nextFillOrdinal only increments on first-time fills; no rewind here.
    if (_nextFillOrdinal < 1) _nextFillOrdinal = 1;
}
    private void Harmony_OnBinFilled(int binIndex, int progressionLength)
    {
        if (binIndex < 0) return;
        if (_binFillOrder == null) return;
        if (binIndex >= _binFillOrder.Count) return;

        if (_binFillOrder[binIndex] == 0)
            _binFillOrder[binIndex] = _nextFillOrdinal++;
    }
    private void Harmony_OnBinEmptied(int binIndex)
    {
        if (binIndex < 0) return;
        if (_binFillOrder == null) return;
        if (binIndex >= _binFillOrder.Count) return;

        // Fill order is a state label (for UI/logic about “filled”).
        _binFillOrder[binIndex] = 0;

        // DO NOT clear _binChordIndex here.
        // Chord identity is deterministic per bin index (or authored override), not fill state.
    }
    public int Harmony_GetChordIndexForBin(int binIndex)
    {
        var hd = GameFlowManager.Instance?.harmony;
        if (hd == null) return -1;

        int progLen = Mathf.Max(1, hd.ProgressionLength);
        if (binIndex < 0) return -1;

        // Optional: honor an authored override if present and valid.
        if (_binChordIndex != null && binIndex < _binChordIndex.Count)
        {
            int authored = _binChordIndex[binIndex];
            if (authored >= 0) return authored;
        }

        // Deterministic: absolute bin -> progression slot
        return ((binIndex % progLen) + progLen) % progLen;
    }
    private int QuantizeNoteToBinChord(int stepIndex, int midiNote, int authoredRootMidi = int.MinValue) {
    // Resolve which bin this step belongs to
    int bin = BinIndexForStep(stepIndex);

    // Use the NoteSet's chordRegion — it carries delta-adjusted absolute roots
    // (NoteSetFactory applies keyRootMidi − authoredFirst to every chord root).
    // HarmonyDirector.profile stores the raw ScriptableObject values, which may be
    // relative scale degrees and would therefore produce a wrong rootDelta.
    var ns = GetNoteSetForBin(bin);
    var region = ns?.chordRegion;
    Chord chord, baseChord;
    if (region != null && region.Count > 0)
    {
        int chordIdx = bin % region.Count;
        chord     = region[chordIdx];
        baseChord = region[0];
    }
    else
    {
        // Fallback: try HarmonyDirector (works when chordRegion is absent)
        int chordIdx = Harmony_GetChordIndexForBin(bin);
        if (chordIdx < 0) return midiNote;
        var hd = GameFlowManager.Instance?.harmony;
        if (hd == null) return midiNote;
        if (!hd.TryGetChordAt(chordIdx, out chord)) return midiNote;
        if (!hd.TryGetChordAt(0, out baseChord)) baseChord = chord;
    }

    // rootDelta: authored note's root → target chord's root
    int rootDelta = (authoredRootMidi != int.MinValue)
        ? chord.rootNote - authoredRootMidi
        : chord.rootNote - baseChord.rootNote;

    bool noteAlreadyInTargetChord = false;
    if (chord.intervals != null && chord.intervals.Count > 0)
    {
        int notePc = ((midiNote % 12) + 12) % 12;
        int chordRootPc = ((chord.rootNote % 12) + 12) % 12;
        for (int i = 0; i < chord.intervals.Count; i++)
        {
            int ivPc = ((chord.intervals[i] % 12) + 12) % 12;
            if (notePc == ((chordRootPc + ivPc) % 12))
            {
                noteAlreadyInTargetChord = true;
                break;
            }
        }
    }

    // If note already fits this chord, preserve it and only octave-fit into track range.
    // Otherwise apply root-delta transposition for bin/chord movement.
        int shifted = noteAlreadyInTargetChord ? midiNote : (midiNote + rootDelta);
        shifted = ShiftByOctavesIntoTrackRange(shifted);

        // Second: snap to nearest chord tone (optional but keeps your original intent)
        // Build allowed chord tones across this track’s playable range
        var allowed = new List<int>(64);
        for (int oct = -2; oct <= 3; oct++)
        {
            for (int k = 0; k < chord.intervals.Count; k++)
            {
                int n = chord.rootNote + chord.intervals[k] + 12 * oct;
                if (n >= lowestAllowedNote && n <= highestAllowedNote)
                    allowed.Add(n);
            }
        }

        if (allowed.Count == 0) return shifted;
        allowed.Sort();

        // Snap to nearest chord tone
        int best = allowed[0];
        int bestDist = Mathf.Abs(best - shifted);

        for (int i = 1; i < allowed.Count; i++)
        {
            int d = Mathf.Abs(allowed[i] - shifted);
            if (d < bestDist)
            {
                best = allowed[i];
                bestDist = d;
            }
        }

        return ShiftByOctavesIntoTrackRange(best);
}

    /// <summary>
    /// Moves a MIDI note by octaves to fit this track's playable range.
    /// If the range is narrower than one octave and no exact octave fit exists,
    /// falls back to clamping.
    /// </summary>
    private int ShiftByOctavesIntoTrackRange(int midiNote)
    {
        int low = Mathf.Min(lowestAllowedNote, highestAllowedNote);
        int high = Mathf.Max(lowestAllowedNote, highestAllowedNote);
        if (high <= low) return low;

        int shifted = midiNote;

        while (shifted < low && shifted + 12 <= high)
            shifted += 12;
        while (shifted > high && shifted - 12 >= low)
            shifted -= 12;

        if (shifted < low || shifted > high)
            shifted = Mathf.Clamp(shifted, low, high);

        return shifted;
    }
    public void ArmAscensionCohort(int windowStartInclusive, int windowEndExclusive)
    {
        ascensionCohort ??= new AscensionCohort();
        
        var loop = GetPersistentLoopNotes();
        if (loop == null)
        {
            ascensionCohort.Clear();
            return;
        }

        var steps = new List<int>();
        foreach (var (step, _, _, _, _) in loop)
            if (step >= windowStartInclusive && step < windowEndExclusive)
                steps.Add(step);

        ascensionCohort.SetSteps(steps);
        ascensionCohort.armed = (ascensionCohort.stepsRemaining != null && ascensionCohort.stepsRemaining.Count > 0);

        Debug.Log($"[CHORD][ARMED] {name} window=[{windowStartInclusive},{windowEndExclusive}) " +
                  $"count={(ascensionCohort.stepsRemaining != null ? ascensionCohort.stepsRemaining.Count : 0)} armed={ascensionCohort.armed}");
    }

    public int CalculateNoteDurationFromSteps(int stepIndex, NoteSet noteSet)
    {
        List<int> allowedSteps = noteSet.GetStepList();
        int totalSteps = GetTotalSteps();

        if (allowedSteps == null || allowedSteps.Count == 0) { 
            int fallbackTicksPerStep = Mathf.RoundToInt(480f / (Mathf.Max(1, totalSteps) / 4f)); 
            Debug.LogWarning($"[TRK:DURATION] {name} CalculateNoteDurationFromSteps: empty step list for noteSet={noteSet}, stepIndex={stepIndex}. Returning single-step fallback duration."); 
            return Mathf.Max(fallbackTicksPerStep / 2, 60);
        }
        // Find the next onset after stepIndex. If none exists (stepIndex is past the last
        // step), wrap around to the first step in the next loop.
        int nextStep = -1; 
        for (int i = 0; i < allowedSteps.Count; i++) { 
            if (allowedSteps[i] > stepIndex) { nextStep = allowedSteps[i]; break; }
        } if (nextStep < 0) nextStep = allowedSteps[0]; // wraparound — safe because Count > 0

        int stepsUntilNext = (nextStep - stepIndex + totalSteps) % totalSteps;
        if (stepsUntilNext == 0) stepsUntilNext = totalSteps;

        int ticksPerStep = Mathf.RoundToInt(480f / (totalSteps / 4f)); // 480 per quarter note
        int baseDuration = stepsUntilNext * ticksPerStep;

        RhythmPattern pattern = RhythmPatterns.Patterns[noteSet.rhythmStyle];
        int adjusted = Mathf.RoundToInt(baseDuration * pattern.DurationMultiplier);

        return Mathf.Max(adjusted, ticksPerStep / 2); // ensure audibility
    }
    public NoteSet GetCurrentNoteSet()
    {
        return _currentNoteSet;
    }
    private void RegisterBurstStep(int burstId, int step)
    {
        if (!_burstSteps.TryGetValue(burstId, out var set))
            _burstSteps[burstId] = set = new HashSet<int>();
        set.Add(step);
    }
    public void ClearBinNotesKeepAllocated(int binIdx)
    {
        int binSize = Mathf.Max(1, BinSize());
        int start = binIdx * binSize;
        int end   = start + binSize;

        for (int i = persistentLoopNotes.Count - 1; i >= 0; i--)
        {
            var n = persistentLoopNotes[i];
            int step = n.Item1;
            if (step >= start && step < end)
                persistentLoopNotes.RemoveAt(i);
        }

        EnsureBinList();
        if (binIdx >= 0 && binIdx < _binFilled.Count)
        {
            _binFilled[binIdx] = false;
            if (_binCompletionTime != null && binIdx < _binCompletionTime.Length) _binCompletionTime[binIdx] = -1f;
            Harmony_OnBinEmptied(binIdx);
        }

        // CRITICAL: removed notes must stop playing immediately
        _loopCacheDirtyPending = true;

    }
    public void OnCollectableCollected(Collectable collectable, int reportedStep, int durationTicks, float force) {
        if (collectable == null || collectable.assignedInstrumentTrack != this) return;
        controller.NotifyCollected(this);
        
        if (collectable.burstId != 0)
        {
            if (_burstCollected.TryGetValue(collectable.burstId, out var c))
                _burstCollected[collectable.burstId] = c + 1;
            else
                _burstCollected[collectable.burstId] = 1; // defensive if we missed init
            
            if (controller != null && controller.noteVisualizer != null &&
                _burstTotalSpawned.TryGetValue(collectable.burstId, out var total) && total > 0)
            {
                float frac = Mathf.Clamp01(_burstCollected[collectable.burstId] / (float)total);
                controller.noteVisualizer.SetPlayheadEnergy01(frac);
            }
        }
        // 1) Free the vacated grid cell (defensive)
        if (drumTrack != null) {
            Vector2Int gridPos = drumTrack.WorldToGridPosition(collectable.transform.position);
            drumTrack.FreeSpawnCell(gridPos.x, gridPos.y);
            drumTrack.ResetSpawnCellBehavior(gridPos.x, gridPos.y);
        }

        // 2) Final target step must be deterministic (never time-based).
        int finalTargetStep = -1;
// Primary: the absolute step decided at spawn.
        if (collectable.intendedStep >= 0)
        {
            finalTargetStep = collectable.intendedStep;
        }
// Secondary: use the reported step (still deterministic, not time-based).
        else if (reportedStep >= 0)
        {
            finalTargetStep = reportedStep;
            Debug.LogWarning($"[COLLECT:FALLBACK] {name} using reportedStep={reportedStep} because intendedStep was missing. burstId={collectable.burstId}");
        }
        else
        {
            Debug.LogWarning($"[COLLECT:ABORT] {name} no valid step (intendedStep={collectable.intendedStep}, reportedStep={reportedStep}) burstId={collectable.burstId}");
            return;
        }
        int binSize = Mathf.Max(1, BinSize());
        if (collectable.intendedStep < 0 && finalTargetStep >= 0)
        {
            int local = ((finalTargetStep % binSize) + binSize) % binSize;

            int bin = collectable.intendedBin;
            if (bin < 0)
            {
                // last resort fallback: keep your old behavior, but log loudly
                var tf = controller != null ? controller.GetTransportFrame() : default;
                bin = tf.playheadBin;
                Debug.LogWarning($"[COLLECT:BASE->ABS:FALLBACK_BIN] {name} missing intendedBin; using playheadBin={bin} (nondeterministic) burstId={collectable.burstId}");
            }
            finalTargetStep = bin * binSize + local;


            Debug.LogWarning($"[COLLECT:BASE->ABS] {name} mapped baseStep={reportedStep} into absStep={finalTargetStep} using intendedBin={collectable.intendedBin}");
        }

// Optional sanity: log if reported differs from intended (useful for chasing bad assignment upstream).
        if (reportedStep >= 0 && collectable.intendedStep >= 0 && reportedStep != collectable.intendedStep)
        {
            Debug.LogWarning($"[COLLECT:MISMATCH] {name} reportedStep={reportedStep} intended={collectable.intendedStep} burstId={collectable.burstId}");
        }


        // 3) Snapshot leader bins BEFORE we write (used for cross-track nudge later)
        int leaderBinsBeforeWrite = (controller != null) ? Mathf.Max(1, controller.GetMaxLoopMultiplier()) : loopMultiplier;
        if (collectable.burstId != 0 && !_burstLeaderBinsBeforeWrite.ContainsKey(collectable.burstId)) { 
            _burstLeaderBinsBeforeWrite[collectable.burstId] = leaderBinsBeforeWrite; 
            _burstWroteBin[collectable.burstId]              = BinIndexForStep(finalTargetStep);
        }
        int note = collectable.GetNote();
        // Look up the authoredRootMidi stored in the template for this step.
        // NoteSetFactory pre-transposes riff notes to each bin's chord root and stores
        // that root as authoredRootMidi, so QuantizeNoteToBinChord sees rootDelta = 0
        // and only snaps the already-correct note to the nearest chord tone.
        int authoredRootMidi = int.MinValue;
        {
            int binIdx    = BinIndexForStep(finalTargetStep);
            int localStep = finalTargetStep % Mathf.Max(1, BinSize());
            var ns        = GetNoteSetForBin(binIdx);
            if (ns != null) authoredRootMidi = ns.GetAuthoredRootMidi(localStep);
        }
        // IMPORTANT: this is what feeds the audible loop.
        CollectNote(finalTargetStep, note, durationTicks, force, authoredRootMidi);

        // Mark bin filled + hooks
        int targetBin = BinIndexForStep(finalTargetStep);
        Debug.Log($"[CURSOR] Target Bin={targetBin} binCursor: {_binCursor} allocated: {binAllocated} filled: {_binFilled}");
        RegisterBurstStep(collectable.burstId, finalTargetStep);
        spawnedCollectables?.Remove(collectable.gameObject);
    // 5) Per-burst decrement + rise + cursor advance
    if (collectable.burstId != 0 && _burstRemaining.TryGetValue(collectable.burstId, out var rem))
    {
        rem--;
        if (rem <= 0)
        {
            int filledBin = targetBin;
            // --- A) Compute collection intensity for this burst ---
            if(_burstWroteBin.TryGetValue(collectable.burstId, out var b))
                filledBin = b;
            SetBinFilled(filledBin, true); 
            // --- VISUAL: snap the NoteVisualizer grid to include this newly-filled bin immediately ---
            // Without this, the visualizer can remain at the prior width until some other system updates
            // the controller leader multiplier or until a later refresh happens, which looks like bins
            // failing to shrink to 1/2, 1/3, etc.
            if (controller != null && controller.noteVisualizer != null && drumTrack != null) { 
                binSize = Mathf.Max(1, drumTrack.totalSteps);
                // This track now requires at least (filledBin+1) bins to represent its authored content.
                int needBinsFromThisTrack = Mathf.Max(1, filledBin + 1);
                // Also respect the global leader if it is already larger.
                int needLeaderBins = Mathf.Max(needBinsFromThisTrack, controller.GetMaxLoopMultiplier());
                controller.noteVisualizer.RequestLeaderGridChange(needLeaderBins * binSize);
            }

            // This track is now eligible to push the global frontier forward by 1 bin on its next burst.
            if (controller != null)
            {
                controller.AllowAdvanceNextBurst(this);
            }
            
            
            // Visual: "release" the charged playhead when the burst resolves.
            if (controller != null && controller.noteVisualizer != null)
            {
                controller.noteVisualizer.TriggerPlayheadReleasePulse(assignedRole);
            }

            var hd = GameFlowManager.Instance?.harmony;
            int progLen = hd != null ? hd.ProgressionLength : 0;
            Harmony_OnBinFilled(filledBin, progLen);
            // --- C) Clean up per-burst tracking ---
            _burstRemaining.Remove(collectable.burstId);
            _burstTotalSpawned.Remove(collectable.burstId);
            _burstCollected.Remove(collectable.burstId);
            _burstTargetBin.Remove(collectable.burstId);

            // --- D) Existing bin cursor + cross-track behavior ---
            AdvanceBinCursor(1); // only THIS track advances its cursor for the next spawn
            bool extendedLeader = false;
            if (_burstLeaderBinsBeforeWrite.TryGetValue(collectable.burstId, out var Lbefore) &&
                _burstWroteBin.TryGetValue(collectable.burstId, out var wroteBin))
            {
                extendedLeader = (wroteBin >= Lbefore);
            }
            _burstLeaderBinsBeforeWrite.Remove(collectable.burstId);
            _burstWroteBin.Remove(collectable.burstId);

            if (extendedLeader && controller != null)
                controller.AdvanceOtherTrackCursors(this, 1); // silence = absence on others

            // --- E) Loop-based fuse for ascension (note markers rising) ---
            int binCount = BinSize();

            int effectiveLoops = ComputeEffectiveAscendLoops(binCount);

            float seconds = drumTrack.GetLoopLengthInSeconds() * effectiveLoops;

            EnqueueNextFrame(() =>
            {
                if (controller != null && controller.noteVisualizer != null)
                    controller.noteVisualizer.TriggerBurstAscend(this, collectable.burstId, seconds);
            });
            Debug.Log($"[TRK:BURST_CLEARED] track={name} burstId={collectable.burstId} reported Step: {reportedStep}  remainingOnTrack={spawnedCollectables.Count} bin cursor: {_binCursor} ");
            
            OnCollectableBurstCleared?.Invoke(this, collectable.burstId, true);
            Debug.Log($"[TRKDBG] {name} OnCollectableCollected: burstId={collectable.burstId} -> HandleCollectableBurstCleared (_burstRemaining={_burstRemaining?.Count ?? -1})");

        }
        else
        {
            _burstRemaining[collectable.burstId] = rem;
        }
    }

        // 6) Animate the pickup and finalize
// Visual: ensure marker exists but stays “waiting” until deposit.
        GameObject markerGo = null;
        if (controller != null && controller.noteVisualizer != null)
        {
            markerGo = controller.noteVisualizer.PlacePersistentNoteMarker(this, finalTargetStep, lit: false, burstId: collectable.burstId);
        }
        if (markerGo != null)
        {
            collectable.ribbonMarker = markerGo.transform;
            if (collectable.tether != null)
                collectable.tether.SetEndpoints(collectable.transform, collectable.ribbonMarker, this.trackColor, 1f);
        }
        double loopLen = drumTrack != null ? drumTrack.GetLoopLengthInSeconds() : 0.0;
        if (loopLen <= 0.0001) loopLen = drumTrack != null ? drumTrack.GetClipLengthInSeconds() : 0.0;
        if (loopLen <= 0.0001) loopLen = 0.1; // last resort
        

// Hard-coded tuning knobs
        const float orbitMax  = 0.65f;  // allowable orbit budget
        const float travelHard = 0.35f; // your desired travel time
        const float minTravel  = 0.02f;

// --- Compute deposit time in LEADER step space (includes bins) ---
// This prevents "bin expansion" from shifting the particle/confirm timing
// relative to where the note actually lives in persistentLoopNotes.

        int leaderSteps = (drumTrack != null) ? drumTrack.GetLeaderSteps() : Mathf.Max(1, binSize);
        leaderSteps = Mathf.Max(1, leaderSteps);

// finalTargetStep is already absolute (bin*binSize + local).
        int targetLeaderStep = ((finalTargetStep % leaderSteps) + leaderSteps) % leaderSteps;

        double depositDsp = 0.0;
        if (drumTrack != null)
        {
            // Compute next occurrence of a specific leader step using the DrumTrack authority.
            // (inline to avoid needing new DrumTrack API)
            double effLen = System.Math.Max(0.0001, drumTrack.GetLoopLengthInSeconds());
            double stepDur = effLen / leaderSteps;

            double dspNow = AudioSettings.dspTime;
            double elapsed = dspNow - drumTrack.leaderStartDspTime;
            if (elapsed < 0) elapsed = 0;

            double tInLoop = elapsed % effLen;

            int curLeaderStep = Mathf.FloorToInt((float)(tInLoop / stepDur));
            int deltaSteps = targetLeaderStep - curLeaderStep;

            // IMPORTANT: if we're already at/past the step this frame, push to NEXT occurrence.
            // This avoids "one behind" and frame-boundary ambiguity.
            if (deltaSteps <= 0) deltaSteps += leaderSteps;

            depositDsp = drumTrack.leaderStartDspTime + (curLeaderStep + deltaSteps) * stepDur;

            // safety: ensure future
            const double kMinLead = 0.005;
            if (depositDsp <= dspNow + kMinLead)
                depositDsp = dspNow + 0.010;
        }
        else
        {
            // Fallback: behave like before (base loop), but this should not happen in your normal flow.
            depositDsp = AudioSettings.dspTime + 0.05;
        }
        Debug.Log($"[DEPOSIT] track={name} stepAbs={finalTargetStep} stepLocal(reportedStep)={reportedStep} intendedBin={collectable.intendedBin} depositDsp={depositDsp:F6} now={AudioSettings.dspTime:F6} dt={(depositDsp-AudioSettings.dspTime):F4}");
        double now = AudioSettings.dspTime;
        float dt = Mathf.Max(0f, (float)(depositDsp - now));
// IMMINENT: shrink travel so we still land exactly at depositDsp.
        float travelSec = Mathf.Clamp(Mathf.Min(travelHard, dt), minTravel, travelHard);

// Whatever time remains before travel starts becomes orbit time (capped).
        float orbitSec = Mathf.Clamp(dt - travelSec, 0f, orbitMax);

// If it’s basically imminent, don’t bother orbiting.
        if (orbitSec < 0.05f) orbitSec = 0f;

        collectable.BeginCarryThenDepositAtDsp(depositDsp, durationTicks: durationTicks, force: force, onArrived: () =>  {
        // -----------------------------------------------------------------
        // ✅ NOTIFY COMMIT AT DEPOSIT (DSP-authoritative moment)
        // -----------------------------------------------------------------

        // If your collectable carries authoredRootMidi, use it here.
        // Otherwise keep int.MinValue (your existing default).
        int authoredRootMidi = int.MinValue;


        // If NotifyCommitted is part of your causality grammar / stinger,
        // it should happen AFTER the note exists in the loop.
        if (controller != null)
            controller.NotifyCommitted(this, finalTargetStep);

        // -----------------------------------------------------------------
        // ✅ VISUAL CONFIRM SCHEDULING (still uses depositDsp)
        // -----------------------------------------------------------------
        if (controller == null || controller.noteVisualizer == null || markerGo == null)
            return;

        controller.noteVisualizer.ScheduleFirstPlayConfirm(
            source: collectable.transform,
            track: this,
            step: finalTargetStep,
            dspTime: depositDsp,
            noteDuration: durationTicks,
            color: trackColor
        );

        controller.noteVisualizer.RegisterCollectedMarker(this, collectable.burstId, finalTargetStep, markerGo);

        var tag = markerGo.GetComponent<MarkerTag>() ?? markerGo.AddComponent<MarkerTag>();
        tag.isPlaceholder = false;
        tag.burstId = collectable.burstId;

        var ml = markerGo.GetComponent<MarkerLight>() ?? markerGo.AddComponent<MarkerLight>();
        ml.LightUp(this.trackColor);

        var vnm = markerGo.GetComponent<VisualNoteMarker>();
        if (vnm != null)
        {
            vnm.LightUp(this.trackColor);
            vnm.Initialize(this.trackColor);
        }
            }
);
        if (_currentBurstArmed && collectable.burstId == currentBurstId) { 
            _currentBurstRemaining = Mathf.Max(0, _currentBurstRemaining - 1); 
            if (_currentBurstRemaining == 0) 
                _currentBurstArmed = false;
        }
}
    // ---------------------------------------------------------------------
    // Manual Note Release integration
    // ---------------------------------------------------------------------

    /// <summary>
    /// Manual-release pickup path: frees the spawn cell and enqueues a note onto the vehicle,
    /// but does NOT commit anything to the loop yet.
    /// </summary>
    public void OnCollectablePickedUpForManualRelease(Vehicle vehicle, Collectable collectable, int reportedBaseStep, int durationTicks, float velocity127)
    {
        if (vehicle == null || collectable == null || collectable.assignedInstrumentTrack != this) return;

        // Free the vacated grid cell (same as normal pickup path).
        if (drumTrack != null)
        {
            Vector2Int gridPos = drumTrack.WorldToGridPosition(collectable.transform.position);
            drumTrack.FreeSpawnCell(gridPos.x, gridPos.y);
            drumTrack.ResetSpawnCellBehavior(gridPos.x, gridPos.y);
        }

        // Keep the collectable in spawnedCollectables until it is released from the Vehicle.
        // Removal is deferred to OnManualReleaseConsumed / OnManualReleaseDiscarded so that
        // AnyCollectablesInFlightGlobal() stays true while the note is being carried.

        int binSize = Mathf.Max(1, drumTrack != null ? drumTrack.totalSteps : BinSize());

        // Determine authored abs/local step identity.
        int authoredAbs = -1;
        if (collectable.intendedStep >= 0)
            authoredAbs = collectable.intendedStep;
        else if (collectable.intendedBin >= 0 && reportedBaseStep >= 0)
            authoredAbs = collectable.intendedBin * binSize + (((reportedBaseStep % binSize) + binSize) % binSize);

        int authoredLocal = (authoredAbs >= 0) ? (((authoredAbs % binSize) + binSize) % binSize) : (((reportedBaseStep % binSize) + binSize) % binSize);

        int collectedMidi = collectable.GetNote();

        // Anchor authoredRootMidi to the chord that was active at the authored bin.
        // QuantizeNoteToBinChord uses rootDelta = (targetChordRoot - authoredRootMidi), so
        // this must reflect which chord the collectable was spawned under, not the I-chord static field.
        int rootInRegister = GetAuthoredRootMidiInRegister(collectedMidi); // fallback
        if (rootShiftNotesByChord && authoredAbs >= 0)
        {
            var hd = GameFlowManager.Instance?.harmony;
            if (hd != null)
            {
                int authoredBin = BinIndexForStep(authoredAbs);
                int authoredChordIdx = Harmony_GetChordIndexForBin(authoredBin);
                if (authoredChordIdx >= 0 && hd.TryGetChordAt(authoredChordIdx, out var authoredChord))
                {
                    int chordRoot = authoredChord.rootNote;
                    while (chordRoot < collectedMidi - 6) chordRoot += 12;
                    while (chordRoot > collectedMidi + 6) chordRoot -= 12;
                    rootInRegister = Mathf.Clamp(chordRoot, lowestAllowedNote, highestAllowedNote);
                }
            }
        }

        var pending = new Vehicle.PendingCollectedNote
        {
            track = this,
            collectable = collectable,
            authoredAbsStep = authoredAbs,
            authoredLocalStep = authoredLocal,
            collectedMidi = collectedMidi,
            durationTicks = durationTicks,
            velocity127 = Mathf.Clamp(velocity127, 1f, 127f),
            authoredRootMidi = rootInRegister,
            burstId = collectable.burstId
        };

        vehicle.EnqueuePendingNote(pending);
    }

    /// <summary>
    /// Commit a released note into the persistent loop at an absolute step. This is where burst
    /// completion and bin fill logic occurs for manual release.
    /// </summary>
    public void CommitManualReleasedNote(int stepAbs, int midiNote, int durationTicks, float velocity127, int authoredRootMidi, int burstId, bool lightMarkerNow, bool skipChordQuantize = false)
    {
        if (drumTrack == null) return;

        controller?.NotifyCollected(this);

        // Burst energy meter: count at COMMIT time (release), not pickup time.
        if (burstId != 0)
        {
            if (_burstCollected.TryGetValue(burstId, out var c))
                _burstCollected[burstId] = c + 1;
            else
                _burstCollected[burstId] = 1;

            if (controller != null && controller.noteVisualizer != null &&
                _burstTotalSpawned.TryGetValue(burstId, out var total) && total > 0)
            {
                float frac = Mathf.Clamp01(_burstCollected[burstId] / (float)total);
                controller.noteVisualizer.SetPlayheadEnergy01(frac);
            }
        }

        // Snapshot leader bins BEFORE we write (used for cross-track nudge later)
        int leaderBinsBeforeWrite = (controller != null) ? Mathf.Max(1, controller.GetMaxLoopMultiplier()) : loopMultiplier;
        if (burstId != 0 && !_burstLeaderBinsBeforeWrite.ContainsKey(burstId))
        {
            _burstLeaderBinsBeforeWrite[burstId] = leaderBinsBeforeWrite;
            _burstWroteBin[burstId] = BinIndexForStep(stepAbs);
        }

        // Replace-or-add: enforce one persistent note per (track, stepAbs) for stability.
        persistentLoopNotes.RemoveAll(t => t.stepIndex == stepAbs);

        AddNoteToLoop(stepAbs, midiNote, durationTicks, velocity127, lightMarkerNow, authoredRootMidi, skipChordQuantize);

        int targetBin = BinIndexForStep(stepAbs);
        RegisterBurstStep(burstId, stepAbs);

        // Per-burst decrement + completion (mirrors OnCollectableCollected, but keyed off release).
        if (burstId != 0 && _burstRemaining.TryGetValue(burstId, out var rem))
        {
            rem--;
            if (rem <= 0)
            {
                int filledBin = targetBin;
                if (_burstWroteBin.TryGetValue(burstId, out var b))
                    filledBin = b;

                SetBinFilled(filledBin, true);

                // VISUAL: snap the NoteVisualizer grid to include this newly-filled bin immediately
                if (controller != null && controller.noteVisualizer != null && drumTrack != null)
                {
                    int bSize = Mathf.Max(1, drumTrack.totalSteps);
                    int needBinsFromThisTrack = Mathf.Max(1, filledBin + 1);
                    int needLeaderBins = Mathf.Max(needBinsFromThisTrack, controller.GetMaxLoopMultiplier());
                    controller.noteVisualizer.RequestLeaderGridChange(needLeaderBins * bSize);
                }

                // This track is now eligible to push the global frontier forward by 1 bin on its next burst.
                if (controller != null)
                    controller.AllowAdvanceNextBurst(this);

                // Clear remaining placeholders and trigger ascension for committed markers.
                if (drumTrack != null)
                {
                    int binCount = BinSize();
                    int effectiveLoops = ComputeEffectiveAscendLoops(binCount);
                    float ascendSeconds = drumTrack.GetLoopLengthInSeconds() * effectiveLoops;
                    int capturedBurstId = burstId;
                    EnqueueNextFrame(() =>
                    {
                        if (controller != null && controller.noteVisualizer != null)
                        {
                            controller.noteVisualizer.RemoveAllPlaceholdersForBurst(this, capturedBurstId);
                            controller.noteVisualizer.TriggerBurstAscend(this, capturedBurstId, ascendSeconds);
                        }
                    });
                }

                _burstRemaining.Remove(burstId);
                _burstLeaderBinsBeforeWrite.Remove(burstId);
                _burstWroteBin.Remove(burstId);
                _burstTargetBin.Remove(burstId);

                // Notify listeners (e.g. PhaseStar) that the burst is complete, same as the
                // auto-collect path does in OnCollectableCollected. This ensures the bridge
                // wait fires at *placement* time rather than at the _awaitingCollectableClear
                // timeout (which fires even if the note hasn't been placed yet).
                OnCollectableBurstCleared?.Invoke(this, burstId, true);
            }
            else
            {
                _burstRemaining[burstId] = rem;
            }
        }
    }

    /// <summary>Removes the persistent note at the given absolute step from the loop.</summary>
    public void RemovePersistentNoteAtStep(int stepAbs)
    {
        persistentLoopNotes.RemoveAll(t => t.stepIndex == stepAbs);
        _loopCacheDirtyPending = true;
    }

    /// <summary>
    /// Call when a manually-queued note was discarded (outside window, queue overflow, etc.).
    /// Decrements the burst counter. Placeholder removal is deferred: when all collectables
    /// for the burst have left the vehicle, any remaining placeholders are cleared together.
    /// </summary>
    public void NotifyNoteDiscarded(int burstId, int authoredAbsStep)
    {
        if (burstId == 0) return;

        if (!_burstRemaining.TryGetValue(burstId, out var rem)) return;

        rem--;
        if (rem > 0)
        {
            _burstRemaining[burstId] = rem;
            return;
        }

        // All notes in this burst have been resolved (committed or discarded).
        // Clear remaining placeholders and trigger ascension for committed markers.
        if (drumTrack != null)
        {
            int binCount = BinSize();
            int effectiveLoops = ComputeEffectiveAscendLoops(binCount);
            float ascendSeconds = drumTrack.GetLoopLengthInSeconds() * effectiveLoops;
            int capturedBurstId = burstId;
            int capturedDiscardedStep = authoredAbsStep;
            EnqueueNextFrame(() =>
            {
                if (controller?.noteVisualizer != null)
                {
                    controller.noteVisualizer.RemoveAllPlaceholdersForBurst(this, capturedBurstId);
                    // Defensive: also explicitly remove the discarded step's marker if it
                    // is still present and not in the loop (catches cases where burstId or
                    // isPlaceholder was altered before cleanup ran).
                    if (capturedDiscardedStep >= 0)
                        controller.noteVisualizer.RemoveOrphanMarkerAtStep(this, capturedDiscardedStep);
                    controller.noteVisualizer.TriggerBurstAscend(this, capturedBurstId, ascendSeconds);
                }
            });
        }

        // Fill the bin only if at least one note was actually committed.
        if (_burstWroteBin.TryGetValue(burstId, out var filledBin))
        {
            SetBinFilled(filledBin, true);

            if (controller?.noteVisualizer != null && drumTrack != null)
            {
                int bSize = Mathf.Max(1, drumTrack.totalSteps);
                int needBinsFromThisTrack = Mathf.Max(1, filledBin + 1);
                int needLeaderBins = Mathf.Max(needBinsFromThisTrack, controller.GetMaxLoopMultiplier());
                controller.noteVisualizer.RequestLeaderGridChange(needLeaderBins * bSize);
            }

            if (controller != null)
                controller.AllowAdvanceNextBurst(this);
        }
        else
        {
            // 0 notes committed — roll back the bin so the next burst retries it without expansion.
            if (_burstTargetBin.TryGetValue(burstId, out int emptyBin))
            {
                SetBinAllocated(emptyBin, false);
                if (GetBinCursor() > emptyBin)
                    SetBinCursor(emptyBin);
            }
        }

        // Capture before cleanup — _burstWroteBin entry exists iff at least one note was committed.
        bool hadNotes = _burstWroteBin.ContainsKey(burstId);

        _burstRemaining.Remove(burstId);
        _burstLeaderBinsBeforeWrite.Remove(burstId);
        _burstWroteBin.Remove(burstId);
        _burstTargetBin.Remove(burstId);

        OnCollectableBurstCleared?.Invoke(this, burstId, hadNotes);
    }

    /// <summary>True if there is already a persistent note committed at the given absolute step.</summary>
    public bool IsPersistentStepOccupied(int stepAbs)
    {
        for (int i = 0; i < persistentLoopNotes.Count; i++)
            if (persistentLoopNotes[i].stepIndex == stepAbs) return true;
        return false;
    }
    public bool HasAnyNoteInBin(int binIndex)
    {
        int start = binIndex * BinSize();
        int end   = start + BinSize();
        for (int i = 0; i < persistentLoopNotes.Count; i++)
        {
            int s = persistentLoopNotes[i].stepIndex;
            if (s >= start && s < end) return true;
        }
        return false;
    }
    /// <summary>
    /// Returns this track's authored root, transposed into the same register as referenceMidi and clamped to allowed range.
    /// </summary>
    /// <summary>
    /// Ascension loops rounded up to the nearest multiple of the committed leader bin count.
    /// This guarantees every track's note plays at least once at each of its bin positions
    /// before being removed, regardless of how long the leader loop is relative to this track.
    ///
    /// Without this, a 1-bin track in a 2-bin leader ascends for 1 bar boundary and is removed
    /// at playheadBin=1 — the track's step=0 is never audible during the ascension window.
    /// </summary>
    private int ComputeEffectiveAscendLoops(int binCount)
    {
        int ascendByConfig = ascendLoopCount + Mathf.Max(0, binCount - 1) * ascensionLoopsPerExtraBin;
        int leaderBins = (controller != null) ? Mathf.Max(1, controller.GetCommittedLeaderBins()) : 1;
        return Mathf.Max(1, Mathf.CeilToInt((float)ascendByConfig / leaderBins)) * leaderBins;
    }

    private int GetAuthoredRootMidiInRegister(int referenceMidi)
    {
        int root = authoredRootMidi;
        if (root <= 0) root = referenceMidi; // defensive fallback

        // bring root close to reference register
        while (root < referenceMidi - 6) root += 12;
        while (root > referenceMidi + 6) root -= 12;

        root = Mathf.Clamp(root, lowestAllowedNote, highestAllowedNote);
        return root;
    }
    public void PlayOneShotMidi(int midiNote, float velocity127, int durationTicks = -1)
    {
        int dur = (durationTicks > 0) ? durationTicks : 120;
        PlayNote127(midiNote, dur, velocity127);
    }
    public int GetHighestAllocatedBin()
    {
        if (binAllocated == null) return -1;

        // IMPORTANT: allocation is a stable frontier; do NOT clamp by loopMultiplier.
        for (int i = binAllocated.Length - 1; i >= 0; i--)
            if (binAllocated[i]) return i;

        return -1;
    }
    public int GetHighestFilledBin()
    {
        EnsureBinList();

        // IMPORTANT: filled is a content frontier; do NOT clamp by loopMultiplier either.
        // (You can choose to clamp for “audible span” decisions elsewhere, but not for frontier detection.)
        for (int i = _binFilled.Count - 1; i >= 0; i--)
            if (_binFilled[i]) return i;

        return -1;
    }
    public bool IsBinFilled(int binIndex)
    {
        EnsureBinList();

        return binIndex >= 0
               && binIndex < _binFilled.Count
               && _binFilled[binIndex];
    }
    /// <summary>
    /// Returns the Time.time value at which the given bin was marked filled,
    /// or -1 if the bin has not been filled yet.
    /// </summary>
    public float GetBinCompletionTime(int binIndex)
    {
        if (_binCompletionTime == null || binIndex < 0 || binIndex >= _binCompletionTime.Length)
            return -1f;
        return _binCompletionTime[binIndex];
    }
    public void ResetBinsForPhase()
    {
        // Hard reset of bin span + allocation for a clean new phase/motif.
        int want = Mathf.Max(1, maxLoopMultiplier);

        _binFilled = Enumerable.Repeat(false, want).ToList();
        _binCompletionTime = Enumerable.Repeat(-1f, want).ToArray();

        // Allocation drives span (EffectiveLoopBins). Ensure we clear it too.
        binAllocated = new bool[want];

        // Harmony bookkeeping per-bin should restart clean.
        InitializeBinChords(want);

        ResetBinCursor();
        loopMultiplier = 1;                    // tracks don’t pre-expand; width grows on demand
        _totalSteps    = BinSize() * loopMultiplier;
    }
    public void ResetBinStateForNewPhase()
    {
        // Cursor-mode
        SetBinCursor(0);

        // Loop span: force a single bin wide loop (no hidden carryover)
        loopMultiplier = 1;

        // If you track bin-fill flags, clear them here (only if you added it)
         _binFilled.Clear();

        // Burst/cursor snapshots (if present)
        _burstLeaderBinsBeforeWrite?.Clear();
        _burstWroteBin?.Clear();
        _expansionCtrl?.ResetForNewPhase();
        
    }
    /// <summary>
    /// Single hard reset entry point for motif boundaries.
    /// Clears loop content, bin allocation, burst state, and expansion/mapping flags.
    /// Intended to be called exactly once by the motif authority (e.g., GameFlowManager).
    /// </summary>
    public void BeginNewMotifHardClear(string reason = "BeginNewMotif")
    {
        _binNoteSets = null;
        Debug.LogWarning(
            $"[TRK:CLEAR_LOOP] track={name} fn=BeginNewMotifHardClear reason={reason} " +
            $"persistentCount={(persistentLoopNotes != null ? persistentLoopNotes.Count : -1)} " +
            $"spawnedNotesCount={(_spawnedNotes != null ? _spawnedNotes.Count : -1)} " +
            $"burstRemainingCount={(_burstRemaining != null ? _burstRemaining.Count : -1)}\n" +
            Environment.StackTrace);

        persistentLoopNotes?.Clear();
        _loopCacheDirtyPending = true;

        _loopNotes?.Clear();
        _spawnedNotes?.Clear();

        _burstSteps?.Clear();
        _burstRemaining?.Clear();
        _burstTotalSpawned?.Clear();
        _burstCollected?.Clear();

        ResetBinStateForNewPhase();
        ResetBinsForPhase();
    }
    public bool HasNoteSet()
    {
        return _currentNoteSet != null;
    }
    public void SetNoteSet(NoteSet noteSet)
    {
        _currentNoteSet = noteSet;
    }
    public int GetTotalSteps()
    {
        // Always reflect current loopMultiplier.
        // _totalSteps is treated as a fallback for early init / scenes where drumTrack is not ready.
        int drumSteps = (drumTrack != null) ? Mathf.Max(1, drumTrack.totalSteps) : 0;
        if (drumSteps > 0)
            return drumSteps * Mathf.Max(1, loopMultiplier);

        return Mathf.Max(1, _totalSteps);
    }
    public void PlayNote127(int note, int durationTicks, float velocity)
    {
        if (midiVoice == null)
        {
            Debug.LogWarning($"{name} - MidiVoice missing; cannot play note.");
            return;
        }

        midiVoice.PlayNoteTicks(note, durationTicks, velocity);
    }
    public void RetuneLoopToChord(Chord chord)
    {
        if (persistentLoopNotes == null || persistentLoopNotes.Count == 0) return;

        // Build allowed tones across range
        var allowed = new List<int>();
        for (int oct = -2; oct <= 3; oct++)
        {
            foreach (var iv in chord.intervals)
            {
                int n = chord.rootNote + iv + 12 * oct;
                if (n >= lowestAllowedNote && n <= highestAllowedNote) allowed.Add(n);
            }
        }
        if (allowed.Count == 0) return;
        allowed.Sort();

        int Closest(int target)
        {
            int best = allowed[0], dBest = Mathf.Abs(best - target);
            for (int i = 1; i < allowed.Count; i++)
            {
                int d = Mathf.Abs(allowed[i] - target);
                if (d < dBest) { dBest = d; best = allowed[i]; }
            }
            return best;
        }

        var modified = new List<(int step, int note, int dur, float vel)>(persistentLoopNotes.Count);
        foreach (var (step, note, dur, vel, authoredRootMidi) in persistentLoopNotes)
            modified.Add((step, Closest(note), dur, vel));

        RebuildLoopFromModifiedNotes(modified, transform.position);
    }

    /// <summary>
    /// Per-bin retune: snaps each note to the nearest tone in its bin's assigned chord
    /// in the current HarmonyDirector progression. Use this instead of RetuneLoopToChord(chord0)
    /// when a profile change should respect the full chord progression across bins.
    /// </summary>
    public void RetuneLoopToCurrentProgression()
    {
        if (persistentLoopNotes == null || persistentLoopNotes.Count == 0) return;

        var modified = new List<(int step, int note, int dur, float vel)>(persistentLoopNotes.Count);
        foreach (var (step, note, dur, vel, _) in persistentLoopNotes)
        {
            int bin = BinIndexForStep(step);

            // Use the NoteSet's chordRegion (adjusted absolute roots) — same reason as
            // QuantizeNoteToBinChord: HarmonyDirector.profile may have unadjusted roots.
            var ns = GetNoteSetForBin(bin);
            var region = ns?.chordRegion;
            Chord chord;
            if (region != null && region.Count > 0)
            {
                chord = region[bin % region.Count];
            }
            else
            {
                int chordIdx = Harmony_GetChordIndexForBin(bin);
                var hd = GameFlowManager.Instance?.harmony;
                if (chordIdx < 0 || hd == null || !hd.TryGetChordAt(chordIdx, out chord))
                {
                    modified.Add((step, note, dur, vel));
                    continue;
                }
            }

            if (step % BinSize() == 0)
            {
                Debug.Log($"[CHORD][TRK][Retune] track={name} step={step} bin={bin} chordRoot={chord.rootNote} intervals={(chord.intervals != null ? chord.intervals.Count : 0)} noteIn={note}");
            }

            var allowed = new List<int>();
            for (int oct = -2; oct <= 3; oct++)
            {
                foreach (var iv in chord.intervals)
                {
                    int n = chord.rootNote + iv + 12 * oct;
                    if (n >= lowestAllowedNote && n <= highestAllowedNote) allowed.Add(n);
                }
            }

            if (allowed.Count == 0) { modified.Add((step, note, dur, vel)); continue; }
            allowed.Sort();

            int best = allowed[0], bestDist = Mathf.Abs(best - note);
            for (int i = 1; i < allowed.Count; i++)
            {
                int d = Mathf.Abs(allowed[i] - note);
                if (d < bestDist) { bestDist = d; best = allowed[i]; }
            }
            modified.Add((step, best, dur, vel));
        }

        RebuildLoopFromModifiedNotes(modified, transform.position);
    }

    private int AddNoteToLoop(int stepIndex, int note, int durationTicks, float force, bool lightMarkerNow, int authoredRootMidi = int.MinValue, bool skipChordQuantize = false)
    {
        int qNote = skipChordQuantize ? ShiftByOctavesIntoTrackRange(note) : QuantizeNoteToBinChord(stepIndex, note, authoredRootMidi);
        Debug.Log($"[COMMIT] track={name} stepAbs={stepIndex} nowDsp={AudioSettings.dspTime:F6} leaderStart={drumTrack.leaderStartDspTime:F6}");
        persistentLoopNotes.Add((stepIndex, qNote, durationTicks, force, authoredRootMidi));
        _noteCommitTimes[stepIndex] = Time.time;

        _loopCacheDirtyPending = true;
        GameObject noteMarker = null;
        // 🔑 Reuse any existing placeholder marker at (track, step)
        if (controller?.noteVisualizer != null &&
            controller.noteVisualizer.noteMarkers != null &&
            controller.noteVisualizer.noteMarkers.TryGetValue((this, stepIndex), out var t) &&
            t != null)
        {
            noteMarker = t.gameObject;
            var dbgTag = noteMarker.GetComponent<MarkerTag>();
            if (lightMarkerNow) { 
                var tag = noteMarker.GetComponent<MarkerTag>() ?? noteMarker.AddComponent<MarkerTag>(); 
                tag.track = this;
                tag.step = stepIndex;
                tag.isPlaceholder = false;
                var vnm = noteMarker.GetComponent<VisualNoteMarker>(); 
                if (vnm != null) vnm.Initialize(trackColor);
                var ml = noteMarker.GetComponent<MarkerLight>() ?? noteMarker.AddComponent<MarkerLight>();
                ml.LightUp(trackColor);
            }
        }
        else
        {
            bool lit = lightMarkerNow;
            // Always stamp currentBurstId so newly created committed markers can be found
            // by GetMarkersForTrackAndBurst when TriggerBurstAscend runs at burst completion.
            // Using -1 here was the original intent (loop-owned), but it breaks ascension.
            int burst = currentBurstId;
            noteMarker = controller?.noteVisualizer?.PlacePersistentNoteMarker(this, stepIndex, lit: lit, burstId: burst);
            Debug.Log($"[TRK:ADD_NOTE] track={name} step={stepIndex} qNote={qNote} reusedMarker=False lit={lit} newMarkerId={(noteMarker!=null?noteMarker.GetInstanceID():-1)}");
        }

        if (noteMarker != null) _spawnedNotes.Add(noteMarker);
        if (lightMarkerNow)
            controller?.noteVisualizer?.CanonicalizeTrackMarkers(this, currentBurstId);
        return stepIndex;
    }
    public float GetVelocityAtStep(int step)
    {
        float max = 0f;
        foreach (var (noteStep, _, _, velocity, _) in GetPersistentLoopNotes())
        {
            if (noteStep == step)
                max = Mathf.Max(max, velocity);
        }
        return max;
    }
    public int GetNextBinForSpawn()
    {
        EnsureBinList();

        int loopMul = Mathf.Max(1, loopMultiplier);

        // Cursor wraps within the active loop width.
        int start = (GetBinCursor() % loopMul + loopMul) % loopMul;

        // Prefer the cursor bin if it isn't filled.
        if (!_binFilled[start])
            return start;

        // Otherwise, scan forward (wrapping) for the next unfilled bin.
        for (int i = 1; i < loopMul; i++)
        {
            int b = (start + i) % loopMul;
            if (!_binFilled[b])
                return b;
        }

        // All bins filled: deterministic wrap-to-zero for density reuse.
        return 0;
    }
    public int GetNextFilledBinForDensity()
    {
        EnsureBinList();

        int loopMul = Mathf.Max(1, loopMultiplier);
        int start = (GetBinCursor() % loopMul + loopMul) % loopMul;

        // Scan for a filled bin, wrapping. If none are filled, fall back to 0.
        for (int i = 0; i < loopMul; i++)
        {
            int b = (start + i) % loopMul;
            if (_binFilled[b])
                return b;
        }
        return 0;
    }

    public void SpawnCollectableBurst(
        NoteSet noteSet,
        int maxToSpawn = -1,
        int forcedBurstId = -1,
        Vector3? originWorld = null,
        Vector3? repelFromWorld = null,
        float burstImpulse = 0f,
        float spreadAngleDeg = 180f,
        float spawnJitterRadius = 0.25f,
        BurstPlacementMode placementMode = BurstPlacementMode.Free,
        int trapSearchRadiusCells = 10,
        int trapBufferCells = 1,
        int forcedTargetBin = -1) {
    // --- ENTRY / ABORT REASONS ---
    if (noteSet == null)
    {
        Debug.LogWarning($"[TRK:BURST] OUTCOME=ABORT track={name} reason=noteSet_null maxToSpawn={maxToSpawn}");
        return;
    }
    if (collectablePrefab == null)
    {
        Debug.LogWarning($"[TRK:BURST] OUTCOME=ABORT track={name} reason=collectablePrefab_null noteSet={noteSet} maxToSpawn={maxToSpawn}");
        return;
    }
    if (controller == null || controller.noteVisualizer == null)
    {
        Debug.LogWarning($"[TRK:BURST] OUTCOME=ABORT track={name} reason=controller_or_noteVisualizer_null controllerNull={(controller == null)} noteVizNull={(controller != null && controller.noteVisualizer == null)} noteSet={noteSet} maxToSpawn={maxToSpawn}");
        return;
    }

    // ------------------------------------------------------------
    // BURST ID: choose exactly once; never change it mid-function.
    // ------------------------------------------------------------
    int burstId;
    if (forcedBurstId > 0)
    {
        burstId = forcedBurstId;
        _nextBurstId = Mathf.Max(_nextBurstId, forcedBurstId);
    }
    else
    {
        burstId = ++_nextBurstId;
    }
    currentBurstId = burstId;

    // When a new burst fires on this track, placeholders on OTHER tracks that
    // belonged to their own prior bursts won't be cleaned up by this track's
    // canonicalization pass — each track only sweeps itself. Force a canonicalize
    // pass on every sibling track now so stale placeholders don't linger.
    if (controller?.tracks != null && controller.noteVisualizer != null)
    {
        foreach (var sibling in controller.tracks)
        {
            if (sibling == null || sibling == this) continue;
            controller.noteVisualizer.CanonicalizeTrackMarkers(sibling, sibling.currentBurstId);
        }
    }

    Debug.Log($"[TRKDBG] {name} SpawnCollectableBurst: burstId={currentBurstId} noteSet={noteSet} " +
              $"stepCount={(noteSet?.GetStepList()?.Count ?? -1)} noteCount={(noteSet?.GetNoteList()?.Count ?? -1)} " +
              $"loopMul={loopMultiplier} pendingExpand={IsExpansionPending} MaxSpawnCount: {maxToSpawn}");

    var nv = controller.noteVisualizer;
    var stepList = noteSet.GetStepList();
    var noteList = noteSet.GetNoteList();

    if (stepList == null || stepList.Count == 0)
    {
        Debug.LogWarning($"[TRK:BURST] OUTCOME=ABORT track={name} burstId={burstId} reason=stepList_empty");
        return;
    }
    if (noteList == null || noteList.Count == 0)
    {
        Debug.LogWarning($"[TRK:BURST] OUTCOME=ABORT track={name} burstId={burstId} reason=noteList_empty");
        return;
    }

    _currentBurstArmed = true;
    _currentBurstRemaining = 0;

    int binSize = BinSize();

    // ------------------------------------------------------------
    // STEP NORMALIZATION (bin-local)
    // ------------------------------------------------------------
    int rawCount = stepList.Count;
    bool hadOutOfRange = false;
    var localSteps = new List<int>(rawCount);
    var seenLocal = new HashSet<int>();

    for (int i = 0; i < rawCount; i++)
    {
        int raw = stepList[i];
        if (raw < 0) continue;
        if (raw >= binSize) hadOutOfRange = true;

        int local = raw % binSize;
        if (seenLocal.Add(local)) localSteps.Add(local);
    }

    if (hadOutOfRange || localSteps.Count != rawCount)
    {
        Debug.LogWarning($"[TRK:STEP_NORMALIZE] track={name} burstId={burstId} binSize={binSize} " +
                         $"rawSteps={rawCount} localUnique={localSteps.Count} hadOutOfRange={hadOutOfRange} " +
                         $"sampleRaw={string.Join(",", stepList.Take(Mathf.Min(8, rawCount)))} " +
                         $"sampleLocal={string.Join(",", localSteps.Take(Mathf.Min(8, localSteps.Count)))}");
    }

// ------------------------------------------------------------
// TARGET BIN: pick exactly once.
// forcedTargetBin beats density injection beats normal selection.
// ------------------------------------------------------------
    int targetBin;

    if (forcedTargetBin >= 0)
    {
        // Clamp to current committed width to avoid accidental re-stage.
        targetBin = Mathf.Clamp(forcedTargetBin, 0, Mathf.Max(0, loopMultiplier - 1));
    }
    else if (_expansionCtrl != null && _expansionCtrl.OverrideNextSpawnBin >= 0)
    {
        targetBin = _expansionCtrl.ConsumeOverrideNextSpawnBin();
    }
    else
    {
        targetBin = controller != null
            ? controller.GetBinForNextSpawn(this)
            : GetNextBinForSpawn();
    }
    
    if (targetBin >= loopMultiplier)
    {
        Debug.Log(
            $"[TRK:BURST] OUTCOME=STAGE_EXPAND track={name} burstId={burstId} " +
            $"targetBin={targetBin} loopMul={loopMultiplier} binSize={binSize} maxToSpawn={maxToSpawn}");

        var stagedBurst = new TrackExpansionController.PendingBurstData
        {
            noteSet               = noteSet,
            maxToSpawn            = maxToSpawn,
            burstId               = burstId,
            originWorld           = originWorld,
            repelFromWorld        = repelFromWorld,
            burstImpulse          = burstImpulse,
            spreadAngleDeg        = spreadAngleDeg,
            spawnJitterRadius     = spawnJitterRadius,
            placementMode         = placementMode,
            trapSearchRadiusCells = trapSearchRadiusCells,
            trapBufferCells       = trapBufferCells,
            intendedTargetBin     = targetBin,
        };

        Vector3 voidPos = originWorld ?? repelFromWorld ?? transform.position;

        bool staged = _expansionCtrl.TryStageExpand(stagedBurst, targetBin, voidPos);

        if (staged)
        {
            if (controller != null && drumTrack != null)
            {
                Vector2Int gp = drumTrack.WorldToGridPosition(voidPos);
                controller.BeginGravityVoidForPendingExpand(this, voidPos, gp);
            }

            foreach (var t in controller.tracks)
            {
                Debug.Log($"[RECOMPUTE] Attempting to recompute track {t}");
                if (t != null) controller.noteVisualizer.RecomputeTrackLayout(t);
            }

            return;
        }

        // TryStageExpand rejected (another expansion already pending for this track).
        // Fall through to SPAWN_NOW on an existing bin so collectables still appear.
        Debug.Log($"[TRK:BURST] STAGE_EXPAND rejected (already pending) → density spawn track={name} burstId={burstId}");
        targetBin = Mathf.Clamp(GetNextBinForSpawn(), 0, Mathf.Max(0, loopMultiplier - 1));
    }



    Debug.Log($"[TRK:BURST] OUTCOME=SPAWN_NOW track={name} burstId={burstId} targetBin={targetBin} loopMul={loopMultiplier} binSize={binSize} maxToSpawn={maxToSpawn}");

    // --- Composition mode: step-sequenced spawn (one collectable per loop step) ---
    if (controller != null && controller.noteCommitMode == NoteCommitMode.Composition)
    {
        EnqueueCompositionSpawns(noteSet, localSteps, targetBin, binSize, maxToSpawn,
            originWorld, spawnJitterRadius, burstId);
        return;
    }

    // --- Attempt spawns ---
    int spawnedCount = 0;
    var usedAbsSteps = new HashSet<int>();

    int gridW = drumTrack != null ? drumTrack.GetSpawnGridWidth() : 0;
    int gridH = drumTrack != null ? drumTrack.GetSpawnGridHeight() : 0;

    if (gridW <= 0 || gridH <= 0)
    {
        _currentBurstArmed = false;
        _currentBurstRemaining = 0;
        Debug.LogWarning($"[TRK:BURST] OUTCOME=ABORT track={name} burstId={burstId} reason=grid_invalid gridW={gridW} gridH={gridH}");
        return;
    }

    var dustGen = GameFlowManager.Instance != null ? GameFlowManager.Instance.dustGenerator : null;

    // redundant but keep (matches your structure)
    if (gridW <= 0)
    {
        _currentBurstArmed = false;
        _currentBurstRemaining = 0;
        Debug.LogWarning($"[TRK:BURST] OUTCOME=ABORT track={name} burstId={burstId} reason=gridW_invalid gridW={gridW} drumTrackNull={(drumTrack == null)}");
        return;
    }


    if (placementMode == BurstPlacementMode.TrappedInDustNearOrigin &&
        dustGen != null &&
        drumTrack != null &&
        originWorld.HasValue)
    {
        List <Vector2Int> trappedCandidates = BuildTrappedCandidatesNearOrigin(
            dustGen,
            drumTrack,
            originWorld.Value,
            gridW,
            gridH,
            trapSearchRadiusCells,
            trapBufferCells
        );

        if ((trappedCandidates == null || trappedCandidates.Count == 0) && trapBufferCells > 0)
        {
            trappedCandidates = BuildTrappedCandidatesNearOrigin(
                dustGen,
                drumTrack,
                originWorld.Value,
                gridW,
                gridH,
                trapSearchRadiusCells,
                trapBufferCells - 1
            );
        }
    }

    int originX = -1;
    if (originWorld.HasValue && drumTrack != null)
    {
        var og = drumTrack.WorldToGridPosition(originWorld.Value);
        originX = og.x;
        if (originX < 0 || originX >= gridW) originX = -1;
    }

    int imprintX = -1;
    if (repelFromWorld.HasValue && drumTrack != null)
    {
        var ig = drumTrack.WorldToGridPosition(repelFromWorld.Value);
        imprintX = ig.x;
        if (imprintX < 0 || imprintX >= gridW) imprintX = -1;
    }

    // --- choose anchor for 2D search ---
    Vector2Int anchor = new Vector2Int(gridW / 2, gridH / 2);

    if (originWorld.HasValue && drumTrack != null)
    {
        var og = drumTrack.WorldToGridPosition(originWorld.Value);
        if (og.x >= 0 && og.x < gridW && og.y >= 0 && og.y < gridH) anchor = og;
    }
    else if (repelFromWorld.HasValue && drumTrack != null)
    {
        var ig = drumTrack.WorldToGridPosition(repelFromWorld.Value);
        if (ig.x >= 0 && ig.x < gridW && ig.y >= 0 && ig.y < gridH) anchor = ig;
    }

    var usedCellsThisBurst = new HashSet<Vector2Int>();

    foreach (int step in localSteps)
    {
        if (maxToSpawn > 0 && spawnedCount >= maxToSpawn) break;

        int note = noteSet.GetNoteForPhaseAndRole(this, step);
        int dur = CalculateNoteDurationFromSteps(step, noteSet);

        int absStep = targetBin * binSize + step;

        if (!usedAbsSteps.Add(absStep))
        {
            Debug.LogWarning($"[SPAWN:STEP-COLLISION] track={name} burstId={burstId} targetBin={targetBin} binSize={binSize} step={step} -> absStep={absStep}");
            continue;
        }

        if (dustGen == null || drumTrack == null)
            continue;

        int fullSteps = BinSize() * maxLoopMultiplier;
        float stepNorm = fullSteps > 0 ? Mathf.Clamp01((float)absStep / fullSteps) : -1f;

        Vector2Int chosenCell;
        if (!TryPickRandomSpawnCell(dustGen, drumTrack, usedCellsThisBurst, out chosenCell, stepNorm))
            continue;

        usedCellsThisBurst.Add(chosenCell);
        Vector3 spawnPos = drumTrack.GridToWorldPosition(chosenCell);

        bool cellHasDust = dustGen != null && dustGen.HasDustAt(chosenCell);
        // Always jail the landing cell — drift can take up to ~7 s (spawnArrivalSeconds * 1.4).
        // Without this, dust that grows into an initially-empty cell causes physics depenetration
        // the moment _rb.simulated = true, which teleports the collectable.
        if (dustGen != null)
        {
            const float jailHoldSeconds = 10f;
            dustGen.CreateJailCenterForCollectable(chosenCell, jailHoldSeconds, ownerId: burstId);
        }

        if (originWorld.HasValue && spawnJitterRadius > 0f)
        {
            Vector2 j = UnityEngine.Random.insideUnitCircle * spawnJitterRadius;
            spawnPos += (Vector3)j;
        }

        Vector3 spawnOrigin = originWorld ?? transform.position;

        var go = Instantiate(collectablePrefab, spawnOrigin, Quaternion.identity, collectableParent);
        if (!go) continue;

        if (!go.TryGetComponent(out Collectable c))
        {
            Destroy(go);
            continue;
        }

// assign burst metadata BEFORE intro flight
        c.burstId = burstId;
        c.intendedStep = absStep;
        c.intendedBin = targetBin;
        c.assignedInstrumentTrack = this;

        _scratchSteps.Clear();
        _scratchSteps.Add(absStep);

        c.isTrappedInDust = cellHasDust;
        if (_destroyHandlers.TryGetValue(c, out var oldHandler) && oldHandler != null)
        {
            c.OnDestroyed -= oldHandler;
            _destroyHandlers.Remove(c);
        }

        Action handler = () => OnCollectableDestroyed(c);
        _destroyHandlers[c] = handler;
        c.OnDestroyed += handler;

        // Marker + tether
        var markerGO = nv.PlacePersistentNoteMarker(this, absStep, lit: false, burstId);
        if (markerGO)
        {
            var tag = markerGO.GetComponent<MarkerTag>() ?? markerGO.AddComponent<MarkerTag>();
            tag.track = this;
            tag.step = absStep;
            tag.burstId = burstId;
            tag.isPlaceholder = true;

            var ml = markerGO.GetComponent<MarkerLight>() ?? markerGO.AddComponent<MarkerLight>();
            ml.SetGrey(new Color(1f, 1f, 1f, 0.25f));
            c.BindMarkerAtSpawn(markerGO.transform, absStep);
        }
        c.ApplyTrackVisuals(this);
// Begin intro flight from MineNode explosion site -> chosen grid spawn pos
        c.BeginSpawnArrival(
            spawnOrigin,
            spawnPos,
            note,
            dur,
            this,
            noteSet,
            _scratchSteps
        );
        spawnedCollectables.Add(go);
        _currentBurstRemaining++;
        spawnedCount++;
    }

    // --- Empty burst handling ---
    if (spawnedCount <= 0)
    {
        _currentBurstArmed = false;
        _currentBurstRemaining = 0;

        controller?.noteVisualizer?.CanonicalizeTrackMarkers(this, currentBurstId);

        OnCollectableBurstCleared?.Invoke(this, burstId, false);
        Debug.LogWarning($"[TRK:BURST] OUTCOME=SPAWN_EMPTY_CLEARED track={name} burstId={burstId} targetBin={targetBin} binSize={binSize} steps={stepList.Count} gridW={gridW}");
        return;
    }

    Debug.Log($"[TRK:BURST] OUTCOME=SPAWN_OK track={name} burstId={burstId} spawnedCount={spawnedCount} targetBin={targetBin} binSize={binSize} loopMul={loopMultiplier}");

    _burstRemaining[burstId] = spawnedCount;
    _burstTotalSpawned[burstId] = spawnedCount;
    _burstCollected[burstId] = 0;

    SetBinAllocated(targetBin, true);
    _burstTargetBin[burstId] = targetBin; // track for rollback on 0-note discard

// Keep cursor moving forward once we allocate a bin.
// Without this, cursor can stay at 0 and repeatedly bias selection at boundaries.
    if (GetBinCursor() <= targetBin)
        SetBinCursor(targetBin + 1);
    if (loopMultiplier > 0 && GetBinCursor() > loopMultiplier)
        SetBinCursor(loopMultiplier);

    controller?.noteVisualizer?.CanonicalizeTrackMarkers(this, currentBurstId);
}
    private bool TryPickRandomSpawnCell(
        CosmicDustGenerator dustGen,
        DrumTrack drumTrack,
        HashSet<Vector2Int> usedCellsThisBurst,
        out Vector2Int chosenCell,
        float preferredXNorm = -1f)
    {
        chosenCell = default;

        int w = drumTrack != null ? drumTrack.GetSpawnGridWidth() : 0;
        int h = drumTrack != null ? drumTrack.GetSpawnGridHeight() : 0;
        if (w <= 0 || h <= 0) return false;

        float cellWorld = Mathf.Max(0.001f, drumTrack.GetCellWorldSize());
        Vector2 halfExtents = Vector2.one * (cellWorld * 0.45f);

        int xBandCenter = -1;
        int xBandHalf   = 0;
        bool useBand = preferredXNorm >= 0f && spawnColumnBandFraction > 0f;
        if (useBand)
        {
            xBandCenter = Mathf.Clamp(Mathf.RoundToInt(preferredXNorm * (w - 1)), 0, w - 1);
            xBandHalf   = Mathf.Max(1, Mathf.RoundToInt(spawnColumnBandFraction * 0.5f * w));
        }

        int maxTries = Mathf.Max(8, spawnPickMaxTries);

        // First pass: constrained to step column band (skipped if no band preference).
        if (useBand)
        {
            int xMin = Mathf.Max(0, xBandCenter - xBandHalf);
            int xMax = Mathf.Min(w - 1, xBandCenter + xBandHalf);
            for (int attempt = 0; attempt < maxTries; attempt++)
            {
                var gp = new Vector2Int(UnityEngine.Random.Range(xMin, xMax + 1), UnityEngine.Random.Range(0, h));
                if (usedCellsThisBurst != null && usedCellsThisBurst.Contains(gp)) continue;
                if (!Collectable.IsCellFreeStatic(gp)) continue;
                Vector2 wp = (Vector2)drumTrack.GridToWorldPosition(gp);
                if (Physics2D.OverlapBox(wp, halfExtents * 2f, 0f, spawnBlockedMask) != null) continue;
                chosenCell = gp;
                return true;
            }
        }

        // Fallback (or default): unconstrained pick across full grid.
        for (int attempt = 0; attempt < maxTries; attempt++)
        {
            var gp = new Vector2Int(UnityEngine.Random.Range(0, w), UnityEngine.Random.Range(0, h));

            if (usedCellsThisBurst != null && usedCellsThisBurst.Contains(gp))
                continue;

            // Avoid stacking collectables.
            if (!Collectable.IsCellFreeStatic(gp))
                continue;

            // Avoid PhaseStar / Vehicle (or any other blockers you include in the mask).
            Vector2 wp = (Vector2)drumTrack.GridToWorldPosition(gp);
            Collider2D hit = Physics2D.OverlapBox(wp, halfExtents * 2f, 0f, spawnBlockedMask);
            if (hit != null)
                continue;

            chosenCell = gp;
            return true;
        }

        return false;
    }

    // -------------------------------------------------------------------------
    // Composition Mode: step-sequenced burst helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Pre-allocates grid cells for a burst and queues one PendingCompositionLaunch per step.
    /// Each collectable launches (with SFX) when the loop's step counter reaches its absStep.
    /// </summary>
    private void EnqueueCompositionSpawns(
        NoteSet noteSet,
        List<int> localSteps,
        int targetBin,
        int binSize,
        int maxToSpawn,
        Vector3? originWorld,
        float spawnJitterRadius,
        int burstId)
    {
        var dustGen   = GameFlowManager.Instance != null ? GameFlowManager.Instance.dustGenerator : null;
        var nv        = controller?.noteVisualizer;
        int gridW     = drumTrack != null ? drumTrack.GetSpawnGridWidth()  : 0;
        int gridH     = drumTrack != null ? drumTrack.GetSpawnGridHeight() : 0;

        if (gridW <= 0 || gridH <= 0 || dustGen == null || drumTrack == null)
        {
            _currentBurstArmed    = false;
            _currentBurstRemaining = 0;
            Debug.LogWarning($"[TRK:COMP] ABORT track={name} burstId={burstId} reason=grid_or_dust_invalid");
            return;
        }

        var usedCells   = new HashSet<Vector2Int>();
        var usedAbsSteps = new HashSet<int>();
        int queued = 0;

        int fullSteps = BinSize() * maxLoopMultiplier;

        foreach (int step in localSteps)
        {
            if (maxToSpawn > 0 && queued >= maxToSpawn) break;

            int absStep = targetBin * binSize + step;
            if (!usedAbsSteps.Add(absStep)) continue;

            int note = noteSet.GetNoteForPhaseAndRole(this, step);
            int dur  = CalculateNoteDurationFromSteps(step, noteSet);

            float stepNorm = fullSteps > 0 ? Mathf.Clamp01((float)absStep / fullSteps) : -1f;

            Vector2Int cell;
            if (!TryPickRandomSpawnCell(dustGen, drumTrack, usedCells, out cell, stepNorm)) continue;
            usedCells.Add(cell);

            bool hasDust = dustGen.HasDustAt(cell);
            if (hasDust)
            {
                const float jailHold = 4f;
                dustGen.CreateJailCenterForCollectable(cell, jailHold, ownerId: burstId);
            }

            Vector3 targetWorld = drumTrack.GridToWorldPosition(cell);
            if (originWorld.HasValue && spawnJitterRadius > 0f)
                targetWorld += (Vector3)(UnityEngine.Random.insideUnitCircle * spawnJitterRadius);

            _pendingCompositionLaunches.Add(new PendingCompositionLaunch
            {
                absStep     = absStep,
                note        = note,
                duration    = dur,
                originWorld = originWorld ?? transform.position,
                targetWorld = targetWorld,
                targetCell  = cell,
                cellHasDust = hasDust,
                noteSet     = noteSet,
                burstId     = burstId,
            });
            queued++;
        }

        if (queued <= 0)
        {
            _currentBurstArmed    = false;
            _currentBurstRemaining = 0;
            OnCollectableBurstCleared?.Invoke(this, burstId, false);
            Debug.LogWarning($"[TRK:COMP] EMPTY track={name} burstId={burstId}");
            return;
        }

        // Burst bookkeeping (mirrors existing SPAWN_NOW path).
        _burstRemaining[burstId]    = queued;
        _burstTotalSpawned[burstId] = queued;
        _burstCollected[burstId]    = 0;
        _currentBurstRemaining      = queued;

        SetBinAllocated(targetBin, true);
        _burstTargetBin[burstId] = targetBin;
        if (GetBinCursor() <= targetBin)  SetBinCursor(targetBin + 1);
        if (loopMultiplier > 0 && GetBinCursor() > loopMultiplier) SetBinCursor(loopMultiplier);

        // Subscribe to per-step events (idempotent).
        if (!_compositionStepListenerActive && drumTrack != null)
        {
            drumTrack.OnStepChanged += OnCompositionStepFired;
            _compositionStepListenerActive = true;
        }

        // Spawn the origin effect at MineNode destruction time (now), not at first step launch.
        if (_compositionSpawnEffect == null && compositionSpawnEffectPrefab != null)
        {
            Vector3 effectPos = originWorld ?? transform.position;
            _compositionSpawnEffect = Instantiate(compositionSpawnEffectPrefab, effectPos, Quaternion.identity);
            var main = _compositionSpawnEffect.main;
            main.startColor = new ParticleSystem.MinMaxGradient(trackColor);
            main.stopAction = ParticleSystemStopAction.Destroy;
            _compositionSpawnEffect.Play();
        }

        Debug.Log($"[TRK:COMP] QUEUED track={name} burstId={burstId} count={queued} targetBin={targetBin}");
    }

    /// <summary>
    /// Fires when the drum transport advances a step. Launches any pending collectable
    /// whose absStep matches the current step index.
    /// </summary>
    private void OnCompositionStepFired(int step, int leaderSteps)
    {
        for (int i = _pendingCompositionLaunches.Count - 1; i >= 0; i--)
        {
            var launch = _pendingCompositionLaunches[i];
            if (launch.absStep != step) continue;

            _pendingCompositionLaunches.RemoveAt(i);

            var nv = controller?.noteVisualizer;

            // Instantiate collectable at the MineNode origin.
            var go = Instantiate(collectablePrefab, launch.originWorld, Quaternion.identity, collectableParent);
            if (!go) continue;
            if (!go.TryGetComponent(out Collectable c)) { Destroy(go); continue; }

            c.burstId                = launch.burstId;
            c.intendedStep           = launch.absStep;
            c.intendedBin            = _burstTargetBin.TryGetValue(launch.burstId, out var bin) ? bin : 0;
            c.assignedInstrumentTrack = this;
            c.isTrappedInDust        = launch.cellHasDust;

            if (_destroyHandlers.TryGetValue(c, out var oldH) && oldH != null)
            {
                c.OnDestroyed -= oldH;
                _destroyHandlers.Remove(c);
            }
            Action handler = () => OnCollectableDestroyed(c);
            _destroyHandlers[c] = handler;
            c.OnDestroyed += handler;

            // Per-launch marker (appears as the collectable launches, not upfront).
            if (nv != null)
            {
                var markerGO = nv.PlacePersistentNoteMarker(this, launch.absStep, lit: false, launch.burstId);
                if (markerGO)
                {
                    var tag = markerGO.GetComponent<MarkerTag>() ?? markerGO.AddComponent<MarkerTag>();
                    tag.track         = this;
                    tag.step          = launch.absStep;
                    tag.burstId       = launch.burstId;
                    tag.isPlaceholder = true;
                    var ml = markerGO.GetComponent<MarkerLight>() ?? markerGO.AddComponent<MarkerLight>();
                    ml.SetGrey(new Color(1f, 1f, 1f, 0.25f));
                    c.BindMarkerAtSpawn(markerGO.transform, launch.absStep);
                }
            }

            c.ApplyTrackVisuals(this);

            // SFX: play the authored note as the collectable launches (one-time MIDI playthrough).
            PlayOneShotMidi(launch.note, 100f, launch.duration);

            // Begin intro flight from origin to grid cell.
            _scratchSteps.Clear();
            _scratchSteps.Add(launch.absStep);
            c.BeginSpawnArrival(launch.originWorld, launch.targetWorld,
                launch.note, launch.duration, this, launch.noteSet, _scratchSteps);

            spawnedCollectables.Add(go);
        }

        // Unsubscribe once all pending launches have fired.
        if (_pendingCompositionLaunches.Count == 0 && _compositionStepListenerActive)
        {
            if (drumTrack != null) drumTrack.OnStepChanged -= OnCompositionStepFired;
            _compositionStepListenerActive = false;
            controller?.noteVisualizer?.CanonicalizeTrackMarkers(this, currentBurstId);

            if (_compositionSpawnEffect != null)
            {
                _compositionSpawnEffect.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                _compositionSpawnEffect = null;
            }
        }
    }

    public float GetSecondsUntilNextLoopBoundaryDSP()
    {
        if (drumTrack == null) return 0f;

        double dspNow = AudioSettings.dspTime;

        // OnLoopBoundary is based on leaderStartDspTime + EffectiveLoopLengthSec.
        double start = (drumTrack.leaderStartDspTime > 0.0) ? drumTrack.leaderStartDspTime : drumTrack.startDspTime;
        if (start <= 0.0) return 0f;

        float loopLen = drumTrack.GetLoopLengthInSeconds(); // this is EffectiveLoopLengthSec
        if (loopLen <= 0f) return 0f;

        double elapsed = dspNow - start;
        if (elapsed < 0.0) elapsed = 0.0;

        // Next leader boundary
        double loopsCompleted = System.Math.Floor(elapsed / loopLen);
        double nextBoundary = start + (loopsCompleted + 1.0) * loopLen;

        double secs = nextBoundary - dspNow;
        if (secs < 0.0) secs = 0.0;

        return (float)secs;
    }
    public bool IsSaturatedForRepeatingNoteSet(NoteSet incoming)
    {
        if (incoming == null) return false; 
        if (persistentLoopNotes == null || persistentLoopNotes.Count == 0) return false;
        int activeBins = Mathf.Max(1, loopMultiplier); 
        int bSz        = Mathf.Max(1, BinSize()); 
        bool anyBinChecked = false;
        var occupied = new HashSet<int>(persistentLoopNotes.Select(n => n.stepIndex));
        
        for (int b = 0; b < activeBins; b++) { 
            if (!IsBinFilled(b)) continue;  // bin exists but player hasn't harvested it yet — skip
            var binNoteSet = GetNoteSetForBin(b) ?? incoming; 
            binNoteSet.Initialize(this, bSz); 
            var allowed = binNoteSet.GetStepList(); 
            if (allowed == null || allowed.Count == 0) continue;
            
            foreach (int localStep in allowed) { 
                int absStep = b * bSz + (localStep % bSz); 
                if (!occupied.Contains(absStep)) return false;
            } 
            anyBinChecked = true;
        }
        // If no filled bins exist yet, we are not saturated.
        return anyBinChecked;
    }
    private void OnCollectableDestroyed(Collectable c) { 
        if (c == null) return; 
        if (c.assignedInstrumentTrack != this) return;
        if (c.burstId == 0) return;
        if (c.ReportedCollected) return;
        if (_destroyHandlers.TryGetValue(c, out var h) && h != null)
        {
            c.OnDestroyed -= h;
            _destroyHandlers.Remove(c);
        }
        Debug.LogWarning($"[COLLECT:LOST] {name} burstId={c.burstId} intendedStep={c.intendedStep}");
        // Decrement remaining just like a collection would, so the burst can clear.
        if (_burstRemaining.TryGetValue(c.burstId, out var rem)) {
            rem--; 
            if (rem <= 0) { 
                _burstRemaining.Remove(c.burstId); 
                _burstTotalSpawned.Remove(c.burstId); 
//                _burstCollected.Remove(c.burstId);
                // Check whether any notes were actually written for this burst.
                int collected = 0; 
                _burstCollected.TryGetValue(c.burstId, out collected); 
                _burstCollected.Remove(c.burstId);
                if (collected > 0) { 
                    // At least one note was harvested — treat bin as partially filled
                    // and let the normal progression continue.
                    if (_burstWroteBin.TryGetValue(c.burstId, out var filledBin)) 
                        SetBinFilled(filledBin, true);
                    if (controller != null) controller.AllowAdvanceNextBurst(this); 
                    AdvanceBinCursor(1);
                    OnCollectableBurstCleared?.Invoke(this, c.burstId, true);
                } 
                else { 
                    // Zero notes collected: the entire burst was lost.
                    // Do NOT mark the bin filled, do NOT advance the cursor, and do NOT
                    // fire OnCollectableBurstCleared — the star should re-arm for this
                    // bin rather than bridging on an empty loop.
                    Debug.LogWarning($"[COLLECT:LOST_BURST] {name} burstId={c.burstId} all collectables lost with zero notes written. Bin will not be marked filled; star will re-arm.");
                    // Clean up remaining burst tracking state.
                    _burstWroteBin.Remove(c.burstId); 
                    _burstLeaderBinsBeforeWrite.Remove(c.burstId);
                    // Notify controller that collectables are cleared so the star can
                    // re-arm, but do not advance bin state.
                    if (controller != null) controller.AllowAdvanceNextBurst(this);
                    OnCollectableBurstCleared?.Invoke(this, c.burstId, false);
                }
            }
            else { 
                _burstRemaining[c.burstId] = rem;
            }
        }
    }

    private void RecomputeBinsFromLoop()
    {
        EnsureBinList();
        for (int i = 0; i < _binFilled.Count; i++)
        {
            bool wasFilled = _binFilled[i];
            _binFilled[i] = false;
            // If we're clearing a bin that was previously filled, also clear its completion time.
            if (wasFilled && _binCompletionTime != null && i < _binCompletionTime.Length)
                _binCompletionTime[i] = -1f;
        }

        foreach (var (step, _, _, _, _) in persistentLoopNotes)
        {
            int b = BinIndexForStep(step);
            if (b >= 0 && b < _binFilled.Count) _binFilled[b] = true;
            // Note: completion time is not re-stamped here — recompute is a structural pass,
            // not a new fill event. Times from the original fill are preserved if bin stays filled.
        }

        // Keep loop span derived from the highest FILLED bin (not the old multiplier)
      //  SyncSpanFromBins();
      _totalSteps = loopMultiplier * BinSize();
    }
    
    private void RebuildLoopFromModifiedNotes(List<(int, int, int, float)> modified, Vector3 _)
    {
        Debug.LogWarning(
            $"[TRK:CLEAR_LOOP] track={name} fn=RebuildLoopFromModifiedNotes " +
            $"modified={(modified != null ? modified.Count : -1)} " +
            $"persistentBefore={(persistentLoopNotes != null ? persistentLoopNotes.Count : -1)}\n" +
            Environment.StackTrace);

        // Preserve original commit times — chord retuning changes pitch but not when the
        // player collected the note. Restoring these keeps CommitTime01 non-zero in the
        // ring glyph so squiggles are visible after a chord change.
        var savedCommitTimes = new Dictionary<int, float>(_noteCommitTimes);

        persistentLoopNotes.Clear();
        _noteCommitTimes.Clear();
        _loopCacheDirtyPending = true;
        foreach (var obj in _spawnedNotes) if (obj) Destroy(obj);
        _spawnedNotes.Clear();

        if (modified != null)
        {
            foreach (var (step, note, dur, vel) in modified)
            {
                // skipChordQuantize=true: notes in `modified` are already at their final pitch;
                // re-quantizing here would double-process them and produce wrong results.
                AddNoteToLoop(step, note, dur, vel, true, skipChordQuantize: true);
                if (savedCommitTimes.TryGetValue(step, out float originalTime))
                    _noteCommitTimes[step] = originalTime;
            }
        }
    }

    public void PruneSpawnedCollectables()
    {
        if (spawnedCollectables == null) return;

        // Remove nulls and inactive pooled objects so controller doesn't think they're "in flight"
        spawnedCollectables.RemoveAll(go => go == null || !go.activeInHierarchy);
    }

    private int CollectNote(int stepIndex, int note, int durationTicks, float force, int authoredRootMidi = int.MinValue)
    {
        Debug.Log($"[COLLECT] Adding Note {note} to Loop at Index {stepIndex} for {durationTicks}");
        // Commit immediately (the loop evolves as notes are collected).
        AddNoteToLoop(stepIndex, note, durationTicks, force, false, authoredRootMidi);

        // Immediate tactile feedback (short), then audition on-grid (full voice).
        PlayCollectionConfirm(note, force);
        //QuantizedAuditionToStep(stepIndex, note, durationTicks, force);

        return stepIndex;
    }
    
    private void PlayCollectionConfirm(int note, float velocity)
    {
        if (midiVoice == null) return;
        midiVoice.PlayCollectionConfirm(note, velocity);
    }
    
    private int FirstEmptyBin()
    {
        for (int i = 0; i < _binFilled.Count; i++)
            if (!_binFilled[i]) return i;
        return -1;
    }

    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    private void DebugBins(string where)
    {
        string s = string.Join("", _binFilled.Select(b => b ? "1" : "0"));
        Debug.Log($"[{name}] {where} | cursor={_binCursor} loopMul={loopMultiplier} filled={s} firstEmpty={FirstEmptyBin()}");
    }
}
