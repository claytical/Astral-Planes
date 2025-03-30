using UnityEngine;

public class TrackUtilityMinedObject : MonoBehaviour
{
    public enum UtilityType { LoopExpansion, AntiNote, TrackClear }
    public UtilityType type;
    public MusicalRole targetRole;

    private InstrumentTrack assignedTrack;

    public void Initialize(InstrumentTrack track)
    {
        assignedTrack = track;
        if (assignedTrack == null)
        {
            Debug.LogWarning($"UtilityItem failed to assign track for role {targetRole}");
        }
    }

    public void OnCollected()
    {
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
                assignedTrack.SpawnAntiNote(); // This should be implemented in InstrumentTrack
                break;
            case UtilityType.TrackClear:
                assignedTrack.ClearLoopedNotes();
                break;
        }

        Debug.Log($"Executed {type} on track: {assignedTrack.name}");
        Destroy(gameObject);
    }
}