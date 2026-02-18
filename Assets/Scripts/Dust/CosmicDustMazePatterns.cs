using System;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

/// <summary>
/// Pure(ish) maze-pattern generators.
///
/// Design goal for the first extraction: allow CosmicDustGenerator to call into a
/// dedicated pattern class without changing its existing payload type
/// (List&lt;(Vector2Int cell, Vector3 world)&gt;) and without requiring new helper
/// methods to exist on CosmicDustGenerator.
///
/// This class does NOT know about pooling, regrowth, claims, composite colliders,
/// or tint systems. Callers may supply predicates to veto cells/world positions.
/// </summary>
public enum MazeArchetype
{
    Establish = 0,   // early stable loop
    Evolve = 1,      // moderate complexity
    Intensify = 2,   // denser and brighter
    Release = 3,     // breath or breakdown
    Wildcard = 4,    // glitchy, unpredictable
    Pop = 5          // catchy hook
}

public static class CosmicDustMazePatterns
{

    // ------------------------------------------------------------------
// NEW: Pattern wiring (calls extracted CosmicDustMazePatterns)
// ------------------------------------------------------------------

    
    /// <summary>
    /// Cellular Automata fill, matching the legacy Build_CA rule-set currently used by CosmicDustGenerator.
    ///
    /// Caller supplies:
    /// - getHexDirectionsByRow(row) -> neighbor offsets (Vector2Int) for the given row parity
    /// - gridToWorld(cell) -> world position for a grid cell
    /// - isCellAvailable(x,y) -> whether the spawn grid cell may be occupied
    /// - includeWorld(world) -> optional world-space veto (screen bounds, hollow radius, star-hole, etc.)
    /// </summary>
    ///
    /// 
    public static List<(Vector2Int cell, Vector3 world)> BuildCA(
        int width,
        int height,
        float fillChance,
        int iterations,
        Func<int, List<Vector2Int>> getHexDirectionsByRow,
        Func<Vector2Int, Vector3> gridToWorld,
        Func<int, int, bool> isCellAvailable,
        Func<Vector3, bool> includeWorld = null)
    {
        var growth = new List<(Vector2Int, Vector3)>();
        if (width <= 0 || height <= 0) return growth;
        if (getHexDirectionsByRow == null) return growth;
        if (gridToWorld == null) return growth;
        if (isCellAvailable == null) return growth;

        // Mirror legacy behavior: represent only available cells in the fill map.
        Dictionary<Vector2Int, bool> fillMap = new Dictionary<Vector2Int, bool>(width * height);

        for (int x = 0; x < width; x++)
        for (int y = 0; y < height; y++)
        {
            if (!isCellAvailable(x, y)) continue;
            var pos = new Vector2Int(x, y);
            fillMap[pos] = Random.value < fillChance;
        }

        // Legacy CA rule-set:
        // - live cell dies if neighbors < 2 or > 4
        // - dead cell becomes live if neighbors == 3
        // - otherwise stays the same
        iterations = Mathf.Max(0, iterations);
        for (int i = 0; i < iterations; i++)
        {
            var next = new Dictionary<Vector2Int, bool>(fillMap.Count);
            foreach (var cell in fillMap.Keys)
            {
                int n = CountFilledNeighbors(cell, fillMap, getHexDirectionsByRow);
                bool cur = fillMap[cell];
                if (cur && (n < 2 || n > 4)) next[cell] = false;
                else if (!cur && n == 3)     next[cell] = true;
                else                         next[cell] = cur;
            }
            fillMap = next;
        }

        foreach (var kv in fillMap)
        {
            if (!kv.Value) continue;
            var grid = kv.Key;
            var world = gridToWorld(grid);
            if (includeWorld != null && !includeWorld(world)) continue;
            growth.Add((grid, world));
        }

        return growth;
    }

