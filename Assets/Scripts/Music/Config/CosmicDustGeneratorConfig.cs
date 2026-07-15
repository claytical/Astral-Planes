using UnityEngine;

[CreateAssetMenu(fileName = "CosmicDustGeneratorConfig", menuName = "Astral Planes/Cosmic Dust Generator Config")]
public class CosmicDustGeneratorConfig : ScriptableObject
{
    [Header("Grid & Collision")]
    [Tooltip("World-units clearance inside each cell. 0 = watertight.")]
    public float cellClearanceWorld = 0f;
    [Tooltip("Sprite footprint relative to one grid cell. >1.0 = slight overlap with neighbours.")]
    [Range(0.8f, 1.6f)] public float dustFootprintMul = 1.15f;

    [Header("Spawn")]
    [Tooltip("Frame budget for initial maze spawn (milliseconds). Tune for target hardware.")]
    public float maxSpawnMillisPerFrame = 1.2f;
    [Tooltip("Visual grow-in duration per cell when spawning a maze hex.")]
    public float hexGrowInSeconds = 0.45f;

    [Header("Regrowth")]
    [Tooltip("Max cells that transition from PendingRegrow to Solid per drum step.")]
    public int regrowCellsPerStep = 1;
    [Tooltip("Seconds after a cell becomes visible before its collider re-enables.")]
    public float regrowColliderEnableDelaySeconds = 0.20f;

    [Header("Regrow Veto (Vehicle Overlap)")]
    [Tooltip("Delay before retrying a vetoed regrow cell.")]
    public float regrowVetoRetryDelaySeconds = 0.5f;
    [Tooltip("Overlap box size as a fraction of cell world size.")]
    [Range(0.25f, 1.25f)] public float regrowVetoBoxMul = 0.85f;
    public int regrowVetoMaxHits = 8;

    [Header("Void Grow")]
    [Tooltip("Sprite scale-in and tint fade duration for gravity void dust.")]
    [Range(0.1f, 2f)] public float voidDustGrowInSeconds = 0.40f;

    [Header("Zap Clear")]
    [Tooltip("Visual fade duration when a star zap clears a dust cell.")]
    public float zapFadeSeconds = 1.5f;
    [Tooltip("Explicit override for zapped-cell regrow delay. -1 defers to the maze pattern's zapRegrowDelay / base regrowDelay.")]
    public float zapRegrowDelaySeconds = -1f;

    [Header("Mine Node Erosion")]
    public int mineNodeErodePerTick = 10;

    [Header("Visual")]
    public Color mazeTint = new Color(0.7f, 0.7f, 0.7f, 0.25f);

    [Header("Topology")]
    [Tooltip("When enabled, the dust grid wraps toroidally — cells at one edge connect to the opposite edge.")]
    public bool toroidal = false;

    [Header("Tint Diffusion")]
    [Tooltip("If enabled, recently modified cells gradually blend toward their neighbours.")]
    public bool enableTintDiffusion = true;
    [Tooltip("Seconds between diffusion passes. Lower = smoother but more CPU.")]
    [Range(0.02f, 0.5f)] public float tintDiffusionInterval = 0.12f;
    [Tooltip("Max cells processed per diffusion pass.")]
    [Range(16, 2048)] public int tintDiffusionMaxCellsPerTick = 256;
    [Tooltip("Neighbourhood radius for diffusion averaging (1 = 8-neighbourhood).")]
    [Range(0, 3)] public int tintDiffusionRadius = 1;
    [Tooltip("How strongly each pass nudges a cell toward the neighbourhood average.")]
    [Range(0f, 1f)] public float tintDiffusionStrength = 0.25f;
    [Tooltip("When a cell changes materially due to diffusion, enqueue its immediate neighbours.")]
    public bool tintDiffusionPropagateOnChange = true;
    [Tooltip("Minimum per-channel delta required to apply a diffusion step.")]
    [Range(0f, 0.05f)] public float tintDiffusionMinDelta = 0.0025f;
    [Tooltip("How far out to mark cells dirty when a tint-affecting event occurs.")]
    [Range(0, 3)] public int tintDirtyMarkRadius = 1;
}
