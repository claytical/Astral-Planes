using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Random = UnityEngine.Random;

public partial class CosmicDustGenerator
{
    private List<MazeRoleGeoConfig> _roleGeoConfigs;
    private MazePatternType _activePatternType = MazePatternType.FullFill;

    public void ApplyMotifGeoConfig(MotifProfile motif)
    {
        _roleGeoConfigs = motif?.roleGeoConfigs != null && motif.roleGeoConfigs.Count > 0
            ? new List<MazeRoleGeoConfig>(motif.roleGeoConfigs)
            : null;
        _activePatternType = motif?.mazePattern?.patternType ?? MazePatternType.FullFill;
    }

    private bool TryGetGeoConfig(MusicalRole role, out MazeRoleGeoConfig config)
    {
        config = null;
        if (_roleGeoConfigs == null) return false;
        for (int i = 0; i < _roleGeoConfigs.Count; i++)
            if (_roleGeoConfigs[i].role == role) { config = _roleGeoConfigs[i]; return true; }
        return false;
    }

    private MazeGeoFeature ResolveGeoFeature(MusicalRole role, int roleIndex)
    {
        if (TryGetGeoConfig(role, out var cfg)) return cfg.feature;

        return _activePatternType switch
        {
            MazePatternType.RingChokepoints => MazeGeoFeature.Rings,
            MazePatternType.DrunkenStrokes  => MazeGeoFeature.Archipelago,
            MazePatternType.DiagonalLanes   => MazeGeoFeature.Archipelago,
            MazePatternType.ClearBoxes      => roleIndex == 0 ? MazeGeoFeature.Glade : MazeGeoFeature.Continent,
            MazePatternType.Tunnels         => roleIndex == 0 ? MazeGeoFeature.Ridge : MazeGeoFeature.Continent,
            _                               => MazeGeoFeature.Continent,
        };
    }

    private MazeRoleGeoConfig ResolveGeoConfig(MusicalRole role)
    {
        TryGetGeoConfig(role, out var cfg);
        return cfg;
    }

    private void ClearMaze()
    {
        try
        {
            for (int x = 0; x < _gridState.Width; x++)
            {
                for (int y = 0; y < _gridState.Height; y++)
                {
                    var gp = new Vector2Int(x, y);
                    if (TryGetCellGo(gp, out var go) && go != null)
                        RemoveActiveAt(gp, go);
                }
            }
            _permanentClearCells.Clear();
            _heldRegrowCells.Clear();
        }
        finally
        {
        }
        _imprints?.Clear();
        _registry.ClearAllFlags();
        _gridState.AllSolidCount    = 0;
        _targetSolidCount = -1;
        _gridState.RegrowingCount   = 0;
        _mazePatternCells = null;
    }

    private List<(Vector2Int grid, Vector3 world)> BuildMazeGrowthFromConfig(
        MazePatternConfig config,
        Vector2Int starCell,
        HashSet<Vector2Int> reservedCells)
    {
        config?.Validate();
        if (drums == null) return new List<(Vector2Int, Vector3)>();

        int w = drums.GetSpawnGridWidth();
        int h = drums.GetSpawnGridHeight();
        if (w <= 0 || h <= 0) return new List<(Vector2Int, Vector3)>();

        Func<int, List<Vector2Int>> getDirsByRow = row => GetHexDirections(row);
        Func<int, int, bool> isCellAvailable = (x, y) => drums.IsSpawnCellAvailable(x, y);
        var context = new MazeTopologyService.Context
        {
            Width = w,
            Height = h,
            StarCell = starCell,
            GetHexDirectionsByRow = getDirsByRow,
            IsCellAvailable = isCellAvailable,
            IsBlocked = cell =>
            {
                if ((uint)cell.x >= (uint)w || (uint)cell.y >= (uint)h) return true;
                if (cell == starCell) return true;
                if (!drums.IsSpawnCellAvailable(cell.x, cell.y)) return true;
                if (reservedCells != null && reservedCells.Contains(cell)) return true;
                if (_permanentClearCells != null && _permanentClearCells.Contains(cell)) return true;
                if (IsKeepClearCell(cell)) return true;
                return false;
            },
            NormalizeCell = toroidal ? WrapCell : null
        };

        var solidCells = _mazeTopologyService.BuildSolidCells(config, context);
        var growth = new List<(Vector2Int cell, Vector3 world)>(solidCells.Count);
        foreach (var gp in solidCells)
        {
            var world = drums.GridToWorldPosition(gp);
            if (!IsWorldPositionInsideScreen(world)) continue;
            growth.Add((gp, world));
        }

        var filtered = new List<(Vector2Int, Vector3)>(growth.Count);
        for (int i = 0; i < growth.Count; i++)
        {
            var gp = growth[i].cell;
            if (reservedCells != null && reservedCells.Contains(gp)) continue;
            if (_permanentClearCells != null && _permanentClearCells.Contains(gp)) continue;
            if (IsKeepClearCell(gp)) continue;
            filtered.Add((growth[i].cell, growth[i].world));
        }

        return filtered;
    }

