using System;
using UnityEngine;
using MidiPlayerTK;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Gameplay.Mining;
using Random = UnityEngine.Random;

public class InstrumentTrack : MonoBehaviour
{
    [Header("Track Settings")]
    public Color trackColor;
    public GameObject collectablePrefab; // Prefab to spawn
    public Transform collectableParent; // Parent object for organization

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
    public List<GameObject> spawnedCollectables = new List<GameObject>(); // Track all spawned Collectables
    
    private NoteSet _currentNoteSet;
    private Boundaries _boundaries;
    private List<(int stepIndex, int note, int duration, float velocity)> persistentLoopNotes = new List<(int, int, int, float)>();
    private List<(int stepIndex, int note, int duration, float velocity)> notesSpawnedThisPhase = new List<(int, int, int, float)>();
    List<GameObject> _spawnedNotes = new();
    private int _totalSteps = 16, _lastStep = -1;
    private float _ghostCycleDuration = 8f; // override if needed
    private bool _wasPerfectThisStar;
    // Tracks spawned vs. collected ghost pattern notes this phase
    private readonly Dictionary<int, (int note, int duration, float vel)> _ghostSpawnedByStep =
        new Dictionary<int, (int, int, float)>();
    private readonly HashSet<int> _ghostCollectedSteps = new HashSet<int>();
    [SerializeField] private LayerMask cosmicDustLayer;   // set this in the Inspector to your Dust layer
    [SerializeField] private float dustCheckRadius = 0.2f;
    void Start() {
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
    void Update() {
        if (drumTrack == null) return;

        float elapsedTime = (float)(AudioSettings.dspTime - drumTrack.startDspTime);
        float stepDuration = GetTrackLoopDurationInSeconds() / _totalSteps;
        int localStep = Mathf.FloorToInt(elapsedTime / stepDuration) % _totalSteps;

        if (localStep != _lastStep)
        {
            PlayLoopedNotes(localStep);
            _lastStep = localStep;
        }
        for (int i = spawnedCollectables.Count - 1; i >= 0; i--)
        {
            var obj = spawnedCollectables[i];
            if (obj == null)
            {
                spawnedCollectables.RemoveAt(i); // 💥 clean up dead reference
                continue;
            }
        }

    }

    private int CalculateNoteDurationFromSteps(int stepIndex, NoteSet noteSet)
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
    public NoteSet GetCurrentNoteSet()
    {
        return _currentNoteSet;
    }
    public bool HasNoteSet()
    {
        return _currentNoteSet != null;
    }
    public void SetNoteSet(NoteSet noteSet)
    {
        _currentNoteSet = noteSet;
        Debug.Log($"Using note set {noteSet.name}");
    }
    public NoteSet GetActiveNoteSet()
    {
        // Return the active NoteSet for this track; use your existing cache if you have it
        if (_currentNoteSet != null) return _currentNoteSet;
        return GetComponentInChildren<NoteSet>();
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
    public int GetTotalSteps()
    {
        return _totalSteps;
    }
    public List<(int stepIndex, int note, int duration, float velocity)> GetPersistentLoopNotes()
    {
        return persistentLoopNotes;
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

        _spawnedNotes.Clear(); // Visuals are handled separately
        persistentLoopNotes.Clear();
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
    public void ApplyChord(Chord chord, bool reusePlayerNotes = true)
    {
        if (_currentNoteSet == null) return;

        // Build allowed chord tones across the track's playable range
        var allowed = new List<int>();
        for (int octave = -2; octave <= 3; octave++)
            foreach (var iv in chord.intervals)
            {
                int n = chord.rootNote + iv + 12 * octave;
                if (n >= lowestAllowedNote && n <= highestAllowedNote)
                    allowed.Add(n);
            }
        allowed.Sort();

        // Remap player's collected notes instead of throwing them away
        var modified = new List<(int step, int note, int dur, float vel)>();
        foreach (var (step, note, dur, vel) in persistentLoopNotes)
        {
            int newNote = Closest(allowed, note);                // nearest chord tone
            modified.Add((step, newNote, dur, vel));
        }

        RebuildLoopFromModifiedNotes(modified, transform.position);
    }
    public void RetuneLoopToChord(Chord chord)
    {
        if (persistentLoopNotes == null || persistentLoopNotes.Count == 0) return;

        // Build allowed tones across range
        var allowed = new List<int>();
        for (int oct = -2; oct <= 3; oct++)
        {
            foreach (var iv in chord.intervals)
            {
                int n = chord.rootNote + iv + 12 * oct;
                if (n >= lowestAllowedNote && n <= highestAllowedNote) allowed.Add(n);
            }
        }
        if (allowed.Count == 0) return;
        allowed.Sort();

        int Closest(int target)
        {
            int best = allowed[0], dBest = Mathf.Abs(best - target);
            for (int i = 1; i < allowed.Count; i++)
            {
                int d = Mathf.Abs(allowed[i] - target);
                if (d < dBest) { dBest = d; best = allowed[i]; }
            }
            return best;
        }

        var modified = new List<(int step, int note, int dur, float vel)>(persistentLoopNotes.Count);
        foreach (var (step, note, dur, vel) in persistentLoopNotes)
            modified.Add((step, Closest(note), dur, vel));

        RebuildLoopFromModifiedNotes(modified, transform.position);
    }
    public void ApplyChordProgression(ChordProgressionProfile profile)
    {
        if (profile == null) return;

        var loopNotes = GetPersistentLoopNotes();
        loopNotes.Clear();

        int totalSteps = GetTotalSteps();

    // beats in one full loop (BPM * seconds-per-loop)
        float beatsPerLoop = drumTrack.drumLoopBPM * (drumTrack.GetLoopLengthInSeconds() / 60f);

    // steps per beat
        float stepsPerBeat = totalSteps / Mathf.Max(1f, beatsPerLoop);

    // final: steps allocated to one chord region
        float stepsPerChord = profile.beatsPerChord * stepsPerBeat;

        for (int i = 0; i < profile.chordSequence.Count; i++)
        {
            Chord chord = profile.chordSequence[i];
            int baseStep = Mathf.RoundToInt(i * stepsPerChord);

            foreach (int interval in chord.intervals)
            {
                int step = (baseStep + UnityEngine.Random.Range(0, 2)) % totalSteps;
                int midiNote = chord.rootNote + interval;
                int duration;
                if (_currentNoteSet != null)
                {
                    duration = CalculateNoteDuration(step, _currentNoteSet);
                }
                else
                {
                    duration = assignedRole switch
                    {
                        MusicalRole.Bass => 480,
                        MusicalRole.Lead => 120,
                        MusicalRole.Harmony => 360,
                        _ => 360
                    };
                }

                float velocity = UnityEngine.Random.Range(90f, 120f);
                loopNotes.Add((step, midiNote, duration, velocity));
            }
        }

        controller.UpdateVisualizer();
    }
    public int GetNoteDensity()
    {
        return persistentLoopNotes.Count;
    }
    public void ExpandLoop()
    {
        if (loopMultiplier >= maxLoopMultiplier)
        {
            
            return;
            
        }
        

        loopMultiplier *= 2;
        _totalSteps = drumTrack.totalSteps * loopMultiplier;

        
    }
    public void ContractLoop()
    {
        if (loopMultiplier <= 1)
        {
            ClearLoopedNotes(TrackClearType.Remix); // Final shrink is a clear
            return;
        }

        loopMultiplier /= 2;
        _totalSteps = drumTrack.totalSteps * loopMultiplier;

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
            .DefaultIfEmpty(stepIndex + _totalSteps) // Wrap around if no further step is found
            .First();

        // Calculate how many steps between the current and next step, looping around if necessary
        int stepsUntilNext = (nextStep - stepIndex + _totalSteps) % _totalSteps;
        if (stepsUntilNext == 0)
            stepsUntilNext = _totalSteps; // Ensure a full loop duration if the next step wraps to itself.

        // Calculate the number of MIDI ticks per musical step.
        int ticksPerStep = Mathf.RoundToInt(480f / (_totalSteps / 4f)); // 480 ticks per quarter note.

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
                Debug.Log($"Adding note {note} with color {trackColor}");

            }
            _spawnedNotes.Add(noteMarker);
        }

        controller.UpdateVisualizer();
        return stepIndex;
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
    public void SpawnCollectableBurst(NoteSet noteSet, int maxToSpawn = -1)
{
    if (noteSet == null || collectablePrefab == null || controller?.noteVisualizer == null) return;

    var nv = controller.noteVisualizer;
    var stepList  = noteSet.GetStepList();
    var noteList  = noteSet.GetNoteList();
    if (stepList == null || stepList.Count == 0 || noteList == null || noteList.Count == 0) return;

    int spawned = 0;
    foreach (int step in stepList)
    {
        if (maxToSpawn > 0 && spawned >= maxToSpawn) break;

        // Decide note & duration like your Ghost code
        int note = noteSet.GetNoteForPhaseAndRole(this, step);
        int dur  = CalculateNoteDurationFromSteps(step, noteSet);

        // Pick a free spawn cell for this pitch row (similar to your Ghost grid logic)
        int pitchIndex = noteList.IndexOf(note);
        if (pitchIndex < 0) continue;
        bool placed = false;
        int w = drumTrack.GetSpawnGridWidth();
        var cols = Enumerable.Range(0, w).OrderBy(_ => UnityEngine.Random.value);  // randomize X

        foreach (int x in cols)
        {
            Vector2Int gp = new Vector2Int(x, pitchIndex);
            if (!drumTrack.IsSpawnCellAvailable(gp.x, gp.y)) continue;

            Vector3 worldPos = drumTrack.GridToWorldPosition(gp);
            var go = Instantiate(collectablePrefab, worldPos, Quaternion.identity, collectableParent);
            if (!go) break;

            if (go.TryGetComponent(out Collectable c))
            {
                // init like before
                c.energySprite.color = trackColor;
                c.Initialize(note, dur, this, noteSet, stepList);

                c.intendedStep = step;

                Transform marker = nv.EnsureMarker(this, step); // <- use EnsureMarker and make it robust (below)
                if (marker != null)
                {
                    var ml = marker.GetComponent<MarkerLight>() ?? marker.gameObject.AddComponent<MarkerLight>();
                    ml.SetGrey(new Color(1f,1f,1f,0.25f));
                    c.AttachTetherAtSpawn(marker, nv.noteTetherPrefab, trackColor, dur);
                }


                drumTrack.OccupySpawnGridCell(gp.x, gp.y, GridObjectType.Note);
                spawnedCollectables.Add(go);
                placed = true;
                spawned++;
            }
            if (placed) break; // ✅ only break after successful placement
        }


        // If couldn’t place at any column, keep going; no hard failure
    }
}
    public void OnCollectableCollected(Collectable collectable, int reportedStep, int durationTicks, float force) {
    if (collectable == null || collectable.assignedInstrumentTrack != this) return;

    // Free the vacated grid cell (safe-guarded)
    if (drumTrack != null)
    {
        Vector2Int gridPos = drumTrack.WorldToGridPosition(collectable.transform.position);
        drumTrack.FreeSpawnCell(gridPos.x, gridPos.y);
        drumTrack.ResetSpawnCellBehavior(gridPos.x, gridPos.y);
    }

    // Prefer the step chosen at spawn so visuals/audio align with the existing tether
    int targetStep = (collectable.intendedStep >= 0) ? collectable.intendedStep : GetCurrentStep();

    // Ensure ribbon marker exists and starts gray; attach tether if it wasn't attached at spawn
    var nv = controller?.noteVisualizer;
    if (nv != null)
    {
        Transform marker = null;
        var markerGO = nv.PlacePersistentNoteMarker(this, targetStep); // creates if missing; returns null if already exists
        if (markerGO != null) marker = markerGO.transform;
        else nv.noteMarkers.TryGetValue((this, targetStep), out marker);

        if (marker != null)
        {
            var ml = marker.GetComponent<MarkerLight>() ?? marker.gameObject.AddComponent<MarkerLight>();
            ml.SetGrey(new Color(1f, 1f, 1f, 0.25f));

            // Fallback: if somehow no tether yet, attach now (nv.noteTetherPrefab should be a GameObject)
            if (collectable.tether == null && nv.noteTetherPrefab != null)
                collectable.AttachTetherAtSpawn(marker, nv.noteTetherPrefab, trackColor, durationTicks);
        }
    }

    // Authoritative commit is INSTANT (no motion/lerp in logic layer)
    CollectNote(targetStep, collectable.GetNote(), durationTicks, force);
    // Visual: ride pre-attached tether to marker, light it, raise events inside Collectable, then self-destroy
    collectable.TravelAlongTetherAndFinalize(durationTicks, force, seconds: 0.35f);

    // List hygiene (avoid double-remove)
    if (spawnedCollectables != null)
        spawnedCollectables.Remove(collectable.gameObject);
}
    public void SetMuted(bool muted)
    {
        // Route to your synth/MIDI/FMOD layer
        // e.g., midiOut.SetTrackGain(trackIndex, muted ? -80f : 0f);
    }
    public void ResetPerfectionFlagForPhase()
    {
        // Whatever you use to mark "this track was made perfect" for the star logic:
        // e.g., clear any per-phase/per-star flag, counters, cached burst status, etc.
        _wasPerfectThisStar = false;  // replace with your real field(s)
    }
    public List<(int step, int note, int duration, float velocity)> GetMissedGhostPayloads()
    {
        var missed = new List<(int, int, int, float)>();
        var loopNotes = GetPersistentLoopNotes();
        int totalSteps = GetTotalSteps();
        // 480 ticks per quarter note; convert payload durations (ticks) -> steps for tolerance
        int ticksPerStep = Mathf.RoundToInt(480f / (totalSteps / 4f));

        bool CoveredByLoopNear(int spawnStep, int durTicks)
        {
            int tolSteps = Mathf.Max(1, Mathf.RoundToInt((durTicks / (float)Mathf.Max(1, ticksPerStep)) * 0.5f));
            for (int i = 0; i < loopNotes.Count; i++)
            {
                int landed = loopNotes[i].stepIndex;
                int diff = Mathf.Abs(landed - spawnStep);
                diff = Mathf.Min(diff, totalSteps - diff); // circular distance
                if (diff <= tolSteps) return true;
            }
            return false;
        }

        foreach (var kv in _ghostSpawnedByStep)
        {
            int spawnStep = kv.Key;
            var (n, d, v) = kv.Value; // d is ticks in your data
            if (_ghostCollectedSteps.Contains(spawnStep)) continue; // on-window hit (legacy path)
            if (CoveredByLoopNear(spawnStep, d))         continue; // near-by loop landing covers it
            missed.Add((spawnStep, n, d, v));
        }
        return missed;
    }
    public void ResetGhostPhaseTracking()
    {
        _ghostSpawnedByStep.Clear();
        _ghostCollectedSteps.Clear();
    }
    public void RegisterSpawnedNotesThisPhase(List<(int stepIndex, int note, int duration, float velocity)> newNotes)
    {
        notesSpawnedThisPhase.AddRange(newNotes);
        _ghostSpawnedByStep.Clear();
        _ghostCollectedSteps.Clear();
        foreach (var (s, n, d, v) in newNotes)
        {
            _ghostSpawnedByStep[s] = (n, d, v);
        }
    }
    public bool IsTrackUtilityRelevant(TrackModifierType modifierType)
    {
        // 1. Skip if there's no loop data yet
        if (persistentLoopNotes == null || persistentLoopNotes.Count == 0)
            return false;

        // 2. Avoid duplicate behavior modifiers — could be expanded
        switch (modifierType)
        {
            case TrackModifierType.RootShift:
                return !HasAlreadyShiftedNotes();
            case TrackModifierType.Clear:
                return persistentLoopNotes.Count > 0;
            case TrackModifierType.Remix:
                return !HasRemixActive(); // placeholder for tracking remix status
            default:
                return true; // fallback: assume useful
        }
    }
    public void PerformSmartNoteModification(Vector3 sourcePosition)
    {
        Debug.Log($"Performing SmartNoteModification on {gameObject.name}");
        if (drumTrack == null || !HasNoteSet())
            return;

        MusicalPhase phase = drumTrack.currentPhase;

        string[] options;
        Debug.Log($"Assessing options for {_currentNoteSet}");

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
                ApplyChordChange(_currentNoteSet, sourcePosition);
                break;
            case "NoteBehaviorChange":
                ApplyNoteBehaviorChange(_currentNoteSet, sourcePosition);
                break;
            case "RootShift":
                ApplyRootShift(_currentNoteSet, sourcePosition);
                break;
        }

        controller.UpdateVisualizer();
    }
    public bool IsPerfectThisPhase { get; private set; }
    public void ResetPerfectionFlag() => IsPerfectThisPhase = false;

    private IEnumerator WaitForDrumTrackStartTime() {
        while (drumTrack == null || drumTrack.GetLoopLengthInSeconds() <= 0 || drumTrack.startDspTime == 0)
            yield return null;

    }
    private float GetTrackLoopDurationInSeconds()
    {
        return drumTrack.GetLoopLengthInSeconds() * loopMultiplier;
    }
    private bool HasAlreadyShiftedNotes()
    {
        // Placeholder logic — adapt as needed for actual behavior detection
        return persistentLoopNotes.Any(n => n.note > 127); // example threshold
    }
    private bool HasRemixActive()
    {
        // You can track this via a bool, tag, or count of modified notes
        return false; // stub for now
    }
    private void RebuildLoopFromModifiedNotes(List<(int, int, int, float)> modifiedNotes, Vector3 sourcePosition)
{
    persistentLoopNotes.Clear();

    foreach (var obj in _spawnedNotes)
    {
        if (obj != null)
            Destroy(obj);
    }
    _spawnedNotes.Clear();

    foreach (var (step, note, duration, velocity) in modifiedNotes)
    {
        AddNoteToLoop(step, note, duration, velocity);
        // compute the ribbon position yourself
        Vector3 worldPos = controller.noteVisualizer.ComputeRibbonWorldPosition(this, step);
//        var marker = controller.noteVisualizer.PlacePersistentNoteMarker(this, step);

        var marker = Instantiate(controller.noteVisualizer.notePrefab, worldPos,
            Quaternion.identity, controller.noteVisualizer.transform);
        VisualNoteMarker noteMarker = marker.GetComponent<VisualNoteMarker>();
        if (noteMarker != null)
        {
            noteMarker.Initialize(trackColor);
            Debug.Log($"Adding note {note} with color {trackColor}");
        }
        else
        {
            Debug.Log($"{marker.gameObject} is missing the note marker");

        }
        _spawnedNotes.Add(marker);
        var key = (this, step);
        controller.noteVisualizer.noteMarkers[key] = marker.transform;
    }
}
    private int Closest(List<int> pool, int target)
{
    int best = pool[0];
    int bestDist = Mathf.Abs(best - target);
    for (int i = 1; i < pool.Count; i++)
    {
        int d = Mathf.Abs(pool[i] - target);
        if (d < bestDist) { best = pool[i]; bestDist = d; }
    }
    return best;
}
    private void ApplyChordChange(NoteSet noteSet, Vector3 sourcePosition)
{
    int[] chordOffsets = noteSet.GetRandomChordOffsets();
    var modifiedNotes = new List<(int, int, int, float)>();

    for (int i = 0; i < persistentLoopNotes.Count; i++)
    {
        var (step, baseNote, duration, velocity) = persistentLoopNotes[i];
        int offset = chordOffsets[i % chordOffsets.Length];
        int newNote = Mathf.Clamp(baseNote + offset, lowestAllowedNote, highestAllowedNote);
        modifiedNotes.Add((step, newNote, duration, velocity));
    }

    RebuildLoopFromModifiedNotes(modifiedNotes, sourcePosition);
}
    private void ApplyRootShift(NoteSet noteSet, Vector3 sourcePosition)
{
    int shift = Random.Range(-3, 4); // ±3 semitones
    noteSet.ShiftRoot(this, shift);

    var newScaleNotes = noteSet.GetNoteList();
    var modifiedNotes = new List<(int, int, int, float)>();

    for (int i = 0; i < persistentLoopNotes.Count; i++)
    {
        var (step, oldNote, duration, velocity) = persistentLoopNotes[i];
        int newNote = noteSet.GetClosestVoiceLeadingNote(oldNote, newScaleNotes);
        modifiedNotes.Add((step, newNote, duration, velocity));
    }

    RebuildLoopFromModifiedNotes(modifiedNotes, sourcePosition);
}
    private void ApplyNoteBehaviorChange(NoteSet noteSet, Vector3 sourcePosition)
{
    var values = Enum.GetValues(typeof(NoteBehavior)).Cast<NoteBehavior>().ToList();
    values.Remove(noteSet.noteBehavior);
    NoteBehavior newBehavior = values[Random.Range(0, values.Count)];

    noteSet.ChangeNoteBehavior(this, newBehavior);

    var modifiedNotes = new List<(int, int, int, float)>();

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

        modifiedNotes.Add((step, note, newDuration, velocity));
    }

    RebuildLoopFromModifiedNotes(modifiedNotes, sourcePosition);
}
    private int CollectNote(int stepIndex, int note, int durationTicks, float force)
    {
        AddNoteToLoop(stepIndex, note, durationTicks, force);
        PlayNote(note, durationTicks, force);
        return stepIndex;
    }
    private IEnumerator ResetPitchBendAfterDelay(float delay)
    {
        yield return new WaitForSeconds(delay);
        midiStreamPlayer.MPTK_Channels[channel].fluid_channel_pitch_bend(8192); // Center
    }
    private int GetCurrentStep()
    {
        if (drumTrack?.drumAudioSource == null) return -1;

        float elapsedTime = (float)(AudioSettings.dspTime - drumTrack.startDspTime);
        float stepDuration = GetTrackLoopDurationInSeconds() / _totalSteps;
        int step = Mathf.FloorToInt(elapsedTime / stepDuration) % _totalSteps;

        return step;
    }
}
