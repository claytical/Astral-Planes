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
        Func<Vector3, bool> includeWorld = null,
        int surviveMin = 2,
        int surviveMax = 4,
        int birthMin = 3,
        int birthMax = 3,
        float radialBias = 0f,
        int smoothPasses = 0,
        int minIslandSize = 0,
        CASymmetry symmetry = CASymmetry.None)
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
            float p = fillChance;
            if (radialBias != 0f)
            {
                // r01: 0 at the grid centre, 1 at the corners. +bias = denser toward the edges
                // (open middle), -bias = denser toward the centre.
                float nx = (x - (width - 1) * 0.5f) / Mathf.Max(1f, (width - 1) * 0.5f);
                float ny = (y - (height - 1) * 0.5f) / Mathf.Max(1f, (height - 1) * 0.5f);
                float r01 = Mathf.Clamp01(Mathf.Sqrt(nx * nx + ny * ny) / 1.4142f);
                p = Mathf.Clamp01(fillChance * (1f + radialBias * (r01 * 2f - 1f)));
            }
            fillMap[pos] = Random.value < p;
        }

        // Life-like rule on the hex grid. Default (legacy) rule:
        // - live cell survives with 2..4 neighbors, otherwise dies
        // - dead cell is born with exactly 3 neighbors
        // - otherwise stays the same
        iterations = Mathf.Max(0, iterations);
        for (int i = 0; i < iterations; i++)
        {
            var next = new Dictionary<Vector2Int, bool>(fillMap.Count);
            foreach (var cell in fillMap.Keys)
            {
                int n = CountFilledNeighbors(cell, fillMap, getHexDirectionsByRow);
                bool cur = fillMap[cell];
                if (cur && (n < surviveMin || n > surviveMax)) next[cell] = false;
                else if (!cur && n >= birthMin && n <= birthMax) next[cell] = true;
                else                                            next[cell] = cur;
            }
            fillMap = next;
        }

        // Majority smoothing: rounds off jagged edges into cave-like blobs.
        for (int i = 0; i < smoothPasses; i++)
        {
            var next = new Dictionary<Vector2Int, bool>(fillMap.Count);
            foreach (var cell in fillMap.Keys)
            {
                var dirs = getHexDirectionsByRow(cell.y);
                int total = dirs != null ? dirs.Count : 0;
                int n = CountFilledNeighbors(cell, fillMap, getHexDirectionsByRow);
                bool cur = fillMap[cell];
                next[cell] = (n * 2 > total) ? true : (n * 2 < total) ? false : cur;
            }
            fillMap = next;
        }

        // Drop filled specks smaller than minIslandSize (flood fill over hex neighbours).
        if (minIslandSize > 1)
        {
            var visited = new HashSet<Vector2Int>();
            var comp = new List<Vector2Int>();
            var stack = new Stack<Vector2Int>();
            foreach (var start in new List<Vector2Int>(fillMap.Keys))
            {
                if (!fillMap[start] || visited.Contains(start)) continue;
                comp.Clear();
                stack.Push(start);
                visited.Add(start);
                while (stack.Count > 0)
                {
                    var c = stack.Pop();
                    comp.Add(c);
                    var dirs = getHexDirectionsByRow(c.y);
                    if (dirs == null) continue;
                    for (int d = 0; d < dirs.Count; d++)
                    {
                        var nb = c + dirs[d];
                        if (visited.Contains(nb)) continue;
                        if (!fillMap.TryGetValue(nb, out bool filled) || !filled) continue;
                        visited.Add(nb);
                        stack.Push(nb);
                    }
                }
                if (comp.Count < minIslandSize)
                    foreach (var c in comp) fillMap[c] = false;
            }
        }

        // Mirror one half onto the other so the layout is symmetric.
        if (symmetry != CASymmetry.None)
        {
            bool mx = symmetry == CASymmetry.Horizontal || symmetry == CASymmetry.Both;
            bool my = symmetry == CASymmetry.Vertical   || symmetry == CASymmetry.Both;
            var mirrored = new Dictionary<Vector2Int, bool>(fillMap.Count);
            foreach (var kv in fillMap)
            {
                var c = kv.Key;
                var src = new Vector2Int(mx && c.x >= width / 2 ? width - 1 - c.x : c.x,
                                         my && c.y >= height / 2 ? height - 1 - c.y : c.y);
                mirrored[c] = fillMap.TryGetValue(src, out bool v) ? v : kv.Value;
            }
            fillMap = mirrored;
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
    /// Full grid fill with N clear zones punched out.
    ///
    /// Distributes box centers evenly across the grid by subdividing it into a sector grid,
    /// then placing one box per sector with random jitter within the sector. With the shape
    /// parameters at their defaults these are plain jittered rectangles; otherwise boxes can be
    /// regular/staggered, round/diamond/plus shaped, size-varied, and linked by corridors.
    /// </summary>
    public static List<(Vector2Int cell, Vector3 world)> BuildClearBoxes(
        int width,
        int height,
        int count,
        int boxW,
        int boxH,
        Func<Vector2Int, Vector3> gridToWorld,
        Func<int, int, bool> isCellAvailable,
        Func<Vector3, bool> includeWorld = null,
        ClearBoxShape shape = ClearBoxShape.Rectangle,
        float placementJitter01 = 1f,
        bool staggerRows = false,
        float sizeJitter01 = 0f,
        float connectorChance01 = 0f,
        int connectorWidth = 2)
    {
        var cleared = new HashSet<Vector2Int>();

        placementJitter01 = Mathf.Clamp01(placementJitter01);
        sizeJitter01 = Mathf.Clamp01(sizeJitter01);
        connectorChance01 = Mathf.Clamp01(connectorChance01);
        connectorWidth = Mathf.Max(1, connectorWidth);

        int cols = Mathf.Max(1, Mathf.RoundToInt(Mathf.Sqrt(count)));
        int rows = Mathf.Max(1, Mathf.CeilToInt(count / (float)cols));
        var centers = new Vector2Int?[cols, rows];

        int placed = 0;
        for (int row = 0; row < rows && placed < count; row++)
        for (int col = 0; col < cols && placed < count; col++, placed++)
        {
            float sectorW = width  / (float)cols;
            float sectorH = height / (float)rows;
            float fx = Mathf.Lerp(0.5f, Random.Range(0.15f, 0.85f), placementJitter01);
            float fy = Mathf.Lerp(0.5f, Random.Range(0.15f, 0.85f), placementJitter01);
            float px = col * sectorW + fx * sectorW;
            if (staggerRows && (row & 1) == 1) px += sectorW * 0.5f;
            int cx = Mathf.RoundToInt(px);
            int cy = Mathf.RoundToInt(row * sectorH + fy * sectorH);
            cx = Mathf.Clamp(cx, 0, width  - 1);
            cy = Mathf.Clamp(cy, 0, height - 1);
            centers[col, row] = new Vector2Int(cx, cy);

            int bw = boxW, bh = boxH;
            if (sizeJitter01 > 0f)
            {
                bw = Mathf.Max(1, Mathf.RoundToInt(boxW * (1f + Random.Range(-sizeJitter01, sizeJitter01))));
                bh = Mathf.Max(1, Mathf.RoundToInt(boxH * (1f + Random.Range(-sizeJitter01, sizeJitter01))));
            }

            int hw = bw / 2;
            int hh = bh / 2;
            for (int dx = -hw; dx <= hw; dx++)
            for (int dy = -hh; dy <= hh; dy++)
            {
                if (shape != ClearBoxShape.Rectangle)
                {
                    float u = Mathf.Abs(dx) / (hw + 0.5f), v = Mathf.Abs(dy) / (hh + 0.5f);
                    bool inside = shape switch
                    {
                        ClearBoxShape.Ellipse => u * u + v * v <= 1f,
                        ClearBoxShape.Diamond => u + v <= 1f,
                        ClearBoxShape.Plus    => u <= 0.34f || v <= 0.34f,
                        _ => true,
                    };
                    if (!inside) continue;
                }
                int nx = cx + dx;
                int ny = cy + dy;
                if ((uint)nx < (uint)width && (uint)ny < (uint)height)
                    cleared.Add(new Vector2Int(nx, ny));
            }
        }

        // Corridors linking neighbouring boxes (L-shaped: horizontal along A's row, then vertical).
        if (connectorChance01 > 0f)
        {
            int lo = -((connectorWidth - 1) / 2), hi = connectorWidth / 2;
            void Carve(int x, int y)
            {
                for (int a = lo; a <= hi; a++)
                for (int c = lo; c <= hi; c++)
                {
                    int nx = x + a, ny = y + c;
                    if ((uint)nx < (uint)width && (uint)ny < (uint)height)
                        cleared.Add(new Vector2Int(nx, ny));
                }
            }
            void Link(Vector2Int A, Vector2Int B)
            {
                int stepX = B.x >= A.x ? 1 : -1;
                for (int x = A.x; x != B.x; x += stepX) Carve(x, A.y);
                int stepY = B.y >= A.y ? 1 : -1;
                for (int y = A.y; y != B.y; y += stepY) Carve(B.x, y);
                Carve(B.x, B.y);
            }

            for (int row = 0; row < rows; row++)
            for (int col = 0; col < cols; col++)
            {
                if (!centers[col, row].HasValue) continue;
                if (col + 1 < cols && centers[col + 1, row].HasValue && Random.value < connectorChance01)
                    Link(centers[col, row].Value, centers[col + 1, row].Value);
                if (row + 1 < rows && centers[col, row + 1].HasValue && Random.value < connectorChance01)
                    Link(centers[col, row].Value, centers[col, row + 1].Value);
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
        Func<Vector2Int, Vector2Int> normalizeCell = null,
        RingMetric metric = RingMetric.Hex,
        int innerClearRadius = 0,
        float spacingGrowth = 1f,
        float thicknessGrowth = 0f,
        int gapCount = 0,
        int gapWidth = 3,
        float gapOffsetDeg = 0f,
        float gapTwistDeg = 0f)
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

        // Non-hex metrics measure straight from the star instead of BFS-ing through the grid.
        // Same vetoes (cell availability + includeCellForBfs) apply.
        if (metric != RingMetric.Hex)
        {
            q.Clear();
            for (int x = 0; x < width; x++)
            for (int y = 0; y < height; y++)
            {
                if (!isCellAvailable(x, y)) continue;
                var c = new Vector2Int(x, y);
                if (c != center && includeCellForBfs != null && !includeCellForBfs(c)) continue;
                int adx = Mathf.Abs(x - center.x), ady = Mathf.Abs(y - center.y);
                dist[c] = metric == RingMetric.Box
                    ? Mathf.Max(adx, ady)
                    : Mathf.RoundToInt(Mathf.Sqrt(adx * adx + ady * ady));
            }
        }

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

        // Per-distance ring lookup (ring index + offset within the ring + that ring's thickness),
        // so rings can vary in spacing/thickness. With growth == 1 and thicknessGrowth == 0 this
        // reduces exactly to the classic (d % spacing) < thickness test.
        innerClearRadius = Mathf.Max(0, innerClearRadius);
        int maxD = 0;
        foreach (var kv in dist) if (kv.Value > maxD) maxD = kv.Value;
        int bandCount = Mathf.Max(0, maxD - innerClearRadius) + 1;
        var ringIndexAt = new int[bandCount];
        var ringOffsetAt = new float[bandCount];
        var ringThickAt = new float[bandCount];
        bool shaped = !Mathf.Approximately(spacingGrowth, 1f) || !Mathf.Approximately(thicknessGrowth, 0f);
        {
            int k = 0, start = 0;
            int sp = ringSpacing;
            for (int dd = 0; dd < bandCount; dd++)
            {
                while (dd >= start + sp)
                {
                    start += sp;
                    k++;
                    sp = shaped && !Mathf.Approximately(spacingGrowth, 1f)
                        ? Mathf.Max(2, Mathf.RoundToInt(ringSpacing * Mathf.Pow(spacingGrowth, k)))
                        : ringSpacing;
                }
                float th = ringThickness + thicknessGrowth * k;
                if (shaped) th = Mathf.Clamp(th, 0f, sp - 1);
                ringIndexAt[dd] = k;
                ringOffsetAt[dd] = dd - start;
                ringThickAt[dd] = th;
            }
        }

        // Place dust on certain rings (distance bands)
        foreach (var kv in dist)
        {
            int d = kv.Value - innerClearRadius;
            if (d < 0) continue; // inner arena stays open

            float r = ringOffsetAt[d];
            // ring test with jitter: offset-in-ring < thickness (+/- jitter)
            bool onRing = r < ringThickAt[d] + Random.Range(-jitter, jitter);
            if (!onRing) continue;

            // Doors through the ring
            if (gapCount > 0 && IsInRingGap(kv.Key - center, ringIndexAt[d], gapCount, gapWidth, gapOffsetDeg, gapTwistDeg))
                continue;

            var grid = kv.Key;
            var world = gridToWorld(grid);
            if (includeWorld != null && !includeWorld(world)) continue;
            growth.Add((grid, world));
        }

        return growth;
    }



    // True when the cell (offset from the star) lies within gapWidth/2 cells of any of gapCount
    // rays from the star. Rays are evenly spaced and rotated by offset + ringIndex * twist, so a
    // non-zero twist turns the doors into a spiral. Constant width in cells, not degrees.
    private static bool IsInRingGap(Vector2Int delta, int ringIndex, int gapCount, int gapWidth,
                                    float offsetDeg, float twistDeg)
    {
        float half = gapWidth * 0.5f;
        for (int i = 0; i < gapCount; i++)
        {
            float phi = (offsetDeg + ringIndex * twistDeg + i * (360f / gapCount)) * Mathf.Deg2Rad;
            float cos = Mathf.Cos(phi), sin = Mathf.Sin(phi);
            float along = delta.x * cos + delta.y * sin;
            float lateral = Mathf.Abs(delta.x * sin - delta.y * cos);
            if (along > 0f && lateral < half) return true;
        }
        return false;
    }

    /// <summary>
    /// Drunken strokes pattern used for Wildcard-like phases.
    /// Generates several random walks on the hex grid, optionally dilating the result.
    /// With the shape parameters at their defaults this is the classic uniform random walk;
    /// otherwise strokes can be steered (straight runs / arcs), branch, start from the edges or
    /// the centre, and be thickened by a deterministic brush radius.
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
        Func<Vector3, bool> includeWorld = null,
        StrokeStart startMode = StrokeStart.Random,
        bool steered = false,
        float headingNoiseDeg = 25f,
        float curlDegPerStep = 0f,
        float branchChance01 = 0f,
        int brushRadius = 0,
        float minLenFraction = 0.5f)
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
        branchChance01 = Mathf.Clamp01(branchChance01);
        brushRadius = Mathf.Max(0, brushRadius);
        minLenFraction = Mathf.Clamp01(minLenFraction);

        var growth = new HashSet<Vector2Int>();
        var walkers = new Queue<(Vector2Int pos, float heading, int len)>();
        const int MaxTotalBranches = 40;

        for (int s = 0; s < strokes; s++)
        {
            // pick a start (random / edge / centre) that is on-screen & available
            Vector2Int p0 = default;
            float heading0 = Random.Range(0f, 360f);
            int safety = 0;
            bool ok = false;
            while (safety++ < 100 && !ok)
            {
                switch (startMode)
                {
                    case StrokeStart.Edge:
                    {
                        int side = Random.Range(0, 4);
                        p0 = side switch
                        {
                            0 => new Vector2Int(0, Random.Range(0, height)),
                            1 => new Vector2Int(width - 1, Random.Range(0, height)),
                            2 => new Vector2Int(Random.Range(0, width), 0),
                            _ => new Vector2Int(Random.Range(0, width), height - 1),
                        };
                        // head roughly toward the middle of the grid
                        heading0 = Mathf.Atan2(height * 0.5f - p0.y, width * 0.5f - p0.x) * Mathf.Rad2Deg
                                 + Random.Range(-40f, 40f);
                        break;
                    }
                    case StrokeStart.Center:
                        p0 = new Vector2Int(
                            Mathf.Clamp(width / 2 + Random.Range(-2, 3), 0, width - 1),
                            Mathf.Clamp(height / 2 + Random.Range(-2, 3), 0, height - 1));
                        break;
                    default:
                        p0 = new Vector2Int(Random.Range(0, width), Random.Range(0, height));
                        break;
                }
                ok = isCellAvailable(p0.x, p0.y) &&
                     (includeWorld == null || includeWorld(gridToWorld(p0)));
            }
            if (!ok) continue;

            int minLen = Mathf.FloorToInt(maxLen * minLenFraction);
            walkers.Enqueue((p0, heading0, Random.Range(minLen, maxLen + 1)));

            int branchesLeft = MaxTotalBranches;
            while (walkers.Count > 0)
            {
                var w = walkers.Dequeue();
                Vector2Int cur = w.pos;
                float heading = w.heading;

                for (int i = 0; i < w.len; i++)
                {
                    growth.Add(cur);

                    var dirs = getHexDirectionsByRow(cur.y);
                    if (dirs == null || dirs.Count == 0) break;

                    Vector2Int step;
                    if (steered)
                    {
                        heading += curlDegPerStep + Random.Range(-headingNoiseDeg, headingNoiseDeg);
                        float hx = Mathf.Cos(heading * Mathf.Deg2Rad), hy = Mathf.Sin(heading * Mathf.Deg2Rad);
                        step = dirs[0];
                        float best = float.MinValue;
                        for (int di = 0; di < dirs.Count; di++)
                        {
                            float dot = dirs[di].x * hx + dirs[di].y * hy;
                            if (dot > best) { best = dot; step = dirs[di]; }
                        }
                    }
                    else
                    {
                        step = dirs[Random.Range(0, dirs.Count)];
                        if (Random.value < stepJitter)
                            step = dirs[Random.Range(0, dirs.Count)];
                    }

                    if (branchChance01 > 0f && branchesLeft > 0 && i < w.len - 2 && Random.value < branchChance01)
                    {
                        branchesLeft--;
                        float turn = Random.Range(35f, 75f) * (Random.value < 0.5f ? -1f : 1f);
                        walkers.Enqueue((cur, heading + turn, Mathf.Max(2, Mathf.RoundToInt((w.len - i) * 0.6f))));
                    }

                    var n = cur + step;

                    if ((uint)n.x >= (uint)width || (uint)n.y >= (uint)height) break;
                    if (!isCellAvailable(n.x, n.y)) break;

                    var wpos = gridToWorld(n);
                    if (includeWorld != null && !includeWorld(wpos)) break;

                    cur = n;
                }
            }
        }

        // Deterministic brush: grow every stroke cell by brushRadius hex steps.
        for (int r = 0; r < brushRadius; r++)
        {
            var grown = new HashSet<Vector2Int>(growth);
            foreach (var c in growth)
            {
                var dirs = getHexDirectionsByRow(c.y);
                if (dirs == null) continue;
                for (int i = 0; i < dirs.Count; i++) grown.Add(c + dirs[i]);
            }
            growth = grown;
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
        Func<Vector3, bool> includeWorld = null,
        int slopeX = 1,
        int slopeY = 2,
        int thickness = 1,
        bool crossHatch = false,
        int gapPeriod = 0,
        int gapLength = 0,
        float waveAmplitude = 0f,
        float waveLength = 16f)
    {
        var growth = new List<(Vector2Int, Vector3)>();
        if (width <= 0 || height <= 0) return growth;
        if (gridToWorld == null) return growth;
        if (isCellAvailable == null) return growth;

        step = Mathf.Max(1, step);
        if (slopeX == 0 && slopeY == 0) { slopeX = 1; slopeY = 2; }
        thickness = Mathf.Clamp(thickness, 1, Mathf.Max(1, step - 1)); // always leave open space

        for (int x = 0; x < width; x++)
        for (int y = 0; y < height; y++)
        {
            if (!isCellAvailable(x, y)) continue;
            bool lane = IsLaneCell(x, y, slopeX, slopeY, step, phaseOffset, thickness,
                                   gapPeriod, gapLength, waveAmplitude, waveLength)
                     || (crossHatch && IsLaneCell(x, y, slopeX, -slopeY, step, phaseOffset, thickness,
                                   gapPeriod, gapLength, waveAmplitude, waveLength));
            if (!lane) continue;

            var grid = new Vector2Int(x, y);
            var world = gridToWorld(grid);
            if (includeWorld != null && !includeWorld(world)) continue;

            growth.Add((grid, world));
        }

        return growth;
    }

    private static int PosMod(int v, int m) => ((v % m) + m) % m;

    // One lane family: lines of constant a*x + b*y (+ phase, + optional sideways wobble),
    // `thickness` cells wide every `step`, optionally broken every gapPeriod along the lane.
    private static bool IsLaneCell(int x, int y, int a, int b, int step, int phase, int thickness,
                                   int gapPeriod, int gapLength, float waveAmplitude, float waveLength)
    {
        int along = b * x - a * y; // coordinate along the lane direction
        if (gapPeriod > 0 && gapLength > 0 && PosMod(along, gapPeriod) < gapLength) return false;

        int v = a * x + b * y + phase;
        if (waveAmplitude > 0f)
            v += Mathf.RoundToInt(waveAmplitude * Mathf.Sin(2f * Mathf.PI * along / Mathf.Max(2f, waveLength)));

        return PosMod(v, step) < thickness;
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
        Func<Vector3, bool> includeWorld = null,
        float straightness01 = 0f,
        float braid01 = 0f,
        float plazaChance01 = 0f,
        int plazaExtraCells = 0)
    {
        var growth = new List<(Vector2Int, Vector3)>();
        if (width <= 0 || height <= 0) return growth;
        if (gridToWorld == null || isCellAvailable == null) return growth;

        corridorWidth = Mathf.Max(1, corridorWidth);
        int step = Mathf.Max(corridorWidth + 1, corridorStep);
        int mw   = Mathf.Max(1, width  / step);
        int mh   = Mathf.Max(1, height / step);

        // Anchor room 0 at the grid's min edge and stretch the last room so its block's far
        // edge lands on width-1/height-1. A fixed rx*step spacing leaves a leftover strip
        // (whenever width/height aren't an exact multiple of step) that no room or corridor
        // ever reaches — a disconnected dead-solid band along the top/right edges that
        // BuildPorousBorderCells' top/right border gaps would open into a dead end.
        int[] roomX = BuildRoomAnchors(mw, width, corridorWidth);
        int[] roomY = BuildRoomAnchors(mh, height, corridorWidth);

        var openCells = new HashSet<Vector2Int>(mw * mh * step * 4);

        // Open a corridorWidth × corridorWidth block at each logical room centre. Optional
        // "plaza" rooms grow up/right by a few cells, clamped so a >= 1-cell wall always remains
        // between the chamber and the next room's anchor (or the grid edge).
        plazaExtraCells = Mathf.Max(0, plazaExtraCells);
        for (int rx = 0; rx < mw; rx++)
        for (int ry = 0; ry < mh; ry++)
        {
            int bx = roomX[rx], by = roomY[ry];
            int extra = 0;
            if (plazaExtraCells > 0 && plazaChance01 > 0f && Random.value < plazaChance01)
            {
                int maxX = rx + 1 < mw ? roomX[rx + 1] - bx - corridorWidth - 1 : width  - bx - corridorWidth;
                int maxY = ry + 1 < mh ? roomY[ry + 1] - by - corridorWidth - 1 : height - by - corridorWidth;
                extra = Mathf.Max(0, Mathf.Min(plazaExtraCells, Mathf.Min(maxX, maxY)));
            }
            int size = corridorWidth + extra;
            for (int dx = 0; dx < size; dx++)
            for (int dy = 0; dy < size; dy++)
                openCells.Add(new Vector2Int(bx + dx, by + dy));
        }

        var logDirs = new[] {
            new Vector2Int( 1,  0),
            new Vector2Int(-1,  0),
            new Vector2Int( 0,  1),
            new Vector2Int( 0, -1)
        };

        // connected[x,y,d]: a passage exists from room (x,y) in logDirs[d]. d ^ 1 is the opposite.
        var connected = new bool[mw, mh, 4];

        // Carve a corridorWidth-wide passage between two adjacent room blocks.
        // The passage spans the gap between room edges (not including the room blocks themselves).
        void CarvePassage(Vector2Int a, int dirIndex)
        {
            var b = a + logDirs[dirIndex];
            connected[a.x, a.y, dirIndex] = true;
            connected[b.x, b.y, dirIndex ^ 1] = true;

            int px0 = roomX[a.x], py0 = roomY[a.y];
            int px1 = roomX[b.x], py1 = roomY[b.y];

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

        // DFS spanning tree — carve passages between rooms
        var visited  = new bool[mw, mh];
        var arrivedDir = new int[mw, mh]; // direction index used to enter each room (-1 = start)
        var stack    = new Stack<Vector2Int>();
        var rStart   = new Vector2Int(Random.Range(0, mw), Random.Range(0, mh));
        visited[rStart.x, rStart.y] = true;
        arrivedDir[rStart.x, rStart.y] = -1;
        stack.Push(rStart);

        var candidateDirs = new List<int>(4);
        while (stack.Count > 0)
        {
            var cur = stack.Peek();

            candidateDirs.Clear();
            for (int d = 0; d < 4; d++)
            {
                var nb = cur + logDirs[d];
                if (nb.x >= 0 && nb.x < mw && nb.y >= 0 && nb.y < mh && !visited[nb.x, nb.y])
                    candidateDirs.Add(d);
            }

            if (candidateDirs.Count == 0) { stack.Pop(); continue; }

            int chosenDir = candidateDirs[Random.Range(0, candidateDirs.Count)];

            // Straightness bias: keep going the way we arrived when that's still possible.
            int lastDir = arrivedDir[cur.x, cur.y];
            if (straightness01 > 0f && lastDir >= 0 && candidateDirs.Contains(lastDir)
                && Random.value < straightness01)
                chosenDir = lastDir;

            var chosen = cur + logDirs[chosenDir];
            visited[chosen.x, chosen.y] = true;
            arrivedDir[chosen.x, chosen.y] = chosenDir;
            stack.Push(chosen);
            CarvePassage(cur, chosenDir);
        }

        // Braiding: open dead-end rooms into a neighbour so the maze has loops.
        if (braid01 > 0f)
        {
            for (int rx = 0; rx < mw; rx++)
            for (int ry = 0; ry < mh; ry++)
            {
                int degree = 0;
                for (int d = 0; d < 4; d++) if (connected[rx, ry, d]) degree++;
                if (degree != 1 || Random.value >= braid01) continue;

                var room = new Vector2Int(rx, ry);
                candidateDirs.Clear();
                for (int d = 0; d < 4; d++)
                {
                    var nb = room + logDirs[d];
                    if (!connected[rx, ry, d] && nb.x >= 0 && nb.x < mw && nb.y >= 0 && nb.y < mh)
                        candidateDirs.Add(d);
                }
                if (candidateDirs.Count == 0) continue;
                CarvePassage(room, candidateDirs[Random.Range(0, candidateDirs.Count)]);
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

    // Evenly spaces room anchors across [0, extent-1] so room 0 sits at 0 and the last room's
    // corridorWidth-wide block reaches extent-1, instead of a fixed rx*step spacing that can
    // leave a leftover unreachable strip when extent isn't an exact multiple of step.
    private static int[] BuildRoomAnchors(int roomCount, int extent, int corridorWidth)
    {
        var anchors = new int[roomCount];
        int maxAnchor = Mathf.Max(0, extent - corridorWidth);
        for (int i = 0; i < roomCount; i++)
            anchors[i] = roomCount <= 1 ? 0 : Mathf.RoundToInt(i * (float)maxAnchor / (roomCount - 1));
        return anchors;
    }

    /// <summary>
    /// Assigns a MusicalRole to every cell in <paramref name="cells"/> using a Voronoi
    /// partition driven by the motif's active roles.
    ///
    /// If activeRoles has 1 role, every cell gets that role (no Voronoi needed).
    /// If 2–4 roles, the first role gets the farthest seed (dominant territory);
    /// remaining roles get evenly-distributed closer seeds.
    ///
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
        return BuildPorousBorderCells(width, height, exitCount, isCellAvailable, out _);
    }

    /// <summary>
    /// Same as the 4-arg overload, but also returns the gap cell groups (one list of
    /// (usually) 2 adjacent cells per exit, in generation order) via <paramref name="gapGroupsOut"/>
    /// so callers can place something (a trigger, a marker) at each opening.
    /// </summary>
    public static HashSet<Vector2Int> BuildPorousBorderCells(
        int width,
        int height,
        int exitCount,
        Func<int, int, bool> isCellAvailable,
        out List<List<Vector2Int>> gapGroupsOut)
    {
        gapGroupsOut = new List<List<Vector2Int>>();

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
        const int minGapLength = 2;

        if (exitCount > 0)
        {
            int n = perimeter.Count;
            for (int seg = 0; seg < exitCount; seg++)
            {
                int segStart = Mathf.RoundToInt(seg       * (n / (float)exitCount));
                int segEnd   = Mathf.RoundToInt((seg + 1) * (n / (float)exitCount));
                segEnd = Mathf.Min(segEnd, n);
                if (segEnd <= segStart) segEnd = Mathf.Min(segStart + 1, n);

                int firstIndex = Random.Range(segStart, segEnd);
                var group = new List<Vector2Int>(2);
                if (gapCells.Add(perimeter[firstIndex]))
                    group.Add(perimeter[firstIndex]);

                if (n >= minGapLength)
                {
                    int secondIndex;
                    if (firstIndex + 1 < segEnd)
                    {
                        secondIndex = firstIndex + 1;
                    }
                    else if (firstIndex - 1 >= segStart)
                    {
                        secondIndex = firstIndex - 1;
                    }
                    else
                    {
                        // Extremely short segment (length 1): spill to adjacent perimeter cell
                        // so each porous opening is still visibly at least two cells long.
                        secondIndex = (firstIndex + 1) % n;
                    }

                    if (gapCells.Add(perimeter[secondIndex]))
                        group.Add(perimeter[secondIndex]);
                }

                if (group.Count > 0) gapGroupsOut.Add(group);
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

    /// <summary>
    /// Like BuildPorousBorderCells, but the border is the full rectangle (bottom row included)
    /// and each entry of <paramref name="sides"/> gets exactly one 2-cell gap on that edge, never
    /// on a corner. <paramref name="gapGroupsOut"/> lines up with <paramref name="sides"/> by index.
    /// </summary>
    public static HashSet<Vector2Int> BuildPorousBorderCellsForSides(
        int width,
        int height,
        IReadOnlyList<BoundaryWrap.BoundarySide> sides,
        Func<int, int, bool> isCellAvailable,
        out List<List<Vector2Int>> gapGroupsOut)
    {
        gapGroupsOut = new List<List<Vector2Int>>();

        var result = new HashSet<Vector2Int>();
        if (width < 4 || height < 4 || isCellAvailable == null) return result;

        var gapCells = new HashSet<Vector2Int>();
        foreach (var side in sides)
        {
            bool horizontal = side == BoundaryWrap.BoundarySide.Top || side == BoundaryWrap.BoundarySide.Bottom;
            int along = horizontal ? width : height;

            // Gap spans [start, start + 1] along the edge, both strictly inside the corners.
            int start = Random.Range(1, along - 2);
            var group = new List<Vector2Int>(2);
            for (int k = 0; k < 2; k++)
            {
                var cell = side switch
                {
                    BoundaryWrap.BoundarySide.Top    => new Vector2Int(start + k, height - 1),
                    BoundaryWrap.BoundarySide.Bottom => new Vector2Int(start + k, 0),
                    BoundaryWrap.BoundarySide.Left   => new Vector2Int(0, start + k),
                    _                                => new Vector2Int(width - 1, start + k),
                };
                if (gapCells.Add(cell)) group.Add(cell);
            }
            gapGroupsOut.Add(group);
        }

        for (int x = 0; x < width; x++)
        for (int y = 0; y < height; y++)
        {
            if (x != 0 && x != width - 1 && y != 0 && y != height - 1) continue;
            var cell = new Vector2Int(x, y);
            if (gapCells.Contains(cell) || !isCellAvailable(x, y)) continue;
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
