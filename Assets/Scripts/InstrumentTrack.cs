using UnityEngine;
using MidiPlayerTK;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

public class InstrumentTrack : MonoBehaviour
{
    public InstrumentTrackController controller; // 🎛️ Reference to main controller
    public MidiStreamPlayer midiStreamPlayer; // Plays MIDI notes
    public NoteSet currentNoteSet;
    public DrumTrack drumTrack;
    public int channel;
    public int preset;
    public int bank;
    public GameObject collectablePrefab; // Prefab to spawn
    public Transform collectableParent; // Parent object for organization

    public int allowedDuration = -1;
    
    public float minCollectableScale = 1.0f;  // Scale for a 1/16th note.
    public float maxCollectableScale = 4.0f;  // Scale for a whole note.

    // Define the duration range (adjust as needed).
    //public int minDurationTicks = 4;    // 1/16th note duration.
    //public int maxDurationTicks = 64;   // Whole note duration.
    
    private List<int> allowedNotes = new List<int>();
    private List<int> allowedSteps = new List<int>();
    private List<(int stepIndex, int note, int duration, float velocity)> persistentLoopNotes = new List<(int, int, int, float)>();
    private int currentSectionStart = 0; // ✅ The starting step of the current section
    private int currentSectionEnd = 64; // ✅ The ending step (expands when a section is completed)    
    private float screenMinX = -8f; // Left boundary
    private float screenMaxX = 8f;  // Right boundary
    public List<GameObject> spawnedCollectables = new List<GameObject>(); // Track all spawned Collectables
    private Dictionary<Collectable, (int note, int duration)> collectableNotes = new Dictionary<Collectable, (int, int)>();
    private int totalSteps = 16;
    private int lastStep = -1;
    private int currentExpansionCount = 0;
    // Define constants for the musical grid resolution:
    private const int musicGridColumns = 64;
    private const int musicGridRows = 12;

// Define your world boundaries for the musical grid.
// You might choose these based on your scene design.
    private float musicMinX = -8f;
    private float musicMaxX = 8f;
    private float musicMinY = -4f;
    private float musicMaxY = 4f;
    private bool hasCollectedNewNoteThisSet = false;
    private Boundaries boundaries;
    // Define the smallest and largest scales for collectables.
     void Start()
    {
        if (controller == null)
        {
            Debug.LogError($"{gameObject.name} - No InstrumentTrackController assigned!");
            return;
        }

        if (drumTrack == null)
        {
            Debug.Log("No drumtrack assigned!");
            return;
        }

        if (drumTrack.spawnGrid == null)
        {
            Debug.LogError($"{gameObject.name} - ERROR: DrumTrack's spawnGrid is NULL! Waiting for initialization.");
            StartCoroutine(WaitForDrumTrackSpawnGrid());
            return;
        }

        Debug.Log($"{gameObject.name} - Successfully pulled SpawnGrid from {drumTrack.name}.");

    }
    Vector3 GetMusicNoteWorldPosition(int column, int row)
    {
        // normalized coordinates: 0 to 1 across the grid dimensions
        float normalizedX = (column + 0.5f) / (float)musicGridColumns;
        float normalizedY = (row + 0.5f) / (float)musicGridRows;

        // Viewport coordinates: (0,0) is bottom-left, (1,1) is top-right of the camera view.
        Vector3 viewportPoint = new Vector3(normalizedX, normalizedY, Camera.main.nearClipPlane);
        return Camera.main.ViewportToWorldPoint(viewportPoint);
    }
    public void ApplyNoteSet(NoteSet newNoteSet)
    {
        totalSteps = drumTrack.totalSteps;
        if (newNoteSet == null)
        {
            Debug.LogError("Assigned NoteSet is null!");
            return;
        }
        currentNoteSet = newNoteSet;
        currentNoteSet.BuildNotesFromKey();
        currentNoteSet.BuildAllowedStepsFromStyle(totalSteps);
        allowedSteps = new List<int>(currentNoteSet.GetStepList());
        allowedNotes = new List<int>(currentNoteSet.GetNoteList());
        Debug.Log($"{gameObject.name} - ApplyNoteSet: {allowedSteps.Count} allowed steps, {allowedNotes.Count} allowed notes.");

        if (allowedSteps.Count == 0 || allowedSteps == null)
        {
            Debug.LogError($"{gameObject.name} - ERROR: No allowed steps after ApplyNoteSet!");
        }
        // ✅ Adjust behavior based on NoteBehavior
        switch (newNoteSet.noteBehavior)
        {
            case NoteBehavior.Bass:
                allowedSteps = allowedSteps.Where(step => step % 8 == 0).ToList(); // Every 8th step
                break;
            case NoteBehavior.Lead:
                allowedSteps = allowedSteps.Where(step => step % 2 == 0).ToList(); // More frequent
                break;
            case NoteBehavior.Harmony:
                allowedSteps = allowedSteps
                    .Where(step => step % 4 == 0 || 
                                   RhythmPatterns.Patterns[newNoteSet.rhythmStyle].Offsets.Any(offset => (step + offset) % 16 == 0))
                    .ToList();
                break;

            case NoteBehavior.Percussion:
                allowedSteps = allowedSteps.Where(step => step % 16 == 0).ToList(); // Sparse, rhythmic
                break;
            case NoteBehavior.Drone:
                allowedSteps = allowedSteps.Where(step => RhythmPatterns.Patterns[newNoteSet.rhythmStyle].Offsets.Contains(step % 16)).Take(1).ToList();
                if (allowedSteps.Count == 0)
                    allowedSteps.Add(0); // Ensure at least one note spawns
                break;

        }

        hasCollectedNewNoteThisSet = false;
        currentExpansionCount = 0; // ✅ Reset expansions
        Debug.Log($"{gameObject.name} - Assigned new NoteSet with behavior {newNoteSet.noteBehavior}");
    }
    
