using UnityEngine;

[System.Serializable]
public class MineRoleTuning
{
    [Header("Speed/Force multipliers")]
    public float maxSpeedMul = 1f;
    public float maxForceMul = 1f;

    [Header("Gait biases (probability nudges)")]
    public float frolicBiasDelta = 0f;   // + makes Frolic more likely
    public float hideBiasDelta   = 0f;   // + prefers SeekDustHide
    public float evadeBiasDelta  = 0f;   // + prefers Evade
    public float orbitBiasDelta  = 0f;   // + prefers OrbitTrack

    [Header("Dust interaction overrides (additive)")]
    public float dustSpeedCapDelta = 0f;
    public float dustExtraBrakeDelta = 0f;
    public float dustLateralDelta = 0f;
    public float dustTurbulenceDelta = 0f;

    [Header("Wiggle deltas")]
    public float wiggleTorqueDelta = 0f;
    public float wiggleFreqDelta   = 0f;
}