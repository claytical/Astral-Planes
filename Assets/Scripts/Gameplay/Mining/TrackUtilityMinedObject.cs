using System.Linq;
using UnityEngine;
public enum TrackModifierType
{
    Expansion, AntiNote, Clear, Drift, Remix,
    Contract, Solo, Magic,
    StructureShift, MoodShift
}

public class TrackUtilityMinedObject : MonoBehaviour
{
    public TrackModifierType type;
    public MusicalRole targetRole;
    
    public void Initialize(InstrumentTrack track)
    {
        GetComponent<MinedObject>().AssignTrack(track);

        // Setup lifetime
        var explode = GetComponent<Explode>();
        if (explode != null)
        {
            explode.ApplyLifetimeProfile(LifetimeProfile.GetProfile(MinedObjectType.TrackUtility));
        }
    }

    public void OnCollected()
    {
        InstrumentTrack assignedTrack = GetComponent<MinedObject>().assignedTrack;

        if (assignedTrack == null)
        {
            Debug.LogWarning("No track assigned to utility item.");
            return;
        }

        switch (type)
        {
            case TrackModifierType.Expansion:
                assignedTrack.ExpandLoop();
                break;
            case TrackModifierType.Contract:
                assignedTrack.ContractLoop();
                break;
            case TrackModifierType.Clear:
                assignedTrack.ClearLoopedNotes();
                break;
            case TrackModifierType.Drift:
                if (assignedTrack.drumTrack != null)
                {
                    var driftoneManager = assignedTrack.drumTrack.driftoneManager;
                    if (driftoneManager != null && !driftoneManager.isDriftoneActive)
                    {
                        driftoneManager.BeginDriftone(assignedTrack.drumTrack.trackController.tracks.ToList());
                    }
                }
                break;
            case TrackModifierType.Remix:
                RemixTrack(assignedTrack);
                break;
            case  TrackModifierType.Solo:
                SoloTrack(assignedTrack);
                break;
            case  TrackModifierType.Magic:
                ExecuteSmartUtility(assignedTrack);
                break;
            case  TrackModifierType.StructureShift:
                ApplySmartStructureShift(assignedTrack);
                break;
            case  TrackModifierType.MoodShift:
                ApplySmartMoodShift(assignedTrack);
                break;

        }

        Debug.Log($"Executed {type} on track: {assignedTrack.name}");
        Explode explode = GetComponent<Explode>();
        if (explode != null)
        {
            explode.Permanent();            
        }
        else
        {
            Destroy(gameObject);
            
        }
        
    }
    private void ApplySmartStructureShift(InstrumentTrack track)
    {
        if (track.loopMultiplier < track.maxLoopMultiplier && track.GetNoteDensity() >= 4)
        {
            track.ExpandLoop();
            Debug.Log("Smart Structure: Expand");
        }
        else if (track.loopMultiplier > 1 && track.GetNoteDensity() < 4)
        {
            track.ContractLoop();
            Debug.Log("Smart Structure: Contract");
        }
        else
        {
            track.ClearLoopedNotes();
            Debug.Log("Smart Structure: Clear Track");
        }
    }
    private void ApplySmartMoodShift(InstrumentTrack track)
    {
        
        var controller = track.controller;
        var drumTrack = track.drumTrack;
        var tracks = controller.tracks;
    if (track == null)
    {
        Debug.LogError("❌ ApplySmartMoodShift called with null track!");
        return;
    }

    var noteSet = track.GetCurrentNoteSet();
    if (noteSet == null)
    {
        Debug.LogError($"❌ Track '{track.name}' has no current NoteSet assigned.");
        return;
    }
        int activeTracks = tracks.Count(t => t.GetNoteDensity() > 0);

        if (activeTracks == 1)
        {
            foreach (var t in tracks)
            {
                drumTrack.driftoneManager.ApplyDriftToTrack(t); // ← Your drift logic
            }
            Debug.Log("Smart Mood: Drift applied to all tracks");
        }
        else if (track.GetNoteDensity() <= 2)
        {
            RemixTrack(track);
            Debug.Log("Smart Mood: Remix");
        }
        else
        {
            foreach (var t in tracks)
            {
                if (t != track) t.ClearLoopedNotes();
            }
            Debug.Log("Smart Mood: Solo");
        }

        controller.UpdateVisualizer();
    }

    private void ExecuteSmartUtility(InstrumentTrack assignedTrack)
    {
        var controller = assignedTrack.controller;
        var tracks = controller.tracks;
        //var phase = assignedTrack.drumTrack.currentPhase;

        // Analyze current state
        int sparseCount = tracks.Count(t => t.GetNoteDensity() <= 1);
        int denseCount = tracks.Count(t => t.GetNoteDensity() >= 6);

        if (sparseCount > 0)
        {
            RemixTrack(assignedTrack); return;
        }

        if (denseCount > 0 && assignedTrack.GetNoteDensity() >= 6)
        {
            ContractLoop(assignedTrack); return;
        }

        if (Random.value < 0.7f)
        {
            SoloTrack(assignedTrack); return;
        }

        // Default fallback
        DriftTrack(assignedTrack);
    }
    private void ContractLoop(InstrumentTrack track)
    {
        track.ContractLoop();
    }

    private void SoloTrack(InstrumentTrack keepTrack)
    {
        foreach (var track in keepTrack.controller.tracks)
        {
            if (track != keepTrack)
                track.ClearLoopedNotes();
        }
    }

    private void DriftTrack(InstrumentTrack track)
    {
        track.PerformSmartNoteModification();
    }
    private void RemixTrack(InstrumentTrack track)
    {
        track.ClearLoopedNotes();

        var noteSet = track.GetCurrentNoteSet();
        if (noteSet == null) return;

        var phase = track.drumTrack.currentPhase;
        var profile = MusicalRoleProfileLibrary.GetProfile(track.assignedRole);
        noteSet.noteBehavior = profile.defaultBehavior;

        // Assign role+phase-specific behaviors
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

        var steps = noteSet.GetStepList();
        var pitches = noteSet.GetNoteList();
        if (steps.Count == 0 || pitches.Count == 0) return;

        for (int i = 0; i < Mathf.Min(steps.Count, 6); i++)
        {
            int step = steps[i];
            int note = noteSet.GetNextArpeggiatedNote(step);
            int duration = track.CalculateNoteDuration(step, noteSet);
            float velocity = Random.Range(60f, 100f);
            track.GetPersistentLoopNotes().Add((step, note, duration, velocity));
        }

        track.controller.UpdateVisualizer();

        Debug.Log($"🎛 Remix applied to {track.name} ({track.assignedRole}, Phase: {phase})");
    }
    
}