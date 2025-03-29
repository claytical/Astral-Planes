using UnityEngine;
using MidiPlayerTK;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

public class InstrumentTrack : MonoBehaviour
{
    public Color trackColor;
    public GameObject collectablePrefab; // Prefab to spawn
    public Transform collectableParent; // Parent object for organization

    public int lowestAllowedNote = 36; // 🎵 Lowest MIDI note allowed for this track
    public int highestAllowedNote = 84; // 🎵 Highest MIDI note
    public InstrumentTrackController controller; // 🎛️ Reference to main controller
    public MidiStreamPlayer midiStreamPlayer; // Plays MIDI notes
    private NoteSet currentNoteSet;
    public DrumTrack drumTrack;
    public int channel;
    public int preset;
    public int bank;
    public int loopMultiplier = 1;
    public int maxLoopMultiplier = 4;
    private List<int> allowedNotes = new List<int>();
    private List<int> allowedSteps = new List<int>();
    private List<(int stepIndex, int note, int duration, float velocity)> persistentLoopNotes = new List<(int, int, int, float)>();
    private int currentSectionStart = 0; // ✅ The starting step of the current section
    private int currentSectionEnd = 64; // ✅ The ending step (expands when a section is completed)    
    private float screenMinX = -8f; // Left boundary
    private float screenMaxX = 8f;  // Right boundary
    private List<GameObject> spawnedCollectables = new List<GameObject>(); // Track all spawned Collectables
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

