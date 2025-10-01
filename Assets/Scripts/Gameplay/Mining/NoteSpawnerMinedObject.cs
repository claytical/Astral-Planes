using Effects;
using UnityEngine;

public class NoteSpawnerMinedObject : MonoBehaviour
{
    public MusicalRoleProfile musicalRole;
    public InstrumentTrack assignedTrack;
    public NoteSet selectedNoteSet;

    public void Initialize(InstrumentTrack track, NoteSet noteSet)
    {
        Debug.Log($"Initializing Spawner Mined Object");
        assignedTrack = track;
        musicalRole = MusicalRoleProfileLibrary.GetProfile(track.assignedRole);
        selectedNoteSet = noteSet;
        if (track == null || noteSet == null)
        {
            Debug.LogWarning("NoteSpawnerMinedObject initialized with missing track or NoteSet.");
            return;
        }

        if (selectedNoteSet != null)
        {
            Debug.Log($"Selected Note Set on Spawner is {selectedNoteSet} for {track} with {musicalRole}");
            selectedNoteSet.assignedInstrumentTrack = track;
            selectedNoteSet.Initialize(track, track.drumTrack.totalSteps);
        }


        ApplyTrackVisuals(track.trackColor);

        Explode explode = GetComponent<Explode>();
        if (explode != null)
        {
            explode.ApplyLifetimeProfile(LifetimeProfile.GetProfile(MinedObjectType.NoteSpawner));
        }
    }
    // NoteSpawnerMinedObject
    private void OnEnable()
    {
        if (assignedTrack != null && selectedNoteSet != null)
        {
            Debug.Log($"[NoteSpawner] Emitting burst for {assignedTrack.name} ({selectedNoteSet.name})");
            assignedTrack.SpawnCollectableBurst(selectedNoteSet, maxToSpawn: 8); // pick a sensible number
        }
        else
        {
            Debug.LogWarning("[NoteSpawner] Missing track or noteset on enable; no burst.");
        }
    }

    void OnCollisionEnter2D(Collision2D coll)
    {
        Debug.Log($"NoteSpawnerMinedObject.OnCollisionEnter2D: {coll.gameObject.name}");
        CollectionSoundManager.Instance?.PlayNoteSpawnerSound(assignedTrack, selectedNoteSet);
    }
    
    private void ApplyTrackVisuals(Color color)
    {
        var visual = GetComponent<TrackItemVisual>();
        if (visual != null)
        {
            visual.trackColor = color;
        }
    }
}
