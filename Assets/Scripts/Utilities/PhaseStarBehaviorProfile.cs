using System.Collections.Generic;
using UnityEngine;
[System.Serializable]
public class PhaseStarEmotion {
    public float curiosity;   // how often it changes direction
    public float fear;        // distance kept from vehicles
    public float appetite;    // how fast it eats dust
}

public enum PhasePersonality { Establish, Evolve, Intensify, Release, Wildcard, Pop, DarkStar }
public enum EjectionStyle { Burst, Spiral, ArcForward, LineScatter }
public enum ExpirePolicy { WaitForAll, ExpireAndAdvance, RecycleAsShards }
public enum NoteVisibility { PulseOnStep, HideInDust, AlwaysVisible }

[CreateAssetMenu(menuName="Astral Planes/PhaseStar Behavior Profile")]
public class PhaseStarBehaviorProfile : ScriptableObject
{
    [Header("Identity & Mood")]
    public PhasePersonality personality;
    public Color mazeColor = Color.white;
    [Range(0.25f,3f)] public float particlePulseSpeed = 1f;
    [Range(0f,1f)] public float starAlphaMin = 0.1f, starAlphaMax = 1f;
    [Tooltip("Roles that this star will spawn, in order. If empty, generated dynamically.")]
    public List<MusicalRole> rolePattern = new();
    [Header("Preview Ring Settings")]
    [Range(0.1f, 2f)] public float previewPulseScale = 1.2f;
    [Header("Tempo / Advance Settings")]
    [Tooltip("How many beats before the preview advances to the next role/petal.")]
    [Range(1, 8)] public int beatsPerRole = 4;   // beginner-friendly default
    [Tooltip("Fallback BPM when no DrumTrack is active.")]
    [Range(30, 180)] public float fallbackBPM = 60f; // slower, readable default
    public AnimationCurve pulseCurve = AnimationCurve.EaseInOut(0, 0.8f, 1, 1.2f);
    public Color inactiveShardTint = new(0.3f, 0.3f, 0.3f, 0.6f);
    [Header("Preview Rotation (color wheel)")]
    public RotationMode rotationMode = RotationMode.PerBin;
    [Range(0.25f, 2f)] public float minPreviewIntervalSec = 0.5f;
    [Range(0.25f, 2f)] public float personalitySpeedMul = 1f;
    public AnimationCurve bpmToSpeedMul = AnimationCurve.Linear(60, 1f, 160, 1f);
    [Range(-0.5f, 0.5f)] public float progressionMulRange = 0.0f;

    [Header("Movement")]
    public float baseCellsPerSecond = 3.8f;
    [Tooltip("Base drift speed of the PhaseStar body (not the nodes).")]
    public float starDriftSpeed = 0.6f;
    [Tooltip("Random steering jitter; higher feels erratic/trickster.")]
    public float starDriftJitter = 0.15f;
    [Range(0f,1f)] public float orbitBias;     // tendency to arc
    [Range(0f,0.2f)] public float teleportChancePerSec; // Wildcard spice (0=off)

    [Header("Dust Pruning / Feeding")]
    public float dustShrinkRadius = 6f;
    public float dustShrinkUnitsPerSec = 1.2f;
    public AnimationCurve dustFalloff = AnimationCurve.Linear(0,1,1,0);

    [Tooltip("If true (Dark Star), dust grows/lingers rather than fades.")]
    public bool feedsDust;
    [Range(0.1f,10f)] public float dustRegrowDelayMul = 1f; // >1 = slower regrow

    [Header("Star Space Control (drives CosmicDustGenerator)")]
    [Tooltip("If true, the star maintains a cleared maneuvering pocket around itself.")]
    public bool starKeepsDustClear = true;
    [Min(0)] public int starKeepClearRadiusCells = 2;

    [Header("Safety Bubble (on poke)")]
    [Tooltip("If true, a larger temporary cleared bubble is spawned when the star is poked/struck.")]
    public bool enableSafetyBubble = true;
    [Min(0)] public int safetyBubbleRadiusCells = 4;
    [Tooltip("Keep-clear radius (cells) while the safety bubble is active. Often matches safetyBubbleRadiusCells.")]
    [Min(0)] public int bubbleRadiusCells = 4;

    [Header("MineNode Ejection")]
    public EjectionStyle ejectionStyle = EjectionStyle.Burst;
    public GameObject ejectionPrefab;
    [Min(1)] public int nodesPerStar = 3;     // how many nodes total
    public float scatterRadius = 1.6f;        // local spawn scatter
    public float nodeFlightSeconds = 0.6f;    // shard -> grid travel time
    public AnimationCurve nodeFlightEase = AnimationCurve.EaseInOut(0,0,1,1);
    [Range(0f,0.2f)] public float ejectionStagger = 0.03f; // spacing for correction/missed

    [Header("Ghost / Collectable Rules")]
    public ExpirePolicy expirePolicy = ExpirePolicy.WaitForAll;
    public bool allowShardRecyclingWhenMissed ; // turn misses into ShardPickup bursts
    
    [Header("Per-Phase speed multipliers for nodes")]
    public float establishSpeedMul = 0.8f;
    public float evolveSpeedMul     = 1.0f;
    public float intensifySpeedMul  = 1.2f;
    public float releaseSpeedMul    = 0.9f;
    public float wildcardSpeedMul   = 1.35f;
    public float popSpeedMul        = 1.1f;

    [Header("Dust Interaction (applied to MineNodeDustInteractor)")]
    public float dustSpeedCapMul = 0.9f;
    public float dustExtraBrake = 0.25f;
    public float dustLateralMul = 1.0f;
    public float dustTurbulenceMul = 1.0f;

    [Header("Wiggle (applied to WiggleMotion)")]
    public float wiggleTorqueStrength = 1f;
    public float wiggleFrequency = 2f;
    public bool  wiggleDrift = false;
    public float wiggleDriftAmplitude = 0.05f;
    public float wiggleDriftFrequency = 1f;

    [Header("Role-specific deltas (override/bias by role)")]
    public MineRoleTuning bass;
    public MineRoleTuning harmony;
    public MineRoleTuning lead;
    public MineRoleTuning groove;
    public float starHoleRadius;
    public float darkCleanupMaxLoops;
    public float previewSpinDps { get; set; }

    // Resolve phase speed multiplier (for MineNodeLocomotion)

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