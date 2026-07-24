using UnityEngine;

[CreateAssetMenu(fileName = "DiscoveryTrackNodeCharacterVisConfig", menuName = "Astral Planes/Discovery Track Node Character Vis Config")]
public class DiscoveryTrackNodeCharacterVisConfig : ScriptableObject
{
    [System.Serializable]
    public struct ArchetypeVisualProfile
    {
        public DiscoveryTrackNodeLocomotionArchetype archetype;
        [Min(0f)] public float thinkingSpinDegPerSec;
        [Min(0f)] public float faceTurnDegPerSec;
        [Min(0f)] public float wobbleDeg;
    }

    [Header("Locomotion Intent Animation")]
    public float defaultThinkingSpinDegPerSec = 480f;
    public float defaultFaceTurnDegPerSec = 420f;
    public float defaultWobbleDeg = 12f;
    public Vector2 wobbleHzRange = new Vector2(2.5f, 4.5f);
    public float innerCounterFactor = -0.4f;
    public float escapeSpinBoost = 1.2f;

    [Header("Profile Swim/Think Blend Thresholds")]
    public float defaultThinkToSwimSpeed = 0.12f;
    public float defaultSwimToThinkSpeed = 0.06f;

    [Header("Archetype Visual Overrides")]
    public ArchetypeVisualProfile[] archetypeVisualProfiles;
}