    /// <summary>
    /// Ring chokepoints pattern, matching the legacy Build_RingChokepoints behavior currently used by CosmicDustGenerator.
    ///
    /// Uses a BFS distance in hex steps (via caller-provided neighbor offsets) to compute a distance field,
    /// then selects cells that fall on distance "rings" (distance % spacing) with jitter.
    ///
    /// Caller supplies:
    /// - getHexDirectionsByRow(row) -> neighbor offsets (Vector2Int) for the given row parity
    /// - gridToWorld(cell) -> world position for a grid cell
    /// - isCellAvailable(x,y) -> whether the spawn grid cell may be occupied
    /// - includeCellForBfs(cell) -> optional grid-space veto applied during BFS expansion (e.g., star hole / hollow)
    /// - includeWorld(world) -> optional world-space veto applied when emitting results (e.g., screen bounds)
    /// </summary>
    public static List<(Vector2Int cell, Vector3 world)> BuildRingChokepoints(
        Vector2Int center,
        int width,
        int height,
        int ringSpacing,
        int ringThickness,
        float jitter,
        Func<int, List<Vector2Int>> getHexDirectionsByRow,
        Func<Vector2Int, Vector3> gridToWorld,
        Func<int, int, bool> isCellAvailable,
        Func<Vector2Int, bool> includeCellForBfs = null,
        Func<Vector3, bool> includeWorld = null)
    {
        var growth = new List<(Vector2Int, Vector3)>();
        if (width <= 0 || height <= 0) return growth;
        if (getHexDirectionsByRow == null) return growth;
        if (gridToWorld == null) return growth;
        if (isCellAvailable == null) return growth;

        ringSpacing = Mathf.Max(1, ringSpacing);
        ringThickness = Mathf.Max(0, ringThickness);
        jitter = Mathf.Max(0f, jitter);

        // BFS distance by hex steps (avoids axial conversion headaches)
        var dist = new Dictionary<Vector2Int, int>(width * height);
        var q = new Queue<Vector2Int>();
        var seen = new HashSet<Vector2Int>();

        if ((uint)center.x >= (uint)width || (uint)center.y >= (uint)height) return growth;
        if (!isCellAvailable(center.x, center.y)) return growth;
        if (includeCellForBfs != null && !includeCellForBfs(center)) return growth;

        q.Enqueue(center);
        dist[center] = 0;
        seen.Add(center);

        while (q.Count > 0)
        {
            var p = q.Dequeue();
            int row = p.y;
            var dirs = getHexDirectionsByRow(row);
            if (dirs == null) continue;

            for (int i = 0; i < dirs.Count; i++)
            {
                var n = p + dirs[i];
                if ((uint)n.x >= (uint)width || (uint)n.y >= (uint)height) continue;
                if (!isCellAvailable(n.x, n.y)) continue;
                if (seen.Contains(n)) continue;
                if (includeCellForBfs != null && !includeCellForBfs(n)) continue;

                seen.Add(n);
                dist[n] = dist[p] + 1;
                q.Enqueue(n);
            }
        }

        // Place dust on certain rings (distance bands)
        foreach (var kv in dist)
        {
            int d = kv.Value;
            // ring test with jitter: (d % spacing) < thickness (+/- jitter)
            float r = d % ringSpacing;
            bool onRing = r < ringThickness + Random.Range(-jitter, jitter);
            if (!onRing) continue;

            var grid = kv.Key;
            var world = gridToWorld(grid);
            if (includeWorld != null && !includeWorld(world)) continue;
            growth.Add((grid, world));
        }

        return growth;
    }



