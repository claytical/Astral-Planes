using UnityEngine;

[CreateAssetMenu(fileName = "DiscoveryTrackNodeConfig", menuName = "Astral Planes/Discovery Nodes/Discovery Track Node Config")]
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

    // ── Used by DiscoveryTrackNodeDustInteractor ──
    [Header("In Dust")]
    [Tooltip("Environment feedback scalar consumed by DiscoveryTrackNode locomotion while in dust.")]
    [Range(0f, 1f)] public float dustDragScalar = 0.85f;
    [Tooltip("Extra braking applied per FixedUpdate while inside dust.")]
    public float extraBrake = 0f;
    [Tooltip("Force applied to push the node back out when it is grid-inside a dust cell.")]
    public float escapePushForce = 5f;
    [Tooltip("Max exhaust-paint energy fraction applied to a dust cell the node grazes past while cornering along it (0 = off). Kept low — a graze, not a full recolor.")]
    [Range(0f, 0.4f)] public float exhaustEnergyFraction = 0.15f;

    // ── Used by DiscoveryTrackNodeCharacterVis ──
    [Header("Character Animation")]
    [Tooltip("Spin speed (deg/s) of the inner sprite while the node is 'thinking' (choosing a direction).")]
    public float defaultThinkingSpinDegPerSec = 480f;
    [Tooltip("How fast (deg/s) the node's face turns toward its travel direction.")]
    public float defaultFaceTurnDegPerSec = 420f;
    [Tooltip("Peak wobble angle (degrees) while moving.")]
    public float defaultWobbleDeg = 12f;
    [Tooltip("Wobble frequency (Hz) from slow (x) to full speed (y).")]
    public Vector2 wobbleHzRange = new Vector2(2.5f, 4.5f);
    [Tooltip("Inner pivot counter-rotation as a fraction of the outer wobble (negative = opposite direction).")]
    public float innerCounterFactor = -0.4f;
    [Tooltip("Spin multiplier while the node is fleeing / escaping.")]
    public float escapeSpinBoost = 1.2f;
}
