using System;
using System.Collections.Generic;
using UnityEngine;

public sealed class MazeTopologyService
{
    public sealed class Context
    {
        public int Width;
        public int Height;
        public Vector2Int StarCell;
        public Func<int, List<Vector2Int>> GetHexDirectionsByRow;
        public Func<int, int, bool> IsCellAvailable;
        public Func<Vector2Int, bool> IsBlocked;
        public Func<Vector2Int, Vector2Int> NormalizeCell;

        // 0=motif at/below CosmicDustGeneratorConfig.minNotesPerBinPerVoice, 1=at/above max.
        // Default 0.5 is the neutral midpoint of every MazeDensityModulation formula, so a
        // caller that never sets this gets byte-identical behavior to before density modulation
        // existed (see CosmicDustGenerator.BuildMazeGrowthFromConfig's busyness01Override).
        public float Busyness01 = 0.5f;
    }

    public HashSet<Vector2Int> BuildSolidCells(
        MazePatternConfig config,
        Context context)
    {
        return BuildSolidCells(config, context, out _);
    }

    // Same as above, but also returns the porous-border gap cell groups computed in this same
    // pass (one list per exit) so callers can place something at each opening without a second,
    // independently-randomized call to BuildPorousBorderCells producing a mismatched layout.
    public HashSet<Vector2Int> BuildSolidCells(
        MazePatternConfig config,
        Context context,
        out List<List<Vector2Int>> gapGroupsOut)
    {
        gapGroupsOut = new List<List<Vector2Int>>();
        config?.Validate();
        if (context == null) return new HashSet<Vector2Int>();

        int width = context.Width;
        int height = context.Height;
        var solid = new HashSet<Vector2Int>();
        var pattern = config != null ? config.patternType : MazePatternType.FullFill;
        List<(Vector2Int cell, Vector3 world)> growth = pattern switch
        {
            MazePatternType.ClearBoxes => CosmicDustMazePatterns.BuildClearBoxes(
                width, height,
                MazeDensityModulation.ClearBoxesCount(config.clearBoxes.clearBoxCount, context.Busyness01),
                MazeDensityModulation.ClearBoxesWidth(config.clearBoxes.clearBoxWidth, context.Busyness01),
                MazeDensityModulation.ClearBoxesHeight(config.clearBoxes.clearBoxHeight, context.Busyness01),
                _ => Vector3.zero,
                context.IsCellAvailable,
                _ => true),
            MazePatternType.CellularAutomata => CosmicDustMazePatterns.BuildCA(
                width, height,
                MazeDensityModulation.CellularAutomataFillChance(config.cellularAutomata.fillChance, context.Busyness01),
                MazeDensityModulation.CellularAutomataIterations(config.cellularAutomata.iterations, context.Busyness01),
                context.GetHexDirectionsByRow,
                _ => Vector3.zero,
                context.IsCellAvailable,
                _ => true),
            MazePatternType.RingChokepoints => CosmicDustMazePatterns.BuildRingChokepoints(
                context.StarCell,
                width, height,
                MazeDensityModulation.RingSpacing(config.ring.spacing, context.Busyness01),
                MazeDensityModulation.RingThickness(MazeDensityModulation.RingSpacing(config.ring.spacing, context.Busyness01), config.ring.thickness, context.Busyness01),
                config.ring.jitter,
                context.GetHexDirectionsByRow,
                _ => Vector3.zero,
                context.IsCellAvailable,
                gp => gp == context.StarCell || !context.IsBlocked(gp),
                _ => true,
                context.NormalizeCell),
            MazePatternType.DrunkenStrokes => CosmicDustMazePatterns.BuildDrunkenStrokes(
                width, height,
                MazeDensityModulation.DrunkenStrokesCount(config.drunkenStrokes.strokes, context.Busyness01),
                config.drunkenStrokes.maxLen,
                config.drunkenStrokes.jitter,
                MazeDensityModulation.DrunkenStrokesDilate(config.drunkenStrokes.dilate, context.Busyness01),
                context.GetHexDirectionsByRow,
                _ => Vector3.zero,
                context.IsCellAvailable,
                _ => true),
            MazePatternType.DiagonalLanes => CosmicDustMazePatterns.BuildPopDots(
                width, height,
                MazeDensityModulation.DiagonalLanesStep(config.diagonalLanes.step, context.Busyness01),
                UnityEngine.Random.Range(0, config.diagonalLanes.step),
                _ => Vector3.zero,
                context.IsCellAvailable,
                _ => true),
            MazePatternType.Tunnels => CosmicDustMazePatterns.BuildPacManTunnels(
                width, height,
                MazeDensityModulation.TunnelsCorridorStep(config.tunnels.corridorStep, MazeDensityModulation.TunnelsCorridorWidth(config.tunnels.corridorWidth, context.Busyness01)),
                MazeDensityModulation.TunnelsCorridorWidth(config.tunnels.corridorWidth, context.Busyness01),
                _ => Vector3.zero,
                context.IsCellAvailable,
                _ => true),
            _ => BuildFullFill(width, height, context.IsBlocked),
        };

        foreach (var item in growth)
        {
            if (!context.IsBlocked(item.cell))
                solid.Add(item.cell);
        }

        if (config != null && config.porousBorder.exitCount >= 0)
        {
            var perimeterCells = BuildPorousBorderPerimeter(width, height);
            foreach (var cell in perimeterCells)
                solid.Remove(cell);

            var borderCells = CosmicDustMazePatterns.BuildPorousBorderCells(
                width,
                height,
                config.porousBorder.exitCount,
                context.IsCellAvailable,
                out gapGroupsOut);
            foreach (var border in borderCells)
            {
                if (!context.IsBlocked(border))
                    solid.Add(border);
            }
        }
        return solid;
    }

    private static List<Vector2Int> BuildPorousBorderPerimeter(int width, int height)
    {
        var perimeter = new List<Vector2Int>(width + 2 * Mathf.Max(0, height - 2));
        if (width <= 0 || height <= 0) return perimeter;

        for (int x = 0; x < width; x++)
            perimeter.Add(new Vector2Int(x, height - 1));

        if (height >= 2)
            for (int y = height - 2; y >= 0; y--)
                perimeter.Add(new Vector2Int(width - 1, y));

        if (height >= 2)
            for (int y = height - 2; y >= 0; y--)
                perimeter.Add(new Vector2Int(0, y));

        return perimeter;
    }

    private static List<(Vector2Int cell, Vector3 world)> BuildFullFill(int width, int height, Func<Vector2Int, bool> isBlocked)
    {
        var growth = new List<(Vector2Int cell, Vector3 world)>(width * height);
        for (int x = 0; x < width; x++)
        for (int y = 0; y < height; y++)
        {
            var gp = new Vector2Int(x, y);
            if (!isBlocked(gp))
                growth.Add((gp, Vector3.zero));
        }

        return growth;
    }
}