    private void BuildMazeRoleImprints(
        Vector2Int starCell,
        List<(Vector2Int cell, Vector3 world)> cells)
    {
        if (cells == null || cells.Count == 0) return;
        if (drums == null) return;

        _imprints.EnsureAllocated(cells.Count * 2);

        IReadOnlyList<MusicalRole> roles = (_roleDensity.ActiveRoles != null && _roleDensity.ActiveRoles.Count > 0)
            ? _roleDensity.ActiveRoles
            : new List<MusicalRole>
            {
                MusicalRole.Bass,
                MusicalRole.Harmony,
                MusicalRole.Lead,
                MusicalRole.Groove,
                MusicalRole.Rhythm
            };

        var rolesList = roles is List<MusicalRole> rl ? rl : new List<MusicalRole>(roles);
        if (rolesList.Count == 0)
            return;

        foreach (var (gp, _) in cells)
        {
            _imprints[gp] = new DustImprint
            {
                role               = MusicalRole.None,
                color              = config.mazeTint,
                carveResistance01  = 0f,
                drainResistance01  = 0f,
                maxEnergyUnits     = 1,
                healDelay          = 0f
            };
        }

        var occupied = BuildOccupiedCells(cells);

        if (rolesList.Count == 1)
        {
            MusicalRole onlyRole = rolesList[0];
            for (int i = 0; i < occupied.Count; i++)
                _imprints.SetHiddenRole(occupied[i], onlyRole);

            if (GameFlowManager.VerboseLogging) Debug.Log($"[MAZE] BuildMazeRoleImprints: gray start, cells={cells.Count}, hidden single role={onlyRole}");
            return;
        }

        var (seedCells, seedRoles, actualSeedCount) = SelectRoleSeedsWithGeo(occupied, rolesList, starCell);

        AssignHiddenImprintsByNearestSeed(occupied, seedCells, seedRoles, actualSeedCount);
        PostProcessGeoImprints(occupied, seedCells, seedRoles, actualSeedCount, rolesList);

        var counts = new Dictionary<MusicalRole, int>();
        for (int i = 0; i < actualSeedCount; i++)
            counts[seedRoles[i]] = 0;

        int hiddenCount = 0;
        foreach (var kv in _imprints)
        {
            if (kv.Value.hiddenRole == MusicalRole.None) continue;
            hiddenCount++;
            if (!counts.ContainsKey(kv.Value.hiddenRole))
                counts[kv.Value.hiddenRole] = 0;
            counts[kv.Value.hiddenRole]++;
        }

        string summary = string.Join(", ", counts.Select(kv => $"{kv.Key}={kv.Value}"));
        if (GameFlowManager.VerboseLogging) Debug.Log($"[MAZE] BuildMazeRoleImprints: gray start, cells={cells.Count}, hidden Voronoi roles={hiddenCount}, seeds={actualSeedCount}, distribution=({summary})");
    }

    private static List<Vector2Int> BuildOccupiedCells(List<(Vector2Int cell, Vector3 world)> cells)
    {
        var occupied = new List<Vector2Int>(cells.Count);
        for (int i = 0; i < cells.Count; i++)
            occupied.Add(cells[i].cell);
        return occupied;
    }

    private void AssignHiddenImprintsByNearestSeed(
        List<Vector2Int> occupied,
        Vector2Int[] seedCells,
        MusicalRole[] seedRoles,
        int actualSeedCount)
    {
        for (int i = 0; i < occupied.Count; i++)
        {
            Vector2Int gp = occupied[i];

            float best = float.MaxValue;
            int bestSeed = 0;

            for (int s = 0; s < actualSeedCount; s++)
            {
                float d = (gp - seedCells[s]).sqrMagnitude;
                if (d < best)
                {
                    best = d;
                    bestSeed = s;
                }
            }

            _imprints.SetHiddenRole(gp, seedRoles[bestSeed]);
        }
    }

