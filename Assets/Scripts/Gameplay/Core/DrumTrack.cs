using UnityEngine;
using System.Collections.Generic;
using System.Collections;
using System.Linq;
using Gameplay.Mining;
using Random = UnityEngine.Random;

public class PhaseSnapshot
{
    public MusicalPhase Pattern;
    public Color Color;
    public List<NoteEntry> CollectedNotes = new();
    public Dictionary<MusicalRole, float> TrackScores = new();
    public float Timestamp;

    public class NoteEntry
    {
        public int Step;
        public int Note;
        public float Velocity;
        public Color TrackColor;

        public NoteEntry(int step, int note, float velocity, Color trackColor)
        {
            this.Step = step;
            this.Note = note;
            this.Velocity = velocity;
            this.TrackColor = trackColor;
        }
    }
}

public class DrumTrack : MonoBehaviour
{
    public GameObject phaseStarPrefab;
    public MineNodePrefabRegistry nodePrefabRegistry;
    public MinedObjectPrefabRegistry minedObjectPrefabRegistry;
    public PhasePersonalityRegistry phasePersonalityRegistry; 
    public MusicalPhase? QueuedPhase;
    
    public float drumLoopBPM = 120f;
    public float gridPadding = 0f;
    public int totalSteps = 16;
    public float timingWindowSteps = 1f; // Can shrink to 0.5 or less as game progresses
    public AudioSource drumAudioSource;
    public double startDspTime;
    public List<PhaseSnapshot> SessionPhases = new();
    public List<MinedObject> activeMinedObjects = new List<MinedObject>();
    public List <MineNode> activeMineNodes = new List<MineNode>();
    public bool isPhaseStarActive;
    public int currentStep;
    
    public int completedLoops { get; private set; } = 0;
    private float _loopLengthInSeconds, _phaseStartTime;
    private float _gridCheckTimer;
    private readonly float _gridCheckInterval = 10f;
    private float _clipLengthSec;
    private const float kMinLen = 1e-4f; // guard for zero/denorm lengths
    private bool HasValidClipLen => _clipLengthSec > kMinLen;

    private PhaseTransitionManager _phaseTransitionManager;
    private bool _started;
    private int _lastLoopCount, _tLoop, _phaseCount;
    private AudioClip _pendingDrumLoop;
    public PhaseStar _star;
    private InstrumentTrackController _trackController;
    private int _binIdx = -1;
    private int _binCount = 4;                   // default; PhaseStar can override per-spawn
    private GameFlowManager GFM => GameFlowManager.Instance;
    public BeatMoodLibrary beatMoodLibrary;
    public BeatMood? QueuedBeatMood;
    private BeatMoodProfile _activeBeatProfile;
    public BeatMoodProfile ActiveBeatMoodProfile => _activeBeatProfile;
    [Header("Beat Mood Intensity Mapping")]
    [Tooltip("Intensity < lowThreshold uses lowIntensityMood.")]
    [Range(0f, 1f)] public float lowIntensityThreshold    = 0.25f;

    [Tooltip("Intensity < mediumThreshold uses mediumIntensityMood.")]
    [Range(0f, 1f)] public float mediumIntensityThreshold = 0.5f;

    [Tooltip("Intensity < highThreshold uses highIntensityMood.")]
    [Range(0f, 1f)] public float highIntensityThreshold   = 0.75f;

    [Tooltip("Beat mood used for very low intensity.")]
    public BeatMood lowIntensityMood;

    [Tooltip("Beat mood used for low–medium intensity.")]
    public BeatMood mediumIntensityMood;

    [Tooltip("Beat mood used for medium–high intensity.")]
    public BeatMood highIntensityMood;

    [Tooltip("Beat mood used for very high intensity.")]
    public BeatMood extremeIntensityMood;
    public event System.Action OnLoopBoundary; // fire in LoopRoutines()
    public event System.Action<MusicalPhase, PhaseStarBehaviorProfile> OnPhaseStarSpawned;
    public event System.Action<int,int> OnBinChanged; // (idx, binCount)
    private float EffectiveLoopLengthSec => (_trackController != null) ? _trackController.GetEffectiveLoopLengthInSeconds() : _clipLengthSec;
    public float GetLoopLengthInSeconds() => EffectiveLoopLengthSec;
    public float GetClipLengthInSeconds() => _clipLengthSec; // new helper for audio-bound code
    public struct PlayArea
    {
        public float left;
        public float right;
        public float bottom;
        public float top;
        public float width  => right - left;
        public float height => top - bottom;
    }

    public PlayArea GetPlayAreaWorld()
    {
        var cam = Camera.main;
        var gfm = GFM;

        if (!cam || gfm == null || gfm.controller == null || gfm.controller.noteVisualizer == null)
            return default;

        float z = -cam.transform.position.z;
        Vector3 bl = cam.ViewportToWorldPoint(new Vector3(0f, 0f, z));
        Vector3 tr = cam.ViewportToWorldPoint(new Vector3(1f, 1f, z));

        float nvTop = gfm.controller.noteVisualizer.GetTopWorldY();

        PlayArea area;
        area.left   = bl.x;
        area.right  = tr.x;
        area.bottom = nvTop;     // exact top of NV
        area.top    = tr.y;

        return area;
    }

