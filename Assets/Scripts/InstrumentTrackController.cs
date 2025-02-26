using UnityEngine;
using System.Collections.Generic;
using System.Collections;
using System.Linq;

public class InstrumentTrackController : MonoBehaviour
{
    public List<NoteSet> assignedNoteSets; // 👈 Array of NoteSets
    public DrumTrack drumTrack;
    private int currentNoteSetIndex = 0; // 👈 Tracks which set is active
    
    public List<InstrumentTrack> instrumentTracks = new List<InstrumentTrack>();
    // This list parallels instrumentTracks; each element is true when that track has completed its expansion.
    

    void Start()
    {
        drumTrack.trackController = this;
        void Start()
        {
            if (assignedNoteSets.Count == 0)
            {
                Debug.LogError("No NoteSets assigned to InstrumentTrackController!");
                return;
            }

            // ✅ Start with the first NoteSet and apply it to its assigned InstrumentTrack.
            ApplyCurrentNoteSet();
        }
        
    }

    public void ApplyCurrentNoteSet()
    {
        if (currentNoteSetIndex >= assignedNoteSets.Count)
        {
            Debug.Log("All NoteSets have been used. Looping back.");
            currentNoteSetIndex = 0; // ✅ Loop back to the beginning
        }

        NoteSet currentNoteSet = assignedNoteSets[currentNoteSetIndex];

        if (currentNoteSet.assignedInstrumentTrack != null)
        {
            currentNoteSet.assignedInstrumentTrack.ApplyNoteSet(currentNoteSet);
            currentNoteSet.assignedInstrumentTrack.SpawnCollectables();
        }
        else
        {
            Debug.LogWarning($"NoteSet {currentNoteSetIndex} has no assigned InstrumentTrack!");
        }
    }


    /// <summary>
    /// Called by an InstrumentTrack when it completes its allowed expansions.
    /// </summary>
    /// <param name="completedTrack">The track that has finished expanding for the current NoteSet.</param>
    public void TrackExpansionCompleted(InstrumentTrack completedTrack)
    {
        Debug.Log($"Instrument {completedTrack.name} finished. Moving to next NoteSet.");
        currentNoteSetIndex++; // ✅ Move to the next NoteSet
        ApplyCurrentNoteSet();
    }
    public NoteSet GetCurrentNoteSet()
    {
        if (currentNoteSetIndex >= assignedNoteSets.Count)
        {
            Debug.LogWarning("GetCurrentNoteSet: No active NoteSet available.");
            return null;
        }
        return assignedNoteSets[currentNoteSetIndex];
    }

    public void RemoveCollectableFromActiveTrack()
    {
        if (currentNoteSetIndex >= assignedNoteSets.Count)
        {
            Debug.LogWarning("RemoveCollectableFromActiveTrack: No active NoteSet available.");
            return;
        }

        NoteSet currentNoteSet = assignedNoteSets[currentNoteSetIndex];
        if (currentNoteSet.assignedInstrumentTrack != null)
        {
            currentNoteSet.assignedInstrumentTrack.RemoveCollectableNote();
        }
        else
        {
            Debug.LogWarning("RemoveCollectableFromActiveTrack: No assigned InstrumentTrack for current NoteSet.");
        }
    }


}
