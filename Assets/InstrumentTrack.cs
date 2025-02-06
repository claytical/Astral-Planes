using UnityEngine;
using MidiPlayerTK;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

public class InstrumentTrack : MonoBehaviour
{
    public InstrumentTrackController controller; // 🎛️ Reference to main controller
    public int noteGroupIndex = 0;

    public List<int> allowedNotes = new List<int>();
    public List<int> allowedDurations = new List<int>();

    public MidiStreamPlayer midiStreamPlayer; // Plays MIDI notes
    public int channel;
    public int preset;
    public int bank;
    public DrumTrack drumTrack; // Syncs with drum loop
    public List<string> availablePatterns; // ✅ List of drum patterns InstrumentTrack can queue

    public GameObject collectablePrefab; // Prefab to spawn
    public Transform collectableParent; // Parent object for organization
    public int maxSpawnedItems = 8; // ✅ Limit the number of spawned collectables


    public float fadeDuration = 1.5f; // Time in seconds for fade-in/out effect
    private float screenMinX = -8f; // Left boundary
    private float screenMaxX = 8f;  // Right boundary
    private Dictionary<int, (int note, int duration)> noteSequence = new Dictionary<int, (int, int)>(); // Stores collected notes & durations
    private List<GameObject> spawnedCollectables = new List<GameObject>(); // Track all spawned Collectables
    private Dictionary<Collectable, (int note, int duration)> collectableNotes = new Dictionary<Collectable, (int, int)>();

    private int totalSteps = 32;
    public int currentLoopCount = 0;
    private bool isFading = false;
    private int setCount = 0;
    private int lastStep = -1; // Tracks previous step to prevent duplicate triggers
    private HashSet<int> usedPositions = new HashSet<int>(); // ✅ Tracks used positions
    private bool allowFinalLoop = false; // ✅ Lets the loop play one extra time before reset

    public bool isLocked = false;
    private int lockInThreshold = -1; // Required notes to lock in this track

    void Start()
    {
        if (controller == null)
        {
            Debug.LogError($"{gameObject.name} - No InstrumentTrackController assigned!");
            return;
        }
        if (drumTrack == null || drumTrack.drums == null)
        {
            Debug.LogError($"{gameObject.name} - DrumTrack reference is missing!");
            return;
        }
        // Assign the correct NoteGroup from the first NoteSet in the controller
        if (controller.assignedNoteSets.Length > 0)
        {
            int setIndex = 0; // Default to first set
            int groupIndex = noteGroupIndex % controller.assignedNoteSets[setIndex].noteGroups.Count;
            ApplyNoteSet(controller.assignedNoteSets[setIndex].noteGroups[groupIndex]);
        }
        else
        {
            Debug.LogError($"{gameObject.name} - No NoteSets assigned to InstrumentTrackController!");
        }

        StartCoroutine(WaitForDrumTrackAndSpawn());
        StartCoroutine(TrackDrumLoops()); // ✅ Ensure each track runs its own loop tracking
    }

    public void ApplyNoteSet(NoteGroup selectedGroup)
    {
        allowedNotes.Clear(); // ✅ Prevents duplicates from previous assignments
        allowedDurations.Clear(); // ✅ Prevents duplicates from previous assignments

        Dictionary<int, int> noteWeights = new Dictionary<int, int>();
        Dictionary<int, int> durationWeights = new Dictionary<int, int>();

        foreach (var wn in selectedGroup.notes)
        {
            noteWeights[wn.noteValue] = wn.weight;
        }
        foreach (var wd in selectedGroup.duration)
        {
            durationWeights[wd.durationTicks] = wd.weight;
        }

        // ✅ Create a weighted selection list
        Debug.Log($"{gameObject.name} - Notes Read: {string.Join(", ", noteWeights.Select(n => $"{n.Key} (Weight: {n.Value})"))}");
        allowedNotes = GenerateWeightedList(noteWeights);
        allowedDurations = GenerateWeightedList(durationWeights);

        // ✅ Assign the lock-in threshold from the NoteGroup
        lockInThreshold = selectedGroup.lockInThreshold;

        Debug.Log($"{gameObject.name} - Assigned Group with {allowedNotes.Count} notes. Lock-in threshold: {lockInThreshold}");
    }

