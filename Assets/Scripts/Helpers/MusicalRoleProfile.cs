using System.Collections.Generic;
using UnityEngine;

public enum MusicalRole
{
    Bass,
    Harmony,
    Lead,
    Groove
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

    [Header("Charge / Deny Tuning")]
    [Range(0f, 1f)]
    [Tooltip("How strongly charge pushes defaultColor toward white at full appetite.")]
    public float chargeToWhite = 0.80f;

    [Range(0f, 1f)]
    [Tooltip("How strongly deny pushes defaultColor toward shadowColor at full severity.")]
    public float denyToShadow = 0.75f;

    [Range(0f, 1f)]
    [Tooltip("Additional alpha added on charge at full appetite (on top of baseAlpha).")]
    public float chargeAlphaBoost = 0.35f;

    [Range(0f, 1f)]
    [Tooltip("Additional alpha added on deny at full severity (on top of baseAlpha).")]
    public float denyAlphaBoost = 0.25f;

    [TextArea]
    public string description;

    [Header("Behavior & Presets")]
    public NoteBehavior defaultBehavior;
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
    public Color GetChargeColor(float appetite01)
    {
        float a = Mathf.Clamp01(appetite01);

        // Hue-preserving brighten: move toward white, but keep it role-coded.
        var baseC = GetBaseColor();
        var c = Color.Lerp(baseC, Color.white, chargeToWhite * a);

        // Optional: keep charge readable without forcing baseline to be blinding.
        c.a = Mathf.Clamp01(baseAlpha + chargeAlphaBoost * a);
        return c;
    }

    public Color GetDenyColor(float severity01)
    {
        float s = Mathf.Clamp01(severity01);

        var baseC = GetBaseColor();

        // Deny toward shadow hue (role-specific).
        var shadow = dustColors.shadowColor;
        shadow.a = baseC.a; // keep alpha authority separate
        var c = Color.Lerp(baseC, shadow, denyToShadow * s);

        c.a = Mathf.Clamp01(baseAlpha + denyAlphaBoost * s);
        return c;
    }

    public float GetDustHardness01() => Mathf.Clamp01(dustHardness01);
}
