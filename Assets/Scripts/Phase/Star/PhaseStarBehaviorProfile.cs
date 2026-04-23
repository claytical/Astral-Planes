using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(menuName = "Astral Planes/PhaseStar Behavior Profile")]
public class PhaseStarBehaviorProfile : ScriptableObject
{

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
    // Charge accumulation (PhaseStar.cs)
    // =====================================================================
    [Header("Charge Accumulation (PhaseStar.cs)")]
    [Tooltip("Multiplier applied to all energy delivered to the star. Raise to make charge build faster.")]
    [Min(0f)] public float dustToStarChargeMul = 1.0f;

    [Tooltip("Energy units lost per second per role (passive decay). 0 disables decay.")]
    [Min(0f)] public float passiveChargeDecayPerSec = 1f;

    [Tooltip("Seconds the star can remain fully charged without being ejected before charge resets and the drain/charge cycle restarts. 0 disables.")]
    [Min(0f)] public float armedTimeoutSeconds = 15f;

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

    [Range(0f, 0.2f)]
    [Tooltip("Chance per second to teleport a small amount (Wildcard spice). 0 disables.")]
    public float teleportChancePerSec = 0f;

    [Header("Craving Navigation (PhaseStarCravingNavigator)")]
    [Tooltip("Seconds between BFS replans. Lower = more responsive direction changes; higher = cheaper. " +
             "Typical range: 0.3–1.0s.")]
    [Min(0.1f)]
    public float mazeNavReplanInterval = 0.5f;

    [Tooltip("Optional prefab used for ejection VFX/markers.")]
    public GameObject ejectionPrefab;


}
