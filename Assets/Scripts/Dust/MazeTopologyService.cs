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
    }

    public HashSet<Vector2Int> BuildSolidCells(
        MazePatternConfig config,
        Context context)
    {
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
                config.clearBoxes.clearBoxCount,
                config.clearBoxes.clearBoxWidth,
                config.clearBoxes.clearBoxHeight,
                _ => Vector3.zero,
                context.IsCellAvailable,
                _ => true),
            MazePatternType.CellularAutomata => CosmicDustMazePatterns.BuildCA(
                width, height,
                config.cellularAutomata.fillChance,
                config.cellularAutomata.iterations,
                context.GetHexDirectionsByRow,
                _ => Vector3.zero,
                context.IsCellAvailable,
                _ => true),
            MazePatternType.RingChokepoints => CosmicDustMazePatterns.BuildRingChokepoints(
                context.StarCell,
                width, height,
                config.ring.spacing,
                config.ring.thickness,
                config.ring.jitter,
                context.GetHexDirectionsByRow,
                _ => Vector3.zero,
                context.IsCellAvailable,
                gp => gp == context.StarCell || !context.IsBlocked(gp),
                _ => true,
                context.NormalizeCell),
            MazePatternType.DrunkenStrokes => CosmicDustMazePatterns.BuildDrunkenStrokes(
                width, height,
                config.drunkenStrokes.strokes,
                config.drunkenStrokes.maxLen,
                config.drunkenStrokes.jitter,
                config.drunkenStrokes.dilate,
                context.GetHexDirectionsByRow,
                _ => Vector3.zero,
                context.IsCellAvailable,
                _ => true),
            MazePatternType.DiagonalLanes => CosmicDustMazePatterns.BuildPopDots(
                width, height,
                config.diagonalLanes.step,
                UnityEngine.Random.Range(0, config.diagonalLanes.step),
                _ => Vector3.zero,
                context.IsCellAvailable,
                _ => true),
            MazePatternType.Tunnels => CosmicDustMazePatterns.BuildPacManTunnels(
                width, height,
                config.tunnels.corridorStep,
                config.tunnels.corridorWidth,
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
            var borderCells = CosmicDustMazePatterns.BuildPorousBorderCells(
                width,
                height,
                config.porousBorder.exitCount,
                context.IsCellAvailable);
            foreach (var border in borderCells)
            {
                if (!context.IsBlocked(border))
                    solid.Add(border);
            }
        }
        return solid;
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
