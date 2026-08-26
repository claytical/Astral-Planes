using UnityEngine;

public enum MazeGeoFeature
{
    Continent,    // single large connected territory — existing farthest-point Voronoi behavior
    Island,       // one small isolated cluster; cells outside radiusCells reassigned to neighbors
    Archipelago,  // 2–4 disconnected clusters of the same role
    Ridge,        // elongated strip: 2 seeds at opposing ends of the maze's long axis
    Glade,        // territory with interior pockets softened to the lowest-hardness active role
    Rings,        // concentric band: all Ring-feature roles get seeds at evenly-spaced radii
}

[System.Serializable]
public class MazeRoleGeoConfig
{
    public MusicalRole role;
    public MazeGeoFeature feature = MazeGeoFeature.Continent;

    [Min(2), UnityEngine.Tooltip("Archipelago: number of island clusters.")]
    public int seedCount = 2;

    [Min(1f), UnityEngine.Tooltip("Island/Archipelago: max cluster radius in cells. Cells farther than this from their seed are reassigned.")]
    public float radiusCells = 6f;

    [Min(1f), UnityEngine.Tooltip("Glade: radius of each punch-out clearing in cells.")]
    public float gladeRadius = 3f;

    [Min(1), UnityEngine.Tooltip("Glade: number of punch-out clearings.")]
    public int gladeCount = 2;

    [UnityEngine.Tooltip("If true, this role's territory size is computed directly from its average note count for this motif instead of the relative density-weight formula. Roles NOT flagged this way get no territory at all in a motif where at least one role uses this — their would-be cells become generic MusicalRole.None dust instead.")]
    public bool useExactNoteCount = false;

    [Min(0f), UnityEngine.Tooltip("Exact-count mode only: cellCount = round(avgNoteCount * this).")]
    public float notesToCellsMultiplier = 1f;

    [Range(0f, 1f), UnityEngine.Tooltip("Exact-count mode only: once this role's claimed territory covers at least " +
        "centerBiasMinDensity01 of the maze, seed placement is restricted to the innermost this-fraction " +
        "of unclaimed cells (by distance to the maze's bounding-box center) instead of the default farthest-" +
        "point-from-star search. 0 (default) disables center bias.")]
    public float centerBiasFraction01 = 0f;

    [Range(0f, 1f), UnityEngine.Tooltip("Exact-count mode only: minimum claimed-territory fraction (targetCount / total " +
        "maze cells) required before centerBiasFraction01 takes effect.")]
    public float centerBiasMinDensity01 = 0.35f;
}