    private List<int> GenerateWeightedList(Dictionary<int, int> weights)
    {
        List<int> weightedList = new List<int>();

        foreach (var pair in weights)
        {
            if (pair.Value <= 0)
            {
                Debug.LogError($"{gameObject.name} - ERROR: Key {pair.Key} has an invalid weight of {pair.Value}!");
                continue;
            }

            for (int i = 0; i < pair.Value; i++)
            {
                weightedList.Add(pair.Key);
            }
        }

        Debug.Log($"{gameObject.name} - Generated Weighted Key: {string.Join(", ", weightedList)}");

        return weightedList;
    }
    
    void Update()
    {
        if (drumTrack?.drumAudioSource == null) return;

        // ✅ Use DSP time for accurate synchronization
        float elapsedTime = drumTrack.drumAudioSource.time;
        float loopLength = drumTrack.loopLengthInSeconds * 2;

        // ✅ Prevent division by zero
        if (loopLength <= 0 || totalSteps <= 0) return;

        // Calculate step size based on the expanded loop
        float baseStepSize = loopLength / drumTrack.totalSteps;
        int globalStep = Mathf.FloorToInt(elapsedTime / baseStepSize) % drumTrack.totalSteps;

        if (globalStep != lastStep)
        {
            PlayLoopedNotes(globalStep);
            lastStep = globalStep;
        }

    }

    private void CollectNote(int note, int durationTicks, float force)
    {
        int stepIndex = GetCurrentStep();

        // ✅ Add the note to the persistent loop
        Debug.Log($"Adding {note} with {force} for {durationTicks} to step {stepIndex} ");
        persistentLoopNotes.Add((stepIndex, note, durationTicks, force));
        PlayNote(note, durationTicks, force);
    }
    
