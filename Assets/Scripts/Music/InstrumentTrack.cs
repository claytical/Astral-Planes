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

public partial class InstrumentTrack : MonoBehaviour, IExpansionHost
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
    public int voiceIndex { get; private set; }
    public void SetVoiceIndex(int index) => voiceIndex = index;
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
    private GameFlowManager _gfm;
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
    private readonly HashSet<int> _gateReleasedBurstIds = new(); // burst ids whose spawn gate has been released without note-discard
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
    public event Action<InstrumentTrack, int> OnBinFilled; // (track, binIndex)

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
        if (GameFlowManager.VerboseLogging) Debug.Log(
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
    public void InstantFillAllBins(bool toMaxCapacity = false)
    {
        int binSz = BinSize();
        int bins  = toMaxCapacity ? Mathf.Max(1, maxLoopMultiplier) : Mathf.Max(1, loopMultiplier);

        // When expanding to max capacity, only fill bins above the current count so that the
        // player's already-collected notes in existing bins survive the fill and are restored
        // correctly when InstantCollapseToLoopMultiplier reverts this track at the boundary.
        int fillFrom = toMaxCapacity ? loopMultiplier : 0;

        if (bins > loopMultiplier)
        {
            loopMultiplier = bins;
            _totalSteps    = binSz * bins;
            EnsureBinList();
            for (int b = 0; b < bins; b++)
                SetBinAllocated(b, true);
        }

        // Clear only the bins we are about to fill (preserves existing bins when toMaxCapacity).
        for (int b = fillFrom; b < bins; b++)
            ClearBinNotesKeepAllocated(b);

        // Write authored notes bin-by-bin (new bins only when toMaxCapacity).
        for (int b = fillFrom; b < bins; b++)
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
        controller?.EndGravityVoidForPendingExpand(this);
        controller?.UpdateVisualizer();
        // Sync leader bin count immediately when loopMultiplier expanded beyond the previous
        // committed value. Without this, GetCommittedBinCount() stays at the old value for up
        // to one full leader loop, causing barIndex >= committedLeaderBins to fire on the extra
        // bars and silence all tracks for half the extended loop.
        if (toMaxCapacity) controller?.ResyncLeaderBinsNow();
    }

    /// <summary>
    /// Instantly contracts this track to <paramref name="targetMult"/> bins, clearing any notes
    /// and allocation state above that count. Mirrors OnDrumDownbeat_CommitCollapse but runs
    /// synchronously. Called by SuperNode when reverting a temporarily-filled track.
    /// </summary>
    public void InstantCollapseToLoopMultiplier(int targetMult)
    {
        // 0 is valid: track had no active bins before the SuperNode; restore to silent empty state.
        targetMult = Mathf.Clamp(targetMult, 0, loopMultiplier);

        // Keep at least 1 bin so the scheduler never divides by zero.
        int effectiveMult = Mathf.Max(1, targetMult);

        bool atTarget   = effectiveMult == loopMultiplier;
        bool notesClean = persistentLoopNotes == null || persistentLoopNotes.Count == 0;
        if (atTarget && (targetMult > 0 || notesClean)) return;

        loopMultiplier = effectiveMult;

        EnsureBinList();
        // When targetMult=0 clear ALL bins (track was empty before SuperNode).
        // When targetMult>0 clear only the bins above the new count.
        ClearBinsAbove(targetMult);

        _totalSteps = BinSize() * loopMultiplier;

        if (targetMult == 0)
            persistentLoopNotes.Clear();  // bin 0 keeps its allocation slot but must be silent
        else
            persistentLoopNotes.RemoveAll(t => t.stepIndex >= _totalSteps);

        _loopCacheDirtyPending = true;
        RecomputeBinsFromLoop();
        RebuildLoopCache_FORCE();
        _loopCacheDirtyPending = false;

        if (controller != null && controller.noteVisualizer != null && drumTrack != null)
        {
            int leaderSteps = drumTrack.GetLeaderSteps();
            if (leaderSteps <= 0) leaderSteps = _totalSteps;
            controller.noteVisualizer.RequestLeaderGridChange(leaderSteps);
            controller.noteVisualizer.ForceSyncMarkersToPersistentLoop(this);
        }

        controller?.UpdateVisualizer();
        controller?.ResyncLeaderBinsNow();
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
    /// Returns NoteEntry objects for all persistent notes in the given bin.
    /// Safe to call immediately after OnBinFilled fires.
    /// </summary>
    public List<MotifSnapshot.NoteEntry> GetBinNoteEntries(int binIndex)
    {
        var result = new List<MotifSnapshot.NoteEntry>();
        var notes = GetPersistentLoopNotes();
        if (notes == null || notes.Count == 0) return result;

        int binSize = Mathf.Max(1, BinSize());
        foreach (var n in notes)
        {
            if (n.stepIndex / binSize != binIndex) continue;
            int localStep = n.stepIndex % binSize;
            float commitTime01 = (binSize > 1) ? localStep / (float)(binSize - 1) : 0.5f;
            result.Add(new MotifSnapshot.NoteEntry(
                step: n.stepIndex,
                note: n.note,
                velocity: n.velocity,
                trackColor: trackColor,
                binIndex: binIndex,
                isMatched: true,
                commitTime01: commitTime01));
        }
        return result;
    }

    /// <summary>
    /// Returns the Time.time value recorded when the note at <paramref name="stepIndex"/> was committed
    /// to the persistent loop. Returns -1 if no commit time is recorded for that step.
    /// </summary>
    public float GetNoteCommitTime(int stepIndex) =>
        _noteCommitTimes.TryGetValue(stepIndex, out var t) ? t : -1f;
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

        int globalBin = WrapIndex(playheadBin, Mathf.Max(1, leaderBins));

        // Silence if leader is ahead of this track's allocated extent,
        // or if the bin has no committed notes yet.
        if (globalBin >= loopMultiplier || !HasAnyNoteInBin(globalBin))
        {
            if (localStep == 0)
                if (GameFlowManager.VerboseLogging) Debug.Log($"[SYNC] {name} BLOCKED bin={globalBin} loopMul={loopMultiplier} filled={IsBinFilled(globalBin)} hasNotes={HasAnyNoteInBin(globalBin)} leaderBins={leaderBins}");
            return;
        }

        if (localStep == 0)
            if (GameFlowManager.VerboseLogging) Debug.Log($"[SYNC] {name} PLAYING bin={globalBin} loopMul={loopMultiplier} filled={IsBinFilled(globalBin)} hasNotes={HasAnyNoteInBin(globalBin)} leaderBins={leaderBins}");

        int trackBin = globalBin;
        float gain = 1f;

        if (localStep == 0)
        {
            int chordIdx = Harmony_GetChordIndexForBin(globalBin);
            if (_gfm == null) _gfm = GameFlowManager.Instance;
            var hd = _gfm?.harmony;
            if (hd != null && hd.TryGetChordAt(chordIdx, out var c))
                if (GameFlowManager.VerboseLogging) Debug.Log($"[CHORD][TRK][Play] track={name} playheadBin={playheadBin} trackBin={trackBin} loopMul={loopMultiplier} chordIdx={chordIdx} chordRoot={c.rootNote}");
            else
                if (GameFlowManager.VerboseLogging) Debug.Log($"[CHORD][TRK][Play] track={name} playheadBin={playheadBin} trackBin={trackBin} loopMul={loopMultiplier} chordIdx={chordIdx} chordRoot=<na>");
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
        // IMPORTANT: don't leave the controller stuck because remaining counts still think something is pending.
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
            ClearBinsAbove(newMult);

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
        if (loopMultiplier <= 1 || _pendingCollapse || IsExpansionPending) return;
        _collapseTargetMultiplier = loopMultiplier - 1;
        _pendingCollapse = true;
        if (!_hookedBoundaryForCollapse && drumTrack != null)
        {
            drumTrack.OnLoopBoundary += OnDrumDownbeat_CommitCollapse;
            _hookedBoundaryForCollapse = true;
        }
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

        if (GameFlowManager.VerboseLogging) Debug.Log($"[CHORD][ARMED] {name} window=[{windowStartInclusive},{windowEndExclusive}) " +
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
            var (step, _, _, _, _) = persistentLoopNotes[i];
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
    public void OnCollectableCollected(Collectable collectable, int reportedStep, int durationTicks, float force)
    {
        if (collectable == null || collectable.assignedInstrumentTrack != this) return;
        controller.NotifyCollected(this);

        // 1) Track burst energy meter
        IncrementBurstCollectedMeter(collectable.burstId);

        // 2) Free the vacated grid cell
        if (drumTrack != null)
        {
            Vector2Int gridPos = drumTrack.WorldToGridPosition(collectable.transform.position);
            drumTrack.FreeSpawnCell(gridPos.x, gridPos.y);
            drumTrack.ResetSpawnCellBehavior(gridPos.x, gridPos.y);
        }

        // 3) Resolve authoritative target step (never time-based)
        int finalTargetStep = ResolveTargetStep(collectable, reportedStep);
        if (finalTargetStep < 0) return;

        if (reportedStep >= 0 && collectable.intendedStep >= 0 && reportedStep != collectable.intendedStep)
            Debug.LogWarning($"[COLLECT:MISMATCH] {name} reportedStep={reportedStep} intended={collectable.intendedStep} burstId={collectable.burstId}");

        // 4) Snapshot leader bins before first write (for cross-track nudge)
        SnapshotBurstLeaderBins(collectable.burstId, finalTargetStep);

        // 5) Commit note to the audible loop
        int note = collectable.GetNote();
        int authoredRootMidi = LookUpAuthoredRootMidi(finalTargetStep);
        CollectNote(finalTargetStep, note, durationTicks, force, authoredRootMidi);

        int targetBin = BinIndexForStep(finalTargetStep);
        if (GameFlowManager.VerboseLogging) Debug.Log($"[CURSOR] Target Bin={targetBin} binCursor: {_binCursor} allocated: {binAllocated} filled: {_binFilled}");
        RegisterBurstStep(collectable.burstId, finalTargetStep);
        spawnedCollectables?.Remove(collectable.gameObject);

        // 6) Per-burst decrement — on last note: fill bin and clean up
        if (collectable.burstId != 0 && _burstRemaining.TryGetValue(collectable.burstId, out var rem))
        {
            rem--;
            if (rem <= 0)
            {
                int filledBin = _burstWroteBin.TryGetValue(collectable.burstId, out var b) ? b : targetBin;

                // Unique to auto-collect: playhead pulse + harmony hook
                controller?.noteVisualizer?.TriggerPlayheadReleasePulse(assignedRole);
                if (_gfm == null) _gfm = GameFlowManager.Instance;
                Harmony_OnBinFilled(filledBin, _gfm?.harmony?.ProgressionLength ?? 0);

                // Unique to auto-collect: extra dict cleanup before shared completion
                _burstTotalSpawned.Remove(collectable.burstId);
                _burstCollected.Remove(collectable.burstId);

                // Unique to auto-collect: cursor advance (ByVoice tracks suppress this)
                var rp = MusicalRoleProfileLibrary.GetProfile(assignedRole);
                if (rp == null || rp.configSelectionMode != RoleConfigSelectionMode.ByVoice)
                    AdvanceBinCursor(1);

                // Precompute extendedLeader before CompleteBurst removes the dicts
                bool extendedLeader =
                    _burstLeaderBinsBeforeWrite.TryGetValue(collectable.burstId, out var Lbefore) &&
                    _burstWroteBin.TryGetValue(collectable.burstId, out var wroteBin) &&
                    wroteBin >= Lbefore;

                if (GameFlowManager.VerboseLogging) Debug.Log($"[TRK:BURST_CLEARED] track={name} burstId={collectable.burstId} reportedStep={reportedStep} remainingOnTrack={spawnedCollectables.Count} binCursor={_binCursor}");

                CompleteBurst(collectable.burstId, filledBin, hadNotes: true, ascendBinCount: loopMultiplier);

                // Unique to auto-collect: advance sibling cursors if this burst extended the leader
                if (extendedLeader && controller != null)
                    controller.AdvanceOtherTrackCursors(this, 1);
            }
            else
            {
                _burstRemaining[collectable.burstId] = rem;
            }
        }

        // 7) Place marker and schedule deposit animation
        GameObject markerGo = null;
        if (controller != null && controller.noteVisualizer != null)
            markerGo = controller.noteVisualizer.PlacePersistentNoteMarker(this, finalTargetStep, lit: false, burstId: collectable.burstId);

        if (markerGo != null)
            collectable.ribbonMarker = markerGo.transform;

        double depositDsp = ComputeDepositDsp(finalTargetStep);

        const float orbitMax   = 0.65f;
        const float travelHard = 0.35f;
        const float minTravel  = 0.02f;

        double now = AudioSettings.dspTime;
        float dt = Mathf.Max(0f, (float)(depositDsp - now));
        float travelSec = Mathf.Clamp(Mathf.Min(travelHard, dt), minTravel, travelHard);
        float orbitSec  = Mathf.Clamp(dt - travelSec, 0f, orbitMax);
        if (orbitSec < 0.05f) orbitSec = 0f;

        if (GameFlowManager.VerboseLogging) Debug.Log($"[DEPOSIT] track={name} stepAbs={finalTargetStep} stepLocal={reportedStep} intendedBin={collectable.intendedBin} depositDsp={depositDsp:F6} now={now:F6} dt={(depositDsp - now):F4}");

        collectable.BeginCarryThenDepositAtDsp(depositDsp, durationTicks: durationTicks, force: force, onArrived: () =>
        {
            if (controller != null)
                controller.NotifyCommitted(this, finalTargetStep);

            if (controller == null || controller.noteVisualizer == null || markerGo == null)
                return;

            controller.noteVisualizer.ScheduleFirstPlayConfirm(
                source: collectable.transform,
                track: this,
                step: finalTargetStep,
                dspTime: depositDsp,
                noteDuration: durationTicks,
                color: trackColor);

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
        });

        if (_currentBurstArmed && collectable.burstId == currentBurstId)
        {
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
        // Prefer the bin-specific NoteSet's stored authoredRootMidi (set by NoteSetFactory to
        // the exact bin chord root), so QuantizeNoteToBinChord sees rootDelta = 0.
        // Fall back to HarmonyDirector nudging only when no bin-specific NoteSet is available.
        int rootInRegister = int.MinValue;
        if (authoredAbs >= 0)
        {
            int authoredBin = BinIndexForStep(authoredAbs);
            if (_binNoteSets != null && authoredBin >= 0 && authoredBin < _binNoteSets.Length && _binNoteSets[authoredBin] != null)
                rootInRegister = _binNoteSets[authoredBin].GetAuthoredRootMidi(authoredLocal);
        }
        if (rootInRegister == int.MinValue && rootShiftNotesByChord && authoredAbs >= 0)
        {
            if (_gfm == null) _gfm = GameFlowManager.Instance;
            var hd = _gfm?.harmony;
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
        if (rootInRegister == int.MinValue)
            rootInRegister = GetAuthoredRootMidiInRegister(collectedMidi);

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
        IncrementBurstCollectedMeter(burstId);

        // Snapshot leader bins before first write (for cross-track nudge)
        SnapshotBurstLeaderBins(burstId, stepAbs);

        // Replace-or-add: enforce one persistent note per (track, stepAbs) for stability.
        persistentLoopNotes.RemoveAll(t => t.stepIndex == stepAbs);
        AddNoteToLoop(stepAbs, midiNote, durationTicks, velocity127, lightMarkerNow, authoredRootMidi, skipChordQuantize);

        int targetBin = BinIndexForStep(stepAbs);
        RegisterBurstStep(burstId, stepAbs);

        // Per-burst decrement — on last note: fill bin and clean up
        if (burstId != 0 && _burstRemaining.TryGetValue(burstId, out var rem))
        {
            rem--;
            if (rem <= 0)
            {
                int filledBin = _burstWroteBin.TryGetValue(burstId, out var b) ? b : targetBin;
                // removePlaceholders=true: manual-release leaves placeholder markers that need clearing
                CompleteBurst(burstId, filledBin, hadNotes: true, ascendBinCount: BinSize(), removePlaceholders: true);
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

        // All notes resolved (committed or discarded).
        // Defensive: also remove the discarded step's orphan marker.
        int capturedDiscardedStep = authoredAbsStep;
        EnqueueNextFrame(() =>
        {
            if (controller?.noteVisualizer != null && capturedDiscardedStep >= 0)
                controller.noteVisualizer.RemoveOrphanMarkerAtStep(this, capturedDiscardedStep);
        });

        bool hadNotes = _burstWroteBin.ContainsKey(burstId);

        if (hadNotes)
        {
            int filledBin = _burstWroteBin[burstId];
            _gateReleasedBurstIds.Remove(burstId);
            // removePlaceholders=true: discard path leaves placeholder markers that need clearing
            CompleteBurst(burstId, filledBin, hadNotes: true, ascendBinCount: BinSize(), removePlaceholders: true);
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
            _gateReleasedBurstIds.Remove(burstId);
            CompleteBurst(burstId, filledBin: 0, hadNotes: false, ascendBinCount: BinSize(), removePlaceholders: true);
        }
    }

    /// <summary>
    /// Releases the StarPool spawn gate for a burst without touching _burstRemaining.
    /// Called by DarkTimeoutRoutine when a collectable's TTL expires but the GO lives on.
    /// The gate fires once per burst (idempotent); _burstRemaining is decremented only when
    /// the player actually places or discards each note via the normal commit path.
    /// </summary>
    public void ReleaseSpawnGate(int burstId)
    {
        if (burstId == 0) return;
        if (!_gateReleasedBurstIds.Add(burstId)) return; // already released
        OnCollectableBurstCleared?.Invoke(this, burstId, false);
    }

    /// <summary>
    /// Called at each loop boundary: if a bin has notes in the persistent loop but is still
    /// marked unfilled AND no live collectables for that bin remain in the world, the burst
    /// tracking counter drifted — resolve it now so audio unblocks within 1 boundary.
    /// </summary>
    public void ResolveStrandedBursts()
    {
        for (int b = 0; b < maxLoopMultiplier; b++)
        {
            if (IsBinFilled(b)) continue;
            if (!HasAnyNoteInBin(b)) continue;
            bool hasLiveCollectable = false;
            for (int i = 0; i < spawnedCollectables.Count; i++)
            {
                var go = spawnedCollectables[i];
                if (go == null) continue;
                if (go.TryGetComponent<Collectable>(out var c) && c.intendedBin == b)
                {
                    hasLiveCollectable = true;
                    break;
                }
            }
            if (!hasLiveCollectable)
            {
                if (GameFlowManager.VerboseLogging) Debug.Log($"[SYNC:RESOLVE] {name} bin={b} has notes but unfilled with no live collectables — resolving.");
                SetBinFilled(b, true);
                controller?.ResyncLeaderBinsNow();
            }
        }
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
        // (You can choose to clamp for "audible span" decisions elsewhere, but not for frontier detection.)
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
        loopMultiplier = 1;                    // tracks don't pre-expand; width grows on demand
        _totalSteps    = BinSize() * loopMultiplier;
    }
    public void ResetBinStateForNewPhase()
    {
        // Cursor-mode
        SetBinCursor(0);

        // Loop span: force a single bin wide loop (no hidden carryover)
        loopMultiplier = 1;

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

        if (RemainingActiveWindowSec() <= 0f)
            Debug.LogWarning($"[NOTE:ZERO_WINDOW] {name} note={note} dur={durationTicks} — window=0 at dsp={AudioSettings.dspTime:F3} leaderStart={drumTrack?.leaderStartDspTime:F3} clipLen={drumTrack?.GetClipLengthInSeconds():F3}");
        midiVoice.PlayNoteTicks(note, durationTicks, velocity);
    }

    /// <summary>
    /// Per-bin retune: snaps each note to the nearest tone in its bin's assigned chord
    /// in the current HarmonyDirector progression. Use this instead of RetuneLoopToChord(chord0)
    /// when a profile change should respect the full chord progression across bins.
    /// </summary>
    public void RetuneLoopToCurrentProgression(bool forceHarmonyDirector = false)
    {
        if (persistentLoopNotes == null || persistentLoopNotes.Count == 0) return;

        var modified = new List<(int step, int note, int dur, float vel)>(persistentLoopNotes.Count);
        foreach (var (step, note, dur, vel, _) in persistentLoopNotes)
        {
            int bin = BinIndexForStep(step);

            // When a new ChordProgressionProfile is being committed (forceHarmonyDirector=true),
            // skip the NoteSet's chordRegion — it was baked from the OLD progression and would
            // override the new one. Always read directly from HarmonyDirector in that case.
            var ns = GetNoteSetForBin(bin);
            var region = (!forceHarmonyDirector) ? ns?.chordRegion : null;
            Chord chord;
            if (region != null && region.Count > 0)
            {
                chord = region[bin % region.Count];
            }
            else
            {
                int chordIdx = Harmony_GetChordIndexForBin(bin);
                if (_gfm == null) _gfm = GameFlowManager.Instance;
                var hd = _gfm?.harmony;
                if (chordIdx < 0 || hd == null || !hd.TryGetChordAt(chordIdx, out chord))
                {
                    modified.Add((step, note, dur, vel));
                    continue;
                }
            }

            if (step % BinSize() == 0)
            {
                if (GameFlowManager.VerboseLogging) Debug.Log($"[CHORD][TRK][Retune] track={name} step={step} bin={bin} chordRoot={chord.rootNote} intervals={(chord.intervals != null ? chord.intervals.Count : 0)} noteIn={note}");
            }

            var allowed = BuildChordTones(chord, lowestAllowedNote, highestAllowedNote);
            if (allowed.Count == 0) { modified.Add((step, note, dur, vel)); continue; }
            allowed.Sort();

            modified.Add((step, SnapToNearestChordTone(note, allowed), dur, vel));
        }

        RebuildLoopFromModifiedNotes(modified, transform.position);

        // Force immediate cache rebuild so the scheduler uses retuned pitches from this
        // frame onward. Without this a script-execution-order race between DrumTrack.Update
        // (which fires OnLoopBoundary / retune) and InstrumentTrack.Update (which rebuilds
        // via boundarySerial) can cause the first steps of the new loop to play old pitches.
        RebuildLoopCache_FORCE();
        _loopCacheDirtyPending = false;
    }

    private int AddNoteToLoop(int stepIndex, int note, int durationTicks, float force, bool lightMarkerNow, int authoredRootMidi = int.MinValue, bool skipChordQuantize = false)
    {
        int qNote = skipChordQuantize ? ShiftByOctavesIntoTrackRange(note) : QuantizeNoteToBinChord(stepIndex, note, authoredRootMidi);
        if (GameFlowManager.VerboseLogging) Debug.Log($"[COMMIT] track={name} stepAbs={stepIndex} nowDsp={AudioSettings.dspTime:F6} leaderStart={drumTrack.leaderStartDspTime:F6}");
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
            if (GameFlowManager.VerboseLogging) Debug.Log($"[TRK:ADD_NOTE] track={name} step={stepIndex} qNote={qNote} reusedMarker=False lit={lit} newMarkerId={(noteMarker!=null?noteMarker.GetInstanceID():-1)}");
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
        int forcedTargetBin = -1)
    {
        // --- Entry guards ---
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

        // --- Burst ID: choose exactly once; never change it mid-function ---
        int burstId = forcedBurstId > 0 ? forcedBurstId : ++_nextBurstId;
        if (forcedBurstId > 0) _nextBurstId = Mathf.Max(_nextBurstId, forcedBurstId);
        currentBurstId = burstId;

        // Sweep sibling tracks for stale placeholders from their own prior bursts
        if (controller?.tracks != null && controller.noteVisualizer != null)
        {
            foreach (var sibling in controller.tracks)
            {
                if (sibling == null || sibling == this) continue;
                controller.noteVisualizer.CanonicalizeTrackMarkers(sibling, sibling.currentBurstId);
            }
        }

        if (GameFlowManager.VerboseLogging) Debug.Log($"[TRKDBG] {name} SpawnCollectableBurst: burstId={currentBurstId} noteSet={noteSet} " +
                  $"stepCount={(noteSet?.GetStepList()?.Count ?? -1)} noteCount={(noteSet?.GetNoteList()?.Count ?? -1)} " +
                  $"loopMul={loopMultiplier} pendingExpand={IsExpansionPending} MaxSpawnCount: {maxToSpawn}");

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

        // --- Step normalization: deduplicate and clamp to bin-local space ---
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
            Debug.LogWarning($"[TRK:STEP_NORMALIZE] track={name} burstId={burstId} binSize={binSize} " +
                             $"rawSteps={rawCount} localUnique={localSteps.Count} hadOutOfRange={hadOutOfRange} " +
                             $"sampleRaw={string.Join(",", stepList.Take(Mathf.Min(8, rawCount)))} " +
                             $"sampleLocal={string.Join(",", localSteps.Take(Mathf.Min(8, localSteps.Count)))}");

        // --- Target bin: forcedTargetBin > expansion override > controller selection ---
        int targetBin;
        if (forcedTargetBin >= 0)
        {
            targetBin = Mathf.Clamp(forcedTargetBin, 0, Mathf.Max(0, loopMultiplier - 1));
            if (targetBin != forcedTargetBin)
                Debug.LogWarning($"[TRK:BURST] forcedTargetBin={forcedTargetBin} clamped to {targetBin} (loopMul={loopMultiplier}) track={name} burstId={burstId}");
        }
        else if (_expansionCtrl != null && _expansionCtrl.OverrideNextSpawnBin >= 0)
        {
            targetBin = _expansionCtrl.ConsumeOverrideNextSpawnBin();
        }
        else
        {
            targetBin = controller != null ? controller.GetBinForNextSpawn(this) : GetNextBinForSpawn();
        }

        // --- Expansion staging: target bin exceeds committed loop width ---
        if (targetBin >= loopMultiplier)
        {
            if (GameFlowManager.VerboseLogging) Debug.Log(
                $"[TRK:BURST] OUTCOME=STAGE_EXPAND track={name} burstId={burstId} " +
                $"targetBin={targetBin} loopMul={loopMultiplier} binSize={binSize} maxToSpawn={maxToSpawn}");

            var stagedBurst = new TrackExpansionController.PendingBurstData
            {
                noteSet = noteSet, maxToSpawn = maxToSpawn, burstId = burstId,
                originWorld = originWorld, repelFromWorld = repelFromWorld,
                burstImpulse = burstImpulse, spreadAngleDeg = spreadAngleDeg,
                spawnJitterRadius = spawnJitterRadius, placementMode = placementMode,
                trapSearchRadiusCells = trapSearchRadiusCells, trapBufferCells = trapBufferCells,
                intendedTargetBin = targetBin,
            };

            Vector3 voidPos = originWorld ?? repelFromWorld ?? transform.position;
            bool staged = _expansionCtrl.TryStageExpand(stagedBurst, targetBin, voidPos);

            if (staged)
            {
                var rpByVoice = MusicalRoleProfileLibrary.GetProfile(assignedRole);
                bool isByVoice = rpByVoice != null && rpByVoice.configSelectionMode == RoleConfigSelectionMode.ByVoice;
                if (!isByVoice && controller != null && drumTrack != null)
                    controller.BeginGravityVoidForPendingExpand(this, voidPos, drumTrack.WorldToGridPosition(voidPos));

                foreach (var t in controller.tracks)
                {
                    if (GameFlowManager.VerboseLogging) Debug.Log($"[RECOMPUTE] Attempting to recompute track {t}");
                    if (t != null) controller.noteVisualizer.RecomputeTrackLayout(t);
                }
                return;
            }

            // Stage rejected (expansion already pending) — fall through to density spawn
            if (GameFlowManager.VerboseLogging) Debug.Log($"[TRK:BURST] STAGE_EXPAND rejected (already pending) → density spawn track={name} burstId={burstId}");
            targetBin = Mathf.Clamp(GetNextBinForSpawn(), 0, Mathf.Max(0, loopMultiplier - 1));
        }

        // --- Backfill empty bins before the target so they don't stay silent after ascension ---
        if (forcedTargetBin < 0)
            BackfillEmptyBins(noteSet, targetBin, maxToSpawn, originWorld, repelFromWorld,
                burstImpulse, spreadAngleDeg, spawnJitterRadius, placementMode,
                trapSearchRadiusCells, trapBufferCells);

        if (GameFlowManager.VerboseLogging) Debug.Log($"[TRK:BURST] OUTCOME=SPAWN_NOW track={name} burstId={burstId} targetBin={targetBin} loopMul={loopMultiplier} binSize={binSize} maxToSpawn={maxToSpawn}");

        // --- Composition mode: step-sequenced spawning ---
        if (controller != null && controller.noteCommitMode == NoteCommitMode.Composition)
        {
            EnqueueCompositionSpawns(noteSet, localSteps, targetBin, binSize, maxToSpawn,
                originWorld, spawnJitterRadius, burstId);
            return;
        }

        // --- Immediate spawn ---
        ExecuteImmediateSpawn(noteSet, localSteps, stepList.Count, targetBin, binSize, maxToSpawn, burstId,
            originWorld, repelFromWorld, spawnJitterRadius, placementMode, trapSearchRadiusCells, trapBufferCells);
    }

    // Spawns a collectable for each preceding empty bin so they don't stay silent after ascension.
    // Only called for top-level (non-forced) bursts to prevent infinite recursion.
    private void BackfillEmptyBins(
        NoteSet noteSet, int targetBin, int maxToSpawn,
        Vector3? originWorld, Vector3? repelFromWorld,
        float burstImpulse, float spreadAngleDeg, float spawnJitterRadius,
        BurstPlacementMode placementMode, int trapSearchRadiusCells, int trapBufferCells)
    {
        for (int b = 0; b < targetBin; b++)
        {
            if (b < loopMultiplier && !HasAnyNoteInBin(b))
            {
                SpawnCollectableBurst(noteSet, maxToSpawn, -1,
                    originWorld, repelFromWorld, burstImpulse, spreadAngleDeg, spawnJitterRadius,
                    placementMode, trapSearchRadiusCells, trapBufferCells,
                    forcedTargetBin: b);
            }
        }
    }

    // The SPAWN_NOW path: places collectables into the world for each step in localSteps.
    private void ExecuteImmediateSpawn(
        NoteSet noteSet, List<int> localSteps, int rawStepCount,
        int targetBin, int binSize, int maxToSpawn, int burstId,
        Vector3? originWorld, Vector3? repelFromWorld,
        float spawnJitterRadius, BurstPlacementMode placementMode,
        int trapSearchRadiusCells, int trapBufferCells)
    {
        int gridW = drumTrack != null ? drumTrack.GetSpawnGridWidth() : 0;
        int gridH = drumTrack != null ? drumTrack.GetSpawnGridHeight() : 0;

        if (gridW <= 0 || gridH <= 0)
        {
            _currentBurstArmed = false;
            _currentBurstRemaining = 0;
            Debug.LogWarning($"[TRK:BURST] OUTCOME=ABORT track={name} burstId={burstId} reason=grid_invalid gridW={gridW} gridH={gridH}");
            return;
        }

        if (_gfm == null) _gfm = GameFlowManager.Instance;
        var dustGen = _gfm?.dustGenerator;
        var nv = controller.noteVisualizer;

        // Build trapped candidate list if placement mode requires it
        if (placementMode == BurstPlacementMode.TrappedInDustNearOrigin &&
            dustGen != null && drumTrack != null && originWorld.HasValue)
        {
            List<Vector2Int> trappedCandidates = BuildTrappedCandidatesNearOrigin(
                dustGen, drumTrack, originWorld.Value, gridW, gridH, trapSearchRadiusCells, trapBufferCells);

            if ((trappedCandidates == null || trappedCandidates.Count == 0) && trapBufferCells > 0)
            {
                trappedCandidates = BuildTrappedCandidatesNearOrigin(
                    dustGen, drumTrack, originWorld.Value, gridW, gridH, trapSearchRadiusCells, trapBufferCells - 1);
            }
        }

        int originX = -1;
        if (originWorld.HasValue && drumTrack != null)
        {
            var og = drumTrack.WorldToGridPosition(originWorld.Value);
            if (og.x >= 0 && og.x < gridW) originX = og.x;
        }

        int imprintX = -1;
        if (repelFromWorld.HasValue && drumTrack != null)
        {
            var ig = drumTrack.WorldToGridPosition(repelFromWorld.Value);
            if (ig.x >= 0 && ig.x < gridW) imprintX = ig.x;
        }

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

        var usedAbsSteps = new HashSet<int>();
        var usedCellsThisBurst = new HashSet<Vector2Int>();
        int spawnedCount = 0;

        foreach (int step in localSteps)
        {
            if (maxToSpawn > 0 && spawnedCount >= maxToSpawn) break;

            int note = noteSet.GetNoteForPhaseAndRole(this, step);
            int dur;
            if (!noteSet.TryGetTemplateTimingAtStep(step, out dur, out _))
                dur = CalculateNoteDurationFromSteps(step, noteSet);

            int absStep = targetBin * binSize + step;
            if (!usedAbsSteps.Add(absStep))
            {
                Debug.LogWarning($"[SPAWN:STEP-COLLISION] track={name} burstId={burstId} targetBin={targetBin} binSize={binSize} step={step} -> absStep={absStep}");
                continue;
            }

            if (dustGen == null || drumTrack == null) continue;

            int fullSteps = BinSize() * maxLoopMultiplier;
            float stepNorm = fullSteps > 0 ? Mathf.Clamp01((float)absStep / fullSteps) : -1f;

            Vector2Int chosenCell;
            if (!TryPickRandomSpawnCell(dustGen, drumTrack, usedCellsThisBurst, out chosenCell, stepNorm))
                continue;

            usedCellsThisBurst.Add(chosenCell);
            Vector3 spawnPos = drumTrack.GridToWorldPosition(chosenCell);

            bool cellHasDust = dustGen.HasDustAt(chosenCell);
            // Jail the landing cell for the collectable's arrival window to prevent dust-depenetration
            if (dustGen != null)
            {
                const float jailHoldSeconds = 10f;
                dustGen.CreateJailCenterForCollectable(chosenCell, jailHoldSeconds, ownerId: burstId);
            }

            if (originWorld.HasValue && spawnJitterRadius > 0f)
                spawnPos += (Vector3)(UnityEngine.Random.insideUnitCircle * spawnJitterRadius);

            Vector3 spawnOrigin = originWorld ?? transform.position;
            var go = Instantiate(collectablePrefab, spawnOrigin, Quaternion.identity, collectableParent);
            if (!go) continue;

            if (!go.TryGetComponent(out Collectable c))
            {
                Destroy(go);
                continue;
            }

            c.burstId = burstId;
            c.intendedStep = absStep;
            c.intendedBin = targetBin;
            c.assignedInstrumentTrack = this;
            c.isTrappedInDust = cellHasDust;

            _scratchSteps.Clear();
            _scratchSteps.Add(absStep);

            HookCollectableDestroyHandler(c);
            PlaceAndBindPlaceholderMarker(nv, c, absStep, burstId);

            c.ApplyTrackVisuals(this);
            c.BeginSpawnArrival(spawnOrigin, spawnPos, note, dur, this, noteSet, _scratchSteps);

            spawnedCollectables.Add(go);
            _currentBurstRemaining++;
            spawnedCount++;
        }

        // Empty burst — nothing spawned
        if (spawnedCount <= 0)
        {
            _currentBurstArmed = false;
            _currentBurstRemaining = 0;
            controller?.noteVisualizer?.CanonicalizeTrackMarkers(this, currentBurstId);
            OnCollectableBurstCleared?.Invoke(this, burstId, false);
            Debug.LogWarning($"[TRK:BURST] OUTCOME=SPAWN_EMPTY_CLEARED track={name} burstId={burstId} targetBin={targetBin} binSize={binSize} steps={rawStepCount} gridW={gridW}");
            return;
        }

        if (GameFlowManager.VerboseLogging) Debug.Log($"[TRK:BURST] OUTCOME=SPAWN_OK track={name} burstId={burstId} spawnedCount={spawnedCount} targetBin={targetBin} binSize={binSize} loopMul={loopMultiplier}");

        _burstRemaining[burstId] = spawnedCount;
        _burstTotalSpawned[burstId] = spawnedCount;
        _burstCollected[burstId] = 0;

        SetBinAllocated(targetBin, true);
        _burstTargetBin[burstId] = targetBin;

        // Advance cursor past the allocated bin (ByVoice tracks suppress — cursor moves on chord-group fill)
        AdvanceCursorPastBin(targetBin);

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
        if (_gfm == null) _gfm = GameFlowManager.Instance;
        var dustGen   = _gfm?.dustGenerator;
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
            int dur;
            if (!noteSet.TryGetTemplateTimingAtStep(step, out dur, out _))
                dur = CalculateNoteDurationFromSteps(step, noteSet);

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
        AdvanceCursorPastBin(targetBin);

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

        if (GameFlowManager.VerboseLogging) Debug.Log($"[TRK:COMP] QUEUED track={name} burstId={burstId} count={queued} targetBin={targetBin}");
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

            HookCollectableDestroyHandler(c);
            PlaceAndBindPlaceholderMarker(nv, c, launch.absStep, launch.burstId);

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
            if (!IsBinAllocated(b)) continue;         // bin not yet reached — skip
            if (!IsBinFilled(b)) return false;        // allocated but incomplete — not saturated
            var binNoteSet = GetNoteSetForBin(b);
            if (binNoteSet == null)
            {
                // No stored per-bin note set — using the incoming set as a reference would compare
                // against the wrong step pattern (this bin was filled with a different set).
                // The bin is already filled, so count it as satisfied and move on.
                anyBinChecked = true;
                continue;
            }
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
                    {
                        var _rp = MusicalRoleProfileLibrary.GetProfile(assignedRole);
                        if (_rp == null || _rp.configSelectionMode != RoleConfigSelectionMode.ByVoice)
                            AdvanceBinCursor(1);
                    }
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
        // Keep existing marker GameObjects alive — chord retune never changes step positions,
        // only pitches. ForceSyncMarkersToPersistentLoop at the end will reposition/reconcile.
        // Destroying here caused a deferred-destroy timing bug: markers appeared alive during
        // the sync but were deleted end-of-frame, leaving the track visually empty.
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

        // AddNoteToLoop above tries to update noteMarkers by key lookup, but those GameObjects
        // were just destroyed by the _spawnedNotes loop above. Dead Transform entries remain in
        // noteMarkers, so AddNoteToLoop finds them but skips creation (t != null fails).
        // ForceSyncMarkersToPersistentLoop purges dead entries and re-creates any missing markers.
        controller?.noteVisualizer?.ForceSyncMarkersToPersistentLoop(this);
    }

    public void PruneSpawnedCollectables()
    {
        if (spawnedCollectables == null) return;

        // Remove nulls and inactive pooled objects so controller doesn't think they're "in flight"
        spawnedCollectables.RemoveAll(go => go == null || !go.activeInHierarchy);
    }

    private int CollectNote(int stepIndex, int note, int durationTicks, float force, int authoredRootMidi = int.MinValue)
    {
        int qNote = QuantizeNoteToBinChord(stepIndex, note, authoredRootMidi);
        if (GameFlowManager.VerboseLogging) Debug.Log($"[COLLECT:PITCH] track={name} step={stepIndex} raw={note} quantized={qNote} diff={qNote - note} authoredRoot={authoredRootMidi}");
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
        if (GameFlowManager.VerboseLogging) Debug.Log($"[{name}] {where} | cursor={_binCursor} loopMul={loopMultiplier} filled={s} firstEmpty={FirstEmptyBin()}");
    }
}