        StartCoroutine(WaitForDrumTrackStartTime());

    }
    private IEnumerator WaitForDrumTrackStartTime()
    {
        while (drumTrack == null || drumTrack.GetLoopLengthInSeconds() <= 0 || drumTrack.startDspTime == 0)
            yield return null;

        Debug.Log($"{gameObject.name} - Synchronized start confirmed.");
    }

    void Update()
    {
        if (drumTrack?.drumAudioSource == null) return;

        float elapsedTime = (float)(AudioSettings.dspTime - drumTrack.startDspTime);
        float stepDuration = drumTrack.GetLoopLengthInSeconds() / drumTrack.totalSteps;
        int globalStep = Mathf.FloorToInt(elapsedTime / stepDuration) % drumTrack.totalSteps;

        if (globalStep != lastStep)
        {
            Debug.Log($"[{gameObject.name}] Step changed: {lastStep} → {globalStep}");
            PlayLoopedNotes(globalStep);
            lastStep = globalStep;
        }
    }

    public List<(int stepIndex, int note, int duration, float velocity)> GetPersistentLoopNotes()
    {
        return persistentLoopNotes;
    }

    public bool HasNoteSet()
    {
        return currentNoteSet != null;
    }
    

    public void BuildNoteSet()
    {
        currentNoteSet.BuildNotesFromKey();
        currentNoteSet.BuildAllowedStepsFromStyle(16);
        
    }

    public void SetCurrentNoteSet(NoteSet noteSet)
    {
        currentNoteSet = noteSet;
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
        Debug.Log($"Initial allowed steps count: {allowedSteps.Count}");

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
    
    private void CollectNote(int note, int durationTicks, float force)
    {
        int stepIndex = GetCurrentStep();

        // ✅ Add the note to the persistent loop
        Debug.Log($"Adding {note} with {force} for {durationTicks} to step {stepIndex} ");
        persistentLoopNotes.Add((stepIndex, note, durationTicks, force));
        controller.UpdateVisualizer();
        PlayNote(note, durationTicks, force);
    }
    
    public void ExpandLoop()
    {
        if (loopMultiplier >= maxLoopMultiplier)
            return;

        loopMultiplier *= 2;
        totalSteps = drumTrack.totalSteps * loopMultiplier;

        RemapLoopedNotes(); // Reorganize current notes
        Debug.Log($"{gameObject.name} expanded to {loopMultiplier}:1 loop ratio.");
    }

    private void RemapLoopedNotes()
    {
        var remappedNotes = new List<(int stepIndex, int note, int duration, float velocity)>();

        foreach (var (oldStep, note, duration, velocity) in persistentLoopNotes)
        {
            int newStep = Mathf.FloorToInt((oldStep / (float)(totalSteps / loopMultiplier)) * (totalSteps / 2));
            remappedNotes.Add((newStep, note, duration, velocity));
        }

        persistentLoopNotes = remappedNotes;
    }

    void PlayLoopedNotes(int globalStep)
    {
        foreach (var (storedStep, note, duration, velocity) in persistentLoopNotes)
        {
            if (storedStep % drumTrack.totalSteps == globalStep) // ✅ Ensure step alignment
            {
                Debug.Log($"[{gameObject.name}] Triggering Note {note} at step {globalStep}");
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
        Debug.Log($"Playing MIDI Note {note} for {durationTicks} ticks @ velocity {velocity}");

        midiStreamPlayer.MPTK_PlayEvent(noteOn);
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
        if (currentNoteSet.GetStepList().Count == 0)
        {
            Debug.LogError($"{gameObject.name} - ERROR: No allowed steps for spawning.");
            return;
        }

        Debug.Log($"{gameObject.name} - Spawning notes ({currentNoteSet.GetStepList().Count} steps).");
        int halfPoint = totalSteps / 2;

// Only spawn notes in the newly expanded part of the loop
        List<int> eligibleSteps = currentNoteSet.GetStepList()
            .Where(step => step >= halfPoint)
            .ToList();

        foreach (int stepIndex in eligibleSteps)
        {
            int assignedNote = currentNoteSet.GetNextArpeggiatedNote(stepIndex);
            int chosenDurationTicks = CalculateNoteDuration(stepIndex, currentNoteSet);

            Vector2Int gridPos = GetGridPositionForStep(stepIndex, assignedNote, currentNoteSet);

            if (!drumTrack.IsSpawnCellAvailable(gridPos.x, gridPos.y))
            {
                Debug.Log($"Cell {gridPos} occupied, skipping.");
                continue; // ✅ Skip if grid cell is unavailable
            }

            Vector3 spawnPosition = drumTrack.GridToWorldPosition(gridPos);
            GameObject spawned = Instantiate(collectablePrefab, spawnPosition, Quaternion.identity, collectableParent);
            Collectable collectable = spawned.GetComponent<Collectable>();
            collectable.energySprite.color = trackColor;
            if (collectable == null)
            {
                Debug.LogError("Spawned object missing Collectable component.");
                Destroy(spawned);
                continue;
            }

            collectable.Initialize(assignedNote, chosenDurationTicks, this, currentNoteSet.noteBehavior);
            collectable.OnCollected += (int duration, float force) => OnCollectableCollected(collectable, stepIndex, duration, force);
            collectable.OnDestroyed += () => OnCollectableDestroyed(collectable);

            drumTrack.OccupySpawnGridCell(gridPos.x, gridPos.y, GridObjectType.Note);
            spawnedCollectables.Add(collectable.gameObject);
        }
    }

    private int GetCurrentStep()
    {
        if (drumTrack?.drumAudioSource == null) return -1;

        // ✅ Use DSP time for precise timing
        float elapsedTime = (float)(AudioSettings.dspTime - drumTrack.startDspTime);
        float stepDuration = drumTrack.GetLoopLengthInSeconds() / drumTrack.totalSteps;
        int step = Mathf.FloorToInt(elapsedTime / stepDuration) % drumTrack.totalSteps;

        return step;
    }

// Clearly translate from 0-63 steps into your gridWidth (e.g., 8)
    private Vector2Int GetGridPositionForStep(int stepIndex, int assignedNote, NoteSet noteSet)
    {
        if (drumTrack == null || !drumTrack.HasSpawnGrid())
        {
            Debug.LogError($"{gameObject.name} - ERROR: DrumTrack or SpawnGrid is NULL!");
            return new Vector2Int(0, 0); // ✅ Safe fallback to prevent errors
        }

        int gridWidth = drumTrack.GetSpawnGridWidth();
        int gridHeight = drumTrack.GetSpawnGridHeight();

        if (gridWidth == 0 || gridHeight == 0)
        {
            Debug.LogError("Grid width or height is 0! Cannot calculate grid position.");
            return new Vector2Int(0, 0);
        }

        // ✅ Ensure stepIndex is within valid range
        int clampedStepIndex = Mathf.Clamp(stepIndex, 0, drumTrack.totalSteps - 1);

        int x = clampedStepIndex % gridWidth;
        int y = noteSet.GetNoteGridRow(assignedNote, gridHeight);

        // ✅ Ensure `x` and `y` are within valid grid range
        x = Mathf.Clamp(x, 0, gridWidth - 1);
        y = Mathf.Clamp(y, 0, gridHeight - 1);

        Debug.Log($"[InstrumentTrack] Calculated Grid Position: ({x}, {y}) for stepIndex {stepIndex}, assignedNote {assignedNote}");

        return new Vector2Int(x, y);
    }


    
    private int CalculateNoteDuration(int stepIndex, NoteSet noteSet)
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
    
    private void CheckSectionComplete()
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
            
//            controller.TrackExpansionCompleted(this);            
        }

    }

    private IEnumerator WaitForDrumTrackSpawnGrid()
    {
        while (!drumTrack.HasSpawnGrid())
        {
            Debug.Log($"{gameObject.name} - Waiting for DrumTrack to initialize spawnGrid...");
            yield return null; // ✅ Wait until it exists
        }
       
        Debug.Log($"{gameObject.name} - SpawnGrid assigned in DrumTrack!");
    }
    


    void OnCollectableDestroyed(Collectable collectable)
    {
        Vector2Int gridPos = drumTrack.WorldToGridPosition(collectable.transform.position);
    
        if (drumTrack.HasSpawnGrid())
        {
            drumTrack.FreeSpawnCell(gridPos.x, gridPos.y); // ✅ Ensure the cell is freed
            Debug.Log($"✅ Freed cell at {gridPos.x}, {gridPos.y}");
        }
        else
        {
            Debug.LogError("❌ SpawnGrid is NULL! Cannot free cell.");
        }

        spawnedCollectables.Remove(collectable.gameObject);
    }

    public void RemoveAllCollectables()
    {
        Debug.Log($"{gameObject.name} - Removing {spawnedCollectables.Count} collectables");

        foreach (GameObject collectable in spawnedCollectables)
        {
            if (drumTrack.HasSpawnGrid())
            {
                Vector2Int gridPos = drumTrack.WorldToGridPosition(collectable.transform.position);
                drumTrack.FreeSpawnCell(gridPos.x, gridPos.y);
                drumTrack.ResetSpawnCellBehavior(gridPos.x, gridPos.y);
                Debug.Log($"Freeing cell {gridPos.x}, {gridPos.y} before destroying");
            }
            Destroy(collectable);
        }
        spawnedCollectables.Clear();
        Debug.Log($"{gameObject.name} - All collectables removed.");
    }



    void OnCollectableCollected(Collectable collectable, int stepIndex, int durationTicks, float force)
    {
        Debug.Log($"Collected Note {collectable.name}");
        if (collectable.assignedInstrumentTrack != this)
        {
            return;
        }

        Vector2Int gridPos = drumTrack.WorldToGridPosition(collectable.transform.position);
        drumTrack.FreeSpawnCell(gridPos.x, gridPos.y);
        drumTrack.ResetSpawnCellBehavior(gridPos.x, gridPos.y);
    
        if (!hasCollectedNewNoteThisSet)
        {
            Debug.Log("First note collected in new NoteSet. Resetting loop for " + gameObject.name);
//            ClearLoopNotes();
            hasCollectedNewNoteThisSet = true;
        }

        // ✅ Store the collected note in the loop
        CollectNote(collectable.GetNote(), durationTicks, force);

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
    


}
