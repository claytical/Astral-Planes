using System;
using UnityEngine;
using System.Collections.Generic;
using System.Collections;
using System.Linq;
using Random = UnityEngine.Random;
public class PhaseSnapshot
{
    public MusicalPhase pattern;
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

public class DrumTrack : MonoBehaviour
{
    // Assuming these are declared and initialized elsewhere:
    public GameObject phaseStarPrefab;
    public float drumLoopBPM = 120f;
    public float gridPadding = 1f;
    public EnergyWaveEffect wave;
    public GalaxyVisualizer galaxyVisualizer;
    public DriftoneManager driftoneManager;
    [Header("Drum Pattern Visuals")]
    public List<DrumLoopPatternVisual> patternVisuals;
    public int totalSteps = 32;
    public float phaseHitVolume = 1f;
    public float boostVolume = 1f;
    public float fadeDuration = 8f; // seconds to fade out after one loop

    // Define candidate spawn steps: if you want to spawn onlly on every 8th note, set:
    public AudioSource drumAudioSource;
    public InstrumentTrackController trackController;
    public double startDspTime;
    public MusicalPhase currentPhase;
    public List<PhaseSnapshot> sessionPhases = new();
    
    private float gridCheckTimer = 0f;
    private float gridCheckInterval = 10f;
    private MusicalPhase? queuedPhase = null;
    private float loopLengthInSeconds;
    [SerializeField] private float loopDurationInSeconds = 8f;       // Duration of the loop.
    private SpawnGrid spawnGrid;
    private List<GameObject> activeNodes = new List<GameObject>(); // Track spawned nodes
    
    
    private List<MineNode> mineNodes = new List<MineNode>();
    private List<MinedObject> activeMinedObjects = new List<MinedObject>();
    private bool isPhaseStarActive = false;
    private AudioClip pendingDrumLoop = null;
    private int lastLoopCount = 0;
    private int currentStep = 0;
    private MineNodeProgressionManager progressionManager;
    private float phaseStartTime;
    private int phaseStartLoop;

    void Start()
    {
        spawnGrid = GetComponent<SpawnGrid>();
        progressionManager = GetComponent<MineNodeProgressionManager>();
        GameFlowManager.Instance.glitch = GetComponent<GlitchManager>();
        if (!GameFlowManager.Instance.ReadyToPlay())
        {
            return;
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
            CleanupExplodedMineNodes();
            LoopRoutines();
        }
    }
    public int GetRemainingMineNodeCount()
    {
        return mineNodes.Count;
    }
    public void ManualStart()
    {
        if (drumAudioSource == null)
        {
            Debug.LogError("DrumTrack: No AudioSource assigned!");
            return;
        }
        StartCoroutine(InitializeDrumLoop()); // ✅ Ensure it loads properly

        // 👇 Spawn first star after loop setup
        progressionManager.BeginFirstPhase();
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
    public void BeginPhase(MusicalPhase phase, MineNodeSpawnerSet spawnerSet)
    {
        queuedPhase = phase;
        SetMineNodeSpawnerSet(spawnerSet);
        ScheduleDrumLoopChange(MusicalPhaseLibrary.GetRandomClip(currentPhase));
        StartCoroutine(SpawnPhaseStarDelayed(phase, spawnerSet)); // ⬅ new
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
    public void FinalizeCurrentPhaseSnapshot()
    {
        var snapshot = new PhaseSnapshot
        {
            pattern = currentPhase,
            color = GetVisualForPattern(currentPhase)?.color ?? Color.white,
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
    public void RemoveActiveMineNode(GameObject node)
    {
        activeNodes.Remove(node);

    }
   
    private void SpawnPhaseStar(MusicalPhase phase, MineNodeSpawnerSet set)
    {
        Vector2Int cell = GetRandomAvailableCell();
        if (cell.x == -1) return;

        Vector3 pos = GridToWorldPosition(cell);
        GameObject star = Instantiate(phaseStarPrefab, pos, Quaternion.identity);

        PhaseStar starLogic = star.GetComponent<PhaseStar>();
        if (starLogic != null)
        {
            starLogic.Initialize(this, phase, set, progressionManager);
        }
        else
        {
            Debug.LogWarning("PhaseStar prefab missing PhaseStar script.");
        }
    }
    private int GetCurrentStep()
    {
        return currentStep;
    }
    private void LoopRoutines()
    {
        CleanupInvalidMineNodes(); // ✅ Remove invalid references
        HandleFloatingMineNodes(); // ✅ Separately process loose obstacles
        HandleEvolvingMineNodes(); // ✅ Process grid-based obstacles
        progressionManager.OnLoopCompleted();
        if (isPhaseStarActive)
        {
            SpawnMineNode();
        }
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
    private DrumLoopPatternVisual GetVisualForPattern(MusicalPhase pattern)
    {
        return patternVisuals.FirstOrDefault(v => v.patternType == pattern);
    }
    public Vector2Int GetRandomAvailableCell()
    {
        return spawnGrid.GetRandomAvailableCell();
    }
    private void SpawnMineNode()
    {
        

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
    private IEnumerator SpawnPhaseStarDelayed(MusicalPhase phase, MineNodeSpawnerSet set)
    {
        yield return new WaitUntil(() => GetCurrentStep() == 0);
        yield return new WaitForSeconds(1.5f);
        SpawnPhaseStar(phase, set);
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
        
        pendingDrumLoop = null;

        // Finalize queued phase change
        if (queuedPhase.HasValue)
        {
            currentPhase = queuedPhase.Value;
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

}