using UnityEngine;
using MidiPlayerTK;
using System.Collections.Generic;
using System.Collections;

public enum DrumLoopPattern
{
    Full,      // Full build-up from 0 to n
    Breakbeat,    // A section that jumps into the later part of the full loop (e.g., n - 3 to n)
    SlowDown   // A special short pattern for slowing down (e.g., perhaps n - 2 to n)
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
    public float beatMultiplier = 8;
    public MidiFilePlayer drums;
    public GameObject drumLoopCollectablePrefab;
    public AudioClip[] fullLoopClips;
    public AudioClip[] breakbeatClips;
    public AudioClip[] slowDownClips;
    // Prefabs for different spawned objects:
    public GameObject obstaclePrefab;
    public GameObject energyVoidPrefab;
    public GameObject hazardPrefab;
    public int totalSteps = 64;
    // Difficulty values (0 = never, 1 = spawn on every candidate step)
    [Range(0f, 1f)]
    public float obstacleDifficulty = 0.5f;
    [Range(0f, 1f)]
    public float energyVoidDifficulty = 0.3f;
    [Range(0f, 1f)]
    public float hazardDifficulty = 0.2f;
    public float xOffset = 1;
    public float obstacleMoveDelay = 1f; // ✅ Time between each obstacle move (0 = all move at once)

    // Define candidate spawn steps: if you want to spawn onlly on every 8th note, set:
    private float obstacleInitialY = 0f;  // Starting Y position (e.g., bottom of the screen)
    public float obstacleTargetY = -8f;   // Target Y position on spawn (between bottom and mid-screen)
    public float easingSpeed = 1f; // Units per second
    public float drumLoopBPM = 120f;
    public float startDspTime;
    public float beatMoveSpeed = 5f; // ✅ Speed at which beats move down


    // Tracking which candidate steps are already used in the current loop:

    private float screenMinX = -8f; // Left boundary
    private float screenMaxX = 8f;  // Right boundary
    private float stepWidth; // Dynamic width per step
    public int loopCounter = 0;
    private float nextSpawnTime = 0f;  // ✅ Tracks when the next obstacles should spawn
    public float spawnCooldown = 3f;  // ✅ Delay before spawning again (increase for slower spawns)


    public AudioSource drumAudioSource;
    private AudioClip pendingDrumLoop = null;
    public InstrumentTrackController trackController;
    public int numVisibleObstacles = 2; // Number of obstacles that should be visible at a time
    private int lastLoopCount = -1;
    private int spawnCycleOffset = 0;
    private int lastSpawnStep = -1;
    private HashSet<int> occupiedSpawnSteps = new HashSet<int>();
    public List<GameObject> activeObstacles = new List<GameObject>(); // Track spawned obstacles
    private int progressionIndex = 0;
    private DrumLoopState currentDrumLoopState = DrumLoopState.Progression;
    public bool isInitialized = false;
    IEnumerator FollowMovementPattern(Transform obj, List<Vector3> pattern, float segmentDuration)
    {
        if (obj == null) yield break;

        // Loop through each movement step in the pattern.
        foreach (Vector3 offset in pattern)
        {
            Vector3 start = obj.position;
            Vector3 target = start + offset;
            float elapsed = 0f;

            while (elapsed < segmentDuration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.SmoothStep(0f, 1f, elapsed / segmentDuration);
                obj.position = Vector3.Lerp(start, target, t);
                yield return null;
            }

            // Ensure the object exactly reaches the target.
            obj.position = target;
        }
    }
    
    private void Update()
    {
        if (drumAudioSource == null || drumAudioSource.clip == null)
            return;

        float currentTime = drumAudioSource.time;
        float stepDuration = loopLengthInSeconds / totalSteps;

        if (stepDuration <= 0)
        {
            Debug.LogError("DrumTrack: Step duration is invalid!");
            return;
        }

        int absoluteStep = Mathf.FloorToInt(currentTime / stepDuration);
        int currentStep = absoluteStep % totalSteps;

        // ✅ Use DSP time to track loops properly
        float elapsedTime = (float)(AudioSettings.dspTime - startDspTime);
        int currentLoop = Mathf.FloorToInt(elapsedTime / loopLengthInSeconds);

        // ✅ If loop count increases, spawn obstacles
        if (currentLoop > lastLoopCount && Time.time >= nextSpawnTime)
        {
            Debug.Log("New Loop Started! Spawning obstacles.");
            spawnCycleOffset = (spawnCycleOffset + 1) % 16;
            lastLoopCount = currentLoop;
            occupiedSpawnSteps.Clear();
            SpawnObstacles(numVisibleObstacles);
            CheckForOverlappingObstacles();
        }
    }


