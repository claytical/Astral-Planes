using UnityEngine;

/// <summary>What a path-choice exit leads to — drives its particle look so the three read apart.</summary>
public enum PathExitKind
{
    Revisit,   // same-phase motif already played
    NewMotif,  // same-phase motif not yet played
    NewPhase   // the outward/inward phase entry
}

/// <summary>
/// Numbers-only look for each path-exit kind, consumed by PathExitParticleBuilder. Every kind
/// differs in mass size, rims, swirl, trails and density so they stay distinguishable at a
/// glance. Optional: GameFlowManager falls back to a built-in instance of these defaults when
/// no asset is assigned.
/// </summary>
[CreateAssetMenu(menuName = "Astral Planes/Path Exit Particle Style", fileName = "PathExitParticleStyle")]
public class PathExitParticleStyle : ScriptableObject
{
    [System.Serializable]
    public class Look
    {
        [Tooltip("Black-hole mass radius in world units.")]
        [Min(0.1f)] public float massRadius = 0.7f;
        [Tooltip("Accretion rims around the mass (1 or 2).")]
        [Range(1, 2)] public int rimCount = 1;
        [Tooltip("Rim turn speed in degrees/second.")]
        public float rimSpin = 40f;
        [Tooltip("Bubble (dust mote) size in world units.")]
        [Min(0.02f)] public float bubbleSize = 0.1f;
        [Tooltip("Bubbles emitted per second.")]
        [Min(0.5f)] public float bubbleRate = 10f;
        [Tooltip("Seconds for a bubble to fall into the mass.")]
        [Min(0.3f)] public float fallSeconds = 1.4f;
        [Tooltip("Orbital swirl (radians/second) as bubbles fall in.")]
        public float swirl = 1.2f;
        [Tooltip("Trail length in seconds behind each bubble; 0 = no trails.")]
        [Min(0f)] public float trailSeconds = 0.3f;
        [Tooltip("Adds a ring that contracts onto the mass.")]
        public bool ringPulse = false;
        [Tooltip("Overall alpha multiplier for rim and dust.")]
        [Range(0.1f, 1f)] public float brightness = 1f;
    }

    // Every kind must be clearly visible; they differ by mass size, rims, swirl, trails and
    // density — not by being faint.
    public Look revisitLook = new Look
    {
        massRadius = 0.5f, rimCount = 1, rimSpin = 25f, bubbleSize = 0.09f, bubbleRate = 6f,
        fallSeconds = 1.8f, swirl = 0.8f, trailSeconds = 0f, ringPulse = false, brightness = 0.75f
    };

    public Look newMotifLook = new Look
    {
        massRadius = 0.75f, rimCount = 1, rimSpin = 45f, bubbleSize = 0.11f, bubbleRate = 12f,
        fallSeconds = 1.4f, swirl = 1.2f, trailSeconds = 0.35f, ringPulse = false, brightness = 0.95f
    };

    public Look newPhaseLook = new Look
    {
        massRadius = 1.05f, rimCount = 2, rimSpin = 70f, bubbleSize = 0.15f, bubbleRate = 22f,
        fallSeconds = 1.1f, swirl = 1.8f, trailSeconds = 0.6f, ringPulse = true, brightness = 1f
    };

    [Header("Played-before dimming (any exit whose target motif was already played)")]
    [Range(0f, 1f)] public float playedSaturation = 0.6f;
    [Range(0f, 1f)] public float playedValue = 0.85f;
    [Range(0f, 1f)] public float playedAlpha = 0.85f;

    public Look Get(PathExitKind kind) => kind switch
    {
        PathExitKind.Revisit  => revisitLook,
        PathExitKind.NewPhase => newPhaseLook,
        _                     => newMotifLook
    };
}
