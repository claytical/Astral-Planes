using System;
using System.Collections.Generic;
using UnityEngine;

public sealed class MazeTopologyService
{
    public HashSet<Vector2Int> BuildSolidCells(
        MazePatternConfig config,
        Vector2Int starCell,
        int width,
        int height,
        Func<Vector2Int, Vector2Int> normalizeCell,
        Func<int, List<Vector2Int>> getHexDirections,
        Func<Vector2Int, bool> isBlocked)
    {
        config?.Validate();
        var solid = new HashSet<Vector2Int>();
        var pattern = config != null ? config.patternType : MazePatternType.FullFill;
        if (pattern == MazePatternType.FullFill)
        {
            for (int x = 0; x < width; x++)
            for (int y = 0; y < height; y++)
            {
                var gp = new Vector2Int(x, y);
                if (!isBlocked(gp)) solid.Add(gp);
            }
            return solid;
        }

        var growth = CosmicDustMazePatterns.BuildRingChokepoints(
            center: starCell,
            width: width,
            height: height,
            ringSpacing: config.ring.spacing,
            ringThickness: config.ring.thickness,
            jitter: config.ring.jitter,
            getHexDirectionsByRow: getHexDirections,
            gridToWorld: _ => Vector3.zero,
            isCellAvailable: (x, y) => !isBlocked(new Vector2Int(x, y)),
            includeCellForBfs: gp => !isBlocked(gp),
            includeWorld: _ => true,
            normalizeCell: normalizeCell);

        foreach (var item in growth) solid.Add(item.Item1);
        return solid;
    }
}
