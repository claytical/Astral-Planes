using System;
using System.Collections.Generic;
using UnityEngine;

// =========================================================================
//  TentacleVineRoute
//
//  World-space polyline a PhaseStar tentacle grows along, root → target.
//  nodes[0] is the LIVE root (re-pinned to the star every tick); nodes[1..]
//  are fixed in the world, so bumping the star stretches/bends the root end
//  while the rest of the vine keeps its path to the target.
//
//  The grow front is stored as distance FROM THE TARGET (FrontDistFromTarget),
//  so editing the root end (prepending breadcrumbs, trimming when the star is
//  pushed back along the vine) never moves the front in the world.
// =========================================================================
public sealed class TentacleVineRoute
{
    public readonly List<Vector2> nodes = new List<Vector2>(32);
    private readonly List<float> _cum = new List<float>(32); // cumulative length at node i

    /// <summary>Distance from the grow front back to the target. Total = nothing grown, 0 = reached.</summary>
    public float FrontDistFromTarget;

    public float TotalLength => _cum.Count > 0 ? _cum[_cum.Count - 1] : 0f;
    public int Count => nodes.Count;
    public float GrownLength => Mathf.Max(0f, TotalLength - FrontDistFromTarget);

    public void Clear()
    {
        nodes.Clear();
        _cum.Clear();
        FrontDistFromTarget = 0f;
    }

    public void SetNodes(List<Vector2> pts)
    {
        nodes.Clear();
        nodes.AddRange(pts);
        Recompute();
    }

    public void SetRoot(Vector2 p)
    {
        if (nodes.Count == 0) return;
        nodes[0] = p;
        Recompute();
    }

    /// <summary>Pins <paramref name="p"/> as a fixed node right after the live root.</summary>
    public void InsertAfterRoot(Vector2 p)
    {
        if (nodes.Count == 0) return;
        nodes.Insert(1, p);
        Recompute();
    }

    /// <summary>Removes fixed nodes [1, count] (never the root or the target).</summary>
    public void RemoveAfterRoot(int count)
    {
        count = Mathf.Min(count, nodes.Count - 2);
        if (count <= 0) return;
        nodes.RemoveRange(1, count);
        Recompute();
        FrontDistFromTarget = Mathf.Min(FrontDistFromTarget, TotalLength);
    }

    public Vector2 Sample(float dist)
    {
        if (nodes.Count == 0) return Vector2.zero;
        if (nodes.Count == 1 || dist <= 0f) return nodes[0];
        float total = TotalLength;
        if (dist >= total) return nodes[nodes.Count - 1];

        int i = 1;
        while (i < _cum.Count - 1 && _cum[i] < dist) i++;
        float segLen = _cum[i] - _cum[i - 1];
        float t = segLen > 1e-5f ? (dist - _cum[i - 1]) / segLen : 0f;
        return Vector2.Lerp(nodes[i - 1], nodes[i], t);
    }

    /// <summary>Route segment (node i → i+1) and fraction along it at <paramref name="dist"/> from the root.</summary>
    public void Locate(float dist, out int seg, out float t)
    {
        seg = 0; t = 0f;
        if (nodes.Count < 2) return;
        if (dist <= 0f) return;
        if (dist >= TotalLength) { seg = nodes.Count - 2; t = 1f; return; }
        int i = 1;
        while (i < _cum.Count - 1 && _cum[i] < dist) i++;
        seg = i - 1;
        float segLen = _cum[i] - _cum[i - 1];
        t = segLen > 1e-5f ? Mathf.Clamp01((dist - _cum[i - 1]) / segLen) : 0f;
    }

    private void Recompute()
    {
        _cum.Clear();
        float acc = 0f;
        for (int i = 0; i < nodes.Count; i++)
        {
            if (i > 0) acc += Vector2.Distance(nodes[i - 1], nodes[i]);
            _cum.Add(acc);
        }
    }
}

// =========================================================================
//  TentacleVinePlanner
//
//  Routes a tentacle around cosmic dust with DustReachability's 0-1 BFS
//  (clearance 0): open cells cost 0, dust cells cost 1, so any fully open
//  route wins and dust is crossed only when nothing else reaches the target
//  — then as little as possible. The cell path is string-pulled into a few
//  world-space corners so the vine curves instead of stair-stepping.
// =========================================================================
public sealed class TentacleVinePlanner
{
    private readonly DustReachability _reach = new DustReachability();
    private readonly List<Vector2Int> _seeds = new List<Vector2Int>(1);
    private readonly List<Vector2Int> _cells = new List<Vector2Int>(64);
    private readonly List<Vector2> _cellWorld = new List<Vector2>(64);
    private readonly List<bool> _mandatory = new List<bool>(64);

