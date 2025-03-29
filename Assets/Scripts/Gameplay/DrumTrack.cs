using System;
using UnityEngine;
using System.Collections.Generic;
using System.Collections;
using System.Linq;
using Random = UnityEngine.Random;

public enum DrumLoopPattern
{
    Full,      // Full build-up from 0 to n
    Breakbeat,    // A section that jumps into the later part of the full loop (e.g., n - 3 to n)
    SlowDown   // A special short pattern for slowing down (e.g., perhaps n - 2 to n)
}
public enum ObstacleType
{
    Standard,        // Default obstacle type
    HeavyBlock,      // Large, slow-moving obstacles (Bass behavior)
    FastProjectile,  // Small, fast-moving obstacles (Lead behavior)
    WaveField,       // Pulsating barriers (Harmony behavior)
    RhythmWall,      // Spawns in rhythm (Percussion behavior)
    FloatingHazard,  // Slow, hovering obstacles (Drone behavior)
    Hazard           // Dangerous, irregular obstacles (Breakbeat pattern)
}

public enum DrumLoopState
{
    Progression,
    Breakbeat
}

public class DrumTrack : MonoBehaviour
{
    // Assuming these are declared and initialized elsewhere:
    public float drumLoopBPM = 120f;
    public float gridPadding = 1f;
    public EnergyWaveEffect wave;
    public AudioClip[] fullLoopClips;
    public AudioClip[] drumFillClips;
    public int totalSteps = 32;
    // Define candidate spawn steps: if you want to spawn onlly on every 8th note, set:
    public AudioSource drumAudioSource;
    public InstrumentTrackController trackController;
    public float startDspTime;

