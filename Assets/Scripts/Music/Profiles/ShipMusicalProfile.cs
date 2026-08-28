using System.Collections.Generic;
using UnityEngine;

[System.Serializable]
public sealed class RoleRegrowDelayMultipliers
{
    [Min(0.05f)] public float bass = 1f;
    [Min(0.05f)] public float harmony = 1f;
    [Min(0.05f)] public float lead = 1f;
    [Min(0.05f)] public float groove = 1f;
    [Min(0.05f)] public float rhythm = 1f;
    [Min(0.05f)] public float gray = 1f;   // cells with no resolvable prospective role

    public float GetFor(MusicalRole role) => role switch
    {
        MusicalRole.Bass    => bass,
        MusicalRole.Harmony => harmony,
        MusicalRole.Lead    => lead,
        MusicalRole.Groove  => groove,
        MusicalRole.Rhythm  => rhythm,
        _                   => gray,
    };
}

[CreateAssetMenu(fileName = "ShipMusicalProfile", menuName = "Astral Planes/Ship Musical Profile")]
public class ShipMusicalProfile : ScriptableObject
{
    [Header("Identity & Music")]
    public string shipName;
    [Tooltip("A brief description of the ship's musical personality")]
    [TextArea(2, 4)] public string description;

    [Header("Movement (Arcade RB2D)")]
    public float arcadeMaxSpeed = 14f;
    public float arcadeAccel = 10f;
    public float arcadeBoostAccel = 70f;
    public float arcadeLinearDamping = 0.10f;
    public float arcadeAngularDamping = 0.50f;

    [Header("Coast / Stop")]
    public float coastBrakeForce = 1.0f;
    public float stopSpeed = 0.03f;
    public float stopAngularSpeed = 5f;
    public float inputDeadzone = 0.20f;
    
    [Header("Physics")]
    public float mass = 1.5f;

    [Header("Impact")]
    [Tooltip("Speed (world units/s) that counts as a full-strength collision hit. Used by GetForceAsDamage. Set to arcadeMaxSpeed for a ship that should deal full damage at top speed.")]
    public float impactSpeedCap = 32f;
    [Range(0.5f, 3f)]
    [Tooltip("Multiplier on arcadeMaxSpeed used as the velocity ceiling in ComputeHitVelocity127. 1.0 = max speed is a full hit. 1.5 = spread hits across a wider range (harder to peg).")]
    public float hitVelocityMultiplier = 1.0f;
    [Tooltip("Exponent applied to normalized impact speed before mapping to damage. >1 = damage ramps up sharply near top speed; 1 = linear.")]
    public float damageCurveExponent = 1.75f;
    [Tooltip("Damage dealt at zero effective impact speed (curvedSpeed = 0).")]
    public float minImpactDamage = 25f;
    [Tooltip("Damage dealt at full effective impact speed (curvedSpeed = 1), before mass multiplier.")]
    public float maxImpactDamage = 100f;
    [Tooltip("Minimum mass multiplier applied to base damage (light ships hit softer).")]
    public float massDamageMultiplierMin = 0.75f;
    [Tooltip("Maximum mass multiplier applied to base damage (heavy ships hit harder).")]
    public float massDamageMultiplierMax = 2f;
    [Tooltip("Seconds since the last hit within which a follow-up impact is padded up to followUpDamageFloor.")]
    public float followUpDamageWindowSeconds = 0.5f;
    [Tooltip("Minimum damage for a follow-up hit landing within followUpDamageWindowSeconds of the previous one.")]
    public float followUpDamageFloor = 10f;
    [Tooltip("Absolute damage ceiling for a single impact, after mass multiplier and follow-up floor.")]
    public float damageCeiling = 120f;

    [Header("Fuel Tradeoffs")]
    public float capacity = 10f;              // tank size (Vehicle energyLevel starts here)
    [Range(0.25f, 2f)] public float burnRate = 1.0f; // fuel units/sec at full trigger pressure
    [Tooltip("Seconds of continuously holding boost before fuel drain (both the flat burnRate cost and " +
             "cosmic-dust resistance drain) reaches full strength. 0 = disabled (default): full cost from " +
             "the first frame of boost. While ramping, the flat drain starts at 25% of burnRate and climbs " +
             "to 100%; dust-resistance drain starts as if unresisted and climbs to its normal cost.")]
    [Min(0f)] public float boostSpoolUpSeconds = 0f;

