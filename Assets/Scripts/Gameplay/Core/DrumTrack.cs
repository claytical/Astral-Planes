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
    public float gridPadding = 1f;
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
 
    private PhaseTransitionManager _phaseTransitionManager;
    private bool _started;
    private int _lastLoopCount, _tLoop, _phaseCount;
    private AudioClip _pendingDrumLoop;
    public PhaseStar _star;
    private int _binIdx = -1;
    private int _binCount = 4;                   // default; PhaseStar can override per-spawn
    public event System.Action OnLoopBoundary; // fire in LoopRoutines()
    public event System.Action<MusicalPhase, PhaseStarBehaviorProfile> OnPhaseStarSpawned;
    public event System.Action<int,int> OnBinChanged; // (idx, binCount)
    public void SetBinCount(int bins) => _binCount = Mathf.Max(1, bins);

    void Start()
    {
        _phaseTransitionManager = GetComponent<PhaseTransitionManager>();
    }
    private void Update()
    {
        if (!GameFlowManager.Instance.ReadyToPlay())
        {
            return;
        }
        if (_gridCheckTimer >= _gridCheckInterval)
        {
            ValidateSpawnGrid();
            _gridCheckTimer = 0f;
        }
        float currentTime = drumAudioSource.time;
        float stepDuration = _loopLengthInSeconds / totalSteps;
        if (stepDuration <= 0)
        {
            return;
        }

        int absoluteStep = Mathf.FloorToInt(currentTime / stepDuration);
        currentStep = absoluteStep % totalSteps;

        // ✅ Use DSP time to track loops properly
        float elapsedTime = (float)(AudioSettings.dspTime - startDspTime);
        int currentLoop = Mathf.FloorToInt(elapsedTime / _loopLengthInSeconds);
        if (_loopLengthInSeconds > 0f)
        {
            int bins = Mathf.Max(1, _binCount);
            double dsp   = AudioSettings.dspTime;
            double pos   = (dsp - startDspTime) % _loopLengthInSeconds;
            if (pos < 0) pos += _loopLengthInSeconds;
            double binDur = _loopLengthInSeconds / bins;

            // small hysteresis avoids double-trigger near boundaries
            const double Eps = 1e-5;
            int idx = (int)((pos + Eps) / binDur);
            if (idx >= bins) idx -= bins;

            if (idx != _binIdx)
            {
                _binIdx = idx;
                OnBinChanged?.Invoke(_binIdx, bins);
            }
        }

        // ✅ Ensure this only runs once per loop restart
        if (currentLoop > _lastLoopCount)
        {
            _lastLoopCount = currentLoop;
            LoopRoutines();
        }
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
            if (_phaseTransitionManager.currentPhase != boot)
            {
                _phaseTransitionManager.HandlePhaseTransition(boot, "DrumTrack/ManualStart");
            }
        }
        var bootProfile = phasePersonalityRegistry != null ? phasePersonalityRegistry.Get(boot) : null; 
        if (GameFlowManager.Instance.dustGenerator != null && bootProfile != null) { 
            GameFlowManager.Instance.dustGenerator.ApplyProfile(bootProfile); 
            GameFlowManager.Instance.dustGenerator.cycleMode = CosmicDustGenerator.MazeCycleMode.Progressive;
            GameFlowManager.Instance.dustGenerator.progressiveMaze = true;
        }
        var initialClip = MusicalPhaseLibrary.GetRandomClip(boot);
        if (initialClip == null)
        {
            Debug.LogError("DrumTrack.ManualStart: No Establish clip found.");
            return;
        }

        // Clear any queued/scheduled transitions; we're booting the clock cleanly.
        QueuedPhase = null;
        _pendingDrumLoop = null;

        drumAudioSource.Stop();
        drumAudioSource.clip = initialClip;
        drumAudioSource.loop = true;

        // Align to DSP so the transport is stable from frame 0.
        double dspStart = AudioSettings.dspTime + 0.05;
        drumAudioSource.PlayScheduled(dspStart);
        startDspTime = dspStart;
        // If InitializeDrumLoop only sets counters/grid, keep it. If it also calls PlayScheduled, remove the call below.
        StartCoroutine(InitializeDrumLoop());
        isPhaseStarActive = false;

        var mpm = GameFlowManager.Instance?.progressionManager;
        if (mpm != null) {
            mpm.BootFirstPhaseStar(MusicalPhase.Establish, regenerateMaze: true);
        } else {
            UnityEngine.Debug.LogError("[DrumTrack] No MineNodeProgressionManager; cannot boot PhaseStar.");
        }
        
    }

    public void RequestPhaseStar(MusicalPhase phase, Vector2Int? cellHint = null)
    {
        Debug.Log($"[Spawn] RequestPhaseStar phase={phase} active={isPhaseStarActive} " +
                  $"hint={(cellHint.HasValue ? cellHint.Value.ToString() : "<none>")} " +
                  $"tracks={(GameFlowManager.Instance?.controller?.tracks?.Length ?? 0)} " +
                  $"prefab={(phaseStarPrefab ? "ok":"NULL")}");
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
        var gfm = GameFlowManager.Instance;
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
    killer.onDestroyed += () => isPhaseStarActive = false;
    killer.onDestroyed += () => { isPhaseStarActive = false; if (grid != null) grid.FreeCell(cell.x, cell.y); };
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

    // Wire star
//    _star.SetSpawnStrategyProfile(strategy);
    _star.Initialize(this, targets, profileAsset, phase);
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
        // distance (world units) between adjacent cell centers, matching GridToWorldPosition mapping
        var a = GridToWorldPosition(new Vector2Int(0, 0));
        var b = GridToWorldPosition(new Vector2Int(1, 0));
        return Vector2.Distance(a, b);
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
        for (int x = 0; x < GameFlowManager.Instance.spawnGrid.gridWidth; x++)
        {
            for (int y = 0; y < GameFlowManager.Instance.spawnGrid.gridHeight; y++)
            {
                if (!GameFlowManager.Instance.spawnGrid.IsCellAvailable(x, y))
                {
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
                        Debug.LogWarning($"Watchdog freed orphaned cell at {x},{y}");
                        GameFlowManager.Instance.spawnGrid.FreeCell(x, y);
                    }
                }
            }
        }
    }
    // DrumTrack.cs — add near other public helpers
    public float GetScreenWorldWidth()
    {
        float z = -Camera.main.transform.position.z;
        Vector3 bottomLeft  = Camera.main.ViewportToWorldPoint(new Vector3(0f, 0f, z));
        Vector3 topRight    = Camera.main.ViewportToWorldPoint(new Vector3(1f, 1f, z));
        // match GridToWorldPosition’s horizontal padding
        return (topRight.x - gridPadding) - (bottomLeft.x + gridPadding);
    }
    public float GetScreenWorldHeight()
    {
        float z = -Camera.main.transform.position.z;
        Vector3 topRight    = Camera.main.ViewportToWorldPoint(new Vector3(1f, 1f, z));
        float bottomY       = GameFlowManager.Instance.controller.noteVisualizer.GetTopWorldY(); // same bottom as GridToWorldPosition
        // match GridToWorldPosition’s vertical padding (bottom+gridPadding → top-gridPadding)
        return (topRight.y - gridPadding) - (bottomY + gridPadding);
    }
    public float GetNoteVisualizerTopY()
    {
        return GameFlowManager.Instance.controller.noteVisualizer.GetTopWorldY();
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
        float cameraDistance = -Camera.main.transform.position.z;

        Vector3 bottomLeft = Camera.main.ViewportToWorldPoint(new Vector3(0, 0, cameraDistance));
        Vector3 topRight = Camera.main.ViewportToWorldPoint(new Vector3(1, 1, cameraDistance));

        float normalizedX = gridPos.x / (float)(GameFlowManager.Instance.spawnGrid.gridWidth - 1);
        float normalizedY = gridPos.y / (float)(GameFlowManager.Instance.spawnGrid.gridHeight - 1);

        // 👇 Define how much vertical space (in world units) to reserve for the UI at the bottom
         // Adjust this to match your UI overlay height in world space

        float worldX = Mathf.Lerp(bottomLeft.x + gridPadding, topRight.x - gridPadding, normalizedX);
        float bottomY = GameFlowManager.Instance.controller.noteVisualizer.GetTopWorldY();
        float worldY = Mathf.Lerp(bottomY + gridPadding, topRight.y - gridPadding, normalizedY);

        return new Vector3(worldX, worldY, 0f);
    }
    public Vector2Int WorldToGridPosition(Vector3 worldPos)
    {
        float cameraDistance = -Camera.main.transform.position.z;

        Vector3 bottomLeft = Camera.main.ViewportToWorldPoint(new Vector3(0, 0, cameraDistance));
        Vector3 topRight = Camera.main.ViewportToWorldPoint(new Vector3(1, 1, cameraDistance));

        float normalizedX = Mathf.InverseLerp(bottomLeft.x, topRight.x, worldPos.x);
        float normalizedY = Mathf.InverseLerp(bottomLeft.y, topRight.y, worldPos.y);

        int gridX = Mathf.Clamp(Mathf.RoundToInt(normalizedX * (GameFlowManager.Instance.spawnGrid.gridWidth - 1)), 0, GameFlowManager.Instance.spawnGrid.gridWidth - 1);
        int gridY = Mathf.Clamp(Mathf.RoundToInt(normalizedY * (GameFlowManager.Instance.spawnGrid.gridHeight - 1)), 0, GameFlowManager.Instance.spawnGrid.gridHeight - 1);

        return new Vector2Int(gridX, gridY);
    }    
    public float GetLoopLengthInSeconds()
    {
        return _loopLengthInSeconds;
    }
    public float GetTimeToLoopEnd()
    {
        float elapsed = (float)((AudioSettings.dspTime - startDspTime) % _loopLengthInSeconds);
        return Mathf.Max(0f, _loopLengthInSeconds - elapsed);
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

        _loopLengthInSeconds = drumAudioSource.clip.length;
        if (_loopLengthInSeconds <= 0)
        {
            Debug.LogError(("DrumTrack: Loop length in seconds is invalid."));
        }
        drumAudioSource.loop = true; // ✅ Ensure the loop setting is applied
    }
    private void LoopRoutines()
    {
        if (isPhaseStarActive &&  _star.gameObject != null)
            _star.OnLoopBoundary_RearmIfNeeded();
        // Only breathe the maze between stars (or when you explicitly want a reset)
        if (GameFlowManager.Instance.dustGenerator != null 
            && !GameFlowManager.Instance.GhostCycleInProgress 
            && !isPhaseStarActive)  // 👈 add this guard
        {
            float loopSeconds = GetLoopLengthInSeconds();
            Vector2Int centerCell = WorldToGridPosition(transform.position);
            GameFlowManager.Instance.dustGenerator.TryRequestLoopAlignedCycle(GameFlowManager.Instance.phaseTransitionManager.currentPhase, centerCell, loopSeconds, 0.25f, 0.50f);
        }
        completedLoops++;
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

        while (drumAudioSource.time < _loopLengthInSeconds - 0.05f)
        {
            yield return null;
        }

        if (_pendingDrumLoop == null)
        {
            Debug.LogWarning("WaitAndChangeDrumLoop: No new drum loop was assigned!");
            yield break;
        }

        drumAudioSource.clip = _pendingDrumLoop;
        _loopLengthInSeconds = drumAudioSource.clip.length;

        double dspNow = AudioSettings.dspTime;
        double nextStart = Mathf.CeilToInt((float)(dspNow / _loopLengthInSeconds)) * _loopLengthInSeconds;

        drumAudioSource.PlayScheduled(nextStart);
        Debug.Log("🎶 New drum loop scheduled"); // 👈 Add this
        if (startDspTime == 0)
        {
            startDspTime = nextStart;
            _lastLoopCount = Mathf.FloorToInt((float)(AudioSettings.dspTime - startDspTime) / _loopLengthInSeconds); 

        }
        
        _pendingDrumLoop = null;
        QueuedPhase = null;

// ✅ Slow maze growth instead of instant; use queued or current phase
            if (GameFlowManager.Instance.dustGenerator != null && !GameFlowManager.Instance.GhostCycleInProgress)
            {
                var phaseForRegrowth = QueuedPhase ?? GameFlowManager.Instance.phaseTransitionManager.currentPhase;
                Vector2Int centerCell = WorldToGridPosition(transform.position);
                float radius = GameFlowManager.Instance.progressionManager.GetHollowRadiusForCurrentPhase(phaseForRegrowth);
                var growthCells = GameFlowManager.Instance.dustGenerator.CalculateMazeGrowth(centerCell, phaseForRegrowth, radius);
                GameFlowManager.Instance.dustGenerator.BeginStaggeredMazeRegrowth(growthCells);
            }


        _lastLoopCount = Mathf.FloorToInt((float)(AudioSettings.dspTime - startDspTime) / _loopLengthInSeconds);
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
// DrumTrack.cs
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