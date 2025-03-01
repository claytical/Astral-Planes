using UnityEngine;
using System.Collections.Generic;
using System.Collections;
using System.Linq;
using Unity.Loading;
using UnityEngine.SceneManagement;

public class InstrumentTrackController : MonoBehaviour
{
    public List<NoteSet> assignedNoteSets; // 👈 Array of NoteSets
    private int currentNoteSetIndex = 0; // 👈 Tracks which set is active
    private HashSet<InstrumentTrack> usedInstrumentTracks = new HashSet<InstrumentTrack>();
    public Boundaries boundaries;

    // This list parallels instrumentTracks; each element is true when that track has completed its expansion.
    

    void Start()
    {

            if (!GamepadManager.Instance.ReadyToPlay())
            {
                return;
            }
    }

    public void ManualStart()
    {
        if (assignedNoteSets.Count == 0)
        {
            Debug.LogError("No NoteSets assigned to InstrumentTrackController!");
            return;
        }

        // ✅ Start with the first NoteSet and apply it to its assigned InstrumentTrack.
        ApplyCurrentNoteSet();


    }
    public void ApplyCurrentNoteSet()
    {
        
        if (currentNoteSetIndex >= assignedNoteSets.Count)
        {
            Debug.Log("All NoteSets have been used. Stopping expansion.");
            return; // ✅ Stop instead of looping back
        }

        NoteSet currentNoteSet = assignedNoteSets[currentNoteSetIndex];
        currentNoteSet.assignedInstrumentTrack.SetBoundaries(boundaries);
        if (currentNoteSet.assignedInstrumentTrack != null)
        {
            //            currentNoteSet.assignedInstrumentTrack.ClearLoopNotes();
            Debug.Log($"Applying NoteSet {currentNoteSetIndex} to {currentNoteSet.assignedInstrumentTrack.name}");
            
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
    /// <param name="completedTrack">The track that h
    ///
    public void TrackExpansionCompleted(InstrumentTrack completedTrack)
    {
        Debug.Log($"Instrument {completedTrack.name} finished. Moving to next NoteSet.");
        currentNoteSetIndex++;

        if (currentNoteSetIndex >= assignedNoteSets.Count)
        {
            Debug.Log("All NoteSets have been completed.");
            currentNoteSetIndex = completedTrack.currentNoteSet.dropBackIndex;
        }

        InstrumentTrack nextTrack = assignedNoteSets[currentNoteSetIndex].assignedInstrumentTrack;
        NoteSet nextNoteSet = assignedNoteSets[currentNoteSetIndex];

        // Check if this instrument is playing a different NoteSet than before
        if (nextTrack.currentNoteSet != nextNoteSet)
        {
            Debug.Log($"Switching {nextTrack.name} to a new NoteSet. Resetting its loop.");
         //   nextTrack.ClearLoopNotes(); // Reset notes only when the NoteSet is new
        }

        // Apply the new NoteSet to the InstrumentTrack
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
    

}
