using UnityEngine;

/// <summary>Movement, lifetime and look of the SuperNode (Idea prefab). One asset shared by all SuperNodes.</summary>
[CreateAssetMenu(fileName = "SuperNodeTrackNodeConfig", menuName = "Astral Planes/Discovery Nodes/SuperNode Config")]
public class SuperNodeTrackNodeConfig : ScriptableObject
{
    private const string ResourcesPath = "Config/SuperNodeTrackNodeConfig";
    private static SuperNodeTrackNodeConfig s_default;

    /// <summary>Resources/Config/SuperNodeTrackNodeConfig, else field defaults. Never null.
    /// For callers that need a SuperNode value without holding a SuperNodeTrackNode.</summary>
    public static SuperNodeTrackNodeConfig Default
    {
        get
        {
            if (s_default != null) return s_default;
            s_default = Resources.Load<SuperNodeTrackNodeConfig>(ResourcesPath);
            if (s_default == null)
            {
                Debug.LogError($"[SuperNodeTrackNodeConfig] none at Resources/{ResourcesPath}; using field defaults.");
                s_default = CreateInstance<SuperNodeTrackNodeConfig>();
                s_default.hideFlags = HideFlags.HideAndDontSave;
            }
            return s_default;
        }
    }

    [Header("Speed (difficulty-scaled)")]
    [Tooltip("Cruise speed at difficulty 0 (world units/s).")]
    public float minSpeed = 1.5f;
    [Tooltip("Cruise speed at difficulty 1. Also the speed that maps to the fastest wobble.")]
    public float maxSpeed = 4.5f;

    [Header("Lifetime (difficulty-scaled)")]
    [Min(1)] [Tooltip("Loops before the node escapes, at difficulty 0.")]
    public int maxLifetimeLoops = 5;
    [Min(1)] [Tooltip("Loops before the node escapes, at difficulty 1.")]
    public int minLifetimeLoops = 1;

    [Header("Direction")]
    [Tooltip("Shortest pause (s) before picking a new heading.")]
    public float reactionDelayMin = 0.12f;
    [Tooltip("Longest pause (s) before picking a new heading.")]
    public float reactionDelayMax = 0.35f;
    [Tooltip("Seconds the node sticks with a chosen heading before re-evaluating.")]
    public float pathCommitDuration = 0.7f;
    [Tooltip("Score bonus for headings close to the current one. Higher = smoother, less zig-zag.")]
    public float continuityBias = 0.6f;
    [Tooltip("Random angle (degrees) added to each chosen heading.")]
    public float turnJitterDeg = 14f;

    [Header("Burst-Pause")]
    [Tooltip("Seconds of each fast burst.")]
    public float burstDuration = 0.55f;
    [Tooltip("Seconds of each slow pause between bursts.")]
    public float pauseDuration = 0.30f;
    [Tooltip("Speed multiplier during a burst.")]
    public float burstSpeedMultiplier = 1.8f;
    [Tooltip("Speed multiplier during a pause.")]
    public float pauseSpeedMultiplier = 0.55f;

    [Header("Steering")]
    [Tooltip("How quickly velocity blends toward the desired heading (per second).")]
    public float turnRate = 7f;
    [Tooltip("Fraction (0-1) that damps Turn Rate. Higher = lazier turns.")]
    public float hesitation = 0.12f;

    [Header("Dust Attraction")]
    [Tooltip("Raycast distance for scoring headings by the dust ahead.")]
    public float lookaheadDistance = 1.5f;
    [Tooltip("Score bonus for headings that run alongside dust on either side.")]
    public float wallHugBonus = 1.0f;
    [Tooltip("Side probe distance for the wall-hug check.")]
    public float sideCheckDistance = 1.5f;

    [Header("Stall")]
    [Tooltip("Seconds between stuck checks.")]
    public float stallCheckInterval = 0.5f;
    [Tooltip("Moving less than this distance between checks counts as stuck, which forces a new heading and burst.")]
    public float stallMoveThreshold = 0.15f;

    [Header("Flee")]
    [Tooltip("A vehicle within this radius makes the node flee directly away.")]
    public float fleeRadius = 3.5f;
    [Tooltip("Speed multiplier while fleeing.")]
    public float fleeSpeedMultiplier = 1.5f;

    [Header("Collection")]
    [Tooltip("Seconds after spawn during which the node can't be collected.")]
    public float spawnGraceSeconds = 0.2f;
    [Tooltip("Loops the collected notes take to ascend into the track.")]
    public int ascendLoopsOverride = 3;

    [Header("Dust Carving")]
    [Tooltip("Fade duration of dust cells the node carves through.")]
    public float dustFadeSeconds = 0.15f;

    [Header("Visual")]
    [Tooltip("How fast (deg/s) the sprite turns toward its travel direction.")]
    public float faceTurnRate = 420f;
    [Tooltip("Peak wobble angle (degrees).")]
    public float wobbleDeg = 12f;
    [Tooltip("Wobble frequency (Hz) from stopped (x) to Max Speed (y).")]
    public Vector2 wobbleHzRange = new Vector2(2.5f, 4.5f);
}
