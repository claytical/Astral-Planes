using UnityEngine;

public enum PhasePersonality { Establish, Evolve, Intensify, Release, Wildcard, Pop, DarkStar }
public enum EjectionStyle { Burst, Spiral, ArcForward, LineScatter }
public enum ExpirePolicy { WaitForAll, ExpireAndAdvance, RecycleAsShards }
public enum NoteVisibility { PulseOnStep, HideInDust, AlwaysVisible }

[CreateAssetMenu(menuName="Astral Planes/PhaseStar Behavior Profile")]
public class PhaseStarBehaviorProfile : ScriptableObject
{
    [Header("Identity & Mood")]
    public PhasePersonality personality;
    public Color starTint = Color.white;
    [Range(0.25f,3f)] public float particlePulseSpeed = 1f;
    [Range(0f,1f)] public float starAlphaMin = 0.1f, starAlphaMax = 1f;

    [Header("Star Drift")]
    [Tooltip("Base drift speed of the PhaseStar body (not the nodes).")]
    public float starDriftSpeed = 0.6f;
    [Tooltip("Random steering jitter; higher feels erratic/trickster.")]
    public float starDriftJitter = 0.15f;
    [Range(0f,1f)] public float orbitBias = 0.0f;     // tendency to arc
    [Range(0f,0.2f)] public float teleportChancePerSec = 0f; // Wildcard spice (0=off)

    [Header("Dust Pruning / Feeding")]
    public float dustShrinkRadius = 6f;
    public float dustShrinkUnitsPerSec = 1.2f;
    public AnimationCurve dustFalloff = AnimationCurve.Linear(0,1,1,0);
    [Tooltip("If true (Dark Star), dust grows/lingers rather than fades.")]
    public bool feedsDust = false;
    [Range(0.1f,2f)] public float dustRegrowDelayMul = 1f; // >1 = slower regrow

    [Header("MineNode Ejection")]
    public EjectionStyle ejectionStyle = EjectionStyle.Burst;
    [Min(1)] public int nodesPerStar = 3;     // how many nodes total
    public float scatterRadius = 1.6f;        // local spawn scatter
    public float nodeFlightSeconds = 0.6f;    // shard -> grid travel time
    public AnimationCurve nodeFlightEase = AnimationCurve.EaseInOut(0,0,1,1);
    [Range(0f,0.2f)] public float ejectionStagger = 0.03f; // spacing for correction/missed

    [Header("Ghost / Collectable Rules")]
    public ExpirePolicy expirePolicy = ExpirePolicy.WaitForAll;
    public NoteVisibility noteVisibility = NoteVisibility.PulseOnStep;
    public bool emitMissedAsCorrection = true;     // current “missed correction” burst
    public bool allowShardRecyclingWhenMissed = false; // turn misses into ShardPickup bursts
    [Range(0f,1f)] public float shardHavocChance = 0f;  // 0 = always helpful, 1 = mostly havoc

    [Header("MineNode (role-aware) movement base")]
    [Tooltip("Global cap for node speed. Roles & phase multipliers ride on top.")]
    public float nodeBaseMaxSpeed = 3.0f;
    public float nodeBaseMaxForce = 10f;
    [Tooltip("Describes the species’ temperament for gaits / steering.")]
    public float leadFrolicBias = 0.6f;
    public float bassHideBias   = 0.6f;
    public float grooveEvadeBias = 0.6f;
    public float harmonyOrbitBias = 0.6f;

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

    // Resolve phase speed multiplier (for MineNodeLocomotion)
    public float GetPhaseSpeedMul(PhasePersonality p)
    {
        return p switch
        {
            PhasePersonality.Establish => establishSpeedMul,
            PhasePersonality.Evolve    => evolveSpeedMul,
            PhasePersonality.Intensify => intensifySpeedMul,
            PhasePersonality.Release   => releaseSpeedMul,
            PhasePersonality.Wildcard  => wildcardSpeedMul,
            PhasePersonality.Pop       => popSpeedMul,
            PhasePersonality.DarkStar  => evolveSpeedMul, // or unique mul
            _ => 1f
        };
    }

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
