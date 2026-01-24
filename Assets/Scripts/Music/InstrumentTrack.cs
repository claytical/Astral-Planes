using System;
using UnityEngine;
using MidiPlayerTK;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
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
    }
    public enum BurstPlacementMode
    {
        Free = 0,

        // Prefer cells that currently have dust (and are not permanently clear),
        // biased near originWorld.x. Falls back to any free cell.
        TrappedInDustNearOrigin = 1
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
    bool _loopCacheDirtyPending;   // authored changes happened
    bool _loopCacheDirtyCommitted; // safe to rebuild
    int  _lastCommittedBar = -1;

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

        int binSize = Mathf.Max(1, BinSize());
        int b = step / binSize; // NOTE: no modulo wrap

        return b >= 0 && b < _binFilled.Count && _binFilled[b];
    }

    private void RebuildLoopCacheIfDirty()
    {
        if (!_loopCacheDirtyPending) return;

        _loopNotes.Clear();

        int binSize = Mathf.Max(1, BinSize());

        // Cache must be based on absolute step indices (pages/bins must not alias).
        // Use hard cap (maxLoopMultiplier) to define validity, not _totalSteps.
        int maxBins = Mathf.Max(1, maxLoopMultiplier);
        int maxStepExclusive = maxBins * binSize;

        for (int i = 0; i < persistentLoopNotes.Count; i++)
        {
            var (stepIndex, note, duration, velocity) = persistentLoopNotes[i];

            // Reject invalid steps rather than wrapping them into the wrong bin.
            if (stepIndex < 0 || stepIndex >= maxStepExclusive)
            {
                Debug.LogWarning(
                    $"[TRK:LOOPCACHE] track={name} DROP step={stepIndex} maxStep={maxStepExclusive} " +
                    $"(maxBins={maxBins} binSize={binSize})"
                );
                continue;
            }

            int bin   = stepIndex / binSize;
            int local = stepIndex % binSize;

            _loopNotes.Add(new LoopNote
            {
                bin = bin,
                localStep = local,
                note = note,
                duration = duration,
                velocity = velocity
            });
        }

        // Optional but highly recommended while validating:
        int[] binCounts = new int[maxBins];
        for (int i = 0; i < _loopNotes.Count; i++)
        {
            int b = _loopNotes[i].bin;
            if (b >= 0 && b < binCounts.Length) binCounts[b]++;
        }
        Debug.Log($"[TRK:LOOPCACHE] {name} bins=" + string.Join(",", binCounts));
        if (persistentLoopNotes.Count > 0 && _loopNotes.Count == 0)
        {
            Debug.LogWarning(
                $"[TRK:LOOPCACHE_EMPTY] track={name} persistentCount={persistentLoopNotes.Count} " +
                $"maxLoopMultiplier={maxLoopMultiplier} loopMultiplier={loopMultiplier} binSize={binSize}\n" +
                Environment.StackTrace);
        }

        _loopCacheDirtyPending = false;
    }

    public bool IsExpansionPending => _pendingExpandForBurst || _pendingBurstAfterExpand.HasValue || _hookedBoundaryForExpand;
    public List<(int stepIndex, int note, int duration, float velocity)> GetPersistentLoopNotes() { // Source-of-truth accessor: keep visuals + controller logic stable.
        return persistentLoopNotes;
    }    
    private bool IsOpenOrPermanentCell(CosmicDustGenerator dustGen, Vector2Int gp)
{
    if (dustGen == null) return true;
    if (dustGen.IsPermanentlyClearCell(gp)) return true;
    return !dustGen.HasDustAt(gp);
}

