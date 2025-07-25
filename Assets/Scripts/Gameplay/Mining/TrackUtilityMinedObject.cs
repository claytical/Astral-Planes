using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Random = UnityEngine.Random;
[Serializable]
public enum TrackModifierType
{
    Spawner, Clear, Remix,
    RootShift, RhythmStyle,
    ChordProgression
}
public enum TrackClearType
{
    EnergyRestore,
    Remix
}

public class TrackUtilityMinedObject : MonoBehaviour
{
    public TrackModifierType type;
    public MusicalRole targetRole;
    private InstrumentTrack assignedTrack;
    public ChordProgressionProfile chordProfile;
    private RemixUtility appliedRemix;

    public void Initialize(InstrumentTrack track)
    {
        GetComponent<MinedObject>().AssignTrack(track);
    }

    public void OnCollected(Vehicle vehicle)
    {
    }
    private void ApplyRemixStrategy(InstrumentTrack track)
    {
        if (track == null || track.drumTrack == null) return;

        MusicalPhase phase = track.drumTrack.currentPhase;
        var profile = MusicalPhaseLibrary.Get(phase);

        if (profile == null)
        {
            Debug.LogWarning($"No phase profile found for remix phase: {phase}");
            return;
        }

        var strategy = MusicalPhaseLibrary.GetGhostStrategyForRole(phase, track.assignedRole);

        if (strategy == null)
        {
            Debug.LogWarning($"No ghost strategy found for role: {track.assignedRole} in phase: {phase}");
            return;
        }

        NoteSet noteSet = track.GetCurrentNoteSet();
        if (noteSet == null) return;

        // Apply behavior and rhythm style aligned with the ghost strategy
        noteSet.Initialize(track.GetTotalSteps());
        switch (strategy)
        {
            case GhostPatternStrategy.StaticRoot:
                AddRootNotes(track, noteSet);
                break;
            case GhostPatternStrategy.WalkingBass:
                AddWalkingNotes(track, noteSet);
                break;
            case GhostPatternStrategy.MelodicPhrase:
                AddPhraseNotes(track, noteSet);
                break;
            case GhostPatternStrategy.PercussiveLoop:
                AddGrooveNotes(track, noteSet);
                break;
            case GhostPatternStrategy.Drone:
                AddSustainedNotes(track, noteSet);
                break;
            case GhostPatternStrategy.Randomized:
            default:
                AddRandomNotes(track, noteSet, 6);
                break;
        }

        track.controller.UpdateVisualizer();
    }
    private void AddRootNotes(InstrumentTrack track, NoteSet noteSet)
    {
        for (int i = 0; i < 4; i++)
        {
            int step = i * 4;
            int note = noteSet.GetRootNote();
            int duration = track.CalculateNoteDuration(step, noteSet);
            float velocity = Random.Range(60f, 90f);
            track.GetPersistentLoopNotes().Add((step, note, duration, velocity));
        }
    }

    private void OnTriggerEnter2D(Collider2D other)
    {
        Vehicle vehicle = other.GetComponent<Vehicle>();
        if(vehicle == null) return;
        CollectionSoundManager.Instance.PlayEffect(SoundEffectPreset.Aether);
        vehicle.AddRemixRole(targetRole);

        assignedTrack = GetComponent<MinedObject>().assignedTrack;
        if (assignedTrack == null)
        {
            Debug.LogWarning("No track assigned to utility item.");
            return;
        }
        
        switch (type)
        {
            case TrackModifierType.Remix:
            {
                break;
            }
            case TrackModifierType.ChordProgression:
                ApplyChordProgressionToTrack(); break;
            case TrackModifierType.Clear:
                assignedTrack.ClearLoopedNotes(TrackClearType.EnergyRestore); break;
            case TrackModifierType.RhythmStyle:
                ExecuteSmartUtility(assignedTrack); break;
            case TrackModifierType.RootShift:
                ApplySmartStructureShift(assignedTrack); break;
        }

        Debug.Log($"Executed {type} on track: {assignedTrack.name}");
        GetComponent<Explode>()?.Permanent();

    }

