using UnityEngine;

public class TrackUtilityMinedObject : MonoBehaviour
{
    public enum UtilityType { LoopExpansion, AntiNote, TrackClear }
    public UtilityType type;
    public MusicalRole targetRole;
    
    public void Initialize(InstrumentTrack track)
    {
        GetComponent<MinedObject>().assignedTrack = track;
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
                assignedTrack.ForceClearTrack();
                break;
        }

        Debug.Log($"Executed {type} on track: {assignedTrack.name}");
        Destroy(gameObject);
    }
}