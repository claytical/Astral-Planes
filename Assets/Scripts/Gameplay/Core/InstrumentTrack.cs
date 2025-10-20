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
    private int _currentBurstRemaining = 0;
    private bool _currentBurstArmed = false;
    private NoteSet _currentNoteSet;
    private Boundaries _boundaries;
    private List<(int stepIndex, int note, int duration, float velocity)> persistentLoopNotes = new List<(int, int, int, float)>();
    List<GameObject> _spawnedNotes = new();
    private int _totalSteps = 16, _lastStep = -1;
    [SerializeField] private LayerMask cosmicDustLayer;   // set this in the Inspector to your Dust layer
    [SerializeField] private float dustCheckRadius = 0.2f;
    private int _nextBurstId = 0;
    private readonly Dictionary<int,int> _burstRemaining = new(); // burstId -> remaining
    private bool _pendingCollapse;
    private int  _collapseTargetMultiplier = 1;
    private bool _hookedBoundaryForCollapse;
    private bool _pendingExpandForBurst;
    private bool _expandCommitted;    
    private int  _oldTotalAtExpand;
    private int  _halfOffsetAtExpand;
    private bool _mapIncomingCollectionsToSecondHalf;
    private bool _hookedBoundaryForExpand;
    private int _pendingMapIntoSecondHalfCount = 0;   // how many pickups to offset
    private float _pendingMapTimeout = 0f;            // safety timeout (seconds)
    private bool _ascendQueued;
    private readonly Dictionary<int, HashSet<int>> _burstSteps = new(); // burstId -> steps for that burst
    private int? _pendingLoopMultiplier;   // supports expand or collapse
    private readonly List<int> _pendingAscendRemovals = new();
    public int currentBurstId;
    public int LastExpandOldTotal { get; private set; } = 0;
    private float BaseLoopSeconds() => drumTrack != null ? drumTrack.GetLoopLengthInSeconds() : 0f;
    private int   LeaderMultiplier() => Mathf.Max(1, controller?.GetMaxLoopMultiplier() ?? 1);
    private int   MyMultiplier()     => Mathf.Max(1, loopMultiplier);
    
    private float TimeSinceStart() =>
        drumTrack != null ? (float)(AudioSettings.dspTime - drumTrack.startDspTime) : 0f;
    private float LeaderLengthSec() =>
        BaseLoopSeconds() * LeaderMultiplier();
    private float TimeInLeader() {
        float L = LeaderLengthSec();
        if (L <= 0f) return 0f;
        float t = TimeSinceStart();
        return t % L;
    }
    private float RemainingActiveWindowSec() {
        float my  = BaseLoopSeconds() * MyMultiplier();
        float L   = LeaderLengthSec();
        if (L <= 0f) return float.MaxValue;
        float tin = TimeInLeader();
        return Mathf.Max(0f, my - tin);
    }
    private int ComputeTargetMultiplierFromUsage()
{
    if (persistentLoopNotes == null || persistentLoopNotes.Count == 0) return 1;

    int drumSteps = drumTrack != null ? drumTrack.totalSteps : 16;
    int maxUsedStep = 0;
    foreach (var (step, _, _, _) in persistentLoopNotes)
        if (step > maxUsedStep) maxUsedStep = step;

    // required segments of base loop to cover maxUsedStep (0-based -> +1)
    int requiredSegments = Mathf.CeilToInt((maxUsedStep + 1) / (float)drumSteps);

    // clamp to powers of two not exceeding current multiplier
    int target = 1;
    while (target < requiredSegments) target <<= 1;

    // never grow, only shrink
    target = Mathf.Min(target, Mathf.Max(1, loopMultiplier));
    return Mathf.Max(1, target);
}

    private void EvaluateAndQueueCollapseIfPossible()
{
    if (drumTrack == null) return;
    if (loopMultiplier <= 1) return;

    int target = ComputeTargetMultiplierFromUsage();
    if (target < loopMultiplier)
        QueueCollapseTo(target);
}

    private void QueueCollapseTo(int newMultiplier)
{
    newMultiplier = Mathf.Clamp(newMultiplier, 1, loopMultiplier);
    if (newMultiplier == loopMultiplier) return;

    _pendingCollapse = true;
    _collapseTargetMultiplier = newMultiplier;
    HookCollapseBoundary();
}

    private void HookCollapseBoundary()
{
    if (_hookedBoundaryForCollapse || drumTrack == null) return;
    drumTrack.OnLoopBoundary += OnDrumDownbeat_CommitCollapse;
    _hookedBoundaryForCollapse = true;
}

    private void UnhookCollapseBoundary()
{
    if (!_hookedBoundaryForCollapse || drumTrack == null) return;
    drumTrack.OnLoopBoundary -= OnDrumDownbeat_CommitCollapse;
    _hookedBoundaryForCollapse = false;
}

    public bool TryGetSplitLayout(out int leftHalfWidth)
    {
        // Prefer the committed value; fall back to the pending snapshot.
        int candidate = (LastExpandOldTotal > 0) ? LastExpandOldTotal : _oldTotalAtExpand;
        if (candidate > 0 && _totalSteps == candidate * 2)
        {
            leftHalfWidth = candidate;
            return true;
        }
        leftHalfWidth = 0;
        return false;
    }

    private void OnDrumDownbeat_CommitCollapse()
{
    if (!_pendingCollapse) { UnhookCollapseBoundary(); return; }

    int newMult = Mathf.Clamp(_collapseTargetMultiplier, 1, loopMultiplier);
    if (newMult != loopMultiplier)
    {
        loopMultiplier = newMult;
        _totalSteps = (drumTrack != null ? drumTrack.totalSteps : 16) * loopMultiplier;
        persistentLoopNotes.RemoveAll(t => t.stepIndex >= _totalSteps);
        // Refresh visuals to reflect the narrower audible window
        controller?.UpdateVisualizer();
        controller?.noteVisualizer?.RecomputeTrackLayout(this);
    }

    _pendingCollapse = false;
    UnhookCollapseBoundary();
    _lastStep = -1;
}

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
        if (_mapIncomingCollectionsToSecondHalf && _pendingMapTimeout > 0f)
        {
            _pendingMapTimeout -= Time.deltaTime;
            if (_pendingMapTimeout <= 0f) {
                _mapIncomingCollectionsToSecondHalf = false;
                _pendingMapIntoSecondHalfCount = 0;
            }
        }
        float elapsedTime  = TimeSinceStart();
        float stepDuration = GetTrackLoopDurationInSeconds() / _totalSteps;
        int   localStep    = Mathf.FloorToInt(elapsedTime / stepDuration) % _totalSteps;

        if (localStep != _lastStep) {
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

    private void RegisterBurstStep(int burstId, int step)
    {
        if (!_burstSteps.TryGetValue(burstId, out var set))
            _burstSteps[burstId] = set = new HashSet<int>();
        set.Add(step);
    }
    public void RemoveNotesForBurst(int burstId)
    {
        if (!_burstSteps.TryGetValue(burstId, out var set) || set.Count == 0) return;

        // remove loop notes whose step is in this burst
        persistentLoopNotes.RemoveAll(tuple => set.Contains(tuple.stepIndex));

        _burstSteps.Remove(burstId);
        // optional: if you want the UI to immediately reflect the removed notes:
        controller?.UpdateVisualizer();
        EvaluateAndQueueCollapseIfPossible();
    }

    public void OnCollectableCollected(Collectable collectable, int reportedStep, int durationTicks, float force)
{
    if (collectable == null || collectable.assignedInstrumentTrack != this) return;

    // 1) Free the vacated grid cell (defensive)
    if (drumTrack != null) {
        Vector2Int gridPos = drumTrack.WorldToGridPosition(collectable.transform.position);
        drumTrack.FreeSpawnCell(gridPos.x, gridPos.y);
        drumTrack.ResetSpawnCellBehavior(gridPos.x, gridPos.y);
    }

    // 2) Determine base step and whether we're in a mapped (expanded) burst
    int baseStep = (collectable.intendedStep >= 0) ? collectable.intendedStep : GetCurrentStep();
    bool expandedBurstActive =
        _expandCommitted && // <— now use the committed flag
        _oldTotalAtExpand > 0 &&
        _totalSteps == _oldTotalAtExpand * 2;
    
    // 3) Compute the final target step (second half if mapping is armed)
    int targetStep = expandedBurstActive
        ? (_halfOffsetAtExpand + (baseStep % _oldTotalAtExpand)) % _totalSteps
        : baseStep;

    // 4) Model: add the note to the loop
    int note = collectable.GetNote();
    if (collectable.IsDark) {
        AddNoteToLoop(targetStep, note, durationTicks, force);
        PlayDarkNote(note, durationTicks, force);
        RecalculatePerfectionForCurrentNoteSet();
    } else {
        CollectNote(targetStep, note, durationTicks, force); // your normal bright path (adds to loop, etc.)
    }

    // 5) Visuals: LIGHT the existing ping (do NOT create grey, do NOT attach tether here)
    LightMarkerAt(targetStep);
    RegisterBurstStep(collectable.burstId, targetStep); 
    // --- REGISTER the lit marker to this collectable's burst ---
    if (controller?.noteVisualizer != null)
    {
        // grab the lit marker we just illuminated at (track, step)
         if (controller.noteVisualizer.noteMarkers.TryGetValue((this, targetStep), out var t) && t != null) { 
             controller.noteVisualizer.RegisterCollectedMarker(this, collectable.burstId, targetStep, t.gameObject);
         }
    }

// --- DECREMENT only this collectable's burst and trigger rise when it finishes ---
    if (collectable.burstId != 0 && _burstRemaining.TryGetValue(collectable.burstId, out var rem))
    {
        rem--;
        if (rem <= 0)
        {
            _burstRemaining.Remove(collectable.burstId);

            // rise takes 16 drum loops, this should vary based on track and phase
            float seconds = Mathf.Max(0.0001f, drumTrack.GetLoopLengthInSeconds() * 16f);
            controller?.noteVisualizer?.TriggerBurstAscend(this, collectable.burstId, seconds);
        }
        else
        {
            _burstRemaining[collectable.burstId] = rem;
        }
    }

    // 6) Mapping book-keeping: decrement counter or time-out; prefer ONE mechanism
    if (expandedBurstActive && _pendingMapIntoSecondHalfCount > 0) {
        _pendingMapIntoSecondHalfCount--;
        if (_pendingMapIntoSecondHalfCount == 0)
            _mapIncomingCollectionsToSecondHalf = false;
    }


    // 7) Animate the pickup to the ribbon and finalize
    collectable.TravelAlongTetherAndFinalize(durationTicks, force, seconds: 1f);

    // 8) List hygiene
    spawnedCollectables?.Remove(collectable.gameObject);
    if (_currentBurstArmed && _currentBurstRemaining > 0)
    {
        _currentBurstRemaining--;
        if (_currentBurstRemaining == 0)
        {
            _currentBurstArmed = false;           // disarm first to avoid re-entrancy
        }
    }

}

    public void SoftReplaceLoop(IReadOnlyList<(int stepIndex, int note, int duration, float velocity)> newNotes)
    {
        // Clear data + our own spawned visuals (quietly)
        persistentLoopNotes.Clear();
        _spawnedNotes.Clear();

        // Rebuild notes + visuals quietly
        if (newNotes != null)
        {
            for (int i = 0; i < newNotes.Count; i++)
            {
                var t = newNotes[i];
                // Reuse your normal add path so markers & bookkeeping stay consistent
                AddNoteToLoop(t.stepIndex, t.note, t.duration, t.velocity);
            }
        }

        // IMPORTANT: do NOT call QueueCollapseTo(1) and do NOT call any blast/ascend visuals here.
    }

    public void SetMuted(bool muted)
    {
        // Route to your synth/MIDI/FMOD layer
        // e.g., midiOut.SetTrackGain(trackIndex, muted ? -80f : 0f);
    }
    public bool HasNoteSet()
    {
        return _currentNoteSet != null;
    }
    public void SetNoteSet(NoteSet noteSet)
    {
        _currentNoteSet = noteSet;
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
            if (storedStep == localStep) PlayNote(note, duration, velocity);
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
        ResetPerfectionFlag();
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
        QueueCollapseTo(1);
    }
    public void PlayNote(int note, int durationTicks, float velocity)
    {
        if (drumTrack == null || drumTrack.drumLoopBPM <= 0)
        {
            Debug.LogError("Drum track is not initialized or has an invalid BPM.");
            return;
        }

        // Convert ticks → ms
        int durationMs = Mathf.RoundToInt(durationTicks * (60000f / (drumTrack.drumLoopBPM * 480f)));

        // 🔑 Trim to the remaining audible window this cycle
        float remainSec = RemainingActiveWindowSec();
        if (!float.IsPositiveInfinity(remainSec))
        {
            int maxMs = Mathf.Max(10, Mathf.FloorToInt(remainSec * 1000f));
            durationMs = Mathf.Min(durationMs, maxMs);
        }

        midiStreamPlayer.MPTK_Channels[channel].ForcedPreset = preset;
        midiStreamPlayer.MPTK_Channels[channel].ForcedBank   = bank;

        var noteOn = new MPTKEvent {
            Command  = MPTKCommand.NoteOn,
            Value    = note,
            Channel  = channel,
            Duration = durationMs,
            Velocity = (int)velocity,
        };
        midiStreamPlayer.MPTK_PlayEvent(noteOn);
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

        string[] options;
        Debug.Log($"Assessing options for {_currentNoteSet}");

        switch (GameFlowManager.Instance.phaseTransitionManager.currentPhase)
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
        //controller.noteVisualizer.SyncTiledClonesForTrack(this);
    }
    public bool IsPerfectThisPhase { get; private set; }
    private void PlayDarkNote(int note, int duration, float velocity)
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
        //controller.noteVisualizer.SyncTiledClonesForTrack(this);
    }
    public int GetNoteDensity()
    {
        return persistentLoopNotes.Count;
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

        GameObject noteMarker = null;
        // 🔑 Reuse any existing placeholder marker at (track, step)
        if (controller?.noteVisualizer != null &&
            controller.noteVisualizer.noteMarkers != null &&
            controller.noteVisualizer.noteMarkers.TryGetValue((this, stepIndex), out var t) &&
            t != null)
        {
            noteMarker = t.gameObject;

            // Flip placeholder → lit
            var tag = noteMarker.GetComponent<MarkerTag>() ?? noteMarker.AddComponent<MarkerTag>();
            tag.track = this;
            tag.step = stepIndex;
            tag.isPlaceholder = false;

            var vnm = noteMarker.GetComponent<VisualNoteMarker>();
            if (vnm != null) vnm.Initialize(trackColor);

            var ml = noteMarker.GetComponent<MarkerLight>() ?? noteMarker.AddComponent<MarkerLight>();
            ml.LightUp(trackColor);
        }
        else
        {
            // Fallback: create a new lit marker if none existed
            noteMarker = controller?.noteVisualizer?.PlacePersistentNoteMarker(this, stepIndex, lit: true, burstId:-1);
        }

        if (noteMarker != null) _spawnedNotes.Add(noteMarker);

        controller?.UpdateVisualizer();
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
    private void SpawnCollectableBurst(NoteSet noteSet, int maxToSpawn = -1)
{
    
    if (noteSet == null || collectablePrefab == null || controller?.noteVisualizer == null) return;
    if (_currentNoteSet != noteSet)
    {
        SetNoteSet(noteSet);
    }
    int burstId = ++_nextBurstId;
    currentBurstId = burstId;
    int count = 0;

    var nv = controller.noteVisualizer;
    var stepList  = noteSet.GetStepList();
    var noteList  = noteSet.GetNoteList();
    if (stepList == null || stepList.Count == 0 || noteList == null || noteList.Count == 0) return; 
    _currentBurstRemaining = 0; 
    _currentBurstArmed     = true;
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
            var explosion = go.GetComponent<Explode>();
            if (explosion != null)
            {
                explosion.Permanent(false);
            }

            if (!go) break;

            if (go.TryGetComponent(out Collectable c))
            {
                // init like before
                c.energySprite.color = trackColor;
                c.Initialize(note, dur, this, noteSet, stepList);
                c.burstId = burstId;              // <-- add this public field to Collectable (int)
                count++;
                c.intendedStep = step;
                int markerStep = step;
                // if this burst was staged to expand, place pings in the NEW half
                if (_mapIncomingCollectionsToSecondHalf && _oldTotalAtExpand > 0 && _totalSteps == _oldTotalAtExpand * 2)
                     markerStep = (_halfOffsetAtExpand + (step % _oldTotalAtExpand)) % _totalSteps;
                
                 // create a **grey** placeholder at the (possibly offset) markerStep
                 var markerGO = nv.PlacePersistentNoteMarker(this, markerStep, lit: false, burstId);
                 if (markerGO != null)
                 {
                     var tag = markerGO.GetComponent<MarkerTag>() ?? markerGO.AddComponent<MarkerTag>();
                     tag.track = this;
                     tag.step = markerStep;
                     tag.burstId = burstId;
                     tag.isPlaceholder = true;

                     var ml = markerGO.GetComponent<MarkerLight>() ?? markerGO.AddComponent<MarkerLight>();
                     ml.SetGrey(new Color(1f,1f,1f,0.25f));

                     c.AttachTetherAtSpawn(markerGO.transform, nv.noteTetherPrefab, trackColor, dur);
                 }

                drumTrack.OccupySpawnGridCell(gp.x, gp.y, GridObjectType.Note);
                spawnedCollectables.Add(go); 
                var collectable = go.GetComponent<Collectable>(); 
                if (collectable != null) { 
                    _currentBurstRemaining++; 
                    // subscribe once; OnDestroyed fires on OnDestroy/OnDisable (pool-safe)
                    //collectable.OnDestroyed += HandleBurstCollectableDestroyedOnce; 
                }
                placed = true;
                spawned++;
            }
            if (placed) break; // ✅ only break after successful placement
        }
    } 
    if (spawned == 0) { 
        _currentBurstArmed = false; 
        _currentBurstRemaining = 0; 
    }
    _burstRemaining[burstId] = count;
    controller?.noteVisualizer?.CanonicalizeTrackMarkers(this, currentBurstId);
    controller?.noteVisualizer?.DestroyOrphanRowMarkers(this);
    controller?.noteVisualizer?.RecomputeTrackLayout(this); 
}
    public void SpawnCollectableBurstWithExpansionIfNeeded(NoteSet noteSet, int maxToSpawn = -1)
    {
        Debug.Log($"[ExpandStage] {name}: hasLoop={persistentLoopNotes.Count>0}, canExpand={loopMultiplier<maxLoopMultiplier}");

        bool hasLoopAlready = persistentLoopNotes != null && persistentLoopNotes.Count > 0;
        bool canExpand      = loopMultiplier < maxLoopMultiplier;
        if (hasLoopAlready && canExpand && drumTrack != null)
        {
            // 1) stage expansion
            _pendingExpandForBurst = true;
            _oldTotalAtExpand      = _totalSteps;
            _halfOffsetAtExpand    = _oldTotalAtExpand;
            _pendingMapIntoSecondHalfCount = Mathf.Max(1, noteSet.GetStepList().Count);
            _pendingMapTimeout = Mathf.Max(0.5f, drumTrack.GetLoopLengthInSeconds()); // 1 drum loop
            _mapIncomingCollectionsToSecondHalf = true;
            _expandCommitted = false;

            HookExpandBoundary();
            StartCoroutine(SpawnBurstAfterExpand(noteSet, maxToSpawn));
        }
        else
        {
            // No expansion needed — just spawn now
            SpawnCollectableBurst(noteSet, maxToSpawn);
        }
    }
    private IEnumerator SpawnBurstAfterExpand(NoteSet noteSet, int maxToSpawn)
    {
        // Wait until OnDrumDownbeat_CommitExpand actually runs
        while (!_expandCommitted) yield return null;

        // One extra frame so visual width/step maps have updated
        yield return null;

        SpawnCollectableBurst(noteSet, maxToSpawn);
    }
    private void HookExpandBoundary()
    {
        if (_hookedBoundaryForExpand || drumTrack == null) return;
        drumTrack.OnLoopBoundary += OnDrumDownbeat_CommitExpand;
        _hookedBoundaryForExpand = true;
    }
    void DumpTrackMarkers(InstrumentTrack track, string label)
    {
        var nv = controller.noteVisualizer;
        Debug.Log($"--- Markers for {track.name} @ {label} ---");
        int idx = 0;
        foreach (var kv in nv.noteMarkers)
        {
            if (kv.Key.Item1 != track) continue;
            var tf = kv.Value;
            var tag = tf ? tf.GetComponent<MarkerTag>() : null;
            var pos = tf ? tf.position : Vector3.zero;
            Debug.Log($"{idx++:00}) step={kv.Key.Item2}  burstId={(tag? tag.burstId : int.MinValue)}  placeholder={(tag? tag.isPlaceholder : false)}  y={pos.y:F1}");
        }
    }

    private void UnhookExpandBoundary()
    {
        if (!_hookedBoundaryForExpand || drumTrack == null) return;
        drumTrack.OnLoopBoundary -= OnDrumDownbeat_CommitExpand;
        _hookedBoundaryForExpand = false;
    }
  private void OnDrumDownbeat_CommitExpand()
{
    if (!_pendingExpandForBurst) { UnhookExpandBoundary(); return; }

    // A) Snapshot old totals
    LastExpandOldTotal = _totalSteps;
    _oldTotalAtExpand = _totalSteps;
    int oldLeaderSteps = controller != null ? controller.GetMaxLoopMultiplier() * drumTrack.totalSteps : _totalSteps;

    // B) Double the track length
    loopMultiplier *= 2;
    _totalSteps = drumTrack.totalSteps * loopMultiplier;

    // C) Arm mapping so the upcoming burst lands in the new half
    _halfOffsetAtExpand                 = _oldTotalAtExpand;      // left-half width
    _mapIncomingCollectionsToSecondHalf = true;
    _pendingExpandForBurst              = false;
    _expandCommitted                    = true;

    // D) Safety: ensure pre-expand notes remain in left half
    //    (If any helper “stretched” steps, snap them back.)
    for (int i = 0; i < persistentLoopNotes.Count; i++)
    {
        var (step, note, dur, vel) = persistentLoopNotes[i];
        if (step >= _oldTotalAtExpand) {
            step %= _oldTotalAtExpand;
            persistentLoopNotes[i] = (step, note, dur, vel);
        }
    }

    // E) Visuals: refresh markers & padding for THIS track
    controller.UpdateVisualizer(); // rebuilds from loop (lit, burstId:-1)
    controller.noteVisualizer.CanonicalizeTrackMarkers(this, currentBurstId);
    controller.noteVisualizer.MarkGhostPadding(this, _oldTotalAtExpand, _totalSteps - _oldTotalAtExpand);
    controller.noteVisualizer.RecomputeTrackLayout(this);
    // F) If this track is now the leader, relayout ALL tracks (their localFraction changed)
    int newLeaderSteps = 1;
    if (controller != null && controller.tracks != null)
    {
        newLeaderSteps = 0;
        foreach (var t in controller.tracks)
            if (t != null) newLeaderSteps = Mathf.Max(newLeaderSteps, t.GetTotalSteps());
    }
    if (newLeaderSteps != oldLeaderSteps && controller != null && controller.tracks != null)
    {
        foreach (var t in controller.tracks)
            if (t != null)
            {
                controller.noteVisualizer.CanonicalizeTrackMarkers(this, currentBurstId);
                controller.noteVisualizer.RecomputeTrackLayout(t);
            }
    }

    // G) Reset step cache so step-edge logic can’t skip
    _lastStep = -1;

    UnhookExpandBoundary();

#if UNITY_EDITOR
    Debug.Log($"[Expand] {name}: oldSteps={_oldTotalAtExpand} -> newSteps={_totalSteps} | " +
              $"oldLeader={oldLeaderSteps} newLeader={newLeaderSteps} | " +
              $"mapSecondHalfFrom={_halfOffsetAtExpand}");
#endif
    DumpTrackMarkers(this, "Expand Burst " + currentBurstId.ToString());
}

    private void LightMarkerAt(int step)
    {
        var nv = controller?.noteVisualizer;
        if (nv == null) return;

        // Prefer existing ping at (track, step)
        if (!nv.noteMarkers.TryGetValue((this, step), out var t) || t == null)
        {
            // Fallback: place a lit marker under the correct row (rare if spawn path failed)
            var go = nv.PlacePersistentNoteMarker(this, step, lit: true);
            
            if (go == null) return;
            t = go.transform;
        }
        var tag = t.GetComponent<MarkerTag>() ?? t.gameObject.AddComponent<MarkerTag>(); 
        tag.isPlaceholder = false;
        tag.burstId = -1;
        // Ensure it is colored and emitting now
        var vnm = t.GetComponent<VisualNoteMarker>();
        if (vnm != null) vnm.Initialize(trackColor);

        var light = t.GetComponent<MarkerLight>();
        if (light != null) light.LightUp(trackColor);
    }
    private void ResetPerfectionFlag() => IsPerfectThisPhase = false;
    private void RecalculatePerfectionForCurrentNoteSet()
    {
        var noteSet = _currentNoteSet;
        if (noteSet == null)
        {
            IsPerfectThisPhase = false;
            return;
        }

        // Required steps for this track in the current phase
        var required = noteSet.GetStepList();
        if (required == null || required.Count == 0)
        {
            IsPerfectThisPhase = false;
            return;
        }

        // Steps we actually have in the loop now
        var have = new HashSet<int>(persistentLoopNotes.Select(n => n.stepIndex));

        // Perfect iff we’ve landed at least one note on every required step
        IsPerfectThisPhase = required.All(step => have.Contains(step));
    }
    private IEnumerator WaitForDrumTrackStartTime() {
        while (drumTrack == null || drumTrack.GetLoopLengthInSeconds() <= 0 || drumTrack.startDspTime == 0)
            yield return null;
        _totalSteps = drumTrack.totalSteps * loopMultiplier;

    }
    private float GetTrackLoopDurationInSeconds()
    {
        if (drumTrack == null) return 0f;

        // Base loop duration from drums (already tracks BPM / clip length)
        float baseLoop = drumTrack.GetLoopLengthInSeconds();

        // Match the way you scale visual & step math: total steps = drumSteps * loopMultiplier
        // To keep stepDuration = loopDuration / totalSteps consistent across multipliers,
        // scale the loop length by the same multiplier.
        int mult = Mathf.Max(1, loopMultiplier);
        return baseLoop * mult;
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
    private void RebuildLoopFromModifiedNotes(List<(int, int, int, float)> modified, Vector3 _)
    {
        persistentLoopNotes.Clear();
        foreach (var obj in _spawnedNotes) if (obj) Destroy(obj);
        _spawnedNotes.Clear();

        foreach (var (step, note, dur, vel) in modified)
            AddNoteToLoop(step, note, dur, vel); // <- this already places & registers the marker
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
        RecalculatePerfectionForCurrentNoteSet();
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
