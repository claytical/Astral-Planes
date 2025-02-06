using UnityEngine;
using System.Collections.Generic;
using System.Collections;
using System.Linq;

public class InstrumentTrackController : MonoBehaviour
{
    public static InstrumentTrackController Instance; // Singleton for global access

//    public List<NoteSet> noteSets; // ✅ Centralized list of all note sets
    public NoteSet[] assignedNoteSets; // 👈 Array of NoteSets
    public DrumTrack drumTrack;
    private int currentSetIndex = 0; // 👈 Tracks which set is active
    
    private List<InstrumentTrack> instrumentTracks = new List<InstrumentTrack>();

    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }
        else
        {
            Destroy(gameObject);
            return;
        }

        FindAllInstrumentTracks();
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

        for (int i = 0; i < instrumentTracks.Count; i++)
        {
            int groupIndex = i % currentNoteSet.noteGroups.Count;
            instrumentTracks[i].ApplyNoteSet(currentNoteSet.noteGroups[groupIndex]);
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
        }
        else
        {
            Debug.Log("All NoteSets used. No further progression.");
            currentSetIndex = 0;
        }
    }

    IEnumerator NextLevel()
    {
        yield return new WaitForSeconds(1f); // Small delay

        foreach (var track in instrumentTracks)
        {
            track.isLocked = false;
            track.ResetCollectables();
        }

        ProgressToNextNoteSet(); // 👈 Move to the next NoteSet

        //RequestPatternChange(); // Switch to new drum pattern
    }
    public void RequestPatternChange()
    {
        if (instrumentTracks.Count > 0 && drumTrack != null)
        {
            int randomIndex = UnityEngine.Random.Range(0, instrumentTracks[0].availablePatterns.Count);
            drumTrack.QueueDrumPattern(instrumentTracks[0].availablePatterns[randomIndex]);
        }
    }
    public void CheckAllTracksLocked()
    {
        if (instrumentTracks.All(track => track.isLocked))
        {
            Debug.Log("All tracks locked! Unlocking the first track and restarting collectables.");

            // ✅ Unlock the first track
            instrumentTracks[0].UnlockTrack();

            // ✅ Force respawn collectables
            instrumentTracks[0].SpawnCollectables();
        }
    }


    private void FindAllInstrumentTracks()
    {
        instrumentTracks = new List<InstrumentTrack>(FindObjectsByType<InstrumentTrack>(FindObjectsSortMode.None));
    }

}
