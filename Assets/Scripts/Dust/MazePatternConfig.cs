using UnityEngine;
using UnityEngine.Serialization;

public enum MazePatternType
{
    FullFill,
    ClearBoxes,
    CellularAutomata,
    RingChokepoints,
    DrunkenStrokes,
    DiagonalLanes,
    Tunnels
}

[System.Serializable]
public sealed class DustTimingParams
{
    [FormerlySerializedAs("regrowDelay")]
    [Tooltip("Base seconds before a cleared cell regrows dust. Fallback for all sources.")]
    public float regrowDelay = 8f;

    [Header("Per-Source Overrides (-1 = use base)")]
    public float vehiclePlowRegrowDelay = -1f;
    public float collectableArrivalRegrowDelay = -1f;
    public float collectablePlowRegrowDelay = -1f;
    public float jailRegrowDelay = -1f;
    public float superNodeRegrowDelay = -1f;
    public float zapRegrowDelay = -1f;
    [Tooltip("Delay applied when a DiscoveryTrackNode dies and releases the star-drained cells it was holding.")]
    public float starDrainReleaseDelay = -1f;

    /// <summary>Per-source delay, or -1 to fall back to the base regrowDelay.</summary>
    public float GetDelayFor(DustClearSource source) => source switch
    {
        DustClearSource.VehiclePlow        => vehiclePlowRegrowDelay,
        DustClearSource.StarDrain          => starDrainReleaseDelay,
        DustClearSource.Zap                => zapRegrowDelay,
        DustClearSource.CollectableArrival => collectableArrivalRegrowDelay,
        DustClearSource.CollectablePlow    => collectablePlowRegrowDelay,
        DustClearSource.Jail               => jailRegrowDelay,
        DustClearSource.SuperNode          => superNodeRegrowDelay,
        _                                  => -1f,
    };

    public void Validate()
    {
        regrowDelay = Mathf.Max(0f, regrowDelay);
        vehiclePlowRegrowDelay        = Mathf.Max(-1f, vehiclePlowRegrowDelay);
        collectableArrivalRegrowDelay = Mathf.Max(-1f, collectableArrivalRegrowDelay);
        collectablePlowRegrowDelay    = Mathf.Max(-1f, collectablePlowRegrowDelay);
        jailRegrowDelay               = Mathf.Max(-1f, jailRegrowDelay);
        superNodeRegrowDelay          = Mathf.Max(-1f, superNodeRegrowDelay);
        zapRegrowDelay                = Mathf.Max(-1f, zapRegrowDelay);
        starDrainReleaseDelay         = Mathf.Max(-1f, starDrainReleaseDelay);
    }
}

[System.Serializable]
public sealed class ClearBoxesParams
{
    [FormerlySerializedAs("clearBoxCount")]
    [Min(1)] public int clearBoxCount = 4;
    [FormerlySerializedAs("clearBoxWidth")]
    [Min(1)] public int clearBoxWidth = 5;
    [FormerlySerializedAs("clearBoxHeight")]
    [Min(1)] public int clearBoxHeight = 5;

    public void Validate()
    {
        clearBoxCount = Mathf.Max(1, clearBoxCount);
        clearBoxWidth = Mathf.Max(1, clearBoxWidth);
        clearBoxHeight = Mathf.Max(1, clearBoxHeight);
    }
}

[System.Serializable]
public sealed class CellularAutomataParams
{
    [FormerlySerializedAs("caFillChance")]
    [Range(0f, 1f)] public float fillChance = 0.30f;
    [FormerlySerializedAs("caIterations")]
    [Range(1, 10)] public int iterations = 3;

    public void Validate()
    {
        fillChance = Mathf.Clamp01(fillChance);
        iterations = Mathf.Clamp(iterations, 1, 10);
    }
}

