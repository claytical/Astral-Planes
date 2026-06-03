using System;
using UnityEngine;

[Serializable]
public struct DustVisualSettings {
    [Header("Sizing (prefab baseline)")]
    public Vector3 prefabReferenceScale; // authored prefab baseline (cell-driven scale overrides at runtime)
    public ParticleSystem particleSystem;

    public SpriteRenderer sprite;
}
[System.Serializable]
public struct DustVisualTimings
{
    [Min(0.01f)] public float regrowSpriteScaleInSeconds; // regrow sprite scale 0->1
    [Min(0.01f)] public float clearSpriteScaleOutSeconds; // clear sprite scale 1->0
    [Min(0.01f)] public float regrowParticleGrowInSeconds; // particle emission/alpha ramp on regrow
    [Min(0.01f)] public float clearFadeOutSeconds; // clear tint/alpha fade out (if used)

    public static DustVisualTimings Default => new DustVisualTimings
    {
        regrowSpriteScaleInSeconds = 0.20f,
        clearSpriteScaleOutSeconds = 0.20f,
        regrowParticleGrowInSeconds = 1.00f,
        clearFadeOutSeconds = 0.20f
    };

    public DustVisualTimings Sanitized()
    {
        var sanitized = this;
        sanitized.regrowSpriteScaleInSeconds = Mathf.Max(0.01f, sanitized.regrowSpriteScaleInSeconds);
        sanitized.clearSpriteScaleOutSeconds = Mathf.Max(0.01f, sanitized.clearSpriteScaleOutSeconds);
        sanitized.regrowParticleGrowInSeconds = Mathf.Max(0.01f, sanitized.regrowParticleGrowInSeconds);
        sanitized.clearFadeOutSeconds = Mathf.Max(0.01f, sanitized.clearFadeOutSeconds);
        return sanitized;
    }
}
[Serializable]
public struct DustInteractionSettings
{
    [Header("Energy Drain")]
    [Min(0f)] public float energyDrainPerSecond;
}
[Serializable]
public struct DustClearingSettings
{
    [Tooltip("Energy drain resistance when a vehicle boosts through. 0 = full drain rate, 1 = no drain.")]
    [Range(0f, 1f)] public float drainResistance01;
}
[Serializable]
public struct DustPluckSettings
{
    [Header("Dust Musical Swell")]
    [Tooltip("How long contact needs to build before plucks reach full intensity.")]
    [Range(0.1f, 4f)] public float swellSeconds;
    [Tooltip("Short/long pluck lengths used at low/high contact intensity.")]
    [Min(1)] public int minDurationTicks;
    [Min(1)] public int maxDurationTicks;
    [Tooltip("Soft/loud pluck velocities used at low/high contact intensity.")]
    [Range(1f, 127f)] public float minVelocity127;
    [Range(1f, 127f)] public float maxVelocity127;
    [Tooltip("Time between plucks. Max applies at first contact, min after sustained pressure.")]
    [Min(0.01f)] public float minCooldownSeconds;
    [Min(0.01f)] public float maxCooldownSeconds;
}
[Serializable]
public struct NoseCompressionSettings
{
    [Header("Vehicle Nose Compression")]
    public bool enabled;
    [Range(0f, 0.75f)] public float compressAmount;
    [Range(0f, 0.5f)] public float bulgeAmount;
    [Tooltip("Maximum world-space visual offset while compressed.")]
    [Min(0f)] public float maxOffsetWorld;
    [Tooltip("Distance from vehicle center used to sample nose contact.")]
    [Min(0f)] public float probeWorld;
    [Tooltip("Vehicle speed (world units/sec) that yields full compression target.")]
    [Min(0.01f)] public float speedForFull;
    [Range(0f, 1f)] public float boostBonus;
    [Min(0f)] public float contactGraceSeconds;
    [Min(0f)] public float minimumVisibleSeconds;
    [Tooltip("Lower = slower, more cushioned. Higher = snappier.")]
    [Min(0.01f)] public float attackSharpness;
    [Min(0.01f)] public float releaseSharpness;
}
