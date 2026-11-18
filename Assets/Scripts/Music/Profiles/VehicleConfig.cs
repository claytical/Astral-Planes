using UnityEngine;

[CreateAssetMenu(fileName = "VehicleConfig", menuName = "Astral Planes/Ship/Vehicle Config (all ships)")]
public class VehicleConfig : ScriptableObject
{
    [Header("Physics Safety")]
    [Tooltip("Hard ceiling on rigidbody angular velocity (deg/s), applied every physics tick regardless of ship. Safety clamp, not a per-ship feel knob — see arcadeAngularDamping on ShipMusicalProfile for day-to-day rotational feel.")]
    public float maxAngularVelocityDegPerSec = 540f;

    [Header("Manual Note Release")]
    [Tooltip("Max notes a ship can carry at once in Manual placement. A full queue refuses further pickups (LEGIBILITY_AUDIT.md C1).")]
    public int manualReleaseQueueCapacity = 9;
    [Min(0.05f), Tooltip("Seconds before a manual release window opens that the approach ring appears around the ship and starts closing in (LEGIBILITY_AUDIT.md C3).")]
    public float releaseApproachLookaheadSeconds = 1.25f;
    [Tooltip("When releasing onto an already-occupied step, multiply committed velocity by this factor.")]
    public float occupiedStepVelocityMultiplier = 1.25f;
    [Tooltip("On occupied-step releases, play a one-shot octave accent (+12) in addition to the committed note.")]
    public bool occupiedStepOctaveAccent = true;
    [Tooltip("If true, button press ARMS the next unlit placeholder; the note auto-commits when the playhead reaches that step.")]
    public bool manualReleaseUseArmLock = true;
    [Range(0.5f, 16f)]
    [Tooltip("How far ahead (in steps) the next placeholder can be for the press to count as an ARM.")]
    public float manualReleaseArmAheadSteps = 6f;
    [Range(0f, 2f)]
    [Tooltip("Minimum arm window in real time (seconds). Overrides manualReleaseArmAheadSteps when step duration is short (e.g. high stepsPerLoop profiles). Set to 0 to disable.")]
    public float manualReleaseArmAheadMinSeconds = 0.5f;
    [Range(0.05f, 1.0f)]
    [Tooltip("How close (in steps) the playhead must be to the armed target for auto-commit.")]
    public float manualReleaseAutoCommitEpsSteps = 0.35f;
    [Range(0f, 2f)]
    [Tooltip("Grace period (in steps) after a commit window closes for retroactive acceptance.")]
    public float manualReleaseGracePeriodSteps = 0.5f;
    [Range(0f, 2f)]
    [Tooltip("After committing a note, briefly ignore collision with the motif ring's collider so depositing a note doesn't immediately break it via the vehicle's own follow-through collision (Vehicle.Damage.cs's TryBreakNextPendingTether hazard).")]
    public float postCommitRingCollisionImmunitySeconds = 0.5f;
    [Min(0f)]
    [Tooltip("Minimum impact speed (relative velocity) for a non-boosting collision with the motif ring to break a pending note. Boosting always breaks; slower bumps only bounce and play the denial shake.")]
    public float ringBreakMinImpactSpeed = 8f;
    [Range(0f, 1f)]
    [Tooltip("Sprite alpha while the vehicle is phasing through a ring stack that appeared on top of it (see Vehicle.RingPhase.cs).")]
    public float ringPhaseGhostAlpha = 0.45f;

