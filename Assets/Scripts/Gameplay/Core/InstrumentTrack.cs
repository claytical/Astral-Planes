using System;
using UnityEngine;
using MidiPlayerTK;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Gameplay.Mining;
using Random = UnityEngine.Random;
[System.Serializable]
public struct AscensionCohort
{
    public int windowStartInclusive;
    public int windowEndExclusive;
    public HashSet<int> stepsRemaining;
    public bool armed;
}

public class InstrumentTrack : MonoBehaviour
{
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
    public int channel;
    public int preset;
    public int bank;
    public int loopMultiplier = 1;
    public int maxLoopMultiplier = 4;
    [Header("Ascension Fuse")]
    [Tooltip("How many extended loops markers for this track take to reach the line of ascension after a burst is armed.")]
    public int ascendLoopCount = 1;
    [Header("Undeveloped Bin Playback ")]
    [Tooltip("If true, when the global leader width is wider than this track, this track's notes will 'ghost repeat' into undeveloped bins at reduced velocity.")]
    [SerializeField] private bool ghostUndevelopedBins = true; 
    [SerializeField, Range(0f, 1f)] private float undevelopedBinGhostGain = 0.35f; // 0=silent, 1=full repeat
    [Tooltip("Velocity scalar applied to ghost repeats in undeveloped bins.")]
    [Range(0f, 1f)] [SerializeField] private float ghostUndevelopedVelocityScalar = 0.25f;
    [Header("Harmony")]
    [Tooltip("If enabled, notes are treated as authored relative to chord index 0 (the 'I' chord), then root-shifted by the current chord before quantization. This makes progressions like I–IV–V change the bass/lead pitch even when the authored notes are static.")]
    public bool rootShiftNotesByChord = true;

    public List<GameObject> spawnedCollectables = new List<GameObject>(); // Track all spawned Collectables
    private int _currentBurstRemaining = 0;
    private bool _currentBurstArmed = false;
    private NoteSet _currentNoteSet;
    private Boundaries _boundaries;
    private readonly List<(int stepIndex, int note, int duration, float velocity)> persistentLoopNotes = new List<(int stepIndex, int note, int duration, float velocity)>();
    List<GameObject> _spawnedNotes = new();
    private int _totalSteps = -1;
    private int _lastLocalStep = -1;
    private int _lastLoopSeen = -1;
    [SerializeField] private LayerMask cosmicDustLayer;   // set this in the Inspector to your Dust layer
    [SerializeField] private float dustCheckRadius = 0.2f;
    private int _nextBurstId = 0;
    private readonly Dictionary<int,int> _burstRemaining = new(); // burstId -> remaining
    private readonly Dictionary<int,int> _burstTotalSpawned = new(); // burstId -> total spawned
    private readonly Dictionary<int,int> _burstCollected    = new(); // burstId -> collected count
    private bool _pendingCollapse;
    private int  _collapseTargetMultiplier = 1;
    private bool _hookedBoundaryForCollapse;
    public bool _pendingExpandForBurst;
    private int _overrideNextSpawnBin = -1;
    private bool _expandCommitted;    
    private int  _oldTotalAtExpand;
    private int  _halfOffsetAtExpand;
    private bool _mapIncomingCollectionsToSecondHalf;
    public bool _hookedBoundaryForExpand;
    private int _pendingMapIntoSecondHalfCount = 0;   // how many pickups to offset
    private float _pendingMapTimeout = 0f;            // safety timeout (seconds)
    private bool _ascendQueued;
    private readonly Dictionary<int, HashSet<int>> _burstSteps = new(); // burstId -> steps for that burst
    private int? _pendingLoopMultiplier;   // supports expand or collapse
    public int currentBurstId;
    [SerializeField] private List<bool> _binFilled = new();
    private bool _waitingForDrumReady;
    [SerializeField] private int _maxBins = 4;                // keep in sync with your loop multiplier
    [SerializeField] private List<int> _binFillOrder = null;  // 0 = unfilled; 1,2,3,... = ordinal it was filled
    [SerializeField] private List<int> _binChordIndex = null; // -1 = unassigned; else index into ChordProgressionProfile.chordSequence
    [SerializeField] private int _nextFillOrdinal = 1;
    private readonly Dictionary<int, int> _burstLeaderBinsBeforeWrite = new(); // burstId -> leaderBins
    private readonly Dictionary<int, int> _burstWroteBin             = new(); // burstId -> targetBin (cursor bin)
    private readonly Dictionary<Collectable, Action> _destroyHandlers = new();
    [SerializeField] private bool[] binAllocated;
    [SerializeField] private int _binCursor = 0;    // counts bins allocated on this track, including silent skips
    [System.Serializable]
    private struct PendingBurst { 
        public NoteSet noteSet; 
        public int maxToSpawn; 
        public int burstId;
        // Optional "void burst" intent (MineNode-origin ejecta).
        public Vector3? originWorld;       // void center (MineNode death position)
        public Vector3? repelFromWorld;    // vehicle position at impact (for "away from vehicle")
        public float    burstImpulse;      // one-time impulse applied to each collectable
        public float    spreadAngleDeg;    // cone around away-dir (deg); 360 = radial
        public float    spawnJitterRadius; // small jitter near spawn cell center
    }


    private struct LoopNote
    {
        public int bin;        // 0..maxLoopMultiplier-1 (or allocated bins)
        public int localStep;  // 0..BinSize-1
        public int note;
        public int duration;
        public float velocity;

        public int ToAbsoluteStep(int binSize) => bin * binSize + localStep;
    }

    [SerializeField]
    private List<LoopNote> _loopNotes = new();
    private bool _loopCacheDirty = true;
    private void MarkLoopCacheDirty() => _loopCacheDirty = true;
    private PendingBurst? _pendingBurstAfterExpand;

    private readonly List<int> _scratchSteps = new List<int>(1);
    private void SetBinCursor(int v) => _binCursor = Mathf.Max(0, v);
    public  int  GetBinCursor()              => Mathf.Max(0, _binCursor);
    public  void AdvanceBinCursor(int by=1)  => _binCursor = Mathf.Max(0, _binCursor + Mathf.Max(1,by));
    private void ResetBinCursor()            => _binCursor = 0;
    public event Action<InstrumentTrack,int,int> OnAscensionCohortCompleted; // (track, start, end)
    public event Action<InstrumentTrack, int> OnCollectableBurstCleared; // (track, burstId)
    private void OnDisable() { UnhookExpandBoundary(); }
    private void OnDestroy() { UnhookExpandBoundary(); }