    public void SetBinCount(int bins)
    {
        // Always clamp the requested value to something sane.
        int requested = Mathf.Max(1, bins);

        int final = requested;

        // If we have a track controller, treat it as authority for bin count:
        // transport bins == placement bins == leader loopMultiplier.
        if (_trackController != null)
        {
            int leaderBins = Mathf.Max(1, _trackController.GetMaxLoopMultiplier());

            if (leaderBins != requested)
            {
                Debug.LogWarning(
                    $"[DrumTrack] SetBinCount request={requested}, " +
                    $"but leader loopMultiplier={leaderBins}. " +
                    $"Using leaderBins as transport bin count.");
            }

            final = leaderBins;
        }

        _binCount = final;
    }
    void Start()
    {
        _phaseTransitionManager = GetComponent<PhaseTransitionManager>();
        _trackController        = GameFlowManager.Instance.controller;
        if (drumAudioSource && drumAudioSource.clip) {
            _clipLengthSec = Mathf.Max(drumAudioSource.clip.length, 0f);
        }

        // 🔍 Debug grid scale vs screen
        var gfm  = GFM;
        var dust = gfm != null ? gfm.dustGenerator : null;
        if (gfm != null && gfm.spawnGrid != null && dust != null)
        {
            float tile   = GetTileDiameterWorld();
            int   w      = gfm.spawnGrid.gridWidth;
            float worldW = tile * (w - 1);
            float scrW   = GetScreenWorldWidth();

            Debug.Log($"[GridScale] tile={tile:F3}, worldWide(grid)={worldW:F3}, screenWide={scrW:F3}, ratio={worldW/scrW:F3}");
        }
    }

