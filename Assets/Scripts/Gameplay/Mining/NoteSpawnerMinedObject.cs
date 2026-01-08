using UnityEngine;

public class NoteSpawnerMinedObject : MonoBehaviour
{
    public MusicalRoleProfile musicalRole;
    public InstrumentTrack assignedTrack;
    public NoteSet selectedNoteSet;
    private bool _burstSpawned;

    public void Initialize(InstrumentTrack track, NoteSet noteSet)
    {
        if (track == null) { Debug.LogWarning("NoteSpawnerMinedObject: track NULL"); return; }
        if (noteSet == null) noteSet = track.GetActiveNoteSet();
        if (noteSet == null) { Debug.LogWarning("NoteSpawnerMinedObject: no NoteSet available"); return; }

        Debug.Log($"Initializing Spawner Mined Object");
        assignedTrack = track;
        musicalRole = MusicalRoleProfileLibrary.GetProfile(track.assignedRole);
        selectedNoteSet = noteSet;
        track.SetNoteSet(noteSet);
        if (selectedNoteSet != null)
        {
            Debug.Log($"Selected Note Set on Spawner is {selectedNoteSet} for {track} with {musicalRole}");
            selectedNoteSet.assignedInstrumentTrack = track;
            selectedNoteSet.Initialize(track, track.GetTotalSteps());
        }
        
        Explode explode = GetComponent<Explode>();
        if (explode != null)
        {
            explode.ApplyLifetimeProfile(LifetimeProfile.GetProfile(MinedObjectType.NoteSpawner));
        }
    }
    

}
