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
    public float loopLengthInSeconds;       // Duration of the loop.
    public SpawnGrid spawnGrid;
    public int gridWidth = 8;
    public int gridHeight = 8;
    public AudioClip[] fullLoopClips;
    public AudioClip[] breakbeatClips;
    public AudioClip[] slowDownClips;
    // Prefabs for different spawned objects:
    public GameObject obstaclePrefab;
    public int totalSteps = 64;
    // Define candidate spawn steps: if you want to spawn onlly on every 8th note, set:
    public AudioSource drumAudioSource;
    public InstrumentTrackController trackController;
    public List<GameObject> activeObstacles = new List<GameObject>(); // Track spawned obstacles
    public List<DrumLoopCollectable> drumLoopCollectables = new List<DrumLoopCollectable>();
    public float drumLoopBPM = 120f;
    private float obstacleInitialY = 0f;  // Starting Y position (e.g., bottom of the screen)
    public float startDspTime;
    public bool isInitialized = false;    
     
    private int progressionIndex = 0;
    private DrumLoopState currentDrumLoopState = DrumLoopState.Progression;
    // Tracking which candidate steps are already used in the current loop:

    private float screenMinX = -8f; // Left boundary
    private float screenMaxX = 8f;  // Right boundary
    private float stepWidth; // Dynamic width per step
    private float nextSpawnTime = 0f;  // ✅ Tracks when the next obstacles should spawn
    private AudioClip pendingDrumLoop = null;
    private int lastLoopCount = 0;
    private int currentStep = 0;
    public int loopsRequiredBeforeEvolve = 2; 
    private void Update()
    {
        if (drumAudioSource == null || drumAudioSource.clip == null)
            return;
        if (!GamepadManager.Instance.ReadyToPlay())
        {
            return;
        }

        float currentTime = drumAudioSource.time;
        float stepDuration = loopLengthInSeconds / totalSteps;

        if (stepDuration <= 0)
        {
            Debug.LogError("DrumTrack: Step duration is invalid!");
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
            LoopRoutines();
        }
    }

    public int GetCurrentStep()
    {
        return currentStep;
    }

    private void LoopRoutines()
    {
        activeObstacles.RemoveAll(obstacle => obstacle == null);
        SpawnObstacle();
        Debug.Log($"Last Loop Count {lastLoopCount} modulo {loopsRequiredBeforeEvolve}");
        if (lastLoopCount%loopsRequiredBeforeEvolve == 0)
        {
            for (int i = 0; i < activeObstacles.Count; i++)
            {
                EvolvingObstacle obstacleToEvolve = activeObstacles[i].GetComponent<EvolvingObstacle>();
                if (obstacleToEvolve != null)
                {
                    obstacleToEvolve.Evolve();
                    Debug.Log($"Evolving {obstacleToEvolve.name}");
                }
            }
        }

        if (drumLoopCollectables.Count > 0)
        {
            Debug.Log($"{drumLoopCollectables.Count} Drum Loop Collectables Found");
            if (drumLoopCollectables.Count == 1)
            {
                ScheduleDrumLoopChange(slowDownClips[Random.Range(0, slowDownClips.Length)]);                
            }
            else if (drumLoopCollectables.Count == 2)
            {
                ScheduleDrumLoopChange(SelectDrumClip(DrumLoopPattern.Full));
            }
            else if (drumLoopCollectables.Count == 3)
            {
                ScheduleDrumLoopChange(SelectDrumClip(DrumLoopPattern.Breakbeat));
            }

            for (int i = drumLoopCollectables.Count - 1; i > 0; i--)
            {
                DrumLoopCollectable dlc = drumLoopCollectables[i];
                dlc.Remove();
            }
            drumLoopCollectables.Clear();
        }

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
        // ✅ Determine NoteBehavior from the current NoteSet (if active)
        NoteSet activeNoteSet = trackController?.GetCurrentNoteSet();
        NoteBehavior behavior = activeNoteSet != null ? activeNoteSet.noteBehavior : NoteBehavior.Percussion; // Default to Percussion if no active NoteSet

        // ✅ Request an available cell using the determined behavior
        Vector2Int spawnCell = spawnGrid.GetRandomAvailableCell(behavior);

        if (spawnCell.x == -1) 
        {
            Debug.LogWarning("No available spawn cell for obstacle!");
            return;
        }

        Vector3 spawnPosition = GridToWorldPosition(spawnCell);

        // ✅ Determine obstacle type based on `NoteBehavior` or `DrumLoopPattern`
        //ObstacleType obstacleType = GetObstacleType();

        // ✅ Instantiate the correct obstacle
        GameObject newObstacle = Instantiate(obstaclePrefab, spawnPosition, Quaternion.identity);
        EvolvingObstacle obstacle = newObstacle.GetComponent<EvolvingObstacle>();
        if (obstacle != null)
        {
            obstacle.SetDrumTrack(this);
        }
        if (newObstacle == null)
        {
            Debug.LogError("Failed to spawn obstacle! Check GetObstacle method.");
            return;
        }

        // ✅ Store obstacle so it can be removed later
        activeObstacles.Add(newObstacle);

        spawnGrid.OccupyCell(spawnCell.x, spawnCell.y, GridObjectType.Obstacle);
        Debug.Log($"Spawned Standard Obstacle at {spawnPosition}");
    }

    public Vector2Int WorldToGridPosition(Vector3 worldPos)
    {
        // ✅ Convert world position (-8 to 8) back to grid position (0 - gridWidth)
        int gridX = Mathf.Clamp(Mathf.RoundToInt((worldPos.x + 8f) / 16f * (gridWidth - 1)), 0, gridWidth - 1);
        int gridY = Mathf.Clamp(Mathf.RoundToInt((worldPos.y + 4f) / 8f * (gridHeight - 1)), 0, gridHeight - 1);

        return new Vector2Int(gridX, gridY);
    }

    public void ScheduleDrumLoopChange(AudioClip newLoop)
    {
        // Store the new loop clip.
        pendingDrumLoop = newLoop;
        // Start waiting for the current loop to finish.
        StartCoroutine(WaitAndChangeDrumLoop());
    }

    public AudioClip SelectDrumClip(DrumLoopPattern pattern)
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
            case DrumLoopPattern.Breakbeat:
                Debug.Log(("Choose a breakbeat pattern drum clip"));
                return breakbeatClips.Length > 0 ? breakbeatClips[Random.Range(0, breakbeatClips.Length)] : null;
            case DrumLoopPattern.SlowDown:
                Debug.Log(("Choose a slow pattern drum clip"));
                return slowDownClips.Length > 0 ? slowDownClips[Random.Range(0, slowDownClips.Length)] : null;
            default:
                Debug.LogWarning("SelectDrumClip: No valid drum loop found for pattern " + pattern);
                return null;
        }
    }

    public Vector3 GridToWorldPosition(Vector2Int gridPos)
    {
        // ✅ Convert grid position (0 - gridWidth) to world position (-8 to 8)
        float worldX = Mathf.Lerp(-8f, 8f, (float)gridPos.x / (gridWidth - 1));
        float worldY = Mathf.Lerp(-4f, 4f, (float)gridPos.y / (gridHeight - 1));

        Vector3 position = new Vector3(worldX, worldY, 0);
        return position;
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
            drumAudioSource.Play();
            startDspTime = (float)AudioSettings.dspTime;
            obstacleInitialY = screenMinX + obstacleInitialY;

        Debug.Log($"Drum loop initialized with length {loopLengthInSeconds} seconds.");
        isInitialized = true;
        }

    void Start()
    {
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
    public void ApplyPercussionLayer()
    {
        Debug.Log("Adding percussion layer...");
        // Apply subtle rhythm changes (e.g., add hi-hats, soft snares)
    }

    public void ModifySyncopation()
    {
        Debug.Log("Modifying syncopation...");
        // Adjust drum pattern for more groove
    }

    public void TriggerBreakbeat()
    {
        Debug.Log("Activating breakbeat transition!");
        ScheduleDrumLoopChange(breakbeatClips[Random.Range(0, breakbeatClips.Length)]);
    }

    public void CauseRhythmGlitch()
    {
        Debug.Log("Triggering rhythm glitch!");
        drumAudioSource.pitch = 1.5f;
        Invoke(nameof(ResetDrumPitch), 1.5f);
    }

    private void ResetDrumPitch()
    {
        drumAudioSource.pitch = 1.0f;
    }
/*
    IEnumerator EaseToPosition(Transform obj, Vector3 target, float moveSpeed)
    {
        if (obj == null)
        {
            yield break;
        }
        Vector3 start = obj.position;
        float distance = Vector3.Distance(start, target);
        // Duration is distance divided by speed (seconds = units / (units/second))
        float duration = distance / moveSpeed;
        float elapsed = 0f;
        while (elapsed < duration)
        {
            if (obj == null)
            {
                yield break;
            }
            elapsed += Time.deltaTime;
            float t = Mathf.SmoothStep(0f, 1f, elapsed / duration);
            obj.position = Vector3.Lerp(start, target, t);
            yield return null;
        }
        if (obj != null)
        {
            obj.position = target;
        }
    }
*/
}