    //public (NoteSet noteSet, int maxToSpawn)? _pendingBurstAfterExpand;
    private readonly List<Action> _nextFrameQueue = new();
    private void EnqueueNextFrame(Action a) => _nextFrameQueue.Add(a);
    public int BinSize() => drumTrack != null ? drumTrack.totalSteps : 16;
    private int BinIndexForStep(int step) => Mathf.Clamp(step / BinSize(), 0, Mathf.Max(0, maxLoopMultiplier - 1));
    public bool IsStepInFilledBin(int step)
    {
        EnsureBinList();
        int total = Mathf.Max(1, _totalSteps);
        int s = ((step % total) + total) % total; // wrap & make non-negative
        int b = BinIndexForStep(s);
        return b >= 0 && b < _binFilled.Count && _binFilled[b];
    }
    private void RebuildLoopCacheIfDirty() {
        if (!_loopCacheDirty) return;
        _loopNotes.Clear();
        int binSize = BinSize();
        int total   = Mathf.Max(1, _totalSteps > 0 ? _totalSteps : binSize * Mathf.Max(1, loopMultiplier));
        for (int i = 0; i < persistentLoopNotes.Count; i++) { 
            var (stepIndex, note, duration, velocity) = persistentLoopNotes[i];
            // Normalize to [0, total)
            int s = stepIndex % total; 
            if (s < 0) s += total;
            int bin      = (binSize > 0) ? (s / binSize) : 0; 
            int local    = (binSize > 0) ? (s % binSize) : 0;
            _loopNotes.Add(new LoopNote { bin = bin, localStep = local, note = note, duration = duration, velocity = velocity }); 
        }
        _loopCacheDirty = false;
    }
    public bool IsExpansionPending => _pendingExpandForBurst || _pendingBurstAfterExpand.HasValue || _hookedBoundaryForExpand;
    public List<(int stepIndex, int note, int duration, float velocity)> GetPersistentLoopNotes() { // Source-of-truth accessor: keep visuals + controller logic stable.
        return persistentLoopNotes;
    }    
    private IEnumerable<int> BuildBiasedXSequence(IList<int> candidates, int originX, int gridW) {
        if (candidates == null || candidates.Count == 0) yield break;
        // No origin: preserve prior behavior (random order).
        if (originX < 0) {
            foreach (var x in candidates.OrderBy(_ => UnityEngine.Random.value)) 
                yield return x;
            yield break;
        }
        var set = new HashSet<int>(candidates);
        if (set.Contains(originX)) yield return originX;
        int maxR = Mathf.Max(originX, (gridW - 1) - originX); 
        for (int r = 1; r <= maxR; r++) {
            int a = originX + r; 
            int b = originX - r;
            bool aOk = (uint)a < (uint)gridW && set.Contains(a); 
            bool bOk = (uint)b < (uint)gridW && set.Contains(b);
            // Slight randomness so the fan-out isn't perfectly symmetrical every time.
            if (UnityEngine.Random.value < 0.5f) {
                if (aOk) yield return a; if (bOk) yield return b;
            }
            else {
                if (bOk) yield return b; 
                if (aOk) yield return a;
            }
        }
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
        if (_waitingForDrumReady) { 
            if (drumTrack != null && drumTrack.GetLoopLengthInSeconds() > 0 && drumTrack.startDspTime != 0) { 
                _totalSteps = drumTrack.totalSteps * loopMultiplier; 
                _waitingForDrumReady = false; // done
            } else { 
                // Still not ready—skip the rest of Update to mimic the original gating (optional)
                return;
            }
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
        if (_mapIncomingCollectionsToSecondHalf && _pendingMapTimeout > 0f)
        {
            _pendingMapTimeout -= Time.deltaTime;
            if (_pendingMapTimeout <= 0f) {
                _mapIncomingCollectionsToSecondHalf = false;
                _pendingMapIntoSecondHalfCount = 0;
            }
        }
        // - Drum loop stays the bar clock (binSize steps).
        // - Bins are "pages" indexed by drumTrack.completedLoops.
        // - We do NOT stretch bar time when bins increase.
        float elapsed = (float)(AudioSettings.dspTime - drumTrack.startDspTime); 
        // Use the *bar* clock (the actual drum clip length) to compute localStep timing.
        // DrumTrack.GetLoopLengthInSeconds() returns the *effective* leader-cycle length,
        // which grows when any track increases loopMultiplier; using it here will halve
        // melodic note density whenever the leader widens.
        float loopLen = 0f;
        try { 
            loopLen = drumTrack.GetClipLengthInSeconds();
        }
        catch { 
            // If GetClipLengthInSeconds isn't available in some scene variant, fall back.
            loopLen = (drumTrack.drumAudioSource != null && drumTrack.drumAudioSource.clip != null) ? drumTrack.drumAudioSource.clip.length : 0f; }
        if (loopLen <= 0f) return;
        int drumSteps = Mathf.Max(1, drumTrack.totalSteps);
        int binSize   = BinSize();                 // should match drumSteps (typically 16)
        float stepDur = loopLen / drumSteps;
        // Current position within the current drum loop (bar)
        float inLoop = elapsed % loopLen;
        if (inLoop < 0f) inLoop += loopLen;
        
        int localStep = Mathf.FloorToInt(inLoop / stepDur);
        localStep = ((localStep % binSize) + binSize) % binSize;
        
         // Which "page/bin" is active on the leader transport this bar?
         int leaderBins = controller ? Mathf.Max(1, controller.GetMaxActiveLoopMultiplier()) : Mathf.Max(1, loopMultiplier);
         float clipLen = drumTrack.GetClipLengthInSeconds();
         int barIndex = Mathf.FloorToInt((elapsed / clipLen));
         int playheadBin = ((barIndex%leaderBins) + leaderBins) % leaderBins;
         int completedLoops = drumTrack.completedLoops;
         int leaderBin = ((completedLoops % leaderBins) + leaderBins) % leaderBins;
        RebuildLoopCacheIfDirty();
         // If we crossed into a new loop since last frame, reset catch-up within the bar.
         if (_lastLoopSeen != completedLoops)
         {
             _lastLoopSeen = completedLoops;
             _lastLocalStep = -1;
         }
    
// first tick
         if (_lastLocalStep < 0)
         {
             PlayLoopedNotesInBin(playheadBin, localStep, leaderBins);
             _lastLocalStep = localStep;
         }
         else if (localStep != _lastLocalStep)
         {
             int adv = (localStep - _lastLocalStep + binSize) % binSize;
             for (int k = 1; k <= adv; k++)
             {
                 int s = (_lastLocalStep + k) % binSize;
                 PlayLoopedNotesInBin(playheadBin, s, leaderBins); // IMPORTANT: s, not localStep
             }
             _lastLocalStep = localStep;
         }

        
        for (int i = spawnedCollectables.Count - 1; i >= 0; i--) {
            var obj = spawnedCollectables[i];
            if (obj == null)
            {
                spawnedCollectables.RemoveAt(i); // 💥 clean up dead reference
                continue;
            }
        }
    }
    private void PlayLoopedNotesInBin(int playheadBin, int localStep, int leaderBins)
    {
        RebuildLoopCacheIfDirty();

        int trackBins = Mathf.Max(1, loopMultiplier);
        bool undevelopedForThisTrack = playheadBin >= trackBins;

        int trackBin;
        float gain = 1f;

        if (!undevelopedForThisTrack)
        {
            trackBin = playheadBin; // IMPORTANT: use playheadBin, not leaderBin
        }
        else
        {
            if (!ghostUndevelopedBins) return;
            trackBin = trackBins - 1; // ghost into last real bin
            gain = Mathf.Clamp01(undevelopedBinGhostGain);
        }

        // Compute the ABSOLUTE step in the track timeline for chord resolution.
        // One bin is one drum loop worth of steps (same "binSize" concept you use elsewhere).
        // leaderBins is not needed here for the step math; the playheadBin tells us where we are.
        int absStepIndex = (playheadBin *  BinSize()) + localStep;

        for (int i = 0; i < _loopNotes.Count; i++)
        {
            var n = _loopNotes[i];
            if (n.bin != trackBin) continue;
            if (n.localStep != localStep) continue;

            int vel = Mathf.RoundToInt(n.velocity * gain);
            vel = Mathf.Clamp(vel, 1, 127);

            // Apply chord progression at PLAYBACK time (bin-based harmony).
            int noteToPlay = QuantizeNoteToBinChord(absStepIndex, n.note);

            PlayNote(noteToPlay, n.duration, vel);
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
    public int GetHighestAllocatedBin() { 
        if (binAllocated == null) return -1; 
        for (int i = binAllocated.Length - 1; i >= 0; i--) 
            if (binAllocated[i]) return i;
            
        return -1;
        }
    public void DisplayNoteSet()
    {
        Debug.Log($"[{gameObject.name} NOTESET] Root: {_currentNoteSet.GetRootNote()}, Scale: {_currentNoteSet.scale}, Behavior: {_currentNoteSet.noteBehavior}, Pattern: {_currentNoteSet.patternStrategy}, Rhythm: {_currentNoteSet.rhythmStyle}");
    }
    private int HighestFilledBinIndex()
    {
        EnsureBinList();
        for (int i = _binFilled.Count - 1; i >= 0; i--)
            if (_binFilled[i]) return i;
        return -1;
    }
    private int EffectiveLoopBins()
    {
        int hi = HighestFilledBinIndex();
        int binsFromFill = Mathf.Clamp(hi + 1, 1, Mathf.Max(1, maxLoopMultiplier));

        // Preserve width while an expansion is staged or active
        if ((_expandCommitted || _pendingExpandForBurst) &&
            (_mapIncomingCollectionsToSecondHalf || _pendingMapIntoSecondHalfCount > 0))
        {
            return Mathf.Max(binsFromFill, Mathf.Max(1, loopMultiplier));
        }

        return binsFromFill;
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
        loopMultiplier = EffectiveLoopBins();
        _totalSteps    = loopMultiplier * BinSize();
    }
    private void SetBinFilled(int bin, bool filled)
    {
        EnsureBinList();
        if (bin < 0 || bin >= _binFilled.Count) return;
        if (_binFilled[bin] == filled) return;

        _binFilled[bin] = filled;

        // Commit the audible span to match filled bins.
        SyncSpanFromBins();
    }
    private int LastExpandOldTotal { get; set; } = 0;
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
        _burstSteps?.Clear();
        return destroyed;
    }
    private float RemainingActiveWindowSec() {
        float my  = BaseLoopSeconds() * MyMultiplier();
        float L   = LeaderLengthSec();
        if (L <= 0f) return float.MaxValue;
        float tin = TimeInLeader();
        return Mathf.Max(0f, my - tin);
    }
    private int ComputeTargetMultiplierFromUsage()
    {
        if (persistentLoopNotes == null || persistentLoopNotes.Count == 0) return 1;

        int drumSteps = drumTrack != null ? drumTrack.totalSteps : 16;
        int maxUsedStep = 0;
        foreach (var (step, _, _, _) in persistentLoopNotes)
            if (step > maxUsedStep) maxUsedStep = step;

        int requiredBins = Mathf.CeilToInt((maxUsedStep + 1) / (float)drumSteps);
        int target = Mathf.Clamp(requiredBins, 1, Mathf.Max(1, loopMultiplier)); // never grow here
        return target;
    }
    private void SacrificeBin(int binIndex)
    {
        EnsureBinList();
        if (binIndex < 0 || binIndex >= _binFilled.Count) return;

        // Remove notes in this bin
        RemoveNotesInBin(binIndex);

        // Mark it empty and sync span (may or may not shrink)
        SetBinFilled(binIndex, false);
        Harmony_OnBinEmptied(binIndex);
        
        controller?.noteVisualizer?.CanonicalizeTrackMarkers(this, currentBurstId);
    }
    private void RemoveNotesInBin(int binIndex)
    {
        int start = binIndex * BinSize();
        int endExcl = start + BinSize();
        persistentLoopNotes.RemoveAll(n => n.stepIndex >= start && n.stepIndex < endExcl);
        MarkLoopCacheDirty();
    }
    private void EvaluateAndQueueCollapseIfPossible() {
        if (drumTrack == null) return;
        if (loopMultiplier <= 1) return;

        int target = ComputeTargetMultiplierFromUsage();
        if (target < loopMultiplier)
            QueueCollapseTo(target);
    }
    private void QueueCollapseTo(int newMultiplier) {
        newMultiplier = Mathf.Clamp(newMultiplier, 1, loopMultiplier);
        if (newMultiplier == loopMultiplier) return;
        _pendingCollapse = true;
        _collapseTargetMultiplier = newMultiplier;
        HookCollapseBoundary();
    }
    private void HookCollapseBoundary() {
        if (_hookedBoundaryForCollapse || drumTrack == null) return;
        drumTrack.OnLoopBoundary += OnDrumDownbeat_CommitCollapse;
        _hookedBoundaryForCollapse = true;
    }
    private void UnhookCollapseBoundary() {
        if (!_hookedBoundaryForCollapse || drumTrack == null) return;
        drumTrack.OnLoopBoundary -= OnDrumDownbeat_CommitCollapse;
        _hookedBoundaryForCollapse = false;
    }
    private void OnDrumDownbeat_CommitCollapse() {
        if (!_pendingCollapse) { UnhookCollapseBoundary(); return; }

        int newMult = Mathf.Clamp(_collapseTargetMultiplier, 1, loopMultiplier);
        if (newMult != loopMultiplier)
        {
            loopMultiplier = newMult;
            _totalSteps = (drumTrack != null ? drumTrack.totalSteps : 16) * loopMultiplier;
            persistentLoopNotes.RemoveAll(t => t.stepIndex >= _totalSteps);
            MarkLoopCacheDirty();
            RecomputeBinsFromLoop();
            // Refresh visuals to reflect the narrower audible window
            controller?.UpdateVisualizer();
            controller?.noteVisualizer?.RecomputeTrackLayout(this);
        }

        _pendingCollapse = false;
        UnhookCollapseBoundary();
        _lastLocalStep = -1;
        _lastLoopSeen = -1;
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
    private void Harmony_OnBinFilled(int binIndex, int progressionLength) {
    if (binIndex < 0) return;
    if (_binFillOrder == null || _binChordIndex == null) return;
    if (binIndex >= _binFillOrder.Count || binIndex >= _binChordIndex.Count) return;
    if (progressionLength <= 0) return;

    // Only assign on first transition to filled (stable label thereafter)
    if (_binFillOrder[binIndex] == 0)
    {
        _binFillOrder[binIndex] = _nextFillOrdinal++;
        _binChordIndex[binIndex] = (_binFillOrder[binIndex] - 1) % progressionLength; // 1st→0(C), 2nd→1(D), 3rd→2(E), wraps
    }
}
    private void Harmony_OnBinEmptied(int binIndex) {
    if (binIndex < 0) return;
    if (_binFillOrder == null || _binChordIndex == null) return;
    if (binIndex >= _binFillOrder.Count || binIndex >= _binChordIndex.Count) return;

    _binFillOrder[binIndex] = 0;
    _binChordIndex[binIndex] = -1;
}
private int QuantizeNoteToBinChord(int stepIndex, int midiNote)
{
    // Resolve which bin this step belongs to
    int bin = BinIndexForStep(stepIndex);

    int chordIdx = Harmony_GetChordIndexForBin(bin);
    if (chordIdx < 0) return midiNote; // unfilled bin → leave as-is

    var hd = GameFlowManager.Instance?.harmony;
    if (hd == null) return midiNote;

    if (!hd.TryGetChordAt(chordIdx, out var chord)) return midiNote;

    // --- Determine a "base" chord root to transpose from ---
    // Prefer whatever chord bin 0 is using (if it has one), otherwise fall back to chord 0.
    int baseChordIdx = Harmony_GetChordIndexForBin(0);
    if (baseChordIdx < 0) baseChordIdx = 0;

    if (!hd.TryGetChordAt(baseChordIdx, out var baseChord))
        baseChord = chord; // last resort: no meaningful transpose

    // Root delta in semitones (base → current)
    int rootDelta = chord.rootNote - baseChord.rootNote;

    // First: transpose by the chord-root delta (this is the part you *expect* to hear)
    int shifted = midiNote + rootDelta;

    // Clamp to this track's playable range
    shifted = Mathf.Clamp(shifted, lowestAllowedNote, highestAllowedNote);

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

    return best;
}

    private int Harmony_GetChordIndexForBin(int binIndex)
    {
        if (binIndex < 0 || binIndex >= _maxBins) return -1;
        return _binChordIndex[binIndex];
    }
    public void ArmAscensionCohort(int windowStartInclusive, int windowEndExclusive)
    {
        ascensionCohort = new AscensionCohort
        {
            windowStartInclusive = windowStartInclusive,
            windowEndExclusive   = windowEndExclusive,
            stepsRemaining       = new HashSet<int>(),
            armed                = true
        };

        var loop = GetPersistentLoopNotes();
        if (loop != null)
        {
            foreach (var (step, _, _, _) in loop)
                if (step >= windowStartInclusive && step < windowEndExclusive)
                    ascensionCohort.stepsRemaining.Add(step);
        }

        if (ascensionCohort.stepsRemaining.Count == 0)
            ascensionCohort.armed = false;

        Debug.Log($"[CHORD][ARMED] {name} window=[{windowStartInclusive},{windowEndExclusive}) " +
                  $"count={ascensionCohort.stepsRemaining.Count} armed={ascensionCohort.armed}");
    }
    public void NotifyNoteAscendedOrRemovedAtStep(int step)
    {
        Debug.Log($"[CHORD] Notification at {step}");
        if (!ascensionCohort.armed) return;
        Debug.Log($"[CHORD] Armed at {step}");

        if (step < ascensionCohort.windowStartInclusive || step >= ascensionCohort.windowEndExclusive) return;
        Debug.Log($"[CHORD] Window Open");

        if (ascensionCohort.stepsRemaining.Remove(step))
        {
            if (ascensionCohort.stepsRemaining.Count == 0)
            {
                ascensionCohort.armed = false;
                Debug.Log($"[CHORD] Invoking Ascent");
// just before invoking the event:
                Debug.Log($"[CHORD][TRACK→EVENT] Firing CohortCompleted " +
                          $"track={name} role={assignedRole} window=[{ascensionCohort.windowStartInclusive},{ascensionCohort.windowEndExclusive}) " +
                          $"step={step} remaining=0 listeners={(OnAscensionCohortCompleted==null?0:OnAscensionCohortCompleted.GetInvocationList().Length)}");

                OnAscensionCohortCompleted?.Invoke(this, ascensionCohort.windowStartInclusive, ascensionCohort.windowEndExclusive);
            }
        }
    }
    private int CalculateNoteDurationFromSteps(int stepIndex, NoteSet noteSet)
    {
        List<int> allowedSteps = noteSet.GetStepList();
        int totalSteps = GetTotalSteps();

        // Find the next step after this one
        int nextStep = allowedSteps
            .Where(s => s > stepIndex)
            .DefaultIfEmpty(allowedSteps.First()) // wraparound
            .First();

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
    public void RemoveNotesForBurst(int burstId)
    {
        if(burstId < 0) return;
        _burstSteps.Remove(burstId);
        if (!_burstSteps.TryGetValue(burstId, out var set) || set.Count == 0) return;

        // remove loop notes whose step is in this burst
//        persistentLoopNotes.RemoveAll(tuple => set.Contains(tuple.stepIndex));
        controller?.noteVisualizer?.CanonicalizeTrackMarkers(this, burstId);
        EvaluateAndQueueCollapseIfPossible();
    }
    public void ClearBinNotesKeepAllocated(int binIdx)
    {
        int binSize = Mathf.Max(1, BinSize());
        int start = binIdx * binSize;
        int end   = start + binSize;

        // Remove notes that live in this bin window
        for (int i = persistentLoopNotes.Count - 1; i >= 0; i--)
        {
            var n = persistentLoopNotes[i];
            int step = n.Item1;
            if (step >= start && step < end)
                persistentLoopNotes.RemoveAt(i);
        }

        // Keep allocation, but mark as not filled
        SetBinFilled(binIdx, false);

        // Optional: if you maintain other per-step registries, clear them here too.
        // e.g., chord/progression bookkeeping, burst step maps, etc.

        Debug.Log($"[ASCEND][CLEAR_BIN] track={name} bin={binIdx} range=[{start},{end}) keptAllocated=true");
    }

    public void OnCollectableCollected(Collectable collectable, int reportedStep, int durationTicks, float force) {
        if (collectable == null || collectable.assignedInstrumentTrack != this) return;
        controller.NotifyCollected();
        
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

        // 2) Final target step is EXACTLY the absolute step decided at spawn.
        int finalTargetStep = (collectable.intendedStep >= 0) ? collectable.intendedStep : GetCurrentStep(); // very rare fallbackint bin = BinSize();
    
        // Optional sanity: ensure the “reportedStep” matches what we expect.
        // This is purely for debugging and can be removed if noisy.
        if (reportedStep >= 0 && reportedStep != finalTargetStep) { 
            Debug.LogWarning($"[COLLECT:MISMATCH] {name} reportedStep={reportedStep} intended={collectable.intendedStep}");
        }
    
        // 3) Snapshot leader bins BEFORE we write (used for cross-track nudge later)
        int leaderBinsBeforeWrite = (controller != null) ? Mathf.Max(1, controller.GetMaxLoopMultiplier()) : loopMultiplier;
        if (collectable.burstId != 0 && !_burstLeaderBinsBeforeWrite.ContainsKey(collectable.burstId)) { 
            _burstLeaderBinsBeforeWrite[collectable.burstId] = leaderBinsBeforeWrite; 
            _burstWroteBin[collectable.burstId]              = BinIndexForStep(finalTargetStep);
        }
        // 4) Write exactly where spawn intended
        int note = collectable.GetNote();
        if (collectable.IsDark) {
            PlayDarkNote(note, durationTicks, force);
        }
        CollectNote(finalTargetStep, note, durationTicks, force);

        // Mark bin filled + hooks
        int targetBin = BinIndexForStep(finalTargetStep);
        Debug.Log($"[COLLECT:ABS] {name} finalStep={finalTargetStep} bin={targetBin}");


        // Visuals
        LightMarkerAt(finalTargetStep, collectable.burstId);
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
            float intensity = 0f;
            if(_burstWroteBin.TryGetValue(collectable.burstId, out var b))
                filledBin = b;
            SetBinFilled(filledBin, true);
            // This track is now eligible to push the global frontier forward by 1 bin on its next burst.
            if (controller != null)
                controller.AllowAdvanceNextBurst(this);

            if (_burstTotalSpawned.TryGetValue(collectable.burstId, out var total) && total > 0)
            {
                _burstCollected.TryGetValue(collectable.burstId, out var collectedCount);
                intensity = Mathf.Clamp01((float)collectedCount / total);
            }

            // --- B) Apply intensity to drums (ambient → steady → building → intense) ---
            var gfm  = GameFlowManager.Instance;
            var drum = drumTrack != null ? drumTrack : gfm?.activeDrumTrack;
            if (drum != null)
            {
                drum.ApplyBeatIntensity(intensity);
            }
            // Visual: "release" the charged playhead when the burst resolves.
            if (controller != null && controller.noteVisualizer != null)
            {
                controller.noteVisualizer.TriggerPlayheadReleasePulse();
            }

            var hd = GameFlowManager.Instance?.harmony;
            int progLen = hd != null ? hd.ProgressionLength : 0;
            Harmony_OnBinFilled(filledBin, progLen);
            // --- C) Clean up per-burst tracking ---
            _burstRemaining.Remove(collectable.burstId);
            _burstTotalSpawned.Remove(collectable.burstId);
            _burstCollected.Remove(collectable.burstId);

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
            int   fuseLoops = Mathf.Max(1, ascendLoopCount);
            float seconds   = 1f;

            if (drum != null)
                seconds = Mathf.Max(0.0001f, drum.GetLoopLengthInSeconds() * fuseLoops);

            EnqueueNextFrame(() =>
            {
                if (controller != null && controller.noteVisualizer != null)
                    controller.noteVisualizer.TriggerBurstAscend(this, collectable.burstId, seconds);
            });
            Debug.Log($"[TRK:BURST_CLEARED] track={name} burstId={collectable.burstId} remainingOnTrack={spawnedCollectables.Count}");
            OnCollectableBurstCleared?.Invoke(this, collectable.burstId);
            Debug.Log($"[TRKDBG] {name} OnCollectableCollected: burstId={collectable.burstId} -> HandleCollectableBurstCleared (_burstRemaining={_burstRemaining?.Count ?? -1})");

        }
        else
        {
            _burstRemaining[collectable.burstId] = rem;
        }
    }

        // 6) Animate the pickup and finalize
        collectable.TravelAlongTetherAndFinalize(durationTicks, force, seconds: 1f);
        if (_currentBurstArmed && _currentBurstRemaining > 0)
        {
            _currentBurstRemaining--;
            if (_currentBurstRemaining == 0)
            {
                _currentBurstArmed = false;
            }
        }

        return;
}
    public int GetHighestFilledBin()
    {
        EnsureBinList();

        // Walk backwards to find the highest bin that has any notes.
        for (int i = _binFilled.Count - 1; i >= 0; i--)
        {
            if (_binFilled[i])
                return i;
        }

        // No bins have any notes yet.
        return -1;
    }
    public bool IsBinFilled(int binIndex)
    {
        EnsureBinList();

        return binIndex >= 0
               && binIndex < _binFilled.Count
               && _binFilled[binIndex];
    }
    public void ResetBinsForPhase()
    {
        _binFilled = Enumerable.Repeat(false, Mathf.Max(1, maxLoopMultiplier)).ToList();
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
        // _filledBins?.Clear();

        // Burst/cursor snapshots (if present)
        _burstLeaderBinsBeforeWrite?.Clear();
        _burstWroteBin?.Clear();

        // Any mapping flags from expand logic
        _pendingMapIntoSecondHalfCount = 0;
        _mapIncomingCollectionsToSecondHalf = false;
        _expandCommitted = false;
        _oldTotalAtExpand = 0;

        // We already clear looped notes elsewhere; no double clear here.
    }
    public void SoftReplaceLoop(IReadOnlyList<(int stepIndex, int note, int duration, float velocity)> newNotes)
    {
        // Clear data + our own spawned visuals (quietly)
        persistentLoopNotes.Clear();
        MarkLoopCacheDirty();
        _spawnedNotes.Clear();

        // Rebuild notes + visuals quietly
        if (newNotes != null)
        {
            RecomputeBinsFromLoop();
            for (int i = 0; i < newNotes.Count; i++)
            {
                var t = newNotes[i];
                // Reuse your normal add path so markers & bookkeeping stay consistent
                AddNoteToLoop(t.stepIndex, t.note, t.duration, t.velocity);
            }
        }

    } 
    public bool HasNoteSet()
    {
        return _currentNoteSet != null;
    }
    public void SetNoteSet(NoteSet noteSet)
    {
        _currentNoteSet = noteSet;
    }
    public NoteSet GetActiveNoteSet()
    {
        // Return the active NoteSet for this track; use your existing cache if you have it
        if (_currentNoteSet == null)
        {
            Debug.LogWarning($"Instrument tracks should always have a noteset.");
        }
        return _currentNoteSet;
    }

    public int GetTotalSteps()
    {
        return _totalSteps;
    }
    public void ClearLoopedNotes(TrackClearType type = TrackClearType.Remix, Vehicle vehicle = null) {
        if (persistentLoopNotes.Count == 0) return;
        ResetPerfectionFlag();
        switch (type)
        {
            case TrackClearType.EnergyRestore:
                if (vehicle != null)
                {
                    controller?.noteVisualizer?.TriggerNoteRushToVehicle(this, vehicle);
                }
                break;
            case TrackClearType.Remix:
                controller?.noteVisualizer?.TriggerNoteBlastOff(this);
                break;
        }
        _spawnedNotes.Clear(); // Visuals are handled separately
        persistentLoopNotes.Clear();
        MarkLoopCacheDirty();
        RecomputeBinsFromLoop();
        EvaluateAndQueueCollapseIfPossible();
    }
    private (int bin, int local) SplitAbsoluteStep(int stepIndex)
    {
        int binSize = BinSize();
        int total   = Mathf.Max(1, binSize * Mathf.Max(1, loopMultiplier));
        int s       = ((stepIndex % total) + total) % total;
        return (s / binSize, s % binSize);
    }

    public void PlayNote(int note, int durationTicks, float velocity)
    {
        if (drumTrack == null || drumTrack.drumLoopBPM <= 0)
        {
            Debug.LogError("Drum track is not initialized or has an invalid BPM.");
            return;
        }

        // Convert ticks → ms
        int durationMs = Mathf.RoundToInt(durationTicks * (60000f / (drumTrack.drumLoopBPM * 480f)));

        // 🔑 Trim to the remaining audible window this cycle
        float remainSec = RemainingActiveWindowSec();
        if (!float.IsPositiveInfinity(remainSec))
        {
            int maxMs = Mathf.Max(10, Mathf.FloorToInt(remainSec * 1000f));
            durationMs = Mathf.Min(durationMs, maxMs);
        }

        midiStreamPlayer.MPTK_Channels[channel].ForcedPreset = preset;
        midiStreamPlayer.MPTK_Channels[channel].ForcedBank   = bank;

        var noteOn = new MPTKEvent {
            Command  = MPTKCommand.NoteOn,
            Value    = note,
            Channel  = channel,
            Duration = durationMs,
            Velocity = (int)velocity,
        };
        midiStreamPlayer.MPTK_PlayEvent(noteOn);
    }
    public bool IsTrackUtilityRelevant(TrackModifierType modifierType)
    {
        // 1. Skip if there's no loop data yet
        if (persistentLoopNotes == null || persistentLoopNotes.Count == 0)
            return false;

        // 2. Avoid duplicate behavior modifiers — could be expanded
        switch (modifierType)
        {
            case TrackModifierType.RootShift:
                return !HasAlreadyShiftedNotes();
            case TrackModifierType.Clear:
                return persistentLoopNotes.Count > 0;
            default:
                return true; // fallback: assume useful
        }
    }
    public void PerformSmartNoteModification(Vector3 sourcePosition)
    {
        Debug.Log($"Performing SmartNoteModification on {gameObject.name}");
        if (drumTrack == null || !HasNoteSet())
            return;

        string[] options;
        Debug.Log($"Assessing options for {_currentNoteSet}");

        switch (GameFlowManager.Instance.phaseTransitionManager.currentPhase)
        {
            case MusicalPhase.Establish:
                options = new[] { "RootShift", "ChordChange" };
                break;
            case MusicalPhase.Evolve:
                options = new[] { "ChordChange", "NoteBehaviorChange" };
                break;
            case MusicalPhase.Intensify:
                options = new[] { "ChordChange", "RootShift", "NoteBehaviorChange" };
                break;
            case MusicalPhase.Release:
                options = new[] { "ChordChange", "RootShift" };
                break;
            case MusicalPhase.Wildcard:
                options = new[] { "ChordChange", "RootShift", "NoteBehaviorChange" };
                break;
            case MusicalPhase.Pop:
                options = new[] { "NoteBehaviorChange" };
                break;
            default:
                options = new[] { "ChordChange" };
                break;
        }
        string selected = options[Random.Range(0, options.Length)];
        

        switch (selected)
        {
            case "ChordChange":
                ApplyChordChange(_currentNoteSet, sourcePosition);
                break;
            case "NoteBehaviorChange":
                ApplyNoteBehaviorChange(_currentNoteSet, sourcePosition);
                break;
            case "RootShift":
                ApplyRootShift(_currentNoteSet, sourcePosition);
                break;
        }

        controller.UpdateVisualizer();
        //controller.noteVisualizer.SyncTiledClonesForTrack(this);
    }
    public bool IsPerfectThisPhase { get; private set; }
    private void PlayDarkNote(int note, int duration, float velocity)
    {
        if (midiStreamPlayer == null)
        {
            Debug.LogWarning($"{name} - Cannot play dark note: MIDI player is null.");
            return;
        }

        // Apply pitch bend downward (e.g., a quarter-tone down)
        int bendValue = 4096; // Halfway down from center
        midiStreamPlayer.MPTK_Channels[channel].fluid_channel_pitch_bend(bendValue);

        MPTKEvent darkNote = new MPTKEvent()
        {
            Command = MPTKCommand.NoteOn,
            Value = note,
            Channel = channel,
            Duration = duration,
            Velocity = Mathf.Clamp((int)velocity, 0, 127),
        };

        midiStreamPlayer.MPTK_PlayEvent(darkNote);

        // Optional: reset pitch bend after short delay
        midiStreamPlayer.StartCoroutine(ResetPitchBendAfterDelay(0.2f));
    }
    public void ApplyChord(Chord chord, bool reusePlayerNotes = true)
    {
        if (_currentNoteSet == null) return;

        // Build allowed chord tones across the track's playable range
        var allowed = new List<int>();
        for (int octave = -2; octave <= 3; octave++)
            foreach (var iv in chord.intervals)
            {
                int n = chord.rootNote + iv + 12 * octave;
                if (n >= lowestAllowedNote && n <= highestAllowedNote)
                    allowed.Add(n);
            }
        allowed.Sort();

        // Remap player's collected notes instead of throwing them away
        var modified = new List<(int step, int note, int dur, float vel)>();
        foreach (var (step, note, dur, vel) in persistentLoopNotes)
        {
            int newNote = Closest(allowed, note);                // nearest chord tone
            modified.Add((step, newNote, dur, vel));
        }

        RebuildLoopFromModifiedNotes(modified, transform.position);
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
        foreach (var (step, note, dur, vel) in persistentLoopNotes)
            modified.Add((step, Closest(note), dur, vel));

        RebuildLoopFromModifiedNotes(modified, transform.position);
    }
    public int GetNoteDensity()
    {
        return persistentLoopNotes.Count;
    }
    public int CalculateNoteDuration(int stepIndex, NoteSet noteSet)
    {
        List<int> allowedSteps = noteSet.GetStepList();

        // Find the next allowed step greater than the current stepIndex.
        int nextStep = allowedSteps
            .Where(step => step > stepIndex)
            .DefaultIfEmpty(stepIndex + _totalSteps) // Wrap around if no further step is found
            .First();

        // Calculate how many steps between the current and next step, looping around if necessary
        int stepsUntilNext = (nextStep - stepIndex + _totalSteps) % _totalSteps;
        if (stepsUntilNext == 0)
            stepsUntilNext = _totalSteps; // Ensure a full loop duration if the next step wraps to itself.

        // Calculate the number of MIDI ticks per musical step.
        int ticksPerStep = Mathf.RoundToInt(480f / (_totalSteps / 4f)); // 480 ticks per quarter note.

        // Base duration is steps multiplied by ticks per step.
        int baseDurationTicks = ticksPerStep * stepsUntilNext;

        // Retrieve the rhythm pattern for the current note set and apply duration multiplier.
        RhythmPattern pattern = RhythmPatterns.Patterns[noteSet.rhythmStyle];
        int chosenDurationTicks = Mathf.RoundToInt(baseDurationTicks * pattern.DurationMultiplier);

        // Enforce a minimum duration for audibility.
        chosenDurationTicks = Mathf.Max(chosenDurationTicks, ticksPerStep / 2);


        return chosenDurationTicks;
    }
    private int AddNoteToLoop(int stepIndex, int note, int durationTicks, float force)
    {
        int qNote = QuantizeNoteToBinChord(stepIndex, note);
        persistentLoopNotes.Add((stepIndex, qNote, durationTicks, force));
        MarkLoopCacheDirty();
        GameObject noteMarker = null;
        // 🔑 Reuse any existing placeholder marker at (track, step)
        if (controller?.noteVisualizer != null &&
            controller.noteVisualizer.noteMarkers != null &&
            controller.noteVisualizer.noteMarkers.TryGetValue((this, stepIndex), out var t) &&
            t != null)
        {
            noteMarker = t.gameObject;
            var dbgTag = noteMarker.GetComponent<MarkerTag>();
            Debug.Log($"[TRK:ADD_NOTE] track={name} step={stepIndex} qNote={qNote} reusedMarker=True markerId={noteMarker.GetInstanceID()} tagPlaceholder={(dbgTag!=null && dbgTag.isPlaceholder)} tagBurst={(dbgTag!=null?dbgTag.burstId:-999)}");

            // Flip placeholder → lit
            var tag = noteMarker.GetComponent<MarkerTag>() ?? noteMarker.AddComponent<MarkerTag>();
            tag.track = this;
            tag.step = stepIndex;
            tag.isPlaceholder = false;
            var vnm = noteMarker.GetComponent<VisualNoteMarker>();
            if (vnm != null) vnm.Initialize(trackColor);

            var ml = noteMarker.GetComponent<MarkerLight>() ?? noteMarker.AddComponent<MarkerLight>();
            ml.LightUp(trackColor);
        }
        else
        {
            // Fallback: create a new lit marker if none existed
            noteMarker = controller?.noteVisualizer?.PlacePersistentNoteMarker(this, stepIndex, lit: true, burstId:-1);
            Debug.Log($"[TRK:ADD_NOTE] track={name} step={stepIndex} qNote={qNote} reusedMarker=False createdLit={(noteMarker!=null)} newMarkerId={(noteMarker!=null?noteMarker.GetInstanceID():-1)}");
        }

        if (noteMarker != null) _spawnedNotes.Add(noteMarker);
        controller?.noteVisualizer?.CanonicalizeTrackMarkers(this, currentBurstId);
        return stepIndex;
    }
    public float GetVelocityAtStep(int step)
    {
        float max = 0f;
        foreach (var (noteStep, _, _, velocity) in GetPersistentLoopNotes())
        {
            if (noteStep == step)
                max = Mathf.Max(max, velocity);
        }
        return max;
    }
    private int GetNextBinForSpawn()
    {
        EnsureBinList();
        int cur = Mathf.Clamp(GetBinCursor(), 0, _binFilled.Count - 1);
        if (!_binFilled[cur]) return cur;

        for (int i = 0; i < _binFilled.Count; i++)
            if (!_binFilled[i]) return i;

        // All bins filled (should be rare); fall back to last
        return _binFilled.Count - 1;
    }
    public void SpawnCollectableBurst(NoteSet noteSet, int maxToSpawn = -1, int forcedBurstId = -1) {

        if (GameFlowManager.ShouldSuppressCollectableSpawns)
        {
            Debug.Log($"[TRK:BURST] OUTCOME=ABORT track={name} reason=bridge_suppression");
            return;
        }
 
        SpawnCollectableBurst(noteSet, maxToSpawn, forcedBurstId, null, null, 0f, 180f, 0.25f);
    }
// Extended overload: allows MineNode-origin "void burst" intent.
    public void SpawnCollectableBurst(
        NoteSet noteSet,
        int maxToSpawn = -1,
        int forcedBurstId = -1,
        Vector3? originWorld = null,
        Vector3? repelFromWorld = null,
        float burstImpulse = 0f,
        float spreadAngleDeg = 180f,
        float spawnJitterRadius = 0.25f){
    // --- ENTRY / ABORT REASONS ---
        if (noteSet == null) {
            Debug.LogWarning($"[TRK:BURST] OUTCOME=ABORT track={name} reason=noteSet_null maxToSpawn={maxToSpawn}"); return;
        } 
        if (collectablePrefab == null) {
            Debug.LogWarning($"[TRK:BURST] OUTCOME=ABORT track={name} reason=collectablePrefab_null noteSet={noteSet} maxToSpawn={maxToSpawn}"); 
            return;
        } 
        if (controller == null || controller.noteVisualizer == null) {
            Debug.LogWarning($"[TRK:BURST] OUTCOME=ABORT track={name} reason=controller_or_noteVisualizer_null controllerNull={(controller==null)} noteVizNull={(controller!=null && controller.noteVisualizer==null)} noteSet={noteSet} maxToSpawn={maxToSpawn}"); 
            return;
        }
        if (_currentNoteSet != noteSet) SetNoteSet(noteSet);

        int burstId = ++_nextBurstId;
        currentBurstId = burstId;

        Debug.Log($"[TRKDBG] {name} SpawnCollectableBurst: burstId={currentBurstId} noteSet={noteSet} " +
                  $"stepCount={(noteSet?.GetStepList()?.Count ?? -1)} noteCount={(noteSet?.GetNoteList()?.Count ?? -1)} " +
                  $"loopMul={loopMultiplier} pendingExpand={IsExpansionPending} MaxSpawnCount: {maxToSpawn}");

        var nv       = controller.noteVisualizer;
        var stepList = noteSet.GetStepList();
        var noteList = noteSet.GetNoteList();
        if (stepList == null || stepList.Count == 0) {
            Debug.LogWarning($"[TRK:BURST] OUTCOME=ABORT track={name} burstId={burstId} reason=stepList_empty"); 
            return;
        } 
        if (noteList == null || noteList.Count == 0) {
            Debug.LogWarning($"[TRK:BURST] OUTCOME=ABORT track={name} burstId={burstId} reason=noteList_empty"); 
            return;
        }
        _currentBurstArmed     = true;
        _currentBurstRemaining = 0;
        int binSize = BinSize();
        // ------------------------------------------------------------
        // STEP NORMALIZATION (bin-local)
        // If the NoteSet stepList includes absolute steps across bins (e.g., 0..47),
        // but we are intentionally spawning into a single target bin, we MUST fold
        // to local [0..binSize-1] and de-dupe. Otherwise we get STEP-COLLISION spam
        // and artificially low spawnedCount which can stall upstream PhaseStar logic.
        // ------------------------------------------------------------
        int rawCount = stepList.Count; 
        bool hadOutOfRange = false; 
        var localSteps = new List<int>(rawCount); 
        var seenLocal  = new HashSet<int>(); 
        for (int i = 0; i < rawCount; i++) {
            int raw = stepList[i]; 
            if (raw < 0) continue;
            if (raw >= binSize) hadOutOfRange = true;
            int local = raw % binSize; if (seenLocal.Add(local)) localSteps.Add(local);
        } 
        if (hadOutOfRange || localSteps.Count != rawCount) {
            Debug.LogWarning($"[TRK:STEP_NORMALIZE] track={name} burstId={burstId} binSize={binSize} " +
                             $"rawSteps={rawCount} localUnique={localSteps.Count} hadOutOfRange={hadOutOfRange} " +
                             $"sampleRaw={string.Join(",", stepList.Take(Mathf.Min(8, rawCount)))} " +
                             $"sampleLocal={string.Join(",", localSteps.Take(Mathf.Min(8, localSteps.Count)))}");
        }
        int targetBin = controller != null
            ? controller.GetBinForNextSpawn(this)
            : GetNextBinForSpawn();
        Debug.Log($"[TRK:BURST] track={name} targetBin={targetBin} loopMul={loopMultiplier} " +
                  $"cursor={GetBinCursor()} alloc={IsBinAllocated(targetBin)} filled={IsBinFilled(targetBin)} " +
                  $"pendingExpand={_pendingExpandForBurst} hooked={_hookedBoundaryForExpand}");

        // One-shot override (density injection).
        if (_overrideNextSpawnBin >= 0)
        {
            targetBin = _overrideNextSpawnBin;
            _overrideNextSpawnBin = -1;
        }

        // If this burst would target a bin we haven't committed yet, stage expand.
        if (targetBin >= loopMultiplier)
        {
            Debug.Log($"[TRK:BURST] OUTCOME=STAGE_EXPAND track={name} burstId={burstId} targetBin={targetBin} loopMul={loopMultiplier} binSize={binSize} maxToSpawn={maxToSpawn}");
            Debug.Log(
                $"[TRK:STAGE_EXPAND] track={name} burstId={burstId} targetBin={targetBin} loopMul={loopMultiplier} " +
                $"pendingExpand={_pendingExpandForBurst} hasPendingBurst={_pendingBurstAfterExpand.HasValue} " +
                $"noteSet={(noteSet)} waitingForDrum={_waitingForDrumReady}"
            ); 
            // Defensive reset: if a prior expand left _expandCommitted true, it can
            // incorrectly trip the "already expanded" path for a NEW expansion request.
            if (_expandCommitted) { 
                Debug.LogWarning($"[TRK:STAGE_EXPAND] track={name} burstId={burstId} RESET stale expandCommitted=true " + $"oldTotalAtExpand={_oldTotalAtExpand} totalSteps={_totalSteps} loopMul={loopMultiplier} targetBin={targetBin}"); 
                _expandCommitted = false;
            }
            // Already at max bins -> density injection rather than a no-op expand.
            if (loopMultiplier >= Mathf.Max(1, maxLoopMultiplier))
            {
                EnsureBinList();
                SetBinAllocated(targetBin, true);
                if (GetBinCursor() <= targetBin) SetBinCursor(targetBin + 1);
                Debug.Log($"[STAGE] LOOP MULTIPLIER MAXED. {name} waiting={_waitingForDrumReady} dsp={drumTrack?.startDspTime ?? -1}");
                _overrideNextSpawnBin = PickRandomExistingBinForDensity();
                var stagedNoteSet = noteSet; // capture
                EnqueueNextFrame(() => SpawnCollectableBurst(stagedNoteSet, maxToSpawn, forcedBurstId, originWorld, repelFromWorld, burstImpulse, spreadAngleDeg, spawnJitterRadius));
                return;
            }
            Debug.Log($"[STAGE] {name} waiting={_waitingForDrumReady} dsp={drumTrack?.startDspTime ?? -1} loopLen={drumTrack?.GetLoopLengthInSeconds() ?? -1} noteSet={noteSet.GetStepList()}");

            // Reserve a burstId NOW and carry it through expand -> spawn
            int reservedId = ++_nextBurstId;
            currentBurstId = reservedId;

            _pendingExpandForBurst = true;
            _pendingBurstAfterExpand = new PendingBurst {
                noteSet = noteSet, 
                maxToSpawn = maxToSpawn, 
                burstId = reservedId,
                // Default: not a void burst (call sites can override by passing origin/repel/impulse)
                originWorld = originWorld, 
                repelFromWorld = repelFromWorld, 
                burstImpulse = burstImpulse, 
                spreadAngleDeg = spreadAngleDeg, 
                spawnJitterRadius = spawnJitterRadius
            };
            Debug.Log(
                $"[TRK:STAGE_EXPAND] track={name} RESERVED burstId={reservedId} targetBin={targetBin} " +
                $"loopMul={loopMultiplier} pendingExpand={_pendingExpandForBurst}");
            HookExpandBoundary();
            return;
        }
        Debug.Log($"[TRK:BURST] OUTCOME=SPAWN_NOW track={name} burstId={burstId} targetBin={targetBin} loopMul={loopMultiplier} binSize={binSize} maxToSpawn={maxToSpawn}");            
        burstId = (forcedBurstId > 0) ? forcedBurstId : (++_nextBurstId);
        if (forcedBurstId > 0) _nextBurstId = Mathf.Max(_nextBurstId, forcedBurstId);
        currentBurstId = burstId;

        Debug.Log($"[TRKDBG] {name} SpawnCollectableBurst: burstId={burstId} noteSet={noteSet} " +
                  $"stepCount={(noteSet?.GetStepList()?.Count ?? -1)} noteCount={(noteSet?.GetNoteList()?.Count ?? -1)} " +
                  $"loopMul={loopMultiplier} pendingExpand={IsExpansionPending} MaxSpawnCount={maxToSpawn}");

        // --- Attempt spawns ---
        int spawnedCount = 0;
        var usedAbsSteps = new HashSet<int>();

        int gridW = drumTrack != null ? drumTrack.GetSpawnGridWidth() : 0;
        var dustGen = GameFlowManager.Instance != null ? GameFlowManager.Instance.dustGenerator : null;
        if (gridW <= 0) { 
            _currentBurstArmed = false; 
            _currentBurstRemaining = 0; 
            Debug.LogWarning($"[TRK:BURST] OUTCOME=ABORT track={name} burstId={burstId} reason=gridW_invalid gridW={gridW} drumTrackNull={(drumTrack==null)}"); 
            return;
        }
        
        int originX = -1; 
        if (originWorld.HasValue && drumTrack != null) { 
            var og = drumTrack.WorldToGridPosition(originWorld.Value); 
            originX = og.x; 
            if (originX < 0 || originX >= gridW) originX = -1; 
        }
        foreach (int step in localSteps)
        {
            if (maxToSpawn > 0 && spawnedCount >= maxToSpawn) break;

            int note = noteSet.GetNoteForPhaseAndRole(this, step);
            int dur  = CalculateNoteDurationFromSteps(step, noteSet);

            int pitchIndex = noteList.IndexOf(note);
            if (pitchIndex < 0) continue;

            int absStep = targetBin * binSize + step;

            // Prevent step collisions inside the same burst.
            if (!usedAbsSteps.Add(absStep))
            {
                Debug.LogWarning($"[SPAWN:STEP-COLLISION] track={name} burstId={burstId} targetBin={targetBin} " +
                                 $"binSize={binSize} step={step} -> absStep={absStep}");
                continue;
            }
            int targetY = pitchIndex;
            // Build an x-sequence:
            //  - permanent-clear cells first (if available)
            //  - then fallback to any available cell
            IEnumerable<int> xSequence;
            if (dustGen != null && gridW > 0)
            {
                var preferredXs = new List<int>(gridW);
                for (int x = 0; x < gridW; x++)
                {
                    var gp = new Vector2Int(x, pitchIndex);
                    if (!dustGen.IsPermanentlyClearCell(gp)) continue;
                    if (!drumTrack.IsSpawnCellAvailable(gp.x, gp.y)) continue;
                    if (!Collectable.IsCellFreeStatic(gp)) continue;
                    preferredXs.Add(x);
                }
                List<int> candidates = (preferredXs.Count > 0) ? preferredXs : Enumerable.Range(0, gridW).ToList();
                // If originX is valid, this will fan out from the void origin.
                // Otherwise it falls back to random order (preserving prior behavior).
                xSequence = BuildBiasedXSequence(candidates, originX, gridW);
                
            }
            else
            {
                // If we can’t reason about width, skip safely (prevents exceptions / false “armed”).
                continue;
            }

            bool spawnedThisStep = false;

            foreach (int x in xSequence)
            {
                var gp = new Vector2Int(x, pitchIndex);

                if (!drumTrack.IsSpawnCellAvailable(gp.x, gp.y)) continue;
                if (!Collectable.IsCellFreeStatic(gp)) continue;
                Vector3 spawnPos = drumTrack.GridToWorldPosition(gp); 
                if (originWorld.HasValue && spawnJitterRadius > 0f) { 
                    Vector2 j = UnityEngine.Random.insideUnitCircle * spawnJitterRadius; 
                    spawnPos += (Vector3)j;
                }
                var go = Instantiate(
                    collectablePrefab,
                    spawnPos,
                    Quaternion.identity,
                    collectableParent
                );

                if (!go) break;

                if (!go.TryGetComponent(out Collectable c))
                {
                    Destroy(go);
                    break;
                }

                // --- IMPORTANT: assign burstId BEFORE Initialize (fixes your burstId issue) ---
                c.burstId = burstId;
                c.intendedStep = absStep;
                c.assignedInstrumentTrack = this;

                _scratchSteps.Clear();
                _scratchSteps.Add(absStep);

                c.Initialize(note, dur, this, noteSet, _scratchSteps);
                if (burstImpulse > 0f && go.TryGetComponent<Rigidbody2D>(out var crb)) { 
                    // Use repelFromWorld (vehicle position) if provided; else origin; else self.
                    Vector3 from = repelFromWorld ?? originWorld ?? spawnPos;
                    Vector2 away = (Vector2)(spawnPos - from); 
                    if (away.sqrMagnitude < 0.0001f) 
                        away = UnityEngine.Random.insideUnitCircle;
                    away.Normalize();
                    
                    float half = Mathf.Max(0f, spreadAngleDeg) * 0.5f; 
                    float ang  = UnityEngine.Random.Range(-half, half); 
                    Vector2 dir = (Vector2)(Quaternion.Euler(0f, 0f, ang) * (Vector3)away);
                    crb.AddForce(dir * burstImpulse, ForceMode2D.Impulse);
                }
                // Ensure we aren’t double-subscribed (defensive).
                if (_destroyHandlers.TryGetValue(c, out var oldHandler) && oldHandler != null)
                {
                    c.OnDestroyed -= oldHandler;
                    _destroyHandlers.Remove(c);
                }
                Action handler = () => OnCollectableDestroyed(c);
                _destroyHandlers[c] = handler;
                c.OnDestroyed += handler;

                // Place placeholder marker + tether
                var markerGO = nv.PlacePersistentNoteMarker(this, absStep, lit: false, burstId);
                Debug.Log($"[TRK:SPAWN_MARKER] track={name} burstId={burstId} absStep={absStep} targetBin={targetBin} markerCreated={(markerGO!=null)} markerId={(markerGO!=null?markerGO.GetInstanceID():-1)}");
                if (markerGO)
                {
                    var tag = markerGO.GetComponent<MarkerTag>() ?? markerGO.AddComponent<MarkerTag>();
                    tag.track = this;
                    tag.step = absStep;
                    tag.burstId = burstId;
                    tag.isPlaceholder = true;

                    var ml = markerGO.GetComponent<MarkerLight>() ?? markerGO.AddComponent<MarkerLight>();
                    ml.SetGrey(new Color(1f, 1f, 1f, 0.25f));

                    Debug.Log($"[TRK:SPAWN_TETHER] track={name} burstId={burstId} absStep={absStep} markerId={markerGO.GetInstanceID()} aboutToAttach=True");
                    c.AttachTetherAtSpawn(markerGO.transform, nv.noteTetherPrefab, trackColor, dur, absStep);
                    Debug.Log($"[TRK:SPAWN_TETHER] track={name} burstId={burstId} absStep={absStep} markerId={markerGO.GetInstanceID()} attached=True tetherPrefab={(nv.noteTetherPrefab!=null)}");
                }

                spawnedCollectables.Add(go);
                _currentBurstRemaining++;
                spawnedCount++;
                spawnedThisStep = true;

                break; // one collectable per step
            }

            // If we couldn’t spawn for this step, we simply skip it.
            // That is intentional: we do NOT want an empty cell to deadlock a burst.
            if (!spawnedThisStep)
            {
                // optional: Debug.Log($"[SPAWN:MISS] track={name} burstId={burstId} absStep={absStep} pitchIndex={pitchIndex}");
            }
        }

        // --- Empty burst handling: clear immediately so upstream systems never hang ---
        if (spawnedCount <= 0)
        {
            _currentBurstArmed = false;
            _currentBurstRemaining = 0;

            // Do NOT create bookkeeping entries for a burst that has no collectables.
            // Do NOT allocate a bin for a burst that didn’t actually manifest.

            controller?.noteVisualizer?.CanonicalizeTrackMarkers(this, currentBurstId);

            // This is the key: treat as cleared NOW (same frame).
            OnCollectableBurstCleared?.Invoke(this, burstId);
            Debug.LogWarning($"[TRK:BURST] OUTCOME=SPAWN_EMPTY_CLEARED track={name} burstId={burstId} targetBin={targetBin} binSize={binSize} steps={stepList.Count} gridW={gridW}");            return;
            return;
        }
        Debug.Log($"[TRK:BURST] OUTCOME=SPAWN_OK track={name} burstId={burstId} spawnedCount={spawnedCount} targetBin={targetBin} binSize={binSize} loopMul={loopMultiplier}");
        // Normal bookkeeping only when at least one collectable exists.
        _burstRemaining[burstId] = spawnedCount;
        _burstTotalSpawned[burstId] = spawnedCount;
        _burstCollected[burstId] = 0;

        SetBinAllocated(targetBin, true);

        controller?.noteVisualizer?.CanonicalizeTrackMarkers(this, currentBurstId);
        DebugBins($"AfterSpawn(bin={targetBin})");
    }
    private void OnCollectableDestroyed(Collectable c) { 
        if (c == null) return; 
        if (c.assignedInstrumentTrack != this) return;
        if (c.burstId == 0) return;
        // If it already reported collection, OnCollectableCollected will handle the decrement.
        // We only handle "lost" collectables here.
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
            if (rem <= 0) { // We did not write notes for this step, but we still want the burst to resolve
                // so bin/cursor/frontier progression does not deadlock.
                _burstRemaining.Remove(c.burstId); 
                _burstTotalSpawned.Remove(c.burstId); 
                _burstCollected.Remove(c.burstId);
                
                // IMPORTANT: do NOT call SetBinFilled here (no successful harvest),
                // but DO allow future bursts to progress rather than deadlocking.
                // If you want a softer rule, we can require at least one collected in the burst.
                if (controller != null) controller.AllowAdvanceNextBurst(this);
                // Cursor advance is safe: cursor is allocation intent, not harvest proof.
                AdvanceBinCursor(1); 
                OnCollectableBurstCleared?.Invoke(this, c.burstId);
            }
            else { 
                _burstRemaining[c.burstId] = rem;
            }
        }
    }
    private void HookExpandBoundary()
    {
        if (_hookedBoundaryForExpand || drumTrack == null) return;
        drumTrack.OnLoopBoundary += OnDrumDownbeat_CommitExpand;
        _hookedBoundaryForExpand = true;
    }
    private void UnhookExpandBoundary()
    {
        if (!_hookedBoundaryForExpand || drumTrack == null) return;
        drumTrack.OnLoopBoundary -= OnDrumDownbeat_CommitExpand;
        _hookedBoundaryForExpand = false;
    }
    
    private void OnDrumDownbeat_CommitExpand() { 
        // Snapshot the staged request early for logging consistency.
        bool hasReq = _pendingBurstAfterExpand.HasValue; 
        var req0 = hasReq ? _pendingBurstAfterExpand.Value : default;
        int binSize0   = BinSize(); 
        int loopMul0   = loopMultiplier; 
        int total0     = _totalSteps; 
        int oldTotal0  = _oldTotalAtExpand; 
        bool exp0      = _expandCommitted; 
        bool pendExp0  = _pendingExpandForBurst; 
        bool wait0     = _waitingForDrumReady; 
        int curBid0    = currentBurstId;
        Debug.Log($"[TRK:COMMIT_EXPAND] track={name} ENTER curBurstId={curBid0} " + $"loopMul={loopMul0} totalSteps={total0} binSize={binSize0} " + $"pendExpand={pendExp0} pendReq={hasReq} waitForDrum={wait0} " + $"expandCommitted={exp0} oldTotalAtExpand={oldTotal0} " + $"req.noteSet={(hasReq ? req0.noteSet?.ToString() : "null")} req.max={(hasReq ? req0.maxToSpawn : -1)}");

    if (!_pendingExpandForBurst && !_pendingBurstAfterExpand.HasValue)
    {
        Debug.Log($"[TRK:COMMIT_EXPAND] track={name} EXIT(noop) reason=no_pending_flags curBurstId={curBid0}");
        UnhookExpandBoundary();
        return;
    }

    // Case A: We already appear expanded (e.g., due to pre-widen).
    if (_expandCommitted && !_pendingExpandForBurst && _totalSteps >= _oldTotalAtExpand + BinSize())
    {
        Debug.Log($"[TRK:COMMIT_EXPAND] track={name} PATH=ALREADY_EXPANDED curBurstId={curBid0} " +
                  $"totalSteps={_totalSteps} oldTotalAtExpand={_oldTotalAtExpand} binSize={BinSize()} " +
                  $"pendExpand={_pendingExpandForBurst} pendReq={_pendingBurstAfterExpand.HasValue} waitForDrum={_waitingForDrumReady} " +
                  $"dsp={drumTrack.startDspTime} loopLen={drumTrack.GetLoopLengthInSeconds()}"
                    );
        _pendingExpandForBurst = false;

        if (_pendingBurstAfterExpand.HasValue)
        {
            var req = _pendingBurstAfterExpand.Value;
            _pendingBurstAfterExpand = null;
            Debug.Log($"[TRK:COMMIT_EXPAND] track={name} PATH=ALREADY_EXPANDED ENQUEUE_SPAWN curBurstId={curBid0} " +
                      $"req.noteSet={req.noteSet} req.max={req.maxToSpawn} " +
                      $"afterClear pendReqNow={_pendingBurstAfterExpand.HasValue}"
                      );
            EnqueueNextFrame(() => SpawnCollectableBurst(req.noteSet, req.maxToSpawn, req.burstId));
        }
        else { 
            Debug.Log($"[TRK:COMMIT_EXPAND] track={name} PATH=ALREADY_EXPANDED NO_REQ curBurstId={curBid0} (pendingExpand cleared only)");
        }
        UnhookExpandBoundary();
        Debug.Log($"[TRK:COMMIT_EXPAND] track={name} EXIT(PATH=ALREADY_EXPANDED) curBurstId={curBid0}");        
        return;
    }

    // A) Snapshot old width
    LastExpandOldTotal = _totalSteps;
    _oldTotalAtExpand  = _totalSteps;

// "Leader" loop width should reflect what is actually audible/committed, not merely a
// track's loopMultiplier field (which may temporarily exceed the audible content).
    int oldLeaderSteps = (controller != null)
        ? controller.GetMaxActiveLoopMultiplier() * drumTrack.totalSteps
        : drumTrack.totalSteps;

    Debug.Log($"[TRK:COMMIT_EXPAND] track={name} SNAPSHOT curBurstId={curBid0} " + 
              $"oldTotal={_oldTotalAtExpand} oldLeaderSteps={oldLeaderSteps} " + 
              $"loopMul={loopMultiplier} maxLoopMul={maxLoopMultiplier} binSize={BinSize()} pendReq={_pendingBurstAfterExpand.HasValue}");

    int maxBins = Mathf.Max(1, maxLoopMultiplier);
    int newBins = Mathf.Clamp(loopMultiplier + 1, 1, maxBins);
    // No-op expansion (already at max bins) → do not silently drop the staged burst.
    // Convert into density injection: pick a random existing bin and spawn there next frame.
    if (newBins == loopMultiplier) {
        Debug.LogWarning($"[TRK:COMMIT_EXPAND] track={name} PATH=MAXED_DENSITY curBurstId={curBid0} " + $"loopMul={loopMultiplier} maxBins={maxBins} pendReq={_pendingBurstAfterExpand.HasValue}");
        _pendingExpandForBurst              = false; 
        _mapIncomingCollectionsToSecondHalf = false; 
        _expandCommitted                    = false;
        if (_pendingBurstAfterExpand.HasValue) { 
            Debug.LogWarning($"[TRK:COMMIT_EXPAND] track={name} PATH=MAXED_DENSITY ENQUEUE_SPAWN curBurstId={curBid0} " +
                             $"overrideBin={_overrideNextSpawnBin} req.noteSet={req0.noteSet} req.max={req0.maxToSpawn}");
            var req = _pendingBurstAfterExpand.Value; 
            _pendingBurstAfterExpand = null; 
            _overrideNextSpawnBin = PickRandomExistingBinForDensity(); 
            EnqueueNextFrame(() => SpawnCollectableBurst(req.noteSet, req.maxToSpawn, req.burstId, req.originWorld, req.repelFromWorld, req.burstImpulse, req.spreadAngleDeg, req.spawnJitterRadius));
        }
        else
        {
            Debug.LogWarning($"[TRK:COMMIT_EXPAND] track={name} PATH=MAXED_DENSITY NO_REQ curBurstId={curBid0}");
        }
        
        UnhookExpandBoundary(); return;
    }
    // B) Arm mapping/expand FIRST so span sync won’t collapse
    _halfOffsetAtExpand                 = _oldTotalAtExpand; // left-half width
    _mapIncomingCollectionsToSecondHalf = true;
    _expandCommitted                    = true;
    _pendingExpandForBurst              = false;

    // C) Apply new width
    loopMultiplier = newBins;
    _totalSteps    = BinSize() * loopMultiplier;
    EnsureBinList();
    Debug.Log($"[TRK:COMMIT_EXPAND] track={name} PATH=WIDEN_APPLIED curBurstId={curBid0} " + $"newBins={newBins} loopMulNow={loopMultiplier} totalStepsNow={_totalSteps} halfOffset={_halfOffsetAtExpand} mapSecondHalf={_mapIncomingCollectionsToSecondHalf}");
    // D) Mark the new bin as created but empty; flags above prevent auto-collapse
    SetBinAllocated(loopMultiplier -1, true);
    SetBinFilled(loopMultiplier - 1, false);

    // E) Spawn staged burst into the new half (next frame to avoid same-tick interleaving)
    if (_pendingBurstAfterExpand.HasValue)
    {
        var req = _pendingBurstAfterExpand.Value;
        _pendingBurstAfterExpand = null;
        Debug.Log($"[TRK:COMMIT_EXPAND] track={name} PATH=WIDEN_APPLIED ENQUEUE_SPAWN curBurstId={curBid0} " + $"req.noteSet={req.noteSet} req.max={req.maxToSpawn}"); 
        EnqueueNextFrame(() => SpawnCollectableBurst(req.noteSet, req.maxToSpawn, req.burstId, req.originWorld, req.repelFromWorld, req.burstImpulse, req.spreadAngleDeg, req.spawnJitterRadius));
    }
    else
    {
        Debug.Log($"[TRK:COMMIT_EXPAND] track={name} PATH=WIDEN_APPLIED NO_REQ curBurstId={curBid0}");
    }


    // G) Visual refresh for THIS track
    if (controller != null)
    {
        controller.UpdateVisualizer();
        if (controller.noteVisualizer != null)
        {
            controller.noteVisualizer.CanonicalizeTrackMarkers(this, currentBurstId);
            controller.noteVisualizer.MarkGhostPadding(this, _oldTotalAtExpand, _totalSteps - _oldTotalAtExpand);
        }
    }

    // H) If leader width changed, relayout ALL tracks
    int newLeaderSteps = 1;
    if (controller != null && controller.tracks != null)
    {
        newLeaderSteps = 0;
        foreach (var t in controller.tracks)
            if (t != null) newLeaderSteps = Mathf.Max(newLeaderSteps, t.GetTotalSteps());

        if (newLeaderSteps != oldLeaderSteps && controller.noteVisualizer != null)
        {
            foreach (var t in controller.tracks)
                if (t != null) controller.noteVisualizer.RecomputeTrackLayout(t);
        }
    }

    // I) Reset edge detector and unhook
    _lastLocalStep = -1;
    _lastLoopSeen = -1;
    UnhookExpandBoundary();
    Debug.Log($"[TRK:COMMIT_EXPAND] track={name} EXIT curBurstId={curBid0} " + $"loopMulNow={loopMultiplier} totalStepsNow={_totalSteps} oldTotalAtExpand={_oldTotalAtExpand} " +
              $"pendExpandNow={_pendingExpandForBurst} pendReqNow={_pendingBurstAfterExpand.HasValue} " +
              $"mapSecondHalf={_mapIncomingCollectionsToSecondHalf} expandCommitted={_expandCommitted} overrideNextBin={_overrideNextSpawnBin}");
}
    private void RecomputeBinsFromLoop()
    {
        EnsureBinList();
        for (int i = 0; i < _binFilled.Count; i++) _binFilled[i] = false;

        foreach (var (step, _, _, _) in persistentLoopNotes)
        {
            int b = BinIndexForStep(step);
            if (b >= 0 && b < _binFilled.Count) _binFilled[b] = true;
        }

        // Keep loop span derived from the highest FILLED bin (not the old multiplier)
        SyncSpanFromBins();
    }
    private void LightMarkerAt(int step, int burstId)
    {
        var nv = controller?.noteVisualizer;
        if (nv == null) return;

        // Prefer existing ping at (track, step)
        if (!nv.noteMarkers.TryGetValue((this, step), out var t) || t == null)
        {
            // Fallback: place a lit marker under the correct row (rare if spawn path failed)
            var go = nv.PlacePersistentNoteMarker(this, step, lit: true, burstId);
            
            if (go == null) return;
            t = go.transform;
        }
        var goObj = t.gameObject;
        var tag = goObj.GetComponent<MarkerTag>() ?? goObj.gameObject.AddComponent<MarkerTag>(); 
        tag.isPlaceholder = false;
        tag.burstId = burstId;
        // Ensure it is colored and emitting now
//        var vnm = t.GetComponent<VisualNoteMarker>();
//                if (vnm != null) vnm.Initialize(trackColor);
        nv.RegisterCollectedMarker(this, burstId, step, goObj);
        var light = t.GetComponent<MarkerLight>();
        if (light != null) light.LightUp(trackColor);
    }
    private void ResetPerfectionFlag() => IsPerfectThisPhase = false;
    private bool HasAlreadyShiftedNotes()
    {
        // Placeholder logic — adapt as needed for actual behavior detection
        return persistentLoopNotes.Any(n => n.note > 127); // example threshold
    }
    private void RebuildLoopFromModifiedNotes(List<(int, int, int, float)> modified, Vector3 _)
    {
        persistentLoopNotes.Clear();
        MarkLoopCacheDirty();
        foreach (var obj in _spawnedNotes) if (obj) Destroy(obj);
        _spawnedNotes.Clear();

        foreach (var (step, note, dur, vel) in modified)
            AddNoteToLoop(step, note, dur, vel); // <- this already places & registers the marker
    }
    public void PruneSpawnedCollectables()
    {
        if (spawnedCollectables == null) return;

        // Remove nulls and inactive pooled objects so controller doesn't think they're "in flight"
        spawnedCollectables.RemoveAll(go => go == null || !go.activeInHierarchy);
    }
    private int Closest(List<int> pool, int target)
{
    int best = pool[0];
    int bestDist = Mathf.Abs(best - target);
    for (int i = 1; i < pool.Count; i++)
    {
        int d = Mathf.Abs(pool[i] - target);
        if (d < bestDist) { best = pool[i]; bestDist = d; }
    }
    return best;
}
    private void ApplyChordChange(NoteSet noteSet, Vector3 sourcePosition)
{
    int[] chordOffsets = noteSet.GetRandomChordOffsets();
    var modifiedNotes = new List<(int, int, int, float)>();

    for (int i = 0; i < persistentLoopNotes.Count; i++)
    {
        var (step, baseNote, duration, velocity) = persistentLoopNotes[i];
        int offset = chordOffsets[i % chordOffsets.Length];
        int newNote = Mathf.Clamp(baseNote + offset, lowestAllowedNote, highestAllowedNote);
        modifiedNotes.Add((step, newNote, duration, velocity));
    }

    RebuildLoopFromModifiedNotes(modifiedNotes, sourcePosition);
}
    private void ApplyRootShift(NoteSet noteSet, Vector3 sourcePosition)
{
    int shift = Random.Range(-3, 4); // ±3 semitones
    noteSet.ShiftRoot(this, shift);

    var newScaleNotes = noteSet.GetNoteList();
    var modifiedNotes = new List<(int, int, int, float)>();

    for (int i = 0; i < persistentLoopNotes.Count; i++)
    {
        var (step, oldNote, duration, velocity) = persistentLoopNotes[i];
        int newNote = noteSet.GetClosestVoiceLeadingNote(oldNote, newScaleNotes);
        modifiedNotes.Add((step, newNote, duration, velocity));
    }

    RebuildLoopFromModifiedNotes(modifiedNotes, sourcePosition);
}
    private void ApplyNoteBehaviorChange(NoteSet noteSet, Vector3 sourcePosition)
{
    var values = Enum.GetValues(typeof(NoteBehavior)).Cast<NoteBehavior>().ToList();
    values.Remove(noteSet.noteBehavior);
    NoteBehavior newBehavior = values[Random.Range(0, values.Count)];

    noteSet.ChangeNoteBehavior(this, newBehavior);

    var modifiedNotes = new List<(int, int, int, float)>();

    for (int i = 0; i < persistentLoopNotes.Count; i++)
    {
        var (step, note, _, velocity) = persistentLoopNotes[i];
        int newDuration = newBehavior switch
        {
            NoteBehavior.Drone => 720,
            NoteBehavior.Bass => 480,
            NoteBehavior.Lead => 120,
            _ => 360
        };

        modifiedNotes.Add((step, note, newDuration, velocity));
    }

    RebuildLoopFromModifiedNotes(modifiedNotes, sourcePosition);
}
    private int CollectNote(int stepIndex, int note, int durationTicks, float force)
    {
        AddNoteToLoop(stepIndex, note, durationTicks, force);
        PlayNote(note, durationTicks, force);
        return stepIndex;
    }
    private IEnumerator ResetPitchBendAfterDelay(float delay)
    {
        yield return new WaitForSeconds(delay);
        midiStreamPlayer.MPTK_Channels[channel].fluid_channel_pitch_bend(8192); // Center
    }
    private int GetCurrentStep()
    {
        if (drumTrack?.drumAudioSource == null) return -1;

        float elapsed = (float)(AudioSettings.dspTime - drumTrack.startDspTime);
        int bin = BinSize();
        int controllerLeaderMul = controller ? Mathf.Max(1, controller.GetMaxActiveLoopMultiplier()) : loopMultiplier;
        int leaderSteps = Mathf.Max(_totalSteps, controllerLeaderMul * bin); // same as Update()

        float clipLen = drumTrack.GetClipLengthInSeconds(); 
        float stepDur = clipLen / Mathf.Max(1, drumTrack.totalSteps); 
        int   globalStep = Mathf.FloorToInt(elapsed / stepDur) % leaderSteps; // 0..leaderSteps-1

        return globalStep; // no remap back to _totalSteps
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
