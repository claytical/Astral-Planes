using UnityEngine;

public class NoteSetFactory : MonoBehaviour
{
    public NoteSetConfigLibrary configLibrary;
    public GameObject noteSetPrefab; // Empty prefab with NoteSet + InstrumentTrack assigned later

    public NoteSet Generate(InstrumentTrack track, MusicalPhase phase, RemixUtility remixUtility = null)
    {
        var config = configLibrary.GetConfig(track.assignedRole, phase);
        if (config == null)
        {
            Debug.LogWarning($"No config found for {track.assignedRole} in {phase}");
            return null;
        }

        var noteSetGO = Instantiate(noteSetPrefab);
        var noteSet = noteSetGO.GetComponent<NoteSet>();

        noteSet.assignedInstrumentTrack = track;
        noteSet.noteBehavior = config.noteBehavior;
        noteSet.scale = config.GetRandomScale();
        noteSet.chordPattern = config.GetRandomChordPattern();
        noteSet.rhythmStyle = config.GetRandomRhythmStyle();
        noteSet.rootMidi = 60;//track.GetDefaultRoot(); // You may randomize if allowed
        noteSet.remixUtility = remixUtility;
        int steps = track.drumTrack.totalSteps;
        noteSet.Initialize(track, steps);

        return noteSet;
    }
}