    private void ClearLoopNotes()
    {
        if (persistentLoopNotes.Count == 0) return;
        persistentLoopNotes.Clear();
        Debug.Log($"{gameObject.name} - Cleared all looping notes for new track pattern.");
    }
    public void SpawnCollectables()
    {
        if (currentNoteSet == null)
        {
            Debug.LogError("No current NoteSet assigned!");
            return;
        }
        if (allowedSteps == null || allowedSteps.Count == 0)
        {
            Debug.LogError($"{gameObject.name} - ERROR: No allowed steps for spawning.");
            return;
        }

        Debug.Log($"{gameObject.name} - SpawnCollectables(): {allowedSteps.Count} steps available."); 
        drumTrack.spawnGrid.PrintGridDebug();
        foreach (int stepIndex in allowedSteps)
        {
            int assignedNote = currentNoteSet.GetRandomNote();
            int chosenDurationTicks = CalculateNoteDuration(stepIndex, currentNoteSet);

            Vector2Int gridPos = GetGridPositionForStep(stepIndex, assignedNote, currentNoteSet);

            if (!drumTrack.spawnGrid.IsCellAvailable(gridPos.x, gridPos.y, currentNoteSet.noteBehavior))
            {
                Debug.Log($"Cell {gridPos} occupied, searching for alternative.");

                bool foundAlternate = false;
                for (int dy = -1; dy <= 1 && !foundAlternate; dy++)
                {
                    for (int dx = -1; dx <= 1 && !foundAlternate; dx++)
                    {
                        int altX = Mathf.Clamp(gridPos.x + dx, 0, drumTrack.spawnGrid.gridWidth - 1);
                        int altY = Mathf.Clamp(gridPos.y + dy, 0, drumTrack.spawnGrid.gridHeight - 1);
                        if (drumTrack.spawnGrid.IsCellAvailable(altX, altY, currentNoteSet.noteBehavior))
                        {
                            gridPos = new Vector2Int(altX, altY);
                            foundAlternate = true;
                            Debug.Log($"Found alternate cell at {gridPos}");
                        }
                    }
                }

                if (!foundAlternate)
                {
                    Debug.LogWarning("No available alternate cells found; skipping this note spawn.");
                    continue;
                }

                // proceed with spawning
            }
            Vector3 spawnPosition = drumTrack.GridToWorldPosition(gridPos);
            Debug.Log($"Spawning at {spawnPosition}");

            GameObject spawned = Instantiate(collectablePrefab, spawnPosition, Quaternion.identity, collectableParent);
            Debug.Log($"Step {stepIndex} → Column {spawnPosition.x}");
            Collectable collectable = spawned.GetComponent<Collectable>();

            if (collectable == null)
            {
                Debug.LogError("Spawned object missing Collectable component.");
                Destroy(spawned);
                continue;
            }

            collectable.Initialize(assignedNote, chosenDurationTicks, this, currentNoteSet.noteBehavior);
            collectable.OnCollected += (int duration, float force) => OnCollectableCollected(collectable, stepIndex, duration, force);
            collectable.OnDestroyed += () => OnCollectableDestroyed(collectable);

            drumTrack.spawnGrid.OccupyCell(gridPos.x, gridPos.y, GridObjectType.Note);
            spawnedCollectables.Add(collectable.gameObject);
        }
    }

    private int GetCurrentStep()
    {
        if (drumTrack?.drumAudioSource == null) return -1;

        // ✅ Use DSP time for precise timing
        float elapsedTime = (float)(AudioSettings.dspTime - drumTrack.startDspTime);
        float stepDuration = (drumTrack.loopLengthInSeconds * 2) / drumTrack.totalSteps;

        if (stepDuration <= 0)
        {
            Debug.LogError("InstrumentTrack: Step duration is invalid!");
            return -1;
        }

        int absoluteStep = Mathf.FloorToInt(elapsedTime / stepDuration);
        return absoluteStep % drumTrack.totalSteps;
    }

// Clearly translate from 0-63 steps into your gridWidth (e.g., 8)
    private Vector2Int GetGridPositionForStep(int stepIndex, int assignedNote, NoteSet noteSet)
    {
        int gridWidth = drumTrack.spawnGrid.gridWidth;
        int gridHeight = drumTrack.spawnGrid.gridHeight;

        // Clearly separate the step horizontally
        float normalizedX = (float)stepIndex / totalSteps;
        int uniqueIndex = allowedSteps.IndexOf(stepIndex);
        if (uniqueIndex == -1) uniqueIndex = 0; // Safety check

         int x = Mathf.FloorToInt((uniqueIndex / (float)allowedSteps.Count) * gridWidth);
//        x = Mathf.Clamp(x, 0, gridWidth - 1);

        
        
//        int x = Mathf.Clamp(Mathf.FloorToInt(normalizedX * gridWidth), 0, gridWidth - 1);
//        int x = stepIndex % gridWidth;
//        int uniqueIndex = allowedSteps.IndexOf(stepIndex); // Get the step's order in the sequence
//        int x = uniqueIndex % gridWidth;  // ✅ Assigns each note to a unique column

        // Map note pitch clearly vertically
        int y = DetermineRowForNote(assignedNote, noteSet.lowestNote, noteSet.highestNote, gridHeight);

        return new Vector2Int(x, y);
    }

    
    
    int DetermineRowForNote(int pitch, int lowestPitch, int highestPitch, int totalRows)
    {
        int totalNotes = highestPitch - lowestPitch + 1;

        // Map pitch explicitly to a row to guarantee uniqueness
        int pitchIndex = pitch - lowestPitch;
        float rowStep = (float)totalRows / Mathf.Max((highestPitch - lowestPitch + 1), 1);
        int row = Mathf.FloorToInt(pitchIndex * rowStep);

        return Mathf.Clamp(row, 0, totalRows - 1);
    }