    [Header("Release Cue (approach ring → open-window disc)")]
    [Tooltip("Approach ring radius when it first appears (Release Approach Lookahead Seconds before the window), as a multiple of the ship sprite's radius.")]
    public float releaseRingStartRadiusMul = 2.5f;
    [Tooltip("Approach ring radius at the moment the window opens, as a multiple of the ship sprite's radius (just outside the hull).")]
    public float releaseRingEndRadiusMul = 1.15f;
    [Range(0f, 1f), Tooltip("Approach ring opacity when it first appears (far from the window).")]
    public float releaseRingAlphaStart = 0.10f;
    [Range(0f, 1f), Tooltip("Approach ring opacity just before the window opens.")]
    public float releaseRingAlphaEnd = 0.75f;
    [Range(0.01f, 0.5f), Tooltip("Approach ring line thickness as a fraction of its radius.")]
    public float releaseCueRingThickness01 = 0.07f;
    [Tooltip("Filled disc radius while the release window is open, as a multiple of the ship sprite's radius. Match Release Ring End Radius Mul so the outline fills in place.")]
    public float releaseDiscRadiusMul = 1.15f;
    [Range(0f, 1f), Tooltip("Open-window disc opacity at the dim end of its pulse. The disc is centered on the ship's note anchor and tinted with the carried note's track color.")]
    public float releaseDiscAlphaMin = 0.05f;
    [Range(0f, 1f), Tooltip("Open-window disc opacity at the bright end of its pulse.")]
    public float releaseDiscAlphaMax = 0.15f;
    [Min(0.01f), Tooltip("Open-window disc pulse rate (Hz).")]
    public float releaseDiscPulseHz = 3f;
    [Tooltip("Sorting order of the ring/disc relative to the ship sprite. Positive = drawn over the hull, negative = behind it. Read when the cue is first created.")]
    public int releaseCueSortingOffset = 1;
    [Range(0f, 1f), Tooltip("Opacity of the Energy Cue — the small circle that rides the tether toward the note's placeholder step (NoteVisualizer's releaseCuePrefab).")]
    public float energyCueAlpha = 1f;

    [Header("Plow")]
    [Tooltip("Fade duration when a cell is carved. Also controls how long cells stay in Clearing state, which sustains velocity drain on the vehicle.")]
    public float plowFadeSeconds = 0.15f;

    [Header("Input")]
    [Tooltip("Seconds before auto-zero if Move() isn't called.")]
    public float inputTimeout = 0.15f;

    [Header("Recovery")]
    [Tooltip("Master switch for out-of-bounds / stuck-in-void recovery (glide or teleport the ship back to its last safe cell).")]
    public bool enableRecovery = true;
    [Tooltip("Allow some overshoot before recovery triggers.")]
    public float viewportOobMargin = 0.15f;
    [Tooltip("World-space margin outside DrumTrack.PlayArea before recovery triggers. Tighter than viewportOobMargin — catches cases like ending up inside the NoteVisualizer strip, which is still within the full camera viewport.")]
    public float playAreaOobMargin = 0.5f;
    [Tooltip("Cooldown between two recoveries, so a ship hovering on the boundary doesn't get yanked back repeatedly.")]
    public float minSecondsBetweenRecoveries = 0.75f;
    [Tooltip("How far (grid cells) around the last safe cell recovery searches for an empty cell to return the ship to.")]
    public int respawnSearchRadiusCells = 8;
    [Tooltip("Speed threshold below which vehicle is considered 'not moving'.")]
    public float stuckSpeedThreshold = 0.35f;
    [Tooltip("Seconds inside void while not moving before ejection.")]
    public float stuckSecondsInVoid = 0.60f;
    [Tooltip("Seconds a vehicle must stay below stuckSpeedThreshold before dust-pluck triggering is suppressed (independent of gravity voids).")]
    public float dustPluckIdleSuppressSeconds = 2.0f;
    [Tooltip("If true, recovery locks player movement input and glides the vehicle back to safety over recoveryReturnSeconds instead of an instant, silent teleport.")]
    public bool lockMovementDuringRecovery = true;
    [Tooltip("Seconds the animated return-to-safety glide takes.")]
    public float recoveryReturnSeconds = 0.6f;
    [Tooltip("Multiplier of arcadeMaxSpeed for the outward exit velocity applied once a void-trap eject reaches its target rim position.")]
    public float voidEjectExitSpeedMultiplier = 0.35f;

    [Header("Gravity Void Detection")]
    [Tooltip("Radius (world units) of the overlap probe that decides whether the ship is inside a gravity void. Also sets the rim margin for void ejects.")]
    public float voidProbeRadiusWorld = 0.6f;

    [Header("Gravity Void Refuel")]
    [Min(0f), Tooltip("Fuel recovered per second while a vehicle is inside a gravity void (flat units, same scale as ship capacity 50–175). 0 disables void refuel.")]
    public float voidEnergyRecoveryPerSecond = 5f;

    [Header("Vehicle Resonance")]
    [Tooltip("Tint the ship toward the carried note's track color while a valid placement window exists.")]
    public bool useVehiclePlacementResonance = true;
    [Tooltip("How quickly the vehicle sprite color moves toward the target color.")]
    public float vehiclePlacementColorLerpSpeed = 10f;
    [Range(0f, 1f)]
    [Tooltip("Minimum tint amount once a valid placement window exists.")]
    public float vehiclePlacementMinTint = 0.08f;

