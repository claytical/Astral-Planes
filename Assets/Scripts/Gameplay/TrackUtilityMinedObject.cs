using UnityEngine;

public class TrackUtilityMinedObject : MonoBehaviour
{
    public enum UtilityType { LoopExpansion, AntiNote, TrackClear, Shift }
    public UtilityType type;
    public MusicalRole targetRole;
    
    public void Initialize(InstrumentTrack track)
    {
        GetComponent<MinedObject>().AssignTrack(track);

        // Setup lifetime
        var explode = GetComponent<Explode>();
        if (explode != null)
        {
            explode.ApplyLifetimeProfile(LifetimeProfile.GetProfile(MinedObjectType.TrackUtility));
        }
    }

    public void OnCollected()
    {
        InstrumentTrack assignedTrack = GetComponent<MinedObject>().assignedTrack;

        if (assignedTrack == null)
        {
            Debug.LogWarning("No track assigned to utility item.");
            return;
        }

        switch (type)
        {
            case UtilityType.LoopExpansion:
                assignedTrack.ExpandLoop();
                break;
            case UtilityType.AntiNote:
                assignedTrack.SpawnAntiNote();
                break;
            case UtilityType.TrackClear:
                assignedTrack.ClearLoopedNotes();
                break;
            case UtilityType.Shift:
                assignedTrack.PerformSmartNoteModification();
                break;
        }

        Debug.Log($"Executed {type} on track: {assignedTrack.name}");
        Destroy(gameObject);
    }
}