    // Per-run predicate state: cached delegates read these instead of capturing locals, so a
    // plan doesn't allocate closures (Run samples the predicates synchronously).
    private Vector2Int _runTarget, _runFrom;
    private Func<Vector2Int, bool> _runHardWall;
    private DrumTrack _runDrum;
    private Func<Vector2Int, bool> _isHard, _isSoft;

    private bool IsHardForRun(Vector2Int c) =>
        c != _runTarget && c != _runFrom && _runHardWall != null && _runHardWall(c);

    private bool IsSoftForRun(Vector2Int c) =>
        c != _runTarget && c != _runFrom && _runDrum != null && _runDrum.HasDustAt(c);

    /// <summary>
    /// Plans root → target. <paramref name="outNodes"/> starts at <paramref name="rootWorld"/> and
    /// ends at the target cell centre. Returns false (straight-line fallback written) when no route
    /// exists at all. <paramref name="dustCrossings"/> = dust cells the route must bore through.
    /// </summary>
    public bool TryPlan(
        DrumTrack drum,
        Vector2 rootWorld,
        Vector2Int target,
        Func<Vector2Int, bool> hardWall,
        List<Vector2> outNodes,
        out int dustCrossings)
    {
        outNodes.Clear();
        dustCrossings = 0;
        Vector2 targetWorld = drum.GridToWorldPosition(target);

        int w = drum.GetSpawnGridWidth();
        int h = drum.GetSpawnGridHeight();
        if (w <= 0 || h <= 0) return WriteStraight(rootWorld, targetWorld, outNodes);

        Vector2Int from = drum.WorldToGridPosition(rootWorld);
        from.x = Mathf.Clamp(from.x, 0, w - 1);
        from.y = Mathf.Clamp(from.y, 0, h - 1);

        _seeds.Clear();
        _seeds.Add(from);
        _runTarget = target;
        _runFrom = from;
        _runHardWall = hardWall;
        _runDrum = drum;
        _isHard ??= IsHardForRun;
        _isSoft ??= IsSoftForRun;
        _reach.Run(w, h, 0, _seeds, _isHard, _isSoft);
        _runHardWall = null;
        _runDrum = null;

        if (!_reach.TryGetBestNear(target, out var node, out int cost))
            return WriteStraight(rootWorld, targetWorld, outNodes);

        _reach.TracePath(node, _cells);
        if (_cells.Count == 0 || _cells[_cells.Count - 1] != target) _cells.Add(target);
        dustCrossings = cost;

        // Mandatory corners: root, every dust cell the route bores through, the target.
        _cellWorld.Clear();
        _mandatory.Clear();
        for (int i = 0; i < _cells.Count; i++)
        {
            var c = _cells[i];
            bool isEnd = i == 0 || i == _cells.Count - 1;
            _cellWorld.Add(i == 0 ? rootWorld : drum.GridToWorldPosition(c));
            _mandatory.Add(isEnd || drum.HasDustAt(c));
        }

        // Greedy string-pull between consecutive mandatory nodes.
        float cell = Mathf.Max(0.01f, drum.GetCellWorldSize());
        outNodes.Add(_cellWorld[0]);
        int anchor = 0;
        while (anchor < _cells.Count - 1)
        {
            int nextMandatory = anchor + 1;
            while (nextMandatory < _cells.Count - 1 && !_mandatory[nextMandatory]) nextMandatory++;

            int best = anchor + 1;
            for (int j = nextMandatory; j > anchor + 1; j--)
            {
                if (SegmentClear(drum, _cellWorld[anchor], _cellWorld[j], _cells[anchor], _cells[j], cell))
                {
                    best = j;
                    break;
                }
            }
            outNodes.Add(_cellWorld[best]);
            anchor = best;
        }

        if (outNodes.Count < 2) return WriteStraight(rootWorld, targetWorld, outNodes);
        return true;
    }

