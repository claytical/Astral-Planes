using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(menuName = "Astral Planes/PhaseStar Behavior Profile")]
public class PhaseStarBehaviorProfile : ScriptableObject
{
    // =====================================================================
    // Core identity (lightweight; mainly informs visuals & authoring clarity)
    // =====================================================================

    [Tooltip("Primary color associated with this profile (used for maze tinting / UI).")]
    public Color mazeColor = Color.white;

    [Tooltip("The role whose dust dominates this phase. Determines the largest Voronoi territory " +
             "and the highest hardness region in the maze. Lead = softest, Bass = hardest.")]
    public MusicalRole dominantRole = MusicalRole.Bass;

    // =====================================================================
    // Core star gameplay levers (PhaseStar.cs)
    // =====================================================================
    [Header("Core Star Gameplay (PhaseStar.cs)")]
    [Min(1)]
    [Tooltip("How many MineNodes this PhaseStar will eject before it transitions to bridge/completion.")]
    public int nodesPerStar = 3;

    [Tooltip("If enabled, the PhaseStar requests a keep-clear pocket in the dust so players always have maneuvering space near it.")]
    public bool starKeepsDustClear = true;

    [Min(0)]
    [Tooltip("Radius in grid cells for the keep-clear pocket when the star is idle/armed.")]
    public int starKeepClearRadiusCells = 2;

    [Header("Safety Bubble (gravity void only)")]
    [Tooltip("If enabled, the star can display a temporary refuge bubble during gravity void expansion.")]
    public bool enableSafetyBubble = true;
    [Tooltip("Speed when the star is hungry (no shard at threshold). Blends down to starDriftSpeed when satiated.")]
    public float starHungrySpeed = 2.5f;
    [Min(0)]
    [Tooltip("Bubble radius in grid cells for the gravity-void refuge zone.")]
    public int safetyBubbleRadiusCells = 4;
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

    [Header("Craving Navigation (PhaseStarCravingNavigator)")]
    [Tooltip("Seconds between BFS replans. Lower = more responsive direction changes; higher = cheaper. " +
             "Typical range: 0.3–1.0s.")]
    [Min(0.1f)]
    public float mazeNavReplanInterval = 0.5f;

    [Tooltip("Max grid cells visited per BFS replan. Keep ≤ 200 for frame-budget safety.")]
    [Min(10)]
    public int mazeNavBfsBudget = 150;

    [Range(0f, 1f)]
    [Tooltip("How strongly the craving waypoint overrides free drift. " +
             "0 = star ignores maze corridors; 1 = fully committed to craving path. " +
             "0.85 is a good default — leaves room for vehicle avoidance to still read clearly.")]
    public float mazeNavWaypointPull = 0.85f;

    
    [Tooltip("Optional prefab used for ejection VFX/markers.")]
    public GameObject ejectionPrefab;
    
    [Tooltip("VISUAL ONLY: time for the shard/node to fly to its target grid cell.")]
    public float nodeFlightSeconds = 0.6f;
    

}