    private void SpawnObstacles(int count)
    {
        if (count <= 0 || count > 16) return;

        RemoveOffscreenObstacles(); // Ensure old obstacles are cleared

        float screenWidth = screenMaxX - screenMinX;
        float stepWidth = screenWidth / 16f; // Full spacing if 16 obstacles
        float spacingFactor = screenWidth / (float)count; // Ensure even spacing

        int gapPosition = spawnCycleOffset % 16; // Shift spawn position each loop

        List<int> spawnedPositions = new List<int>();

        for (int i = 0; i < count; i++)  // ✅ Spawn only `count` obstacles
        {
            int spawnIndex = (i + gapPosition) % 16; // Cycle spawn positions

            if (spawnedPositions.Contains(spawnIndex)) continue; // Avoid duplicate positions
            spawnedPositions.Add(spawnIndex);

            float posX;
            if (count == 16) 
            {
                posX = screenMinX + (spawnIndex * stepWidth) + xOffset; // Standard spacing for full 16
            } 
            else 
            {
                posX = screenMinX + (i * spacingFactor) + xOffset; // ✅ Evenly spread out fewer obstacles
            }

            SpawnObstacle(posX); // ✅ Call the improved method
        }
    }

    private void RemoveOffscreenObstacles()
    {
        float screenTopY = Camera.main.ViewportToWorldPoint(new Vector3(0.5f, 1.1f, 0)).y; // Ensure a bit of buffer

        for (int i = activeObstacles.Count - 1; i >= 0; i--)
        {
            GameObject obj = activeObstacles[i];
            if (obj == null || obj.transform.position.y > screenTopY)
            {
                Destroy(obj);
                activeObstacles.RemoveAt(i);
            }
        }
        // ✅ If all obstacles are gone, force a new spawn on next loop restart
        if (activeObstacles.Count == 0)
        {
            Debug.Log("All obstacles removed. Waiting for next loop to spawn new ones.");
        }
    }
    private void CheckForOverlappingObstacles()
    {
        float checkSize = 1f; // ✅ Adjust based on obstacle size

        List<GameObject> toDestroy = new List<GameObject>();

        for (int i = 0; i < activeObstacles.Count; i++)
        {
            GameObject obstacleA = activeObstacles[i];
            if (obstacleA == null) continue;

            Collider2D colA = obstacleA.GetComponent<Collider2D>();
            if (colA == null) continue;

            for (int j = i + 1; j < activeObstacles.Count; j++)
            {
                GameObject obstacleB = activeObstacles[j];
                if (obstacleB == null) continue;

                Collider2D colB = obstacleB.GetComponent<Collider2D>();
                if (colB == null) continue;

                if (colA.bounds.Intersects(colB.bounds)) // ✅ Check if two obstacles overlap
                {
                    Debug.Log($"{obstacleA.name} is overlapping with {obstacleB.name}!");

                    // ✅ Randomly choose one to destroy
                    GameObject toExplode = (Random.value > 0.5f) ? obstacleA : obstacleB;

                    Explode explodeScript = toExplode.GetComponent<Explode>();
                    if (explodeScript != null)
                    {
                        toDestroy.Add(toExplode);
                    }
                }
            }
        }

        // ✅ Destroy the chosen obstacles after checking all overlaps
        foreach (GameObject obj in toDestroy)
        {
            activeObstacles.Remove(obj);
            obj.GetComponent<Explode>().Permanent();
        }
    }

    private void SpawnObstacle(float posX)
    {
        Vector3 spawnPos = new Vector3(posX, obstacleInitialY, 0);
        ObstacleType selectedType = ObstacleType.Standard;
        if(Random.Range(-1,2) > 0)
        {
            selectedType = ObstacleType.Hazard;
        }
        GameObject obj = GetObstacle(selectedType, spawnPos);
        if (obj != null)
        {
            ObstacleMovement obstacleMovement = obj.GetComponent<ObstacleMovement>();
            if (obstacleMovement != null)
            {
                obstacleMovement.Init(spawnPos); // Use the new Init method
                obstacleMovement.SetDrumTrack(this);
                obstacleMovement.beatInterval = (60f / drumLoopBPM) * beatMultiplier; // ✅ Ensure correct timing
                activeObstacles.Add(obj); // ✅ Add to list for tracking            }
            }
        }
    }

    public void RemoveOccupiedStep(int candidateStep)
    {
        if (occupiedSpawnSteps.Contains(candidateStep))
        {
            occupiedSpawnSteps.Remove(candidateStep);
            Debug.Log("DrumTrack: Freed candidate step " + candidateStep);
        }
    }

