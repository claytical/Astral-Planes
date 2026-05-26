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
}
