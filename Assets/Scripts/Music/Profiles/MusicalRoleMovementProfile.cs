using UnityEngine;

[CreateAssetMenu(menuName = "Astral Planes/Musical Role Movement Profile", fileName = "NewMusicalRoleMovementProfile")]
public class MusicalRoleMovementProfile : ScriptableObject
{
    [Header("Orbital (Harmony)")]
    [Tooltip("Orbital (Harmony): weight added to perpendicular-curve directions in corridor scoring. 0 = no orbital bias.")]
    [Range(0f, 1.5f)] public float orbitalTurnBias = 0.6f;
    [Tooltip("Harmony only: arc (deg) swept around the home ring over one note duration during the pulse. Short notes dart; long chords sweep slowly.")]
    [Range(30f, 720f)] public float collectableOrbitArcDegreesPerNote = 240f;
    [Tooltip("Harmony only: fraction of role speed while orbiting between pulses (0 = rest still).")]
    [Range(0f, 1f)] public float collectableOrbitRestSpeedMul = 0.35f;

    [Header("Rhythmic (Groove)")]
    [Tooltip("Rhythmic (Groove): seconds at burst speed before snapping to next beat boundary.")]
    [Min(0.1f)] public float burstDuration = 0.4f;
    [Tooltip("Rhythmic (Groove): seconds at near-standstill after each burst.")]
    [Min(0.05f)] public float pauseDuration = 0.35f;

    [Header("Darting (Lead)")]
    [Tooltip("Lead only: straight-line reach per pulse, in home-radius units. 1 = each pulse sweeps about one radius (before swerve curl); higher = bolder excursions.")]
    [Range(0.25f, 3f)] public float collectableTravelRadiiPerNote = 1.5f;
    [Tooltip("Lead only: peak swerve angle (deg) off the base heading for the serpentine weave.")]
    [Range(0f, 90f)] public float collectableSwerveDegrees = 65f;
    [Tooltip("Lead only: S-cycles completed over one note duration. Short notes flick quickly; long notes trace lazy curves.")]
    [Min(0.25f)] public float collectableSwerveCyclesPerNote = 1.5f;

    [Header("Spawn Launch Trajectory")]
    [Tooltip("Groove only: lateral sine-wave offset amplitude for the spawn launch, as a fraction of the straight-line launch distance.")]
    [Range(0f, 0.5f)] public float launchSineAmplitude = 0.12f;
    [Tooltip("Groove only: number of full lateral sine cycles traced over the launch flight.")]
    [Range(0.5f, 4f)] public float launchSineCycles = 1.5f;
    [Tooltip("Harmony only: minimum perpendicular bow/curl offset for the firework-tail launch arc, as a fraction of the straight-line launch distance. Actual magnitude is drawn randomly per launch between min and max, with a randomized curl side, so simultaneous Harmony launches fan out uniquely.")]
    [Range(0f, 1f)] public float launchCurlAmplitudeMin = 0.15f;
    [Tooltip("Harmony only: maximum perpendicular bow/curl offset fraction (see launchCurlAmplitudeMin).")]
    [Range(0f, 1f)] public float launchCurlAmplitudeMax = 0.45f;
    [Tooltip("Lead only: corkscrew radius for the spawn launch, as a fraction of the straight-line launch distance.")]
    [Range(0f, 0.3f)] public float launchCorkscrewAmplitude = 0.06f;
    [Tooltip("Lead only: corkscrew turns completed over the launch flight.")]
    [Range(1f, 6f)] public float launchCorkscrewCycles = 3f;
}
