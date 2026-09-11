using UnityEngine;

[CreateAssetMenu(fileName = "WispConfig", menuName = "Astral Planes/Dust/Wisp Config")]
public class WispConfig : ScriptableObject
{
    [Header("Approach")]
    [Tooltip("World-unit radius within which the vehicle causes the wisp to commit to leading toward its assigned exit.")]
    [Min(0f)] public float approachRadius = 3f;

    [Tooltip("Seconds between proximity re-checks while idle, and between waypoint-path refreshes once committed.")]
    [Min(0.05f)] public float recheckPeriod = 0.2f;

    [Header("Movement")]
    [Tooltip("World units/sec once the wisp has committed to leading the vehicle.")]
    [Min(0f)] public float seekSpeed = 3f;

    [Tooltip("World-unit distance from a waypoint cell's center that counts as 'arrived' before advancing to the next one.")]
    [Min(0.01f)] public float waypointArriveDistance = 0.3f;

    [Tooltip("World units/sec² used to accelerate velocity toward its desired direction — lower values feel floatier/laggier than an instant snap.")]
    [Min(0f)] public float steerAcceleration = 6f;

    [Header("Drift / Whimsy")]
    [Tooltip("Perpendicular sine-wave amplitude (world units/sec) layered onto committed travel, giving it a gentle weave toward its target instead of a straight line.")]
    [Min(0f)] public float travelDriftAmplitude = 0.6f;

    [Tooltip("Frequency range (Hz) for the travel weave — picked once per commit so the wisp doesn't weave identically every time.")]
    public Vector2 travelDriftHzRange = new Vector2(0.5f, 1f);

    [Header("Wander (no valid exit path)")]
    [Tooltip("Consecutive failed path-checks (at recheckPeriod intervals) required before giving up on the assigned exit and starting to wander. Guards against a single transient pathfinding miss sending the wisp off before the vehicle ever had a chance to approach.")]
    [Min(1)] public int wanderGiveUpFailures = 5;

    [Tooltip("Wander speed, as a fraction of seekSpeed — used when no path exists to the assigned exit (e.g. this wisp survived into a new motif whose maze no longer has it), so it drifts instead of sitting frozen.")]
    [Range(0f, 1f)] public float wanderSpeedFraction = 0.3f;

    [Tooltip("How quickly the wander heading's Perlin noise advances — lower is slower/lazier meandering.")]
    [Min(0.01f)] public float wanderNoiseFrequency = 0.15f;

    [Header("Spawn")]
    [Tooltip("Minimum cells the wisp must spawn away from the vehicle's starting cell, along the route to the exit.")]
    [Min(0)] public int spawnMinCellsFromVehicle = 4;

    [Tooltip("Minimum cells the wisp must spawn away from the assigned exit's anchor cell, along the route from the vehicle.")]
    [Min(0)] public int spawnMinCellsFromExit = 4;

    [Header("Consume — Maze Pulse")]
    [Tooltip("Seconds the whole maze rainbow-pulses for when this wisp is consumed.")]
    [Min(0f)] public float mazePulseSeconds = 1.2f;

    [Tooltip("How many full rainbow hue cycles per second during the pulse.")]
    [Min(0f)] public float mazePulseHueCyclesPerSecond = 0.8f;

    [Range(0f, 1f)] public float mazePulseSaturation = 0.85f;
    [Range(0f, 1f)] public float mazePulseValue = 1f;
}
