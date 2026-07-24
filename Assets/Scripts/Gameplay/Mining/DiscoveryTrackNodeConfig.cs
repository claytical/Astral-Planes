using UnityEngine;

[CreateAssetMenu(fileName = "DiscoveryTrackNodeConfig", menuName = "Astral Planes/Discovery Track Node Config")]
public class DiscoveryTrackNodeConfig : ScriptableObject
{
    [Header("Expiry")]
    [Tooltip("Radius in grid cells within which hidden dust matching this node's role is revealed on expiry.")]
    [Min(0)] public int expireBlastRadiusCells = 5;

    [Header("NoteSet-Driven Motion")]
    public bool driveCarvingMotionFromNoteSet = true;
}
