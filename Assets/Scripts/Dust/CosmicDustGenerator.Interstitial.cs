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

        var mazeResult = BuildMazeGrowthFromConfig(interstitialPattern, contextAnchor, reserved, out var gapGroups);

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

        const int vehicleHoleRadiusCells = 2;
        if (vehicleCells != null)
            foreach (var vehCell in vehicleCells)
                CarvePermanentDisk(vehCell, vehicleHoleRadiusCells);

        var exits = new List<PathExitInfo>(gapGroups.Count);
        foreach (var group in gapGroups)
        {
            if (group == null || group.Count == 0) continue;

            foreach (var gapCell in group)
            {
                CarvePermanentDisk(gapCell, 1);
                _permanentClearCells.Add(gapCell);
            }

            CarveConnectorToInterior(group[0], w, h);

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

    // Safe because BuildPorousBorderCells' perimeter walk always goes top row -> right column
    // -> left column, so a top-row cell is never also a side-column cell (no corner ambiguity).
    private static BoundaryWrap.BoundarySide SideOfBorderCell(Vector2Int c, int w, int h)
    {
        if (c.y == h - 1) return BoundaryWrap.BoundarySide.Top;
        if (c.x == 0) return BoundaryWrap.BoundarySide.Left;
        return BoundaryWrap.BoundarySide.Right;
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
    private void CarveConnectorToInterior(Vector2Int gapCell, int w, int h)
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
                        CarvePermanentDisk(trace, 1);
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