    private void Update()
    {
        // 0) Manager may exist but not be ready (or still wiring scenes)
        var gfm = GFM;
        if (gfm == null || !gfm.ReadyToPlay())
        {
            return;
        }

        // 1) Watchdog timer for the spawn grid
        _gridCheckTimer += Time.deltaTime;
        if (_gridCheckTimer >= _gridCheckInterval)
        {
            ValidateSpawnGrid();
            _gridCheckTimer = 0f;
        }

        // 2) Transport/clip guards
        if (drumAudioSource == null || !HasValidClipLen || totalSteps <= 0)
            return;

        float currentTime  = drumAudioSource.time;
        float stepDuration = _clipLengthSec / totalSteps;
        if (stepDuration <= 0f || float.IsInfinity(stepDuration))
            return;

        int absoluteStep = Mathf.FloorToInt(currentTime / stepDuration);
        currentStep = absoluteStep % totalSteps;

        // 3) Loop/bins driven by EFFECTIVE loop length
        float elapsedTime  = (float)(AudioSettings.dspTime - startDspTime);
        float effectiveLen = EffectiveLoopLengthSec;

        if (effectiveLen > kMinLen)
        {
            int   bins = Mathf.Max(1, _binCount);
            double dsp = AudioSettings.dspTime;
            double pos = (dsp - startDspTime) % effectiveLen;
            if (pos < 0) pos += effectiveLen;
            double binDur = effectiveLen / bins;

            const double Eps = 1e-5;
            int idx = (int)((pos + Eps) / binDur);
            if (idx >= bins) idx -= bins;

            if (idx != _binIdx)
            {
                _binIdx = idx;
                OnBinChanged?.Invoke(_binIdx, bins);
            }

            int extendedLoop = Mathf.FloorToInt(elapsedTime / effectiveLen);
            if (extendedLoop > _lastLoopCount)
            {
                _lastLoopCount = extendedLoop;
                Debug.Log($"[LOOP] Extended loop {extendedLoop} (effectiveLen={effectiveLen:F2})");
                LoopRoutines();
            }
        }

        // 4) Housekeeping
        if (activeMineNodes != null)
            activeMineNodes.RemoveAll(n => n == null);
    }

public void ManualStart()
{
    isPhaseStarActive = false;
    if (_started) return;
    _started = true;

    if (drumAudioSource == null)
    {
        Debug.LogError("DrumTrack: No AudioSource assigned!");
        return;
    }

    // --- A) START THE CURRENT PHASE CLIP *NOW* (no queued change) ---
    // Establish is the first phase; play its loop immediately so timing is correct.
    var boot = MusicalPhase.Establish;

    if (_phaseTransitionManager != null)
    {
        Debug.Log($"[BOOT] Handling phase {_phaseTransitionManager.currentPhase}"); 
        _phaseTransitionManager.HandlePhaseTransition(boot, "DrumTrack/ManualStart");
    }
    else
    {
        Debug.Log($"[BOOT] no phase transition manager found! {_phaseTransitionManager}");
    }
    var bootProfile = phasePersonalityRegistry != null ? phasePersonalityRegistry.Get(boot) : null;
    if (GameFlowManager.Instance.dustGenerator != null && bootProfile != null)
    {
        GameFlowManager.Instance.dustGenerator.ApplyProfile(bootProfile);
        SyncTileWithScreen();
        GameFlowManager.Instance.dustGenerator.cycleMode     = CosmicDustGenerator.MazeCycleMode.Progressive;
        GameFlowManager.Instance.dustGenerator.progressiveMaze = true;
    }

    AudioClip initialClip = null;
    if (_phaseTransitionManager != null &&
        _phaseTransitionManager.currentMotif != null &&
        beatMoodLibrary != null)
    {
        var mood = _phaseTransitionManager.currentMotif.beatMood;
        initialClip = beatMoodLibrary.GetRandomClip(mood);
        Debug.Log($"[BOOT] Using motif '{_phaseTransitionManager.currentMotif.motifId}' beat mood '{mood}' for initial drum loop: '{(initialClip != null ? initialClip.name : "null")}'.");
    }

    if (initialClip == null)
    {
        initialClip = MusicalPhaseLibrary.GetRandomClip(boot);
        Debug.LogError("DrumTrack.ManualStart: No Establish clip found.");
        return;
    }

    // Clear any queued/scheduled transitions; we're booting the clock cleanly.
    QueuedPhase      = null;
    _pendingDrumLoop = null;

    drumAudioSource.Stop();
    drumAudioSource.clip = initialClip;
    drumAudioSource.loop = true;
    _trackController     = GameFlowManager.Instance.controller;
    _clipLengthSec       = Mathf.Max(initialClip.length, 0f);

    // Align to DSP so the transport is stable from frame 0.
    double dspStart = AudioSettings.dspTime + 0.05;
    drumAudioSource.PlayScheduled(dspStart);
    startDspTime = dspStart;
    StartCoroutine(InitializeDrumLoop());
    isPhaseStarActive = false;


}


public void RequestPhaseStar(MusicalPhase phase, Vector2Int? cellHint = null)
{
    Debug.Log($"[Spawn] RequestPhaseStar phase={phase} active={isPhaseStarActive} " +
              $"hint={(cellHint.HasValue ? cellHint.Value.ToString() : "<none>")} " +
              $"tracks={(GameFlowManager.Instance?.controller?.tracks?.Length ?? 0)} " +
              $"prefab={(phaseStarPrefab ? "ok" : "NULL")}");

    if (isPhaseStarActive)
    {
        Debug.Log("[SpawnGuard] PhaseStar already active; abort.");
        return;
    }

    if (!phaseStarPrefab)
    {
        Debug.LogError("[Spawn] PhaseStar prefab is NULL.");
        return;
    }

    // Resolve dependencies up-front so we can error loudly instead of NRE
    var gfm  = GameFlowManager.Instance;
    var grid = gfm ? gfm.spawnGrid : null;
    var ctrl = gfm ? gfm.controller : null;
    if (!ctrl || ctrl.tracks == null || ctrl.tracks.Length == 0)
    {
        Debug.LogError("[Spawn] No instrument tracks available.");
        return;
    }

    // Pick a cell (prefer hint)
    Vector2Int cell = cellHint ?? (grid != null ? grid.GetRandomAvailableCell() : GetRandomAvailableCell());
    if (cell.x < 0)
    {
        Debug.LogWarning("[Spawn] 🚫 No available cell for PhaseStar.");
        return;
    }

    var pos = GridToWorldPosition(cell);
    Debug.Log($"[Spawn] 🌠 Spawning PhaseStar at {cell} (world {pos}) for phase {phase}");

    // Instantiate
    var go = Instantiate(phaseStarPrefab, pos, Quaternion.identity);
    _star = go.GetComponent<PhaseStar>();
    if (!_star)
    {
        Debug.LogError("[Spawn] Prefab missing PhaseStar");
        Destroy(go);
        return;
    }

    isPhaseStarActive = true;

    // Simple hook – PhaseStar exposes OnDestroyed? If not, use a helper component:
    var killer = go.AddComponent<OnDestroyRelay>();
    killer.onDestroyed += () =>
    {
        isPhaseStarActive = false;
        if (grid != null) grid.FreeCell(cell.x, cell.y);
    };

    // Behavior profile + dust
    var profileAsset = phasePersonalityRegistry ? phasePersonalityRegistry.Get(phase) : null;
    if (gfm && gfm.dustGenerator && profileAsset) gfm.dustGenerator.ApplyProfile(profileAsset);
    if (gfm && gfm.dustGenerator) gfm.dustGenerator.RetintExisting(0.4f);

    // Targets
    IEnumerable<InstrumentTrack> targets = ctrl.tracks
        .Where(t => t != null)
        .OrderBy(_ => UnityEngine.Random.value)
        .Take(4)
        .ToList();

    // 🔹 Look up the motif for this spawn from the PhaseTransitionManager
    MotifProfile motif = null;
    var ptm = gfm ? gfm.phaseTransitionManager : null;
    if (ptm != null && ptm.currentMotif != null)
    {
        motif = ptm.currentMotif;

        // Optional sanity check: warn if phase/motif phase don't line up
        Debug.Log($"[Spawn] Using motif '{motif.motifId}' for PhaseStar (phase {phase}).");
    }
    else
    {
        Debug.Log("[Spawn] No current motif available; PhaseStar will use phase-based NoteSets.");
    }

    // Wire star (now motif-aware)
    _star.Initialize(this, targets, profileAsset, phase, motif);
    _star.WireBinSource(this);

    OnPhaseStarSpawned?.Invoke(phase, profileAsset);
}

    private sealed class OnDestroyRelay : MonoBehaviour
{
    public System.Action onDestroyed;
    private void OnDestroy() { try { onDestroyed?.Invoke(); } catch {} }
}
    public float TryGetBPM()
    {
        // return current BPM if known; otherwise <= 0 to signal "unknown"
        return drumLoopBPM > 0 ? drumLoopBPM : 0f;
    }

