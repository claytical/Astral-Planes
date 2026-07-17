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

    [Header("MineNode Behavior Modifiers")]
    [Tooltip("Orbital (Harmony): weight added to perpendicular-curve directions in corridor scoring. 0 = no orbital bias.")]
    [Range(0f, 1.5f)] public float orbitalTurnBias = 0.6f;

    [Tooltip("Rhythmic (Groove): seconds at burst speed before snapping to next beat boundary.")]
    [Min(0.1f)] public float burstDuration = 0.4f;

    [Tooltip("Rhythmic (Groove): seconds at near-standstill after each burst.")]
    [Min(0.05f)] public float pauseDuration = 0.35f;

    [Tooltip("MineNode only (Darting/Lead corridor scoring): grid-cell radius within which a Vehicle biases direction away. Collectables never use this — their evasion is collectableFleeRadiusCells.")]
    [Range(0f, 10f)] public float evasionCells = 3f;

    [Header("Collectable Movement")]
    [Tooltip("Global speed multiplier for this role's collectable motion — scales drift, role pulses (bass slam, lead pulse, harmony surge), and flee. 1 = baseline feel (2.4 u/s drift). MIDI velocity still modulates drift ±25%. Does not affect MineNode speed.")]
    [Min(0f)] public float collectableSpeed = 1f;

    [Tooltip("Seconds to ramp from rest to full drift speed. Lower = snappier direction changes; also governs glide-to-rest and tether correction.")]
    [Range(0.02f, 1f)] public float collectableAccelSeconds = 0.12f;

    [Tooltip("Seconds of reduced steering after a dust collision so the bounce reads before the collectable re-charges.")]
    [Min(0f)] public float collectableBounceRecoverSeconds = 0.45f;

    [Tooltip("Lead only: straight-line reach per pulse, in home-radius units. 1 = each pulse sweeps about one radius (before swerve curl); higher = bolder excursions.")]
    [Range(0.25f, 3f)] public float collectableTravelRadiiPerNote = 1.5f;

    [Tooltip("Free-move radius around the spawn destination, in grid cells. Outside it the home pull ramps up. Smaller = tighter cage (Lead).")]
    [Min(0f)] public float collectableHomeRadiusCells = 1.5f;

    [Tooltip("Inward pull speed gained per world unit beyond the free radius (u/s per u). Higher = snappier return; the pattern can stray ~patternSpeed/this beyond the radius.")]
    [Min(0f)] public float collectableHomePullPerUnit = 1.5f;

    [Tooltip("Lead only: peak swerve angle (deg) off the base heading for the serpentine weave.")]
    [Range(0f, 90f)] public float collectableSwerveDegrees = 65f;

    [Tooltip("Lead only: S-cycles completed over one note duration. Short notes flick quickly; long notes trace lazy curves.")]
    [Min(0.25f)] public float collectableSwerveCyclesPerNote = 1.5f;

    [Tooltip("Harmony only: arc (deg) swept around the home ring over one note duration during the pulse. Short notes dart; long chords sweep slowly.")]
    [Range(30f, 720f)] public float collectableOrbitArcDegreesPerNote = 240f;

    [Tooltip("Harmony only: fraction of role speed while orbiting between pulses (0 = rest still).")]
    [Range(0f, 1f)] public float collectableOrbitRestSpeedMul = 0.35f;

    [Tooltip("Vehicle distance (grid cells) that triggers fleeing. Flee ends at 1.5× this distance, then the home tether pulls the note back. 0 = never flees.")]
    [Min(0f)] public float collectableFleeRadiusCells = 2f;

    [Tooltip("Speed multiplier while fleeing a vehicle (× the note's normal speed).")]
    [Range(1f, 3f)] public float collectableFleeSpeedMul = 1.3f;

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

    [Header("Regrowth")]
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
}