    public void ApplyRemixUtility(RemixUtility utility)
    {
        if (assignedTrack == null || utility == null)
        {
            Debug.LogWarning("❌ ApplyRemixUtility failed: missing assignedTrack or utility.");
            return;
        }

        NoteSet noteSet = assignedTrack.GetCurrentNoteSet();
        if (noteSet == null)
        {
            Debug.LogWarning("❌ ApplyRemixUtility: NoteSet is null.");
            return;
        }

        // Override settings only if told to do so
        if (utility.overrideDefaults)
        {
            noteSet.noteBehavior = utility.noteBehaviorOverride;
            noteSet.rhythmStyle = utility.rhythmStyleOverride;
        }

        noteSet.Initialize(assignedTrack.GetTotalSteps());

        // Step 1: Collect notes from current NoteSet
        List<int> collectedNotes = noteSet.GetNoteList();
        if (collectedNotes == null || collectedNotes.Count == 0)
        {
            Debug.LogWarning("⚠️ ApplyRemixUtility: NoteSet returned no collected notes.");
            return;
        }

        // Step 2: Clear current loop
        assignedTrack.ClearLoopedNotes(TrackClearType.Remix);

        // Step 3: Run Remix logic
        int totalSteps = assignedTrack.GetTotalSteps();
        var remixData = utility.Remix(collectedNotes, noteSet, totalSteps);

        // Step 4: Apply result
        var loop = assignedTrack.GetPersistentLoopNotes();
        foreach (var (step, note, duration, velocity) in remixData)
        {
            loop.Add((step, note, duration, velocity));
        }

        assignedTrack.controller?.UpdateVisualizer();
        Debug.Log($"🎛 Applied RemixUtility '{utility.name}' to track {assignedTrack.assignedRole} (steps: {remixData.Count})");
    }


    private void AddWalkingNotes(InstrumentTrack track, NoteSet noteSet)
    {
        for (int step = 0; step < track.GetTotalSteps(); step += 2)
        {
            int note = noteSet.GetNextWalkingNote(step);
            int duration = track.CalculateNoteDuration(step, noteSet);
            float velocity = Random.Range(50f, 80f);
            track.GetPersistentLoopNotes().Add((step, note, duration, velocity));
        }
    }

    private void AddPhraseNotes(InstrumentTrack track, NoteSet noteSet)
    {
        for (int step = 0; step < track.GetTotalSteps(); step += 3)
        {
            int note = noteSet.GetPhraseNote(step);
            int duration = track.CalculateNoteDuration(step, noteSet);
            float velocity = Random.Range(70f, 100f);
            track.GetPersistentLoopNotes().Add((step, note, duration, velocity));
        }
    }

    private void AddGrooveNotes(InstrumentTrack track, NoteSet noteSet)
    {
        for (int step = 0; step < track.GetTotalSteps(); step++)
        {
            if (noteSet.IsAccentStep(step))
            {
                int note = noteSet.GetGrooveNote(out _);
                int duration = track.CalculateNoteDuration(step, noteSet);
                float velocity = Random.Range(80f, 110f);
                track.GetPersistentLoopNotes().Add((step, note, duration, velocity));
            }
        }
    }

    private void AddSustainedNotes(InstrumentTrack track, NoteSet noteSet)
    {
        int totalSteps = track.GetTotalSteps();
        int note = noteSet.GetSustainedNote();
        track.GetPersistentLoopNotes().Add((0, note, totalSteps, 80f));
    }

    private void ApplyDrift(InstrumentTrack track)
    {
        track.PerformSmartNoteModification();
    }

    private void SoloTrackWithBassSupport(InstrumentTrack keepTrack)
    {
        foreach (var track in keepTrack.controller.tracks)
        {
            if (track != keepTrack && track.assignedRole != MusicalRole.Bass)
                track.ClearLoopedNotes(TrackClearType.Remix);
        }

        var bassTrack = keepTrack.controller.FindTrackByRole(MusicalRole.Bass);
        if (bassTrack != null && bassTrack.GetNoteDensity() < 3)
        {
            RemixTrack(bassTrack);
        }
    }
    private void ApplyChordProgressionToTrack()
    {
        if (chordProfile == null || assignedTrack == null) return;

        assignedTrack.ClearLoopedNotes(TrackClearType.Remix);
        assignedTrack.ApplyChordProgression(chordProfile);

        Debug.Log($"🎵 Overwrote {assignedTrack.assignedRole} with progression {chordProfile.name}");
    }