private bool HasTrapBuffer(CosmicDustGenerator dustGen, Vector2Int gp, int gridW, int gridH, int bufferCells)
{
    if (bufferCells <= 0) return true;

    // Treat edges as "open" so we don't place on borders of the maze.
    for (int dx = -bufferCells; dx <= bufferCells; dx++)
    {
        for (int dy = -bufferCells; dy <= bufferCells; dy++)
        {
            int x = gp.x + dx;
            int y = gp.y + dy;

            if (x < 0 || y < 0 || x >= gridW || y >= gridH)
                return false;

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
    [SerializeField] private Color trackShadowColor = new Color(0.08f,0.08f,0.08f,1f);
    public Color TrackShadowColor => trackShadowColor;

    public void RefreshRoleColorsFromProfile()
    {
        var prof = MusicalRoleProfileLibrary.GetProfile(assignedRole);
        if (prof == null) return;

        trackColor       = prof.GetBaseColor();
        trackShadowColor = prof.GetShadowColor(); // new field on MusicalRoleProfile
    }

    void Awake()
    {
        RefreshRoleColorsFromProfile();
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
        // - We do NOT stretch bar time when bins increase.
// ----- TRANSPORT (single authority) -----
        if (controller == null) return;

        var tf = controller.GetTransportFrame();
        int barIndex    = tf.barIndex;
        int playheadBin = tf.playheadBin;

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

        double start = drumTrack.startDspTime;
        if (start <= 0.0) return;

        double barStart = start + (double)barIndex * clipLen;

// ----- BAR BOUNDARY COMMIT (cache + step reset) -----
        if (barIndex != _lastCommittedBar)
        {
            _lastCommittedBar = barIndex;

            // Commit any authored changes atomically at the boundary
            if (_loopCacheDirtyPending)
            {
                // IMPORTANT: this must rebuild regardless of old _loopCacheDirty naming
                RebuildLoopCache_FORCE();
                _loopCacheDirtyPending = false;
            }

            // Reset step cursor so step 0 is eligible this bar
            _lastLocalStep = -1;
        }

// ----- STEP INDEX (DSP-derived) -----
        int curStep = GetDspStepIndexInBar(dspNow, barStart, clipLen, drumSteps);
        int targetCurLocal = ((curStep % binSize) + binSize) % binSize;

// ----- PLAYBACK (catch-up deterministically) -----
        // Audio must follow the committed leader bins (transport), not the UI's visual bins.
        int leaderBins = Mathf.Max(1, controller.GetMaxActiveLoopMultiplier());

// Play every missed step exactly once, in order.
        int startStep = _lastLocalStep + 1;
        if (startStep < 0) startStep = 0;

        for (int s = startStep; s <= targetCurLocal; s++)
        {
            int local = ((s % binSize) + binSize) % binSize;
            PlayLoopedNotesInBin(playheadBin, local, leaderBins);
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
            var (stepIndex, note, duration, velocity) = persistentLoopNotes[i];

            if (stepIndex < 0 || stepIndex >= maxStepExclusive)
                continue;

            int bin   = stepIndex / binSize;
            int local = stepIndex % binSize;

            _loopNotes.Add(new LoopNote
            {
                bin = bin,
                localStep = local,
                note = note,
                duration = duration,
                velocity = velocity
            });
        }
    }

    private void PlayLoopedNotesInBin(int playheadBin, int localStep, int leaderBins)
    {
        RebuildLoopCacheIfDirty();

        int trackBins = Mathf.Max(1, loopMultiplier);

        // Map the global playhead bin into an actual bin this track can play.
        // Never hard-silence undeveloped bins; repeat the last real bin instead.
        int trackBin = playheadBin;
        float gain = 1f;

        if (playheadBin >= trackBins)
        {
            // Repeat last real bin (or 0 for single-bin tracks).
            trackBin = Mathf.Max(0, trackBins - 1);

            // Optional “ghost” behavior: reduced gain. But still audible.
            if (ghostUndevelopedBins)
                gain = Mathf.Clamp01(undevelopedBinGhostGain);
        }

        int absStepIndex = (trackBin * BinSize()) + localStep;

        for (int i = 0; i < _loopNotes.Count; i++)
        {
            var n = _loopNotes[i];
            if (n.bin != trackBin) continue;
            if (n.localStep != localStep) continue;

            int vel = Mathf.RoundToInt(n.velocity * gain);
            vel = Mathf.Clamp(vel, 1, 127);

            PlayNote(n.note, n.duration, vel);        }
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
        if ((_expandCommitted || _pendingExpandForBurst) &&
            (_mapIncomingCollectionsToSecondHalf || _pendingMapIntoSecondHalfCount > 0))
        {
            return Mathf.Max(binsFromAlloc, Mathf.Max(1, loopMultiplier));
        }

        // IMPORTANT: span = allocation (stable). Fill just makes bins audible or silent.
        return Mathf.Max(1, binsFromAlloc);
    }

    public bool TryGetBurstSteps(int burstId, out HashSet<int> steps)
    {
        steps = null;
        return _burstSteps != null && _burstSteps.TryGetValue(burstId, out steps) && steps != null && steps.Count > 0;
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
                Harmony_OnBinEmptied(b);
            }

            _totalSteps = (drumTrack != null ? drumTrack.totalSteps : 16) * loopMultiplier;

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

    private int Harmony_GetChordIndexForBin(int binIndex)
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

    private int QuantizeNoteToBinChord(int stepIndex, int midiNote)
{
    // Resolve which bin this step belongs to
    int bin = BinIndexForStep(stepIndex);

    int chordIdx = Harmony_GetChordIndexForBin(bin);
    if (chordIdx < 0) return midiNote;
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
            Harmony_OnBinEmptied(binIdx);
        }

        // CRITICAL: removed notes must stop playing immediately
        _loopCacheDirtyPending = true;

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

// Optional sanity: log if reported differs from intended (useful for chasing bad assignment upstream).
        if (reportedStep >= 0 && collectable.intendedStep >= 0 && reportedStep != collectable.intendedStep)
        {
            Debug.LogWarning($"[COLLECT:MISMATCH] {name} reportedStep={reportedStep} intended={collectable.intendedStep} burstId={collectable.burstId}");
        }

        int binSize = BinSize();

        // 3) Snapshot leader bins BEFORE we write (used for cross-track nudge later)
        int leaderBinsBeforeWrite = (controller != null) ? Mathf.Max(1, controller.GetMaxLoopMultiplier()) : loopMultiplier;
        if (collectable.burstId != 0 && !_burstLeaderBinsBeforeWrite.ContainsKey(collectable.burstId)) { 
            _burstLeaderBinsBeforeWrite[collectable.burstId] = leaderBinsBeforeWrite; 
            _burstWroteBin[collectable.burstId]              = BinIndexForStep(finalTargetStep);
        }
        // 4) Write exactly where spawn intended
        int note = collectable.GetNote();
        CollectNote(finalTargetStep, note, durationTicks, force);

        // Mark bin filled + hooks
        int targetBin = BinIndexForStep(finalTargetStep);
        Debug.Log($"[CURSOR] Target Bin={targetBin} binCursor: {_binCursor} allocated: {binAllocated} filled: {_binFilled}");


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

            if (drumTrack != null)
                seconds = Mathf.Max(0.0001f, drumTrack.GetLoopLengthInSeconds() * fuseLoops);

            EnqueueNextFrame(() =>
            {
                if (controller != null && controller.noteVisualizer != null)
                    controller.noteVisualizer.TriggerBurstAscend(this, collectable.burstId, seconds);
            });
            Debug.Log($"[TRK:BURST_CLEARED] track={name} burstId={collectable.burstId} reported Step: {reportedStep}  remainingOnTrack={spawnedCollectables.Count} bin cursor: {_binCursor} ");
            
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
    public void ResetBinsForPhase()
    {
        // Hard reset of bin span + allocation for a clean new phase/motif.
        int want = Mathf.Max(1, maxLoopMultiplier);

        _binFilled = Enumerable.Repeat(false, want).ToList();

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

    /// <summary>
    /// Single hard reset entry point for motif boundaries.
    /// Clears loop content, bin allocation, burst state, and expansion/mapping flags.
    /// Intended to be called exactly once by the motif authority (e.g., GameFlowManager).
    /// </summary>
    public void BeginNewMotifHardClear(string reason = "BeginNewMotif")
    {
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

    public void SoftReplaceLoop(IReadOnlyList<(int stepIndex, int note, int duration, float velocity)> newNotes)
    {
        Debug.LogWarning(
            $"[TRK:CLEAR_LOOP] track={name} fn=SoftReplaceLoop " +
            $"newNotes={(newNotes != null ? newNotes.Count : -1)} " +
            $"persistentBefore={(persistentLoopNotes != null ? persistentLoopNotes.Count : -1)}\n" +
            Environment.StackTrace);

        persistentLoopNotes.Clear();
        _loopCacheDirtyPending = true;

        _spawnedNotes.Clear();

        if (newNotes != null)
        {
            RecomputeBinsFromLoop();
            for (int i = 0; i < newNotes.Count; i++)
            {
                var t = newNotes[i];
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
        // Always reflect current loopMultiplier.
        // _totalSteps is treated as a fallback for early init / scenes where drumTrack is not ready.
        int drumSteps = (drumTrack != null) ? Mathf.Max(1, drumTrack.totalSteps) : 0;
        if (drumSteps > 0)
            return drumSteps * Mathf.Max(1, loopMultiplier);

        return Mathf.Max(1, _totalSteps);
    }

    public void ClearLoopedNotes(Vehicle vehicle = null)
    {
        if (persistentLoopNotes.Count == 0) return;

        Debug.LogWarning(
            $"[TRK:CLEAR_LOOP] track={name} fn=ClearLoopedNotes " +
            $"vehicle={(vehicle != null ? vehicle.name : "null")} " +
            $"persistentBefore={persistentLoopNotes.Count}\n" +
            Environment.StackTrace);

        ResetPerfectionFlag();
        controller?.noteVisualizer?.TriggerNoteBlastOff(this);
        _spawnedNotes.Clear();
        persistentLoopNotes.Clear();
        _loopCacheDirtyPending = true;
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

    private void SpawnCollectableBurst(NoteSet noteSet, int maxToSpawn = -1, int forcedBurstId = -1) {

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
    float spawnJitterRadius = 0.25f,
    BurstPlacementMode placementMode = BurstPlacementMode.Free,
    int trapSearchRadiusCells = 10,
    int trapBufferCells = 1)
{
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
    if (_currentNoteSet != noteSet) SetNoteSet(noteSet);

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

        // Defensive reset
        if (_expandCommitted)
        {
            Debug.LogWarning($"[TRK:STAGE_EXPAND] track={name} burstId={burstId} RESET stale expandCommitted=true oldTotalAtExpand={_oldTotalAtExpand} totalSteps={_totalSteps} loopMul={loopMultiplier} targetBin={targetBin}");
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

            // IMPORTANT: carry the SAME burstId through the retry
            EnqueueNextFrame(() => SpawnCollectableBurst(
                stagedNoteSet,
                maxToSpawn,
                burstId,          // force the same burstId
                originWorld,
                repelFromWorld,
                burstImpulse,
                spreadAngleDeg,
                spawnJitterRadius,
                placementMode,
                trapSearchRadiusCells,
                trapBufferCells));

            return;
        }

        Debug.Log($"[STAGE] {name} waiting={_waitingForDrumReady} dsp={drumTrack?.startDspTime ?? -1} loopLen={drumTrack?.GetLoopLengthInSeconds() ?? -1} noteSet={noteSet.GetStepList()}");

        // Reserve THIS burstId for after expansion; do NOT mint a new id.
        _pendingExpandForBurst = true;
        _pendingBurstAfterExpand = new PendingBurst
        {
            noteSet = noteSet,
            maxToSpawn = maxToSpawn,
            burstId = burstId,
            originWorld = originWorld,
            repelFromWorld = repelFromWorld,
            burstImpulse = burstImpulse,
            spreadAngleDeg = spreadAngleDeg
        };

        Debug.Log($"[TRK:STAGE_EXPAND] track={name} RESERVED burstId={burstId} targetBin={targetBin} loopMul={loopMultiplier} pendingExpand={_pendingExpandForBurst}. {controller.tracks.Length} tracks");

        foreach (var t in controller.tracks)
        {
            Debug.Log($"[RECOMPUTE] Attempting to recompute track {t}");
            if (t != null) controller.noteVisualizer.RecomputeTrackLayout(t);
        }

        HookExpandBoundary();
        return;
    }

    Debug.Log($"[TRK:BURST] OUTCOME=SPAWN_NOW track={name} burstId={burstId} targetBin={targetBin} loopMul={loopMultiplier} binSize={binSize} maxToSpawn={maxToSpawn}");

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

    List<Vector2Int> trappedCandidates = null;

    if (placementMode == BurstPlacementMode.TrappedInDustNearOrigin &&
        dustGen != null &&
        drumTrack != null &&
        originWorld.HasValue)
    {
        trappedCandidates = BuildTrappedCandidatesNearOrigin(
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

        Vector2Int chosenCell;
        if (!TryPickSpawnCell2D(
                placementMode,
                anchor,
                trapSearchRadiusCells,
                trapBufferCells,
                dustGen,
                drumTrack,
                usedCellsThisBurst,
                out chosenCell))
        {
            continue;
        }

        usedCellsThisBurst.Add(chosenCell);

        Vector3 spawnPos = drumTrack.GridToWorldPosition(chosenCell);

        if (placementMode == BurstPlacementMode.TrappedInDustNearOrigin && dustGen != null)
        {
            float cellWorld = Mathf.Max(0.001f, drumTrack.GetCellWorldSize());
            float radiusWorld = cellWorld * 0.15f;

            var gfm = GameFlowManager.Instance;
            MazeArchetype phaseNow = (gfm != null && gfm.phaseTransitionManager != null)
                ? gfm.phaseTransitionManager.currentPhase
                : drumTrack.GetCurrentPhaseSafe();

            dustGen.CarveTemporaryDiskFromCollectable(spawnPos, radiusWorld, phaseNow, holdSeconds: 0.65f);
        }

        if (originWorld.HasValue && spawnJitterRadius > 0f)
        {
            Vector2 j = UnityEngine.Random.insideUnitCircle * spawnJitterRadius;
            spawnPos += (Vector3)j;
        }

        var go = Instantiate(collectablePrefab, spawnPos, Quaternion.identity, collectableParent);
        if (!go) continue;

        if (!go.TryGetComponent(out Collectable c))
        {
            Destroy(go);
            continue;
        }

        // assign burstId BEFORE Initialize
        c.burstId = burstId;
        c.intendedStep = absStep;
        c.assignedInstrumentTrack = this;

        _scratchSteps.Clear();
        _scratchSteps.Add(absStep);

        c.isTrappedInDust = (placementMode == BurstPlacementMode.TrappedInDustNearOrigin);
        c.Initialize(note, dur, this, noteSet, _scratchSteps);

        if (burstImpulse > 0f && go.TryGetComponent<Rigidbody2D>(out var crb))
        {
            Vector3 from = repelFromWorld ?? originWorld ?? spawnPos;
            Vector2 away = (Vector2)(spawnPos - from);
            if (away.sqrMagnitude < 0.0001f) away = UnityEngine.Random.insideUnitCircle;
            away.Normalize();

            float half = Mathf.Max(0f, spreadAngleDeg) * 0.5f;
            float ang = UnityEngine.Random.Range(-half, half);
            Vector2 dir = (Vector2)(Quaternion.Euler(0f, 0f, ang) * (Vector3)away);
            crb.AddForce(dir * burstImpulse, ForceMode2D.Impulse);
        }

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

            c.AttachTetherAtSpawn(markerGO.transform, nv.noteTetherPrefab, trackColor, dur, absStep);
        }

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

        OnCollectableBurstCleared?.Invoke(this, burstId);
        Debug.LogWarning($"[TRK:BURST] OUTCOME=SPAWN_EMPTY_CLEARED track={name} burstId={burstId} targetBin={targetBin} binSize={binSize} steps={stepList.Count} gridW={gridW}");
        return;
    }

    Debug.Log($"[TRK:BURST] OUTCOME=SPAWN_OK track={name} burstId={burstId} spawnedCount={spawnedCount} targetBin={targetBin} binSize={binSize} loopMul={loopMultiplier}");

    _burstRemaining[burstId] = spawnedCount;
    _burstTotalSpawned[burstId] = spawnedCount;
    _burstCollected[burstId] = 0;

    SetBinAllocated(targetBin, true);

    controller?.noteVisualizer?.CanonicalizeTrackMarkers(this, currentBurstId);
    DebugBins($"AfterSpawn(bin={targetBin})");
}

    private bool IsCellTrulyTrapped(
    Vector2Int gp,
    int bufferCells,
    CosmicDustGenerator dustGen,
    int gridW,
    int gridH)
{
    // Must be dust, and not permanently clear.
    if (!dustGen.HasDustAt(gp)) return false;
    if (dustGen.IsPermanentlyClearCell(gp)) return false;

    // Buffer rule: within +/- bufferCells, ALL cells must be dust (and not permanently clear).
    // If any neighbor is open/cleared, this location is "near a thin part of the maze".
    for (int dx = -bufferCells; dx <= bufferCells; dx++)
    {
        for (int dy = -bufferCells; dy <= bufferCells; dy++)
        {
            int x = gp.x + dx;
            int y = gp.y + dy;
            if (x < 0 || y < 0 || x >= gridW || y >= gridH) return false; // treat bounds as unsafe

            var n = new Vector2Int(x, y);

            // If this neighbor is permanently clear, it's basically a corridor/structure → unsafe.
            if (dustGen.IsPermanentlyClearCell(n)) return false;

            // If neighbor is not dust, we’re too close to open space → unsafe.
            if (!dustGen.HasDustAt(n)) return false;
        }
    }

    return true;
}
private bool TryPickSpawnCell2D(
    BurstPlacementMode mode,
    Vector2Int anchor,
    int searchRadiusCells,
    int bufferCells,
    CosmicDustGenerator dustGen,
    DrumTrack drum,
    HashSet<Vector2Int> usedCells,
    out Vector2Int chosen)
{
    chosen = default;
    if (dustGen == null || drum == null) return false;

    int w = drum.GetSpawnGridWidth();
    int h = drum.GetSpawnGridHeight();
    if (w <= 0 || h <= 0) return false;

    // Clamp anchor into bounds
    anchor.x = Mathf.Clamp(anchor.x, 0, w - 1);
    anchor.y = Mathf.Clamp(anchor.y, 0, h - 1);

    // In trapped mode, we REQUIRE dust; no fallback to open cells.
    bool requireDust = (mode == BurstPlacementMode.TrappedInDustNearOrigin);

    // Spiral-ish search around anchor
    int rMax = Mathf.Max(0, searchRadiusCells);

    // We’ll progressively relax buffer if necessary, but still require dust in trapped mode.
    for (int buf = Mathf.Max(0, bufferCells); buf >= 0; buf--)
    {
        for (int r = 0; r <= rMax; r++)
        {
            for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
            {
                if (Mathf.Abs(dx) != r && Mathf.Abs(dy) != r) continue; // perimeter only

                int x = anchor.x + dx;
                int y = anchor.y + dy;
                if (x < 0 || y < 0 || x >= w || y >= h) continue;

                var gp = new Vector2Int(x, y);
                if (usedCells != null && usedCells.Contains(gp)) continue;
                if (!Collectable.IsCellFreeStatic(gp)) continue;

                bool hasDust = dustGen.HasDustAt(gp);
                if (requireDust && !hasDust) continue;

                // Deep-dust test: must be buf cells away from any open (including out-of-bounds).
                if (requireDust && buf > 0)
                {
                    if (!IsDeepDustCell(gp, buf, dustGen, w, h))
                        continue;
                }

                chosen = gp;
                return true;
            }
        }
    }

    if (requireDust)
        return false;

    // Free mode fallback: any free cell (prefer dust if you want; here we allow anything).
    for (int y = 0; y < h; y++)
    for (int x = 0; x < w; x++)
    {
        var gp = new Vector2Int(x, y);
        if (usedCells != null && usedCells.Contains(gp)) continue;
        if (!Collectable.IsCellFreeStatic(gp)) continue;
        chosen = gp;
        return true;
    }

    return false;
}

private bool IsDeepDustCell(Vector2Int gp, int buffer, CosmicDustGenerator dustGen, int w, int h)
{
    // Treat out-of-bounds as OPEN; this prevents “near edge” trapped placements.
    for (int dy = -buffer; dy <= buffer; dy++)
    for (int dx = -buffer; dx <= buffer; dx++)
    {
        int x = gp.x + dx;
        int y = gp.y + dy;

        if (x < 0 || y < 0 || x >= w || y >= h)
            return false; // edge is open

        var n = new Vector2Int(x, y);

        // “Deep dust” means *not* adjacent to any effectively-open cell
        if (!dustGen.IsEffectivelyDustCell(n))
            return false;
        
    }
    return true;
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
//GRAVITY VOID?
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
            EnqueueNextFrame(() => SpawnCollectableBurst(req.noteSet, req.maxToSpawn, req.burstId, req.originWorld, req.repelFromWorld, req.burstImpulse, req.spreadAngleDeg));
        }
        else
        {
            Debug.LogWarning($"[TRK:COMMIT_EXPAND] track={name} PATH=MAXED_DENSITY NO_REQ curBurstId={curBid0}");
        }
        Debug.Log($"[BURST] Recomputing Track Layouts");
        foreach (var t in controller.tracks) 
            if (t != null) controller.noteVisualizer.RecomputeTrackLayout(t);
        UnhookExpandBoundary();
        return;
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
    // Immediately re-sync controller/drum/ui to the new committed width.
    // Without this, the transport can be correct while the NoteVisualizer remains at 1 bin until the next loop.
    if (controller != null)
        controller.ResyncLeaderBinsNow();
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
        EnqueueNextFrame(() => SpawnCollectableBurst(req.noteSet, req.maxToSpawn, req.burstId, req.originWorld, req.repelFromWorld, req.burstImpulse, req.spreadAngleDeg));
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

    private void RebuildLoopFromModifiedNotes(List<(int, int, int, float)> modified, Vector3 _)
    {
        Debug.LogWarning(
            $"[TRK:CLEAR_LOOP] track={name} fn=RebuildLoopFromModifiedNotes " +
            $"modified={(modified != null ? modified.Count : -1)} " +
            $"persistentBefore={(persistentLoopNotes != null ? persistentLoopNotes.Count : -1)}\n" +
            Environment.StackTrace);

        persistentLoopNotes.Clear();
        _loopCacheDirtyPending = true;
        foreach (var obj in _spawnedNotes) if (obj) Destroy(obj);
        _spawnedNotes.Clear();

        if (modified != null)
        {
            foreach (var (step, note, dur, vel) in modified)
                AddNoteToLoop(step, note, dur, vel);
        }
    }

    public void PruneSpawnedCollectables()
    {
        if (spawnedCollectables == null) return;

        // Remove nulls and inactive pooled objects so controller doesn't think they're "in flight"
        spawnedCollectables.RemoveAll(go => go == null || !go.activeInHierarchy);
    }

    private int CollectNote(int stepIndex, int note, int durationTicks, float force)
    {
        // Commit immediately (the loop evolves as notes are collected).
        AddNoteToLoop(stepIndex, note, durationTicks, force);

        // Immediate tactile feedback (short), then audition on-grid (full voice).
        PlayCollectionConfirm(note, force);
        QuantizedAuditionToStep(stepIndex, note, durationTicks, force);

        return stepIndex;
    }
    private void QuantizedAuditionToStep(int stepIndex, int note, int durationTicks, float velocity)
    {
        if (drumTrack == null) return;

        // Use the leader transport so the audition aligns to what the player hears.
        int leaderSteps = Mathf.Max(1, drumTrack.GetLeaderSteps());
        float loopLenSec = Mathf.Max(0.0001f, drumTrack.GetLoopLengthInSeconds());

        // Normalize step into leader range.
        int s = stepIndex % leaderSteps;
        if (s < 0) s += leaderSteps;

        // If leaderStartDspTime hasn't been anchored yet, fail safe: don't audition.
        double loopStartDsp = drumTrack.leaderStartDspTime;
        if (loopStartDsp <= 0.0) return;

        double dspNow = AudioSettings.dspTime;

        // Time within current loop [0, loopLenSec)
        double tInLoop = (dspNow - loopStartDsp) % loopLenSec;
        if (tInLoop < 0) tInLoop += loopLenSec;

        double stepDur = loopLenSec / leaderSteps;
        double targetT = s * stepDur;

        // Seconds until next occurrence of that step.
        double dt = targetT - tInLoop;
        if (dt < 0) dt += loopLenSec;

        // If we're very close, play immediately to avoid “why didn’t it respond?”.
        const double snapEps = 0.050; // 50ms; tune to taste
        if (dt <= snapEps)
        {
            PlayNote(note, durationTicks, velocity);
            return;
        }

        // Schedule using DSP polling (more robust than WaitForSeconds if frame hitches occur).
        double targetDsp = dspNow + dt;
        StartCoroutine(PlayNoteAtDsp(targetDsp, note, durationTicks, velocity));
    }

    private IEnumerator PlayNoteAtDsp(double targetDsp, int note, int durationTicks, float velocity)
    {
        // Poll DSP time so we stay accurate under variable frame time.
        while (AudioSettings.dspTime < targetDsp)
            yield return null;

        PlayNote(note, durationTicks, velocity);
    }
    private void PlayCollectionConfirm(int note, float velocity)
    {
        if (midiStreamPlayer == null) return;

        // Short confirmation; do not trim to RemainingActiveWindowSec().
        const int confirmDurationMs = 35;

        midiStreamPlayer.MPTK_Channels[channel].ForcedPreset = preset;
        midiStreamPlayer.MPTK_Channels[channel].ForcedBank   = bank;

        int v = Mathf.Clamp(Mathf.RoundToInt(velocity * 0.45f), 1, 80);

        var ev = new MPTKEvent
        {
            Command  = MPTKCommand.NoteOn,
            Value    = note,
            Channel  = channel,
            Duration = confirmDurationMs,
            Velocity = v,
        };

        midiStreamPlayer.MPTK_PlayEvent(ev);
    }
    
    private IEnumerator PlayNoteAfterDelay(float delaySec, int note, int durationTicks, float velocity)
    {
        yield return new WaitForSeconds(delaySec);
        PlayNote(note, durationTicks, velocity);
    }

    private IEnumerator ResetPitchBendAfterDelay(float delay)
    {
        yield return new WaitForSeconds(delay);
        midiStreamPlayer.MPTK_Channels[channel].fluid_channel_pitch_bend(8192); // Center
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
