using UnityEngine;

[CreateAssetMenu(menuName = "Astral Planes/Mine Node Motion Config", fileName = "MineNodeMotionConfig")]
public class MineNodeMotionConfig : ScriptableObject
{
    [Header("Shared Motion Constants")]
    [Min(0f)] public float stallSpeed = 0.20f;
    [Range(-1f, 1f)] public float stuckDot = 0.10f;
    [Min(0f)] public float escapeJitterDeg = 25f;
    [Min(0f)] public float minSpeedFloor = 0.25f;
    [Min(0f)] public float stallSamplePeriod = 0.40f;
    [Min(0f)] public float stallDistanceEps = 0.12f;
    [Min(0f)] public float escapeCooldown = 0.30f;

    [Header("Validation Envelope")]
    [Tooltip("Allowed overlap ratio before warning that archetypes are insufficiently distinct.")]
    [Range(0f, 1f)] public float maxAllowedEnvelopeOverlap = 0.20f;
}
