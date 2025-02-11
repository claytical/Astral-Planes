using UnityEngine;
using System.Collections.Generic;
using System.Collections;
using System.Linq;

public class InstrumentTrackController : MonoBehaviour
{
    public NoteSet[] assignedNoteSets; // 👈 Array of NoteSets
    public DrumTrack drumTrack;
    private int currentSetIndex = 0; // 👈 Tracks which set is active
    
    public List<InstrumentTrack> instrumentTracks = new List<InstrumentTrack>();

    private void Awake()
    {

        if(instrumentTracks == null)
        {
            Debug.Log("No instrument tracks found.");
            return;
        }
        ApplyCurrentNoteSet();
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
            Debug.Log($"Switched to NoteSet: {assignedNoteSets[currentSetIndex].name}");

            // ✅ Remove all collected notes when switching patterns
            foreach (var track in instrumentTracks)
            {
                track.ClearLoopNotes();
            }
        }
        else
        {
            Debug.Log("All NoteSets used. No further progression.");
            currentSetIndex = 0;
        }
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



}
