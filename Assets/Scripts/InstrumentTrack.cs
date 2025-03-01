using UnityEngine;
using MidiPlayerTK;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil.Cil;

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
    public int minDurationTicks = 4;    // 1/16th note duration.
    public int maxDurationTicks = 64;   // Whole note duration.

    private List<int> allowedNotes = new List<int>();
    private List<int> allowedSteps = new List<int>();
    private List<(int stepIndex, int note, int duration, float velocity)> persistentLoopNotes = new List<(int, int, int, float)>();
    private int currentSectionStart = 0; // ✅ The starting step of the current section
    private int currentSectionEnd = 64; // ✅ The ending step (expands when a section is completed)    
    private float screenMinX = -8f; // Left boundary
    private float screenMaxX = 8f;  // Right boundary
    private List<GameObject> spawnedCollectables = new List<GameObject>(); // Track all spawned Collectables
    private Dictionary<Collectable, (int note, int duration)> collectableNotes = new Dictionary<Collectable, (int, int)>();
    private int totalSteps = 64;
    private int lastStep = -1;
    private int currentExpansionCount = 0;
    private bool isPlayerControlled = false; // Set this in the inspector or via code.
    private bool hasCollectedNewNoteThisSet = false;
    private int freezeIndex = 0;
    private Boundaries boundaries;
    // Define the smallest and largest scales for collectables.
     void Start()
    {
        if (controller == null)
        {
            Debug.LogError($"{gameObject.name} - No InstrumentTrackController assigned!");
            return;
        }
        
    }

    public void AllowPlayerControl()
    {
        isPlayerControlled = true;
    }
    private IEnumerator DelayedStart()
    {
        yield return StartCoroutine(WaitForDrumLoopAndSpawn());
        yield return new WaitForSeconds(0.1f); // ✅ Small delay to prevent race condition
    }

    public void ApplyNoteSet(NoteSet newNoteSet)
    {
        if (currentNoteSet != null)
        {
            ClearLoopNotes();
        }
        
        isPlayerControlled = true;
        currentNoteSet = newNoteSet;
        allowedSteps = new List<int>(newNoteSet.allowedSteps);
        allowedNotes = new List<int>(newNoteSet.notes);
        currentExpansionCount = 0; // ✅ Reset expansions
        Debug.Log($"{gameObject.name} - Assigned new NoteSet with {allowedNotes.Count} notes. {currentNoteSet.name}");
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

    int SnapToClosestStep(long currentTick, long totalTicks)
    {
        if (totalTicks == 0) return 0; // Prevent division by zero

        float stepSize = totalTicks / (float)totalSteps;
        int snappedStep = Mathf.RoundToInt(currentTick / stepSize); // ✅ More accurate rounding

        // ✅ Ensure we never go out of bounds
        snappedStep = Mathf.Clamp(snappedStep, 0, totalSteps - 1);

        return snappedStep;
    }
    void Update()
    {
        if (drumTrack?.drumAudioSource == null) return;

        // ✅ Use DSP time for accurate synchronization
        float elapsedTime = (float)(AudioSettings.dspTime - drumTrack.startDspTime);
        float loopLength = drumTrack.loopLengthInSeconds;

        // ✅ Prevent division by zero
        if (loopLength <= 0 || totalSteps <= 0) return;

        // Calculate step size based on the expanded loop
        float baseStepSize = loopLength / totalSteps;
        int globalStep = Mathf.FloorToInt(elapsedTime / baseStepSize) % totalSteps;

        if (globalStep != lastStep)
        {
            
            PlayLoopedNotes(globalStep);
            lastStep = globalStep;
            for (int i = 0; i < spawnedCollectables.Count; i++)
            {
                if (freezeIndex % totalSteps == 0)
                {
                    spawnedCollectables[i].GetComponent<Rigidbody2D>().constraints = RigidbodyConstraints2D.FreezeAll;
                   
                }
                else
                {
                    spawnedCollectables[i].GetComponent<Rigidbody2D>().constraints = RigidbodyConstraints2D.None;
                }

            }

            freezeIndex++;
        }

        // ✅ Remove offscreen collectables
        RemoveOffscreenCollectables();

        // ✅ Check for section completion
        if (isPlayerControlled)
        {
            CheckSectionCompletion();
        }
    }

    public void SetBoundaries(Boundaries b)
    {
        boundaries = b;
    }
// ✅ Extract logic into a helper function
    void RemoveOffscreenCollectables()
    {
        Camera mainCamera = Camera.main;
        if (mainCamera == null) return;

        // Get the world Y position of the bottom of the screen
        float screenBottomY = mainCamera.ViewportToWorldPoint(new Vector3(0, -0.1f, 0)).y;

        for (int i = spawnedCollectables.Count - 1; i >= 0; i--)
        {
            GameObject collectableObj = spawnedCollectables[i];
            if (collectableObj == null) continue;

            // Get the collider bounds (works better for Rigidbody objects)
            Collider2D col = collectableObj.GetComponent<Collider2D>();
            if (col != null)
            {
                float objectBottomY = col.bounds.min.y; // Bottom of the collider

                // Destroy the collectable if it is fully below the screen
                if (objectBottomY < screenBottomY)
                {
                    RemoveCollectableNote(i);
                }
            }
            else
            {
                // Fallback: Use transform position if no collider is found
                if (collectableObj.transform.position.y < screenBottomY)
                {
                    RemoveCollectableNote(i);
                }
            }
        }
    }

    public void ClearLoopNotes()
    {
        persistentLoopNotes.Clear();
        Debug.Log($"{gameObject.name} - Cleared all looping notes for new track pattern.");
    }
    
    public void SpawnCollectables()
    {
        if (currentNoteSet == null)
        {
            Debug.LogError($"{gameObject.name} - No NoteSet assigned! Cannot spawn collectables.");
            return;
        }

        List<int> sectionSteps = allowedSteps
            .Where(step => step >= currentSectionStart && step < currentSectionEnd)
            .ToList();

        if (sectionSteps.Count == 0)
        {
            Debug.LogWarning($"{gameObject.name} - No allowed steps in this section.");
            return;
        }

        Debug.Log($"{gameObject.name} - Spawning collectables for expansion {currentExpansionCount}");

        foreach (int stepIndex in sectionSteps)
        {
            // ✅ Ensure no duplicate collectables at this step
            bool exists = spawnedCollectables.Any(obj => obj != null && Mathf.Abs(obj.transform.position.x - MapStepToX(stepIndex)) < 0.1f);
            if (!exists)
            {
                GameObject spawned = SpawnCollectable(stepIndex);
                Collider2D collider = spawned.GetComponent<Collider2D>();
                boundaries.Ignore(collider);
                Debug.Log($"Spawned collectable at step index {stepIndex}");
                spawnedCollectables.Add(spawned);
            }
            else
            {
                Debug.LogWarning($"{gameObject.name} - Skipping duplicate collectable at step {stepIndex}");
            }
        }
        //TODO: WAIT FOR NEXT TRACK

        
    }
    
    private float MapStepToX(int stepIndex)
    {
        float stepWidth = (screenMaxX - screenMinX) / (float)totalSteps;
        int adjustedStepIndex = stepIndex % totalSteps;
        return screenMinX + (adjustedStepIndex * stepWidth);
    }

    private GameObject SpawnCollectable(int stepIndex)
    {
        if (currentNoteSet == null || currentNoteSet.notes.Count == 0)
        {
            Debug.LogWarning($"{gameObject.name} - No notes in current NoteSet! Cannot spawn collectable.");
            return null;
        }

        int assignedNote = currentNoteSet.notes[Random.Range(0, currentNoteSet.notes.Count)];

        GameObject collectableObj = Instantiate(collectablePrefab, collectableParent);
        Collider2D collider = collectableObj.GetComponent<Collider2D>();
        // Spawn off-screen at the top
        float spawnOffscreenY = Camera.main.ViewportToWorldPoint(new Vector3(0.5f, 1.2f, 0)).y;
        float stepWidth = (screenMaxX - screenMinX) / (float)totalSteps;
        int adjustedStepIndex = stepIndex % totalSteps;
        float posX = screenMinX + (adjustedStepIndex * stepWidth);

        collectableObj.transform.position = new Vector3(posX, spawnOffscreenY, 0);


        // ✅ Assign properties correctly
        Collectable collectable = collectableObj.GetComponent<Collectable>();
        if (collectable == null)
        {
            Debug.LogError($"Collectable component missing on {collectableObj.name}. Check prefab.");
            return null;
        }
        // ✅ Determine duration (previously `chosenDurationTicks`)
        int chosenDurationTicks;
        if (currentNoteSet.allowedDuration == -1)
        {
            // ✅ Calculate duration dynamically based on step timing
            int nextStep = GetNextStep(stepIndex, currentNoteSet.allowedSteps);
            chosenDurationTicks = (nextStep - stepIndex) * (480 / 64); // Convert step difference to MIDI ticks
        }
        else
        {
            // ✅ Use explicitly assigned max duration
            chosenDurationTicks = Mathf.Min(currentNoteSet.allowedDuration, 48); // Limit excessive duration
        }
       
        collectable.noteDurationTicks = chosenDurationTicks; // ✅ Ensures correct duration
        collectable.Initialize(assignedNote, this);

        collectable.OnCollected += (int duration, float force) => OnCollectableCollected(collectable, stepIndex, duration, force);
                
        Debug.Log($"Spawned {collectableObj.name} at {collectableObj.transform.position}");

        return collectableObj;
}


    int GetNextStep(int currentStep, List<int> stepList)
    {
        foreach (int step in stepList)
        {
            if (step > currentStep) return step; // ✅ Return the next available step
        }
        return totalSteps; // ✅ If no further step, extend to the end of the loop
    }
    private void CheckSectionCompletion()
    {
        
        if (spawnedCollectables.Count > 0) 
        {
            return;
        }

        if (currentNoteSet == null)
        {
            Debug.LogError($"{gameObject.name} - No current NoteSet assigned!");
            return;
        }

        if (currentExpansionCount < currentNoteSet.maxExpansionAllowed - 1)
        {
            currentExpansionCount++;
            currentSectionStart = currentSectionEnd;
            currentSectionEnd += totalSteps;
            allowedSteps = allowedSteps.Select(step => step + totalSteps).ToList();

            Debug.Log($"{gameObject.name} - Expanding section {currentExpansionCount}/{currentNoteSet.maxExpansionAllowed}");

            SpawnCollectables();
            //still in control
        }
        else
        {
            Debug.Log($"{gameObject.name} - All expansions complete. Moving to next track.");
            //next track
            isPlayerControlled = false;
            controller.TrackExpansionCompleted(this);
        }
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
                Debug.Log("Exploding collectable at " + noteToRemove.transform.position);

                noteToRemove.GetComponent<Explode>().Permanent();
            }
            else
            {
                Debug.Log($"Destroying collectable at {noteToRemove.transform.position}");
                Destroy(noteToRemove);

            }

            Debug.Log($"{gameObject.name} - A collectable note was removed due to collision with an indestructable object.");
    }



    void OnCollectableDestroyed(Collectable collectable)
    {     

    }

    void OnCollectableCollected(Collectable collectable, int stepIndex, int durationTicks, float force)
    {
        // Verify this collectable belongs to this track.
        if (collectable.assignedInstrumentTrack != this)
        {
            return;
        }
        if(!hasCollectedNewNoteThisSet)
        {
            Debug.Log("First new collectable received, resetting loop for " + gameObject.name + " Expansion Count: " + currentExpansionCount);
            hasCollectedNewNoteThisSet = true;
        }
        
        // Here, stepIndex is assumed to be the absolute step for this note (e.g. 0, 48, 64, 112).
        // If needed, you could adjust by an offset (like currentSectionStart) if your allowedSteps are relative.
        int absoluteStep = stepIndex;
        int note = collectable.assignedNote;

        // Add the collected note into the persistent loop.
        persistentLoopNotes.Add((absoluteStep, note, durationTicks, force));
        Debug.Log($"{persistentLoopNotes.Count} notes in {gameObject.name} ");
        PlayNote(note, durationTicks, force);

        // Remove the collectable from tracking.
        collectableNotes.Remove(collectable);
        // Remove the spawned collectable by accessing its parent (if that's how your prefab is structured).
        GameObject parentObj = collectable.gameObject;
        spawnedCollectables.Remove(parentObj);
        Destroy(parentObj);
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
        int durationMs = Mathf.Max(
            Mathf.RoundToInt(durationTicks * (60000f / (drumTrack.drumLoopBPM * 480f))),
            200  // ✅ Set a minimum duration of 200ms
        );
        midiStreamPlayer.MPTK_Channels[channel].ForcedPreset = preset;
        midiStreamPlayer.MPTK_Channels[channel].ForcedBank = bank;
        MPTKEvent noteOn = new MPTKEvent()
        {
            Command = MPTKCommand.NoteOn,
            Value = note,
            Channel = channel,
            Duration = durationMs, // ✅ Fixed duration scaling
            Velocity = (int)velocity * 10,
        };

        MPTKEvent noteOff = new MPTKEvent()
        {
            Command = MPTKCommand.NoteOff,
            Value = note,
            Channel = channel,
            Delay = durationMs // ✅ Ensure note stops after correct time
        };

        midiStreamPlayer.MPTK_PlayEvent(noteOn);
    }


}