    // Prefabs for different spawned objects:
    public EvolvingObstacleSet[] evolvingObstaclesSets;
    private int evolvingObstaclesSetIndex = 0;
    public GameObject[] obstaclePrefab;
    private int obstacleSpawnTypeIndex = 0;
    private float loopLengthInSeconds;       // Duration of the loop.
    private SpawnGrid spawnGrid;
    private List<GameObject> activeObstacles = new List<GameObject>(); // Track spawned obstacles
    private List<DrumLoopCollectable> drumLoopCollectables = new List<DrumLoopCollectable>();
    private bool isInitialized = false;    
    private int progressionIndex = 0;
    private DrumLoopState currentDrumLoopState = DrumLoopState.Progression;
    private float screenMinX = -8f; // Left boundary
    private float screenMaxX = 8f;  // Right boundary
    private float stepWidth; // Dynamic width per step
    private float nextSpawnTime = 0f;  // ✅ Tracks when the next obstacles should spawn
    private AudioClip pendingDrumLoop = null;
    private int lastLoopCount = 0;
    private int currentStep = 0;
    private int loopsRequiredBeforeEvolve = 2;
    private int drumLoopCollectablesCollected = 0;
    private bool isTransitioning = false;
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
            Debug.Log($"New Loop Started! Current Loop: {currentLoop}");
            lastLoopCount = currentLoop;
            CleanupExplodedObstacles();
            LoopRoutines();
        }

        if (drumLoopCollectablesCollected >= 4)
        {
            //DO DRUMS

        }
    }

    public int GetSpawnGridHeight()
    {
        return spawnGrid.gridHeight;
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
        return loopLengthInSeconds;
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

    public Vector3 GetNextCornerPosition()
    {
        int index = drumLoopCollectablesCollected - 1; // ✅ Assign to next available corner
        if (index >= cornerPositions.Length) 
        {
            index = (index % cornerPositions.Length); // ✅ Wrap around if more than 4 Stars
        }
        return cornerPositions[index];
    }

    public IEnumerator MergeAllCollectablesIntoCenter()
    {
        Debug.Log("[DrumTrack] Merging collectables...");

        List<DrumLoopCollectable> collectablesToDestroy = new List<DrumLoopCollectable>();

        foreach (DrumLoopCollectable collectable in drumLoopCollectables)
        {
            if (collectable != null)
            {
                Debug.Log($"[DrumTrack] Triggering final burst for: {collectable.name}");
                collectable.TriggerFinalBurst();
                collectablesToDestroy.Add(collectable); // ✅ Track objects for destruction
            }
        }

        // ✅ Wait for all collectables to finish their particle effects
        yield return new WaitForSeconds(2f);

        foreach (DrumLoopCollectable collectable in collectablesToDestroy)
        {
            if (collectable != null)
            {
                Debug.Log($"[DrumTrack] Destroying collectable: {collectable.name}");
                Destroy(collectable.gameObject);
            }
        }

        drumLoopCollectables.Clear();
        drumLoopCollectablesCollected = 0;
        Debug.Log("[DrumTrack] Drum Loop Collectables reset and cleared.");
    }

    public void RemoveActiveObstacle(GameObject obstacle)
    {
        activeObstacles.Remove(obstacle);

    }
    public void RemoveDrumLoopCollectable(DrumLoopCollectable collectable)
    {
        if (drumLoopCollectables.Contains(collectable))
        {
            Debug.Log($"[DrumTrack] Removing collectable from list: {collectable.name}");
            drumLoopCollectables.Remove(collectable);
        }
        else
        {
            Debug.LogWarning($"[DrumTrack] Tried to remove a collectable not in list: {collectable.name}");
        }
    }


    public float GetStarCollectionRatio()
    {
        return Mathf.Clamp01(drumLoopCollectablesCollected / 4f);
    }

    public int GetCollectedStarCount()
    {
        return drumLoopCollectablesCollected;
    }

    public float GetRemainingLoopTime()
    {
        float dspNow = (float)AudioSettings.dspTime;
        float loopEndDsp = startDspTime + (lastLoopCount + 1) * loopLengthInSeconds;
        return Mathf.Max(0, loopEndDsp - dspNow);
    }
    private void ExplodeAllObstacles()
    {
        // Handle Hazards
        Hazard[] hazards = FindObjectsByType<Hazard>(FindObjectsSortMode.None);
        for (int i = hazards.Length - 1; i >= 0; i--)
        {
            Hazard hazard = hazards[i];
            if (hazard != null)
            {
                Explode explode = hazard.GetComponent<Explode>();
                if (explode != null)
                {
                    explode.Permanent();
                }
                else
                {
                    Destroy(hazard.gameObject);
                }
            }
        }

        // Handle Obstacles
        var obstaclesCopy = activeObstacles.ToList();
        foreach (GameObject obstacle in obstaclesCopy)
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
        activeObstacles.Clear();
    }

    private void PushToNextPlane()
    {
        float dspNow = (float)AudioSettings.dspTime;
        float loopEndDsp = startDspTime + (lastLoopCount + 1) * loopLengthInSeconds;
        float timeRemaining = loopEndDsp - dspNow;
        wave.TriggerWave(Vector3.zero, timeRemaining);
        ScheduleDrumLoopChange(SelectDrumClip(DrumLoopPattern.Full));
    }

    private void ResetDrumLoopCollectables()
    {
        drumLoopCollectablesCollected = 0;
        for (int i = drumLoopCollectables.Count - 1; i >= 0; i--)
        {
            DrumLoopCollectable dlc = drumLoopCollectables[i];
            dlc.Remove();
        }
        drumLoopCollectables.Clear();
    }
    public int GetCurrentStep()
    {
        return currentStep;
    }

    public void LoopRoutines()
    {
        CleanupInvalidObstacles(); // ✅ Remove invalid references
        HandleFloatingObstacles(); // ✅ Separately process loose obstacles
        HandleEvolvingObstacles(); // ✅ Process grid-based obstacles
        Debug.Log("Spawning Obstacles...");
        SpawnObstacle();
        
/*
        obstacleSpawnTypeIndex++;
        if (obstacleSpawnTypeIndex >= obstaclePrefab.Length)
        {
            obstacleSpawnTypeIndex = 0;
        }
  */
    }


    private void HandleEvolvingObstacles()
    {
        foreach (var obstacle in activeObstacles.ToList()) // ✅ Safe iteration
        {
            Obstacle gridObstacle = obstacle.GetComponent<Obstacle>();

            if (gridObstacle != null && !gridObstacle.isLoose) // ✅ Ensure it's still in the grid
            {
                gridObstacle.Age(); // ✅ Trigger standard obstacle behavior
            }
        }
    }


    public void CollectedDrumLoop(DrumLoopCollectable collectable)
    {
        drumLoopCollectablesCollected++;
        drumLoopCollectables.Add(collectable);
        Debug.Log($"Collected {collectable.name} at {drumLoopCollectablesCollected} total collected");
        collectable.MoveToCorner(GetNextCornerPosition());
        // Send it to one of the 4 corners (0..3)
        // ✅ Update Star visuals dynamically
        foreach (var star in drumLoopCollectables)
        {
            star.UpdateStarAppearance();
        }

        if (drumLoopCollectablesCollected >= 4)
        {
            Debug.Log($"Start Index: {evolvingObstaclesSetIndex}");
            evolvingObstaclesSetIndex++;
            Debug.Log($"Advance Index: {evolvingObstaclesSetIndex}");

            if (evolvingObstaclesSetIndex >= evolvingObstaclesSets.Length)
            {
                evolvingObstaclesSetIndex = 0;
            }

            PushToNextPlane();
            Debug.Log($"Current: {evolvingObstaclesSetIndex}");

            StartCoroutine(MergeAllCollectablesIntoCenter());
        }
    }
    
    public void ScheduleDrumFillBeforeBeatChange()
    {
        float remainingTime = GetRemainingLoopTime();
    
        if (remainingTime > 0f)
        {
            Invoke(nameof(PlayDrumFill), Mathf.Max(remainingTime - SelectFillClipLength(), 0.01f));
        }
    }
    public AudioClip SelectDrumFillClip()
    {
        float remainingTime = GetRemainingLoopTime();
        AudioClip bestFitClip = null;
        float closestFit = float.MaxValue;

        foreach (AudioClip clip in drumFillClips) // ✅ Iterate over available fills
        {
            float difference = remainingTime - clip.length;

            if (difference >= 0 && difference < closestFit) // ✅ Best fit that doesn't exceed time
            {
                bestFitClip = clip;
                closestFit = difference;
            }
        }

        if (bestFitClip == null && drumFillClips.Length > 0)
        {
            // ✅ No perfect fit, pick the shortest available fill
            bestFitClip = drumFillClips.OrderBy(clip => clip.length).First();
        }

        return bestFitClip;
    }
    public float SelectFillClipLength()
    {
        AudioClip selectedClip = SelectDrumFillClip();
        return selectedClip != null ? selectedClip.length : 0f;
    }

    private void PlayDrumFill()
    {
        Debug.Log("Playing drum fill before beat change.");

        AudioClip fillClip = SelectDrumFillClip();
        float remainingTime = GetRemainingLoopTime();

        if (fillClip.length > remainingTime)
        {
            Debug.LogWarning("Drum fill is too long for remaining loop time! Adjusting...");
            fillClip = TrimAudioClip(fillClip, remainingTime); // ✅ Dynamically adjust the fill length
        }

        drumAudioSource.PlayOneShot(fillClip);
        Invoke(nameof(PushToNextPlane), fillClip.length);
    }
    private AudioClip TrimAudioClip(AudioClip originalClip, float maxLength)
    {
        if (originalClip.length <= maxLength) return originalClip; // ✅ No need to trim if it's already short enough

        int sampleCount = Mathf.FloorToInt(maxLength * originalClip.frequency);
        float[] samples = new float[sampleCount * originalClip.channels];

        originalClip.GetData(samples, 0);
        AudioClip trimmedClip = AudioClip.Create(originalClip.name + "_trimmed", sampleCount, originalClip.channels, originalClip.frequency, false);
        trimmedClip.SetData(samples, 0);

        return trimmedClip;
    }

    public void RemoveObstacleAt(Vector2Int gridPos)
    {
        GameObject obstacleToRemove = null;

        foreach (GameObject obstacle in activeObstacles)
        {
            if (WorldToGridPosition(obstacle.transform.position) == gridPos)
            {
                obstacleToRemove = obstacle;
                break; // ✅ Stop after finding the first matching obstacle
            }
        }

        if (obstacleToRemove != null)
        {
            Debug.Log($"Removing obstacle at grid {gridPos}");
            activeObstacles.Remove(obstacleToRemove);
            Destroy(obstacleToRemove);

            // ✅ Ensure the grid cell is freed after removal
            spawnGrid.FreeCell(gridPos.x, gridPos.y);
            Debug.Log($"Cell {gridPos} is now free.");
        }
        else
        {
            Debug.LogWarning($"No obstacle found at {gridPos} to remove.");
        }
    }

    private void SpawnObstacle()
    {
        Debug.Log($"Spawning obstacle at {WorldToGridPosition(transform.position)}");
        Vector2Int spawnCell = spawnGrid.GetRandomAvailableCell();
        if (spawnCell.x == -1)
        {
            Debug.LogWarning("No available spawn cell for obstacle!");
            return;
        }

        Vector3 spawnPosition = GridToWorldPosition(spawnCell);
        GameObject newObstacleParent = Instantiate(evolvingObstaclesSets[evolvingObstaclesSetIndex].GetEvolvingObstacle(), spawnPosition, Quaternion.identity);

        EvolvingObstacle evolvingObstacle = newObstacleParent.GetComponent<EvolvingObstacle>();

        if (evolvingObstacle != null)
        {
            evolvingObstacle.SetDrumTrack(this);
            evolvingObstacle.SpawnObstacle(spawnCell); // ✅ Create the initial Block Obstacle
            activeObstacles.Add(newObstacleParent);
            spawnGrid.OccupyCell(spawnCell.x, spawnCell.y, GridObjectType.Obstacle);
        }
    }
    
    private void HandleFloatingObstacles()
    {
        foreach (var obstacle in activeObstacles.ToList()) // ✅ Iterate safely
        {
            Obstacle floatingObstacle = obstacle.GetComponent<Obstacle>();

            if (floatingObstacle != null && floatingObstacle.isLoose)
            {
                floatingObstacle.Age(); // ✅ Ensure loose obstacles evolve on their own
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
            case DrumLoopPattern.Full:
                Debug.Log(("Choose a full pattern drum clip"));
                progressionIndex++;
                if (progressionIndex > fullLoopClips.Length - 1)
                {
                    progressionIndex = 0;
                }
                return fullLoopClips[progressionIndex];
            default:
                Debug.LogWarning("SelectDrumClip: No valid drum loop found for pattern " + pattern);
                return null;
        }
    }
    public Vector3 GridToWorldPosition(Vector2Int gridPos)
    {
        float cameraDistance = -Camera.main.transform.position.z;

        // Camera viewport bounds (bottom-left, top-right)
        Vector3 bottomLeft = Camera.main.ViewportToWorldPoint(new Vector3(0, 0, cameraDistance));
        Vector3 topRight = Camera.main.ViewportToWorldPoint(new Vector3(1, 1, cameraDistance));

        // Properly defined normalized positions
        float normalizedX = gridPos.x / (float)(spawnGrid.gridWidth - 1);
        float normalizedY = gridPos.y / (float)(spawnGrid.gridHeight - 1);

        // Clearly defined worldX and worldY using full viewport mapping
        float worldX = Mathf.Lerp(bottomLeft.x + gridPadding, topRight.x - gridPadding, normalizedX);
        float worldY = Mathf.Lerp(bottomLeft.y + gridPadding, topRight.y - gridPadding, normalizedY);

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
        // ✅ First, check if `drumAudioSource` is valid
        if (drumAudioSource == null || drumAudioSource.clip == null)
        {
            Debug.LogError("WaitAndChangeDrumLoop: drumAudioSource or its clip is null!");
            yield break;
        }

        // Wait until the current loop is about to finish.
        while (drumAudioSource.time < drumAudioSource.clip.length - 0.1f)
        {
            yield return null;
        }
        if (pendingDrumLoop == null)
        {
            Debug.LogWarning("WaitAndChangeDrumLoop: No new drum loop was assigned!");
            yield break;
        }
        // ✅ Change the drum loop to the pending one.
        drumAudioSource.clip = pendingDrumLoop;
        drumAudioSource.Play();
        pendingDrumLoop = null;
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
            startDspTime = (float)AudioSettings.dspTime;
            drumAudioSource.Play();

        Debug.Log($"Drum loop initialized with length {loopLengthInSeconds} seconds.");
        isInitialized = true;
        }

    void Start()
    {
        spawnGrid = GetComponent<SpawnGrid>();
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

    public void CauseRhythmGlitch()
    {
        Debug.Log("Triggering rhythm glitch!");
        drumAudioSource.pitch = 1.5f;
        Invoke(nameof(ResetDrumPitch), 1.5f);
    }
    private void CleanupExplodedObstacles()
    {
        for (int i = activeObstacles.Count - 1; i >= 0; i--)
        {
            GameObject obstacle = activeObstacles[i];

            if (obstacle == null) continue;

            Explode explode = obstacle.GetComponent<Explode>();
            if (explode != null)
            {
                Debug.Log($"Forcing removal of exploded obstacle at {obstacle.transform.position}");
                activeObstacles.RemoveAt(i);
                Destroy(obstacle);
            }
        }
    }

    public void CleanupInvalidObstacles()
    {
        Debug.Log("Checking for invalid obstacles...");

        for (int i = activeObstacles.Count - 1; i >= 0; i--)
        {
            GameObject obstacle = activeObstacles[i];

            if (obstacle == null)
            {
                Debug.Log($"Removing null obstacle reference at index {i}");
                activeObstacles.RemoveAt(i);
                continue;
            }

            if (obstacle.GetComponent<EvolvingObstacle>() == null)
            {
                Vector2Int gridPos = WorldToGridPosition(obstacle.transform.position);
                Debug.Log($"Removing non-evolving obstacle at {gridPos}");

                activeObstacles.RemoveAt(i);
                spawnGrid.FreeCell(gridPos.x, gridPos.y);
                Destroy(obstacle);
            }
        }
    }

    private void ResetDrumPitch()
    {
        drumAudioSource.pitch = 1.0f;
    }

}