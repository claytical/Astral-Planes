using System.Collections.Generic;
using UnityEngine;

#region Lightweight helper types (design-facing)

[System.Serializable]
public class PhaseStarEmotion
{
    [Tooltip("How often the star changes its preferred direction (higher = more jittery/curious).")]
    public float curiosity;

    [Tooltip("How strongly the star avoids vehicles (higher = keeps distance).")]
    public float fear;

    [Tooltip("How aggressively the star prunes dust when it is allowed to prune (higher = faster).")]
    public float appetite;
}

public enum PhasePersonality { Establish, Evolve, Intensify, Release, Wildcard, Pop, DarkStar }
public enum EjectionStyle     { Burst, Spiral, ArcForward, LineScatter }
public enum ExpirePolicy      { WaitForAll, ExpireAndAdvance, RecycleAsShards }
public enum NoteVisibility    { PulseOnStep, HideInDust, AlwaysVisible }

#endregion

/// <summary>
/// Authoring surface for PhaseStar difficulty / play-type behavior.
/// IMPORTANT:
/// - Keep "Core star gameplay levers" small and obvious.
/// - Put visual-only tuning under "Visual" headers.
/// - Put component-specific tuning under the component header (Motion/Dust/etc.).
/// </summary>
[CreateAssetMenu(menuName = "Astral Planes/PhaseStar Behavior Profile")]
public class PhaseStarBehaviorProfile : ScriptableObject
{
    // =====================================================================
    // Core identity (lightweight; mainly informs visuals & authoring clarity)
    // =====================================================================
    [Header("Identity")]
    public PhasePersonality personality;

    [Tooltip("Primary color associated with this profile (used for maze tinting / UI).")]
    public Color mazeColor = Color.white;

    // =====================================================================
    // Core star gameplay levers (PhaseStar.cs)
    // =====================================================================
    [Header("Core Star Gameplay (PhaseStar.cs)")]
    [Min(1)]
    [Tooltip("How many MineNodes this PhaseStar will eject before it transitions to bridge/completion.")]
    public int nodesPerStar = 3;

    [Tooltip("Tint applied to petals that are no longer active/available.")]
    public Color inactiveShardTint = new(0.30f, 0.30f, 0.30f, 0.60f);

    [Tooltip("If enabled, the PhaseStar requests a keep-clear pocket in the dust so players always have maneuvering space near it.")]
    public bool starKeepsDustClear = true;

    [Min(0)]
    [Tooltip("Radius in grid cells for the keep-clear pocket when the star is idle/armed.")]
    public int starKeepClearRadiusCells = 2;

    [Header("Safety Bubble (on poke)")]
    [Tooltip("If enabled, the star spawns a temporary safety bubble on poke (used to let players escape / carve fairly).")]
    public bool enableSafetyBubble = true;

    [Min(0)]
    [Tooltip("Bubble radius in grid cells. Primary beginner-friendliness lever.")]
    public int safetyBubbleRadiusCells = 4;

    [Header("Selection Rotation (agency)")]
    [Tooltip("If true, the highlighted preview shard rotates by 1 at each loop boundary while the star is idle/armed.")]
    public bool rotateSelectionOnLoopBoundary = true;

    [Min(1)]
    [Tooltip("If rotateSelectionOnLoopBoundary is true, rotate every N loop boundaries. (Future: step-synced scheduling can layer on top.)")]
    public int rotateEveryNLoops = 1;

    [Header("Self-heal / Deadlock Recovery")]
    [Min(0)]
    [Tooltip("If waiting for collectables to clear but nothing is in flight, re-arm/advance after this many loop boundaries. 0 disables.")]
    public int collectableClearTimeoutLoops = 2;

    [Tooltip("Secondary timeout in seconds (DSP time). Used if loop counter is unavailable. <= 0 disables.")]
    public float collectableClearTimeoutSeconds = 0f;

    // =====================================================================
    // Preview ring & tempo coupling (visual + readability)
    // =====================================================================
    [Header("Preview Ring (visual rhythm)")]
    [Range(0.25f, 3f)] public float particlePulseSpeed = 1f;
    [Range(0f, 1f)]    public float starAlphaMin = 0.10f;
    [Range(0f, 1f)]    public float starAlphaMax = 1.00f;

    [Tooltip("Roles that this star will spawn, in order. If empty, generated dynamically by SpawnStrategyProfile.")]
    public List<MusicalRole> rolePattern = new();

    [Range(0.1f, 2f)]
    [Tooltip("Scale multiplier applied during preview pulse.")]
    public float previewPulseScale = 1.2f;

    [Header("Preview Rotation (legacy wheel)")]
    // NOTE: RotationMode is defined elsewhere in your project.
    public RotationMode rotationMode = RotationMode.PerBin;

    [Range(0.25f, 2f)]
    [Tooltip("Minimum interval (seconds) between preview changes when not loop-synced.")]
    public float minPreviewIntervalSec = 0.5f;

    [Range(1, 8)]
    [Tooltip("How many beats before the preview advances to the next role/petal (if using beat-driven advance).")]
    public int beatsPerRole = 4;

    [Range(30, 180)]
    [Tooltip("Fallback BPM when no DrumTrack is active.")]
    public float fallbackBPM = 60f;

    public AnimationCurve pulseCurve = AnimationCurve.EaseInOut(0, 0.8f, 1, 1.2f);

    [Range(0.25f, 2f)]
    [Tooltip("Global multiplier applied to preview timing based on personality.")]
    public float personalitySpeedMul = 1f;