    private void RemixTrack(InstrumentTrack track)
    {
        track.ClearLoopedNotes(TrackClearType.Remix);
        var noteSet = track.GetCurrentNoteSet();
        if (noteSet == null) return;

        var profile = MusicalRoleProfileLibrary.GetProfile(track.assignedRole);
        var phase = track.drumTrack.currentPhase;

        noteSet.noteBehavior = profile.defaultBehavior;

        switch (profile.role)
        {
            case MusicalRole.Bass:
                noteSet.noteBehavior = NoteBehavior.Bass;
                noteSet.rhythmStyle = (phase == MusicalPhase.Pop) ? RhythmStyle.FourOnTheFloor : RhythmStyle.Sparse;
                break;
            case MusicalRole.Lead:
                noteSet.noteBehavior = NoteBehavior.Lead;
                noteSet.rhythmStyle = RhythmStyle.Syncopated;
                break;
            case MusicalRole.Harmony:
                noteSet.noteBehavior = NoteBehavior.Harmony;
                noteSet.chordPattern = (phase == MusicalPhase.Intensify) ? ChordPattern.Arpeggiated : ChordPattern.RootTriad;
                break;
            case MusicalRole.Groove:
                noteSet.noteBehavior = NoteBehavior.Percussion;
                noteSet.rhythmStyle = RhythmStyle.Dense;
                break;
        }

        noteSet.Initialize(track.GetTotalSteps());
        AddRandomNotes(track, noteSet, 6); // Main remix pass
        if (track.GetNoteDensity() < 4)
            AddRandomNotes(track, noteSet, 4 - track.GetNoteDensity()); // Fill in to avoid sparseness

        track.controller.UpdateVisualizer();
    }

    private void AddRandomNotes(InstrumentTrack track, NoteSet noteSet, int count)
    {
        var steps = noteSet.GetStepList().OrderBy(_ => Random.value).ToList();
        var notes = noteSet.GetNoteList();

        for (int i = 0; i < Mathf.Min(count, steps.Count); i++)
        {
            int step = steps[i];
            int note = noteSet.GetNextArpeggiatedNote(step);
            int duration = track.CalculateNoteDuration(step, noteSet);
            float velocity = Random.Range(60f, 100f);
            track.GetPersistentLoopNotes().Add((step, note, duration, velocity));
        }
    }

    private void RemixTrackLite(InstrumentTrack track)
    {
        var noteSet = track.GetCurrentNoteSet();
        if (noteSet == null) return;

        noteSet.Initialize(track.GetTotalSteps());
        AddRandomNotes(track, noteSet, 3);
    }

    private void ExecuteSmartUtility(InstrumentTrack track)
    {
        var tracks = track.controller.tracks;

        int sparseCount = tracks.Count(t => t.GetNoteDensity() <= 1);
        int denseCount = tracks.Count(t => t.GetNoteDensity() >= 6);

        if (sparseCount > 0)
        {
            RemixTrackLite(track); return;
        }

        if (denseCount > 0 && track.GetNoteDensity() >= 6)
        {
            track.ContractLoop(); return;
        }

        if (Random.value < 0.7f)
        {
            SoloTrackWithBassSupport(track); return;
        }

        ApplyDrift(track);
    }

    private void ApplySmartStructureShift(InstrumentTrack track)
    {
        if (track.loopMultiplier < track.maxLoopMultiplier && track.GetNoteDensity() >= 4)
        {
            track.ExpandLoop();
        }
        else if (track.loopMultiplier > 1 && track.GetNoteDensity() < 4)
        {
            track.ContractLoop();
        }
        else
        {
            track.ClearLoopedNotes(TrackClearType.Remix);
        }
    }
/*
    private void ApplySmartMoodShift(InstrumentTrack track)
    {
        var controller = track.controller;
        var drumTrack = track.drumTrack;
        var tracks = controller.tracks;
        var noteSet = track.GetCurrentNoteSet();

        int activeTracks = tracks.Count(t => t.GetNoteDensity() > 0);

        if (activeTracks == 1)
        {
//            foreach (var t in tracks)
//                drumTrack.driftoneManager.ApplyDriftToTrack(t);
        }
        else if (track.GetNoteDensity() <= 2)
        {
            RemixTrack(track);
        }
        else
        {
            foreach (var t in tracks)
                if (t != track) t.ClearLoopedNotes(TrackClearType.Remix);
        }

        controller.UpdateVisualizer();
    }
    */
}
