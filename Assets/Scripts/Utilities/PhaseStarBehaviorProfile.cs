using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

public enum PhasePersonality { Establish, Evolve, Intensify, Release, Wildcard, Pop, DarkStar }
public enum EjectionStyle { Burst, Spiral, ArcForward, LineScatter }
public enum ExpirePolicy { WaitForAll, ExpireAndAdvance, RecycleAsShards }
public enum NoteVisibility { PulseOnStep, HideInDust, AlwaysVisible }

[CreateAssetMenu(menuName="Astral Planes/PhaseStar Behavior Profile")]
public class PhaseStarBehaviorProfile : ScriptableObject
{
    // ---------------------------------------------------------------------
    // Identity & Visual Mood
    // ---------------------------------------------------------------------
    [Header("Identity")]
    public PhasePersonality personality;

    [Header("Maze Visuals")]
    public Color mazeColor = Color.white;

    [Header("Star Visuals")]
    [Range(0.25f, 3f)] public float particlePulseSpeed = 1f;
    [Range(0f, 1f)] public float starAlphaMin = 0.1f, starAlphaMax = 1f;

    [Header("Role Pattern (Petals)")]
    [Tooltip("Roles that this star will spawn, in order. If empty, generated dynamically.")]
    public List<MusicalRole> rolePattern = new();

    // ---------------------------------------------------------------------
    // Preview / UI Ring (Design-facing)
    // ---------------------------------------------------------------------
    [Header("Preview Ring")]
    [Range(0.1f, 2f)] public float previewPulseScale = 1.2f;
    public AnimationCurve pulseCurve = AnimationCurve.EaseInOut(0, 0.8f, 1, 1.2f);
    public Color inactiveShardTint = new(0.3f, 0.3f, 0.3f, 0.6f);

    [Header("Preview Timing")]
    [Tooltip("How many beats before the preview advances to the next role/petal.")]
    [Range(1, 8)] public int beatsPerRole = 4;
    [Tooltip("Fallback BPM when no DrumTrack is active.")]
    [Range(30, 180)] public float fallbackBPM = 60f;

    [Header("Preview Rotation (Advanced)")]
    public RotationMode rotationMode = RotationMode.PerBin;
    [Range(0.25f, 2f)] public float minPreviewIntervalSec = 0.5f;
    [Range(0.25f, 2f)] public float personalitySpeedMul = 1f;
    public AnimationCurve bpmToSpeedMul = AnimationCurve.Linear(60, 1f, 160, 1f);
    [Range(-0.5f, 0.5f)] public float progressionMulRange = 0.0f;

    // ---------------------------------------------------------------------
    // Star Motion (High-level only; detailed steering lives on PhaseStarMotion2D)
    // ---------------------------------------------------------------------
    [Header("Star Motion (High-level)")]
    [Tooltip("Base drift speed of the PhaseStar body (not the nodes).")]
    public float starDriftSpeed = 0.6f;

    [Tooltip("Random steering jitter; higher feels erratic/trickster.")]
    public float starDriftJitter = 0.15f;

    [Range(0f, 1f)] public float orbitBias;               // tendency to arc
    [Range(0f, 0.2f)] public float teleportChancePerSec;  // Wildcard spice (0=off)

    // ---------------------------------------------------------------------
    // Maze / Space Control (drives CosmicDustGenerator)
    // ---------------------------------------------------------------------
    [Header("Maze / Space Control")]
    [Range(0.1f, 10f)]
    [Tooltip(">1 = slower regrow / gentler closure pressure. <1 = faster regrow / higher pressure.")]
    public float dustRegrowDelayMul = 1f;

    [Header("Star Keep-Clear Pocket")]
    [Tooltip("If true, the star maintains a cleared maneuvering pocket around itself.")]
    public bool starKeepsDustClear = true;
    [Min(0)] public int starKeepClearRadiusCells = 2;

    [Header("Safety Bubble (On Poke)")]
    [Tooltip("If true, a larger temporary cleared bubble is spawned when the star is poked/struck.")]
    public bool enableSafetyBubble = true;
    [Min(0)] public int safetyBubbleRadiusCells = 4;
    [Tooltip("Keep-clear radius (cells) while the safety bubble is active. Often matches safetyBubbleRadiusCells.")]
    [Min(0)] public int bubbleRadiusCells = 4;

