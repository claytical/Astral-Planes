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
    public Boundaries boundaries;

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
        // Ensure we only process the active track
        if (completedTrack != activeTrack)
        {
            Debug.Log($"Ignoring completion of {completedTrack.name} because active track is {activeTrack.name}");
            return;
        }

        Debug.Log($"Instrument {completedTrack.name} finished. Checking for next NoteSet.");

        // ✅ Ensure all collectables have been collected before moving on
        if (completedTrack.spawnedCollectables.Count > 0)
        {
            Debug.Log($"Waiting for all collectables to be collected before transitioning.");
            return;
        }

        completedTrack.ResetTrackGridBehavior();
        
        currentNoteSetIndex++;

        if (currentNoteSetIndex >= assignedNoteSets.Count)
        {
            Debug.Log("All note sets completed.");
            return;
        }

        NoteSet nextNoteSet = assignedNoteSets[currentNoteSetIndex];
    
        if (nextNoteSet == null || nextNoteSet.assignedInstrumentTrack == null)
        {
            Debug.LogWarning($"Next NoteSet {currentNoteSetIndex} has no assigned InstrumentTrack. Waiting.");
            return; // ✅ Prevents transition if no valid instrument is assigned
        }

        InstrumentTrack nextTrack = nextNoteSet.assignedInstrumentTrack;

        Debug.Log($"Transitioning to new track: {nextTrack.name}");

        activeTrack = nextTrack;
        activeTrack.ApplyNoteSet(nextNoteSet);
        activeTrack.SpawnCollectables();
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