    public void ScheduleBeatMoodAndLoopChange(BeatMood mood)
    {
        QueuedBeatMood = mood;

        if (beatMoodLibrary == null)
        {
            Debug.LogWarning($"[DrumTrack] No BeatMoodLibrary assigned; cannot schedule mood {mood}.");
            return;
        }

        var profile = beatMoodLibrary.GetProfile(mood);
        if (profile == null)
        {
            Debug.LogWarning($"[DrumTrack] No BeatMoodProfile found for mood {mood}.");
            return;
        }

        var clip = profile.GetRandomLoopClip();
        if (clip == null)
        {
            Debug.LogWarning($"[DrumTrack] BeatMoodProfile {profile.name} has no loop clips for mood {mood}.");
            return;
        }

        _activeBeatProfile = profile;

        // Apply BPM / loop length from the BeatMood profile.
        drumLoopBPM = profile.bpm;
        totalSteps  = profile.stepsPerLoop;

        ScheduleDrumLoopChange(clip);

        // Keep your dust behavior consistent with phase changes.
        if (GameFlowManager.Instance.dustGenerator != null)
        {
            GameFlowManager.Instance.dustGenerator.cycleMode = CosmicDustGenerator.MazeCycleMode.Progressive;
        }
    }
    /// <summary>
    /// Apply a normalized intensity value [0..1] to the drum system by
    /// selecting an appropriate BeatMood and scheduling a loop change.
    /// Call this from gameplay systems that compute "how hard the player is going".
    /// </summary>
    public void ApplyBeatIntensity(float intensity01)
    {
        if (beatMoodLibrary == null)
            return;

        // Clamp to [0,1]
        float x = Mathf.Clamp01(intensity01);

        // Ensure thresholds are ordered
        float tLow    = Mathf.Min(lowIntensityThreshold, mediumIntensityThreshold);
        float tMed    = Mathf.Clamp(mediumIntensityThreshold, tLow, highIntensityThreshold);
        float tHigh   = Mathf.Max(highIntensityThreshold, tMed);

        // Map scalar to one of the four moods
        BeatMood target;
        if (x < tLow)
            target = lowIntensityMood;
        else if (x < tMed)
            target = mediumIntensityMood;
        else if (x < tHigh)
            target = highIntensityMood;
        else
            target = extremeIntensityMood;

        // If we already have an active profile with this mood, do nothing.
        if (_activeBeatProfile != null && _activeBeatProfile.mood == target)
            return;

        ScheduleBeatMoodAndLoopChange(target);
    }

    public bool TryFindPath(Vector2Int start, Vector2Int goal, List<Vector2Int> outPath)
    {
        outPath.Clear();
        if (start == goal) { outPath.Add(goal); return true; }

        int w = GetSpawnGridWidth(), h = GetSpawnGridHeight();
        bool InBounds(Vector2Int c) => (uint)c.x < (uint)w && (uint)c.y < (uint)h;
        var q = new Queue<Vector2Int>();
        var came = new Dictionary<Vector2Int, Vector2Int>();
        var seen = new HashSet<Vector2Int> { start };
        q.Enqueue(start);

        while (q.Count > 0)
        {
            var cur = q.Dequeue();
            foreach (var n in HexNeighbors(cur))
            {
                if (!InBounds(n) || seen.Contains(n)) continue;
                // 🚫 Dust/Node/etc. block; allow goal even if currently reserved (we'll stop at its center)
                if (!IsSpawnCellAvailable(n.x, n.y) && n != goal) continue;

                came[n] = cur;
                if (n == goal) {
                    // reconstruct
                    var p = n;
                    outPath.Add(p);
                    while (came.TryGetValue(p, out var prev)) { p = prev; outPath.Add(p); }
                    outPath.Reverse();
                    return true;
                }
                seen.Add(n);
                q.Enqueue(n);
            }
        }
        return false;
    }
    public void SchedulePhaseAndLoopChange(MusicalPhase nextPhase)
    {
        QueuedPhase = nextPhase;
        var clip = MusicalPhaseLibrary.GetRandomClip(nextPhase);
        if (clip == null)
        {
            Debug.LogWarning($"SchedulePhaseAndLoopChange: No drum loop found for phase {nextPhase}");
            return;
        }
        ScheduleDrumLoopChange(clip); 
        if (GameFlowManager.Instance.dustGenerator != null)
        {
            GameFlowManager.Instance.dustGenerator.cycleMode = CosmicDustGenerator.MazeCycleMode.Progressive;
        }
    }
    public void SetBridgeAccent(bool on)
    {
        // Simple example: LPF + lower hats when on
        // Wire into your mixer/filters as appropriate.
    }
    public void RestructureTracksWithRemixLogic()
    {
        foreach (var t in GameFlowManager.Instance.controller.tracks)
        {
            RemixTrack(t);
        }

        GameFlowManager.Instance.controller.UpdateVisualizer();
    }
    public float GetCellWorldSize()
    {
        return GetTileDiameterWorld();
    }
    public Vector2Int FarthestReachableCellInComponent(Vector2Int start)
    {
        int w = GetSpawnGridWidth(), h = GetSpawnGridHeight();
        bool InBounds(Vector2Int c) => (uint)c.x < (uint)w && (uint)c.y < (uint)h;

        var q = new Queue<Vector2Int>();
        var dist = new Dictionary<Vector2Int, int>();
        q.Enqueue(start);
        dist[start] = 0;

        Vector2Int far = start;
        int best = 0;

        while (q.Count > 0)
        {
            var cur = q.Dequeue();
            int d = dist[cur];
            if (d > best) { best = d; far = cur; }

            foreach (var nb in HexNeighbors(cur)) // your existing neighbor func
            {
                if (!InBounds(nb) || dist.ContainsKey(nb)) continue;
                if (!IsSpawnCellAvailable(nb.x, nb.y)) continue; // dust = walls
                dist[nb] = d + 1;
                q.Enqueue(nb);
            }
        }
        return far;
    }
    public Vector3 CellCenter(Vector2Int c) => GridToWorldPosition(c);
    public Vector2Int CellOf(Vector3 world) => WorldToGridPosition(world);
    public void RegisterMinedObject(MinedObject obj)
    {
        if (!activeMinedObjects.Contains(obj))
        {
            activeMinedObjects.Add(obj);
        }
    }
    public void UnregisterMinedObject(MinedObject obj)
    {
        Debug.Log($"Removing MinedObject {obj}. Total Count: {activeMinedObjects.Count}");
        activeMinedObjects.Remove(obj);
        Debug.Log($"Mined Object Count Now: {activeMinedObjects.Count}");
    }
    private void ValidateSpawnGrid()
    {
        var gfm = GFM;
        if (gfm == null || gfm.spawnGrid == null)
            return;

        var grid = gfm.spawnGrid;

        for (int x = 0; x < grid.gridWidth; x++)
        {
            for (int y = 0; y < grid.gridHeight; y++)
            {
                // Skip empty cells outright
                if (grid.IsCellAvailable(x, y))
                    continue;

                // 🔐 Only validate cells that *should* belong to Collectables / notes.
                // Do NOT touch Dust or MineNode occupancy here.
                var cell = grid.GridCells[x, y];
                if (cell.ObjectType != GridObjectType.Note)
                    continue;

                Vector3 worldPos = GridToWorldPosition(new Vector2Int(x, y));
                Collider2D[] hits = Physics2D.OverlapCircleAll(worldPos, 0.25f);

                bool objectPresent = false;
                foreach (var hit in hits)
                {
                    if (hit.GetComponent<Collectable>())
                    {
                        objectPresent = true;
                        break;
                    }
                }

                if (!objectPresent)
                {
                    grid.FreeCell(x, y);
                }
            }
        }
    }

