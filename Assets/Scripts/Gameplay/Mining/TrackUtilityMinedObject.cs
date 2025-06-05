using System.Linq;
using UnityEngine;
public enum TrackModifierType
{
    Expansion, AntiNote, Clear, Drift, Remix,
    Contract, Solo, Magic,
    StructureShift, MoodShift,
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
    public void Initialize(InstrumentTrack track)
    {
        GetComponent<MinedObject>().AssignTrack(track);

        var explode = GetComponent<Explode>();
        if (explode != null)
        {
            explode.ApplyLifetimeProfile(LifetimeProfile.GetProfile(MinedObjectType.TrackUtility));
        }
    }

    public void OnCollected()
    {
        assignedTrack = GetComponent<MinedObject>().assignedTrack;
        if (assignedTrack == null)
        {
            Debug.LogWarning("No track assigned to utility item.");
            return;
        }

        switch (type)
        {
            case TrackModifierType.ChordProgression:
                ApplyChordProgressionToTrack(); break;
            case TrackModifierType.Expansion:
                assignedTrack.ExpandLoop(); break;
            case TrackModifierType.Contract:
                assignedTrack.ContractLoop(); break;
            case TrackModifierType.Clear:
                assignedTrack.ClearLoopedNotes(); break;
            case TrackModifierType.Drift:
                ApplyDrift(assignedTrack); break;
            case TrackModifierType.Remix:
                RemixTrack(assignedTrack); break;
            case TrackModifierType.Solo:
                SoloTrackWithBassSupport(assignedTrack); break;
            case TrackModifierType.Magic:
                ExecuteSmartUtility(assignedTrack); break;
            case TrackModifierType.StructureShift:
                ApplySmartStructureShift(assignedTrack); break;
            case TrackModifierType.MoodShift:
                ApplySmartMoodShift(assignedTrack); break;
        }

        Debug.Log($"Executed {type} on track: {assignedTrack.name}");
        GetComponent<Explode>()?.Permanent();
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
                track.ClearLoopedNotes();
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

        assignedTrack.ClearLoopedNotes();
        assignedTrack.ApplyChordProgression(chordProfile);

        Debug.Log($"🎵 Overwrote {assignedTrack.assignedRole} with progression {chordProfile.name}");
    }

    private void RemixTrack(InstrumentTrack track)
    {
        track.ClearLoopedNotes();
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
            track.ClearLoopedNotes();
        }
    }

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
                if (t != track) t.ClearLoopedNotes();
        }

        controller.UpdateVisualizer();
    }
}