    private List<int> GenerateWeightedList(Dictionary<int, int> weights)
    {
        List<int> weightedList = new List<int>();

        foreach (var pair in weights)
        {
            if (pair.Value <= 0)
            {
                Debug.LogError($"{gameObject.name} - ERROR: Note {pair.Key} has an invalid weight of {pair.Value}!");
                continue;
            }

            for (int i = 0; i < pair.Value; i++)
            {
                weightedList.Add(pair.Key);
            }
        }

        Debug.Log($"{gameObject.name} - Generated Weighted Notes: {string.Join(", ", weightedList)}");

        return weightedList;
    }

    public void RequestPatternChange()
    {
        if (availablePatterns.Count > 0)
        {
            int randomIndex = UnityEngine.Random.Range(0, availablePatterns.Count);
            drumTrack.QueueDrumPattern(availablePatterns[randomIndex]);
        }
    }

    IEnumerator WaitForDrumTrackAndSpawn()
    {
        int retries = 0;
        while (drumTrack.drums.MPTK_TickLast <= 0 && retries < 10)
        {
            yield return new WaitForSeconds(0.5f);
            retries++;
        }
        if (drumTrack.drums.MPTK_TickLast <= 0)
        {
            Debug.LogError("DrumTrack failed to initialize.");
        }
        SpawnCollectables();
    }


    void Update()
    {
        long currentTick = drumTrack.drums.MPTK_TickCurrent;
        long totalTicks = drumTrack.drums.MPTK_TickLast;

        if (totalTicks > 0)
        {
            int currentStep = SnapToClosestStep(currentTick, totalTicks);

            // ✅ Process every step from the last played step to current step
            for (int step = lastStep + 1; step <= currentStep; step++)
            {
                if (step >= totalSteps) break; // Prevents out-of-bounds errors
                PlayLoopedNotes(step);
            }

            lastStep = currentStep; // ✅ Save the last processed step
        }
    }
    IEnumerator TrackDrumLoops()
    {
        while (true)
        {
            // ✅ Wait for the drum loop to restart (Tick < 10)
            yield return new WaitUntil(() => drumTrack.drums.MPTK_TickCurrent < 10);

            // ✅ Wait until the drum loop progresses past the restart point
            yield return new WaitUntil(() => drumTrack.drums.MPTK_TickCurrent > 10);

            if (!isFading)
            {
                currentLoopCount++; // ✅ Increment only after a FULL drum cycle

                // ✅ Check if this track has met the lock-in requirement
                if (noteSequence.Count >= lockInThreshold && !isLocked)
                {
                    isLocked = true;
                    Debug.Log($"{gameObject.name} - Track locked in!");

                    // ✅ Remove remaining collectables for this track
                    foreach (GameObject obj in spawnedCollectables)
                        Destroy(obj);
                    spawnedCollectables.Clear();

                    controller.CheckAllTracksLocked(); // ✅ Let the controller handle progression
                }
            }
        }
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
        isFading = false;

        SpawnCollectables(); // ✅ Spawn a new set
    }



    public void SpawnCollectables()
    {
        long totalTicks = drumTrack.drums.MPTK_TickLast;
        if (totalTicks <= 0)
        {
            return;
        }

        float stepSize = totalTicks / (float)totalSteps;
        int itemsSpawned = 0;
        List<int> availableSteps = new List<int>();

        // ✅ Generate a list of all steps so we can select random ones
        for (int i = 0; i < totalSteps; i++)
        {
            availableSteps.Add(i);
        }

        System.Random rng = new System.Random();
        availableSteps = availableSteps.OrderBy(x => rng.Next()).ToList(); // ✅ Randomize steps

        // ✅ Set an ideal cluster size (can be adjusted based on track)
        int clusterSize = Mathf.Max(2, maxSpawnedItems / 4); // Ensures clusters form but still spread

        for (int i = 0; i < maxSpawnedItems && availableSteps.Count > 0; i++)
        {
            int stepIndex = availableSteps[0]; // Pick a random step
            availableSteps.RemoveAt(0); // Prevent duplicate placement

            int durationTicks = GetRandomDuration();

            // ✅ Ensure a cluster forms but items still spread
            float clusterOffset = UnityEngine.Random.Range(-clusterSize, clusterSize + 1);
            stepIndex = Mathf.Clamp(stepIndex + (int)clusterOffset, 0, totalSteps - 1);

            if (!usedPositions.Contains(stepIndex))
            {
                GameObject collectableObj = SpawnCollectable(stepIndex, stepIndex * (long)stepSize, durationTicks);
                //StartCoroutine(FadeInCollectable(collectableObj));

                usedPositions.Add(stepIndex);
                itemsSpawned++;
            }
        }

        if (usedPositions.Count >= totalSteps)
        {
            allowFinalLoop = true;
        }
    }