    public float GetScreenWorldWidth()
    {
        var cam = Camera.main;
        if (!cam)
        {
            Debug.LogWarning("[DrumTrack] GetScreenWorldWidth: Camera.main is null.");
            return 0f;
        }

        float z          = -cam.transform.position.z;
        Vector3 bottomLeft = cam.ViewportToWorldPoint(new Vector3(0f, 0f, z));
        Vector3 topRight   = cam.ViewportToWorldPoint(new Vector3(1f, 1f, z));
        return (topRight.x - gridPadding) - (bottomLeft.x + gridPadding);
    }
    
    public int GetSpawnGridWidth()
    {
        return GameFlowManager.Instance.spawnGrid.gridWidth;
    }
    public bool IsSpawnCellAvailable(int x, int y)
    {
        return GameFlowManager.Instance.spawnGrid.IsCellAvailable(x, y);
    }
    public bool HasSpawnGrid()
    {
        return GameFlowManager.Instance.spawnGrid != null;
    }
    public void OccupySpawnGridCell(int x, int y, GridObjectType gridObjectType)
    {
        GameFlowManager.Instance.spawnGrid.OccupyCell(x, y, gridObjectType);
    }
    public void ResetSpawnCellBehavior(int x, int y)
    {
        GameFlowManager.Instance.spawnGrid.ResetCellBehavior(x, y);
    }
    public void FreeSpawnCell(int x, int y)
    {
        GameFlowManager.Instance.spawnGrid.FreeCell(x, y);
    }
    public Vector2Int GetRandomAvailableCell()
    {
        return GameFlowManager.Instance.spawnGrid.GetRandomAvailableCell();
    }
    public Vector3 GridToWorldPosition(Vector2Int gridPos)
    {
        var gfm = GFM;
        if (gfm == null || gfm.spawnGrid == null) return Vector3.zero;

        int w = gfm.spawnGrid.gridWidth;
        int h = gfm.spawnGrid.gridHeight;
        if (w <= 0 || h <= 0) return Vector3.zero;

        float tile = GetCellWorldSize();
        PlayArea area = GetPlayAreaWorld();

        float x = area.left   + (gridPos.x + 0.5f) * tile;
        float y = area.bottom + (gridPos.y + 0.5f) * tile;

        return new Vector3(x, y, 0f);
    }

