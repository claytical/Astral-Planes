﻿using System;
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
public enum NodeType
{
    Standard
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

    private float loopLengthInSeconds;       // Duration of the loop.
    private SpawnGrid spawnGrid;
    private List<GameObject> activeNodes = new List<GameObject>(); // Track spawned nodes
    private List<DrumLoopCollectable> drumLoopCollectables = new List<DrumLoopCollectable>();
    private List<MineNode> mineNodes = new List<MineNode>();
    private List<MinedObject> activeMinedObjects = new List<MinedObject>();

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
            CleanupExplodedMineNodes();
            LoopRoutines();
        }

        if (drumLoopCollectablesCollected >= 4)
        {
            progressionManager.EvaluateProgression();

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
        foreach (DrumLoopCollectable collectable in drumLoopCollectables.ToList())
        {

            if (collectable == null) continue;
            Explode explode = collectable.GetComponent<Explode>();
            if (explode != null) explode.DelayedExplosion(Random.Range(.2f,.5f));

            Destroy(collectable.gameObject);
        }
        drumLoopCollectables.Clear();
        drumLoopCollectablesCollected = 0;

        // Reset grid
        spawnGrid?.ClearAll();
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

    private Vector3 GetNextCornerPosition()
    {
        int index = drumLoopCollectablesCollected - 1; // ✅ Assign to next available corner
        if (index >= cornerPositions.Length) 
        {
            index = (index % cornerPositions.Length); // ✅ Wrap around if more than 4 Stars
        }
        return cornerPositions[index];
    }

    private IEnumerator MergeAllCollectablesIntoCenter()
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

    public void RemoveActiveMineNode(GameObject node)
    {
        activeNodes.Remove(node);

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
    private void ExplodeAllMineNodes()
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
        float dspNow = (float)AudioSettings.dspTime;
        float loopEndDsp = startDspTime + (lastLoopCount + 1) * loopLengthInSeconds;
        float timeRemaining = loopEndDsp - dspNow;
        wave.TriggerWave(Vector3.zero, timeRemaining);
        ScheduleDrumLoopChange(SelectDrumClip(DrumLoopPattern.Full));
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
        Debug.Log("Spawning Nodes...");
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
/*
            mineNodeSetIndex++;
            if (mineNodeSetIndex >= mineNodeSpawnerSets.Length)
            {
                mineNodeSetIndex = 0;
            }
*/
            PushToNextPlane();
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
            Debug.Log($"Removing obstacle at grid {gridPos}");
            activeNodes.Remove(nodeToRemove);
            Destroy(nodeToRemove);

            // ✅ Ensure the grid cell is freed after removal
            spawnGrid.FreeCell(gridPos.x, gridPos.y);
            Debug.Log($"Cell {gridPos} is now free.");
        }
        else
        {
            Debug.LogWarning($"No obstacle found at {gridPos} to remove.");
        }
    }

    private void SpawnMineNode()
    {
        Debug.Log($"Spawning mine node at {WorldToGridPosition(transform.position)}");
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
            spawnGrid.OccupyCell(spawnCell.x, spawnCell.y, GridObjectType.Node);
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
                Debug.Log($"Forcing removal of exploded obstacle at {node.transform.position}");
                activeNodes.RemoveAt(i);
                Destroy(node);
            }
        }
    }

    public void CleanupInvalidMineNodes()
    {
        Debug.Log("Checking for invalid obstacles...");

        for (int i = activeNodes.Count - 1; i >= 0; i--)
        {
            GameObject node = activeNodes[i];

            if (node == null)
            {
                Debug.Log($"Removing null obstacle reference at index {i}");
                activeNodes.RemoveAt(i);
                continue;
            }

            if (node.GetComponent<MineNodeSpawner>() == null)
            {
                Vector2Int gridPos = WorldToGridPosition(node.transform.position);
                Debug.Log($"Removing mine node spawner at {gridPos}");

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