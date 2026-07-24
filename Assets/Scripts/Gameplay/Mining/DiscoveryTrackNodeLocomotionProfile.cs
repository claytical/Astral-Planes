using UnityEngine;

[CreateAssetMenu(menuName = "Astral Planes/Discovery Track Node Locomotion Profile", fileName = "DiscoveryTrackNodeLocomotionProfile")]
public class DiscoveryTrackNodeLocomotionProfile : ScriptableObject
{
    [Header("Linear Motion")]
    [Min(0f)] public float baseSpeed = 1f;
    [Min(0f)] public float maxSpeed = 6f;
    [Min(0f)] public float acceleration = 10f;
    [Min(0f)] public float braking = 2f;

    [Header("Turning")]
    [Min(0f)] public float turnRate = 6f;
    [Min(0f)] public float hesitation = 0.1f;

    [Header("Environment")]
    [Range(0f, 1f)] public float dustPenalty = 0.2f;

    [Header("Combat & Lifespan")]
    [Min(1)] public int strength = 100;
    [Tooltip("Loops before this node expires. 0 = never (only used when no NoteSet override applies).")]
    [Min(0)] public int expireAfterLoops = 0;

    [Header("Decision Timing")]
    [Tooltip("Random range (min, max seconds) before this role is even eligible to reconsider its heading.")]
    public Vector2 reactionDelayWindow = new Vector2(0.12f, 0.25f);
    [Tooltip("Minimum seconds locked onto a chosen heading before a rescan is considered.")]
    [Min(0f)] public float pathCommitmentDuration = 0.85f;
    [Tooltip("Random wobble (degrees) added on top of the best-scoring direction.")]
    [Range(0f, 45f)] public float turnJitter = 6f;

    [Header("Flee & Recovery")]
    [Tooltip("How hard this role commits to steering toward its sought exit gap while fleeing.")]
    [Range(0f, 1f)] public float fleeCommitment01 = 0.55f;
    [Tooltip("How aggressively this role unsticks itself from a stall (cooldown + escape jitter).")]
    [Range(0f, 1f)] public float stallRecoveryAggressiveness = 0.6f;
    [Tooltip("How much this role favors already-decent-but-risky directions over the safest one.")]
    [Range(0f, 1f)] public float dustRiskTolerance = 0.5f;

    [Header("Hit Stun")]
    [Tooltip("Seconds after a Vehicle hit during which this role dashes away on a locked heading.")]
    [Min(0f)] public float hitStunDuration = 0.8f;
    [Tooltip("Speed multiplier applied only during the post-hit stun dash.")]
    [Min(1f)] public float hitStunSpeedMultiplier = 1.6f;

    public float EvaluateTargetSpeed(float intensity01)
    {
        return Mathf.Lerp(baseSpeed, Mathf.Max(baseSpeed, maxSpeed), Mathf.Clamp01(intensity01));
    }

    public float SampleReactionDelay()
    {
        float min = Mathf.Min(reactionDelayWindow.x, reactionDelayWindow.y);
        float max = Mathf.Max(reactionDelayWindow.x, reactionDelayWindow.y);
        return Random.Range(min, max);
    }
}