    int CalculateNoteDuration(int stepIndex, NoteSet noteSet)
    {
        List<int> allowedSteps = noteSet.GetStepList();

        // Find the next allowed step greater than the current stepIndex.
        int nextStep = allowedSteps
            .Where(step => step > stepIndex)
            .DefaultIfEmpty(stepIndex + totalSteps) // Wrap around if no further step is found
            .First();

        // Calculate how many steps between the current and next step, looping around if necessary
        int stepsUntilNext = (nextStep - stepIndex + totalSteps) % totalSteps;
        if (stepsUntilNext == 0)
            stepsUntilNext = totalSteps; // Ensure a full loop duration if the next step wraps to itself.

        // Calculate the number of MIDI ticks per musical step.
        int ticksPerStep = Mathf.RoundToInt(480f / (totalSteps / 4f)); // 480 ticks per quarter note.

        // Base duration is steps multiplied by ticks per step.
        int baseDurationTicks = ticksPerStep * stepsUntilNext;

        // Retrieve the rhythm pattern for the current note set and apply duration multiplier.
        RhythmPattern pattern = RhythmPatterns.Patterns[noteSet.rhythmStyle];
        int chosenDurationTicks = Mathf.RoundToInt(baseDurationTicks * pattern.DurationMultiplier);

        // Enforce a minimum duration for audibility.
        chosenDurationTicks = Mathf.Max(chosenDurationTicks, ticksPerStep / 2);

        Debug.Log($"CalculateNoteDuration: step {stepIndex}, nextStep {nextStep}, stepsUntilNext {stepsUntilNext}, baseDurationTicks {baseDurationTicks}, finalDuration {chosenDurationTicks}");

        return chosenDurationTicks;
    }



    int GetNextStep(int currentStep, List<int> stepList)
    {
        foreach (int step in stepList)
        {
            if (step > currentStep) return step; // ✅ Return the next available step
        }
        return totalSteps; // ✅ If no further step, extend to the end of the loop
    }
    public void CheckSectionComplete()
    {
        if (currentNoteSet == null)
        {
            Debug.LogError($"{gameObject.name} - ERROR: No current NoteSet assigned!");
            return;
        }

        if (spawnedCollectables.Count > 0)
        {
            return; // ✅ Ensures no transition until all notes are collected
        }
        else
        {
            
            controller.TrackExpansionCompleted(this);            
        }

    }

    IEnumerator WaitForDrumTrackSpawnGrid()
    {
        while (drumTrack.spawnGrid == null)
        {
            Debug.Log($"{gameObject.name} - Waiting for DrumTrack to initialize spawnGrid...");
            yield return null; // ✅ Wait until it exists
        }
       
        Debug.Log($"{gameObject.name} - SpawnGrid assigned in DrumTrack!");
    }

    IEnumerator WaitForDrumLoopAndSpawn()
    {
        // Guard: if we've already spawned collectables, exit.
        // Wait until DrumTrack is fully initialized.
        yield return new WaitUntil(() => drumTrack.isInitialized);
        Debug.Log("Drum Track is valid");

        yield return new WaitUntil(() => allowedSteps != null && allowedSteps.Count > 0);
        Debug.Log($"{gameObject.name} - Drum loop initialized. Allowed Steps at spawn: {string.Join(", ", allowedSteps)}");
        SpawnCollectables();
        
    }

    public void RemoveCollectableNote(int index = -1)
    {
        if (index < 0)
        {
            if (spawnedCollectables.Count > 0)
            {
                Debug.Log("Removing Random Spawned Item");
                index = Random.Range(0, spawnedCollectables.Count);
                
            }
        }
        // Ensure the index is within bounds.
        if (index < 0 || index >= spawnedCollectables.Count)
        {
            Debug.LogWarning("RemoveCollectableNote: invalid index " + index);
            return;
        }
        
        GameObject noteToRemove = spawnedCollectables[index];

            // Remove from the spawned list.
            spawnedCollectables.RemoveAt(index);

            // If you are also tracking additional data in dictionaries (like collectableNotes), remove it there as well.
            Collectable collectableComponent = noteToRemove.GetComponentInChildren<Collectable>();
            if (collectableComponent != null && collectableNotes.ContainsKey(collectableComponent))
            {
                collectableNotes.Remove(collectableComponent);
            }

            // Finally, destroy the game object.
            if(noteToRemove.GetComponent<Explode>()) {
                noteToRemove.GetComponent<Explode>().Permanent();
            }
            else
            {
                Destroy(noteToRemove);

            }
    }

