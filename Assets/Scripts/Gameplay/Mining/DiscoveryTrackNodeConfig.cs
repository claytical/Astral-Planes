using UnityEngine;

[CreateAssetMenu(fileName = "DiscoveryTrackNodeConfig", menuName = "Astral Planes/Gameplay/Mining/Discovery Track Node Config")]
public class DiscoveryTrackNodeConfig : ScriptableObject
{
    [Header("Expiry")]
    [Tooltip("Radius in grid cells within which hidden dust matching this node's role is revealed on expiry.")]
    [Min(0)] public int expireBlastRadiusCells = 5;

    [Header("NoteSet-Driven Motion")]
    public bool driveCarvingMotionFromNoteSet = true;

    [Header("Visuals")]
    [Tooltip("Outline sprite override for Rhythm-role nodes. All other roles keep the default outline sprite.")]
    public Sprite outlineSpriteRhythm;
}
