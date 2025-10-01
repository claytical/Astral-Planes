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
    public CosmicDustGenerator hexMazeGenerator;
    public GameObject phaseStarPrefab;
    public MineNodePrefabRegistry nodePrefabRegistry;
    public MinedObjectPrefabRegistry minedObjectPrefabRegistry;
    public PhasePersonalityRegistry phasePersonalityRegistry; 
    public MusicalPhase? QueuedPhase;
    public float drumLoopBPM = 120f;
    public SpawnGrid spawnGrid;
    public float gridPadding = 1f;
    public int totalSteps = 32;
    public float timingWindowSteps = 1f; // Can shrink to 0.5 or less as game progresses
    public AudioSource drumAudioSource;
    public InstrumentTrackController trackController;
    public double startDspTime;
    public MusicalPhase currentPhase;
    public MineNodeProgressionManager progressionManager;
    public List<PhaseSnapshot> SessionPhases = new();
    public List<GameObject> activeHexagons = new List<GameObject>();
    public List<MinedObject> activeMinedObjects = new List<MinedObject>();
    public List <MineNode> activeMineNodes = new List<MineNode>();
    public bool isPhaseStarActive;
    public int currentStep;

    private float _loopLengthInSeconds, _phaseStartTime;
    private float _gridCheckTimer;
    private readonly float _gridCheckInterval = 10f;
 
    private PhaseTransitionManager _phaseTransitionManager;
    private bool _started;
    private int _lastLoopCount, _phaseStartLoop, _phaseCount;
    private AudioClip _pendingDrumLoop;
    private PhaseStar _star;
    public event System.Action OnLoopBoundary; // fire in LoopRoutines()

    void Start()
    {
        spawnGrid = GetComponent<SpawnGrid>();
        progressionManager = GetComponent<MineNodeProgressionManager>();
        _phaseTransitionManager = GetComponent<PhaseTransitionManager>();
        GameFlowManager.Instance.glitch = GetComponent<GlitchManager>();

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

        // ✅ Ensure this only runs once per loop restart
        if (currentLoop > _lastLoopCount)
        {
            _lastLoopCount = currentLoop;
            LoopRoutines();
        }
    }
    public void ManualStart()
    {
        if (_started) return;
        _started = true;

        if (drumAudioSource == null)
        {
            Debug.LogError("DrumTrack: No AudioSource assigned!");
            return;
        }

        // --- A) START THE CURRENT PHASE CLIP *NOW* (no queued change) ---
        // Establish is the first phase; play its loop immediately so timing is correct.
        currentPhase = MusicalPhase.Establish;
        var bootProfile = phasePersonalityRegistry != null ? phasePersonalityRegistry.Get(currentPhase) : null; 
        if (hexMazeGenerator != null && bootProfile != null) { 
            hexMazeGenerator.ApplyProfile(bootProfile); 
            hexMazeGenerator.cycleMode = CosmicDustGenerator.MazeCycleMode.ClassicLoopAligned;
        }
        var initialClip = MusicalPhaseLibrary.GetRandomClip(currentPhase);
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

        // If InitializeDrumLoop only sets counters/grid, keep it. If it also calls PlayScheduled, remove the call below.
        StartCoroutine(InitializeDrumLoop());

        // --- B) BUILD MAZE + SPAWN FIRST STAR (NO CLIP CHANGE SCHEDULED) ---
        if (hexMazeGenerator != null)
        {
            Debug.Log($"Building Maze for {currentPhase}");
            StartCoroutine(hexMazeGenerator.GenerateMazeThenPlacePhaseStar(
                MusicalPhase.Establish,
                progressionManager.GetCurrentSpawnerStrategyProfile()
            ));
        }
    }
    public void SpawnPhaseStar(MusicalPhase phase, SpawnStrategyProfile profile, bool armFirstPokeCommit = false)
    {
        Vector2Int cell = GetRandomAvailableCell();
        if (cell.x == -1)
        {
            Debug.LogWarning("🚫 No available cell for PhaseStar.");
            
            return;
        }

        Vector3 pos = GridToWorldPosition(cell);
        Debug.Log($"🌠 Spawning PhaseStar at {cell} for phase {phase}");
        GameObject starObject = Instantiate(phaseStarPrefab, pos, Quaternion.identity);
        isPhaseStarActive = true;

        _star = starObject.GetComponent<PhaseStar>();
        if (_star == null)
        {
            Debug.LogWarning("PhaseStar prefab missing PhaseStar script.");
            return;
        }

        var mgr = progressionManager != null ? progressionManager : GetComponent<MineNodeProgressionManager>();
        // Decide which tracks to target (up to 4). Adjust selection policy if you like.
        IEnumerable<InstrumentTrack> targets = trackController.tracks
            .OrderBy(_ => Random.value)
            .Take(4);
        // Give the star its spawner strategy (so bursts/misses can honor it later)
        _star.SetSpawnStrategyProfile(profile);
        var profileAsset = phasePersonalityRegistry != null ? phasePersonalityRegistry.Get(phase) : null;
        if (hexMazeGenerator != null && profileAsset != null)
            hexMazeGenerator.ApplyProfile(profileAsset);
        // Initialize using YOUR current signature
        _star.Initialize(this, mgr, targets, armFirstPokeCommit, profileAsset, phase);

        // (Optional) bind the musical phase profile for “first poke commits loop” flow
        var phaseProfile = mgr.GetProfileForPhase(phase);
        if (phaseProfile != null) mgr.BindPendingPhase(phaseProfile);
    }
    public void SpawnPhaseStarAtCell(MusicalPhase phase, SpawnStrategyProfile profile, Vector2Int cell)
    {
        if (cell.x < 0) { Debug.LogWarning("No valid cell for PhaseStar."); return; }
        if (phaseStarPrefab == null) { Debug.LogError("PhaseStar prefab is null."); return; }

        Vector3 pos = GridToWorldPosition(cell);
        Debug.Log($"🌟 Spawning PhaseStar at {cell} for phase {phase}");
        GameObject starObject = Instantiate(phaseStarPrefab, pos, Quaternion.identity);

        // Initialize the star exactly like SpawnPhaseStar(...)
        _star = starObject.GetComponent<PhaseStar>();
        if (_star != null)
        {
            var mgr = progressionManager != null ? progressionManager : GetComponent<MineNodeProgressionManager>();
            var phaseProfile = mgr.GetProfileForPhase(phase);
            var profileAsset = phasePersonalityRegistry != null ? phasePersonalityRegistry.Get(phase) : null;
            _star.SetSpawnStrategyProfile(profile);
            var targets = (trackController != null ? trackController.tracks : GetComponentsInChildren<InstrumentTrack>())
                .Where(t => t != null).OrderBy(_ => UnityEngine.Random.value).Take(4);
//HACK: behavior profile set on prefab
            _star.Initialize(this, mgr, targets, armFirstPokeCommit: false, profileAsset, phase);
            // Cache the musical phase profile for the upcoming “first poke commits” path, if you use it
            if (phaseProfile != null) mgr.BindPendingPhase(phaseProfile);
        }
        else
        {
            Debug.LogWarning("PhaseStar prefab missing PhaseStar component.");
        }

        // Mark grid & gate
        OccupySpawnGridCell(cell.x, cell.y, GridObjectType.Node);
        isPhaseStarActive = true;
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
        if (hexMazeGenerator != null) { 
            hexMazeGenerator.cycleMode = (nextPhase == MusicalPhase.Release) ? CosmicDustGenerator.MazeCycleMode.ClassicLoopAligned : CosmicDustGenerator.MazeCycleMode.Progressive;
        }
    }
    public void SetBridgeAccent(bool on)
    {
        // Simple example: LPF + lower hats when on
        // Wire into your mixer/filters as appropriate.
    }
    public void RestructureTracksWithRemixLogic()
    {
        foreach (var t in trackController.tracks)
        {
            RemixTrack(t);
        }

        trackController.UpdateVisualizer();
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
        for (int x = 0; x < spawnGrid.gridWidth; x++)
        {
            for (int y = 0; y < spawnGrid.gridHeight; y++)
            {
                if (!spawnGrid.IsCellAvailable(x, y))
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
                        spawnGrid.FreeCell(x, y);
                    }
                }
            }
        }
    }
    public int GetSpawnGridWidth()
    {
        return spawnGrid.gridWidth;
    }
    public bool IsSpawnCellAvailable(int x, int y)
    {
        return spawnGrid.IsCellAvailable(x, y);
    }
    public bool HasSpawnGrid()
    {
        return spawnGrid != null;
    }
    public void OccupySpawnGridCell(int x, int y, GridObjectType gridObjectType)
    {
        spawnGrid.OccupyCell(x, y, gridObjectType);
    }
    public void ResetSpawnCellBehavior(int x, int y)
    {
        spawnGrid.ResetCellBehavior(x, y);
    }
    public void FreeSpawnCell(int x, int y)
    {
        spawnGrid.FreeCell(x, y);
    }
    public Vector2Int GetRandomAvailableCell()
    {
        return spawnGrid.GetRandomAvailableCell();
    }
    public Vector3 GridToWorldPosition(Vector2Int gridPos)
    {
        float cameraDistance = -Camera.main.transform.position.z;

        Vector3 bottomLeft = Camera.main.ViewportToWorldPoint(new Vector3(0, 0, cameraDistance));
        Vector3 topRight = Camera.main.ViewportToWorldPoint(new Vector3(1, 1, cameraDistance));

        float normalizedX = gridPos.x / (float)(spawnGrid.gridWidth - 1);
        float normalizedY = gridPos.y / (float)(spawnGrid.gridHeight - 1);

        // 👇 Define how much vertical space (in world units) to reserve for the UI at the bottom
         // Adjust this to match your UI overlay height in world space

        float worldX = Mathf.Lerp(bottomLeft.x + gridPadding, topRight.x - gridPadding, normalizedX);
        float bottomY = trackController.noteVisualizer.GetTopWorldY();
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

        int gridX = Mathf.Clamp(Mathf.RoundToInt(normalizedX * (spawnGrid.gridWidth - 1)), 0, spawnGrid.gridWidth - 1);
        int gridY = Mathf.Clamp(Mathf.RoundToInt(normalizedY * (spawnGrid.gridHeight - 1)), 0, spawnGrid.gridHeight - 1);

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
        return spawnGrid.gridHeight;
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
        spawnGrid?.ClearAll();
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
        startDspTime = AudioSettings.dspTime;
        drumAudioSource.Play();

    }
    private void LoopRoutines()
    {
        Debug.Log($"[MAZE] Loop start @ {AudioSettings.dspTime:0.000}s, loopLen={GetLoopLengthInSeconds():0.000}s");
        // Only breathe the maze between stars (or when you explicitly want a reset)
        if (hexMazeGenerator != null 
            && !GameFlowManager.Instance.GhostCycleInProgress 
            && !isPhaseStarActive)  // 👈 add this guard
        {
            float loopSeconds = GetLoopLengthInSeconds();
            Vector2Int centerCell = WorldToGridPosition(transform.position);
            hexMazeGenerator.TryRequestLoopAlignedCycle(currentPhase, centerCell, loopSeconds, 0.25f, 0.50f);
        }
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
        }
        
        _pendingDrumLoop = null;
            if (GetRemainingMineNodeCount() > 0)
            {
                Debug.Log("⏳ Waiting: Mine nodes still active. Postpone phase shift.");
                StartCoroutine(WaitForMineNodesThenAdvance());
            }
            else
            {
                if (QueuedPhase.HasValue)
                {
                    currentPhase = QueuedPhase.Value;
                }
                progressionManager.isPhaseInProgress = false;
                StartCoroutine(DelayedBeginPhase(currentPhase, progressionManager.GetCurrentSpawnerStrategyProfile()));
            }