    public void UnlockTrack()
    {
        isLocked = false;
        noteSequence.Clear(); // ✅ Ensure old notes are gone
        spawnedCollectables.Clear(); // ✅ Clear stale collectables
        Debug.Log($"{gameObject.name} - Track unlocked, ready to spawn new collectables.");
    }




    GameObject SpawnCollectable(int stepIndex, long tickPosition, int durationTicks)
    {
        if (allowedNotes.Count == 0)
        {
            Debug.LogWarning($"{gameObject.name} - allowedNotes list is empty! Cannot spawn collectable.");
            return null;
        }

        GameObject collectableObj = Instantiate(collectablePrefab, collectableParent);
//        collectableObj.transform.localScale = Vector3.zero; // Start hidden

        // ✅ Convert step index into correct X-position
        float stepWidth = (screenMaxX - screenMinX) / (float)totalSteps; // Evenly distribute steps
        float posX = screenMinX + (stepIndex * stepWidth);
        float posY = collectableParent.position.y; // Keep aligned with parent

        collectableObj.transform.position = new Vector3(posX, posY, 0); // ✅ Correctly positions collectable!

        Collectable collectable = collectableObj.GetComponentInChildren<Collectable>();
        if (collectable == null)
        {
            Debug.LogError($"Collectable component is missing on {collectableObj.name}. Please check the prefab.");
            return null;
        }
        // 🚀 Assign THIS InstrumentTrack to the Collectable
        

        collectable.noteDurationTicks = durationTicks;
        int assignedNote = allowedNotes[Random.Range(0, allowedNotes.Count)]; // ✅ Pick a note from allowedNotes
        collectable.Initialize(assignedNote, this); // ✅ Assign the note & track
        collectableNotes[collectable] = (assignedNote, durationTicks);
        spawnedCollectables.Add(collectableObj);

        collectable.OnCollected += (int duration) => OnCollectableCollected(collectable, stepIndex, duration);
        collectable.OnDestroyed += OnCollectableDestroyed;

        // ✅ Scale the collectable based on its duration
        // ✅ Ensure scaling makes a clear difference
        float minScale = 1f; // 1x scale for smallest notes
        float maxScale = 4f; // 4x scale for whole notes
        float durationMultiplier = Mathf.Lerp(minScale, maxScale, (float)durationTicks / totalSteps);

//        float durationMultiplier = Mathf.Max(1f, durationTicks / (totalSteps / 4f)); // Whole note = 4x size
//        collectableObj.transform.localScale = new Vector3(durationMultiplier, 1f, 1f);

        return collectableObj;
    }

    void OnCollectableDestroyed(Collectable collectable)
    {
       
        int collectedNotes = noteSequence.Count;
        int remainingNeeded = lockInThreshold - collectedNotes;
        spawnedCollectables.Remove(collectable.gameObject.transform.parent.gameObject);
        //Destroy(collectable.transform.parent.gameObject);

        if (spawnedCollectables.Count < maxSpawnedItems && remainingNeeded > 0)
        {
            Debug.Log($"{gameObject.name} - Spawning another collectable. Remaining needed: {remainingNeeded}");
            SpawnCollectables();
        }

    }

    void OnCollectableCollected(Collectable collectable, int stepIndex, int durationTicks)
    {
        Debug.Log($"{gameObject.name} - Collected a note at step {stepIndex}. Remaining Collectables: {spawnedCollectables.Count}");

        if (collectable.assignedInstrumentTrack != this) // 🚨 Prevent cross-track confusion!
        {
            Debug.LogWarning($"{gameObject.name} - Collected a note that does not belong to this track!");
            return;
        }
        if (collectableNotes.ContainsKey(collectable))
        {
            int note = collectable.assignedNote; // ✅ Use assigned note
            int storedDuration = collectableNotes[collectable].Item2;
        
            // ✅ Ensure the note is stored
            if (!noteSequence.ContainsKey(stepIndex))
            {
                noteSequence[stepIndex] = (note, storedDuration);
                Debug.Log($"{gameObject.name} - Stored note {note} at step {stepIndex}");
            }
            if (noteSequence.ContainsKey(stepIndex))
            {
                noteSequence.Remove(stepIndex);
                Debug.Log($"{gameObject.name} - Removed note at step {stepIndex}. Notes Left: {noteSequence.Count}");
            }
            PlayNote(note, storedDuration);

            // ✅ Remove from tracking
            collectableNotes.Remove(collectable);
            spawnedCollectables.Remove(collectable.gameObject.transform.parent.gameObject);
            Destroy(collectable.gameObject.transform.parent.gameObject);

            Debug.Log("SPAWNED COLLECTABLES AFTER NOTE PLAYED: " + spawnedCollectables.Count);
            int collectedNotes = noteSequence.Count;
            int remainingNeeded = lockInThreshold - collectedNotes;

            if (spawnedCollectables.Count < maxSpawnedItems && remainingNeeded > 0)
            {
                Debug.Log($"{gameObject.name} - Spawning another collectable. Remaining needed: {remainingNeeded}");
                SpawnCollectables();
            }

            // ✅ If all collectables are collected and no more are needed, lock in
            if (collectedNotes >= lockInThreshold)
            {
                isLocked = true;
                Debug.Log($"{gameObject.name} - Track locked in!");
                // Remove all remaining collectables for this track
                foreach (GameObject obj in spawnedCollectables)
                    Destroy(obj);
                spawnedCollectables.Clear();

                controller.CheckAllTracksLocked();
            }
        }

    }