    public Vector2Int WorldToMusicGridPosition(Vector3 worldPos)
    {
        if (Camera.main == null)
        {
            Debug.LogError("Main camera not found!");
            return new Vector2Int(-1, -1);
        }

        float zDistance = -Camera.main.transform.position.z;

        Vector3 bottomLeft = Camera.main.ViewportToWorldPoint(new Vector3(0, 0, zDistance));
        Vector3 topRight = Camera.main.ViewportToWorldPoint(new Vector3(1, 1, zDistance));

        float normalizedX = Mathf.InverseLerp(bottomLeft.x, topRight.x, worldPos.x);
        float normalizedY = Mathf.InverseLerp(bottomLeft.y, topRight.y, worldPos.y);

        int x = Mathf.Clamp(Mathf.RoundToInt(normalizedX * (musicGridColumns - 1)), 0, musicGridColumns - 1);
        int y = Mathf.Clamp(Mathf.RoundToInt(normalizedY * (musicGridRows - 1)), 0, musicGridRows - 1);

        return new Vector2Int(x, y);
    }

    void OnCollectableDestroyed(Collectable collectable)
    {
        Vector2Int gridPos = drumTrack.WorldToGridPosition(collectable.transform.position);

        // ✅ Ensure grid position is freed
        Debug.Log($"Freeing cell {gridPos.x}, {gridPos.y}");
        drumTrack.spawnGrid.FreeCell(gridPos.x, gridPos.y);
        drumTrack.spawnGrid.ResetCellBehavior(gridPos.x, gridPos.y);
        spawnedCollectables.Remove(collectable.gameObject);
        CheckSectionComplete();
    }

    void OnCollectableCollected(Collectable collectable, int stepIndex, int durationTicks, float force)
    {
        
        if (collectable.assignedInstrumentTrack != this)
        {
            return;
        }

        Vector2Int gridPos = drumTrack.WorldToGridPosition(collectable.transform.position);
        drumTrack.spawnGrid.FreeCell(gridPos.x, gridPos.y);
        drumTrack.spawnGrid.ResetCellBehavior(gridPos.x, gridPos.y);
    
        if (!hasCollectedNewNoteThisSet)
        {
            Debug.Log("First note collected in new NoteSet. Resetting loop for " + gameObject.name);
            ClearLoopNotes();
            hasCollectedNewNoteThisSet = true;
        }

        // ✅ Store the collected note in the loop
        CollectNote(collectable.assignedNote, durationTicks, force);

        // ✅ Remove the collected note from the spawned list
        spawnedCollectables.Remove(collectable.gameObject);
        Destroy(collectable.gameObject);

    }

    public void ResetTrackGridBehavior()
    {
        if (currentNoteSet == null) return;
        hasCollectedNewNoteThisSet = false;
        Debug.Log($"{gameObject.name} - Reset grid behavior for completed track.");
    }


    void PlayLoopedNotes(int globalStep)
    {
        // Here, persistentLoopNotes stores the absolute step at which each note should play.
        foreach (var (storedStep, note, duration, velocity) in persistentLoopNotes)
        {

            if (storedStep == globalStep)
            {
                PlayNote(note, duration, velocity);

            }
        }
    }

    void PlayNote(int note, int durationTicks, float velocity)
    {
        if (drumTrack == null || drumTrack.drumLoopBPM <= 0)
        {
            Debug.LogError("Drum track is not initialized or has an invalid BPM.");
            return;
        }

        // ✅ Convert durationTicks into milliseconds using WAV BPM
        int durationMs = Mathf.RoundToInt(durationTicks * (60000f / (drumTrack.drumLoopBPM * 480f)));
        float loopDurationMs = (60000f / drumTrack.drumLoopBPM) * drumTrack.totalSteps;
        midiStreamPlayer.MPTK_Channels[channel].ForcedPreset = preset;
        midiStreamPlayer.MPTK_Channels[channel].ForcedBank = bank;
        MPTKEvent noteOn = new MPTKEvent()
        {
            Command = MPTKCommand.NoteOn,
            Value = note,
            Channel = channel,
            Duration = durationMs, // ✅ Fixed duration scaling
            Velocity = (int)velocity,
        };

        midiStreamPlayer.MPTK_PlayEvent(noteOn);
    }


}
