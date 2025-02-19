using UnityEngine;
using MidiPlayerTK;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

public class InstrumentTrack : MonoBehaviour
{
    public InstrumentTrackController controller; // 🎛️ Reference to main controller
    public MidiStreamPlayer midiStreamPlayer; // Plays MIDI notes
    public int channel;
    public int preset;
    public int bank;
    public GameObject collectablePrefab; // Prefab to spawn
    public Transform collectableParent; // Parent object for organization

    public int allowedDuration = -1;

    private NoteGroup currentNoteGroup; // ✅ Add this variable at the top of `InstrumentTrack.cs`
    private List<int> allowedNotes = new List<int>();
    private List<int> allowedSteps = new List<int>();
    private List<(int stepIndex, int note, int duration)> persistentLoopNotes = new List<(int, int, int)>();
    private int currentSectionStart = 0; // ✅ The starting step of the current section
    private int currentSectionEnd = 64; // ✅ The ending step (expands when a section is completed)    
    private int totalInstrumentSteps = 64; // ✅ This will grow as sections are added
    private float screenMinX = -8f; // Left boundary
    private float screenMaxX = 8f;  // Right boundary
    private Dictionary<int, (int note, int duration)> noteSequence = new Dictionary<int, (int, int)>(); // Stores collected notes & durations
    private List<GameObject> spawnedCollectables = new List<GameObject>(); // Track all spawned Collectables
    private Dictionary<Collectable, (int note, int duration)> collectableNotes = new Dictionary<Collectable, (int, int)>();
    private int totalSteps = 64;
    private int currentLoopCount = 0;
    private int lastStep = -1; // Tracks previous step to prevent duplicate triggers
    private bool isLocked = false;
    
    private bool hasSpawnedInitialCollectables = false;
    private float instrumentElapsedTime = 0f;  // ✅ Independent time tracker
    private int currentExpansionCount = 0;
    private bool hasStartedNewCollection = false;
    private bool hasBegun = false; // Guard flag
    private bool isPlayerControlled = false; // Set this in the inspector or via code.
    private bool hasCollectedNewNoteThisSet = false;

    void Start()
    {
        if (controller == null)
        {
            Debug.LogError($"{gameObject.name} - No InstrumentTrackController assigned!");
            return;
        }
    }

    public void BeginTrack()
    {
        Debug.Log("Begin Track Started");
        if (hasBegun)
        {
            return;
        }
        hasBegun = true;
        isPlayerControlled = true;
        int trackIndex = controller.instrumentTracks.IndexOf(this);
        NoteSet currentSet = controller.assignedNoteSets[controller.CurrentNoteSetIndex()];

        // Check if there is a corresponding NoteGroup for this track.
        if (trackIndex < currentSet.noteGroups.Count)
            {
                ApplyNoteGroup(currentSet.noteGroups[trackIndex]);
                Debug.Log($"{gameObject.name} - NoteGroup applied in BeginTrack.");
            }
        else
            {
                Debug.Log($"{gameObject.name} - No matching NoteGroup found for track index {trackIndex}. Disabling this track for the current arrangement.");
                // Optionally disable this track so it doesn't run.
                gameObject.SetActive(false);
                return;
        }

            // Only set hasBegun if allowedSteps is now populated.
            if (allowedSteps != null && allowedSteps.Count > 0)
            {
                hasBegun = true;
                StartCoroutine(DelayedStart());
            }
            else
            {
                Debug.Log($"{gameObject.name} is still empty.");
            }

    }

        public void ApplyNoteGroup(NoteGroup noteGroup)
    {
        if (noteGroup == null)
        {
            Debug.LogWarning($"{gameObject.name} - No NoteGroup assigned.");
            return;
        }

        currentNoteGroup = noteGroup;
        allowedSteps = new List<int>(noteGroup.allowedSteps);
        allowedSteps.Sort();
        allowedNotes = new List<int>(noteGroup.notes.ConvertAll(n => n.noteValue));

        Debug.Log($"{gameObject.name} - Assigned NoteGroup with {allowedNotes.Count} notes.");
    }
    private IEnumerator DelayedStart()
    {
        yield return StartCoroutine(WaitForDrumLoopAndSpawn());
        yield return new WaitForSeconds(0.1f); // ✅ Small delay to prevent race condition
    }