    /// <summary>
    /// Drunken strokes pattern used for Wildcard-like phases.
    /// Generates several short random walks on the hex grid, optionally dilating the result.
    /// </summary>
    public static List<(Vector2Int cell, Vector3 world)> BuildDrunkenStrokes(
        int width,
        int height,
        int strokes,
        int maxLen,
        float stepJitter,
        float dilate,
        Func<int, List<Vector2Int>> getHexDirectionsByRow,
        Func<Vector2Int, Vector3> gridToWorld,
        Func<int, int, bool> isCellAvailable,
        Func<Vector3, bool> includeWorld = null)
    {
        var list = new List<(Vector2Int, Vector3)>();
        if (width <= 0 || height <= 0) return list;
        if (getHexDirectionsByRow == null) return list;
        if (gridToWorld == null) return list;
        if (isCellAvailable == null) return list;

        strokes = Mathf.Max(0, strokes);
        maxLen = Mathf.Max(1, maxLen);
        stepJitter = Mathf.Clamp01(stepJitter);
        dilate = Mathf.Clamp01(dilate);

        var growth = new HashSet<Vector2Int>();

        for (int s = 0; s < strokes; s++)
        {
            // random start on-screen & available
            Vector2Int p0 = new Vector2Int(Random.Range(0, width), Random.Range(0, height));
            int safety = 0;
            while (safety++ < 100 &&
                   (!isCellAvailable(p0.x, p0.y) ||
                    (includeWorld != null && !includeWorld(gridToWorld(p0)))))
            {
                p0 = new Vector2Int(Random.Range(0, width), Random.Range(0, height));
            }

            if (safety >= 100) continue;

            int len = Random.Range(maxLen / 2, maxLen + 1);
            Vector2Int cur = p0;

            for (int i = 0; i < len; i++)
            {
                growth.Add(cur);

                var dirs = getHexDirectionsByRow(cur.y);
                if (dirs == null || dirs.Count == 0) break;

                var step = dirs[Random.Range(0, dirs.Count)];
                if (Random.value < stepJitter)
                    step = dirs[Random.Range(0, dirs.Count)];

                var n = cur + step;

                if ((uint)n.x >= (uint)width || (uint)n.y >= (uint)height) break;
                if (!isCellAvailable(n.x, n.y)) break;

                var wpos = gridToWorld(n);
                if (includeWorld != null && !includeWorld(wpos)) break;

                cur = n;
            }
        }

        // Optional dilation to thicken strokes
        if (dilate > 0f)
        {
            var thick = new HashSet<Vector2Int>(growth);
            foreach (var c in growth)
            {
                var dirs = getHexDirectionsByRow(c.y);
                if (dirs == null) continue;
                for (int i = 0; i < dirs.Count; i++)
                {
                    if (Random.value < dilate)
                        thick.Add(c + dirs[i]);
                }
            }
            growth = thick;
        }

        foreach (var g in growth)
        {
            if ((uint)g.x >= (uint)width || (uint)g.y >= (uint)height) continue;
            if (!isCellAvailable(g.x, g.y)) continue;
            var wpos = gridToWorld(g);
            if (includeWorld != null && !includeWorld(wpos)) continue;
            list.Add((g, wpos));
        }

        return list;
    }

    /// <summary>
    /// Periodic mask (polka dots) used for Pop-like phases.
    /// </summary>
    public static List<(Vector2Int cell, Vector3 world)> BuildPopDots(
        int width,
        int height,
        int step,
        int phaseOffset,
        Func<Vector2Int, Vector3> gridToWorld,
        Func<int, int, bool> isCellAvailable,
        Func<Vector3, bool> includeWorld = null)
    {
        var growth = new List<(Vector2Int, Vector3)>();
        if (width <= 0 || height <= 0) return growth;
        if (gridToWorld == null) return growth;
        if (isCellAvailable == null) return growth;

        step = Mathf.Max(1, step);

        for (int x = 0; x < width; x++)
        for (int y = 0; y < height; y++)
        {
            if (!isCellAvailable(x, y)) continue;
            if (((x + (y * 2) + phaseOffset) % step) != 0) continue;

            var grid = new Vector2Int(x, y);
            var world = gridToWorld(grid);
            if (includeWorld != null && !includeWorld(world)) continue;

            growth.Add((grid, world));
        }

        return growth;
    }
    private static int CountFilledNeighbors(
        Vector2Int cell,
        Dictionary<Vector2Int, bool> fillMap,
        Func<int, List<Vector2Int>> getHexDirectionsByRow)
    {
        int count = 0;
        var dirs = getHexDirectionsByRow(cell.y);
        if (dirs == null) return 0;

        for (int i = 0; i < dirs.Count; i++)
        {
            Vector2Int neighbor = cell + dirs[i];
            if (fillMap.TryGetValue(neighbor, out bool filled) && filled)
                count++;
        }
        return count;
    }
}
