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

        Debug.Log($"Instrument {completedTrack.name} finished. Moving to next NoteSet.");

        currentNoteSetIndex++;

        if (currentNoteSetIndex >= assignedNoteSets.Count)
        {
            Debug.Log("All note sets completed.");
            return;
        }

        InstrumentTrack nextTrack = assignedNoteSets[currentNoteSetIndex].assignedInstrumentTrack;
    
        // Prevent duplicate spawns by ensuring only one transition occurs
        if (nextTrack == activeTrack)
        {
            Debug.LogWarning($"Next track {nextTrack.name} is already active. Aborting transition.");
            return;
        }

        activeTrack = nextTrack;
        activeTrack.ApplyNoteSet(assignedNoteSets[currentNoteSetIndex]);
        activeTrack.SpawnCollectables();
    
        Debug.Log($"Transitioned to {activeTrack.name}.");
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
