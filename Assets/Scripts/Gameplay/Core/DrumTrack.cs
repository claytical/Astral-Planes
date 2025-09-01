using UnityEngine;
using System.Collections.Generic;
using System.Collections;
using System.Linq;
using Gameplay.Mining;
using Random = UnityEngine.Random;

public class PhaseSnapshot
{
    public MusicalPhase pattern;
    public Color color;
    public List<NoteEntry> collectedNotes = new();
    public Dictionary<MusicalRole, float> trackScores = new();
    public float timestamp;

    public class NoteEntry
    {
        public int step;
        public int note;
        public float velocity;
        public Color trackColor;

        public NoteEntry(int step, int note, float velocity, Color trackColor)
        {
            this.step = step;
            this.note = note;
            this.velocity = velocity;
            this.trackColor = trackColor;
        }
    }
}

public class DrumTrack : MonoBehaviour
{
    // Assuming these are declared and initialized elsewhere:
    public CosmicDustGenerator hexMazeGenerator;
    public GameObject phaseStarPrefab;
    public MineNodePrefabRegistry nodePrefabRegistry;
    public MinedObjectPrefabRegistry minedObjectPrefabRegistry;
    public StarProgressUI starProgressUI;
    public float drumLoopBPM = 120f;
    public float gridPadding = 1f;
    public GalaxyVisualizer galaxyVisualizer;
    [Header("Dark Star Mode")]
    public bool darkStarModeEnabled = false;
    public NoteCollectionMode collectionMode = NoteCollectionMode.TimedPuzzle;
    public AudioClip darkStarDrumLoop;
    //public AudioClip normalDrumLoop; // optional fallback
    [Header("Drum Pattern Visuals")]
    public List<DrumLoopPatternVisual> patternVisuals;
    public int totalSteps = 32;
    public AudioSource drumAudioSource;
    public InstrumentTrackController trackController;
    public double startDspTime;
    public MusicalPhase currentPhase;
    public List<PhaseSnapshot> sessionPhases = new();
    
    private float gridCheckTimer = 0f;
    private float gridCheckInterval = 10f;
    public MusicalPhase? queuedPhase = null;
    private float loopLengthInSeconds;
    [SerializeField] private float loopDurationInSeconds = 8f;       // Duration of the loop.
    public SpawnGrid spawnGrid;
    private List<GameObject> activeNodes = new List<GameObject>(); // Track spawned nodes
    public List<GameObject> activeHexagons = new List<GameObject>();

    private PhaseTransitionManager phaseTransitionManager;
    private List<MinedObject> activeMinedObjects = new List<MinedObject>();
    public bool isPhaseStarActive = false;
    private AudioClip pendingDrumLoop = null;
    private int lastLoopCount = 0;
    public int currentStep = 0;
    public MineNodeProgressionManager progressionManager;
    private float phaseStartTime;
    private int phaseStartLoop;
    private PhaseStar star;
    private int phaseCount = 0;
    private bool started = false;
    public float timingWindowSteps = 1f; // Can shrink to 0.5 or less as game progresses
    private float lastLoopStartTime;


    void Start()
    {
        spawnGrid = GetComponent<SpawnGrid>();
        progressionManager = GetComponent<MineNodeProgressionManager>();
        phaseTransitionManager = GetComponent<PhaseTransitionManager>();
        GameFlowManager.Instance.glitch = GetComponent<GlitchManager>();
        if (GameFlowManager.Instance.SelectedMode.Contains("River"))
        {
            darkStarModeEnabled = false;
            Debug.Log($"Playing the River");
        }
        else
        {
            darkStarModeEnabled = true;
            Debug.Log($"Playing the Fire");
        }
        starProgressUI.Initialize(7);
        if (!GameFlowManager.Instance.ReadyToPlay())
        {
            return;
        }
    }
    public void MarkLoopStartTime()
    {
        lastLoopStartTime = Time.time;
    }

    public float GetLoopStartTime()
    {
        return lastLoopStartTime;
    }

    public void ManualStart()
    {
        if (started) return;
        started = true;

        if (drumAudioSource == null)
        {
            Debug.LogError("DrumTrack: No AudioSource assigned!");
            return;
        }

        StartCoroutine(InitializeDrumLoop());

        // ✅ Let the loop handle phase startup
        queuedPhase = MusicalPhase.Establish;
        ScheduleDrumLoopChange(MusicalPhaseLibrary.GetRandomClip(MusicalPhase.Establish));
        if (hexMazeGenerator != null)
        {
            Vector2Int centerCell = WorldToGridPosition(transform.position);
            float radius = progressionManager.GetHollowRadiusForCurrentPhase();
            var growthCells = hexMazeGenerator.CalculateMazeGrowth(centerCell, queuedPhase.Value, radius);
            hexMazeGenerator.BeginStaggeredMazeRegrowth(growthCells);
        }
    }

