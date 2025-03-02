using UnityEngine;
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
    public GameObject drumLoopCollectablePrefab;
    public SpawnGrid spawnGrid;
    public int gridWidth = 8;
    public int gridHeight = 4;
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
    private float nextSpawnTime = 0f;  // ✅ Tracks when the next obstacles should spawn
    public float spawnCooldown = 3f;  // ✅ Delay before spawning again (increase for slower spawns)


    public AudioSource drumAudioSource;
    private AudioClip pendingDrumLoop = null;
    public InstrumentTrackController trackController;
    public int numVisibleObstacles = 2; // Number of obstacles that should be visible at a time
    private int lastLoopCount = -1;
    public List<GameObject> activeObstacles = new List<GameObject>(); // Track spawned obstacles
    private int progressionIndex = 0;
    private DrumLoopState currentDrumLoopState = DrumLoopState.Progression;
    public bool isInitialized = false;
    public float spawnDelay = 5f;
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
        int currentStep = absoluteStep % totalSteps;

        // ✅ Use DSP time to track loops properly
        float elapsedTime = (float)(AudioSettings.dspTime - startDspTime);
        int currentLoop = Mathf.FloorToInt(elapsedTime / loopLengthInSeconds);

        // ✅ Ensure this only runs once per loop restart
        if (currentLoop > lastLoopCount)
        {
            Debug.Log($"New Loop Started! Current Loop: {currentLoop}");

            lastLoopCount = currentLoop;

            // ✅ Alternate logic for obstacles and collectables
            if (currentLoop % 2 == 0)
            {
                Debug.Log("Spawning an obstacle.");
                SpawnObstacle();
            }
            else
            {
                Debug.Log("Spawning a drum loop collectable.");
                SpawnDrumLoopCollectable(DrumLoopPattern.Full);
            }
        }
    }

    private bool DrumLoopRestarted()
    {
        float elapsedTime = (float)(AudioSettings.dspTime - startDspTime);
        int currentLoop = Mathf.FloorToInt(elapsedTime / loopLengthInSeconds);

        if (currentLoop > lastLoopCount)
        {
            lastLoopCount = currentLoop;
            return true;
        }
        return false;
    }


    private void SpawnObstacle()
    {
        Vector2Int spawnCell = spawnGrid.GetRandomAvailableCell();

        if (spawnCell.x == -1) return; // No available space

        Vector3 spawnPosition = GridToWorldPosition(spawnCell);
        GameObject newObstacle = GetObstacle(ObstacleType.Standard);
        spawnGrid.OccupyCell(spawnCell.x, spawnCell.y, GridObjectType.Obstacle);

    }


    GameObject GetObstacle(ObstacleType _obstacleType)
    {
        switch (_obstacleType)
        {
            case ObstacleType.Hazard:
                GameObject obj = Instantiate(hazardPrefab, transform.position, Quaternion.identity);
                return obj;

            case ObstacleType.Standard:
                GameObject objB = Instantiate(obstaclePrefab, transform.position, Quaternion.identity);
                return objB;
            case ObstacleType.Void:
                GameObject objC = Instantiate(energyVoidPrefab, transform.position, Quaternion.identity);
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
    Vector2Int spawnCell = spawnGrid.GetRandomAvailableCell();

    if (spawnCell.x == -1) return; // No available space

    Vector3 spawnPosition = GridToWorldPosition(spawnCell);
    GameObject newCollectable = Instantiate(drumLoopCollectablePrefab, spawnPosition, Quaternion.identity);
    spawnGrid.OccupyCell(spawnCell.x, spawnCell.y, GridObjectType.DrumCollectable);
    DrumLoopCollectable dlc = newCollectable.GetComponent<DrumLoopCollectable>();

    if (dlc != null)
    {
        AudioClip selectedClip = null;

        if (patternToUse == DrumLoopPattern.Full)
        {
            Debug.Log(("CURRENT DRUM LOOP STATE: " + currentDrumLoopState));
            if (currentDrumLoopState == DrumLoopState.Progression)
            {
                if (fullLoopClips.Length > 0)
                {
                    Debug.Log(("FULL LENGTH CLIPS AVAILABLE"));
                    // ✅ Ensure progressionIndex does not exceed array bounds
                    if (progressionIndex < fullLoopClips.Length - 1)
                    {
                        Debug.Log("Progression Index: " + progressionIndex);
                        progressionIndex++;
                    }
                    else
                    {
                        progressionIndex = fullLoopClips.Length - 1; // ✅ Stay within bounds
                        Debug.Log("Breakbeat Progression Index: " + progressionIndex);
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

                // ✅ Prevent invalid range selection
                if (fullLoopClips.Length >= 4)
                {
                    progressionIndex = Random.Range(fullLoopClips.Length - 4, fullLoopClips.Length);
                }
                else
                {
                    progressionIndex = Random.Range(0, fullLoopClips.Length); // ✅ Adjust for smaller arrays
                }

                currentDrumLoopState = DrumLoopState.Progression;
            }
        }
        else if (patternToUse == DrumLoopPattern.SlowDown)
        {
            if (slowDownClips.Length > 0)
                selectedClip = slowDownClips[Random.Range(0, slowDownClips.Length)];
        }

        if (selectedClip == null)
        {
            Debug.LogWarning("SpawnLoopDrumCollectable no valid clip found for pattern " + patternToUse);
            if (fullLoopClips.Length > 0)
            {
                selectedClip = fullLoopClips[0];
            }
        }
        dlc.newDrumLoopClip = selectedClip;
        dlc.SetTrack(this);
    }
}
private Vector3 GridToWorldPosition(Vector2Int gridPos)
{
    float worldX = gridPos.x * 2 - (gridWidth / 2);
    float worldY = gridPos.y * 2 - (gridHeight / 2);
    return new Vector3(worldX, worldY, 0);
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

    void Awake() // ✅ Initialize early
    {
        if (spawnGrid == null)
        {
            spawnGrid = new SpawnGrid(gridWidth, gridHeight);
            Debug.Log($"{gameObject.name} - SpawnGrid initialized in Awake().");
        }
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