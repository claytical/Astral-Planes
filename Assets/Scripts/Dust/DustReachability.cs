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
//  The grid is ~40x18, so a full run is cheap enough to do on demand; the wall
//  predicates are sampled once per cell up front and the deque never allocates.
// =========================================================================
public sealed class DustReachability
{
    private int _w, _h, _rc;
    private int[] _dist;
    private int[] _parent;
    // Wall predicates are sampled once per cell per run (instead of once per disk cell per
    // neighbour visit) and the deque is an array ring buffer, so a run allocates nothing once
    // the buffers have grown to the grid size.
    private bool[] _hard;
    private bool[] _soft;
    private int[] _dq;
    private int _dqHead, _dqCount;
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
        if (_dist == null || _dist.Length != n)
        {
            _dist = new int[n]; _parent = new int[n];
            _hard = new bool[n]; _soft = new bool[n];
        }
        for (int i = 0; i < n; i++) { _dist[i] = -1; _parent[i] = -1; }
        BuildDisk(_rc);
        if (n == 0 || seeds == null) return;

        bool anySoft = softWall != null;
        for (int y = 0; y < _h; y++)
        for (int x = 0; x < _w; x++)
        {
            var p = new Vector2Int(x, y);
            int i = y * _w + x;
            _hard[i] = hardWall != null && hardWall(p);
            _soft[i] = anySoft && softWall(p);
        }

        DequeClear(n);
        for (int i = 0; i < seeds.Count; i++)
        {
            var s = seeds[i];
            if (!InBounds(s)) continue;
            int si = s.y * _w + s.x;
            if (_dist[si] == 0) continue;
            _dist[si] = 0;               // seeds always count, even if the ship is wedged in
            PushFront(si);
        }

        while (_dqCount > 0)
        {
            int cur = PopFront();
            int cx = cur % _w, cy = cur / _w;
            int cd = _dist[cur];
            for (int k = 0; k < 4; k++)
            {
                int nx = cx + (k == 0 ? 1 : k == 1 ? -1 : 0);
                int ny = cy + (k == 2 ? 1 : k == 3 ? -1 : 0);
                if (nx < 0 || ny < 0 || nx >= _w || ny >= _h) continue;
                var np = new Vector2Int(nx, ny);
                if (!Clear(np)) continue;
                int cost = (anySoft && TouchesSoft(np)) ? 1 : 0;
                int ni = ny * _w + nx;
                int nd = cd + cost;
                if (_dist[ni] >= 0 && _dist[ni] <= nd) continue;
                _dist[ni] = nd;
                _parent[ni] = cur;
                if (cost == 0) PushFront(ni); else PushBack(ni);
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
    private bool Clear(Vector2Int c)
    {
        for (int i = 0; i < _disk.Count; i++)
        {
            var p = c + _disk[i];
            if (InBounds(p) && _hard[p.y * _w + p.x]) return false;
        }
        return true;
    }

    private bool TouchesSoft(Vector2Int c)
    {
        for (int i = 0; i < _disk.Count; i++)
        {
            var p = c + _disk[i];
            if (InBounds(p) && _soft[p.y * _w + p.x]) return true;
        }
        return false;
    }

    // ---- array ring-buffer deque (0-1 BFS can re-push a node, so it grows on demand) ----
    private void DequeClear(int minCapacity)
    {
        int cap = Mathf.Max(16, minCapacity);
        if (_dq == null || _dq.Length < cap) _dq = new int[cap];
        _dqHead = 0; _dqCount = 0;
    }

    private void GrowDeque()
    {
        var bigger = new int[_dq.Length * 2];
        for (int i = 0; i < _dqCount; i++) bigger[i] = _dq[(_dqHead + i) % _dq.Length];
        _dq = bigger; _dqHead = 0;
    }

    private void PushFront(int v)
    {
        if (_dqCount == _dq.Length) GrowDeque();
        _dqHead = (_dqHead - 1 + _dq.Length) % _dq.Length;
        _dq[_dqHead] = v; _dqCount++;
    }

    private void PushBack(int v)
    {
        if (_dqCount == _dq.Length) GrowDeque();
        _dq[(_dqHead + _dqCount) % _dq.Length] = v; _dqCount++;
    }

    private int PopFront()
    {
        int v = _dq[_dqHead];
        _dqHead = (_dqHead + 1) % _dq.Length; _dqCount--;
        return v;
    }

    private void BuildDisk(int r)
    {
        _disk.Clear();
        for (int dy = -r; dy <= r; dy++)
        for (int dx = -r; dx <= r; dx++)
            if (dx * dx + dy * dy <= r * r) _disk.Add(new Vector2Int(dx, dy));
    }
}
