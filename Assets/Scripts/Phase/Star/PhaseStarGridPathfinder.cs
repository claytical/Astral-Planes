using System;
using System.Collections.Generic;
using UnityEngine;

// Grid-aware pathing for the PhaseStar (CorridorFollow / RingOrbit nav modes, see
// StarNavigationParams). Same connectivity rule as WispExitPathfinder and
// StarSpawnReachabilityWeighter: 4-neighbour BFS through cells without dust. Differences:
//   - Multi-goal: colored dust targets are themselves solid, so a target is "reached" when the
//     search expands an open cell adjacent to it. First hit = nearest target BY PATH, not by
//     straight-line distance.
//   - Scratch collections are reused across calls (runs every replan, up to 4 stars).
public sealed class PhaseStarGridPathfinder
{
    private const int kBfsBudget = 4000;

    private static readonly Vector2Int[] kNeighbours4 =
    {
        new(1, 0), new(-1, 0), new(0, 1), new(0, -1)
    };

    private readonly Queue<Vector2Int> _queue = new(256);
    private readonly Dictionary<Vector2Int, Vector2Int> _parents = new(512);
    private readonly Dictionary<Vector2Int, int> _dist = new(512);
    private readonly List<Vector2Int> _waypoints = new(64);
    private readonly List<Vector2Int> _wanderCandidates = new(256);

    /// <summary>Open cells from the first step after the start cell to the cell adjacent to the target.</summary>
    public IReadOnlyList<Vector2Int> Waypoints => _waypoints;
    public bool HasPath { get; private set; }

    public void Clear()
    {
        HasPath = false;
        _waypoints.Clear();
    }

    /// <param name="isTarget">True for cells the star wants to reach (typically solid colored dust).</param>
    /// <param name="extraPassable">Optional: treat these cells as open even when dusty (e.g. Burrow carve radius).</param>
    public bool TryFindPathToAny(
        DrumTrack drum,
        Vector2Int from,
        Func<Vector2Int, bool> isTarget,
        out Vector2Int target,
        Func<Vector2Int, bool> extraPassable = null)
    {
        target = default;
        Clear();
        if (drum == null || isTarget == null) return false;

        int w = drum.GetSpawnGridWidth();
        int h = drum.GetSpawnGridHeight();
        if (w <= 0 || h <= 0) return false;

        from.x = Mathf.Clamp(from.x, 0, w - 1);
        from.y = Mathf.Clamp(from.y, 0, h - 1);

        _queue.Clear();
        _parents.Clear();
        _queue.Enqueue(from);
        _parents[from] = from;

        int visited = 0;
        while (_queue.Count > 0 && visited < kBfsBudget)
        {
            var cell = _queue.Dequeue();
            visited++;

            // Check targets adjacent to this open cell first so the nearest-by-path wins.
            for (int i = 0; i < kNeighbours4.Length; i++)
            {
                var nb = cell + kNeighbours4[i];
                if (nb.x < 0 || nb.y < 0 || nb.x >= w || nb.y >= h) continue;
                if (!isTarget(nb)) continue;

                target = nb;
                BuildWaypoints(from, cell);
                HasPath = true;
                return true;
            }

            for (int i = 0; i < kNeighbours4.Length; i++)
            {
                var nb = cell + kNeighbours4[i];
                if (nb.x < 0 || nb.y < 0 || nb.x >= w || nb.y >= h) continue;
                if (_parents.ContainsKey(nb)) continue;
                if (!IsPassable(drum, nb, extraPassable)) continue;
                _parents[nb] = cell;
                _queue.Enqueue(nb);
            }
        }

        return false;
    }

    /// <summary>
    /// Picks a random open cell reachable from <paramref name="from"/> at least
    /// <paramref name="minHops"/> steps away (falls back to the farthest reached) and stores the
    /// path to it. Used for corridor-following idle wander.
    /// </summary>
    public bool TryPickWanderTarget(DrumTrack drum, Vector2Int from, int minHops, out Vector2Int wanderCell)
    {
        wanderCell = default;
        Clear();
        if (drum == null) return false;

        int w = drum.GetSpawnGridWidth();
        int h = drum.GetSpawnGridHeight();
        if (w <= 0 || h <= 0) return false;

        from.x = Mathf.Clamp(from.x, 0, w - 1);
        from.y = Mathf.Clamp(from.y, 0, h - 1);

        _queue.Clear();
        _parents.Clear();
        _dist.Clear();
        _wanderCandidates.Clear();
        _queue.Enqueue(from);
        _parents[from] = from;
        _dist[from] = 0;

        Vector2Int farthest = from;
        int farthestD = 0;
        int visited = 0;
        while (_queue.Count > 0 && visited < kBfsBudget)
        {
            var cell = _queue.Dequeue();
            visited++;
            int d = _dist[cell];
            if (d >= minHops) _wanderCandidates.Add(cell);
            if (d > farthestD) { farthestD = d; farthest = cell; }

            for (int i = 0; i < kNeighbours4.Length; i++)
            {
                var nb = cell + kNeighbours4[i];
                if (nb.x < 0 || nb.y < 0 || nb.x >= w || nb.y >= h) continue;
                if (_parents.ContainsKey(nb)) continue;
                if (drum.HasDustAt(nb)) continue;
                _parents[nb] = cell;
                _dist[nb] = d + 1;
                _queue.Enqueue(nb);
            }
        }

        if (_wanderCandidates.Count > 0)
            wanderCell = _wanderCandidates[UnityEngine.Random.Range(0, _wanderCandidates.Count)];
        else if (farthest != from)
            wanderCell = farthest;
        else
            return false;

        BuildWaypoints(from, wanderCell);
        HasPath = true;
        return true;
    }