    public float GetDustBandHeightWorld()
    {
        var cam = Camera.main;
        var gfm = GFM;

        if (!cam || gfm == null || gfm.controller == null || gfm.controller.noteVisualizer == null || gfm.spawnGrid == null)
        {
            Debug.LogWarning("[DrumTrack] GetDustBandHeightWorld: missing Camera, controller, NoteVisualizer, or spawnGrid.");
            return 0f;
        }

        // Bottom of band: top of NoteVisualizer
        float nvTop = gfm.controller.noteVisualizer.GetTopWorldY();

        // Top of band: top of the camera view
        float z = -cam.transform.position.z;
        var topRight = cam.ViewportToWorldPoint(new Vector3(1f, 1f, z));
        float camTopY = topRight.y;

        // Apply the same padding concept vertically if you want
        float bandBottom = nvTop + gridPadding;
        float bandTop    = camTopY - gridPadding;

        float bandHeight = bandTop - bandBottom;
        if (bandHeight <= 0f)
        {
            Debug.LogWarning($"[DrumTrack] GetDustBandHeightWorld: bandHeight <= 0 (bottom={bandBottom}, top={bandTop}).");
            return 0f;
        }

        return bandHeight;
    }
    public float GetDustBandBottomCenterY()
    {
        var gfm = GameFlowManager.Instance;
        if (gfm == null || gfm.controller == null || gfm.controller.noteVisualizer == null)
            return 0f;

        float tile = GetCellWorldSize();
        PlayArea area = GetPlayAreaWorld();

        // Bottom edge + half a tile gives the center of row 0
        return area.bottom + tile * 0.5f;
    }

    public float GetDustBandTopCenterY()
    {
        var gfm = GameFlowManager.Instance;
        if (gfm == null || gfm.spawnGrid == null)
            return GetDustBandBottomCenterY();

        int h = gfm.spawnGrid.gridHeight;
        if (h <= 0) return GetDustBandBottomCenterY();

        float tile = GetCellWorldSize();
        float bottomCenterY = GetDustBandBottomCenterY();

        return bottomCenterY + tile * (h - 1);
    }

    public Vector2Int WorldToGridPosition(Vector3 worldPos)
    {
        var gfm = GFM;
        if (gfm == null || gfm.spawnGrid == null) return Vector2Int.zero;

        int w = gfm.spawnGrid.gridWidth;
        int h = gfm.spawnGrid.gridHeight;
        if (w <= 0 || h <= 0) return Vector2Int.zero;

        float tile = GetCellWorldSize();
        PlayArea area = GetPlayAreaWorld();

        float gx = (worldPos.x - area.left)   / tile - 0.5f;
        float gy = (worldPos.y - area.bottom) / tile - 0.5f;

        int ix = Mathf.RoundToInt(gx);
        int iy = Mathf.RoundToInt(gy);

        ix = Mathf.Clamp(ix, 0, w - 1);
        iy = Mathf.Clamp(iy, 0, h - 1);

        return new Vector2Int(ix, iy);
    }

    public float GetTimeToLoopEnd(bool effective = true) { 
        float L = effective ? EffectiveLoopLengthSec : _clipLengthSec; 
        if (L <= 0f) return 0f; 
        float elapsed = (float)((AudioSettings.dspTime - startDspTime) % L); 
        return Mathf.Max(0f, L - elapsed);
    }
    public int GetSpawnGridHeight()
    {
        return GameFlowManager.Instance.spawnGrid.gridHeight;
    }
    public void ClearAllActiveMineNodes()
    {
        // Destroy tracked nodes if any
        if (activeMineNodes != null)
        {
            foreach (var n in activeMineNodes.ToList())
                if (n) Destroy(n.gameObject);
            activeMineNodes.Clear();
        }

        // Belt-and-suspenders: purge any stragglers not in the list
        foreach (var node in FindObjectsByType<MineNode>(FindObjectsSortMode.None))
            if (node) Destroy(node.gameObject);
    }
    public void ClearAllActiveMinedObjects()
    {
        // Clear MinedObjects (notes, modifiers, etc.)
        foreach (MinedObject obj in activeMinedObjects.ToList())
        {
            if (obj == null) continue;
            Destroy(obj.gameObject);
        }
        activeMinedObjects.Clear();
        
        // Reset grid
        GameFlowManager.Instance.spawnGrid?.ClearAll();
        ValidateSpawnGrid();
        
    }
    private IEnumerator InitializeDrumLoop()
    {
        // ✅ Wait until the AudioSource has a valid clip
        while (drumAudioSource.clip == null)
        {
            yield return null; // Wait until the next frame
        }
        _clipLengthSec = Mathf.Max(drumAudioSource.clip.length, 0f); 
        if (!HasValidClipLen)
        { 
            Debug.LogError("DrumTrack: Clip length is zero/invalid; aborting loop init."); 
            yield break;
        }
        drumAudioSource.loop = true; // ✅ Ensure the loop setting is applied
    }
    void OnDrawGizmosSelected()
    {
        if (!HasSpawnGrid()) return;

        Gizmos.color = Color.green;
        Gizmos.DrawSphere(GridToWorldPosition(new Vector2Int(0, 0)), 0.1f);                       // bottom-left grid
        Gizmos.DrawSphere(GridToWorldPosition(new Vector2Int(GetSpawnGridWidth() - 1, 0)), 0.1f); // bottom-right
        Gizmos.DrawSphere(GridToWorldPosition(new Vector2Int(0, GetSpawnGridHeight() - 1)), 0.1f); // top-left
        Gizmos.DrawSphere(GridToWorldPosition(new Vector2Int(GetSpawnGridWidth() - 1, GetSpawnGridHeight() - 1)), 0.1f); // top-right
    }