[System.Serializable]
public sealed class RingParams
{
    [FormerlySerializedAs("ringSpacing")]
    [Min(1)] public int spacing = 6;
    [FormerlySerializedAs("ringThickness")]
    [Min(1)] public int thickness = 1;
    [FormerlySerializedAs("ringJitter")]
    [Range(0f, 1f)] public float jitter = 0.35f;

    public void Validate()
    {
        spacing = Mathf.Max(1, spacing);
        thickness = Mathf.Max(1, thickness);
        jitter = Mathf.Clamp01(jitter);
    }
}

[System.Serializable]
public sealed class DrunkenStrokesParams
{
    [FormerlySerializedAs("strokes")]
    [Min(1)] public int strokes = 10;
    [FormerlySerializedAs("strokeMaxLen")]
    [Min(1)] public int maxLen = 18;
    [FormerlySerializedAs("strokeJitter")]
    [Range(0f, 1f)] public float jitter = 0.35f;
    [FormerlySerializedAs("strokeDilate")]
    [Range(0f, 1f)] public float dilate = 0.30f;

    public void Validate()
    {
        strokes = Mathf.Max(1, strokes);
        maxLen = Mathf.Max(1, maxLen);
        jitter = Mathf.Clamp01(jitter);
        dilate = Mathf.Clamp01(dilate);
    }
}

[System.Serializable]
public sealed class DiagonalLanesParams
{
    [FormerlySerializedAs("laneStep")]
    [Min(2)] public int step = 12;

    public void Validate()
    {
        step = Mathf.Max(2, step);
    }
}

[System.Serializable]
public sealed class TunnelsParams
{
    [FormerlySerializedAs("tunnelCorridorStep")]
    [Min(1)] public int corridorStep = 1;
    [FormerlySerializedAs("tunnelCorridorWidth")]
    [Min(1)] public int corridorWidth = 3;

    public void Validate()
    {
        corridorStep = Mathf.Max(1, corridorStep);
        corridorWidth = Mathf.Max(1, corridorWidth);
        if (corridorWidth > corridorStep)
            corridorStep = corridorWidth;
    }
}

[System.Serializable]
public sealed class PorousBorderParams
{
    [FormerlySerializedAs("borderExitCount")]
    [Min(0)] public int exitCount = 3;

    public void Validate()
    {
        exitCount = Mathf.Max(0, exitCount);
    }
}

[CreateAssetMenu(menuName = "Astral Planes/Maze/Maze Pattern Config")]
public class MazePatternConfig : ScriptableObject
{
    public MazePatternType patternType = MazePatternType.FullFill;

    [Header("Dust Timing")]
    public DustTimingParams dustTiming = new();

    [Header("Clear Boxes")]
    public ClearBoxesParams clearBoxes = new();

    [Header("Cellular Automata")]
    public CellularAutomataParams cellularAutomata = new();

    [Header("Ring Chokepoints")]
    public RingParams ring = new();

    [Header("Drunken Strokes")]
    public DrunkenStrokesParams drunkenStrokes = new();

    [Header("Diagonal Lanes")]
    public DiagonalLanesParams diagonalLanes = new();

    [Header("Tunnels")]
    public TunnelsParams tunnels = new();

    [Header("Porous Border")]
    public PorousBorderParams porousBorder = new();
    
    public void Validate()
    {
        dustTiming ??= new DustTimingParams();
        clearBoxes ??= new ClearBoxesParams();
        cellularAutomata ??= new CellularAutomataParams();
        ring ??= new RingParams();
        drunkenStrokes ??= new DrunkenStrokesParams();
        diagonalLanes ??= new DiagonalLanesParams();
        tunnels ??= new TunnelsParams();
        porousBorder ??= new PorousBorderParams();

        dustTiming.Validate();
        clearBoxes.Validate();
        cellularAutomata.Validate();
        ring.Validate();
        drunkenStrokes.Validate();
        diagonalLanes.Validate();
        tunnels.Validate();
        porousBorder.Validate();
    }

    private void OnValidate() => Validate();
}
