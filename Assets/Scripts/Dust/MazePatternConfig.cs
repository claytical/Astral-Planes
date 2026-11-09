using System.Collections.Generic;
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
    [Tooltip("Delay applied to cells cleared by a discarded carried note's detonation blast.")]
    public float collectableDiscardRegrowDelay = -1f;
    [Tooltip("Delay applied to cells cleared by a time bomb collectable's detonation blast.")]
    public float collectableBombBlastRegrowDelay = -1f;
    [Tooltip("Delay applied when a track-filled role sweep (SuperNode fill) releases its held cells.")]
    public float trackFilledRegrowDelay = -1f;

    /// <summary>Per-source delay, or -1 to fall back to the base regrowDelay.</summary>
    public float GetDelayFor(DustClearSource source) => source switch
    {
        DustClearSource.VehiclePlow          => vehiclePlowRegrowDelay,
        DustClearSource.StarDrain            => starDrainReleaseDelay,
        DustClearSource.Zap                  => zapRegrowDelay,
        DustClearSource.CollectableArrival   => collectableArrivalRegrowDelay,
        DustClearSource.CollectablePlow      => collectablePlowRegrowDelay,
        DustClearSource.Jail                 => jailRegrowDelay,
        DustClearSource.SuperNode            => superNodeRegrowDelay,
        DustClearSource.CollectableDiscard   => collectableDiscardRegrowDelay,
        DustClearSource.CollectableBombBlast => collectableBombBlastRegrowDelay,
        DustClearSource.TrackFilled          => trackFilledRegrowDelay,
        _                                    => -1f,
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
        collectableDiscardRegrowDelay = Mathf.Max(-1f, collectableDiscardRegrowDelay);
        collectableBombBlastRegrowDelay = Mathf.Max(-1f, collectableBombBlastRegrowDelay);
        trackFilledRegrowDelay        = Mathf.Max(-1f, trackFilledRegrowDelay);
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

    [Header("Shape (defaults = classic jittered rectangles)")]
    [Tooltip("Outline of each clearing: Rectangle, Ellipse, Diamond or Plus (cross).")]
    public ClearBoxShape shape = ClearBoxShape.Rectangle;
    [Range(0f, 1f), Tooltip("How far a box wanders inside its grid sector. 1 = classic random, 0 = dead centre (regular lattice).")]
    public float placementJitter01 = 1f;
    [Tooltip("Shift every other row of boxes half a sector sideways (brick / hex-like lattice).")]
    public bool staggerRows = false;
    [Range(0f, 1f), Tooltip("Random per-box size variation (fraction of width/height).")]
    public float sizeJitter01 = 0f;
    [Range(0f, 1f), Tooltip("Chance each box is linked to its right / upper neighbour by a corridor (rooms-and-halls).")]
    public float connectorChance01 = 0f;
    [Min(1), Tooltip("Corridor width in cells (keep >= 2-3 so the ship fits).")]
    public int connectorWidth = 2;

    public void Validate()
    {
        clearBoxCount = Mathf.Max(1, clearBoxCount);
        clearBoxWidth = Mathf.Max(1, clearBoxWidth);
        clearBoxHeight = Mathf.Max(1, clearBoxHeight);
        placementJitter01 = Mathf.Clamp01(placementJitter01);
        sizeJitter01 = Mathf.Clamp01(sizeJitter01);
        connectorChance01 = Mathf.Clamp01(connectorChance01);
        connectorWidth = Mathf.Max(1, connectorWidth);
    }
}

public enum ClearBoxShape { Rectangle = 0, Ellipse = 1, Diamond = 2, Plus = 3 }

[System.Serializable]
public sealed class CellularAutomataParams
{
    [FormerlySerializedAs("caFillChance")]
    [Range(0f, 1f)] public float fillChance = 0.30f;
    [FormerlySerializedAs("caIterations")]
    [Range(1, 10)] public int iterations = 3;

