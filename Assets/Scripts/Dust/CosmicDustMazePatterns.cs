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
    Windows = 0,   // early stable loop
    Evolve = 1,      // moderate complexity
    Intensify = 2,   // denser and brighter
    Release = 3,     // breath or breakdown
    Wildcard = 4,    // glitchy, unpredictable
    Pop = 5,         // catchy hook
    Tunnel = 6       // pac-man corridor grid
}

public static class CosmicDustMazePatterns
{
    
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
    /// Full grid fill with N rectangular clear zones punched out.
    ///
    /// Distributes box centers evenly across the grid by subdividing it into a sector grid,
    /// then placing one box per sector with random jitter within the sector.
    /// </summary>
    public static List<(Vector2Int cell, Vector3 world)> BuildClearBoxes(
        int width,
        int height,
        int count,
        int boxW,
        int boxH,
        Func<Vector2Int, Vector3> gridToWorld,
        Func<int, int, bool> isCellAvailable,
        Func<Vector3, bool> includeWorld = null)
    {
        var cleared = new HashSet<Vector2Int>();

        int cols = Mathf.Max(1, Mathf.RoundToInt(Mathf.Sqrt(count)));
        int rows = Mathf.Max(1, Mathf.CeilToInt(count / (float)cols));

        int placed = 0;
        for (int row = 0; row < rows && placed < count; row++)
        for (int col = 0; col < cols && placed < count; col++, placed++)
        {
            float sectorW = width  / (float)cols;
            float sectorH = height / (float)rows;
            int cx = Mathf.RoundToInt(col * sectorW + Random.Range(0.15f, 0.85f) * sectorW);
            int cy = Mathf.RoundToInt(row * sectorH + Random.Range(0.15f, 0.85f) * sectorH);
            cx = Mathf.Clamp(cx, 0, width  - 1);
            cy = Mathf.Clamp(cy, 0, height - 1);

            int hw = boxW / 2;
            int hh = boxH / 2;
            for (int dx = -hw; dx <= hw; dx++)
            for (int dy = -hh; dy <= hh; dy++)
            {
                int nx = cx + dx;
                int ny = cy + dy;
                if ((uint)nx < (uint)width && (uint)ny < (uint)height)
                    cleared.Add(new Vector2Int(nx, ny));
            }
        }

        var growth = new List<(Vector2Int, Vector3)>(width * height);
        for (int x = 0; x < width; x++)
        for (int y = 0; y < height; y++)
        {
            if (!isCellAvailable(x, y)) continue;
            var gp = new Vector2Int(x, y);
            if (cleared.Contains(gp)) continue;
            var world = gridToWorld(gp);
            if (includeWorld != null && !includeWorld(world)) continue;
            growth.Add((gp, world));
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
        Func<Vector3, bool> includeWorld = null,
        Func<Vector2Int, Vector2Int> normalizeCell = null)
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
                if (normalizeCell != null) n = normalizeCell(n);
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
    /// <summary>
    /// Pac-Man / labyrinth corridor pattern built via DFS maze carving.
    ///
    /// Logical rooms sit on a (corridorStep × corridorStep) grid. A DFS spanning tree
    /// randomly connects every room through exactly one path. Each room block and carved
    /// passage is corridorWidth cells wide, so the vehicle can actually navigate through.
    ///
    /// corridorStep must be > corridorWidth (wall thickness = step - corridorWidth).
    ///   corridorStep=5, corridorWidth=2 → 2-cell-wide corridors, 3-cell-thick walls
    ///   corridorStep=4, corridorWidth=2 → 2-cell-wide corridors, 2-cell-thick walls
    ///
    /// phaseOffsetX/Y are accepted for API symmetry but unused; randomness comes from DFS.
    /// </summary>
    public static List<(Vector2Int cell, Vector3 world)> BuildPacManTunnels(
        int width,
        int height,
        int corridorStep,
        int corridorWidth,
        Func<Vector2Int, Vector3> gridToWorld,
        Func<int, int, bool> isCellAvailable,
        Func<Vector3, bool> includeWorld = null)
    {
        var growth = new List<(Vector2Int, Vector3)>();
        if (width <= 0 || height <= 0) return growth;
        if (gridToWorld == null || isCellAvailable == null) return growth;

        corridorWidth = Mathf.Max(1, corridorWidth);
        int step = Mathf.Max(corridorWidth + 1, corridorStep);
        int mw   = Mathf.Max(1, width  / step);
        int mh   = Mathf.Max(1, height / step);

        var openCells = new HashSet<Vector2Int>(mw * mh * step * 4);

        // Open a corridorWidth × corridorWidth block at each logical room centre
        for (int rx = 0; rx < mw; rx++)
        for (int ry = 0; ry < mh; ry++)
        {
            int bx = rx * step, by = ry * step;
            for (int dx = 0; dx < corridorWidth; dx++)
            for (int dy = 0; dy < corridorWidth; dy++)
                openCells.Add(new Vector2Int(bx + dx, by + dy));
        }

        // DFS spanning tree — carve passages between rooms
        var visited = new bool[mw, mh];
        var stack   = new Stack<Vector2Int>();
        var rStart  = new Vector2Int(Random.Range(0, mw), Random.Range(0, mh));
        visited[rStart.x, rStart.y] = true;
        stack.Push(rStart);

        var logDirs = new[] {
            new Vector2Int( 1,  0),
            new Vector2Int(-1,  0),
            new Vector2Int( 0,  1),
            new Vector2Int( 0, -1)
        };

        while (stack.Count > 0)
        {
            var cur = stack.Peek();

            var neighbours = new List<Vector2Int>(4);
            foreach (var d in logDirs)
            {
                var nb = cur + d;
                if (nb.x >= 0 && nb.x < mw && nb.y >= 0 && nb.y < mh && !visited[nb.x, nb.y])
                    neighbours.Add(nb);
            }

            if (neighbours.Count == 0) { stack.Pop(); continue; }

            var chosen = neighbours[Random.Range(0, neighbours.Count)];
            visited[chosen.x, chosen.y] = true;
            stack.Push(chosen);

            // Carve a corridorWidth-wide passage between the two room blocks.
            // The passage spans the gap between room edges (not including the room blocks themselves).
            int px0 = cur.x    * step, py0 = cur.y    * step;
            int px1 = chosen.x * step, py1 = chosen.y * step;

            if (px0 != px1) // horizontal passage
            {
                int xFrom = Mathf.Min(px0, px1) + corridorWidth;
                int xTo   = Mathf.Max(px0, px1);
                int yBase = Mathf.Min(py0, py1);
                for (int x = xFrom; x < xTo; x++)
                for (int w = 0; w < corridorWidth; w++)
                    openCells.Add(new Vector2Int(x, yBase + w));
            }
            else // vertical passage
            {
                int yFrom = Mathf.Min(py0, py1) + corridorWidth;
                int yTo   = Mathf.Max(py0, py1);
                int xBase = Mathf.Min(px0, px1);
                for (int y = yFrom; y < yTo; y++)
                for (int w = 0; w < corridorWidth; w++)
                    openCells.Add(new Vector2Int(xBase + w, y));
            }
        }

        // Every available cell NOT in openCells becomes dust (wall)
        for (int x = 0; x < width; x++)
        for (int y = 0; y < height; y++)
        {
            if (!isCellAvailable(x, y)) continue;
            if (openCells.Contains(new Vector2Int(x, y))) continue;

            var grid  = new Vector2Int(x, y);
            var world = gridToWorld(grid);
            if (includeWorld != null && !includeWorld(world)) continue;

            growth.Add((grid, world));
        }

        return growth;
    }

    /// <summary>
    /// Assigns a MusicalRole to every cell in <paramref name="cells"/> using a Voronoi
    /// partition driven by the motif's active roles.
    ///
    /// If activeRoles has 1 role, every cell gets that role (no Voronoi needed).
    /// If 2–4 roles, the first role gets the farthest seed (dominant territory);
    /// remaining roles get evenly-distributed closer seeds.
    ///
    /// Role → hardness tier (via MusicalRoleProfile.dustHardness01):
    ///   Lead ≈ 0.15 (softest)  →  Harmony ≈ 0.35  →  Groove ≈ 0.55  →  Bass ≈ 0.75 (hardest)
    /// Authors set the exact values in the MusicalRoleProfile assets.
    /// </summary>
    /// <param name="cells">Growth cells produced by the maze pattern builder.</param>
    /// <param name="starCell">Grid-space position of the PhaseStar spawn.</param>
    /// <param name="gridW">Grid width in cells.</param>
    /// <param name="gridH">Grid height in cells.</param>
    /// <param name="activeRoles">Roles active in the current motif. First entry is dominant.</param>
    /// <returns>Dictionary mapping each cell to its assigned MusicalRole.</returns>


    /// <summary>
    /// Builds the set of border cells that should be filled as dust walls.
    /// Covers the top row and full side columns, including bottom corners.
    /// <paramref name="exitCount"/> evenly-spaced gaps are punched out using segment-based
    /// distribution so exits are spread across the perimeter rather than clustered.
    /// </summary>
    public static HashSet<Vector2Int> BuildPorousBorderCells(
        int width,
        int height,
        int exitCount,
        Func<int, int, bool> isCellAvailable)
    {
        var result = new HashSet<Vector2Int>();
        if (width <= 0 || height <= 0 || isCellAvailable == null) return result;

        var perimeter = new List<Vector2Int>(width + 2 * Mathf.Max(0, height - 2));

        // Top row: left to right
        for (int x = 0; x < width; x++)
            perimeter.Add(new Vector2Int(x, height - 1));

        // Right column: descending, skipping top-right corner already added
        if (height >= 2)
            for (int y = height - 2; y >= 0; y--)
                perimeter.Add(new Vector2Int(width - 1, y));

        // Left column: descending, skipping top-left corner already added
        if (height >= 2)
            for (int y = height - 2; y >= 0; y--)
                perimeter.Add(new Vector2Int(0, y));

        if (perimeter.Count == 0) return result;

        exitCount = Mathf.Max(0, exitCount);
        var gapCells = new HashSet<Vector2Int>();

        if (exitCount > 0)
        {
            int n = perimeter.Count;
            for (int seg = 0; seg < exitCount; seg++)
            {
                int segStart = Mathf.RoundToInt(seg       * (n / (float)exitCount));
                int segEnd   = Mathf.RoundToInt((seg + 1) * (n / (float)exitCount));
                segEnd = Mathf.Min(segEnd, n);
                if (segEnd <= segStart) segEnd = Mathf.Min(segStart + 1, n);
                gapCells.Add(perimeter[Random.Range(segStart, segEnd)]);
            }
        }

        foreach (var cell in perimeter)
        {
            if (!isCellAvailable(cell.x, cell.y)) continue;
            if (gapCells.Contains(cell)) continue;
            result.Add(cell);
        }

        return result;
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
