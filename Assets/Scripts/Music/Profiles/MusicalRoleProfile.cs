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


    [Header("Dust Mining")]
    [Range(0f, 1f)]
    [Tooltip("How hard dust imprinted/regrown with this role should be. 0 = soft/easy, 1 = hard/resists digging.")]
    public float dustHardness01 = 0.50f;

    [Header("MineNode Balance")]
    [Range(0f, 1f)] public float mineNodeSpeed    = 0.5f;  // 0 = sluggish, 1 = fast
    [Range(0f, 1f)] public float mineNodeAgility  = 0.5f;  // 0 = gentle curves, 1 = sharp turns

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
    public float GetDustHardness01() => Mathf.Clamp01(dustHardness01);
}