    public void ApplyNoteSet(NoteGroup selectedGroup)
    {
        if(selectedGroup == null)
        {
            Debug.LogError("Tried to apply a null NoteSet: " + gameObject.name);
        }

        this.currentNoteGroup = selectedGroup; // ✅ Assign the NoteGroup
        allowedSteps = new List<int>(selectedGroup.allowedSteps);
        if (selectedGroup.allowedSteps == null || selectedGroup.allowedSteps.Count == 0)
        {
            Debug.LogError("selected group allowed steps is empty");
            return;
        }

        allowedNotes.Clear(); // ✅ Prevents duplicates from previous assignments

        Dictionary<int, int> noteWeights = new Dictionary<int, int>();

        foreach (var wn in selectedGroup.notes)
        {
            noteWeights[wn.noteValue] = wn.weight;
        }

        // ✅ Create a weighted selection list
        Debug.Log($"{gameObject.name} - Notes Read: {string.Join(", ", noteWeights.Select(n => $"{n.Key} (Weight: {n.Value})"))}");
        allowedNotes = GenerateWeightedList(noteWeights);

        // ✅ Assign the lock-in threshold from the NoteGroup
        //lockInThreshold = selectedGroup.lockInThreshold;

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

    public bool Locked()
    {
        return isLocked;
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
        if (controller.drumTrack.drumAudioSource == null) return;


        // ✅ Use drum loop's playback time for synchronization
        // Calculate step size based on the base loop division (64 steps per loop)

        // Global step accounts for completed loops
        // drum loop length in seconds (for one 64-tick cycle)
        float drumLoopLength = controller.drumTrack.loopLengthInSeconds;

        // Calculate how long one full instrument loop lasts.
        float instrumentLoopLength = drumLoopLength * (totalInstrumentSteps / 64f);
        float loopLength = controller.drumTrack.loopLengthInSeconds; // length of one measure (64 ticks)
        // Use dspTime so we have a continuously increasing time value.
        float elapsedTime = (float)(AudioSettings.dspTime - controller.drumTrack.startDspTime);
        int measureCount = Mathf.FloorToInt(elapsedTime / loopLength);
        // Determine the duration of one step in the expanded instrument loop.
        float baseStepSize = instrumentLoopLength / totalInstrumentSteps;

        // Global step cycles from 0 to totalInstrumentSteps-1
        int globalStep = Mathf.FloorToInt(elapsedTime / baseStepSize) % totalInstrumentSteps;
        if (globalStep != lastStep)
        {
            PlayLoopedNotes(globalStep);
            lastStep = globalStep;

        }

        CheckSectionCompletion();
    }

    public void ClearLoopNotes()
    {
        persistentLoopNotes.Clear();
        Debug.Log($"{gameObject.name} - Cleared all looping notes for new drum pattern.");
    }

    // Example of resetting state when starting a new NoteSet:
    public void ResetTrackState()
    {
        hasBegun = false;
        hasSpawnedInitialCollectables = false;
        // Reset any other state variables (like expansion counters, flags, etc.)
    }


    public void ResetCollectables()
    {
        foreach (GameObject obj in spawnedCollectables)
        {
            if (obj != null)
                Destroy(obj);
        }
        spawnedCollectables.Clear();
        collectableNotes.Clear();
        noteSequence.Clear();
        currentLoopCount = 0; // ✅ Reset loop tracking
//        isFading = false;

        SpawnCollectables(); // ✅ Spawn a new set
    }

    public void SpawnCollectables()
    {
        hasStartedNewCollection = false;
        if (hasSpawnedInitialCollectables && currentSectionStart == 0)
        {
            Debug.LogWarning($"{gameObject.name} - Preventing duplicate spawn on first set.");
            return; // ✅ Prevents first set from spawning twice
        }

        Debug.Log($"{gameObject.name} - SpawnCollectables called. Allowed Steps: {string.Join(", ", allowedSteps)}");
        
        if (controller.drumTrack.drumAudioSource == null) return;
        if (allowedSteps == null || allowedSteps.Count == 0)
        {
            Debug.LogWarning($"{gameObject.name} - No allowed steps set in NoteSet.");
            return;
        }

        if (allowedNotes == null || allowedNotes.Count == 0)
        {
            Debug.LogWarning($"{gameObject.name} - No allowed notes set in NoteSet.");
            return;
        }

        float loopLength = controller.drumTrack.loopLengthInSeconds;
        float stepSize = loopLength / totalSteps; // ✅ Use base drum loop steps

        List<int> sectionSteps = allowedSteps.Where(step => step >= currentSectionStart && step < currentSectionEnd).ToList();

        if (sectionSteps.Count == 0)
        {
            Debug.LogWarning($"{gameObject.name} - No matching section steps, using all allowed steps instead.");
            sectionSteps = new List<int>(allowedSteps);
        }

        foreach (int stepIndex in sectionSteps)
        {
            int nextStep = GetNextStep(stepIndex, sectionSteps);
            if(nextStep <= stepIndex)
            {
                nextStep = currentSectionEnd;
            }
            int maxAllowedDurationTicks = (nextStep - stepIndex) * (int)(stepSize * 480f);
            int chosenDurationTicks = SelectMaxDuration(maxAllowedDurationTicks);
            float stepTime = stepIndex * stepSize;

            GameObject collectableObj = SpawnCollectable(stepIndex, stepTime, chosenDurationTicks);
        }

        hasSpawnedInitialCollectables = true; // ✅ Mark that initial set has been spawned
    }

    GameObject SpawnCollectable(int stepIndex, float tickPosition, int durationTicks)
    {
        if (allowedNotes.Count == 0)
        {
            Debug.LogWarning($"{gameObject.name} - allowedNotes list is empty! Cannot spawn collectable.");
            return null;
        }

        GameObject collectableObj = Instantiate(collectablePrefab, collectableParent);

        // ✅ Convert step index into correct X-position based on expanded steps
        float stepWidth = (screenMaxX - screenMinX) / (float)totalSteps;

        // 🛠 Adjust stepIndex relative to the expanded section
        int adjustedStepIndex = stepIndex % totalSteps;  // Ensure it's within visible range

        float posX = screenMinX + (adjustedStepIndex * stepWidth);  // ✅ Align X correctly

        // 🎵 Pick a note and determine Y-position dynamically
        int assignedNote = allowedNotes[Random.Range(0, allowedNotes.Count)];
        float posY = MapNoteToYPosition(assignedNote);  // ✅ Dynamically scaled Y-position

        collectableObj.transform.position = new Vector3(posX, posY, 0); // ✅ Correctly positions collectable

        // ✅ Ensure the Collectable component is present
        Collectable collectable = collectableObj.GetComponentInChildren<Collectable>();
        if (collectable == null)
        {
            Debug.LogError($"Collectable component is missing on {collectableObj.name}. Please check the prefab.");
            return null;
        }

        // Assign properties
        collectable.noteDurationTicks = durationTicks;
        collectable.Initialize(assignedNote, this);
        collectableNotes[collectable] = (assignedNote, durationTicks);
        spawnedCollectables.Add(collectableObj);

        collectable.OnCollected += (int duration) => OnCollectableCollected(collectable, stepIndex, duration);
        collectable.OnDestroyed += OnCollectableDestroyed;

        return collectableObj;
    }
    private float MapNoteToYPosition(int noteValue)
    {
        if (allowedNotes == null || allowedNotes.Count == 0)
        {
            Debug.LogWarning("MapNoteToYPosition: No allowed notes available.");
            return 0; // Default to middle if no notes are available
        }

        // Find the min and max values dynamically from allowedNotes
        int minNote = allowedNotes.Min();
        int maxNote = allowedNotes.Max();

        // Prevent division by zero if there's only one note
        if (minNote == maxNote) return 0;

        // Ensure note is clamped within the dynamically set range
        noteValue = Mathf.Clamp(noteValue, minNote, maxNote);

        // Map note to range -2 to 2 using linear interpolation
        return Mathf.Lerp(-2f, 2f, (noteValue - minNote) / (float)(maxNote - minNote));
    }


    int GetNextStep(int currentStep, List<int> stepList)
    {
        foreach (int step in stepList)
        {
            if (step > currentStep) return step; // ✅ Return the next available step
        }
        return totalSteps; // ✅ If no further step, extend to the end of the loop
    }
    public void CheckSectionCompletion()
    {
        // Prevent expansion before initial collectables have spawned.
        if (!hasSpawnedInitialCollectables)
        {
            return;
        }

        // If there are still collectables on screen, don't expand.
        if (spawnedCollectables.Count > 0)
        {
            return;
        }

        // Ensure we have a valid NoteGroup assigned.
        if (currentNoteGroup == null)
        {
            Debug.LogError($"{gameObject.name} - No current NoteGroup assigned!");
            return;
        }

        //For level design, if the max count is set by the designer to 2, we want to make sure it goes twice, not 3 times due to counting from 0. zero based array math versus human counting.
        if (currentExpansionCount < currentNoteGroup.maxExpansionAllowed - 1)
        {
            currentExpansionCount++;
            Debug.Log($"{gameObject.name} - Expanding section {currentExpansionCount}/{currentNoteGroup.maxExpansionAllowed}: expanding from {currentSectionStart} to {currentSectionEnd + totalSteps}");

            // Offset the allowedSteps by totalSteps so that new collectables fall into the next section.
            allowedSteps = allowedSteps.Select(step => step + totalSteps).ToList();

            // Update section boundaries and overall instrument steps.
            currentSectionStart = currentSectionEnd;
            currentSectionEnd += totalSteps;
            totalInstrumentSteps += totalSteps;
            Debug.Log("Total Instrument Steps: " + totalInstrumentSteps + " CURRENT START: " + currentSectionStart + " CURRENT END: " + currentSectionEnd);
            // Spawn new collectables for the next section.
            SpawnCollectables();
        }
        else
        {
            // Maximum expansions reached for the current NoteGroup.
            Debug.Log($"{gameObject.name} - Maximum expansions reached for current NoteGroup. Awaiting new note collection to transition.");

            // Optionally, signal to the controller that this track's expansion for the current NoteGroup is complete.
            controller.TrackExpansionCompleted(this);
            // Do not clear persistentLoopNotes here; let them play until new collection starts.
        }
    }





    IEnumerator WaitForDrumLoopAndSpawn()
    {
        // Guard: if we've already spawned collectables, exit.
        if (hasSpawnedInitialCollectables)
            yield break;

        yield return new WaitUntil(() => controller.drumTrack.loopLengthInSeconds > 0);
        yield return new WaitUntil(() => allowedSteps != null && allowedSteps.Count > 0);

        Debug.Log($"{gameObject.name} - Drum loop initialized. Allowed Steps at spawn: {string.Join(", ", allowedSteps)}");
        SpawnCollectables();
        hasSpawnedInitialCollectables = true;
    }

    public void UnlockTrack()
    {
        isLocked = false;
        noteSequence.Clear(); // ✅ Ensure old notes are gone
        spawnedCollectables.Clear(); // ✅ Clear stale collectables
        Debug.Log($"{gameObject.name} - Track unlocked, ready to spawn new collectables.");
    }




    void OnCollectableDestroyed(Collectable collectable)
    {     

        spawnedCollectables.Remove(collectable.gameObject.transform.parent.gameObject);

    }

    void OnCollectableCollected(Collectable collectable, int stepIndex, int durationTicks)
    {
        // Verify this collectable belongs to this track.
        if (collectable.assignedInstrumentTrack != this)
        {
            return;
        }
        if(!hasCollectedNewNoteThisSet)
        {
            Debug.Log("First new collectable received");
            persistentLoopNotes.Clear();
            hasCollectedNewNoteThisSet = true;
        }

        if(!hasStartedNewCollection)
        {
            Debug.Log("Starting new collection,");

//            persistentLoopNotes.Clear();
            hasStartedNewCollection = true;
        }

        // Here, stepIndex is assumed to be the absolute step for this note (e.g. 0, 48, 64, 112).
        // If needed, you could adjust by an offset (like currentSectionStart) if your allowedSteps are relative.
        int absoluteStep = stepIndex;
        int note = collectable.assignedNote;

        // Add the collected note into the persistent loop.
        persistentLoopNotes.Add((absoluteStep, note, durationTicks));

        Debug.Log($"{gameObject.name} - Collected note {note} at absolute step {absoluteStep} with duration {durationTicks}");

        // Remove the collectable from tracking.
        collectableNotes.Remove(collectable);
        // Remove the spawned collectable by accessing its parent (if that's how your prefab is structured).
        GameObject parentObj = collectable.gameObject.transform.parent.gameObject;
        spawnedCollectables.Remove(parentObj);
        Destroy(parentObj);
    }

    public void ResetInstrumentLoop()
    {
        if(isPlayerControlled)
        {
            hasCollectedNewNoteThisSet = false;
        }

        // Reset expansion-related variables.
        currentExpansionCount = 0;
        hasStartedNewCollection = false;

        // Reset the section boundaries and total steps to the base values.
        currentSectionStart = 0;
        // Assume totalSteps is the base length (for example, 64 ticks)
        currentSectionEnd = totalSteps;
        totalInstrumentSteps = totalSteps;
    }


    void PlayLoopedNotes(int globalStep)
    {
        // Here, persistentLoopNotes stores the absolute step at which each note should play.
        foreach (var (storedStep, note, duration) in persistentLoopNotes)
        {
            if (storedStep == globalStep)
            {
                PlayNote(note, duration);
            }
        }
    }

    void PlayNote(int note, int durationTicks)
    {
        if (controller.drumTrack == null || controller.drumTrack.drumLoopBPM <= 0)
        {
            Debug.LogError("Drum track is not initialized or has an invalid BPM.");
            return;
        }

        // ✅ Convert durationTicks into milliseconds using WAV BPM
        int durationMs = Mathf.RoundToInt((float)(durationTicks * (60000f / (controller.drumTrack.drumLoopBPM * 480f))));
        midiStreamPlayer.MPTK_Channels[channel].ForcedPreset = preset;
        midiStreamPlayer.MPTK_Channels[channel].ForcedBank = bank;

        MPTKEvent noteOn = new MPTKEvent()
        {
            Command = MPTKCommand.NoteOn,
            Value = note,
            Channel = channel,
            Duration = durationMs, // ✅ Fixed duration scaling
            Velocity = 100,
        };

        MPTKEvent noteOff = new MPTKEvent()
        {
            Command = MPTKCommand.NoteOff,
            Value = note,
            Channel = channel,
            Delay = durationMs // ✅ Ensure note stops after correct time
        };

        midiStreamPlayer.MPTK_PlayEvent(noteOn);
//        StartCoroutine(DelayedNoteOff(noteOff, durationMs)); // ✅ Ensure delayed stop
    }


    IEnumerator DelayedNoteOff(MPTKEvent noteOff, int delayMs)
    {
        yield return new WaitForSeconds(delayMs / 1000f); // Convert ms to seconds
        midiStreamPlayer.MPTK_PlayEvent(noteOff);
    }
    private int SelectMaxDuration(int maxAllowedDurationTicks)
    {
        if (currentNoteGroup == null)
        {
            Debug.LogWarning("SelectMaxDuration: No currentNoteGroup assigned!");
            return maxAllowedDurationTicks; // Default to the max allowed if no group is set
        }

        int selectedDurationTicks = currentNoteGroup.allowedDuration;

        if (selectedDurationTicks == -1)
        {
            return maxAllowedDurationTicks; // ✅ If duration is -1, use full time until next step
        }

        return Mathf.Min(selectedDurationTicks, maxAllowedDurationTicks); // ✅ Ensure we never exceed next step
    }

}
