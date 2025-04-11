using System;
using UnityEngine;
using System.Collections.Generic;
using System.Collections;
using System.Linq;
using Random = UnityEngine.Random;
public class PhaseSnapshot
{
    public DrumLoopPattern pattern;
    public Color color;
    public List<NoteEntry> collectedNotes = new();
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


public enum DrumLoopPattern
{
    Establish,
    Evolve,
    Intensify,
    Release,
    Wildcard,
    Pop
}
public enum NodeType
{
    Standard
}

public class DrumTrack : MonoBehaviour
{
    // Assuming these are declared and initialized elsewhere:
    public float drumLoopBPM = 120f;
    public float gridPadding = 1f;
    public EnergyWaveEffect wave;
    public GalaxyVisualizer galaxyVisualizer;
    [Header("Drum Pattern Visuals")]
    public List<DrumLoopPatternVisual> patternVisuals;
    public AudioClip[] establishDrumClips;
    public AudioClip[] evolveDrumClips;
    public AudioClip[] intensifyDrumClips;
    public AudioClip[] releaseDrumClips;
    public AudioClip[] wildcardClips;
    public AudioClip[] popDrumClips;
    public int totalSteps = 32;
    // Define candidate spawn steps: if you want to spawn onlly on every 8th note, set:
    public AudioSource drumAudioSource;
    public InstrumentTrackController trackController;
    public double startDspTime;
    public DrumLoopPattern currentPattern = DrumLoopPattern.Establish;
    private bool patternLocked = false;
    private float gridCheckTimer = 0f;
    private float gridCheckInterval = 10f;
    private DrumLoopPattern queuedPattern;
    private SpawnerPhase? queuedPhase = null;
    private float loopLengthInSeconds;
    [SerializeField] private float loopDurationInSeconds = 8f;       // Duration of the loop.
    private SpawnGrid spawnGrid;
    private List<GameObject> activeNodes = new List<GameObject>(); // Track spawned nodes
//    private List<DrumLoopCollectable> drumLoopCollectables = new List<DrumLoopCollectable>();
    private List<DrumLoopCollectable> activeStars = new List<DrumLoopCollectable>();
    private List<DrumLoopCollectable> collectedStars = new List<DrumLoopCollectable>();
    private List<PhaseSnapshot> sessionPhases = new();

    private List<MineNode> mineNodes = new List<MineNode>();
    private List<MinedObject> activeMinedObjects = new List<MinedObject>();

    private bool isInitialized = false;    
    private int progressionIndex = 0;
    private float screenMinX = -8f; // Left boundary
    private float screenMaxX = 8f;  // Right boundary
    //private float stepWidth; // Dynamic width per step
    //private float nextSpawnTime = 0f;  // ✅ Tracks when the next obstacles should spawn
    private AudioClip pendingDrumLoop = null;
    private int lastLoopCount = 0;
    private int currentStep = 0;
    private int loopsRequiredBeforeEvolve = 2;
    private bool isTransitioning = false;
    private MineNodeProgressionManager progressionManager;
    public Vector3[] cornerPositions = new Vector3[] {
        new Vector3(-7f, 3f, 0f),  // Top-left
        new Vector3( 7f, 3f, 0f),  // Top-right
        new Vector3(-7f,-3f, 0f),  // Bottom-left
        new Vector3( 7f,-3f, 0f),  // Bottom-right
    };


    private void Update()
    {
        if (!GamepadManager.Instance.ReadyToPlay())
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
            Debug.Log($"Loop {currentLoop} started — calling LoopRoutines()");
            lastLoopCount = currentLoop;
            CleanupExplodedMineNodes();
            LoopRoutines();
        }
    }
    public int GetActiveStarCount()
    {
        return activeStars.Count;
    }


    public void NotifyStarExpired(DrumLoopCollectable expiredStar)
    {
        if (isTransitioning) return;

        RemoveDrumLoopCollectable(expiredStar); // 🔄 move this to the top

        if (GetCollectedStarCount() == 0 && GetActiveStarCount() == 0)
        {
         //   Debug.Log("All stars expired, rerolling pattern...");
         //   currentPattern = null;
         //   patternLocked = false;
        }
    }

    public void RegisterMineNode(MineNode node)
    {
        if (!mineNodes.Contains(node))
        {
            mineNodes.Add(node);
        }
    }
    public void RegisterMinedObject(MinedObject obj)
    {
        if (!activeMinedObjects.Contains(obj))
        {
            activeMinedObjects.Add(obj);
        }
    }
    public void ValidateSpawnGrid()
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
                        if (hit.GetComponent<Collectable>() || hit.GetComponent<MineNode>())
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

