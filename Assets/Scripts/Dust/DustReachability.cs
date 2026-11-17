using System;
using System.Collections.Generic;
using UnityEngine;

// =========================================================================
//  DustReachability
//
//  Clearance-aware grid search for "can a ship actually get there?". Ships
//  boost through ordinary dust, so carvable dust is passable; only hard walls
//  (Cosmic Blocks, bypass-proof interstitial dust) stop them. A ship's centre
//  may stand on a cell only if every cell within the clearance radius rc
//  (derived from the widest active ship) is free of hard walls — so a wide
//  ship can't be routed through a gap it doesn't fit.
//
//  0-1 BFS: "soft" walls (cells about to become blocks) are passable at cost 1
//  per node that touches one, everything else costs 0. One run from a seed
//  answers both "is X reachable?" (hard walls only) and "which soft cells must
//  stay open so X is reachable?" (trace the cheapest path, CosmicDustGenerator
//  .Blocks.cs doorways). Modeled on StarSpawnReachabilityWeighter's multi-source
//  BFS and CosmicDustGenerator.Interstitial's CarveConnectorToReachable.
//  The grid is ~40x18, so a full run is cheap enough to do on demand.
// =========================================================================
public sealed class DustReachability
{
    private int _w, _h, _rc;
    private int[] _dist;
    private int[] _parent;
    private readonly LinkedList<int> _deque = new LinkedList<int>();
    private readonly List<Vector2Int> _disk = new List<Vector2Int>();

    public int ClearanceCells => _rc;

    /// <param name="hardWall">Cells a ship can never pass (blocks, uncarveable dust).</param>
    /// <param name="softWall">Optional: cells passable at a cost (candidate blocks). Null = none.</param>
    public void Run(int w, int h, int clearanceCells, IReadOnlyList<Vector2Int> seeds,
                    Func<Vector2Int, bool> hardWall, Func<Vector2Int, bool> softWall = null)
    {
        _w = Mathf.Max(0, w); _h = Mathf.Max(0, h);
        _rc = Mathf.Max(0, clearanceCells);
        int n = _w * _h;
        if (_dist == null || _dist.Length != n) { _dist = new int[n]; _parent = new int[n]; }
        for (int i = 0; i < n; i++) { _dist[i] = -1; _parent[i] = -1; }
        BuildDisk(_rc);
        if (n == 0 || seeds == null) return;

        _deque.Clear();
        for (int i = 0; i < seeds.Count; i++)
        {
            var s = seeds[i];
            if (!InBounds(s)) continue;
            int si = s.y * _w + s.x;
            if (_dist[si] == 0) continue;
            _dist[si] = 0;               // seeds always count, even if the ship is wedged in
            _deque.AddFirst(si);
        }

        while (_deque.Count > 0)
        {
            int cur = _deque.First.Value; _deque.RemoveFirst();
            int cx = cur % _w, cy = cur / _w;
            int cd = _dist[cur];
            for (int k = 0; k < 4; k++)
            {
                int nx = cx + (k == 0 ? 1 : k == 1 ? -1 : 0);
                int ny = cy + (k == 2 ? 1 : k == 3 ? -1 : 0);
                if (nx < 0 || ny < 0 || nx >= _w || ny >= _h) continue;
                var np = new Vector2Int(nx, ny);
                if (!Clear(np, hardWall)) continue;
                int cost = (softWall != null && TouchesSoft(np, softWall)) ? 1 : 0;
                int ni = ny * _w + nx;
                int nd = cd + cost;
                if (_dist[ni] >= 0 && _dist[ni] <= nd) continue;
                _dist[ni] = nd;
                _parent[ni] = cur;
                if (cost == 0) _deque.AddFirst(ni); else _deque.AddLast(ni);
            }
        }
    }

    /// <summary>
    /// Cheapest reached node within the clearance radius of <paramref name="target"/> — a ship
    /// only has to get close enough to touch it. Returns false if nothing near it was reached.
    /// </summary>
    public bool TryGetBestNear(Vector2Int target, out Vector2Int node, out int cost)
    {
        node = default; cost = int.MaxValue;
        if (_dist == null) return false;
        int r = Mathf.Max(1, _rc);
        bool found = false;
        for (int dy = -r; dy <= r; dy++)
        for (int dx = -r; dx <= r; dx++)
        {
            if (dx * dx + dy * dy > r * r) continue;
            var p = new Vector2Int(target.x + dx, target.y + dy);
            if (!InBounds(p)) continue;
            int d = _dist[p.y * _w + p.x];
            if (d < 0 || d >= cost) continue;
            cost = d; node = p; found = true;
        }
        return found;
    }

    public bool IsReachableNear(Vector2Int target) => TryGetBestNear(target, out _, out _);

    /// <summary>Node path from a seed to <paramref name="node"/> (seed first).</summary>
    public void TracePath(Vector2Int node, List<Vector2Int> outPath)
    {
        outPath.Clear();
        if (!InBounds(node) || _dist == null) return;
        int i = node.y * _w + node.x;
        int guard = _w * _h;
        while (i >= 0 && guard-- > 0)
        {
            outPath.Add(new Vector2Int(i % _w, i / _w));
            i = _parent[i];
        }
        outPath.Reverse();
    }

    /// <summary>Cells within the clearance radius of <paramref name="center"/> (in bounds).</summary>
    public void ForEachInClearance(Vector2Int center, Action<Vector2Int> visit)
    {
        for (int i = 0; i < _disk.Count; i++)
        {
            var p = center + _disk[i];
            if (InBounds(p)) visit(p);
        }
    }

    private bool InBounds(Vector2Int p) => p.x >= 0 && p.y >= 0 && p.x < _w && p.y < _h;

    // Out-of-bounds cells are ignored (the play-area edge isn't a grid wall), so ships can still
    // hug the border.
    private bool Clear(Vector2Int c, Func<Vector2Int, bool> hardWall)
    {
        for (int i = 0; i < _disk.Count; i++)
        {
            var p = c + _disk[i];
            if (InBounds(p) && hardWall(p)) return false;
        }
        return true;
    }

    private bool TouchesSoft(Vector2Int c, Func<Vector2Int, bool> softWall)
    {
        for (int i = 0; i < _disk.Count; i++)
        {
            var p = c + _disk[i];
            if (InBounds(p) && softWall(p)) return true;
        }
        return false;
    }

    private void BuildDisk(int r)
    {
        _disk.Clear();
        for (int dy = -r; dy <= r; dy++)
        for (int dx = -r; dx <= r; dx++)
            if (dx * dx + dy * dy <= r * r) _disk.Add(new Vector2Int(dx, dy));
    }
}
