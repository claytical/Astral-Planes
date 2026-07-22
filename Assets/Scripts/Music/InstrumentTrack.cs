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
    private static int _nextId;
    internal readonly int InstanceId = System.Threading.Interlocked.Increment(ref _nextId);

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
    // Canonical display color: prefers the MusicalRoleProfile's voice color so spawned
    // objects match the profile palette rather than the raw Inspector field.
    public Color DisplayColor => MusicalRoleProfileLibrary.GetProfile(assignedRole)?.GetColorForVoice(voiceIndex) ?? trackColor;
    public GameObject collectablePrefab; // Prefab to spawn
    public Transform collectableParent; // Parent object for organization
    public AscensionCohort ascensionCohort;
    [Header("Musical Role Assignment")]
    public MusicalRole assignedRole;
    public int voiceIndex { get; private set; }
    public void SetVoiceIndex(int index) => voiceIndex = index;
    private MusicalRoleProfile _activeProfile;
    public MusicalRoleProfile ActiveProfile => _activeProfile;
    public int lowestAllowedNote => _activeProfile?.lowestNote ?? MusicalRoleProfileLibrary.GetProfile(assignedRole)?.lowestNote ?? 36;
    public int highestAllowedNote => _activeProfile?.highestNote ?? MusicalRoleProfileLibrary.GetProfile(assignedRole)?.highestNote ?? 84;
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
    [Header("Config")]
    [SerializeField] public InstrumentTrackConfig config;
    public int maxLoopMultiplier => config != null ? config.maxLoopMultiplier : 4;
    public bool rootShiftNotesByChord => config != null ? config.rootShiftNotesByChord : true;
    private NoteSet[] _binNoteSets;
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
    private readonly Dictionary<int, float> _noteCommitTimes = new(); // stepIndex -> Time.time at commit
    private bool _pendingCollapse;
    private int  _collapseTargetMultiplier = 1;
    private bool _hookedBoundaryForCollapse;
    private bool _ascendQueued;
    private int? _pendingLoopMultiplier;
    public int currentBurstId;
    [SerializeField] private List<bool> _binFilled = new();
    private float[] _binCompletionTime;
    private bool _waitingForDrumReady;
    [SerializeField] private List<int> _binFillOrder = null;
    [SerializeField] private List<int> _binChordIndex = null;
    [SerializeField] private int _nextFillOrdinal = 1;

    // All per-burst tracking lives here — replaces 7 parallel dictionaries.
    private sealed class BurstState
    {
        public int remaining;
        public int collected;
        public int totalSpawned;
        public int targetBin;
        public int leaderBins;  // snapshot before first note write (0 = not yet snapshotted)
        public int wroteBin = -1; // bin written by first note (-1 = no note written yet)
        public readonly HashSet<int> steps = new();
    }
    private readonly Dictionary<int, BurstState> _bursts = new();
    private readonly Dictionary<Collectable, Action> _destroyHandlers = new();

    // ---- Composition Mode: step-sequenced burst spawning (see InstrumentTrackCompositionSpawner) ----
    [SerializeField] private ParticleSystem compositionSpawnEffectPrefab;
    [SerializeField] private bool[] binAllocated;
    [SerializeField] private int _binCursor = 0;    // counts bins allocated on this track, including silent skips
    [Header("Components")]
    [SerializeField] private MidiVoice midiVoice;
    [SerializeField] private LoopPattern loopPattern;