    IEnumerator FadeInCollectable(GameObject collectable)
    {
        if (collectable == null) yield break; // ✅ Exit early if the object is null
        
        float timer = 0f;
        Vector3 targetScale = collectable.transform.localScale; // ✅ Keep target scale
        Vector3 startScale = Vector3.zero;

        while (timer < fadeDuration)
        {
            // ✅ Prevent accessing destroyed objects
            if (collectable == null) yield break;

            timer += Time.deltaTime;
            float progress = Mathf.Clamp01(timer / fadeDuration);

            if (collectable.transform != null) // ✅ Check if transform is still valid
            {
                collectable.transform.localScale = Vector3.Lerp(startScale, targetScale, progress); // ✅ Preserve target scale
            }

            yield return null;
        }
    }


    IEnumerator FadeOutCollectables()
    {
        isFading = true;

        float timer = 0f;
        while (timer < fadeDuration)
        {
            timer += Time.deltaTime;
            float progress = 1 - Mathf.Clamp01(timer / fadeDuration);

            foreach (var obj in spawnedCollectables)
            {
                if (obj != null)
                {
                    obj.transform.localScale = Vector3.one * progress;
                }
            }
            yield return null;
        }
        Debug.Log("FADE OUT " + gameObject.name);
        ResetCollectables();
//        RequestPatternChange();
        isFading = false;
    }

    void PlayLoopedNotes(int stepIndex)
    {

        if (noteSequence.ContainsKey(stepIndex))
        {
            (int note, int duration) = noteSequence[stepIndex];
            PlayNote(note, duration);
        }
        else
        {
            Debug.LogWarning($"{gameObject.name} - No note stored at step {stepIndex}, step skipped.");
        }
    }


    void PlayNote(int note, int durationTicks)
    {
        midiStreamPlayer.MPTK_Channels[channel].ForcedPreset = preset;
        midiStreamPlayer.MPTK_Channels[channel].ForcedBank = bank;

        MPTKEvent noteEvent = new MPTKEvent()
        {
            Command = MPTKCommand.NoteOn,
            Value = note,
            Channel = channel,
            Duration = durationTicks * 20, // Scale ticks to milliseconds
            Velocity = 100,
        };
        midiStreamPlayer.MPTK_PlayEvent(noteEvent);
    }

    int SnapToClosestStep(long currentTick, long totalTicks)
    {
        if (totalTicks == 0) return 0;

        float stepSize = totalTicks / (float)totalSteps;
        int snappedStep = Mathf.FloorToInt((currentTick + (stepSize / 2)) / stepSize);
//        int snappedStep = Mathf.RoundToInt((currentTick + (stepSize / 2)) / stepSize); // ✅ Adds offset to prevent skipped steps

        snappedStep = Mathf.Clamp(snappedStep, 0, totalSteps - 1);

//        Debug.Log($"{gameObject.name} - Snapped tick {currentTick} to step {snappedStep} (StepSize: {stepSize}, TotalTicks: {totalTicks})");

        return snappedStep;
    }


    public int GetRandomDuration()
    {
        if (allowedDurations.Count == 0)
        {
            Debug.LogWarning($"{gameObject.name} - allowedDurations list is empty! Defaulting to quarter note.");
            return 480; // 🎵 Default to quarter note
        }

        int selectedDuration = allowedDurations[Random.Range(0, allowedDurations.Count)];
    
        return selectedDuration;
    }
}