    /// <summary>Drops leading waypoints the star has already reached (it is standing on or past them).</summary>
    public void AdvancePast(Vector2Int currentCell)
    {
        int idx = _waypoints.IndexOf(currentCell);
        if (idx >= 0) _waypoints.RemoveRange(0, idx + 1);
    }

    private static bool IsPassable(DrumTrack drum, Vector2Int cell, Func<Vector2Int, bool> extraPassable)
    {
        if (!drum.HasDustAt(cell)) return true;
        return extraPassable != null && extraPassable(cell);
    }

    private void BuildWaypoints(Vector2Int from, Vector2Int end)
    {
        _waypoints.Clear();
        var c = end;
        int guard = 0;
        while (c != from && guard++ < kBfsBudget)
        {
            _waypoints.Add(c);
            c = _parents[c];
        }
        _waypoints.Reverse();
    }
}

/// <summary>
/// World-space maze geometry the star's nav modes steer against: lane axes for LaneGlide,
/// ring centre for RingOrbit. Built once per star Initialize from the motif's MazePatternConfig.
/// </summary>
public readonly struct MazeNavGeometry
{
    public readonly bool HasLaneAxis;
    public readonly Vector2 LaneAxis;
    public readonly bool HasSecondLaneAxis;
    public readonly Vector2 LaneAxis2;
    public readonly bool HasRingCentre;
    public readonly Vector2 RingCentre;

    private MazeNavGeometry(bool hasLane, Vector2 lane, bool hasLane2, Vector2 lane2, bool hasRing, Vector2 ring)
    {
        HasLaneAxis = hasLane; LaneAxis = lane;
        HasSecondLaneAxis = hasLane2; LaneAxis2 = lane2;
        HasRingCentre = hasRing; RingCentre = ring;
    }

    public static MazeNavGeometry Build(MazePatternConfig maze, DrumTrack drum, CosmicDustGenerator gen)
    {
        if (maze == null || drum == null) return default;

        bool hasLane = false, hasLane2 = false, hasRing = false;
        Vector2 lane = Vector2.zero, lane2 = Vector2.zero, ring = Vector2.zero;

        if (maze.patternType == MazePatternType.DiagonalLanes && maze.diagonalLanes != null)
        {
            int sx = maze.diagonalLanes.slopeX, sy = maze.diagonalLanes.slopeY;
            if (sx == 0 && sy == 0) { sx = 1; sy = 2; }
            // Lanes are lines of constant sx*x + sy*y → direction (sy, -sx) in grid space.
            hasLane = TryGridDirToWorld(drum, new Vector2Int(sy, -sx), out lane);
            // crossHatch mirrors to sx*x - sy*y → direction (sy, sx).
            if (maze.diagonalLanes.crossHatch)
                hasLane2 = TryGridDirToWorld(drum, new Vector2Int(sy, sx), out lane2);
        }

        if (maze.patternType == MazePatternType.RingChokepoints && gen != null &&
            gen.TryGetMazeAnchorCell(out var anchor))
        {
            ring = drum.GridToWorldPosition(anchor);
            hasRing = true;
        }

        return new MazeNavGeometry(hasLane, lane, hasLane2, lane2, hasRing, ring);
    }

    // Grid → world via GridToWorldPosition so non-square tiles are respected.
    private static bool TryGridDirToWorld(DrumTrack drum, Vector2Int gridDir, out Vector2 worldDir)
    {
        int w = drum.GetSpawnGridWidth(), h = drum.GetSpawnGridHeight();
        var origin = new Vector2Int(Mathf.Max(0, w / 2), Mathf.Max(0, h / 2));
        Vector2 a = drum.GridToWorldPosition(origin);
        Vector2 b = drum.GridToWorldPosition(origin + gridDir);
        worldDir = b - a;
        if (worldDir.sqrMagnitude < 1e-8f) { worldDir = Vector2.zero; return false; }
        worldDir.Normalize();
        return true;
    }
}