    [Header("Collision Safety Clearance")]
    [Tooltip("Seconds between keep-clear refreshes while boosting. Throttles the grid query to avoid per-frame overhead.")]
    public float vehicleKeepClearRefreshSeconds = 0.10f;

    [Header("Trail (Decaying Wake)")]
    [Tooltip("How long a recently-visited cell stays protected from regrowth behind the vehicle.")]
    public float trailDecaySeconds = 3f;
    [Tooltip("How many recently-visited cells stay protected at once (oldest is released as new ones are added).")]
    public int trailLength = 6;

    [Header("Spawn Pocket")]
    [Tooltip("Carves a small pocket at spawn so the vehicle is not born intersecting dust colliders.")]
    public bool carveSpawnRestPocket = true;
    [Tooltip("If true, compute pocket radius from vehicle collider bounds and drum grid cell size.")]
    public bool spawnRestPocketAutoRadius = true;
    [Tooltip("Used when Auto Radius is disabled.")]
    public int spawnRestPocketRadiusCells = 1;
    [Tooltip("Fade time (seconds) for the initial pocket carve.")]
    public float spawnRestPocketFadeSeconds = 0.05f;
    [Tooltip("Delay (seconds) before carving the pocket.")]
    public float spawnRestPocketDelaySeconds = 0.0f;

    /// <summary>
    /// Returns the effective arm-ahead window in steps, respecting the minimum
    /// real-time floor so high-resolution profiles (large stepsPerLoop) remain playable.
    /// </summary>
    public float EffectiveArmAheadSteps(double stepDurSeconds)
    {
        if (manualReleaseArmAheadMinSeconds <= 0f || stepDurSeconds <= 0.0)
            return manualReleaseArmAheadSteps;
        float fromSeconds = (float)(manualReleaseArmAheadMinSeconds / stepDurSeconds);
        return Mathf.Max(manualReleaseArmAheadSteps, fromSeconds);
    }

    [Header("Note Carry")]
    [Tooltip("Multiplier on the ship sprite's half-size where the note tether leaves the hull. <1 pulls the start inward, >1 pushes it outward.")]
    public float tetherHullRadiusScale = 0.9f;

    [Header("Drain Feedback")]
    [Range(0.1f, 1f)]
    [Tooltip("Scale the vehicle shrinks to at the bottom of the flicker/pulse when hit by an energy-draining blast (e.g. a discarded-note bomb). 1 = no shrink.")]
    public float drainFeedbackShrinkScale = 0.7f;
    [Tooltip("Duration of the shrink-and-recover flicker/pulse triggered by a drain hit.")]
    public float drainFeedbackDuration = 0.25f;

    [Header("Fuel Status Tells (ship glow)")]
    [Range(0.01f, 0.9f), Tooltip("Fuel fraction below which the ship glow AND the HUD fuel bar show the low-fuel warning.")]
    public float lowFuelThreshold01 = 0.2f;
    [Tooltip("Low-fuel warning color — ship glow and HUD fuel bar tint.")]
    public Color lowFuelGlowColor = new Color(1f, 0.35f, 0.2f, 1f);
    [Tooltip("Low-fuel pulse rate at the threshold (Hz), ship glow and fuel bar; speeds toward Low Fuel Pulse Hz Empty as fuel nears zero.")]
    public float lowFuelPulseHz = 1.2f;
    [Tooltip("Low-fuel pulse rate (Hz) as fuel reaches zero.")]
    public float lowFuelPulseHzEmpty = 3.5f;
    [Tooltip("Refuel color while inside a gravity void — steady ship glow and HUD fuel bar shimmer tint.")]
    public Color voidRefuelGlowColor = new Color(0.75f, 0.95f, 1f, 1f);
    [Tooltip("Radius of the soft status glow behind the ship, as a multiple of the ship sprite's radius. Centered on the note core anchor.")]
    public float statusGlowRadiusMul = 1.6f;
    [Range(0f, 1f)] public float statusGlowMaxAlpha = 0.45f;

