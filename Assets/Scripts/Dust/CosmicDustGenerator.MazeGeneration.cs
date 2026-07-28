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

    // Geo shape (Archipelago/Ridge/etc.) is authored per-ROLE, not per-voice — two voices of the
    // same role (e.g. two Bass variants) share the same shape and roleIndex, but get independently
    // seeded/evicted territories within it (see SelectRoleSeedsWithGeo/PostProcessGeoImprints).
    // This maps each voice's role to its first-appearance index among the distinct roles in
    // voicesList, so existing single-voice-per-role content resolves identically to before.
    private static Dictionary<MusicalRole, int> BuildRoleIndexByRole(List<RoleVoiceKey> voicesList)
    {
        var map = new Dictionary<MusicalRole, int>();
        for (int i = 0; i < voicesList.Count; i++)
        {
            var r = voicesList[i].role;
            if (!map.ContainsKey(r)) map[r] = map.Count;
        }
        return map;
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

        IReadOnlyList<RoleVoiceKey> voices = (_roleDensity.ActiveVoices != null && _roleDensity.ActiveVoices.Count > 0)
            ? _roleDensity.ActiveVoices
            : new List<RoleVoiceKey>
            {
                MusicalRole.Bass,
                MusicalRole.Harmony,
                MusicalRole.Lead,
                MusicalRole.Groove,
                MusicalRole.Rhythm
            };

        var voicesList = voices is List<RoleVoiceKey> vl ? vl : new List<RoleVoiceKey>(voices);
        if (voicesList.Count == 0)
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

        if (voicesList.Count == 1)
        {
            RoleVoiceKey onlyVoice = voicesList[0];
            for (int i = 0; i < occupied.Count; i++)
                _imprints.SetHiddenRole(occupied[i], onlyVoice.role, onlyVoice.voiceIndex);

            if (GameFlowManager.VerboseLogging) Debug.Log($"[MAZE] BuildMazeRoleImprints: gray start, cells={cells.Count}, hidden single voice={onlyVoice}");
            return;
        }

        var (seedCells, seedVoices, actualSeedCount) = SelectRoleSeedsWithGeo(occupied, voicesList, starCell);

        AssignHiddenImprintsByNearestSeed(occupied, seedCells, seedVoices, actualSeedCount);
        PostProcessGeoImprints(occupied, seedCells, seedVoices, actualSeedCount, voicesList);

        var counts = new Dictionary<RoleVoiceKey, int>();
        for (int i = 0; i < actualSeedCount; i++)
            counts[seedVoices[i]] = 0;

        int hiddenCount = 0;
        foreach (var kv in _imprints)
        {
            if (kv.Value.hiddenRole == MusicalRole.None) continue;
            hiddenCount++;
            var key = new RoleVoiceKey(kv.Value.hiddenRole, kv.Value.hiddenVoiceIndex);
            if (!counts.ContainsKey(key))
                counts[key] = 0;
            counts[key]++;
        }

        string summary = string.Join(", ", counts.Select(kv => $"{kv.Key}={kv.Value}"));
        if (GameFlowManager.VerboseLogging) Debug.Log($"[MAZE] BuildMazeRoleImprints: gray start, cells={cells.Count}, hidden Voronoi voices={hiddenCount}, seeds={actualSeedCount}, distribution=({summary})");
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
        RoleVoiceKey[] seedVoices,
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

            _imprints.SetHiddenRole(gp, seedVoices[bestSeed].role, seedVoices[bestSeed].voiceIndex);
        }
    }

    // Geo-aware seed selection. Returns an expanded seed list where Archipelago voices appear
    // seedCount times, Ridge voices appear twice, and Ring voices get radius-based positions.
    private (Vector2Int[] seedCells, RoleVoiceKey[] seedVoices, int actualSeedCount) SelectRoleSeedsWithGeo(
        List<Vector2Int> occupied,
        List<RoleVoiceKey> voicesList,
        Vector2Int starCell)
    {
        if (occupied.Count == 0) return (System.Array.Empty<Vector2Int>(), System.Array.Empty<RoleVoiceKey>(), 0);

        var roleIndexByRole = BuildRoleIndexByRole(voicesList);

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

        // Desired slot count per voice, then interleaved round-robin (not appended as sequential
        // per-voice blocks) so that if occupied.Count ever truncates the list below, every active
        // voice loses slots evenly instead of the last voice in voicesList order losing all of its own.
        var desiredCounts = new int[voicesList.Count];
        for (int vi = 0; vi < voicesList.Count; vi++)
        {
            var voice = voicesList[vi];
            int roleIdx = roleIndexByRole[voice.role];
            MazeGeoFeature feature = ResolveGeoFeature(voice.role, roleIdx);
            MazeRoleGeoConfig cfg = ResolveGeoConfig(voice.role);
            desiredCounts[vi] = feature switch
            {
                MazeGeoFeature.Archipelago => cfg != null ? Mathf.Clamp(cfg.seedCount, 2, 8) : 3,
                MazeGeoFeature.Ridge => 2,
                _ => 1,
            };
        }
        int maxCount = desiredCounts.Length > 0 ? desiredCounts.Max() : 0;
        var slotVoices = new List<RoleVoiceKey>();
        for (int round = 0; round < maxCount; round++)
            for (int vi = 0; vi < voicesList.Count; vi++)
                if (round < desiredCounts[vi]) slotVoices.Add(voicesList[vi]);

        int totalSlots = Mathf.Min(slotVoices.Count, occupied.Count);
        var seedCells = new Vector2Int[totalSlots];
        var seedVoices = new RoleVoiceKey[totalSlots];
        var chosen = new HashSet<Vector2Int>();

        var ringVoices = new List<RoleVoiceKey>();
        for (int vi = 0; vi < voicesList.Count; vi++)
            if (ResolveGeoFeature(voicesList[vi].role, roleIndexByRole[voicesList[vi].role]) == MazeGeoFeature.Rings)
                ringVoices.Add(voicesList[vi]);

        int filled = 0;
        if (ringVoices.Count > 0 && maxDistFromStar > 0f)
        {
            for (int k = 0; k < ringVoices.Count && filled < totalSlots; k++)
            {
                float targetRadius = (k + 0.5f) * (maxDistFromStar / ringVoices.Count);
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
                seedVoices[filled] = ringVoices[k];
                chosen.Add(occupied[bestIdx]);
                filled++;
            }
        }

        bool widerX = (maxX - minX) >= (maxY - minY);
        for (int vi = 0; vi < voicesList.Count && filled < totalSlots; vi++)
        {
            if (ResolveGeoFeature(voicesList[vi].role, roleIndexByRole[voicesList[vi].role]) != MazeGeoFeature.Ridge) continue;
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
                seedVoices[filled] = voicesList[vi];
                chosen.Add(occupied[bestIdx2]);
                filled++;
            }
        }

        for (int s = 0; s < totalSlots && filled < totalSlots; s++)
        {
            RoleVoiceKey slotVoice = slotVoices[s];
            bool alreadyFilledBySpecialPass = false;
            int specialCount = 0;
            for (int f = 0; f < filled; f++)
                if (seedVoices[f].Equals(slotVoice)) specialCount++;
            int slotsBeforeS = 0;
            for (int k = 0; k < s; k++)
                if (slotVoices[k].Equals(slotVoice)) slotsBeforeS++;
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
            seedVoices[filled] = slotVoice;
            chosen.Add(occupied[bestIdx3]);
            filled++;
        }

        string geoSummary = string.Join(", ", System.Linq.Enumerable.Range(0, voicesList.Count)
            .Select(vi => $"{voicesList[vi]}={ResolveGeoFeature(voicesList[vi].role, roleIndexByRole[voicesList[vi].role])}"));
        if (GameFlowManager.VerboseLogging) Debug.Log($"[MAZE] SelectRoleSeedsWithGeo: pattern={_activePatternType}, seeds={filled}, geoFeatures=({geoSummary})");

        return (seedCells, seedVoices, filled);
    }

    private void PostProcessGeoImprints(
        List<Vector2Int> occupied,
        Vector2Int[] seedCells,
        RoleVoiceKey[] seedVoices,
        int seedCount,
        List<RoleVoiceKey> voicesList)
    {
        // Every voice's eviction/glade decision below is computed by reading _imprints, but the
        // decision is only ever applied to cells whose ORIGINAL hidden voice equals that voice's
        // own. So no two voices' decisions can ever target the same cell — but if writes were
        // applied immediately, voices processed later in voicesList order would see earlier
        // voices' evictions and could re-evict cells that were just handed to them, leaving the
        // LAST voice in voicesList systematically starved of territory. Deferring every write
        // until all voices have been evaluated against the same pre-loop snapshot removes that
        // order dependency.
        var pendingReassignments = new List<(Vector2Int cell, RoleVoiceKey newVoice)>();
        var roleIndexByRole = BuildRoleIndexByRole(voicesList);

        for (int vi = 0; vi < voicesList.Count; vi++)
        {
            RoleVoiceKey voice = voicesList[vi];
            MazeGeoFeature feature = ResolveGeoFeature(voice.role, roleIndexByRole[voice.role]);
            MazeRoleGeoConfig cfg = ResolveGeoConfig(voice.role);

            if (feature == MazeGeoFeature.Island || feature == MazeGeoFeature.Archipelago)
            {
                float radius = cfg != null ? cfg.radiusCells : 6f;
                float radiusSq = radius * radius;

                for (int i = 0; i < occupied.Count; i++)
                {
                    Vector2Int gp = occupied[i];
                    if (!_imprints.TryGetValue(gp, out var geoImp)) continue;
                    var geoVoice = new RoleVoiceKey(geoImp.hiddenRole, geoImp.hiddenVoiceIndex);
                    if (!geoVoice.Equals(voice)) continue;

                    float minOwnSeedDistSq = float.MaxValue;
                    for (int s = 0; s < seedCount; s++)
                        if (seedVoices[s].Equals(voice))
                        {
                            float d = (gp - seedCells[s]).sqrMagnitude;
                            if (d < minOwnSeedDistSq) minOwnSeedDistSq = d;
                        }

                    if (minOwnSeedDistSq <= radiusSq) continue;

                    float bestOtherDist = float.MaxValue;
                    int bestOtherSeed = -1;
                    for (int s = 0; s < seedCount; s++)
                    {
                        if (seedVoices[s].Equals(voice)) continue;
                        float d = (gp - seedCells[s]).sqrMagnitude;
                        if (d < bestOtherDist) { bestOtherDist = d; bestOtherSeed = s; }
                    }
                    if (bestOtherSeed >= 0)
                        pendingReassignments.Add((gp, seedVoices[bestOtherSeed]));
                }
            }
            else if (feature == MazeGeoFeature.Glade)
            {
                RoleVoiceKey softVoice = voice;
                float softestResistance = float.MaxValue;
                for (int vi2 = 0; vi2 < voicesList.Count; vi2++)
                {
                    if (voicesList[vi2].Equals(voice)) continue;
                    var prof = _roleDensity.GetResolvedRoleProfile(voicesList[vi2]);
                    float r = prof != null ? prof.carveResistance01 : 0.5f;
                    if (r < softestResistance) { softestResistance = r; softVoice = voicesList[vi2]; }
                }

                var territory = new List<Vector2Int>();
                for (int i = 0; i < occupied.Count; i++)
                    if (_imprints.TryGetValue(occupied[i], out var gladeImp)
                        && gladeImp.hiddenRole == voice.role && gladeImp.hiddenVoiceIndex == voice.voiceIndex)
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
                            pendingReassignments.Add((territory[i], softVoice));
                    }
                }
            }
        }

        foreach (var (cell, newVoice) in pendingReassignments)
            _imprints.SetHiddenRole(cell, newVoice.role, newVoice.voiceIndex);
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
