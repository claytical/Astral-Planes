using System.Collections.Generic;
using UnityEngine;

public enum MusicalRole
{
    Bass,
    Harmony,
    Lead,
    Groove,
    None
}
[System.Serializable]
public struct DustColorSet
{
    public Color baseColor;    // neutral presence
    public Color shadowColor;  // regrowth / memory / deny feedback
}

[CreateAssetMenu(menuName = "Astral Planes/Musical Role Profile", fileName = "NewMusicalRoleProfile")]
public class MusicalRoleProfile : ScriptableObject
{
    public MusicalRole role;

    [Header("Visuals & Styling")]
    public DustColorSet dustColors;
    [Range(0f, 1f)]
    [Tooltip("Baseline alpha for this role when used as a semantic color (track / MineNode / imprints).")]
    public float baseAlpha = 0.25f;


    [Header("Dust Energy")]
    [Tooltip("Maximum energy units this role's dust holds. Default=1, Lead=2, Groove=3, Harmony=4, Bass=5.")]
    [Min(1)] public int maxEnergyUnits = 1;

    [Range(0f, 1f)]
    [Tooltip("How hard vehicle plow carving is against this dust. 0=instant, 1=nearly indestructible by vehicle.")]
    public float carveResistance01 = 0.50f;

    [Range(0f, 1f)]
    [Tooltip("How hard PhaseStar draining is against this dust. 0=drains instantly, 1=very slow. Invert carveResistance for ecological tension.")]
    public float drainResistance01 = 0.50f;

    [Header("Dust Mining (Legacy)")]
    [Range(0f, 1f)]
    [Tooltip("Legacy single-axis hardness. Superseded by carveResistance01 + drainResistance01. Kept for migration.")]
    public float dustHardness01 = 0.50f;

    [Header("MineNode Locomotion")]
    [Tooltip("Legacy selector used to choose a locomotion archetype/variant when no explicit profile is assigned.")]
    [Range(0f, 1f)] public float mineNodeSpeed = 0.5f;

    [Tooltip("Direct locomotion profile override for this role.")]
    public MineNodeLocomotionProfile mineNodeLocomotionProfile;

    [Header("Presets")] public int midiPreset;

    [Header("Ripeness / Decay")]
    [Tooltip("How long (seconds) the revealed role color stays visible before fading back to gray. Must be > 0.")]
    public float ripeDuration = 8f;

    [Tooltip("Per-role regrowth delay override (seconds). -1 = use maze pattern default.")]
    public float regrowthDelay = -1f;

    // ---------- Color helpers (authority) ----------

    public Color GetBaseColor()
    {
        var c = dustColors.baseColor;
        c.a = baseAlpha;
        return c;
    }
    public Color GetShadowColor()
    {
        var c = dustColors.shadowColor;
        c.a = baseAlpha;
        return c;
    }
    public float GetDustHardness01()    => Mathf.Clamp01(dustHardness01);
    public float GetCarveResistance01() => Mathf.Clamp01(carveResistance01);
    public float GetDrainResistance01() => Mathf.Clamp01(drainResistance01);
}