// ✅ Slow maze growth instead of instant; use queued or current phase
            if (hexMazeGenerator != null && !GameFlowManager.Instance.GhostCycleInProgress)
            {
                var phaseForRegrowth = QueuedPhase ?? currentPhase;
                Vector2Int centerCell = WorldToGridPosition(transform.position);
                float radius = progressionManager.GetHollowRadiusForCurrentPhase();
                var growthCells = hexMazeGenerator.CalculateMazeGrowth(centerCell, phaseForRegrowth, radius);
                hexMazeGenerator.BeginStaggeredMazeRegrowth(growthCells);
            }


        _lastLoopCount = Mathf.FloorToInt((float)(AudioSettings.dspTime - startDspTime) / _loopLengthInSeconds);
    }
    private IEnumerator DelayedBeginPhase(MusicalPhase phase, SpawnStrategyProfile profile)
    {
        yield return null; // wait one frame
        BeginPhase(phase, profile);
        QueuedPhase = null; // ✅ now cleared AFTER BeginPhase is set up
    }
    private void BeginPhase(MusicalPhase phase, SpawnStrategyProfile profile)
    {
        
        Debug.Log($"📣 BeginPhase called for: {phase} with profile: {profile}");
        StartCoroutine(SpawnPhaseStarDelayed(phase, profile)); // ⬅ new
    }
    private IEnumerator SpawnPhaseStarDelayed(MusicalPhase phase, SpawnStrategyProfile profile)
    {
        Debug.Log($"🕓 Waiting for next step to spawn PhaseStar: {phase}");

        yield return new WaitUntil(() => GetCurrentStep() == 0);
        Debug.Log("✅ Hit step 0, starting 1.5s delay...");

        yield return new WaitForSeconds(1.5f);
        Debug.Log("🌟 Calling SpawnPhaseStar now...");
        SpawnPhaseStar(phase, profile);
    }
    private IEnumerator WaitForMineNodesThenAdvance()
    {
        yield return new WaitUntil(() => GetRemainingMineNodeCount() == 0);
        if (QueuedPhase.HasValue)
        {
            StartCoroutine(WaitForPhaseStarToDieThenAdvance());
        }

    }
    private int GetRemainingMineNodeCount()
    {
        // Most robust: count live MinedObject components in the scene
        var objs = GameObject.FindObjectsOfType<MinedObject>(includeInactive: false);
        Debug.Log($"Looking up remaining count:{ objs.Length } ");
        return objs?.Length ?? 0;
    }
    private IEnumerator WaitForPhaseStarToDieThenAdvance()
    {
        // Wait until all mine nodes are gone AND no active phase star
        yield return new WaitUntil(() => GetRemainingMineNodeCount() == 0 && !isPhaseStarActive);
        if (QueuedPhase.HasValue)
        {
            currentPhase = QueuedPhase.Value;
            progressionManager.MoveToNextPhase(specificPhase: QueuedPhase.Value);
            _phaseTransitionManager.HandlePhaseTransition(currentPhase);
            QueuedPhase = null;
        }
        else
        {
            Debug.Log("📡 No queuedPhase set — calling EvaluateProgression()");
            progressionManager.EvaluateProgression();  // ✅ This will call MoveToNextPhase() based on currentPhase
        }
    }
    private void RemixTrack(InstrumentTrack track)
    {
        track.ClearLoopedNotes(TrackClearType.Remix);
        var noteSet = track.GetCurrentNoteSet();
        if (noteSet == null) return;

        var profile = MusicalRoleProfileLibrary.GetProfile(track.assignedRole);
        var phase = track.drumTrack.currentPhase;

        noteSet.noteBehavior = profile.defaultBehavior;

        switch (profile.role)
        {
            case MusicalRole.Bass:
                noteSet.noteBehavior = NoteBehavior.Bass;
                noteSet.rhythmStyle = (phase == MusicalPhase.Pop) ? RhythmStyle.FourOnTheFloor : RhythmStyle.Sparse;
                break;
            case MusicalRole.Lead:
                noteSet.noteBehavior = NoteBehavior.Lead;
                noteSet.rhythmStyle = RhythmStyle.Syncopated;
                break;
            case MusicalRole.Harmony:
                noteSet.noteBehavior = NoteBehavior.Harmony;
                noteSet.chordPattern = (phase == MusicalPhase.Intensify) ? ChordPattern.Arpeggiated : ChordPattern.RootTriad;
                break;
            case MusicalRole.Groove:
                noteSet.noteBehavior = NoteBehavior.Percussion;
                noteSet.rhythmStyle = RhythmStyle.Dense;
                break;
        }

        noteSet.Initialize(track, track.GetTotalSteps());

        AddRandomNotes(track, noteSet, 6); // Initial remix
        if (track.GetNoteDensity() < 4)
            AddRandomNotes(track, noteSet, 4 - track.GetNoteDensity()); // Pad sparsity
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
    private int GetCurrentStep()
    {
        return currentStep;
    }
    
}