    private void LoopRoutines()
    { if (isPhaseStarActive &&  _star.gameObject != null && !GameFlowManager.Instance.controller.AnyCollectablesInFlight())
            _star.OnLoopBoundary_RearmIfNeeded();
        Debug.Log($"Loop Routine Running: {GetLoopLengthInSeconds()}");

        // Only breathe the maze between stars (or when you explicitly want a reset)
        if (GameFlowManager.Instance.dustGenerator != null
            && !GameFlowManager.Instance.GhostCycleInProgress
            && !isPhaseStarActive)  // 👈 add this guard
        {
            float loopSeconds = _trackController.GetEffectiveLoopLengthInSeconds();
            Vector2Int centerCell = WorldToGridPosition(transform.position);
            GameFlowManager.Instance.dustGenerator.TryRequestLoopAlignedCycle(GameFlowManager.Instance.phaseTransitionManager.currentPhase, centerCell, loopSeconds, 0.25f, 0.50f);
        }
        completedLoops++;
        Debug.Log($"[MNDBG] LoopBoundary: completedLoops(before)={completedLoops}, " +
                  $"activeMineNodes={activeMineNodes.Count}");

        OnLoopBoundary?.Invoke();
    }
    private IEnumerable<Vector2Int> HexNeighbors(Vector2Int c)
    {
        bool even = (c.y & 1) == 0; // even-r offset layout
        if (even)
        {
            yield return new Vector2Int(c.x + 1, c.y);
            yield return new Vector2Int(c.x - 1, c.y);
            yield return new Vector2Int(c.x    , c.y + 1);
            yield return new Vector2Int(c.x - 1, c.y + 1);
            yield return new Vector2Int(c.x    , c.y - 1);
            yield return new Vector2Int(c.x - 1, c.y - 1);
        }
        else
        {
            yield return new Vector2Int(c.x + 1, c.y);
            yield return new Vector2Int(c.x - 1, c.y);
            yield return new Vector2Int(c.x + 1, c.y + 1);
            yield return new Vector2Int(c.x    , c.y + 1);
            yield return new Vector2Int(c.x + 1, c.y - 1);
            yield return new Vector2Int(c.x    , c.y - 1);
        }
    }
    private void ScheduleDrumLoopChange(AudioClip newLoop)
    {
        // Store the new loop clip.
        _pendingDrumLoop = newLoop;

        // Start waiting for the current loop to finish.

        StartCoroutine(WaitAndChangeDrumLoop());
    }
private IEnumerator WaitAndChangeDrumLoop()
{
    if (drumAudioSource == null || drumAudioSource.clip == null)
    {
        Debug.LogError("WaitAndChangeDrumLoop: drumAudioSource or its clip is null!");
        yield break;
    }

    float oldLen = drumAudioSource.clip ? drumAudioSource.clip.length : 0f;
    if (oldLen <= kMinLen)
    {
        Debug.LogError("WaitAndChangeDrumLoop: current clip length is zero/invalid.");
        yield break;
    }

    while (drumAudioSource.time < oldLen - 0.05f)
    {
        yield return null;
    }

    if (_pendingDrumLoop == null)
    {
        Debug.LogWarning("WaitAndChangeDrumLoop: No new drum loop was assigned!");
        yield break;
    }

    drumAudioSource.clip = _pendingDrumLoop;
    float newLen = drumAudioSource.clip ? drumAudioSource.clip.length : 0f;
    if (newLen <= kMinLen)
    {
        Debug.LogError("WaitAndChangeDrumLoop: new clip length is zero/invalid.");
        yield break;
    }

    _clipLengthSec = newLen;
    double dspNow  = AudioSettings.dspTime; // schedule on a multiple of the NEW clip length (validated above)
    double cycles  = dspNow / newLen;
    double nextStart = Mathf.CeilToInt((float)cycles) * newLen;
    drumAudioSource.PlayScheduled(nextStart);
    Debug.Log("🎶 New drum loop scheduled");

    if (startDspTime == 0)
    {
        startDspTime   = nextStart;
        _lastLoopCount = 0; // reset; we’re starting a new transport reference
    }

    _pendingDrumLoop = null;
    QueuedPhase      = null;

    // ✅ Slow maze growth instead of instant; use queued or current phase
    var gfm = GameFlowManager.Instance;
    if (gfm != null && gfm.dustGenerator != null && !gfm.GhostCycleInProgress)
    {
        var phaseForRegrowth = QueuedPhase ?? gfm.phaseTransitionManager.currentPhase;
        Vector2Int centerCell = WorldToGridPosition(transform.position);

        // Inline what MineNodeProgressionManager.GetHollowRadiusForCurrentPhase() did:
        float radius = 0f;
        if (phasePersonalityRegistry != null)
        {
            var persona = phasePersonalityRegistry.Get(phaseForRegrowth);
            if (persona != null)
                radius = Mathf.Max(0f, persona.starHoleRadius);
        }

        var growthCells = gfm.dustGenerator.CalculateMazeGrowth(centerCell, phaseForRegrowth, radius);
        gfm.dustGenerator.BeginStaggeredMazeRegrowth(growthCells);
    }

    _lastLoopCount = Mathf.FloorToInt((float)(AudioSettings.dspTime - startDspTime) / _clipLengthSec);
}
private float GetTileDiameterWorld()
{
    var gfm  = GFM;
    var dust = gfm != null ? gfm.dustGenerator : null;

    if (dust != null && dust.TileDiameterWorld > 0f)
        return dust.TileDiameterWorld;

    // Fallback: distance between cells (0,0) and (1,0) using current mapping
    // so we don’t blow up if dustGenerator is missing in some scenes.
    var a = GridToWorldPosition_Legacy(new Vector2Int(0, 0));
    var b = GridToWorldPosition_Legacy(new Vector2Int(1, 0));
    float d = Vector2.Distance(a, b);
    return d > 0f ? d : 1f;
}
// Legacy mapping used only as a fallback for deriving an approximate cell size
private Vector3 GridToWorldPosition_Legacy(Vector2Int gridPos)
{
    var cam = Camera.main;
    var gfm = GFM;
    if (!cam || gfm == null || gfm.spawnGrid == null || gfm.controller == null || gfm.controller.noteVisualizer == null)
    {
        return Vector3.zero;
    }

    float cameraDistance = -cam.transform.position.z;

    Vector3 bottomLeft = cam.ViewportToWorldPoint(new Vector3(0, 0, cameraDistance));
    Vector3 topRight   = cam.ViewportToWorldPoint(new Vector3(1, 1, cameraDistance));

    float normalizedX = gridPos.x / (float)(gfm.spawnGrid.gridWidth  - 1);
    float normalizedY = gridPos.y / (float)(gfm.spawnGrid.gridHeight - 1);

    float worldX  = Mathf.Lerp(bottomLeft.x + gridPadding, topRight.x - gridPadding, normalizedX);
    float bottomY = GetDustBandBottomCenterY();
    float worldY  = Mathf.Lerp(bottomY + gridPadding, topRight.y - gridPadding, normalizedY);

    return new Vector3(worldX, worldY, 0f);
}