    [Range(0f, 1f), Tooltip("How strongly the motif's busyness scales fill chance and iterations. 1 = legacy. " +
        "Cave-style rules (survive/birth ranges that favour majority) are bistable: full busyness swing flips them " +
        "between an empty field and a solid one, so use ~0.25 (or 0 to ignore busyness) with those rules.")]
    public float busynessInfluence01 = 1f;

    [Header("Rule set (defaults = legacy: survive 2-4, born on 3)")]
    [Range(0, 6), Tooltip("A filled cell stays filled with this many filled hex neighbours or more...")]
    public int surviveMin = 2;
    [Range(0, 6), Tooltip("...and this many or fewer. Higher survive range = blobbier caves; low range = thin, winding walls.")]
    public int surviveMax = 4;
    [Range(0, 6), Tooltip("An empty cell becomes filled with at least this many filled neighbours...")]
    public int birthMin = 3;
    [Range(0, 6), Tooltip("...and at most this many.")]
    public int birthMax = 3;

    [Header("Shaping")]
    [Range(-1f, 1f), Tooltip("Seed density gradient. +1 = dense toward the edges (open middle), -1 = dense toward the centre. 0 = uniform.")]
    public float radialBias = 0f;
    [Range(0, 4), Tooltip("Majority-vote smoothing passes after the rule runs; rounds jagged edges into cave-like blobs.")]
    public int smoothPasses = 0;
    [Min(0), Tooltip("Remove filled clumps smaller than this many cells (0 = keep everything).")]
    public int minIslandSize = 0;
    [Tooltip("Mirror the layout left-right and/or top-bottom for a deliberate, temple-like look.")]
    public CASymmetry symmetry = CASymmetry.None;

    public void Validate()
    {
        fillChance = Mathf.Clamp01(fillChance);
        iterations = Mathf.Clamp(iterations, 1, 10);
        surviveMin = Mathf.Clamp(surviveMin, 0, 6);
        surviveMax = Mathf.Clamp(surviveMax, surviveMin, 6);
        birthMin = Mathf.Clamp(birthMin, 0, 6);
        birthMax = Mathf.Clamp(birthMax, birthMin, 6);
        busynessInfluence01 = Mathf.Clamp01(busynessInfluence01);
        radialBias = Mathf.Clamp(radialBias, -1f, 1f);
        smoothPasses = Mathf.Clamp(smoothPasses, 0, 4);
        minIslandSize = Mathf.Max(0, minIslandSize);
    }
}

public enum CASymmetry { None = 0, Horizontal = 1, Vertical = 2, Both = 3 }

[System.Serializable]
public sealed class RingParams
{
    [FormerlySerializedAs("ringSpacing")]
    [Min(1)] public int spacing = 6;
    [FormerlySerializedAs("ringThickness")]
    [Min(1)] public int thickness = 1;
    [FormerlySerializedAs("ringJitter")]
    [Range(0f, 1f)] public float jitter = 0.35f;

    [Header("Shape (defaults = classic evenly spaced closed hex rings)")]
    [Tooltip("How ring distance from the star is measured. Hex = hex-step BFS (classic), " +
        "Circle = euclidean, Box = square (Chebyshev).")]
    public RingMetric metric = RingMetric.Hex;
    [Min(0), Tooltip("Cells within this distance of the star stay open (a clear arena).")]
    public int innerClearRadius = 0;
    [Range(0.6f, 1.6f), Tooltip("Each ring's spacing = spacing * growth^ringIndex. >1 spreads rings outward, <1 packs them.")]
    public float spacingGrowth = 1f;
    [Range(-1f, 1f), Tooltip("Cells added to ring thickness per ring (clamped below that ring's spacing).")]
    public float thicknessGrowth = 0f;
    [Min(0), Tooltip("Doors carved through every ring, evenly spaced around the star. 0 = closed rings.")]
    public int gapCount = 0;
    [Min(2), Tooltip("Door width in cells (keep >= 3-4 so the ship fits).")]
    public int gapWidth = 3;
    [Tooltip("Rotation of the doors in degrees (0 = east, counter-clockwise).")]
    public float gapOffsetDeg = 0f;
    [Tooltip("Extra door rotation per ring in degrees. Non-zero turns doors into a spiral path.")]
    public float gapTwistDeg = 0f;