    void OnDrumLoopComplete()
    {
        // Clear old obstacles before spawning new ones
        foreach (var obj in activeObstacles)
        {
            if (obj != null) Destroy(obj);
        }

        activeObstacles.Clear();
    }
    
    public void RemoveRandomNote()
    {
        trackController.RemoveCollectableFromActiveTrack();
    }
    GameObject GetObstacle(ObstacleType _obstacleType, Vector3 position)
    {
        switch (_obstacleType)
        {
            case ObstacleType.Hazard:
                GameObject obj = Instantiate(hazardPrefab, position, Quaternion.identity);
                return obj;

            case ObstacleType.Standard:
                GameObject objB = Instantiate(obstaclePrefab, position, Quaternion.identity);
                return objB;
            case ObstacleType.Void:
                GameObject objC = Instantiate(energyVoidPrefab, position, Quaternion.identity);
                return objC;
            default:
                return null;
        }
    }


    public void ScheduleDrumLoopChange(AudioClip newLoop)
    {
        // Store the new loop clip.
        pendingDrumLoop = newLoop;
        // Start waiting for the current loop to finish.
        StartCoroutine(WaitAndChangeDrumLoop());
    }
    public void SpawnDrumLoopCollectable(DrumLoopPattern patternToUse)
    {
        Vector2 drumTrackPosition = new Vector2(3, 2);

        GameObject newCollectable = Instantiate(drumLoopCollectablePrefab, transform);
        newCollectable.transform.position = drumTrackPosition;
        
        Debug.Log("NEW COLLECTABLE:" + newCollectable.name);
        DrumLoopCollectable dlc = newCollectable.GetComponent<DrumLoopCollectable>();
        if (dlc != null)
        {
            AudioClip selectedClip = null;
            if (patternToUse == DrumLoopPattern.Full)
            {
                if(currentDrumLoopState == DrumLoopState.Progression)
                {
                    // Use the next clip in the progression sequence
                    if(fullLoopClips.Length > 0 && progressionIndex < fullLoopClips.Length)
                    {
                        progressionIndex++;
                        // If we've reached the end, switch to breakbeat next time
                        if(progressionIndex >= fullLoopClips.Length)
                        {
                            currentDrumLoopState = DrumLoopState.Breakbeat;
                        }
                        selectedClip = fullLoopClips[progressionIndex];

                    }
                }
            }
            else if (patternToUse == DrumLoopPattern.Breakbeat)
            {
                if (breakbeatClips.Length > 0)
                {
                    selectedClip = breakbeatClips[Random.Range(0, breakbeatClips.Length)];
                    int newProgressionIndex = Random.Range(fullLoopClips.Length - 4, fullLoopClips.Length);
                    if (newProgressionIndex < 0)
                    {
                        newProgressionIndex = 0;
                    }
                    progressionIndex = newProgressionIndex;
                    currentDrumLoopState = DrumLoopState.Progression;                
                }
            }
            else if (patternToUse == DrumLoopPattern.SlowDown)
            {
                if (slowDownClips.Length > 0)
                    selectedClip = slowDownClips[Random.Range(0, slowDownClips.Length)];
            }
            
            dlc.newDrumLoopClip = selectedClip;
            dlc.SetTrack(this);
        }
    }

    private IEnumerator WaitAndChangeDrumLoop()
    {
        // Wait until the current loop is about to finish.
        while (drumAudioSource.time < drumAudioSource.clip.length - 0.1f)
        {
            yield return null;
        }

        // ✅ Change the drum loop to the pending one.
        drumAudioSource.clip = pendingDrumLoop;
        drumAudioSource.Play();
        pendingDrumLoop = null;

        // ✅ Get the currently active `NoteSet` to find which `InstrumentTrack` is playing.
        if (trackController != null)
        {
            NoteSet currentNoteSet = trackController.GetCurrentNoteSet();
            if (currentNoteSet != null && currentNoteSet.assignedInstrumentTrack != null)
            {
                Debug.Log($"Drum loop changed! Advancing from {currentNoteSet.assignedInstrumentTrack.name} to next NoteSet.");
                trackController.TrackExpansionCompleted(currentNoteSet.assignedInstrumentTrack);
            }
            else
            {
                Debug.LogWarning("WaitAndChangeDrumLoop: No valid InstrumentTrack found for the current NoteSet.");
            }
        }
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
            if (drumAudioSource == null)
            {
                Debug.LogError("DrumTrack: No AudioSource assigned!");
                return;
            }
        GamepadManager.Instance.drumTrack = this;
        StartCoroutine(InitializeDrumLoop()); // ✅ Ensure it loads properly
        }
    
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

}