    private void RemixTrack(InstrumentTrack track)
    {
        track.ClearLoopedNotes(TrackClearType.Remix);
        var noteSet = track.GetCurrentNoteSet();
        if (noteSet == null) return;

        var profile = MusicalRoleProfileLibrary.GetProfile(track.assignedRole);
        noteSet.noteBehavior = profile.defaultBehavior;
        switch (profile.role) {
            case MusicalRole.Bass:
                noteSet.rhythmStyle = (GameFlowManager.Instance.phaseTransitionManager.currentPhase == MusicalPhase.Pop) ? RhythmStyle.FourOnTheFloor : RhythmStyle.Sparse;
                break;
            case MusicalRole.Lead:
                noteSet.rhythmStyle = RhythmStyle.Syncopated;
                break;
            case MusicalRole.Harmony:
                noteSet.chordPattern = (GameFlowManager.Instance.phaseTransitionManager.currentPhase == MusicalPhase.Intensify) ? ChordPattern.Arpeggiated : ChordPattern.RootTriad;
                break;
            case MusicalRole.Groove:
                noteSet.rhythmStyle = RhythmStyle.Dense;
                break;
        }

        noteSet.Initialize(track, track.GetTotalSteps());

        AddRandomNotes(track, noteSet, 6); // Initial remix
        if (track.GetNoteDensity() < 4)
            AddRandomNotes(track, noteSet, 4 - track.GetNoteDensity()); // Pad sparsity
    }
    public void SyncTileWithScreen()
    {
        var gfm  = GFM;
        var dust = gfm != null ? gfm.dustGenerator : null;
        if (gfm == null || gfm.spawnGrid == null || dust == null)
            return;

        int gridW = gfm.spawnGrid.gridWidth;
        int gridH = gfm.spawnGrid.gridHeight;
        if (gridW <= 0 || gridH <= 0) return;

        PlayArea area = GetPlayAreaWorld();
        if (area.width <= 0f || area.height <= 0f) return;

        // NOTE: Dividing by gridW / gridH, NOT (gridW - 1)/(gridH - 1)
        float tileX = area.width  / gridW;
        float tileY = area.height / gridH;

        float tile = Mathf.Min(tileX, tileY);   // square cells; in your case tileX == tileY

        dust.TileDiameterWorld = tile;
    }

    public int GetLeaderSteps()
    {
        var ctrl = GameFlowManager.Instance?.controller;
        if (ctrl == null || ctrl.tracks == null || ctrl.tracks.Length == 0)
            return totalSteps;

        int maxMul = 1;
        foreach (var t in ctrl.tracks)
        {
            if (t == null) continue;
            var notes = t.GetPersistentLoopNotes();
            if (notes != null && notes.Count > 0)
                maxMul = Mathf.Max(maxMul, Mathf.Max(1, t.loopMultiplier));
        }
        return totalSteps * maxMul;
    }


    private void AddRandomNotes(InstrumentTrack track, NoteSet noteSet, int count)
    {
        var steps = noteSet.GetStepList();
        var pitches = noteSet.GetNoteList();
        if (steps.Count == 0 || pitches.Count == 0) return;

        for (int i = 0; i < count; i++)
        {
            int step = steps[Random.Range(0, steps.Count)];
            int note = noteSet.GetNextArpeggiatedNote(step);
            int duration = track.CalculateNoteDuration(step, noteSet);
            float velocity = UnityEngine.Random.Range(60f, 100f);
            track.AddNoteToLoop(step, note, duration, velocity);
        }
    }

}