    // Geo-aware seed selection. Returns an expanded seed list where Archipelago roles appear
    // seedCount times, Ridge roles appear twice, and Ring roles get radius-based positions.
    private (Vector2Int[] seedCells, MusicalRole[] seedRoles, int actualSeedCount) SelectRoleSeedsWithGeo(
        List<Vector2Int> occupied,
        List<MusicalRole> rolesList,
        Vector2Int starCell)
    {
        if (occupied.Count == 0) return (System.Array.Empty<Vector2Int>(), System.Array.Empty<MusicalRole>(), 0);

        int minX = occupied[0].x, maxX = occupied[0].x;
        int minY = occupied[0].y, maxY = occupied[0].y;
        for (int i = 1; i < occupied.Count; i++)
        {
            if (occupied[i].x < minX) minX = occupied[i].x;
            if (occupied[i].x > maxX) maxX = occupied[i].x;
            if (occupied[i].y < minY) minY = occupied[i].y;
            if (occupied[i].y > maxY) maxY = occupied[i].y;
        }

        float maxDistFromStar = 0f;
        for (int i = 0; i < occupied.Count; i++)
        {
            float d = (occupied[i] - starCell).sqrMagnitude;
            if (d > maxDistFromStar) maxDistFromStar = d;
        }
        maxDistFromStar = Mathf.Sqrt(maxDistFromStar);

        var slotRoles = new List<MusicalRole>();
        for (int ri = 0; ri < rolesList.Count; ri++)
        {
            MusicalRole role = rolesList[ri];
            MazeGeoFeature feature = ResolveGeoFeature(role, ri);
            MazeRoleGeoConfig cfg = ResolveGeoConfig(role);

            switch (feature)
            {
                case MazeGeoFeature.Archipelago:
                    int n = cfg != null ? Mathf.Clamp(cfg.seedCount, 2, 8) : 3;
                    for (int k = 0; k < n; k++) slotRoles.Add(role);
                    break;
                case MazeGeoFeature.Ridge:
                    slotRoles.Add(role);
                    slotRoles.Add(role);
                    break;
                default:
                    slotRoles.Add(role);
                    break;
            }
        }

        int totalSlots = Mathf.Min(slotRoles.Count, occupied.Count);
        var seedCells = new Vector2Int[totalSlots];
        var seedRoles = new MusicalRole[totalSlots];
        var chosen = new HashSet<Vector2Int>();

        var ringRoles = new List<MusicalRole>();
        for (int ri = 0; ri < rolesList.Count; ri++)
            if (ResolveGeoFeature(rolesList[ri], ri) == MazeGeoFeature.Rings)
                ringRoles.Add(rolesList[ri]);

        int filled = 0;
        if (ringRoles.Count > 0 && maxDistFromStar > 0f)
        {
            for (int k = 0; k < ringRoles.Count && filled < totalSlots; k++)
            {
                float targetRadius = (k + 0.5f) * (maxDistFromStar / ringRoles.Count);
                int bestIdx = -1;
                float bestDelta = float.MaxValue;
                for (int i = 0; i < occupied.Count; i++)
                {
                    if (chosen.Contains(occupied[i])) continue;
                    float dist = Mathf.Sqrt((occupied[i] - starCell).sqrMagnitude);
                    float delta = Mathf.Abs(dist - targetRadius);
                    if (delta < bestDelta) { bestDelta = delta; bestIdx = i; }
                }
                if (bestIdx < 0) continue;
                seedCells[filled] = occupied[bestIdx];
                seedRoles[filled] = ringRoles[k];
                chosen.Add(occupied[bestIdx]);
                filled++;
            }
        }

        bool widerX = (maxX - minX) >= (maxY - minY);
        for (int ri = 0; ri < rolesList.Count && filled < totalSlots; ri++)
        {
            if (ResolveGeoFeature(rolesList[ri], ri) != MazeGeoFeature.Ridge) continue;
            for (int pass = 0; pass < 2 && filled < totalSlots; pass++)
            {
                int bestIdx2 = -1;
                float bestVal = pass == 0 ? float.MinValue : float.MaxValue;
                for (int i = 0; i < occupied.Count; i++)
                {
                    if (chosen.Contains(occupied[i])) continue;
                    float v = widerX ? occupied[i].x : occupied[i].y;
                    if (pass == 0 && v > bestVal) { bestVal = v; bestIdx2 = i; }
                    if (pass == 1 && v < bestVal) { bestVal = v; bestIdx2 = i; }
                }
                if (bestIdx2 < 0) continue;
                seedCells[filled] = occupied[bestIdx2];
                seedRoles[filled] = rolesList[ri];
                chosen.Add(occupied[bestIdx2]);
                filled++;
            }
        }

        for (int s = 0; s < totalSlots && filled < totalSlots; s++)
        {
            MusicalRole slotRole = slotRoles[s];
            bool alreadyFilledBySpecialPass = false;
            int specialCount = 0;
            for (int f = 0; f < filled; f++)
                if (seedRoles[f] == slotRole) specialCount++;
            int slotsBeforeS = 0;
            for (int k = 0; k < s; k++)
                if (slotRoles[k] == slotRole) slotsBeforeS++;
            if (slotsBeforeS < specialCount) { alreadyFilledBySpecialPass = true; }
            if (alreadyFilledBySpecialPass) continue;

            int bestIdx3 = -1;
            float bestMinDist = float.MinValue;

            for (int i = 0; i < occupied.Count; i++)
            {
                Vector2Int candidate = occupied[i];
                if (chosen.Contains(candidate)) continue;

                float minDistToChosen;
                if (chosen.Count == 0)
                {
                    minDistToChosen = (candidate - starCell).sqrMagnitude;
                }
                else
                {
                    minDistToChosen = float.MaxValue;
                    foreach (var c in chosen)
                    {
                        float d = (candidate - c).sqrMagnitude;
                        if (d < minDistToChosen) minDistToChosen = d;
                    }
                }

                if (minDistToChosen > bestMinDist) { bestMinDist = minDistToChosen; bestIdx3 = i; }
            }

            if (bestIdx3 < 0) break;
            seedCells[filled] = occupied[bestIdx3];
            seedRoles[filled] = slotRole;
            chosen.Add(occupied[bestIdx3]);
            filled++;
        }

        string geoSummary = string.Join(", ", System.Linq.Enumerable.Range(0, rolesList.Count)
            .Select(ri => $"{rolesList[ri]}={ResolveGeoFeature(rolesList[ri], ri)}"));
        if (GameFlowManager.VerboseLogging) Debug.Log($"[MAZE] SelectRoleSeedsWithGeo: pattern={_activePatternType}, seeds={filled}, geoFeatures=({geoSummary})");

        return (seedCells, seedRoles, filled);
    }