    public void Validate()
    {
        spacing = Mathf.Max(1, spacing);
        thickness = Mathf.Max(1, thickness);
        jitter = Mathf.Clamp01(jitter);
        innerClearRadius = Mathf.Max(0, innerClearRadius);
        spacingGrowth = Mathf.Clamp(spacingGrowth, 0.6f, 1.6f);
        thicknessGrowth = Mathf.Clamp(thicknessGrowth, -1f, 1f);
        gapCount = Mathf.Max(0, gapCount);
        gapWidth = Mathf.Max(2, gapWidth);
    }
}

public enum RingMetric { Hex = 0, Circle = 1, Box = 2 }

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

    [Header("Shape (defaults = classic uniform random walks)")]
    [Tooltip("Where strokes begin: anywhere, the grid edges (heading inward, like cracks), or the " +
        "centre (radiating outward, like a starburst).")]
    public StrokeStart startMode = StrokeStart.Random;
    [Tooltip("Strokes keep a heading instead of picking a random direction every step, giving " +
        "straight runs and smooth arcs. Off = classic drunken walk (jitter is then ignored).")]
    public bool steered = false;
    [Range(0f, 90f), Tooltip("Steered only: random heading wobble per step, in degrees. Low = ruler-straight, high = scribbly.")]
    public float headingNoiseDeg = 25f;
    [Range(-30f, 30f), Tooltip("Steered only: constant turn per step, in degrees. Non-zero bends strokes into arcs and hooks.")]
    public float curlDegPerStep = 0f;
    [Range(0f, 0.3f), Tooltip("Chance per step that a stroke sprouts a side branch (lightning / river / tree shapes).")]
    public float branchChance01 = 0f;
    [Min(0), Tooltip("Deterministic brush: grow every stroke by this many hex steps (applied before dilate).")]
    public int brushRadius = 0;
    [Range(0f, 1f), Tooltip("Shortest stroke as a fraction of maxLen. 0.5 = classic; 1 = every stroke is full length.")]
    public float minLenFraction = 0.5f;

    public void Validate()
    {
        strokes = Mathf.Max(1, strokes);
        maxLen = Mathf.Max(1, maxLen);
        jitter = Mathf.Clamp01(jitter);
        dilate = Mathf.Clamp01(dilate);
        headingNoiseDeg = Mathf.Clamp(headingNoiseDeg, 0f, 90f);
        curlDegPerStep = Mathf.Clamp(curlDegPerStep, -30f, 30f);
        branchChance01 = Mathf.Clamp(branchChance01, 0f, 0.3f);
        brushRadius = Mathf.Max(0, brushRadius);
        minLenFraction = Mathf.Clamp01(minLenFraction);
    }
}

public enum StrokeStart { Random = 0, Edge = 1, Center = 2 }

[System.Serializable]
public sealed class DiagonalLanesParams
{
    [FormerlySerializedAs("laneStep")]
    [Min(2)] public int step = 12;

    [Header("Shape (defaults = classic thin 1:2 lanes)")]
    [Tooltip("Lane direction: lines of constant slopeX*x + slopeY*y. 1/1 = 45 degrees, 3/1 = steep, " +
        "negative values mirror. Both 0 is invalid and falls back to 1/2.")]
    public int slopeX = 1;
    public int slopeY = 2;
    [Min(1), Tooltip("Lane thickness in cells (clamped below the step).")]
    public int thickness = 1;
    [Tooltip("Add a second, mirrored lane family so lanes cross into diamonds.")]
    public bool crossHatch = false;
    [Min(0), Tooltip("Every gapPeriod cells along a lane, open gapLength cells. 0 = unbroken lanes.")]
    public int gapPeriod = 0;
    [Min(0)] public int gapLength = 0;
    [Min(0f), Tooltip("Sideways wobble of the lanes, in cells. 0 = straight.")]
    public float waveAmplitude = 0f;
    [Min(2f), Tooltip("Distance along a lane for one full wobble.")]
    public float waveLength = 16f;

