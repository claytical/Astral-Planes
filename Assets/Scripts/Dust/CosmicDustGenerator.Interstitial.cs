using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public partial class CosmicDustGenerator
{
    // One tunnel exit from a path-choice interstitial maze: AnchorCell is the cell
    // PathChoiceCoordinator should carve/route a corridor toward and place a trigger at;
    // GapCells are every border cell that makes up the opening; Side is the boundary edge
    // it sits on, used by SceneFlowCoordinator to mirror the next maze's entry point.
    public struct PathExitInfo
    {
        public Vector2Int AnchorCell;
        public List<Vector2Int> GapCells;
        public BoundaryWrap.BoundarySide Side;
        // Radius (cells) of the permanent-clear disk carved around every gap cell — the real
        // passable opening is the gap span widened by this on each end.
        public int PassRadiusCells;
    }

    /// <summary>
    /// Grows a standalone "path choice" maze around the given vehicle cells: no PhaseStar,
    /// blind (flat-tinted, no per-role imprints) walls, and N border exits taken from
    /// <paramref name="interstitialPattern"/>.porousBorder.exitCount. Reports the exits via
    /// <paramref name="onExitsComputed"/> once growth and permanent-disk carving are done, so
    /// every exit's mouth is already guaranteed open before a trigger gets placed there.
    /// </summary>
    public IEnumerator GenerateInterstitialMaze(
        IReadOnlyList<Vector2Int> vehicleCells,
        MazePatternConfig interstitialPattern,
        float totalSpawnDuration,
        Action<List<PathExitInfo>> onExitsComputed)
    {
        _isBootstrappingMaze = true;

        if (drums == null)
        {
            Debug.LogError("[MAZE] No DrumTrack available; cannot build interstitial maze.");
            _isBootstrappingMaze = false;
            onExitsComputed?.Invoke(new List<PathExitInfo>());
            yield break;
        }

        yield return new WaitUntil(() =>
            drums.HasSpawnGrid() &&
            drums.GetSpawnGridWidth()  > 0 &&
            drums.GetSpawnGridHeight() > 0 &&
            Camera.main != null);

        if (activeDustRoot != null && !activeDustRoot.gameObject.activeSelf)
            activeDustRoot.gameObject.SetActive(true);

        ClearMaze();
        drums.SyncTileWithScreen();
        EnsureCellGrid();
        int w = drums.GetSpawnGridWidth();
        int h = drums.GetSpawnGridHeight();

        _runtimeVoidOnlyDustCreation = false;

        // MazeTopologyService.Context always wants a StarCell, but Tunnels (the only pattern
        // the interstitial uses) never reads it — only RingChokepoints does. Any in-bounds cell
        // is inert here.
        Vector2Int contextAnchor = (vehicleCells != null && vehicleCells.Count > 0)
            ? vehicleCells[0]
            : new Vector2Int(w / 2, h / 2);

        var reserved = new HashSet<Vector2Int>();
        const int startupVehicleReserveRadiusCells = 2;
        if (vehicleCells != null)
        {
            for (int i = 0; i < vehicleCells.Count; i++)
            {
                var v = vehicleCells[i];
                for (int dx = -startupVehicleReserveRadiusCells; dx <= startupVehicleReserveRadiusCells; dx++)
                {
                    for (int dy = -startupVehicleReserveRadiusCells; dy <= startupVehicleReserveRadiusCells; dy++)
                    {
                        if (dx * dx + dy * dy > startupVehicleReserveRadiusCells * startupVehicleReserveRadiusCells)
                            continue;
                        var gp = new Vector2Int(v.x + dx, v.y + dy);
                        if (!IsInBounds(gp)) continue;
                        reserved.Add(gp);
                        _permanentClearCells.Add(gp);
                    }
                }
            }
        }

        var mazeResult = BuildMazeGrowthFromConfig(interstitialPattern, contextAnchor, reserved, out var gapGroups, busyness01Override: 0.5f);

        _mazePatternCells = new HashSet<Vector2Int>(mazeResult.Count);
        foreach (var (cell, _) in mazeResult)
            _mazePatternCells.Add(cell);

        // Blind exits: skip BuildMazeRoleImprints' per-role Voronoi coloring (it exists to
        // telegraph musical-role territory for a real motif's maze) — flat tint instead, so no
        // wall color can accidentally hint at which exit leads where. Walls are the bypass-proof
        // "Solid" dust type (CellFlags.Solid), not just high resistance, so no ship's
        // carveResistanceBypass01 can carve through them — see CosmicDustGenerator.CellLifecycle.cs.
        _imprints.EnsureAllocated(mazeResult.Count * 2);
        foreach (var (gp, _) in mazeResult)
        {
            _imprints[gp] = new DustImprint
            {
                role              = MusicalRole.None,
                color             = config.solidDustTint,
                carveResistance01 = 1f,
                drainResistance01 = 1f,
                maxEnergyUnits    = 1,
                healDelay         = 0f
            };
            SetCellFlag(gp, CellFlags.Solid);
        }

        var cellsToFill = new List<(Vector2Int grid, Vector3 world)>(mazeResult);

        float spawnDuration = Mathf.Clamp(totalSpawnDuration, 0.05f, 3.0f);
        if (_spawnRoutine != null)
        {
            StopCoroutine(_spawnRoutine);
            _spawnRoutine = null;
        }
        for (int s = cellsToFill.Count - 1; s > 0; s--)
        {
            int r = UnityEngine.Random.Range(0, s + 1);
            (cellsToFill[s], cellsToFill[r]) = (cellsToFill[r], cellsToFill[s]);
        }
        _spawnRoutine = StartCoroutine(StaggeredGrowthFitDuration(cellsToFill, spawnDuration));
        yield return _spawnRoutine;
        EnterRuntimeVoidOnlyDustCreationMode();
        _spawnRoutine = null;

        // Corridor width is sized to the widest active ship (see PathChoiceCoordinator), so the
        // exit-gap and connector carves below must scale with it too, or they become the new
        // bottleneck once a ship-driven corridorWidth grows past the old fixed radius of 1.
        int corridorWidth = Mathf.Max(1, interstitialPattern.tunnels.corridorWidth);
        int passRadiusCells = Mathf.Max(1, Mathf.CeilToInt((corridorWidth - 1) / 2f));

        int vehicleHoleRadiusCells = Mathf.Max(3, passRadiusCells);
        if (vehicleCells != null)
            foreach (var vehCell in vehicleCells)
                CarvePermanentDisk(vehCell, vehicleHoleRadiusCells);

        // Everything must connect to the first vehicle's region: neither a vehicle's hole nor an
        // exit gap is guaranteed to touch BuildPacManTunnels' DFS network, and an open pocket
        // that merely touches a gap isn't necessarily reachable from the players. Each connector
        // walks to the region already reachable from vehicle 0, carving only the walls crossed.
        Vector2Int seedCell = (vehicleCells != null && vehicleCells.Count > 0)
            ? vehicleCells[0]
            : new Vector2Int(w / 2, h / 2);
        var reachable = FloodOpenInterior(seedCell, w, h);
        if (vehicleCells != null)
            for (int i = 1; i < vehicleCells.Count; i++)
                if (!reachable.Contains(vehicleCells[i]) &&
                    CarveConnectorToReachable(vehicleCells[i], null, reachable, w, h, passRadiusCells))
                    reachable = FloodOpenInterior(seedCell, w, h);

        var exits = new List<PathExitInfo>(gapGroups.Count);
        foreach (var group in gapGroups)
        {
            if (group == null || group.Count == 0) continue;

            foreach (var gapCell in group)
            {
                CarvePermanentDisk(gapCell, passRadiusCells);
                _permanentClearCells.Add(gapCell);
            }

            if (CarveConnectorToReachable(group[0], group, reachable, w, h, passRadiusCells))
                reachable = FloodOpenInterior(seedCell, w, h);

            var anchor = group[0];
            exits.Add(new PathExitInfo
            {
                AnchorCell = anchor,
                GapCells = group,
                Side = SideOfBorderCell(anchor, w, h),
                PassRadiusCells = passRadiusCells
            });
        }

        _targetSolidCount = _roleDensity.TotalSolidCount();
        _isBootstrappingMaze = false;

        onExitsComputed?.Invoke(exits);
    }

    // Gap cells are never corners (both BuildPorousBorderCells variants keep them off the corner),
    // so checking Top, Left, Right, then falling through to Bottom is unambiguous.
    private static BoundaryWrap.BoundarySide SideOfBorderCell(Vector2Int c, int w, int h)
    {
        if (c.y == h - 1) return BoundaryWrap.BoundarySide.Top;
        if (c.x == 0) return BoundaryWrap.BoundarySide.Left;
        if (c.x == w - 1) return BoundaryWrap.BoundarySide.Right;
        return BoundaryWrap.BoundarySide.Bottom;
    }

    private static readonly Vector2Int[] kCardinalDirs =
    {
        new Vector2Int(1, 0), new Vector2Int(-1, 0), new Vector2Int(0, 1), new Vector2Int(0, -1)
    };

    private static bool IsStrictInterior(Vector2Int c, int w, int h) =>
        c.x > 0 && c.x < w - 1 && c.y > 0 && c.y < h - 1;

    // Open = never grown as maze wall, or carved permanently clear since.
    private bool IsOpenCell(Vector2Int c) =>
        _mazePatternCells == null || !_mazePatternCells.Contains(c) || _permanentClearCells.Contains(c);

    // Every open interior cell 4-connected to the seed. Border cells (walls and gaps) are excluded
    // so an exit gap never counts as "reachable" just because it is open.
    private HashSet<Vector2Int> FloodOpenInterior(Vector2Int seed, int w, int h)
    {
        var reached = new HashSet<Vector2Int>();
        if (!IsStrictInterior(seed, w, h) || !IsOpenCell(seed)) return reached;

        var queue = new Queue<Vector2Int>();
        reached.Add(seed);
        queue.Enqueue(seed);
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            foreach (var d in kCardinalDirs)
            {
                var nb = cur + d;
                if (!IsStrictInterior(nb, w, h) || !IsOpenCell(nb)) continue;
                if (reached.Add(nb)) queue.Enqueue(nb);
            }
        }
        return reached;
    }

    // The porous border's exit gaps are placed independently of where BuildPacManTunnels' DFS
    // corridor network actually reaches the edge (MazeTopologyService bulldozes and re-punches
    // the whole perimeter after the tunnel pattern runs), and a vehicle's hole may sit inside a
    // solid block. Find the cheapest route (0-1 BFS: open cells free, wall cells cost 1) from
    // <paramref name="start"/> to the region reachable from the players, then carve only the wall
    // cells it crosses. The route may leave the border only through <paramref name="startGroup"/>
    // (the exit's own gap cells), so it never trenches along the outer wall or treats a sibling
    // gap cell as "interior". Returns true when anything was carved.
    private bool CarveConnectorToReachable(
        Vector2Int start, IReadOnlyList<Vector2Int> startGroup, HashSet<Vector2Int> reachable,
        int w, int h, int radiusCells)
    {
        if (_mazePatternCells == null || reachable == null || reachable.Count == 0) return false;
        if (reachable.Contains(start)) return false;

        var cost = new Dictionary<Vector2Int, int> { [start] = 0 };
        var predecessor = new Dictionary<Vector2Int, Vector2Int>();
        var deque = new LinkedList<Vector2Int>();
        deque.AddFirst(start);

        Vector2Int? goal = null;
        while (deque.Count > 0)
        {
            var cur = deque.First.Value;
            deque.RemoveFirst();
            if (reachable.Contains(cur)) { goal = cur; break; }

            int curCost = cost[cur];
            foreach (var d in kCardinalDirs)
            {
                var nb = cur + d;
                if (nb.x < 0 || nb.x >= w || nb.y < 0 || nb.y >= h) continue;
                bool inStartGroup = nb == start || (startGroup != null && Contains(startGroup, nb));
                if (!IsStrictInterior(nb, w, h) && !inStartGroup) continue;

                int step = IsOpenCell(nb) ? 0 : 1;
                int nbCost = curCost + step;
                if (cost.TryGetValue(nb, out int known) && known <= nbCost) continue;
                cost[nb] = nbCost;
                predecessor[nb] = cur;
                if (step == 0) deque.AddFirst(nb); else deque.AddLast(nb);
            }
        }

        if (goal == null)
        {
            Debug.LogWarning($"[MAZE] Interstitial: could not connect {start} to the players' region.");
            return false;
        }

        bool carved = false;
        var trace = goal.Value;
        while (trace != start)
        {
            if (!IsOpenCell(trace))
            {
                CarvePermanentDisk(trace, radiusCells);
                _permanentClearCells.Add(trace);
                carved = true;
            }
            trace = predecessor[trace];
        }
        return carved;
    }

    private static bool Contains(IReadOnlyList<Vector2Int> list, Vector2Int c)
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i] == c) return true;
        return false;
    }
}
