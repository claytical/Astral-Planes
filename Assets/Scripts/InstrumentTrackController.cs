using UnityEngine;
using System.Collections.Generic;
using System.Collections;
using System.Linq;
using Unity.Loading;
using UnityEngine.SceneManagement;

public class InstrumentTrackController : MonoBehaviour
{
    public List<NoteSet> assignedNoteSets; // 👈 Array of NoteSets

    private InstrumentTrack activeTrack;
    private int currentNoteSetIndex = 0; // 👈 Tracks which set is active
    private HashSet<InstrumentTrack> usedInstrumentTracks = new HashSet<InstrumentTrack>();

    // This list parallels instrumentTracks; each element is true when that track has completed its expansion.
    

    void Start()
    {
        Debug.Log($"InstrumentTrackController initialized. Assigned NoteSets: {assignedNoteSets.Count}");
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
    

}
