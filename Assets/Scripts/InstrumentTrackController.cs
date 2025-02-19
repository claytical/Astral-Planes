using UnityEngine;
using System.Collections.Generic;
using System.Collections;
using System.Linq;

public class InstrumentTrackController : MonoBehaviour
{
    public NoteSet[] assignedNoteSets; // 👈 Array of NoteSets
    public DrumTrack drumTrack;
    private int currentSetIndex = 0; // 👈 Tracks which set is active
    private int currentTrackIndex = 0;
    
    public List<InstrumentTrack> instrumentTracks = new List<InstrumentTrack>();
    // This list parallels instrumentTracks; each element is true when that track has completed its expansion.
    private List<bool> trackCompletionStatus;
    

    private void Awake()
    {

        if(instrumentTracks == null)
        {
            Debug.Log("No instrument tracks found.");
            return;
        }
        // Initialize trackCompletionStatus with false (none completed yet).
        trackCompletionStatus = new List<bool>();
        for (int i = 0; i < instrumentTracks.Count; i++)
        {
            trackCompletionStatus.Add(false);
        }

        ApplyCurrentNoteSet();
    }

    private void Start()
    {
        if(instrumentTracks.Count> 0)
        {
            currentTrackIndex = 0;
            instrumentTracks[currentTrackIndex].BeginTrack();
        }
    }

    public void ApplyCurrentNoteSet()
    {
        if (assignedNoteSets.Length == 0 || currentSetIndex >= assignedNoteSets.Length)
        {
            Debug.LogError("No available NoteSets or index out of range!");
            return;
        }

        NoteSet currentNoteSet = assignedNoteSets[currentSetIndex];

        // ✅ Loop through NoteGroups and assign them dynamically
        foreach (var noteGroup in currentNoteSet.noteGroups)
        {
            if (noteGroup.assignedInstrumentTrack != null)
            {
                noteGroup.assignedInstrumentTrack.ApplyNoteGroup(noteGroup);
            }
            else
            {
                Debug.LogWarning($"NoteGroup is missing an assigned InstrumentTrack.");
            }
        }
        
        Debug.Log($"Applied NoteSet: {currentNoteSet.name}");
    }

    public void ProgressToNextNoteSet()
    {
        if (currentSetIndex < assignedNoteSets.Length - 1)
        {
            currentSetIndex++;
            ApplyCurrentNoteSet();
            foreach (var track in instrumentTracks)
            {
                track.ResetInstrumentLoop();
            }
        }
        else
        {
            currentSetIndex = 0;
            Debug.Log("All note sets used. Resetting to first noteset.");
        }
        ApplyCurrentNoteSet();
    }

    public NoteSet GetNextNoteSet()
    {
        if (currentSetIndex < assignedNoteSets.Length - 1)
        {
            return assignedNoteSets[currentSetIndex + 1]; // ✅ Fetch next NoteSet
        }
        return null; // No more NoteSets left
    }

    public int CurrentNoteSetIndex()
    {
        return Mathf.Min(currentSetIndex, assignedNoteSets.Length - 1);
    }

    /// <summary>
    /// Called by an InstrumentTrack when it completes its allowed expansions.
    /// </summary>
    /// <param name="completedTrack">The track that has finished expanding for the current NoteSet.</param>
    public void TrackExpansionCompleted(InstrumentTrack completedTrack)
    {
        // Find the index of the completed track in our list.
        int trackIndex = instrumentTracks.IndexOf(completedTrack);
        if (trackIndex == -1)
        {
            Debug.LogError("Completed track is not part of the instrumentTracks list.");
            return;
        }

        // Mark the completed track as done.
        trackCompletionStatus[trackIndex] = true;
        Debug.Log($"InstrumentTrack at index {trackIndex} marked as completed.");

        // Get the current NoteSet.
        NoteSet currentSet = assignedNoteSets[CurrentNoteSetIndex()];

        // Look for the next instrument track that has a corresponding NoteGroup.
        // We assume that a valid track must have an index less than the number of noteGroups.
        int nextTrackIndex = -1;
        for (int i = trackIndex + 1; i < instrumentTracks.Count; i++)
        {
            // Only consider this track if there's a matching NoteGroup in the current NoteSet 
            // and it hasn't already been marked complete.
            if (i < currentSet.noteGroups.Count && !trackCompletionStatus[i])
            {
                nextTrackIndex = i;
                break;
            }
        }

        if (nextTrackIndex == -1)
        {
            // No further valid track found in the current arrangement.
            Debug.Log("No further instrument tracks to start in current NoteSet. Progressing to next NoteSet.");

            // Reset all track completion flags.
            for (int i = 0; i < trackCompletionStatus.Count; i++)
            {
                trackCompletionStatus[i] = false;
            }
            currentTrackIndex = 0;

            ProgressToNextNoteSet();

            // Start the first instrument track of the new NoteSet.
            instrumentTracks[currentTrackIndex].BeginTrack();
            instrumentTracks[currentTrackIndex].SpawnCollectables();
        }
        else
        {
            // We found a valid next track.
            instrumentTracks[currentTrackIndex].ResetTrackState();
            currentTrackIndex = nextTrackIndex;
            Debug.Log($"Advancing to instrument track index: {currentTrackIndex}");
            instrumentTracks[currentTrackIndex].BeginTrack();
            instrumentTracks[currentTrackIndex].SpawnCollectables();
        }
    }


}