    // ---------------------------------------------------------------------
    // Dust Erosion / Pruning (Star eats dust)
    // ---------------------------------------------------------------------
    [Header("Dust Erosion (Star Pruning)")]
    [FormerlySerializedAs("dustShrinkRadius")]
    [Tooltip("World radius used when the star erodes dust.")]
    public float dustErodeRadiusWorld = 6f;

    [FormerlySerializedAs("dustShrinkUnitsPerSec")]
    [Tooltip("Erosion rate scalar used by PhaseStarDustAffect (units per second / appetite).")]
    public float dustErodeUnitsPerSecond = 1.2f;

    [FormerlySerializedAs("dustFalloff")]
    public AnimationCurve dustErodeFalloff = AnimationCurve.Linear(0, 1, 1, 0);

    // ---------------------------------------------------------------------
    // MineNode Ejection (Shard -> Node creation)
    // ---------------------------------------------------------------------
    [Header("MineNode Ejection")]
    public EjectionStyle ejectionStyle = EjectionStyle.Burst;
    public GameObject ejectionPrefab;
    [Min(1)] public int nodesPerStar = 3;
    public float scatterRadius = 1.6f;
    public float nodeFlightSeconds = 0.6f;
    public AnimationCurve nodeFlightEase = AnimationCurve.EaseInOut(0, 0, 1, 1);
    [Range(0f, 0.2f)] public float ejectionStagger = 0.03f;

    // ---------------------------------------------------------------------
    // Ghost / Collectable rules
    // ---------------------------------------------------------------------
    [Header("Ghost / Collectable Rules")]
    public ExpirePolicy expirePolicy = ExpirePolicy.WaitForAll;
    [Tooltip("If true, missed nodes can recycle into ShardPickup bursts.")]
    public bool allowShardRecyclingWhenMissed;

    // ---------------------------------------------------------------------
    // Node locomotion (role-independent, phase-flavored)
    // ---------------------------------------------------------------------
    [Header("MineNode Speed Multipliers (by personality)")]
    public float establishSpeedMul = 0.8f;
    public float evolveSpeedMul     = 1.0f;
    public float intensifySpeedMul  = 1.2f;
    public float releaseSpeedMul    = 0.9f;
    public float wildcardSpeedMul   = 1.35f;
    public float popSpeedMul        = 1.1f;

    // ---------------------------------------------------------------------
    // Dust interaction (applied to MineNodeDustInteractor / Vehicle dust feel)
    // ---------------------------------------------------------------------
    [Header("Dust Interaction (Movement Feel)")]
    public float dustSpeedCapMul = 0.9f;
    public float dustExtraBrake = 0.25f;
    public float dustLateralMul = 1.0f;
    public float dustTurbulenceMul = 1.0f;

    // ---------------------------------------------------------------------
    // Wiggle (advanced, applied to WiggleMotion)
    // ---------------------------------------------------------------------
    [Header("Wiggle (Advanced)")]
    public float wiggleTorqueStrength = 1f;
    public float wiggleFrequency = 2f;
    public bool  wiggleDrift = false;
    public float wiggleDriftAmplitude = 0.05f;
    public float wiggleDriftFrequency = 1f;

    // ---------------------------------------------------------------------
    // Role tuning overrides (designer knobs per musical role)
    // ---------------------------------------------------------------------
    [Header("Role Tuning Overrides")]
    public MineRoleTuning bass;
    public MineRoleTuning harmony;
    public MineRoleTuning lead;
    public MineRoleTuning groove;

    // ---------------------------------------------------------------------
    // Legacy / DarkStar leftovers (keep, but isolate)
    // ---------------------------------------------------------------------
    [Header("Legacy / DarkStar (Isolated)")]
    public float starHoleRadius;
    public float darkCleanupMaxLoops;

    public MineRoleTuning GetRoleTuning(MusicalRole role)
    {
        return role switch
        {
            MusicalRole.Bass    => bass,
            MusicalRole.Harmony => harmony,
            MusicalRole.Lead    => lead,
            MusicalRole.Groove  => groove,
            _ => lead
        };
    }
}
