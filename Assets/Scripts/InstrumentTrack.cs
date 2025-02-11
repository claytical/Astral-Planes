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
    private int currentSectionEnd = 32; // ✅ The ending step (expands when a section is completed)    
    private int totalInstrumentSteps = 32; // ✅ This will grow as sections are added
    private float screenMinX = -8f; // Left boundary
    private float screenMaxX = 8f;  // Right boundary
    private Dictionary<int, (int note, int duration)> noteSequence = new Dictionary<int, (int, int)>(); // Stores collected notes & durations
    private List<GameObject> spawnedCollectables = new List<GameObject>(); // Track all spawned Collectables
    private Dictionary<Collectable, (int note, int duration)> collectableNotes = new Dictionary<Collectable, (int, int)>();
    private int totalSteps = 32;
    private int currentLoopCount = 0;
    private int lastStep = -1; // Tracks previous step to prevent duplicate triggers
    private bool isLocked = false;
    private int lockInThreshold = -1; // Required notes to lock in this track
    private bool hasSpawnedInitialCollectables = false;
    private float instrumentElapsedTime = 0f;  // ✅ Independent time tracker

    void Start()
    {
        if (controller == null)
        {
            Debug.LogError($"{gameObject.name} - No InstrumentTrackController assigned!");
            return;
        }

        StartCoroutine(DelayedStart()); // ✅ Ensures correct order of execution
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
        lockInThreshold = selectedGroup.lockInThreshold;

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

        float loopLength = controller.drumTrack.loopLengthInSeconds;
        float baseStepSize = loopLength / 32f;  // ✅ Keeps original step timing

        // ✅ Manually track time instead of relying on `loopTime`
        instrumentElapsedTime += Time.deltaTime;

        // ✅ Compute step index correctly using totalInstrumentSteps
        int currentStep = Mathf.FloorToInt(instrumentElapsedTime / baseStepSize) % totalInstrumentSteps;


        if (currentStep != lastStep)
        {
            PlayLoopedNotes(currentStep);
            lastStep = currentStep;
        }

        CheckSectionCompletion();
    }


    public void ClearLoopNotes()
    {
        persistentLoopNotes.Clear();
        Debug.Log($"{gameObject.name} - Cleared all looping notes for new drum pattern.");
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
        if (!hasSpawnedInitialCollectables)
        {
            Debug.Log($"{gameObject.name} - Preventing early CheckSectionCompletion call.");
            return; // ✅ Ensures expansion only happens *after* the first set is collected
        }

        if (spawnedCollectables.Count == 0)
        {
            Debug.Log($"{gameObject.name} - Expanding Section: {currentSectionStart} → {currentSectionEnd}");

            // 1️⃣ Fetch the next NoteSet from InstrumentTrackController
            NoteSet nextNoteSet = controller.GetNextNoteSet();
            if (nextNoteSet == null)
            {
                Debug.LogWarning($"{gameObject.name} - No more NoteSets available, stopping expansion.");
                return;
            }

            // 2️⃣ Get the next note group's allowed steps
            int nextIndex = controller.CurrentNoteSetIndex();
            List<int> nextAllowedSteps = nextNoteSet.noteGroups[nextIndex].allowedSteps;
            if (nextAllowedSteps == null || nextAllowedSteps.Count == 0)
            {
                Debug.LogError($"{gameObject.name} - ERROR: Next NoteSet has no allowed steps!");
                return;
            }

            // 🛠 Shift allowed steps by `currentSectionEnd`, NOT `currentSectionStart`
            allowedSteps = nextAllowedSteps.Select(step => step + currentSectionEnd).ToList();

            // 3️⃣ Update section boundaries correctly
            currentSectionStart = currentSectionEnd;  // ✅ Shift start to the next section
            currentSectionEnd += totalSteps;  // ✅ Expand section boundary
            totalInstrumentSteps += totalSteps;  // ✅ Ensure total steps increase

            Debug.Log($"{gameObject.name} - New Section Set: {currentSectionStart} → {currentSectionEnd} (Total Steps: {totalInstrumentSteps})");
            Debug.Log($"{gameObject.name} - Updated Allowed Steps: {string.Join(", ", allowedSteps)}");

            // 4️⃣ Spawn new collectables for this section using the new NoteSet steps
            SpawnCollectables();
        }
    }





    IEnumerator WaitForDrumLoopAndSpawn()
    {
        // ✅ Ensure drum loop is initialized first
        yield return new WaitUntil(() => controller.drumTrack.loopLengthInSeconds > 0);

        // ✅ Ensure allowedSteps is populated before spawning
        yield return new WaitUntil(() => allowedSteps != null && allowedSteps.Count > 0);

        Debug.Log($"{gameObject.name} - Drum loop initialized. Allowed Steps at spawn: {string.Join(", ", allowedSteps)}");

        SpawnCollectables();
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

        if (collectable.assignedInstrumentTrack != this) // 🚨 Prevent cross-track confusion!
        {
            return;
        }
        int note = collectable.assignedNote;
        persistentLoopNotes.Add((stepIndex, note, durationTicks));

        // ✅ Remove from tracking
        collectableNotes.Remove(collectable);
        spawnedCollectables.Remove(collectable.gameObject.transform.parent.gameObject);
        Destroy(collectable.gameObject.transform.parent.gameObject);
     }


    void PlayLoopedNotes(int stepIndex)
    {
        int adjustedStepIndex = stepIndex % totalInstrumentSteps; // ✅ Ensure step wraps over full range

        foreach (var (storedStep, note, duration) in persistentLoopNotes)
        {
            if (storedStep == adjustedStepIndex)
            {
                Debug.Log($"{gameObject.name} - Playing Note: {note} at Step: {adjustedStepIndex}");
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
        StartCoroutine(DelayedNoteOff(noteOff, durationMs)); // ✅ Ensure delayed stop
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