    [Header("Plow — Footprint")]
    [Tooltip("Role-capture half-width (cells). Only plow-carved cells have their Voronoi role promoted into the active imprint, " +
             "so this is the primary axis of per-pass role-capture luck. " +
             "0 = single-cell column, 1 = 3-wide, 2 = 5-wide.")]
    public int plowHalfWidthCells = 1;
    [Tooltip("Collision safety clearance radius (cells) maintained around the vehicle while boosting. " +
             "Prevents regrowth directly under the vehicle hull. Not a carve — suppressed cells keep their role.")]
    public int vehicleKeepClearRadiusCells = 0;
    [Tooltip("Forward depth of the plow (cells). Combined with plowHalfWidthCells, sets total role-capture area per boost pass. " +
             "0 = center cell only.")]
    public int plowDepthCells = 2;
    [Tooltip("Minimum speed (world units/s) before the plow chips cells. Drag is still felt below this threshold.")]
    public float plowMinSpeed = 2f;
    [Min(1)]
    [Tooltip("HP removed per plow tick (before resistance). Higher values chip through thick cells faster " +
             "and reduce the number of passes needed to earn a role-capture.")]
    public int plowChipAmount = 1;

    [Header("Regrowth Modifier")]
    [Tooltip("Per-role multipliers on the regrow delay of dust THIS ship carves. " +
             "1 = neutral, 0.75 = accelerator (an 8s cell regrows in 6s), >1 = slows regrowth.")]
    public RoleRegrowDelayMultipliers regrowDelayMultipliers = new();

    [Header("Plow — Carving Feel")]
    [Tooltip("Fraction of a cell's carve resistance this vehicle ignores. 0 = fully resisted, 1 = punches through anything below resistance=1.")]
    [Range(0f, 1f)] public float carveResistanceBypass01 = 0f;

    [Tooltip("Speed fraction drained per resistive cell per FixedUpdate while the plow is active. Drain scales with the cell's live carve resistance and persists while cells are in the Clearing state.")]
    [Range(0f, 0.5f)] public float carveVelocityDrainPerCell = 0f;

    [Header("Plow — Steering")]
    [Tooltip("How strongly player input can redirect current velocity direction. " +
             "1 = snap to input (default). 0.2 = very slidey — can barely redirect at speed. " +
             "Drifter: ~0.2. Needle: ~0.95. Plow base: ~0.85 (further reduced by plowSteeringPenalty01 while carving).")]
    [Range(0.05f, 1f)] public float directionalAuthority01 = 1f;

    [Tooltip("Plow: additional authority reduction WHILE actively carving (boosting + speed ≥ plowMinSpeed). " +
             "Effective authority while plowing = directionalAuthority01 × (1 - plowSteeringPenalty01). Plow: ~0.65. Others: 0.")]
    [Range(0f, 1f)] public float plowSteeringPenalty01 = 0f;

    [Header("Archetype — Needle")]
    [Tooltip("World-unit radius within which a DiscoveryTrackNode triggers handling instability. 0 = disabled.")]
    [Min(0f)] public float pressureInstabilityRadius = 0f;

    [Tooltip("Maximum authority reduction when a DiscoveryTrackNode is at the inner edge of pressureInstabilityRadius. " +
             "0.55 → controls become ~45% effective at full pressure.")]
    [Range(0f, 1f)] public float pressureInstabilityStrength01 = 0f;

    [Header("Archetype — Redline")]
    [Tooltip("Steering authority reduction that ramps in as current speed approaches arcadeMaxSpeed. " +
             "0 = disabled (default). 0.5 = up to 50% authority loss right at top speed.")]
    [Range(0f, 1f)] public float speedInstabilityStrength01 = 0f;

    [Header("Soul Sprite")]
    [Tooltip("Local scale for the hidden soul sprite when inactive.")]
    public float soulMinScale = 0.3f;
    [Tooltip("Local scale for the soul sprite at full placement readiness.")]
    public float soulMaxScale = 1.0f;
    [Tooltip("How quickly the soul sprite scale follows the pulse.")]
    public float soulScaleLerpSpeed = 7f;
    [Range(0f, 1f)]
    [Tooltip("Soul alpha at the first visible hint of placement availability.")]
    public float soulAlphaMin = 0.0f;
    [Range(0f, 1f)]
    [Tooltip("Soul alpha at full placement readiness.")]
    public float soulAlphaMax = 0.5f;
}
