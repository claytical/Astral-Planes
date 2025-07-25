using System;
using UnityEngine;
using MidiPlayerTK;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Random = UnityEngine.Random;

public class InstrumentTrack : MonoBehaviour
{
    [Header("Track Settings")]
    public Color trackColor;
    public GameObject collectablePrefab; // Prefab to spawn
    public Transform collectableParent; // Parent object for organization
    public GameObject tetherPrefab;

    [Header("Musical Role Assignment")]
    public MusicalRole assignedRole;
    public int lowestAllowedNote = 36; // 🎵 Lowest MIDI note allowed for this track
    public int highestAllowedNote = 84; // 🎵 Highest MIDI note
    public InstrumentTrackController controller; // 🎛️ Reference to main controller
    public MidiStreamPlayer midiStreamPlayer; // Plays MIDI notes
    public DrumTrack drumTrack;
    public int channel;
    public int preset;
    public int bank;
    public int loopMultiplier = 1;
    public int maxLoopMultiplier = 4;
    
    private NoteSet currentNoteSet;
    //private Coroutine currentSpawnerRoutine;
    private List<(int stepIndex, int note, int duration, float velocity)> persistentLoopNotes = new List<(int, int, int, float)>();
    public List<GameObject> spawnedCollectables = new List<GameObject>(); // Track all spawned Collectables
    private int totalSteps = 16;
    private int lastStep = -1;
    private Boundaries boundaries;
    List<GameObject> spawnedNotes = new();
    public int CollectedNotesCount => persistentLoopNotes.Count;
    private HashSet<int> collectedStepsInCycle = new(); // Step indices collected this cycle
    private float ghostCycleDuration = 8f; // override if needed
    
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

    }
    void Update()
    {
        if (drumTrack == null) return;

        float elapsedTime = (float)(AudioSettings.dspTime - drumTrack.startDspTime);
        float stepDuration = GetTrackLoopDurationInSeconds() / totalSteps;
        int localStep = Mathf.FloorToInt(elapsedTime / stepDuration) % totalSteps;

        if (localStep != lastStep)
        {
            PlayLoopedNotes(localStep);
            lastStep = localStep;
        }
        for (int i = spawnedCollectables.Count - 1; i >= 0; i--)
        {
            var obj = spawnedCollectables[i];
            if (obj == null)
            {
                spawnedCollectables.RemoveAt(i); // 💥 clean up dead reference
                continue;
            }

            if (obj.TryGetComponent(out Collectable c))
                c.PulseIfOnStep(localStep);
        }

    }
    private float GetTrackLoopDurationInSeconds()
    {
        return drumTrack.GetLoopLengthInSeconds() * loopMultiplier;
    }
    public List<(int stepIndex, int note, int duration, float velocity)> GetPersistentLoopNotes()
    {
        return persistentLoopNotes;
    }
    public bool HasNoteSet()
    {
        return currentNoteSet != null;
    }
    public int GetTotalSteps()
    {
        return totalSteps;
    }
    public void PerformSmartNoteModification()
    {
        
        if (drumTrack == null || !HasNoteSet())
            return;

        MusicalPhase phase = drumTrack.currentPhase;
        NoteSet noteSet = GetCurrentNoteSet();

        string[] options;

        switch (phase)
        {
            case MusicalPhase.Establish:
                options = new[] { "RootShift", "ChordChange" };
                break;
            case MusicalPhase.Evolve:
                options = new[] { "ChordChange", "NoteBehaviorChange" };
                break;
            case MusicalPhase.Intensify:
                options = new[] { "ChordChange", "RootShift", "NoteBehaviorChange" };
                break;
            case MusicalPhase.Release:
                options = new[] { "ChordChange", "RootShift" };
                break;
            case MusicalPhase.Wildcard:
                options = new[] { "ChordChange", "RootShift", "NoteBehaviorChange" };
                break;
            case MusicalPhase.Pop:
                options = new[] { "NoteBehaviorChange" };
                break;
            default:
                options = new[] { "ChordChange" };
                break;
        }
        string selected = options[Random.Range(0, options.Length)];
        

        switch (selected)
        {
            case "ChordChange":
                ApplyChordChange(noteSet);
                break;
            case "NoteBehaviorChange":
                ApplyNoteBehaviorChange(noteSet);
                break;
            case "RootShift":
                ApplyRootShift(noteSet);
                break;
        }

        controller.UpdateVisualizer();
    }
    private void ApplyChordChange(NoteSet noteSet)
{
    int[] chordOffsets = noteSet.GetRandomChordOffsets();

    for (int i = 0; i < persistentLoopNotes.Count; i++)
    {
        var (step, baseNote, duration, velocity) = persistentLoopNotes[i];
        int offset = chordOffsets[i % chordOffsets.Length];
        int newNote = Mathf.Clamp(baseNote + offset, lowestAllowedNote, highestAllowedNote);
        persistentLoopNotes[i] = (step, newNote, duration, velocity);
    }
}
    private void ApplyNoteBehaviorChange(NoteSet noteSet)
{
    // Pick a random behavior different from current
    var values = Enum.GetValues(typeof(NoteBehavior)).Cast<NoteBehavior>().ToList();
    values.Remove(noteSet.noteBehavior);
    NoteBehavior newBehavior = values[Random.Range(0, values.Count)];

    noteSet.ChangeNoteBehavior(newBehavior);
    

    // Optionally adjust durations to reflect behavior (e.g., Drone = long, Lead = short)
    for (int i = 0; i < persistentLoopNotes.Count; i++)
    {
        var (step, note, _, velocity) = persistentLoopNotes[i];
        int newDuration = newBehavior switch
        {
            NoteBehavior.Drone => 720,
            NoteBehavior.Bass => 480,
            NoteBehavior.Lead => 120,
            _ => 360
        };

        persistentLoopNotes[i] = (step, note, newDuration, velocity);
    }
}
    private void ApplyRootShift(NoteSet noteSet)
{
    int shift = Random.Range(-3, 4); // ±3 semitones
    noteSet.ShiftRoot(shift);

    // Rebuild note loop by voice-leading into closest new scale tones
    var newScaleNotes = noteSet.GetNoteList();

    for (int i = 0; i < persistentLoopNotes.Count; i++)
    {
        var (step, oldNote, duration, velocity) = persistentLoopNotes[i];
        int newNote = noteSet.GetClosestVoiceLeadingNote(oldNote, newScaleNotes);
        persistentLoopNotes[i] = (step, newNote, duration, velocity);
    }
}
    private int CollectNote(int stepIndex, int note, int durationTicks, float force)
    {
        AddNoteToLoop(stepIndex, note, durationTicks, force);
        PlayNote(note, durationTicks, force);
        return stepIndex;
    }
    public void ApplyChordProgression(ChordProgressionProfile profile)
    {
        if (profile == null) return;

        var loopNotes = GetPersistentLoopNotes();
        loopNotes.Clear();

        int totalSteps = GetTotalSteps();
        float stepsPerChord = profile.beatsPerChord * (totalSteps / drumTrack.drumLoopBPM);

        for (int i = 0; i < profile.chordSequence.Count; i++)
        {
            Chord chord = profile.chordSequence[i];
            int baseStep = Mathf.RoundToInt(i * stepsPerChord);

            foreach (int interval in chord.intervals)
            {
                int step = (baseStep + UnityEngine.Random.Range(0, 2)) % totalSteps;
                int midiNote = chord.rootNote + interval;
                int duration = CalculateNoteDuration(step, currentNoteSet);
                float velocity = UnityEngine.Random.Range(90f, 120f);
                loopNotes.Add((step, midiNote, duration, velocity));
            }
        }

        controller.UpdateVisualizer();
    }
    public void ExpandLoop()
    {
        if (loopMultiplier >= maxLoopMultiplier)
        {
            
            return;
            
        }
        

        loopMultiplier *= 2;
        totalSteps = drumTrack.totalSteps * loopMultiplier;

        
    }
    void PlayLoopedNotes(int localStep)
    {
        foreach (var (storedStep, note, duration, velocity) in persistentLoopNotes)
        {
            if (storedStep == localStep)
            {
                PlayNote(note, duration, velocity);
            }
        }
    }

    public void PlayNote(int note, int durationTicks, float velocity)
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
    public void PlayDarkNote(int note, int duration, float velocity)
    {
        if (midiStreamPlayer == null)
        {
            Debug.LogWarning($"{name} - Cannot play dark note: MIDI player is null.");
            return;
        }

        // Apply pitch bend downward (e.g., a quarter-tone down)
        int bendValue = 4096; // Halfway down from center
        midiStreamPlayer.MPTK_Channels[channel].fluid_channel_pitch_bend(bendValue);

        MPTKEvent darkNote = new MPTKEvent()
        {
            Command = MPTKCommand.NoteOn,
            Value = note,
            Channel = channel,
            Duration = duration,
            Velocity = Mathf.Clamp((int)velocity, 0, 127),
        };

        midiStreamPlayer.MPTK_PlayEvent(darkNote);

        // Optional: reset pitch bend after short delay
        midiStreamPlayer.StartCoroutine(ResetPitchBendAfterDelay(0.2f));
    }
    private IEnumerator ResetPitchBendAfterDelay(float delay)
    {
        yield return new WaitForSeconds(delay);
        midiStreamPlayer.MPTK_Channels[channel].fluid_channel_pitch_bend(8192); // Center
    }
    public void ClearLoopedNotes(TrackClearType type = TrackClearType.Remix, Vehicle vehicle = null)
    {
        if (persistentLoopNotes.Count == 0) return;

        switch (type)
        {
            case TrackClearType.EnergyRestore:
                if (vehicle != null)
                {
                    controller?.noteVisualizer?.TriggerNoteRushToVehicle(this, vehicle);
                }
                break;
            case TrackClearType.Remix:
                controller?.noteVisualizer?.TriggerNoteBlastOff(this);
                break;
        }

        spawnedNotes.Clear(); // Visuals are handled separately
        persistentLoopNotes.Clear();
    }
    public int CalculateNoteDurationFromSteps(int stepIndex, NoteSet noteSet)
    {
        List<int> allowedSteps = noteSet.GetStepList();
        int totalSteps = GetTotalSteps();

        // Find the next step after this one
        int nextStep = allowedSteps
            .Where(s => s > stepIndex)
            .DefaultIfEmpty(allowedSteps.First()) // wraparound
            .First();

        int stepsUntilNext = (nextStep - stepIndex + totalSteps) % totalSteps;
        if (stepsUntilNext == 0) stepsUntilNext = totalSteps;

        int ticksPerStep = Mathf.RoundToInt(480f / (totalSteps / 4f)); // 480 per quarter note
        int baseDuration = stepsUntilNext * ticksPerStep;

        RhythmPattern pattern = RhythmPatterns.Patterns[noteSet.rhythmStyle];
        int adjusted = Mathf.RoundToInt(baseDuration * pattern.DurationMultiplier);

        return Mathf.Max(adjusted, ticksPerStep / 2); // ensure audibility
    }
    public int GetNoteDensity()
    {
        return persistentLoopNotes.Count;
    }
    public NoteSet GetCurrentNoteSet()
    {
        return currentNoteSet;
    }
    private int GetCurrentStep()
    {
        if (drumTrack?.drumAudioSource == null) return -1;

        float elapsedTime = (float)(AudioSettings.dspTime - drumTrack.startDspTime);
        float stepDuration = GetTrackLoopDurationInSeconds() / totalSteps;
        int step = Mathf.FloorToInt(elapsedTime / stepDuration) % totalSteps;

        return step;
    }
    public void ContractLoop()
    {
        if (loopMultiplier <= 1)
        {
            ClearLoopedNotes(TrackClearType.Remix); // Final shrink is a clear
            return;
        }

        loopMultiplier /= 2;
        totalSteps = drumTrack.totalSteps * loopMultiplier;

        // Halve the note list by discarding every second note
        persistentLoopNotes = persistentLoopNotes
            .Where((_, index) => index % 2 == 0)
            .ToList();

        
    }
    public int CalculateNoteDuration(int stepIndex, NoteSet noteSet)
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


        return chosenDurationTicks;
    }
    public int AddNoteToLoop(int stepIndex, int note, int durationTicks, float force)
    {
        persistentLoopNotes.Add((stepIndex, note, durationTicks, force));

        GameObject noteMarker = controller.noteVisualizer.PlacePersistentNoteMarker(this, stepIndex);
        if (noteMarker != null)
        {
            VisualNoteMarker marker = noteMarker.GetComponent<VisualNoteMarker>();
            if (marker != null)
            {
                marker.Initialize(trackColor);
            }
            spawnedNotes.Add(noteMarker);
        }

        controller.UpdateVisualizer();
        return stepIndex;
    }
    private void OnCollectableDestroyed(Collectable collectable)
    {
        if (spawnedCollectables.Contains(collectable.gameObject))
        {
            spawnedCollectables.Remove(collectable.gameObject);
        }

        if (drumTrack != null && drumTrack.HasSpawnGrid())
        {
            Vector2Int gridPos = drumTrack.WorldToGridPosition(collectable.transform.position);
            drumTrack.FreeSpawnCell(gridPos.x, gridPos.y);
        }
    }
    public float GetVelocityAtStep(int step)
    {
        float max = 0f;
        foreach (var (noteStep, note, duration, velocity) in GetPersistentLoopNotes())
        {
            if (noteStep == step)
                max = Mathf.Max(max, velocity);
        }
        return max;
    }
    public void OnGhostCycleStarted()
    {
        collectedStepsInCycle.Clear(); // ✅ Reset first
        Debug.Log($"OnGhostCycleStarted: REGISTERING {name}");
        GameFlowManager.Instance?.RegisterInstrumentTrack(this);
    }
    public void OnGhostCycleEnded()
    {
        if (drumTrack.collectionMode == NoteCollectionMode.TimedPuzzle)
        {
            if (collectedStepsInCycle.Count == 0)
            {
                StartCoroutine(DelayUnregisterByOneLoop());
            }
            else
            {
                Debug.Log($"✅ Track '{name}' collected {collectedStepsInCycle.Count} notes in ghost cycle.");
            }
        }

    }
    private IEnumerator DelayUnregisterByOneLoop()
    {
        float loopLength = drumTrack.GetLoopLengthInSeconds();
        yield return new WaitForSeconds(loopLength);

        // If still no notes, clear and unregister
        if (persistentLoopNotes.Count == 0)
        {
            Debug.Log($"⌛ Track '{name}' expired after grace loop. Clearing.");
            ClearLoopedNotes(TrackClearType.Remix);
            GameFlowManager.Instance?.UnregisterInstrumentTrack(this);
        }
    }
    public void OnCollectableCollected(Collectable collectable, int _, int durationTicks, float force)
    {
        Debug.Log($"On Collectable Collected Called {collectable.name}");
        if (collectable.assignedInstrumentTrack != this)
        {
            return;
        }

        Vector2Int gridPos = drumTrack.WorldToGridPosition(collectable.transform.position);
        drumTrack.FreeSpawnCell(gridPos.x, gridPos.y);
        drumTrack.ResetSpawnCellBehavior(gridPos.x, gridPos.y);

        // ✅ Use current playhead step — NOT visual column
        int stepIndex = GetCurrentStep();
        collectedStepsInCycle.Add(stepIndex);
        stepIndex = CollectNote(stepIndex, collectable.GetNote(), durationTicks, force);
        ShowTetherEffect(collectable.transform.position, stepIndex);
        drumTrack.NotifyNoteCollected();
        spawnedCollectables.Remove(collectable.gameObject);
        Destroy(collectable.gameObject);
    }
    private void ShowTetherEffect(Vector3 source, int stepIndex)
    {
        Vector3 target = controller.noteVisualizer.GetNoteMarkerPosition(this, stepIndex);
        GameObject tether = Instantiate(tetherPrefab, source, Quaternion.identity);
        if (tether.TryGetComponent(out VisualTether tetherScript))
        {
            tetherScript.SetColor(trackColor);
            tetherScript.SetTargetPosition(target);
        }
    }

}