    /// <summary>
    /// True when the world segment a→b crosses no dust cell other than the endpoint cells.
    /// Sampled at quarter-cell steps.
    /// </summary>
    public static bool SegmentClear(DrumTrack drum, Vector2 a, Vector2 b, Vector2Int ignoreA, Vector2Int ignoreB, float cellWorld)
    {
        float len = Vector2.Distance(a, b);
        int steps = Mathf.Max(1, Mathf.CeilToInt(len / Mathf.Max(0.01f, cellWorld * 0.25f)));
        for (int s = 1; s < steps; s++)
        {
            var c = drum.WorldToGridPosition(Vector2.Lerp(a, b, s / (float)steps));
            if (c == ignoreA || c == ignoreB) continue;
            if (drum.HasDustAt(c)) return false;
        }
        return true;
    }

    /// <summary>
    /// Adds gentle alternating bends along the route's straight runs so the vine reads as curved
    /// growth instead of straight lines between corners. Every bend point must sit on an open cell
    /// and keep both adjoining segments dust-free; otherwise it's halved, then dropped. Segments
    /// that bore through dust stay straight (their check fails). Done once at plan time, so the
    /// bends are part of the locked route and never move.
    /// </summary>
    /// <param name="meander">Bend offset as a fraction of the run between bends.</param>
    /// <param name="maxOffsetWorld">Cap on a bend's sideways offset.</param>
    /// <param name="spacingWorld">Roughly one bend per this much route length.</param>
    /// <param name="startSign">±1: which side the first bend goes (vary per tentacle).</param>
    public void AddMeander(DrumTrack drum, List<Vector2> nodes, float meander, float maxOffsetWorld,
                           float spacingWorld, float startSign)
    {
        if (drum == null || nodes.Count < 2 || meander <= 0f || maxOffsetWorld <= 0f) return;

        float cell = Mathf.Max(0.01f, drum.GetCellWorldSize());
        int gridW = drum.GetSpawnGridWidth(), gridH = drum.GetSpawnGridHeight();
        spacingWorld = Mathf.Max(cell, spacingWorld);
        _meanderScratch.Clear();
        _meanderScratch.Add(nodes[0]);
        float sign = startSign >= 0f ? 1f : -1f;

        for (int i = 0; i < nodes.Count - 1; i++)
        {
            Vector2 a = nodes[i], b = nodes[i + 1];
            Vector2 ab = b - a;
            float len = ab.magnitude;
            Vector2Int bCell = drum.WorldToGridPosition(b);

            int bends = Mathf.FloorToInt(len / spacingWorld);
            if (bends == 0 && len > cell * 1.5f) bends = 1;

            if (bends > 0 && len > 1e-4f)
            {
                Vector2 perp = new Vector2(-ab.y, ab.x) / len;
                float baseAmp = Mathf.Min(meander * len / (bends + 1), maxOffsetWorld);

                for (int j = 1; j <= bends; j++)
                {
                    Vector2 basePt = Vector2.Lerp(a, b, j / (float)(bends + 1));
                    Vector2 prev = _meanderScratch[_meanderScratch.Count - 1];
                    Vector2Int prevCell = drum.WorldToGridPosition(prev);

                    for (float amp = baseAmp; amp >= cell * 0.15f; amp *= 0.5f)
                    {
                        Vector2 p = basePt + perp * (amp * sign);
                        Vector2Int pCell = drum.WorldToGridPosition(p);
                        if (pCell.x < 0 || pCell.y < 0 || pCell.x >= gridW || pCell.y >= gridH) continue;
                        if (drum.HasDustAt(pCell)) continue;
                        if (!SegmentClear(drum, prev, p, prevCell, pCell, cell)) continue;
                        if (!SegmentClear(drum, p, b, pCell, bCell, cell)) continue;
                        _meanderScratch.Add(p);
                        break;
                    }
                    sign = -sign; // alternate sides → S-curves
                }
            }
            _meanderScratch.Add(b);
        }

        nodes.Clear();
        nodes.AddRange(_meanderScratch);
    }

    private readonly List<Vector2> _meanderScratch = new List<Vector2>(64);

    private static bool WriteStraight(Vector2 a, Vector2 b, List<Vector2> outNodes)
    {
        outNodes.Clear();
        outNodes.Add(a);
        outNodes.Add(b);
        return false;
    }
}
