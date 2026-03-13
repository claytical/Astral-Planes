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
    public Color baseColor;     // neutral presence
    public Color chargeColor;   // boost → brighten
    public Color denyColor;     // bump → darken / assert
    public Color shadowColor;   // regrowth / memory / afterimage
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
    
    [Header("Presets")]
    public List<int> allowedMidiPresets = new List<int>();

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