    [Header("Fuel Bar (HUD) — change FX")]
    [Tooltip("Ignore fuel-ratio changes smaller than this (float noise).")]
    public float fuelBarChangeEpsilon = 0.0005f;
    [Tooltip("Fuel-ratio change that maps to full FX intensity (1).")]
    public float fuelBarIntensityPerFullDelta = 0.05f;
    [Tooltip("How fast the bar's FX intensity decays back to rest (per second).")]
    public float fuelBarIntensityDecayPerSecond = 3.5f;
    [Tooltip("Peak scale offset when fuel is gained.")]
    public float fuelBarSwellScale = 0.15f;
    [Tooltip("Peak scale offset (shrink) when fuel drains.")]
    public float fuelBarSqueezeScale = 0.1f;
    [Tooltip("Peak wobble (degrees) when fuel is gained.")]
    public float fuelBarGainShakeDegrees = 1f;
    [Tooltip("Wobble frequency (Hz) when fuel is gained.")]
    public float fuelBarGainShakeFrequency = 9f;
    [Tooltip("Wobble frequency while draining against near-zero resistance (drainWeight01 = 0).")]
    public float fuelBarLightDrainShakeFrequency = 2f;
    [Tooltip("Wobble frequency while draining against max resistance (drainWeight01 = 1, e.g. Bass dust).")]
    public float fuelBarHeavyDrainShakeFrequency = 15f;
    [Tooltip("Bar flashes toward this color on gain.")]
    public Color fuelBarGainTint = Color.white;
    [Tooltip("Bar darkens toward this color on drain.")]
    public Color fuelBarDrainTint = Color.black;
    [Range(0f, 1f), Tooltip("Max lerp toward the gain/drain tint at full intensity.")]
    public float fuelBarMaxTintLerp = 0.6f;

    [Header("Fuel Bar (HUD) — drain heaviness (plowing resistant dust)")]
    [Tooltip("Baseline shake/squeeze intensity while draining against near-zero resistance (drainWeight01 = 0).")]
    public float fuelBarMinResistanceIntensity = 0.02f;
    [Tooltip("Baseline shake/squeeze intensity while draining against max resistance (drainWeight01 = 1, e.g. Bass dust).")]
    public float fuelBarMaxResistanceIntensity = 1f;
    [Tooltip("Wobble peak degrees vs drainWeight01 (0 = lightest material, 1 = heaviest). " +
             "Default keys: Rhythm~0.1→~0.1°, Lead~0.275→~0.25°, mid Harmony/Groove~0.6→~1°, Bass~0.815→~2°.")]
    public AnimationCurve fuelBarShakeByResistance = new AnimationCurve(
        new Keyframe(0f,     0.1f),
        new Keyframe(0.275f, 0.25f),
        new Keyframe(0.6f,   1f),
        new Keyframe(0.815f, 2f),
        new Keyframe(1f,     2.3f));
    [Range(0f, 1f), Tooltip("Fraction of Max Tint Lerp reached at drainWeight01 = 0; heavier drains darken closer to the full drain tint.")]
    public float fuelBarMinHeavyTintMul = 0.5f;

    [Header("Fuel Bar (HUD) — status pulses (colors/Hz/threshold come from Fuel Status Tells)")]
    [Range(0f, 1f), Tooltip("How far the bar is tinted toward Low Fuel Glow Color at the peak of each pulse.")]
    public float fuelBarLowFuelTintMax = 0.75f;
    [Tooltip("Bar scale pulse amount while low on fuel.")]
    public float fuelBarLowFuelScalePulse = 0.06f;
    [Tooltip("Bar shimmer rate (Hz) while refuelling in a gravity void.")]
    public float fuelBarRefuelShimmerHz = 2f;
    [Range(0f, 1f), Tooltip("How far the bar is tinted toward Void Refuel Glow Color at the peak of the shimmer.")]
    public float fuelBarRefuelTintMax = 0.55f;
    [Tooltip("Bar scale pulse amount while refuelling.")]
    public float fuelBarRefuelScalePulse = 0.04f;

    [Header("Turbulence (passing through a dormant star)")]
    [Tooltip("Peak sideways buffeting acceleration at full turbulence (world units/s^2).")]
    public float turbulenceLateralAccel = 45f;
    [Tooltip("How fast the buffeting flips side to side (noise cycles per second).")]
    public float turbulenceFrequency = 7f;
    [Range(0f, 1f)]
    [Tooltip("Fraction of steering authority lost at full turbulence.")]
    public float turbulenceAuthorityPenalty01 = 0.5f;
}
