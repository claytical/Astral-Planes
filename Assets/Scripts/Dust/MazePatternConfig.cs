using UnityEngine;

public enum MazePatternType
{
    FullFill,         // fill entire available grid
    ClearBoxes,       // fill grid, punch N rectangular clear zones
    CellularAutomata, // organic blobs
    RingChokepoints,  // concentric ring walls with gaps
    DrunkenStrokes,   // glitchy scribble lines
    DiagonalLanes,    // repeating diagonal open lanes
    Tunnels           // pac-man corridor grid
}

[CreateAssetMenu(menuName = "Astral Planes/Maze/Maze Pattern Config")]
public class MazePatternConfig : ScriptableObject
{
    public MazePatternType patternType = MazePatternType.FullFill;

    [Header("Dust Timing")]
    [Tooltip("Base seconds before a cleared cell regrows dust.")]
    public float regrowDelay = 8f;

    [Header("Clear Boxes")]
    [Tooltip("Number of rectangular clear zones punched into an otherwise full grid.")]
    [Min(1)] public int clearBoxCount  = 4;
    [Tooltip("Width of each clear zone in grid cells.")]
    [Min(1)] public int clearBoxWidth  = 5;
    [Tooltip("Height of each clear zone in grid cells.")]
    [Min(1)] public int clearBoxHeight = 5;

    [Header("Cellular Automata")]
    [Tooltip("Initial fill probability before CA smoothing (0 = empty, 1 = full).")]
    [Range(0f, 1f)] public float caFillChance = 0.30f;
    [Tooltip("Number of CA smoothing passes.")]
    [Range(1, 10)]  public int   caIterations  = 3;

    [Header("Ring Chokepoints")]
    [Tooltip("Grid cells between successive ring walls.")]
    [Min(1)]        public int   ringSpacing   = 6;
    [Tooltip("Thickness of each ring wall in cells.")]
    [Min(1)]        public int   ringThickness = 1;
    [Tooltip("Per-cell radial noise applied to ring placement (0 = perfect circles).")]
    [Range(0f, 1f)] public float ringJitter    = 0.35f;

    [Header("Drunken Strokes")]
    [Tooltip("Number of scribble strokes drawn.")]
    [Min(1)]        public int   strokes      = 10;
    [Tooltip("Maximum cells per stroke.")]
    [Min(1)]        public int   strokeMaxLen = 18;
    [Tooltip("Chance per step to deviate from current heading.")]
    [Range(0f, 1f)] public float strokeJitter = 0.35f;
    [Tooltip("Fraction of neighboring cells also filled per step (thickens strokes).")]
    [Range(0f, 1f)] public float strokeDilate = 0.30f;

    [Header("Diagonal Lanes")]
    [Tooltip("Cell stride between lane walls. Larger = wider open lanes.")]
    [Min(2)] public int laneStep = 12;

    [Header("Tunnels")]
    [Tooltip("Cell stride of the corridor grid (wall thickness = corridorStep - corridorWidth).")]
    [Min(1)] public int tunnelCorridorStep  = 1;
    [Tooltip("Width of each open corridor in cells.")]
    [Min(1)] public int tunnelCorridorWidth = 3;
}
