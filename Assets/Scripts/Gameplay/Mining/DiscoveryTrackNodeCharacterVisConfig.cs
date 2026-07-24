using UnityEngine;

[CreateAssetMenu(fileName = "DiscoveryTrackNodeCharacterVisConfig", menuName = "Astral Planes/Discovery Track Node Character Vis Config")]
public class DiscoveryTrackNodeCharacterVisConfig : ScriptableObject
{
    [Header("Locomotion Intent Animation")]
    public float defaultThinkingSpinDegPerSec = 480f;
    public float defaultFaceTurnDegPerSec = 420f;
    public float defaultWobbleDeg = 12f;
    public Vector2 wobbleHzRange = new Vector2(2.5f, 4.5f);
    public float innerCounterFactor = -0.4f;
    public float escapeSpinBoost = 1.2f;
}
