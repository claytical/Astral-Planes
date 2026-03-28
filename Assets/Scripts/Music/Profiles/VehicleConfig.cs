using UnityEngine;

[CreateAssetMenu(fileName = "VehicleConfig", menuName = "Astral Planes/Vehicle Config")]
public class VehicleConfig : ScriptableObject
{
    [Header("Manual Note Release")]
    public int manualReleaseQueueCapacity = 9;
    [Tooltip("When releasing onto an already-occupied step, multiply committed velocity by this factor.")]
    public float occupiedStepVelocityMultiplier = 1.25f;
    [Tooltip("On occupied-step releases, play a one-shot octave accent (+12) in addition to the committed note.")]
    public bool occupiedStepOctaveAccent = true;
    [Tooltip("If true, button press ARMS the next unlit placeholder; the note auto-commits when the playhead reaches that step.")]
    public bool manualReleaseUseArmLock = true;
    [Range(0.5f, 16f)]
    [Tooltip("How far ahead (in steps) the next placeholder can be for the press to count as an ARM.")]
    public float manualReleaseArmAheadSteps = 6f;
    [Range(0.05f, 1.0f)]
    [Tooltip("How close (in steps) the playhead must be to the armed target for auto-commit.")]
    public float manualReleaseAutoCommitEpsSteps = 0.35f;
    [Range(0f, 2f)]
    [Tooltip("Grace period (in steps) after a commit window closes for retroactive acceptance.")]
    public float manualReleaseGracePeriodSteps = 0.5f;

    [Header("Dust Plow Timing")]
    public float plowTickSeconds = 0.06f;
    public float plowFadeSeconds = 0.15f;

    [Header("Input Filtering")]
    [Tooltip("Seconds before auto-zero if Move() isn't called.")]
    public float inputTimeout = 0.15f;

    [Header("Recovery / Out-of-Bounds")]
    public bool enableRecovery = true;
    [Tooltip("Allow some overshoot before recovery triggers.")]
    public float viewportOobMargin = 0.15f;
    public float minSecondsBetweenRecoveries = 0.75f;
    public int respawnSearchRadiusCells = 8;
    [Tooltip("Speed threshold below which vehicle is considered 'not moving'.")]
    public float stuckSpeedThreshold = 0.35f;
    [Tooltip("Seconds inside void while not moving before ejection.")]
    public float stuckSecondsInVoid = 0.60f;

    [Header("Gravity Void Detection")]
    [Tooltip("Optional fallback tag if you don't want a LayerMask.")]
    public string gravityVoidTag = "GravityVoid";
    public float voidProbeRadiusWorld = 0.6f;

    [Header("Dust Legibility Pocket")]
    public bool keepDustClearAroundVehicle = true;
    public float vehicleKeepClearRefreshSeconds = 0.10f;

    [Header("Vehicle Placement Resonance")]
    public bool useVehiclePlacementResonance = true;
    [Tooltip("How quickly the vehicle sprite color moves toward the target color.")]
    public float vehiclePlacementColorLerpSpeed = 10f;
    [Range(0f, 1f)]
    [Tooltip("Minimum tint amount once a valid placement window exists.")]
    public float vehiclePlacementMinTint = 0.08f;
    [Range(0f, 1f)]
    [Tooltip("Extra rhythmic breathing layered on top of the placement pulse.")]
    public float vehiclePlacementOscillation = 0.18f;
    [Tooltip("Oscillation speed multiplier.")]
    public float vehiclePlacementOscillationSpeed = 1f;

    [Header("Dust Spawn Rest Pocket")]
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

    [Header("Note Trail")]
    [Tooltip("World-space spacing between queued notes trailing behind the vehicle.")]
    public float trailSlotSpacing = 0.55f;
    [Tooltip("How far behind the vehicle the first note trails (world units).")]
    public float trailFirstSlotOffset = 0.7f;
    [Tooltip("Number of historical positions stored for trail direction sampling.")]
    public int trailHistoryCapacity = 48;
    [Tooltip("Steps ahead within which the release pulse starts building.")]
    public float trailReleasePulseSteps = 4f;
}