    private void Update()
    {
        if (!GameFlowManager.Instance.ReadyToPlay())
        {
            return;
        }
        if (gridCheckTimer >= gridCheckInterval)
        {
            ValidateSpawnGrid();
            gridCheckTimer = 0f;
        }
        float currentTime = drumAudioSource.time;
        float stepDuration = loopLengthInSeconds / totalSteps;
        if (stepDuration <= 0)
        {
            return;
        }

        int absoluteStep = Mathf.FloorToInt(currentTime / stepDuration);
        currentStep = absoluteStep % totalSteps;

        // ✅ Use DSP time to track loops properly
        float elapsedTime = (float)(AudioSettings.dspTime - startDspTime);
        int currentLoop = Mathf.FloorToInt(elapsedTime / loopLengthInSeconds);

        // ✅ Ensure this only runs once per loop restart
        if (currentLoop > lastLoopCount)
        {
            lastLoopCount = currentLoop;
            LoopRoutines();
            MarkLoopStartTime();
        }
    }

    private void LoopRoutines()
    {
        CleanupExplodedMineNodes();
        CleanupInvalidMineNodes(); // ✅ Remove invalid references
        progressionManager.OnLoopCompleted();
        if (isPhaseStarActive)
        {
            //   SpawnMineNode();
        }
    }


    private void CleanupExplodedMineNodes()
    {
        for (int i = activeNodes.Count - 1; i >= 0; i--)
        {
            GameObject node = activeNodes[i];

            if (node == null) continue;

            Explode explode = node.GetComponent<Explode>();
            if (explode != null)
            {
                activeNodes.RemoveAt(i);
                Destroy(node);
            }
        }
    }
    private void CleanupInvalidMineNodes()
    {

        for (int i = activeNodes.Count - 1; i >= 0; i--)
        {
            GameObject node = activeNodes[i];

            if (node == null)
            {
                activeNodes.RemoveAt(i);
                continue;
            }

            if (node.GetComponent<MineNodeSpawner>() == null)
            {
                Vector2Int gridPos = WorldToGridPosition(node.transform.position);

                activeNodes.RemoveAt(i);
                spawnGrid.FreeCell(gridPos.x, gridPos.y);
                Destroy(node);
            }
        }
    }

    public void EnterDarkStarDrumLoop()
    {
        if (darkStarDrumLoop == null)
        {
            Debug.LogWarning("No DarkStar drum loop assigned.");
            return;
        }

        drumAudioSource.clip = darkStarDrumLoop;
        loopLengthInSeconds = darkStarDrumLoop.length;
        startDspTime = AudioSettings.dspTime;
        drumAudioSource.Play();
    }
    public void ExitDarkStarDrumLoop()
    {
        if (!queuedPhase.HasValue)
        {
            Debug.LogWarning("No queued phase to transition to after DarkStar.");
            return;
        }

        AudioClip nextLoop = MusicalPhaseLibrary.GetRandomClip(queuedPhase.Value);
        if (nextLoop == null)
        {
            Debug.LogWarning($"No drum loop found for phase {queuedPhase.Value}");
            return;
        }

        pendingDrumLoop = nextLoop;

        // Wait until the current loop is about to end, then schedule the switch
        StartCoroutine(WaitAndChangeDrumLoop());
    }
    public void ExitDarkStarMode()
    {
        hexMazeGenerator.ClearMaze();
        foreach (var track in trackController.tracks)
        {
            track.midiStreamPlayer.MPTK_Volume = 1f;
        }
        starProgressUI.SetShardState(phaseCount, StarProgressUI.ShardState.Fixed);
        phaseCount++;
    }
    
    
    public int GetRemainingMineNodeCount()
    {
        return activeMinedObjects.Count;
    }
    public void NotifyNoteCollected()
    {
        if (progressionManager == null)
        {
            progressionManager = GetComponent<MineNodeProgressionManager>();
            if (progressionManager == null)
            {
                Debug.LogError("❌ MineNodeProgressionManager is missing on DrumTrack GameObject.");
                return;
            }
        }

        progressionManager.OnMinedObjectCollected();
        progressionManager.TryAdvanceSet();
    }

    private void BeginPhase(MusicalPhase phase, SpawnStrategyProfile profile)
    {
        Debug.Log($"📣 BeginPhase called for: {phase} with profile: {profile}");
        queuedPhase = phase;
        ScheduleDrumLoopChange(MusicalPhaseLibrary.GetRandomClip(currentPhase));
        StartCoroutine(SpawnPhaseStarDelayed(phase, profile)); // ⬅ new
    }
    