// Scratch buffer to avoid allocations each step.
    private readonly List<(int note, int duration, float velocity01)> _tmpStepNotes = new();
    [SerializeField] private Color trackShadowColor = new Color(0.08f,0.08f,0.08f,1f);
    public Color TrackShadowColor => trackShadowColor;

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
        // authoredRootMidi defaults to 0 when unset; use lowestAllowedNote as a safe floor.
        int fallback = authoredRootMidi > 0 ? authoredRootMidi : lowestAllowedNote;
        if (binSz <= 0) return fallback;
        int binIndex = absStep / binSz;
        int localStep = absStep % binSz;
        var noteSet = GetNoteSetForBin(binIndex);
        if (noteSet != null)
        {
            int n = noteSet.GetNoteForPhaseAndRole(this, localStep);
            return n > 0 ? n : fallback;
        }
        return fallback;
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
    public string DebugExpansionState() => _expansionCtrl?.DebugPendingState() ?? "no-ctrl";
    /// <summary>Target bin of the currently staged/pending burst, if any. Cosmetic hint for the gravity void's particle-duration estimate.</summary>
    public int? PendingIntendedTargetBin => _expansionCtrl?.PendingIntendedTargetBin;
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
                trackColor: DisplayColor,
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
        return _bursts.TryGetValue(burstId, out var s) && (steps = s.steps) != null && steps.Count > 0;
    }

    public bool HasOutstandingNotesInRange(int stepStart, int stepEnd)
    {
        foreach (var kv in _bursts)
        {
            if (kv.Value.remaining <= 0) continue;
            foreach (int step in kv.Value.steps)
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
        _bursts?.Clear();

        // Clear any pending composition-mode launches and unsubscribe the step listener.
        _compositionSpawnerBacking?.ClearPendingLaunches();

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

    public NoteSet GetCurrentNoteSet()
    {
        return _currentNoteSet;
    }
    private void RegisterBurstStep(int burstId, int step)
    {
        if (_bursts.TryGetValue(burstId, out var s))
            s.steps.Add(step);
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
    /// <summary>Removes the persistent note at the given absolute step from the loop.</summary>
    public void RemovePersistentNoteAtStep(int stepAbs)
    {
        persistentLoopNotes.RemoveAll(t => t.stepIndex == stepAbs);
        _loopCacheDirtyPending = true;

        // If ascension just drained the last note out of this bin, clear its fill state
        // (keep allocation so the span/timeline stays stable). Without this the bin reads
        // as filled forever and GetBinForNextSpawn expands instead of refilling it.
        int bin = BinIndexForStep(stepAbs);
        if (!HasAnyNoteInBin(bin))
        {
            EnsureBinList();
            if (bin >= 0 && bin < _binFilled.Count && _binFilled[bin])
            {
                _binFilled[bin] = false;
                if (_binCompletionTime != null && bin < _binCompletionTime.Length) _binCompletionTime[bin] = -1f;
                Harmony_OnBinEmptied(bin);
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
        int baseAscend = ResolveAscendLoopsFromMotif();
        if (baseAscend <= 0) baseAscend = config != null ? config.defaultAscendLoops : 4;
        int perExtraBin = config != null ? config.ascensionLoopsPerExtraBin : 2;
        int ascendByConfig = baseAscend + Mathf.Max(0, binCount - 1) * perExtraBin;
        int leaderBins = controller != null ? Mathf.Max(1, controller.GetCommittedLeaderBins()) : 1;
        return Mathf.Max(1, Mathf.CeilToInt((float)ascendByConfig / leaderBins)) * leaderBins;
    }

    private int ResolveAscendLoopsFromMotif()
    {
        var motif = GameFlowManager.Instance?.phaseTransitionManager?.currentMotif;
        if (motif?.roleNoteConfigs == null) return -1;
        foreach (var cfg in motif.roleNoteConfigs)
            if (cfg != null && cfg.ascendLoops > 0 && cfg.role == assignedRole)
                return cfg.ascendLoops;
        return -1;
    }

    /// <summary>
    /// Authored-root fallback with its exact register preserved (set to the motif key
    /// root by PhaseTransitionManager). Never normalized toward a reference note or
    /// clamped to the track range — the playable range applies to the final note, not
    /// the chord root. Returns int.MinValue when unset so quantization falls back to
    /// progression-relative deltas.
    /// </summary>
    private int GetAuthoredRootMidiExact()
    {
        return authoredRootMidi > 0 ? authoredRootMidi : int.MinValue;
    }
    public void PlayOneShotMidi(int midiNote, float velocity127, int durationTicks = -1)
    {
        if (midiNote < lowestAllowedNote || midiNote > highestAllowedNote)
        {
            Debug.LogWarning($"[TRK] PlayOneShotMidi skipping out-of-range note {midiNote} " +
                             $"(range=[{lowestAllowedNote},{highestAllowedNote}]) on {name}");
            return;
        }
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
            $"burstRemainingCount={_bursts?.Count ?? -1}\n" +
            Environment.StackTrace);

        persistentLoopNotes?.Clear();
        _loopCacheDirtyPending = true;

        _loopNotes?.Clear();
        _spawnedNotes?.Clear();

        _bursts?.Clear();

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

        var modified = new List<(int step, int note, int dur, float vel, int authoredRoot)>(persistentLoopNotes.Count);
        foreach (var (step, note, dur, vel, authoredRoot) in persistentLoopNotes)
        {
            int bin = BinIndexForStep(step);

            // When a new ChordProgressionProfile is being committed (forceHarmonyDirector=true),
            // skip the NoteSet's chordRegion — it was baked from the OLD progression and would
            // override the new one. Always read directly from HarmonyDirector in that case.
            var ns = GetNoteSetForBin(bin);
            var region = (!forceHarmonyDirector) ? ns?.chordRegion : null;
            Chord chord;
            Chord baseChord = default;
            if (region != null && region.Count > 0)
            {
                chord    = region[bin % region.Count];
                baseChord = region[0];
            }
            else
            {
                int chordIdx = Harmony_GetChordIndexForBin(bin);
                if (_gfm == null) _gfm = GameFlowManager.Instance;
                var hd = _gfm?.harmony;
                if (chordIdx < 0 || hd == null || !hd.TryGetChordAt(chordIdx, out chord))
                {
                    modified.Add((step, note, dur, vel, authoredRoot));
                    continue;
                }
                if (!hd.TryGetChordAt(0, out baseChord)) baseChord = chord;
            }

            if (step % BinSize() == 0)
            {
                if (GameFlowManager.VerboseLogging) Debug.Log($"[CHORD][TRK][Retune] track={name} step={step} bin={bin} chordRoot={chord.rootNote} intervals={(chord.intervals != null ? chord.intervals.Count : 0)} noteIn={note}");
            }

            var allowed = BuildChordTones(chord, lowestAllowedNote, highestAllowedNote);
            if (allowed.Count == 0) { modified.Add((step, note, dur, vel, authoredRoot)); continue; }
            allowed.Sort();

            int rootDelta = (authoredRoot != int.MinValue) ? chord.rootNote - authoredRoot : chord.rootNote - baseChord.rootNote;
            // Exact-register transposition: always apply the root delta. A pitch-class
            // match with the target chord must not skip it — that discards the octave
            // encoded in the progression. Octave-fit only if the result is unplayable.
            int shifted = ShiftByOctavesIntoTrackRange(note + rootDelta);
            int retuned = ShiftByOctavesIntoTrackRange(SnapToNearestChordTone(shifted, allowed));
            modified.Add((step, retuned, dur, vel, authoredRoot));
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
        if (qNote < lowestAllowedNote || qNote > highestAllowedNote)
            Debug.LogError($"[TRK:COMMIT_OOB] track={name} step={stepIndex} rawNote={note} qNote={qNote} range=[{lowestAllowedNote},{highestAllowedNote}] skipQuantize={skipChordQuantize} authoredRoot={authoredRootMidi}\n{System.Environment.StackTrace}");
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
                if (vnm != null) vnm.Initialize(DisplayColor);
                var ml = noteMarker.GetComponent<MarkerLight>() ?? noteMarker.AddComponent<MarkerLight>();
                ml.LightUp(DisplayColor);
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
        _compositionSpawner.SpawnBurst(
            noteSet, maxToSpawn, forcedBurstId,
            originWorld, repelFromWorld,
            burstImpulse, spreadAngleDeg, spawnJitterRadius,
            placementMode, trapSearchRadiusCells, trapBufferCells,
            forcedTargetBin);
    }

    private InstrumentTrackCompositionSpawner _compositionSpawnerBacking;
    private InstrumentTrackCompositionSpawner _compositionSpawner => _compositionSpawnerBacking ??= new InstrumentTrackCompositionSpawner(
        getName: () => name,
        getCollectablePrefab: () => collectablePrefab,
        getCollectableParent: () => collectableParent,
        getSpawnBlockedMask: () => spawnBlockedMask,
        getCompositionSpawnEffectPrefab: () => compositionSpawnEffectPrefab,
        getDisplayColor: () => DisplayColor,
        getHostPosition: () => transform.position,
        getLoopMultiplier: () => loopMultiplier,
        getController: () => controller,
        getDrumTrack: () => drumTrack,
        getExpansionCtrl: () => _expansionCtrl,
        getCurrentBurstId: () => currentBurstId,
        setCurrentBurstId: v => currentBurstId = v,
        setCurrentBurstArmed: v => _currentBurstArmed = v,
        setCurrentBurstRemaining: v => _currentBurstRemaining = v,
        hasAnyNoteInBin: HasAnyNoteInBin,
        getNextBinForSpawn: GetNextBinForSpawn,
        setBinAllocated: SetBinAllocated,
        advanceCursorPastBin: AdvanceCursorPastBin,
        getBinSize: BinSize,
        hookCollectableDestroyHandler: HookCollectableDestroyHandler,
        placeAndBindPlaceholderMarker: PlaceAndBindPlaceholderMarker,
        playOneShotMidi: (note, vel, dur) => PlayOneShotMidi(note, vel, dur),
        registerBurstState: (burstId, queued, targetBin) =>
            _bursts[burstId] = new BurstState { remaining = queued, totalSpawned = queued, targetBin = targetBin },
        getBurstTargetBinOrZero: burstId => _bursts.TryGetValue(burstId, out var bs) ? bs.targetBin : 0,
        raiseBurstCleared: (burstId, hadNotes) => OnCollectableBurstCleared?.Invoke(this, burstId, hadNotes),
        resolveTargetBinFromController: () => controller != null ? controller.GetBinForNextSpawn(this) : GetNextBinForSpawn(),
        isExpansionPending: () => IsExpansionPending,
        beginGravityVoidForPendingExpandIfEligible: voidPos =>
        {
            var rpByVoice = MusicalRoleProfileLibrary.GetProfile(assignedRole);
            bool isByVoice = rpByVoice != null && rpByVoice.configSelectionMode == RoleConfigSelectionMode.ByVoice;
            if (!isByVoice && controller != null && drumTrack != null)
                controller.BeginGravityVoidForPendingExpand(this, voidPos, drumTrack.WorldToGridPosition(voidPos));
        },
        endGravityVoidForPendingExpandIfOwner: () => controller?.EndGravityVoidForPendingExpand(this),
        assignInstrumentTrack: c => c.assignedInstrumentTrack = this,
        applyTrackVisuals: c => c.ApplyTrackVisuals(this),
        beginSpawnArrival: (c, origin, target, note, dur, noteSet, steps) =>
            c.BeginSpawnArrival(origin, target, note, dur, this, noteSet, steps),
        getNoteForPhaseAndRole: (noteSet, step) => noteSet.GetNoteForPhaseAndRole(this, step),
        canonicalizeOwnMarkers: () => controller?.noteVisualizer?.CanonicalizeTrackMarkers(this, currentBurstId),
        spawnedCollectables: spawnedCollectables);

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
        if (_bursts.TryGetValue(c.burstId, out var state)) {
            state.remaining--;
            if (state.remaining <= 0) {
                int collected = state.collected;
                _bursts.Remove(c.burstId);
                if (collected > 0) {
                    // At least one note was harvested — treat bin as partially filled
                    // and let the normal progression continue.
                    if (state.wroteBin >= 0)
                        SetBinFilled(state.wroteBin, true);
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
                    if (controller != null) controller.AllowAdvanceNextBurst(this);
                    OnCollectableBurstCleared?.Invoke(this, c.burstId, false);
                }
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
    
    private void RebuildLoopFromModifiedNotes(List<(int, int, int, float, int)> modified, Vector3 _)
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
            foreach (var (step, note, dur, vel, authoredRoot) in modified)
            {
                // skipChordQuantize=true: notes in `modified` are already at their final pitch;
                // re-quantizing here would double-process them and produce wrong results.
                AddNoteToLoop(step, note, dur, vel, true, authoredRoot, skipChordQuantize: true);
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
