using UnityEngine;

public enum MineNodeLocomotionArchetype
{
    Cautious,
    Balanced,
    Aggressive,
    Skittish
}

[CreateAssetMenu(menuName = "Astral Planes/Mine Node Locomotion Profile", fileName = "MineNodeLocomotionProfile")]
public class MineNodeLocomotionProfile : ScriptableObject
{
    [Header("Identity")]
    public MineNodeLocomotionArchetype archetype = MineNodeLocomotionArchetype.Balanced;

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
    [Tooltip("Loops before this node expires. 0 = never (only used when no NoteSet override and no config default apply).")]
    [Min(0)] public int expireAfterLoops = 0;

    public float EvaluateTargetSpeed(float intensity01)
    {
        return Mathf.Lerp(baseSpeed, Mathf.Max(baseSpeed, maxSpeed), Mathf.Clamp01(intensity01));
    }
}
