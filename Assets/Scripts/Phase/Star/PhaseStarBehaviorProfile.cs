using UnityEngine;

[CreateAssetMenu(menuName = "Astral Planes/PhaseStar Behavior Profile")]
public class PhaseStarBehaviorProfile : ScriptableObject
{

    // =====================================================================
    // Core star gameplay levers (PhaseStar.cs)
    // =====================================================================

    [Tooltip("If enabled, the PhaseStar requests a keep-clear pocket in the dust so players always have maneuvering space near it.")]
    public bool starKeepsDustClear = true;

    [Min(0)]
    [Tooltip("Radius in grid cells for the keep-clear pocket when the star is idle/armed.")]
    public int starKeepClearRadiusCells = 2;

    [Header("Self-heal / Deadlock Recovery")]
    [Min(0)]
    [Tooltip("If waiting for collectables to clear but nothing is in flight, re-arm/advance after this many loop boundaries. 0 disables.")]
    public int collectableClearTimeoutLoops = 2;

    // =====================================================================
    // Star body motion (PhaseStarMotion2D)
    // =====================================================================
    [Header("Star Motion (PhaseStarMotion2D)")]
    [Tooltip("Base drift speed of the PhaseStar body (not the nodes).")]
    public float starDriftSpeed = 0.6f;

    [Tooltip("Speed when the star is hungry (no role at threshold). Blends down to starDriftSpeed when satiated.")]
    public float starHungrySpeed = 2.5f;

    [Tooltip("Random steering jitter; higher feels erratic/trickster.")]
    public float starDriftJitter = 0.15f;

    [Range(0f, 1f)]
    [Tooltip("Tendency to arc around avoidance vectors (0 = straight avoidance, 1 = strong tangential orbit).")]
    public float orbitBias = 0f;

    [Header("Craving Navigation (PhaseStarCravingNavigator)")]
    [Tooltip("Seconds between BFS replans. Lower = more responsive direction changes; higher = cheaper. " +
             "Typical range: 0.3–1.0s.")]
    [Min(0.1f)]
    public float mazeNavReplanInterval = 0.5f;

    [Tooltip("Optional prefab used for ejection VFX/markers.")]
    public GameObject ejectionPrefab;

    [Header("Charge Readiness (PhaseStar)")]
    [Tooltip("Accumulator rotation speed multiplier when charge is ready.")]
    [Min(1f)] public float readyRotSpeedMul = 2.5f;

    [Header("Spawn Reachability Bias (StarSpawnReachabilityWeighter)")]
    [Tooltip("How strongly spawn selection favors dust-free cells reachable through the player's already-carved corridors, by hop distance from the nearest vehicle. Higher = stronger bias toward nearby/connected cells; 0 = no bias (uniform random).")]
    [Min(0f)] public float spawnReachabilityFalloff = 0.35f;

    [Range(0f, 1f)]
    [Tooltip("Flat fallback weight given to candidate cells that aren't reachable through any carved path from a vehicle (walled off by solid dust). Never 0 — keeps rare 'lucky' spawns in disconnected pockets possible.")]
    public float spawnUnreachableFloorWeight01 = 0.05f;

}
