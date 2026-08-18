using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;

public enum MusicalRole
{
    Bass,
    Harmony,
    Lead,
    Groove,
    None,
    Rhythm
}

public enum RoleConfigSelectionMode { ByBin, ByVoice, RandomPerBin }
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
    [Tooltip("Baseline alpha for this role when used as a semantic color (track / DiscoveryTrackNode / imprints).")]
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

    [Header("DiscoveryTrackNode Locomotion")]
    [Tooltip("Direct locomotion profile override for this role.")]
    public DiscoveryTrackNodeLocomotionProfile mineNodeLocomotionProfile;

    [Tooltip("Shared per-archetype tuning for role-specific DiscoveryTrackNode/Collectable movement (orbital, burst/pause, swerve). Only relevant for Harmony/Groove/Lead roles — leave unassigned for Bass/None/Rhythm.")]
    public MusicalRoleMovementProfile movementArchetype;

    [Tooltip("DiscoveryTrackNode only (Darting/Lead corridor scoring): grid-cell radius within which a Vehicle biases direction away. Collectables never use this — their evasion is collectableFleeRadiusCells.")]
    [Range(0f, 10f)] public float evasionCells = 3f;

    [Header("Collectable Movement")]
    [Tooltip("Global speed multiplier for this role's collectable motion — scales drift, role pulses (bass slam, lead pulse, harmony surge), and flee. 1 = baseline feel (2.4 u/s drift). MIDI velocity still modulates drift ±25%. Does not affect DiscoveryTrackNode speed.")]
    [Min(0f)] public float collectableSpeed = 1f;

    [Tooltip("Seconds to ramp from rest to full drift speed. Lower = snappier direction changes; also governs glide-to-rest and tether correction.")]
    [Range(0.02f, 1f)] public float collectableAccelSeconds = 0.12f;

    [Tooltip("Seconds of reduced steering after a dust collision so the bounce reads before the collectable re-charges.")]
    [Min(0f)] public float collectableBounceRecoverSeconds = 0.45f;

    [Tooltip("Free-move radius around the spawn destination, in grid cells. Outside it the home pull ramps up. Smaller = tighter cage (Lead).")]
    [Min(0f)] public float collectableHomeRadiusCells = 1.5f;

    [Tooltip("Inward pull speed gained per world unit beyond the free radius (u/s per u). Higher = snappier return; the pattern can stray ~patternSpeed/this beyond the radius.")]
    [Min(0f)] public float collectableHomePullPerUnit = 1.5f;

    [Tooltip("Vehicle distance (grid cells) that triggers fleeing. Flee ends at 1.5× this distance, then the home tether pulls the note back. 0 = never flees.")]
    [Min(0f)] public float collectableFleeRadiusCells = 2f;

    [Tooltip("Speed multiplier while fleeing a vehicle (× the note's normal speed).")]
    [Range(1f, 3f)] public float collectableFleeSpeedMul = 1.3f;

    [Header("Presets")] public int midiPreset;

    [Header("MIDI Range")]
    [Tooltip("Lowest MIDI note this role's preset can play musically. Notes below this are octave-shifted up.")]
    public int lowestNote = 36;
    [Tooltip("Highest MIDI note this role's preset can play musically. Notes above this are octave-shifted down.")]
    public int highestNote = 84;

    [Header("Audio Mix")]
    [Tooltip("Mixer bus this role's MidiStreamPlayer AudioSource routes to (ProceduralMixer's per-role groups). Leave unassigned to keep whatever the scene's shared MidiStreamPlayer template is already routed to.")]
    public AudioMixerGroup outputMixerGroup;
    [Range(0f, 2f)]
    [Tooltip("Per-role MPTK channel volume trim (pure gain multiply, applied per note without altering velocity/timbre). 1 = unchanged.")]
    public float volumeMultiplier = 1f;
    [Range(1, 127)]
    [Tooltip("Floor of the velocity clamp applied to this role's notes before playback. Narrowing the min/max range compresses loudness swing between quiet and loud notes.")]
    public int minVelocity = 60;
    [Range(1, 127)]
    [Tooltip("Ceiling of the velocity clamp applied to this role's notes before playback.")]
    public int maxVelocity = 127;

    [Header("Regrowth")]
    [Tooltip("Seconds before this cell regrows after being carved. −1 = use the active maze pattern's default.")]
    public float regrowthDelay = -1f;

    [Header("Chord Voices (Advanced)")]
    [Tooltip("ByBin: configs rotate by bin index (default for all roles). ByVoice: configs are pinned by voiceIndex — voice 0 always uses config[0], voice 1 uses config[1], etc. RandomPerBin: one config is drawn independently at random per bin, stable for the session, varies across playthroughs.")]
    public RoleConfigSelectionMode configSelectionMode = RoleConfigSelectionMode.ByBin;
    [Tooltip("Override colors for chord voices 1, 2, … (voice 0 uses dustColors.baseColor). Only relevant when configSelectionMode = ByVoice.")]
    public Color[] chordVoiceColors;
    [Tooltip("If true, every bin's riff for this role anchors to the motif's first/tonic chord root (chordRegion[0]) instead of walking the chord progression bin-by-bin. Lets a role hold one harmonic center across bins — e.g. a solo/lead pieced together from distinct per-bin riffs, or an arpeggio that shouldn't re-root each bin. Authored per-bin chord overrides still take priority. Only the bin-0-resolved profile governs this for the whole motif — same rule as configSelectionMode.")]
    public bool sameRootAcrossBins = false;

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