    private void PostProcessGeoImprints(
        List<Vector2Int> occupied,
        Vector2Int[] seedCells,
        MusicalRole[] seedRoles,
        int seedCount,
        List<MusicalRole> rolesList)
    {
        for (int ri = 0; ri < rolesList.Count; ri++)
        {
            MusicalRole role = rolesList[ri];
            MazeGeoFeature feature = ResolveGeoFeature(role, ri);
            MazeRoleGeoConfig cfg = ResolveGeoConfig(role);

            if (feature == MazeGeoFeature.Island || feature == MazeGeoFeature.Archipelago)
            {
                float radius = cfg != null ? cfg.radiusCells : 6f;
                float radiusSq = radius * radius;

                for (int i = 0; i < occupied.Count; i++)
                {
                    Vector2Int gp = occupied[i];
                    if (!_imprints.TryGetValue(gp, out var geoImp) || geoImp.hiddenRole != role)
                        continue;

                    float minOwnSeedDistSq = float.MaxValue;
                    for (int s = 0; s < seedCount; s++)
                        if (seedRoles[s] == role)
                        {
                            float d = (gp - seedCells[s]).sqrMagnitude;
                            if (d < minOwnSeedDistSq) minOwnSeedDistSq = d;
                        }

                    if (minOwnSeedDistSq <= radiusSq) continue;

                    float bestOtherDist = float.MaxValue;
                    int bestOtherSeed = -1;
                    for (int s = 0; s < seedCount; s++)
                    {
                        if (seedRoles[s] == role) continue;
                        float d = (gp - seedCells[s]).sqrMagnitude;
                        if (d < bestOtherDist) { bestOtherDist = d; bestOtherSeed = s; }
                    }
                    if (bestOtherSeed >= 0)
                        _imprints.SetHiddenRole(gp, seedRoles[bestOtherSeed]);
                }
            }
            else if (feature == MazeGeoFeature.Glade)
            {
                MusicalRole softRole = role;
                float softestResistance = float.MaxValue;
                for (int ri2 = 0; ri2 < rolesList.Count; ri2++)
                {
                    if (rolesList[ri2] == role) continue;
                    var prof = MusicalRoleProfileLibrary.GetProfile(rolesList[ri2]);
                    float r = prof != null ? prof.carveResistance01 : 0.5f;
                    if (r < softestResistance) { softestResistance = r; softRole = rolesList[ri2]; }
                }

                var territory = new List<Vector2Int>();
                for (int i = 0; i < occupied.Count; i++)
                    if (_imprints.TryGetValue(occupied[i], out var gladeImp) && gladeImp.hiddenRole == role)
                        territory.Add(occupied[i]);

                if (territory.Count == 0) continue;

                int gladeCount2 = cfg != null ? cfg.gladeCount : 2;
                float gladeRadius2 = cfg != null ? cfg.gladeRadius : 3f;
                float gladeRadiusSq = gladeRadius2 * gladeRadius2;

                for (int g = 0; g < gladeCount2; g++)
                {
                    Vector2Int center = territory[UnityEngine.Random.Range(0, territory.Count)];

                    for (int i = 0; i < territory.Count; i++)
                    {
                        if ((territory[i] - center).sqrMagnitude <= gladeRadiusSq)
                            _imprints.SetHiddenRole(territory[i], softRole);
                    }
                }
            }
        }
    }

