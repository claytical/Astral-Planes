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
            {
                CarvePermanentDisk(vehCell, vehicleHoleRadiusCells);
                // The vehicle's carved hole has no guarantee of being reached by
                // BuildPacManTunnels' DFS network — unlike exit gaps below, which always get this
                // same connector walk. Without it, a vehicle spawned inside what would otherwise
                // have been a solid wall block can end up sealed off from every exit by
                // bypass-proof Solid dust. No-ops when the hole already touches open interior.
                CarveConnectorToInterior(vehCell, w, h, passRadiusCells);
            }

        var exits = new List<PathExitInfo>(gapGroups.Count);
        foreach (var group in gapGroups)
        {
            if (group == null || group.Count == 0) continue;

            foreach (var gapCell in group)
            {
                CarvePermanentDisk(gapCell, passRadiusCells);
                _permanentClearCells.Add(gapCell);
            }

            CarveConnectorToInterior(group[0], w, h, passRadiusCells);

            var anchor = group[0];
            exits.Add(new PathExitInfo
            {
                AnchorCell = anchor,
                GapCells = group,
                Side = SideOfBorderCell(anchor, w, h)
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

    // The porous border's exit gaps are placed independently of where BuildPacManTunnels' DFS
    // corridor network actually reaches the edge (MazeTopologyService bulldozes and re-punches
    // the whole perimeter after the tunnel pattern runs). A gap can open onto solid wall with no
    // connected path to the interior at all. Walk inward through wall cells until the tunnel
    // network is reached and carve every step, so a real, navigable route always exists from the
    // gap to the interior. No-ops (0 iterations) when the gap already touches open interior.
    private void CarveConnectorToInterior(Vector2Int gapCell, int w, int h, int radiusCells)
    {
        if (_mazePatternCells == null) return;

        var visited = new HashSet<Vector2Int> { gapCell };
        var predecessor = new Dictionary<Vector2Int, Vector2Int>();
        var queue = new Queue<Vector2Int>();
        queue.Enqueue(gapCell);

        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            foreach (var d in kCardinalDirs)
            {
                var nb = cur + d;
                if (nb.x < 0 || nb.x >= w || nb.y < 0 || nb.y >= h) continue;
                if (!visited.Add(nb)) continue;

                if (!_mazePatternCells.Contains(nb))
                {
                    // Reached open interior — carve every wall cell crossed to get here.
                    var trace = cur;
                    while (trace != gapCell)
                    {
                        CarvePermanentDisk(trace, radiusCells);
                        _permanentClearCells.Add(trace);
                        trace = predecessor[trace];
                    }
                    return;
                }

                predecessor[nb] = cur;
                queue.Enqueue(nb);
            }
        }
    }
}
