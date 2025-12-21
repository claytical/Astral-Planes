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
    private int _lastLeaderSteps = -1;

    public List<GameObject> spawnedCollectables = new List<GameObject>(); // Track all spawned Collectables
    private int _currentBurstRemaining = 0;
    private bool _currentBurstArmed = false;
    private NoteSet _currentNoteSet;
    private Boundaries _boundaries;
    private List<(int stepIndex, int note, int duration, float velocity)> persistentLoopNotes = new List<(int, int, int, float)>();
    List<GameObject> _spawnedNotes = new();
    private int _totalSteps = -1, _lastStep = -1;
    [SerializeField] private LayerMask cosmicDustLayer;   // set this in the Inspector to your Dust layer
    [SerializeField] private float dustCheckRadius = 0.2f;
    private int _nextBurstId = 0;
    private readonly Dictionary<int,int> _burstRemaining = new(); // burstId -> remaining
// How many collectables WERE spawned for this burst (denominator).
    private readonly Dictionary<int,int> _burstTotalSpawned = new(); // burstId -> total spawned
// How many collectables HAVE been collected so far (numerator).
    private readonly Dictionary<int,int> _burstCollected    = new(); // burstId -> collected count
    private bool _pendingCollapse;
    private int  _collapseTargetMultiplier = 1;
    private bool _hookedBoundaryForCollapse;
    private bool _pendingExpandForBurst;
    private int _overrideNextSpawnBin = -1;
    private bool _expandCommitted;    
    private int  _oldTotalAtExpand;
    private int  _halfOffsetAtExpand;
    private bool _mapIncomingCollectionsToSecondHalf;
    private bool _hookedBoundaryForExpand;
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
    // Capture state on first note of a burst
    private readonly Dictionary<int, int> _burstLeaderBinsBeforeWrite = new(); // burstId -> leaderBins
    private readonly Dictionary<int, int> _burstWroteBin             = new(); // burstId -> targetBin (cursor bin)
    private readonly Dictionary<InstrumentTrack, bool> _allowAdvanceNextBurst = new Dictionary<InstrumentTrack, bool>();
    private readonly Dictionary<Collectable, Action> _destroyHandlers = new();
    [SerializeField] private bool[] binAllocated;
    [SerializeField] private int _binCursor = 0;    // counts bins allocated on this track, including silent skips
    private readonly List<int> _scratchSteps = new List<int>(1);
    private void SetBinCursor(int v) => _binCursor = Mathf.Max(0, v);
    public  int  GetBinCursor()              => Mathf.Max(0, _binCursor);
    public  void AdvanceBinCursor(int by=1)  => _binCursor = Mathf.Max(0, _binCursor + Mathf.Max(1,by));
    private void ResetBinCursor()            => _binCursor = 0;
    public event Action<InstrumentTrack,int,int> OnAscensionCohortCompleted; // (track, start, end)
    public event Action<InstrumentTrack, int> OnCollectableBurstCleared; // (track, burstId)
    private void OnDisable() { UnhookExpandBoundary(); }
    private void OnDestroy() { UnhookExpandBoundary(); }

    private (NoteSet noteSet, int maxToSpawn)? _pendingBurstAfterExpand;
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
    public bool IsExpansionPending => _pendingExpandForBurst || _expandCommitted || _pendingBurstAfterExpand.HasValue;

    public void AllowAdvanceNextBurst(InstrumentTrack track)
    {
        if (track == null) return;
        _allowAdvanceNextBurst[track] = true;
    }

    private bool ConsumeAllowAdvanceNextBurst(InstrumentTrack track)
    {
        if (track == null) return false;
        if (_allowAdvanceNextBurst.TryGetValue(track, out bool allowed) && allowed)
        {
            _allowAdvanceNextBurst[track] = false;
            return true;
        }
        return false;
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
        // Compute the *leader-grid* step and play notes whose storedStep matches the absolute leader step.
        float elapsed       = (float)(AudioSettings.dspTime - drumTrack.startDspTime); 
        int bin = BinSize(); 
        int controllerLeaderMul = controller ? Mathf.Max(1, controller.GetMaxActiveLoopMultiplier()) : loopMultiplier;        int leaderSteps = Mathf.Max(_totalSteps, controllerLeaderMul * bin);  // >= widest track
        float stepDur   = drumTrack.GetLoopLengthInSeconds() / Mathf.Max(1, drumTrack.totalSteps);
        int   globalStep    = Mathf.FloorToInt(elapsed / stepDur) % leaderSteps; // 0..leaderSteps-1
        // If the leader grid width changed, the modulo space changed.
        // Reset the step walker to avoid "catch-up smash" (double-time bursts).
        if (_lastLeaderSteps != leaderSteps) { 
            _lastLeaderSteps = leaderSteps; 
            _lastStep = -1;
        }
        // Per-frame catch-up: emit *all* crossed global steps (handles frame skips & high BPM)
        if (_lastStep < 0) { 
            PlayLoopedNotes(globalStep); 
            _lastStep = globalStep;
        } else if (globalStep != _lastStep) { 
            int advance = (globalStep - _lastStep + leaderSteps) % leaderSteps; 
            for (int k = 1; k <= advance; k++) {
                int s = (_lastStep + k) % leaderSteps; 
                PlayLoopedNotes(s);
            } 
            _lastStep = globalStep;
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
    public void SetBinAllocated(int bin, bool v) { 
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
            RecomputeBinsFromLoop();
            // Refresh visuals to reflect the narrower audible window
            controller?.UpdateVisualizer();
            controller?.noteVisualizer?.RecomputeTrackLayout(this);
        }

        _pendingCollapse = false;
        UnhookCollapseBoundary();
        _lastStep = -1;
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
        if (chordIdx < 0) return midiNote; // unfilled bin → leave as-is (or silence elsewhere)

        var hd = GameFlowManager.Instance?.harmony;
        if (hd == null) return midiNote;

        if (!hd.TryGetChordAt(chordIdx, out var chord)) return midiNote;

        // Build allowed chord tones across this track’s playable range
        var allowed = new List<int>(64);
        for (int oct = -2; oct <= 3; oct++)
        {
            for (int k = 0; k < chord.intervals.Count; k++)
            {
                int n = chord.rootNote + chord.intervals[k] + 12 * oct;
                if (n >= lowestAllowedNote && n <= highestAllowedNote) allowed.Add(n);
            }
        }
        if (allowed.Count == 0) return midiNote;
        allowed.Sort();

        // Snap to nearest chord tone
        int best = allowed[0], bestDist = Mathf.Abs(best - midiNote);
        for (int i = 1; i < allowed.Count; i++)
        {
            int d = Mathf.Abs(allowed[i] - midiNote);
            if (d < bestDist) { best = allowed[i]; bestDist = d; }
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
        if (!_burstSteps.TryGetValue(burstId, out var set) || set.Count == 0) return;

        // remove loop notes whose step is in this burst
        persistentLoopNotes.RemoveAll(tuple => set.Contains(tuple.stepIndex));

        _burstSteps.Remove(burstId);
        controller?.noteVisualizer?.CanonicalizeTrackMarkers(this, currentBurstId);
        EvaluateAndQueueCollapseIfPossible();
    }
    public void OnCollectableCollected(Collectable collectable, int reportedStep, int durationTicks, float force) {
        Debug.Log("[COLLECT] OnCollectableCollected Running");

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
        LightMarkerAt(finalTargetStep);
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
            OnCollectableBurstCleared?.Invoke(this, collectable.burstId);
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
    public int GetFilledBinCount()
    {
        EnsureBinList();
        int count = 0;
        for (int i = 0; i < _binFilled.Count; i++) if (_binFilled[i]) count++;
        return count;
    }
    public void PruneToSingleCoreBin()
    {
        // Keep bin 0; remove all higher bins.
        EnsureBinList();
        for (int i = 1; i < _binFilled.Count; i++)
            if (_binFilled[i]) RemoveNotesInBin(i);
        RecomputeBinsFromLoop();
        EvaluateAndQueueCollapseIfPossible();
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
    void PlayLoopedNotes(int localStep)
    {
        foreach (var (storedStep, note, duration, velocity) in persistentLoopNotes)
            if (storedStep == localStep)
            {
                PlayNote(note, duration, velocity);
            }
    }
    public int GetTotalSteps()
    {
        return _totalSteps;
    }
    public List<(int stepIndex, int note, int duration, float velocity)> GetPersistentLoopNotes()
    {
        return persistentLoopNotes;
    }
    public void ClearLoopedNotes(TrackClearType type = TrackClearType.Remix, Vehicle vehicle = null)
    {
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
        RecomputeBinsFromLoop();
        EvaluateAndQueueCollapseIfPossible();
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
    public int AddNoteToLoop(int stepIndex, int note, int durationTicks, float force)
    {
        int qNote = QuantizeNoteToBinChord(stepIndex, note);
        persistentLoopNotes.Add((stepIndex, qNote, durationTicks, force));
        GameObject noteMarker = null;
        // 🔑 Reuse any existing placeholder marker at (track, step)
        if (controller?.noteVisualizer != null &&
            controller.noteVisualizer.noteMarkers != null &&
            controller.noteVisualizer.noteMarkers.TryGetValue((this, stepIndex), out var t) &&
            t != null)
        {
            noteMarker = t.gameObject;

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

    public void SpawnCollectableBurst(NoteSet noteSet, int maxToSpawn = -1)
    {
        if (noteSet == null || collectablePrefab == null || controller?.noteVisualizer == null) return;
        if (_currentNoteSet != noteSet) SetNoteSet(noteSet);

        int burstId = ++_nextBurstId;
        currentBurstId = burstId;

        var nv       = controller.noteVisualizer;
        var stepList = noteSet.GetStepList();
        var noteList = noteSet.GetNoteList();
        if (stepList == null || stepList.Count == 0 || noteList == null || noteList.Count == 0) return;

        _currentBurstArmed     = true;
        _currentBurstRemaining = 0;

        int binSize   = BinSize();
        int targetBin = controller != null
            ? controller.GetBinForNextSpawn(this)
            : GetNextBinForSpawn();
        // One-shot override (used for density injection).
        if (_overrideNextSpawnBin >= 0) { 
            targetBin = _overrideNextSpawnBin; 
            _overrideNextSpawnBin = -1;
        }

        // If this burst would target a bin we haven't committed yet,
        // DO NOT widen now. Stage an expand and spawn the burst AFTER commit.
        if (targetBin >= loopMultiplier) { 
            // If we cannot widen further, convert this into density injection rather than staging a no-op expand.
            // This prevents the "caught but nothing spawned" experience when already at max bins.
            if (loopMultiplier >= Mathf.Max(1, maxLoopMultiplier)) {
                _overrideNextSpawnBin = PickRandomExistingBinForDensity(); 
                EnqueueNextFrame(() => SpawnCollectableBurst(noteSet, maxToSpawn)); 
                return;
            }
            // Guard: if we're already staging an expand, don't stack more.
            // Just remember the latest requested burst.
            _pendingExpandForBurst = true;
            _pendingBurstAfterExpand = (noteSet, maxToSpawn);

            // Ensure the expand boundary is armed so OnDrumDownbeat_CommitExpand() will run.
            HookExpandBoundary();

            // Exit now; the burst will be spawned next frame after expansion commits.
            return;
        }

        int spawned = 0, count = 0;
        var usedAbsSteps = new HashSet<int>();
        foreach (int step in stepList)
        {
            if (maxToSpawn > 0 && spawned >= maxToSpawn) break;

            int note = noteSet.GetNoteForPhaseAndRole(this, step);
            int dur  = CalculateNoteDurationFromSteps(step, noteSet);

            int pitchIndex = noteList.IndexOf(note);
            if (pitchIndex < 0) continue;

            int absStep = targetBin * binSize + (step % binSize); 
            if (!usedAbsSteps.Add(absStep)) { 
                Debug.LogWarning($"[SPAWN:STEP-COLLISION] track={name} burstId={burstId} targetBin={targetBin} " + $"binSize={binSize} step={step} -> absStep={absStep} " +                    $"stepList=[{string.Join(",", stepList)}]");
            }
        int w = drumTrack.GetSpawnGridWidth();

        // Prefer cells that are permanently cleared by the maze generator
        var dustGen = GameFlowManager.Instance != null ? GameFlowManager.Instance.dustGenerator : null;

        List<int> preferredXs = null;
        if (dustGen != null)
        {
            preferredXs = new List<int>();
            for (int x = 0; x < w; x++)
            {
                var gp = new Vector2Int(x, pitchIndex);

                // Must be a permanently-cleared maze cell AND available in the spawn grid
                if (!dustGen.IsPermanentlyClearCell(gp)) continue;
                if (!drumTrack.IsSpawnCellAvailable(gp.x, gp.y)) continue;

                preferredXs.Add(x);
            }
        }

            // Choose the sequence of x positions:
            //  - random order of permanent-empty cells if any exist
            //  - otherwise fall back to random over whole width, like before
            IEnumerable<int> xSequence;
            if (preferredXs != null && preferredXs.Count > 0)
            {
                xSequence = preferredXs.OrderBy(_ => UnityEngine.Random.value);
            }
            else
            {
                xSequence = Enumerable.Range(0, w).OrderBy(_ => UnityEngine.Random.value);
            }

            foreach (int x in xSequence)
            {
                var gp = new Vector2Int(x, pitchIndex);
                if (!drumTrack.IsSpawnCellAvailable(gp.x, gp.y)) continue;
                if (!Collectable.IsCellFreeStatic(gp)) continue;
                var go = Instantiate(
                    collectablePrefab,
                    drumTrack.GridToWorldPosition(gp),
                    Quaternion.identity,
                    collectableParent
                );
                if (!go) break;

                if (go.TryGetComponent(out Collectable c))
                {
                    c.intendedStep = absStep;
                    c.assignedInstrumentTrack = this;

                    _scratchSteps.Clear();
                    _scratchSteps.Add(absStep);
                    c.Initialize(note, dur, this, noteSet, _scratchSteps);

                    c.burstId = burstId;
                    count++;
                    if (_destroyHandlers.TryGetValue(c, out var oldHandler) && oldHandler != null) { 
                        c.OnDestroyed -= oldHandler; 
                        _destroyHandlers.Remove(c);
                    } 
                    Action handler = () => OnCollectableDestroyed(c); 
                    _destroyHandlers[c] = handler; 
                    c.OnDestroyed += handler;
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

                        c.AttachTetherAtSpawn(markerGO.transform, nv.noteTetherPrefab, trackColor, dur, absStep);
                    }

                    spawnedCollectables.Add(go);
                    _currentBurstRemaining++;
                    spawned++;
                }

                // Important: break after the first successful spawn for this step
                break;
            }

        }

        if (spawned == 0) { _currentBurstArmed = false; _currentBurstRemaining = 0; }

        _burstRemaining[burstId] = count;
        _burstTotalSpawned[burstId] = count;
        _burstCollected[burstId]    = 0;
        if (count > 0) { 
            SetBinAllocated(targetBin, true);
        }
        controller?.noteVisualizer?.CanonicalizeTrackMarkers(this, currentBurstId);
        DebugBins($"AfterSpawn(bin={targetBin})");   // <- see section 6
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
    private void OnDrumDownbeat_CommitExpand()
{
    if (!_pendingExpandForBurst && !_pendingBurstAfterExpand.HasValue)
    {
        UnhookExpandBoundary();
        return;
    }

    // Case A: We already appear expanded (e.g., due to pre-widen).
    if (_expandCommitted && _totalSteps >= _oldTotalAtExpand + BinSize())
    {
        _pendingExpandForBurst = false;

        if (_pendingBurstAfterExpand.HasValue)
        {
            var req = _pendingBurstAfterExpand.Value;
            _pendingBurstAfterExpand = null;
            EnqueueNextFrame(() => SpawnCollectableBurst(req.noteSet, req.maxToSpawn));
        }

        UnhookExpandBoundary();
        return;
    }

    // A) Snapshot old width
    LastExpandOldTotal = _totalSteps;
    _oldTotalAtExpand  = _totalSteps;

    int oldLeaderSteps = (controller != null)
        ? controller.GetMaxLoopMultiplier() * drumTrack.totalSteps
        : _totalSteps;

    int maxBins = Mathf.Max(1, maxLoopMultiplier);
    int newBins = Mathf.Clamp(loopMultiplier + 1, 1, maxBins);
    // No-op expansion (already at max bins) → do not silently drop the staged burst.
    // Convert into density injection: pick a random existing bin and spawn there next frame.
    if (newBins == loopMultiplier) {
        _pendingExpandForBurst              = false; 
        _mapIncomingCollectionsToSecondHalf = false; 
        _expandCommitted                    = false;
        if (_pendingBurstAfterExpand.HasValue) { 
            var req = _pendingBurstAfterExpand.Value; 
            _pendingBurstAfterExpand = null; 
            _overrideNextSpawnBin = PickRandomExistingBinForDensity(); 
            EnqueueNextFrame(() => SpawnCollectableBurst(req.noteSet, req.maxToSpawn));
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

    // D) Mark the new bin as created but empty; flags above prevent auto-collapse
    SetBinFilled(loopMultiplier - 1, false);

    // E) Spawn staged burst into the new half (next frame to avoid same-tick interleaving)
    if (_pendingBurstAfterExpand.HasValue)
    {
        var req = _pendingBurstAfterExpand.Value;
        _pendingBurstAfterExpand = null;
        EnqueueNextFrame(() => SpawnCollectableBurst(req.noteSet, req.maxToSpawn));
    }

    // F) Safety: ensure pre-expand loop notes remain mapped to left half
    for (int i = 0; i < persistentLoopNotes.Count; i++)
    {
        var (step, note, dur, vel) = persistentLoopNotes[i];
        if (step >= _oldTotalAtExpand)
        {
            step %= _oldTotalAtExpand;
            persistentLoopNotes[i] = (step, note, dur, vel);
        }
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
    _lastStep = -1;
    UnhookExpandBoundary();

#if UNITY_EDITOR
    Debug.Log($"[Expand] {name}: oldSteps={_oldTotalAtExpand} -> newSteps={_totalSteps} | " +
              $"oldLeader={oldLeaderSteps} newLeader={newLeaderSteps} | " +
              $"mapSecondHalfFrom={_halfOffsetAtExpand}");
#endif
    Debug.Log($"[CommitExpand] {name}: expanded to {_totalSteps} steps, LastExpandOldTotal={LastExpandOldTotal}");
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
    private void LightMarkerAt(int step)
    {
        var nv = controller?.noteVisualizer;
        if (nv == null) return;

        // Prefer existing ping at (track, step)
        if (!nv.noteMarkers.TryGetValue((this, step), out var t) || t == null)
        {
            // Fallback: place a lit marker under the correct row (rare if spawn path failed)
            var go = nv.PlacePersistentNoteMarker(this, step, lit: true);
            
            if (go == null) return;
            t = go.transform;
        }
        var tag = t.GetComponent<MarkerTag>() ?? t.gameObject.AddComponent<MarkerTag>(); 
        tag.isPlaceholder = false;
        tag.burstId = -1;
        // Ensure it is colored and emitting now
//        var vnm = t.GetComponent<VisualNoteMarker>();
//                if (vnm != null) vnm.Initialize(trackColor);

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
        foreach (var obj in _spawnedNotes) if (obj) Destroy(obj);
        _spawnedNotes.Clear();

        foreach (var (step, note, dur, vel) in modified)
            AddNoteToLoop(step, note, dur, vel); // <- this already places & registers the marker
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

        float stepDur = drumTrack.GetLoopLengthInSeconds() / Mathf.Max(1, drumTrack.totalSteps);
        int globalStep = Mathf.FloorToInt(elapsed / stepDur) % leaderSteps; // absolute on leader grid

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