    public void UnregisterMinedObject(MinedObject obj)
    {
        activeMinedObjects.Remove(obj);
    }

    public void UnregisterMineNode(MineNode node)
    {
        mineNodes.Remove(node);
    }

    public int GetSpawnGridHeight()
    {
        return spawnGrid.gridHeight;
    }

    void FinalizeCurrentPhaseSnapshot()
    {
        var snapshot = new PhaseSnapshot
        {
            pattern = currentPattern,
            color = GetVisualForPattern(currentPattern)?.color ?? Color.white,
            timestamp = Time.time,
            collectedNotes = new List<PhaseSnapshot.NoteEntry>()
        };

        foreach (var track in trackController.tracks)
        {
            Color trackColor = track.trackColor;

            foreach (var (step, note, duration, velocity) in track.GetPersistentLoopNotes())
            {
                snapshot.collectedNotes.Add(new PhaseSnapshot.NoteEntry(step, note, velocity, trackColor));
            }
        }

        sessionPhases.Add(snapshot);
    }

    public void ClearAllActiveMineNodes()
    {
        // Clear MineNodeSpawners
        foreach (GameObject node in activeNodes.ToList())
        {
            if (node == null) continue;
            Explode explode = node.GetComponent<Explode>();
            if (explode != null) explode.DelayedExplosion(Random.Range(.2f,.5f));
            else Destroy(node);
        }
        activeNodes.Clear();

        // Clear MineNodes
        foreach (MineNode node in mineNodes.ToList())
        {
            if (node == null) continue;
            Explode explode = node.GetComponent<Explode>();
            if (explode != null) explode.DelayedExplosion(Random.Range(.2f,.5f));
            else Destroy(node.gameObject);
        }
        mineNodes.Clear();

        // Clear MinedObjects (notes, modifiers, etc.)
        foreach (MinedObject obj in activeMinedObjects.ToList())
        {
            if (obj == null) continue;
            Destroy(obj.gameObject);
        }
        activeMinedObjects.Clear();

        // Clear drum collectables
        foreach (DrumLoopCollectable collectable in activeStars.ToList())
        {

            if (collectable == null) continue;
            Explode explode = collectable.GetComponent<Explode>();
            if (explode != null) explode.DelayedExplosion(Random.Range(.2f,.5f));

            Destroy(collectable.gameObject);
        }
        activeStars.Clear();
//        collectedStars.Clear();

        // Reset grid
        spawnGrid?.ClearAll();
        ValidateSpawnGrid();
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

    public float GetLoopLengthInSeconds()
    {
        return loopDurationInSeconds;
    }
    
    public void SetTempo(float newBPM)
    {
        if (newBPM <= 0)
        {
            Debug.LogError("Invalid BPM value. Must be greater than zero.");
            return;
        }

        drumLoopBPM = newBPM;
        loopLengthInSeconds = (60f / drumLoopBPM) * totalSteps; // ✅ Recalculate loop length

        // ✅ Adjust the playback speed
        if (drumAudioSource != null && drumAudioSource.clip != null)
        {
            float originalBPM = 60f; // Adjust this to the original BPM of your drum loop
            drumAudioSource.pitch = drumLoopBPM / originalBPM; // ✅ Scale pitch to match new BPM

            Debug.Log($"Drum Loop BPM changed to {drumLoopBPM}, adjusted pitch: {drumAudioSource.pitch}");
        }
    }

    private Vector3 GetNextCornerPosition()
    {
        int index = GetCollectedStarCount() - 1; // ✅ Assign to next available corner
        if (index >= cornerPositions.Length) 
        {
            index = (index % cornerPositions.Length); // ✅ Wrap around if more than 4 Stars
        }
        Debug.Log(($"Corner Index: {index}"));
        return cornerPositions[index];
    }
    
    
    private IEnumerator MergeAllCollectablesIntoCenter()
    {
        Debug.Log("[DrumTrack] Merging collectables...");

        List<DrumLoopCollectable> collectablesToDestroy = new List<DrumLoopCollectable>(collectedStars);

        foreach (DrumLoopCollectable collectable in collectablesToDestroy)
        {
            if (collectable != null)
            {
                collectable.TriggerFinalBurst();
            }
        }

        yield return new WaitForSeconds(2f); // allow particles to play

        collectedStars.Clear(); // ✅ clear collected stars now
        isTransitioning = false;

        // ✅ Only notify progression manager once, here.
        progressionManager.NotifyStarsCollected(); // → triggers EvaluateProgression
        if (galaxyVisualizer != null)
        {
            var snapshot = BuildCurrentPhaseSnapshot();
            galaxyVisualizer.AddSnapshot(snapshot);
        }
        Debug.Log("[DrumTrack] Drum Loop Collectables reset and cleared.");
    }
    private PhaseSnapshot BuildCurrentPhaseSnapshot()
    {
        var snapshot = new PhaseSnapshot
        {
            pattern = currentPattern,
            color = GetVisualForPattern(currentPattern)?.color ?? Color.white,
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


    public void RemoveActiveMineNode(GameObject node)
    {
        activeNodes.Remove(node);

    }
    public void RemoveDrumLoopCollectable(DrumLoopCollectable collectable)
    {
        if (collectable == null) return;

        if (activeStars.Contains(collectable))
        {
            activeStars.Remove(collectable);
            Debug.Log($"[DrumTrack] Removed collectable: {collectable.name}");
        }
    }

    
    public float GetStarCollectionRatio()
    {
        return Mathf.Clamp01(GetCollectedStarCount() / 4f);
    }

    public int GetCollectedStarCount()
    {
        return collectedStars.Count;
    }

    public double GetRemainingLoopTime()
    {
        double dspNow = AudioSettings.dspTime; 
        double loopEndDsp = startDspTime + (lastLoopCount + 1) * loopLengthInSeconds;
        return Math.Max(0, loopEndDsp - dspNow);
    }
    private void ExplodeAllMineNodes()
    {
        // Handle Obstacles
        var nodeCopy = activeNodes.ToList();
        foreach (GameObject obstacle in nodeCopy)
        {
            if (obstacle != null)
            {
                Vector2Int gridPos = WorldToGridPosition(obstacle.transform.position);
            
                // Check if spawnGrid is not null before using it
                if (spawnGrid != null)
                {
                    spawnGrid.FreeCell(gridPos.x, gridPos.y);
                }
                else
                {
                    Debug.LogError("spawnGrid is null!");
                }

                Explode explodeComponent = obstacle.GetComponent<Explode>();
                if (explodeComponent != null)
                {
                    explodeComponent.Permanent();
                }
                else
                {
                    Destroy(obstacle);
                }
            }
        }
        activeNodes.Clear();
    }

    private void PushToNextPlane()
    {
        if (isTransitioning) return;
        isTransitioning = true;

        double dspNow = AudioSettings.dspTime;
        double loopEndDsp = startDspTime + (lastLoopCount + 1) * loopLengthInSeconds;
        double timeRemaining = loopEndDsp - dspNow;

        wave.TriggerWave(Vector3.zero, (float)timeRemaining);

        // 🔁 Peek at next phase and get its corresponding pattern
        SpawnerPhase? nextPhase = progressionManager.PeekNextPhase();
        DrumLoopPattern nextPattern = currentPattern; // fallback

        if (nextPhase.HasValue)
        {
            queuedPhase = nextPhase.Value;
            queuedPattern = (DrumLoopPattern)nextPhase.Value;
            nextPattern = queuedPattern;
        }
        // ✅ Now schedule the next drum loop using the correct pattern
        ScheduleDrumLoopChange(SelectDrumClip(nextPattern));

        // ✅ Update all collected star visuals before progressing
        var visual = GetVisualForPattern(nextPattern);
        foreach (var star in collectedStars)
        {
            star.pattern = nextPattern;
            star.ApplyVisual(visual);
        }

        ExplodeAllMineNodes();
    }

    public DrumLoopPattern CurrentPattern
    {
        get => currentPattern;
        set => currentPattern = value;
    }

    private DrumLoopPattern ChooseRandomPattern()
    {
        Array values = Enum.GetValues(typeof(DrumLoopPattern));
        return (DrumLoopPattern)values.GetValue(UnityEngine.Random.Range(0, values.Length));
    }
    public void SetPatternFromPhase(SpawnerPhase phase)
    {
        if (Enum.IsDefined(typeof(DrumLoopPattern), (int)phase))
        {
            currentPattern = (DrumLoopPattern)phase;
            Debug.Log($"[DrumTrack] Pattern set from phase: {phase} → {currentPattern}");
        }
        else
        {
            Debug.LogWarning($"[DrumTrack] No mapped pattern for phase {phase}");
        }
    }

    

    public int GetCurrentStep()
    {
        return currentStep;
    }

    private void LoopRoutines()
    {
        CleanupInvalidMineNodes(); // ✅ Remove invalid references
        HandleFloatingMineNodes(); // ✅ Separately process loose obstacles
        HandleEvolvingMineNodes(); // ✅ Process grid-based obstacles
        progressionManager.OnLoopCompleted();
        SpawnMineNode();
    }


    private void HandleEvolvingMineNodes()
    {
        foreach (var node in activeNodes.ToList()) // ✅ Safe iteration
        {
            MineNode gridNode = node.GetComponent<MineNode>();

            if (gridNode != null && !gridNode.isLoose) // ✅ Ensure it's still in the grid
            {
                gridNode.Age(); // ✅ Trigger standard obstacle behavior
            }
        }
    }
    public void RegisterDrumLoopCollectable(DrumLoopCollectable collectable)
    {
        if (!activeStars.Contains(collectable))
        {
            activeStars.Add(collectable);
            Debug.Log($"[DrumTrack] Registered collectable: {collectable.name}");
        }
    }
    public void CollectedDrumLoop(DrumLoopCollectable collectable)
    {
        Debug.Log($"[DrumTrack] CollectedDrumLoop() called for {collectable.name}");
        if (!patternLocked)
        {
            patternLocked = true;
            currentPattern = collectable.pattern;
        }

        if (!collectedStars.Contains(collectable))
        {
            collectedStars.Add(collectable);
        }

        RemoveDrumLoopCollectable(collectable);

        collectable.MoveToCorner(GetNextCornerPosition());
        // Send it to one of the 4 corners (0..3)
        // ✅ Update Star visuals dynamically
        foreach (var star in collectedStars)
        {
            var visual = GetVisualForPattern(currentPattern);
            star.ApplyVisual(visual);        
        }
        if (GetCollectedStarCount() >= 4)
        {
            FinalizeCurrentPhaseSnapshot();
            PushToNextPlane();
            StartCoroutine(MergeAllCollectablesIntoCenter());
        }
    }
    public DrumLoopPatternVisual GetVisualForPattern(DrumLoopPattern pattern)
    {
        return patternVisuals.FirstOrDefault(v => v.patternType == pattern);
    }

    public void RemoveMineNodeAt(Vector2Int gridPos)
    {
        GameObject nodeToRemove = null;

        foreach (GameObject node in activeNodes)
        {
            if (WorldToGridPosition(node.transform.position) == gridPos)
            {
                nodeToRemove = node;
                break; // ✅ Stop after finding the first matching obstacle
            }
        }

        if (nodeToRemove != null)
        {
            activeNodes.Remove(nodeToRemove);
            Destroy(nodeToRemove);

            // ✅ Ensure the grid cell is freed after removal
            spawnGrid.FreeCell(gridPos.x, gridPos.y);
        }
    }
    public int GetNoteDensityAcrossAllTracks()
    {
        if (trackController == null || trackController.tracks == null)
            return 0;

        int total = 0;
        foreach (var track in trackController.tracks)
        {
            total += track.CollectedNotesCount;
        }
        return total;
    }

    public Vector2Int GetRandomAvailableCell()
    {
        return spawnGrid.GetRandomAvailableCell();
    }
    private void SpawnMineNode()
    {
        Debug.Log($"Attempting to spawn a mine node at loop {lastLoopCount}");

        Vector2Int spawnCell = spawnGrid.GetRandomAvailableCell();
        if (spawnCell.x == -1)
        {
            Debug.LogWarning("No available spawn cell for mine node!");
            return;
        }

        Vector3 spawnPosition = GridToWorldPosition(spawnCell);
        MineNodeSpawnerSet currentSet = progressionManager.GetCurrentSpawnerSet();
        if (currentSet == null)
        {
            Debug.LogWarning("No current MineNodeSpawnerSet assigned.");
            return;
        }

        GameObject newNodeParent = Instantiate(currentSet.GetMineNode(), spawnPosition, Quaternion.identity);

        MineNodeSpawner evolvingNode = newNodeParent.GetComponent<MineNodeSpawner>();

        if (evolvingNode != null)
        {
            evolvingNode.SetDrumTrack(this);
            evolvingNode.SpawnNode(spawnCell); // ✅ Create the initial Block Obstacle
            activeNodes.Add(newNodeParent);
//            spawnGrid.OccupyCell(spawnCell.x, spawnCell.y, GridObjectType.Node);
        }
        else
        {
            Debug.LogError($"[DrumTrack No MineNodeSpawner assigned on {newNodeParent.name}.");
        }
    }
    
    private void HandleFloatingMineNodes()
    {
        foreach (var node in activeNodes.ToList()) // ✅ Iterate safely
        {
            MineNode floatingNode = node.GetComponent<MineNode>();

            if (floatingNode != null && floatingNode.isLoose)
            {
                floatingNode.Age(); // ✅ Ensure loose obstacles evolve on their own
            }
        }
    }

    
    private void ScheduleDrumLoopChange(AudioClip newLoop)
    {
        // Store the new loop clip.
        pendingDrumLoop = newLoop;
       
        // Start waiting for the current loop to finish.

        StartCoroutine(WaitAndChangeDrumLoop());
    }

    private AudioClip SelectDrumClip(DrumLoopPattern pattern)
    {
        switch (pattern)
        {
            case DrumLoopPattern.Establish:
                return GetRandomClip(establishDrumClips);
            case DrumLoopPattern.Evolve:
                return GetRandomClip(evolveDrumClips);
            case DrumLoopPattern.Intensify:
                return GetRandomClip(intensifyDrumClips);
            case DrumLoopPattern.Release:
                return GetRandomClip(releaseDrumClips);
            case DrumLoopPattern.Wildcard:
                return GetRandomClip(wildcardClips);
            case DrumLoopPattern.Pop:
                return GetRandomClip(popDrumClips);
            default:
                return GetRandomClip(establishDrumClips);
        }
    }
    private AudioClip GetRandomClip(AudioClip[] clips)
    {
        if (clips == null || clips.Length == 0) return null;
        return clips[Random.Range(0, clips.Length)];
    }
    public Vector3 GridToWorldPosition(Vector2Int gridPos)
    {
        float cameraDistance = -Camera.main.transform.position.z;

        Vector3 bottomLeft = Camera.main.ViewportToWorldPoint(new Vector3(0, 0, cameraDistance));
        Vector3 topRight = Camera.main.ViewportToWorldPoint(new Vector3(1, 1, cameraDistance));

        float normalizedX = gridPos.x / (float)(spawnGrid.gridWidth - 1);
        float normalizedY = gridPos.y / (float)(spawnGrid.gridHeight - 1);

        // 👇 Define how much vertical space (in world units) to reserve for the UI at the bottom
        float uiHeightOffset = 2f; // Adjust this to match your UI overlay height in world space

        float worldX = Mathf.Lerp(bottomLeft.x + gridPadding, topRight.x - gridPadding, normalizedX);
        float worldY = Mathf.Lerp(bottomLeft.y + gridPadding + uiHeightOffset, topRight.y - gridPadding, normalizedY);

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
        if (startDspTime == 0)
        {
            startDspTime = nextStart;
        }

        Debug.Log($"✅ Drum loop changed to: {pendingDrumLoop.name}");

        // Now safe to reset these as the transition is complete
        patternLocked = false;
        pendingDrumLoop = null;
// Finalize queued pattern + phase after drum loop starts
        if (queuedPhase.HasValue)
        {
            SetPatternFromPhase(queuedPhase.Value); // updates currentPattern
            progressionManager.MoveToNextPhase(specificPhase: queuedPhase.Value);
            queuedPhase = null;
        }
        lastLoopCount = Mathf.FloorToInt((float)(AudioSettings.dspTime - startDspTime) / loopLengthInSeconds);
    }

    private IEnumerator InitializeDrumLoop()
        {
            // ✅ Wait until the AudioSource has a valid clip
            while (drumAudioSource.clip == null)
            {
                Debug.Log("Waiting for drum audio clip to load...");
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

        Debug.Log($"Drum loop initialized with length {loopLengthInSeconds} seconds.");
        isInitialized = true;
        }

    void Start()
    {
        spawnGrid = GetComponent<SpawnGrid>();
        progressionManager = GetComponent<MineNodeProgressionManager>();

        if (!GamepadManager.Instance.ReadyToPlay())
        {
            return;
        }
    }

    public void ManualStart()
    {
        {
            if (drumAudioSource == null)
            {
                Debug.LogError("DrumTrack: No AudioSource assigned!");
                return;
            }
            StartCoroutine(InitializeDrumLoop()); // ✅ Ensure it loads properly

        }
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
    public void SetMineNodeSpawnerSet(MineNodeSpawnerSet set)
    {
        progressionManager.OverrideSpawnerSet(set);
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

    public void CleanupInvalidMineNodes()
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

    private void ResetDrumPitch()
    {
        drumAudioSource.pitch = 1.0f;
    }

}