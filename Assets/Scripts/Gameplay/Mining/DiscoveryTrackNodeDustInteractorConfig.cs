using UnityEngine;

[CreateAssetMenu(fileName = "DiscoveryTrackNodeDustInteractorConfig", menuName = "Astral Planes/Discovery Track Node Dust Interactor Config")]
public class DiscoveryTrackNodeDustInteractorConfig : ScriptableObject
{
    [Header("Multipliers while in dust (node-specific)")]
    [Tooltip("Environment feedback scalar consumed by DiscoveryTrackNode locomotion while in dust.")]
    [Range(0f, 1f)] public float dustDragScalar = 0.85f;

    [Tooltip("Extra braking applied per FixedUpdate while inside dust.")]
    public float extraBrake = 0.25f;

    [Header("Exhaust Role Painting")]
    [Tooltip("Paints adjacent dust with this node's role via exhaust. " +
             "Painted cells become eligible for PhaseStar drain. " +
             "Because painting does not update the cell's hidden imprint, " +
             "carving a painted cell re-rolls its role via neighbor-plurality / least-dense.")]
    public bool exhaustPaintRole = false;

    [Tooltip("Fraction of maxEnergyUnits to assign when exhaust-painting a cell (0=empty, 1=full).")]
    [Range(0f, 1f)] public float exhaustEnergyFraction = 0.4f;

    [Tooltip("Seconds between exhaust-paint carve ticks.")]
    public float carveIntervalSeconds = 0.08f;

    public float edgeHugForce = 2f;

    [Tooltip("Force applied to push the node back out when it is grid-inside a dust cell.")]
    public float escapePushForce = 12f;

    [Header("Role Hunter")]
    [Tooltip("Max BFS cells visited when searching for the nearest untinted dust cell.")]
    public int huntBfsBudget = 600;

    [Tooltip("How strongly the hunt direction biases _carveDir in DiscoveryTrackNode. 0 = no bias, 1 = full override.")]
    [Range(0f, 1f)] public float huntDirWeight = 0.55f;

    [Tooltip("Grid-cell radius around the node's current cell that counts as 'arrived' at the target.")]
    public int arrivalRadiusCells = 1;

    [Tooltip("Seconds between retarget BFS ticks when no target is held.")]
    public float retargetInterval = 0.35f;
}