    public void ResetMazeGenerationFlag()
    {
        _mazeAlreadyGenerated = false;
    }

    public IEnumerator GenerateMazeForPhaseWithPaths(Vector2Int starCell, IReadOnlyList<Vector2Int> vehicleCells, float totalSpawnDuration = 1.0f, System.Action<List<(Vector2Int, Vector3)>> onBeforeGrowth = null)
    {
        _isBootstrappingMaze = true;
        if (_mazeAlreadyGenerated)
        {
            if (GameFlowManager.VerboseLogging) Debug.Log($"[MAZE] Maze is already generated, skipping.");
            yield break;
        }

        _mazeAlreadyGenerated = true;
        if (drums == null)
        {
            Debug.LogError("[MAZE] No DrumTrack available; cannot build maze.");
            _isBootstrappingMaze = false;
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
        if (GameFlowManager.VerboseLogging) Debug.Log($"[MAZE] Maze grid size: {w}x{h}");

        _activeMazePattern = phaseTransitionManager?.currentMotif?.mazePattern;
        _runtimeVoidOnlyDustCreation = false;
        var reserved = new HashSet<Vector2Int> { starCell };

        var cellsToFill = new List<(Vector2Int grid, Vector3 world)>();
        onBeforeGrowth?.Invoke(cellsToFill);
        int preinjectCount = cellsToFill.Count;
        for (int pi = 0; pi < preinjectCount; pi++)
            reserved.Add(cellsToFill[pi].grid);

        _permanentClearCells.Add(starCell);
        const int startupVehicleReserveRadiusCells = 2;
        if (vehicleCells != null) {
            for (int i = 0; i < vehicleCells.Count; i++) {
                var v = vehicleCells[i];
                for (int dx = -startupVehicleReserveRadiusCells; dx <= startupVehicleReserveRadiusCells; dx++) {
                    for (int dy = -startupVehicleReserveRadiusCells; dy <= startupVehicleReserveRadiusCells; dy++) {
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

        var mazeResult = BuildMazeGrowthFromConfig(_activeMazePattern, starCell, reserved);
        if (GameFlowManager.VerboseLogging) Debug.Log($"[MAZE] maze cell count={mazeResult.Count} ring cell count={preinjectCount}");

        _mazePatternCells = new HashSet<Vector2Int>(mazeResult.Count);
        foreach (var (cell, _) in mazeResult)
            _mazePatternCells.Add(cell);

        BuildMazeRoleImprints(starCell, mazeResult);

        cellsToFill.AddRange(mazeResult);

        float spawnDuration = Mathf.Clamp(totalSpawnDuration, 0.05f, 3.0f);
        if (GameFlowManager.VerboseLogging) Debug.Log($"[MAZE] StaggeredGrowthFitDuration with spawnDuration={spawnDuration}");
        if (_spawnRoutine != null) {
            StopCoroutine(_spawnRoutine);
            _spawnRoutine = null;
        }
        for (int s = cellsToFill.Count - 1; s > 0; s--)
        {
            int r = Random.Range(0, s + 1);
            var tmp = cellsToFill[s];
            cellsToFill[s] = cellsToFill[r];
            cellsToFill[r] = tmp;
        }
        _spawnRoutine = StartCoroutine(StaggeredGrowthFitDuration(cellsToFill, spawnDuration));
        yield return _spawnRoutine;
        EnterRuntimeVoidOnlyDustCreationMode();
        _spawnRoutine = null;

        const int starHoleRadiusCells    = 3;
        const int vehicleHoleRadiusCells = 2;

        CarvePermanentDisk(starCell, starHoleRadiusCells);
        if (vehicleCells != null)
        {
            foreach (var vehCell in vehicleCells)
                CarvePermanentDisk(vehCell, vehicleHoleRadiusCells);
        }
        _targetSolidCount = _roleDensity.TotalSolidCount();
        _isBootstrappingMaze = false;
    }

}
