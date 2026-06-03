using System.Collections.Generic;
using UnityEngine;

public enum MusicalRole
{
    Bass,
    Harmony,
    Lead,
    Groove,
    None,
    Rhythm
}

public enum RoleConfigSelectionMode { ByBin, ByVoice }
[System.Serializable]
public struct DustColorSet
{
    public Color baseColor;    // neutral presence
    public Color shadowColor;  // regrowth / memory / deny feedback
}

[CreateAssetMenu(menuName = "Astral Planes/Musical Role Profile", fileName = "NewMusicalRoleProfile")]
public class MusicalRoleProfile : ScriptableObject
{
    [System.Serializable]
    public struct MineNodeArchetypeVariant
    {
        public string variantId;
        public string archetypeId;
    }

    public MusicalRole role;

    [Header("Visuals & Styling")]
    public DustColorSet dustColors;
    [Range(0f, 1f)]
    [Tooltip("Baseline alpha for this role when used as a semantic color (track / MineNode / imprints).")]
    public float baseAlpha = 0.25f;


    [Header("Dust Energy")]
    [Tooltip("HP of this dust cell. Cells with unbypassable resistance require (maxEnergyUnits ÷ plowChipAmount) plow ticks to clear.")]
    [Min(1)] public int maxEnergyUnits = 1;

    [Range(0f, 1f)]
    [Tooltip("How hard this cell is to carve. 0 = instant carve, 1 = fully blocked. Intermediate values slow and drain the vehicle proportionally.")]
    public float carveResistance01 = 0.50f;

    [Range(0f, 1f)]
    [Tooltip("How much this cell resists boost energy drain on contact. 0 = full drain rate, 1 = no drain.")]
    public float drainResistance01 = 0.50f;

    [Header("MineNode Locomotion")]
    [Tooltip("Base speed for this role's MineNode. Applied at init; category multipliers are layered on top in FixedUpdateDrifting.")]
    [Range(0f, 1f)] public float mineNodeSpeed = 0.5f;

    [Tooltip("Direct locomotion profile override for this role.")]
    public MineNodeLocomotionProfile mineNodeLocomotionProfile;

    [Header("MineNode Decision Archetype")]
    [Tooltip("Default decision archetype id (e.g. Steady, Aggressive, Skittish, Darting).")]
    public string defaultMineNodeArchetypeId = "Steady";

    [Tooltip("Optional role-specific variant overrides (e.g. Lead_A, Lead_B).")]
    public MineNodeArchetypeVariant[] mineNodeArchetypeVariants;

    [Header("MineNode Behavior Modifiers")]
    [Tooltip("Scales base speed within this role's behavioral category. 1.0 = default. Electronic Bass ~1.3, Acoustic Bass ~0.7.")]
    [Range(0.3f, 2.5f)] public float behaviorSpeedMultiplier = 1.0f;

    [Tooltip("Deliberate (Bass): strength of territory affinity bias in corridor scoring.")]
    [Range(0f, 1f)] public float territoryAffinity01 = 0.3f;

    [Tooltip("Deliberate (Bass): multiplier on pathCommitmentDuration. Higher = slower to change direction.")]
    [Range(0.5f, 3f)] public float commitDurationScale = 1.0f;

    [Tooltip("Orbital (Harmony): weight added to perpendicular-curve directions in corridor scoring. 0 = no orbital bias.")]
    [Range(0f, 1.5f)] public float orbitalTurnBias = 0.6f;

    [Tooltip("Rhythmic (Groove): seconds at burst speed before snapping to next beat boundary.")]
    [Min(0.1f)] public float burstDuration = 0.4f;

    [Tooltip("Rhythmic (Groove): seconds at near-standstill after each burst.")]
    [Min(0.05f)] public float pauseDuration = 0.35f;

    [Tooltip("Rhythmic (Groove): speed multiplier during burst phase relative to base driftSpeedMultiplier.")]
    [Range(1f, 3f)] public float burstSpeedMultiplier = 2.0f;

    [Tooltip("Darting (Lead): grid-cell radius within which a Vehicle triggers directional evasion. Keep small (2–4) for 1v1 pursuit — fires only on close approach.")]
    [Range(0f, 10f)] public float evasionCells = 3f;

    [Header("Chord Voices")]
    [Tooltip("ByBin: configs rotate by bin index (default for all roles). ByVoice: configs are pinned by voiceIndex — voice 0 always uses config[0], voice 1 uses config[1], etc.")]
    public RoleConfigSelectionMode configSelectionMode = RoleConfigSelectionMode.ByBin;
    [Tooltip("Override colors for chord voices 1, 2, … (voice 0 uses dustColors.baseColor). Only relevant when configSelectionMode = ByVoice.")]
    public Color[] chordVoiceColors;

    [Header("Presets")] public int midiPreset;

    [Header("MIDI Range")]
    [Tooltip("Lowest MIDI note this role's preset can play musically. Notes below this are octave-shifted up.")]
    public int lowestNote = 36;
    [Tooltip("Highest MIDI note this role's preset can play musically. Notes above this are octave-shifted down.")]
    public int highestNote = 84;

    [Header("Ripeness / Decay")]
    [Tooltip("How long (seconds) the revealed role color stays visible before fading back to gray. Must be > 0.")]
    public float ripeDuration = 8f;

    [Tooltip("Seconds before this cell regrows after being carved. −1 = use the active maze pattern's default.")]
    public float regrowthDelay = -1f;

    // ---------- Color helpers (authority) ----------

    public Color GetBaseColor()
    {
        var c = dustColors.baseColor;
        c.a = baseAlpha;
        return c;
    }

    public Color GetColorForVoice(int voiceIndex)
    {
        if (voiceIndex <= 0 || configSelectionMode != RoleConfigSelectionMode.ByVoice ||
            chordVoiceColors == null || voiceIndex - 1 >= chordVoiceColors.Length)
            return GetBaseColor();
        var c = chordVoiceColors[voiceIndex - 1];
        c.a = baseAlpha;
        return c;
    }
    public Color GetRandomVoiceColor()
    {
        if (configSelectionMode != RoleConfigSelectionMode.ByVoice ||
            chordVoiceColors == null || chordVoiceColors.Length == 0)
            return GetBaseColor();
        var c = chordVoiceColors[Random.Range(0, chordVoiceColors.Length)];
        c.a = baseAlpha;
        return c;
    }

    public Color GetShadowColor()
    {
        var c = dustColors.shadowColor;
        c.a = baseAlpha;
        return c;
    }
    public float GetCarveResistance01() => Mathf.Clamp01(carveResistance01);
    public float GetDrainResistance01() => Mathf.Clamp01(drainResistance01);

    public string ResolveMineNodeArchetypeId(string variantId)
    {
        if (!string.IsNullOrWhiteSpace(variantId) && mineNodeArchetypeVariants != null)
        {
            for (int i = 0; i < mineNodeArchetypeVariants.Length; i++)
            {
                var candidate = mineNodeArchetypeVariants[i];
                if (string.Equals(candidate.variantId, variantId, System.StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(candidate.archetypeId))
                    return candidate.archetypeId;
            }
        }

        return string.IsNullOrWhiteSpace(defaultMineNodeArchetypeId) ? "Steady" : defaultMineNodeArchetypeId;
    }
}