    public void Validate()
    {
        step = Mathf.Max(2, step);
        if (slopeX == 0 && slopeY == 0) { slopeX = 1; slopeY = 2; }
        thickness = Mathf.Max(1, thickness);
        gapPeriod = Mathf.Max(0, gapPeriod);
        gapLength = Mathf.Clamp(gapLength, 0, Mathf.Max(0, gapPeriod - 1));
        waveAmplitude = Mathf.Max(0f, waveAmplitude);
        waveLength = Mathf.Max(2f, waveLength);
    }
}

[System.Serializable]
public sealed class TunnelsParams
{
    [FormerlySerializedAs("tunnelCorridorStep")]
    [Tooltip("Room-to-room pitch in cells. Wall thickness = step - width. The generator enforces " +
        "step >= width + 1 at build time; this field is never rewritten, so it can be edited freely.")]
    [Min(1)] public int corridorStep = 1;
    [FormerlySerializedAs("tunnelCorridorWidth")]
    [Tooltip("Open corridor width in cells (1 = single-cell tunnels).")]
    [Min(1)] public int corridorWidth = 3;

    [Header("Shape (0 = classic uniform DFS maze)")]
    [Range(0f, 1f), Tooltip("DFS bias toward continuing in the same direction. Higher = long straight hallways.")]
    public float straightness01 = 0f;
    [Range(0f, 1f), Tooltip("Chance each dead-end room is opened into a neighbour, creating loops.")]
    public float braid01 = 0f;
    [Range(0f, 1f), Tooltip("Chance a room is carved larger (a chamber).")]
    public float plazaChance01 = 0f;
    [Range(0, 4), Tooltip("Extra cells added to a chamber's width/height (clamped to keep a 1-cell wall).")]
    public int plazaExtraCells = 0;

    public void Validate()
    {
        // Deliberately no corridorStep >= corridorWidth coercion: it made lowering the step
        // snap back to the width. BuildPacManTunnels floors step at width + 1 itself.
        corridorStep = Mathf.Max(1, corridorStep);
        corridorWidth = Mathf.Max(1, corridorWidth);
        straightness01 = Mathf.Clamp01(straightness01);
        braid01 = Mathf.Clamp01(braid01);
        plazaChance01 = Mathf.Clamp01(plazaChance01);
        plazaExtraCells = Mathf.Clamp(plazaExtraCells, 0, 4);
    }
}

[System.Serializable]
public sealed class PorousBorderParams
{
    [FormerlySerializedAs("borderExitCount")]
    [Min(0)] public int exitCount = 3;

    /// <summary>When non-empty, replaces the evenly-spread exitCount gaps with exactly one gap on
    /// each listed edge (Bottom allowed), reported in this order. Used by the path-choice
    /// interstitial so each exit's edge matches the maze direction it leads in.</summary>
    public List<BoundaryWrap.BoundarySide> exitSides = new();

    public void Validate()
    {
        exitCount = Mathf.Max(0, exitCount);
    }
}

[CreateAssetMenu(menuName = "Astral Planes/Dust/Maze/Maze Pattern Config", fileName = "MazePatternConfig")]
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

    [Header("Generic Fill Floor")]
    [Range(0f, 1f), Tooltip("Minimum fraction of occupied maze cells guaranteed to be reserved as generic " +
        "MusicalRole.None dust for ANY motif using this pattern, regardless of that motif's own " +
        "genericFillRatio01 or how much territory its roles' exact-count multipliers demand. Composes as " +
        "Max(motif.genericFillRatio01, this) in CosmicDustGenerator.ApplyMotifGeoConfig — a motif can " +
        "always author more gray, never less than the pattern's own floor.")]
    public float minGenericFillRatio01 = 0.10f;

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

        minGenericFillRatio01 = Mathf.Clamp01(minGenericFillRatio01);
    }

    private void OnValidate() => Validate();
}
