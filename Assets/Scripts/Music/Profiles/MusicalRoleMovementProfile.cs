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
}