    public void UnregisterHexagon(GameObject hex)
    {
        if (hex != null)
        {
            activeHexagons.Remove(hex);
        }
    }
    /*
    public void RegisterMineNode(MineNode node)
    {
        if (!mineNodes.Contains(node))
        {
            mineNodes.Add(node);
        }
    }*/
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
    /*
    public void UnregisterMineNode(MineNode node)
    {
        mineNodes.Remove(node);
    }
    */
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
    public int GetSpawnGridHeight()
    {
        return spawnGrid.gridHeight;
    }
    public PhaseSnapshot BuildCurrentPhaseSnapshot()
    {
        var snapshot = new PhaseSnapshot
        {
            pattern = currentPhase,
            color = GetVisualForPattern(currentPhase)?.color ?? Color.white,
            timestamp = Time.time,
            collectedNotes = new()
        };

        foreach (var track in trackController.tracks)
        {
            Color trackColor = track.trackColor;

            foreach (var (step, note, _, velocity) in track.GetPersistentLoopNotes())
            {
                snapshot.collectedNotes.Add(new PhaseSnapshot.NoteEntry(step, note, velocity, trackColor));
            }
        }


        return snapshot;
    }
    public void FinalizeCurrentPhaseSnapshot()
    {
        var snapshot = new PhaseSnapshot
        {
            pattern = currentPhase,
            color = GetVisualForPattern(currentPhase)?.color ?? Color.white,
            timestamp = Time.time,
            collectedNotes = new List<PhaseSnapshot.NoteEntry>(),
            trackScores = new Dictionary<MusicalRole, float>() 
        };

        foreach (var track in trackController.tracks)
        {
            Color trackColor = track.trackColor;

            foreach (var (step, note, duration, velocity) in track.GetPersistentLoopNotes())
            {
                snapshot.collectedNotes.Add(new PhaseSnapshot.NoteEntry(step, note, velocity, trackColor));
            }
            // ✅ Run evaluation just before archiving the score
            float score = track.EvaluateCompositeScore();
            snapshot.trackScores[track.assignedRole] = score;
        }

        sessionPhases.Add(snapshot);
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
    public void RestructureTracksWithRemixLogic()
    {
        int chosenTrack = Random.Range(0, trackController.tracks.Length);
        for (int i = 0; i < trackController.tracks.Length; i++)
        {
            if (chosenTrack == i)
            {
                RemixTrack(trackController.tracks[i]);
            }
            else
            {
                trackController.tracks[i].ClearLoopedNotes(TrackClearType.Remix);   
            }
        }

        trackController.UpdateVisualizer();
    }

    public void RemixTrack(InstrumentTrack track)
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
            int step = steps[UnityEngine.Random.Range(0, steps.Count)];
            int note = noteSet.GetNextArpeggiatedNote(step);
            int duration = track.CalculateNoteDuration(step, noteSet);
            float velocity = UnityEngine.Random.Range(60f, 100f);
            track.AddNoteToLoop(step, note, duration, velocity);
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
    public float GetGridCellSize()
    {
        return spawnGrid.cellSize;
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
    public Vector3 HexToWorldPosition(Vector2Int gridPos, float cellSize)
    {
        float width = cellSize;
        float height = Mathf.Sqrt(3f) / 2f * cellSize;

        float x = gridPos.x * width * 0.75f;
        float y = gridPos.y * height + (gridPos.x % 2 == 1 ? height / 2f : 0f);

        return new Vector3(x, y, 0f);
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
        return loopDurationInSeconds;
    }

   
    public void SpawnPhaseStar(MusicalPhase phase, SpawnStrategyProfile profile)
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

        star = starObject.GetComponent<PhaseStar>();
        if (star != null)
        {
            star.Initialize(this, profile, phase, progressionManager);
        }
        else
        {
            Debug.LogWarning("PhaseStar prefab missing PhaseStar script.");
        }
    }
    
    public Vector3 GetStarPosition()
    {
        return star.transform.position;
    }
    private int GetCurrentStep()
    {
        return currentStep;
    }
    private DrumLoopPatternVisual GetVisualForPattern(MusicalPhase pattern)
    {
        return patternVisuals.FirstOrDefault(v => v.patternType == pattern);
    }


    private IEnumerator InitializeDrumLoop()
    {
        // ✅ Wait until the AudioSource has a valid clip
        while (drumAudioSource.clip == null)
        {
            yield return null; // Wait until the next frame
        }

        loopLengthInSeconds = drumAudioSource.clip.length;
        if (loopLengthInSeconds <= 0)
        {
            Debug.LogError(("DrumTrack: Loop length in seconds is invalid."));
        }
        drumAudioSource.loop = true; // ✅ Ensure the loop setting is applied
        startDspTime = AudioSettings.dspTime;
        drumAudioSource.Play();

    }
    private IEnumerator WaitAndChangeDrumLoop()
    {
        if (drumAudioSource == null || drumAudioSource.clip == null)
        {
            Debug.LogError("WaitAndChangeDrumLoop: drumAudioSource or its clip is null!");
            yield break;
        }

        while (drumAudioSource.time < loopLengthInSeconds - 0.05f)
        {
            yield return null;
        }

        if (pendingDrumLoop == null)
        {
            Debug.LogWarning("WaitAndChangeDrumLoop: No new drum loop was assigned!");
            yield break;
        }

        drumAudioSource.clip = pendingDrumLoop;
        loopLengthInSeconds = drumAudioSource.clip.length;

        double dspNow = AudioSettings.dspTime;
        double nextStart = Mathf.CeilToInt((float)(dspNow / loopLengthInSeconds)) * loopLengthInSeconds;

        drumAudioSource.PlayScheduled(nextStart);
        Debug.Log("🎶 New drum loop scheduled"); // 👈 Add this
        if (startDspTime == 0)
        {
            startDspTime = nextStart;
        }
        
        pendingDrumLoop = null;

        // Finalize queued phase change
        Debug.Log($"🔍 WaitAndChangeDrumLoop: queuedPhase = {queuedPhase}, awaitingDarkStar = {progressionManager.IsAwaitingDarkStar()}");
        if (queuedPhase.HasValue && !progressionManager.IsAwaitingDarkStar())
        {
            if (GetRemainingMineNodeCount() > 0)
            {
                Debug.Log("⏳ Waiting: Mine nodes still active. Postpone phase shift.");
                StartCoroutine(WaitForMineNodesThenAdvance());
            }
            else
            {
                currentPhase = queuedPhase.Value;
                progressionManager.isPhaseInProgress = false;
                StartCoroutine(DelayedBeginPhase(queuedPhase.Value, progressionManager.GetCurrentSpawnerStrategyProfile()));
            }
            // ✅ Slow maze growth instead of instant
            if (hexMazeGenerator != null)
            {
                Vector2Int centerCell = WorldToGridPosition(transform.position);
                float radius = progressionManager.GetHollowRadiusForCurrentPhase();
                var growthCells = hexMazeGenerator.CalculateMazeGrowth(centerCell, queuedPhase.Value, radius);
                hexMazeGenerator.BeginStaggeredMazeRegrowth(growthCells);
            }
        }

        lastLoopCount = Mathf.FloorToInt((float)(AudioSettings.dspTime - startDspTime) / loopLengthInSeconds);
    }
    
    public void ScheduleDrumLoopChange(AudioClip newLoop)
    {
        // Store the new loop clip.
        pendingDrumLoop = newLoop;
       
        // Start waiting for the current loop to finish.

        StartCoroutine(WaitAndChangeDrumLoop());
    }
    private IEnumerator WaitForMineNodesThenAdvance()
    {
        yield return new WaitUntil(() => GetRemainingMineNodeCount() == 0);
        if (queuedPhase.HasValue)
        {
            StartCoroutine(WaitForPhaseStarToDieThenAdvance());
        }

    }
    public IEnumerator WaitForPhaseStarToDieThenAdvance()
    {
        // Wait until all mine nodes are gone AND no active phase star
        yield return new WaitUntil(() =>
            GetRemainingMineNodeCount() == 0 && !isPhaseStarActive);
        starProgressUI.SetShardState(phaseCount, StarProgressUI.ShardState.Debug);

        if (queuedPhase.HasValue)
        {
            currentPhase = queuedPhase.Value;
            progressionManager.MoveToNextPhase(specificPhase: queuedPhase.Value);
            phaseTransitionManager.HandlePhaseTransition(currentPhase);
            queuedPhase = null;
        }
        else
        {
            Debug.Log("📡 No queuedPhase set — calling EvaluateProgression()");
            progressionManager.EvaluateProgression();  // ✅ This will call MoveToNextPhase() based on currentPhase
        }
    }
    private IEnumerator DelayedBeginPhase(MusicalPhase phase, SpawnStrategyProfile profile)
    {
        yield return null; // wait one frame
        BeginPhase(phase, profile);
        queuedPhase = null; // ✅ now cleared AFTER BeginPhase is set up
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



}