    [Tooltip("Optional mapping from BPM to a speed multiplier (for visual timing only).")]
    public AnimationCurve bpmToSpeedMul = AnimationCurve.Linear(60, 1f, 160, 1f);

    [Range(-0.5f, 0.5f)]
    [Tooltip("Authoring spice: small variation range applied to progression speed (kept here for legacy compatibility).")]
    public float progressionMulRange = 0.0f;

    // =====================================================================
    // Star body motion (PhaseStarMotion2D)
    // =====================================================================
    [Header("Star Motion (PhaseStarMotion2D)")]
    [Tooltip("Base drift speed of the PhaseStar body (not the nodes).")]
    public float starDriftSpeed = 0.6f;

    [Tooltip("Random steering jitter; higher feels erratic/trickster.")]
    public float starDriftJitter = 0.15f;

    [Range(0f, 1f)]
    [Tooltip("Tendency to arc around avoidance vectors (0 = straight avoidance, 1 = strong tangential orbit).")]
    public float orbitBias = 0f;

    [Range(0f, 0.2f)]
    [Tooltip("Chance per second to teleport a small amount (Wildcard spice). 0 disables.")]
    public float teleportChancePerSec = 0f;

    // =====================================================================
    // Dust interaction (PhaseStarDustAffect / MineNodeDustInteractor)
    // =====================================================================
    [Header("Dust Interaction (PhaseStarDustAffect / MineNodeDustInteractor)")]
    [Tooltip("Radius (world units) for dust pruning. Larger = eats bigger chunks.")]
    public float dustShrinkRadius = 6f;

    [Tooltip("Strength scalar used by dust pruning logic (units/sec or equivalent).")]
    public float dustShrinkUnitsPerSec = 1.2f;

    [Tooltip("Falloff curve from center->edge for dust pruning.")]
    public AnimationCurve dustFalloff = AnimationCurve.Linear(0, 1, 1, 0);

    [Range(0.1f, 2f)]
    [Tooltip(">1 = slower dust regrow; <1 = faster dust regrow. Primary maze difficulty lever.")]
    public float dustRegrowDelayMul = 1f;

    [Header("Vehicle-in-dust tuning (legacy)")]
    [Tooltip("Max speed multiplier while inside dust.")]
    public float dustSpeedCapMul = 0.9f;

    [Tooltip("Extra braking applied while inside dust.")]
    public float dustExtraBrake = 0.25f;

    [Tooltip("Lateral movement multiplier while inside dust.")]
    public float dustLateralMul = 1.0f;

    [Tooltip("Turbulence multiplier while inside dust.")]
    public float dustTurbulenceMul = 1.0f;

    [Header("Obsolete / Legacy")]
    [Tooltip("OBSOLETE. Kept only to preserve old assets. Do not use.")]
    public bool feedsDust = false;

    // =====================================================================
    // MineNode ejection (PhaseStar.cs visuals + MineNode flight)
    // =====================================================================
    [Header("MineNode Ejection (visual + feel)")]
    public EjectionStyle ejectionStyle = EjectionStyle.Burst;

    [Tooltip("Optional prefab used for ejection VFX/markers.")]
    public GameObject ejectionPrefab;

    [Tooltip("Local spawn scatter around contact point (world units).")]
    public float scatterRadius = 1.6f;

    [Tooltip("VISUAL ONLY: time for the shard/node to fly to its target grid cell.")]
    public float nodeFlightSeconds = 0.6f;

    public AnimationCurve nodeFlightEase = AnimationCurve.EaseInOut(0, 0, 1, 1);

    [Range(0f, 0.2f)]
    [Tooltip("Extra spacing to help with correction/missed collisions.")]
    public float ejectionStagger = 0.03f;

    // =====================================================================
    // Bridge / collectable policy (likely consumed by other systems)
    // =====================================================================
    [Header("Ghost / Collectable Rules")]
    public ExpirePolicy expirePolicy = ExpirePolicy.WaitForAll;

    [Tooltip("If true, misses can recycle into ShardPickup bursts.")]
    public bool allowShardRecyclingWhenMissed;

    // =====================================================================
    // Per-phase node speed multipliers (MineNode locomotion)
    // =====================================================================
    [Header("MineNode Speed Multipliers (MineNodeLocomotion)")]
    public float establishSpeedMul  = 0.8f;
    public float evolveSpeedMul     = 1.0f;
    public float intensifySpeedMul  = 1.2f;
    public float releaseSpeedMul    = 0.9f;
    public float wildcardSpeedMul   = 1.35f;
    public float popSpeedMul        = 1.1f;

    // =====================================================================
    // Wiggle motion (WiggleMotion)
    // =====================================================================
    [Header("Wiggle (WiggleMotion)")]
    public float wiggleTorqueStrength = 1f;
    public float wiggleFrequency = 2f;
    public bool  wiggleDrift = false;
    public float wiggleDriftAmplitude = 0.05f;
    public float wiggleDriftFrequency = 1f;

    // =====================================================================
    // Role-specific deltas (MineRoleTuning)
    // =====================================================================
    [Header("Role-specific tuning (MineRoleTuning)")]
    public MineRoleTuning bass;
    public MineRoleTuning harmony;
    public MineRoleTuning lead;
    public MineRoleTuning groove;

    // =====================================================================
    // Runtime-only scratch (do not author)
    // =====================================================================
    [System.NonSerialized]
    public float previewSpinDps;

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
