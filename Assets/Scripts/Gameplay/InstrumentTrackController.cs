using System;
using UnityEngine;
using System.Collections.Generic;
using System.Collections;
using System.Linq;
using Unity.Loading;
using UnityEngine.SceneManagement;
using Random = UnityEngine.Random;

public class InstrumentTrackController : MonoBehaviour
{
    public InstrumentTrack[] tracks;
    public InstrumentTrack activeTrack;
    public NoteVisualizer noteVisualizer;
    

    void Start()
    {
            if (!GamepadManager.Instance.ReadyToPlay())
            {
                return;
            }
    }
    public int GetMaxLoopMultiplier()
    {
        return tracks.Max(track => track.loopMultiplier);
    }

    public void UpdateVisualizer()
    {
       // noteVisualizer.DisplayNotes(tracks.ToList());
    }

    public InstrumentTrack FindTrackByRole(MusicalRole role)
    {
        return tracks.FirstOrDefault(t => t.assignedRole == role);
    }
    public void PruneSparseTracks(int threshold = 1)
    {
        foreach (var track in tracks.Where(t => t.CollectedNotesCount <= threshold))
        {
            track.ClearLoopedNotes();
        }
    }
    public void ReassignNoteBehaviorsForPhase(SpawnerPhase phase)
    {
        foreach (var track in tracks)
        {
            var noteSet = track.GetCurrentNoteSet();
            if (noteSet == null) continue;

            switch (phase)
            {
                case SpawnerPhase.Establish:
                    noteSet.noteBehavior = ChooseFrom(NoteBehavior.Bass, NoteBehavior.Drone, NoteBehavior.Harmony);
                    break;
                case SpawnerPhase.Evolve:
                    noteSet.noteBehavior = ChooseFrom(NoteBehavior.Harmony, NoteBehavior.Lead);
                    break;
                case SpawnerPhase.Intensify:
                    noteSet.noteBehavior = ChooseFrom(NoteBehavior.Percussion, NoteBehavior.Lead);
                    break;
                case SpawnerPhase.Release:
                    noteSet.noteBehavior = ChooseFrom(NoteBehavior.Drone, NoteBehavior.Harmony);
                    break;
                case SpawnerPhase.WildCard:
                    noteSet.noteBehavior = ChooseRandomBehavior();
                    break;
                case SpawnerPhase.Pop:
                    noteSet.noteBehavior = ChooseFrom(NoteBehavior.Bass, NoteBehavior.Harmony, NoteBehavior.Lead);
                    break;
            }
        }
    }

    private NoteBehavior ChooseFrom(params NoteBehavior[] options)
    {
        return options[UnityEngine.Random.Range(0, options.Length)];
    }

    private NoteBehavior ChooseRandomBehavior()
    {
        var values = Enum.GetValues(typeof(NoteBehavior));
        return (NoteBehavior)values.GetValue(UnityEngine.Random.Range(0, values.Length));
    }



    public InstrumentTrack FindRandomTrackByRole(MusicalRole role)
    {
        var matching = tracks.Where(t => t.assignedRole == role).ToList();
        Debug.Log($"Found {matching.Count} matching tracks");
        if (matching.Count == 0) return null;
        return matching[Random.Range(0, matching.Count)];
    }

    public InstrumentTrack GetRandomTrack()
    {
        return tracks[Random.Range(0, tracks.Length)];
    }

    public InstrumentTrack GetActiveTrack()
    {
        return activeTrack;
    }

/*
    public void ManualStart()
    {
        if (assignedNoteSets.Count == 0)
        {
            Debug.LogError("No NoteSets assigned to InstrumentTrackController!");
            return;
        }

        currentNoteSetIndex = 0;
        // ✅ Activate only the first track
        NoteSet firstNoteSet = assignedNoteSets[currentNoteSetIndex];
        if (firstNoteSet == null || firstNoteSet.assignedInstrumentTrack == null)
        {
            Debug.LogError("No NoteSets assigned to InstrumentTrackController!");
            return;
        } 
        activeTrack = firstNoteSet.assignedInstrumentTrack;
        activeTrack.ApplyNoteSet(firstNoteSet);
        Debug.Log($"Starting first instrument track: {activeTrack.name}");
        activeTrack.SpawnCollectables();
    }
  

    public void TrackExpansionCompleted(InstrumentTrack completedTrack)
    {
        Debug.Log("Current Note Set: " + currentNoteSetIndex);
        Debug.Log($"Completed instrument track: {completedTrack.name} with NoteSetIndex {currentNoteSetIndex} {GetCurrentNoteSet().name}" );
        completedTrack.ResetTrackGridBehavior();
        
        currentNoteSetIndex++;

        if (currentNoteSetIndex >= assignedNoteSets.Count)
        {
            Debug.Log("All note sets completed. Starting over at random spot");
            currentNoteSetIndex = Random.Range(0, assignedNoteSets.Count);
        }

        NoteSet nextNoteSet = assignedNoteSets[currentNoteSetIndex];
        Debug.Log("Next Note Set: " + nextNoteSet.name);
    
        if (nextNoteSet == null || nextNoteSet.assignedInstrumentTrack == null)
        {
            Debug.LogWarning($"Next NoteSet {currentNoteSetIndex} has no assigned InstrumentTrack. Waiting.");
            return; // ✅ Prevents transition if no valid instrument is assigned
        }

        InstrumentTrack nextTrack = nextNoteSet.assignedInstrumentTrack;

        Debug.Log($"Transitioning to new track: {nextTrack.name}");

        activeTrack = nextTrack;
        activeTrack.ApplyNoteSet(nextNoteSet);
        Debug.Log(("Calling Spawn Collectables on " + activeTrack.name + " with " + nextNoteSet.name));
        activeTrack.SpawnCollectables();
    }

    private NoteSet GetCurrentNoteSet()
    {
        if (currentNoteSetIndex >= assignedNoteSets.Count)
        {
            Debug.LogWarning("GetCurrentNoteSet: No active NoteSet available.");
            return null;
        }
        return assignedNoteSets[currentNoteSetIndex];
    }
*/



}
