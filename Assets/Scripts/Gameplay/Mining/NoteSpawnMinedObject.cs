using UnityEngine;

public class NoteSpawnerMinedObject : MonoBehaviour
{
    public MusicalRole musicalRole;
    public NoteSetSeries noteSetSeries;

    private InstrumentTrack assignedTrack;
    private NoteSet selectedNoteSet;

    public void Initialize(InstrumentTrack track, NoteSet noteSet)
    {
        assignedTrack = track;
        this.GetComponent<MinedObject>().assignedTrack = track;

        selectedNoteSet = noteSet;
        if (track == null || noteSet == null)
        {
            Debug.LogWarning("NoteSpawnerMinedObject initialized with missing track or NoteSet.");
            return;
        }

        selectedNoteSet.assignedInstrumentTrack = track;
        selectedNoteSet.Initialize(track.drumTrack.totalSteps);
        ApplyTrackVisuals(track.trackColor);
        Explode explode = GetComponent<Explode>();
        if (explode != null)
        {
            explode.ApplyLifetimeProfile(LifetimeProfile.GetProfile(MinedObjectType.NoteSpawner));
        }

    }


    public void OnCollected()
    {
        if (assignedTrack != null && selectedNoteSet != null)
        {
            assignedTrack.AdaptiveSpawnCollectables(selectedNoteSet);
            
        }

        Explode explode = GetComponent<Explode>();
        if (explode != null)
        {
            Debug.Log($"Explosion called from {gameObject.name}");
            explode.Permanent();
        }
        else
        {
            Debug.Log($"Explosion null on {gameObject.name}");
            Destroy(gameObject);
        }
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
