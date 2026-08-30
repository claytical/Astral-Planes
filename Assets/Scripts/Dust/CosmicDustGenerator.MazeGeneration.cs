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
    private DustMotifDensitySnapshot _densitySnapshot;
    private float _genericFillNeverRegrowRatio = 1f;
    private float _genericFillRatio01 = 0f;
    private float _genericFillRegrowSoftenMultiplier01 = 1f;

    public void ApplyMotifGeoConfig(MotifProfile motif, IReadOnlyList<InstrumentTrack> tracks = null)
    {
        _roleGeoConfigs = motif?.roleGeoConfigs != null && motif.roleGeoConfigs.Count > 0
            ? new List<MazeRoleGeoConfig>(motif.roleGeoConfigs)
            : null;
        _activePatternType = motif?.mazePattern?.patternType ?? MazePatternType.FullFill;
        _genericFillNeverRegrowRatio = motif != null ? Mathf.Clamp01(motif.genericFillNeverRegrowRatio) : 1f;
        _genericFillRatio01 = motif != null ? Mathf.Clamp01(motif.genericFillRatio01) : 0f;
        _genericFillRegrowSoftenMultiplier01 = motif != null ? Mathf.Clamp01(motif.genericFillRegrowSoftenMultiplier01) : 1f;
        _densitySnapshot = MotifNoteDensityAnalyzer.Analyze(
            motif,
            tracks,
            config != null ? config.minNotesPerBinPerVoice : 2f,
            config != null ? config.maxNotesPerBinPerVoice : 16f,
            config != null ? config.minBpmForDensity : 80f,
            config != null ? config.maxBpmForDensity : 160f,
            config != null ? config.noteDensityWeight : 1f,
            config != null ? config.bpmDensityWeight : 0f);
    }

    private float GetWeightNorm01ByVoice(RoleVoiceKey voice) =>
        _densitySnapshot?.GetWeightNorm01ByVoice(voice) ?? 0.5f;

    private float GetWeightNorm01ByRole(MusicalRole role) =>
        _densitySnapshot?.GetWeightNorm01ByRole(role) ?? 0.5f;

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
            MazePatternType.ClearBoxes      => roleIndex == 0 ? MazeGeoFeature.Glade : DensityRankFeature(role),
            MazePatternType.Tunnels         => roleIndex == 0 ? MazeGeoFeature.Ridge : DensityRankFeature(role),
            _                               => DensityRankFeature(role),
        };
    }

    private const float DensityFeatureBandHigh = 0.66f;
    private const float DensityFeatureBandLow  = 0.33f;

    // Replaces the old flat "always Continent" catch-all: a role's relative note density (see
    // MotifNoteDensityAnalyzer) picks between a sprawling single territory (busy), a few
    // scattered footholds (typical), or one compact cluster (sparse).
    private MazeGeoFeature DensityRankFeature(MusicalRole role)
    {
        float w = GetWeightNorm01ByRole(role);
        if (w >= DensityFeatureBandHigh) return MazeGeoFeature.Continent;
        if (w <= DensityFeatureBandLow) return MazeGeoFeature.Island;
        return MazeGeoFeature.Archipelago;
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
        // Own the "grid is now truly empty" guarantee here rather than relying on callers to
        // have run HardStopRegrowthForBridge first — a coroutine left running past this point
        // (regrow timer, void-grow, commit-regrow) would otherwise materialize a stray cell in
        // the next motif at old coordinates. Safe to call even if HardStopRegrowthForBridge
        // already ran (idempotent: dictionaries already empty, StopCoroutine(null) no-ops).
        CancelAllInFlightDustCoroutines();

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
        _carveAccumulator.Clear();

        if (GameFlowManager.VerboseLogging)
            Debug.Assert(_regrowthScheduler.VoidGrowCoroutines.Count == 0 && _regrowthScheduler.CommitRegrowCoroutines.Count == 0,
                "[MAZE] ClearMaze: scheduler coroutine dictionaries not empty after cancellation.");
    }

    private List<(Vector2Int grid, Vector3 world)> BuildMazeGrowthFromConfig(
        MazePatternConfig config,
        Vector2Int starCell,
        HashSet<Vector2Int> reservedCells,
        float? busyness01Override = null)
    {
        return BuildMazeGrowthFromConfig(config, starCell, reservedCells, out _, busyness01Override);
    }

    // Same as above, but also returns the porous-border gap cell groups computed in this same
    // pass (see MazeTopologyService.BuildSolidCells's out-param overload).
    // busyness01Override forces a specific Context.Busyness01 instead of reading the current
    // motif's density snapshot — used by GenerateInterstitialMaze (CosmicDustGenerator.Interstitial.cs)
    // so its blind, role-less path-choice tunnels never inherit stale cross-motif density state.
    private List<(Vector2Int grid, Vector3 world)> BuildMazeGrowthFromConfig(
        MazePatternConfig config,
        Vector2Int starCell,
        HashSet<Vector2Int> reservedCells,
        out List<List<Vector2Int>> gapGroupsOut,
        float? busyness01Override = null)
    {
        config?.Validate();
        if (drums == null) { gapGroupsOut = new List<List<Vector2Int>>(); return new List<(Vector2Int, Vector3)>(); }

        int w = drums.GetSpawnGridWidth();
        int h = drums.GetSpawnGridHeight();
        if (w <= 0 || h <= 0) { gapGroupsOut = new List<List<Vector2Int>>(); return new List<(Vector2Int, Vector3)>(); }

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
            NormalizeCell = toroidal ? WrapCell : null,
            Busyness01 = busyness01Override ?? _densitySnapshot?.MotifBusyness01 ?? 0.5f
        };

        var solidCells = _mazeTopologyService.BuildSolidCells(config, context, out gapGroupsOut);
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
                carveResistance01  = config.noneCarveResistance01,
                drainResistance01  = config.noneDrainResistance01,
                maxEnergyUnits     = config.noneMaxEnergyUnits,
                healDelay          = 0f
            };
        }

        var occupied = BuildOccupiedCells(cells);
        if (_genericFillRatio01 > 0f && occupied.Count > 0)
            occupied = ReserveUniversalGenericFill(occupied);

        List<RoleVoiceKey> exactVoices = null;
        for (int i = 0; i < voicesList.Count; i++)
            if (TryGetGeoConfig(voicesList[i].role, out var exactCfg) && exactCfg.useExactNoteCount)
                (exactVoices ??= new List<RoleVoiceKey>()).Add(voicesList[i]);

        if (exactVoices != null && exactVoices.Count > 0)
        {
            ClaimExactCountTerritoryAndFillGeneric(occupied, exactVoices, starCell);
            ApplyHiddenColorPreview(occupied);
            return;
        }

        if (voicesList.Count == 1)
        {
            RoleVoiceKey onlyVoice = voicesList[0];
            for (int i = 0; i < occupied.Count; i++)
                _imprints.SetHiddenRole(occupied[i], onlyVoice.role, onlyVoice.voiceIndex);

            ApplyHiddenColorPreview(occupied);

            if (GameFlowManager.VerboseLogging) Debug.Log($"[MAZE] BuildMazeRoleImprints: gray start, cells={cells.Count}, hidden single voice={onlyVoice}");
            return;
        }

        var (seedCells, seedVoices, actualSeedCount) = SelectRoleSeedsWithGeo(occupied, voicesList, starCell);

        AssignHiddenImprintsByNearestSeed(occupied, seedCells, seedVoices, actualSeedCount);
        PostProcessGeoImprints(occupied, seedCells, seedVoices, actualSeedCount, voicesList);
        ApplyHiddenColorPreview(occupied);

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

    // Exact-count mode: each exact-flagged voice claims exactly round(avgNoteCount * multiplier)
    // cells nearest to a set of greedily-spread seed points, instead of Voronoi-partitioning the
    // whole maze. Every cell left over once all exact voices have claimed their share — including
    // whatever non-exact active roles would otherwise have taken — becomes generic None dust.
    private void ClaimExactCountTerritoryAndFillGeneric(
        List<Vector2Int> occupied,
        List<RoleVoiceKey> exactVoices,
        Vector2Int starCell)
    {
        var unclaimed = new List<Vector2Int>(occupied);
        var counts = new Dictionary<RoleVoiceKey, int>();
        Vector2 gridCenter = ComputeBoundsCenter(occupied);

        foreach (var voice in exactVoices)
        {
            TryGetGeoConfig(voice.role, out var cfg);
            float avgCount = _densitySnapshot?.GetAvgNoteCount(voice) ?? 0f;
            float multiplier = cfg != null ? cfg.notesToCellsMultiplier : 1f;
            int targetCount = Mathf.Clamp(Mathf.RoundToInt(avgCount * multiplier), 0, unclaimed.Count);
            counts[voice] = 0;
            if (targetCount <= 0 || unclaimed.Count == 0) continue;

            int seedCount = (cfg != null && cfg.feature == MazeGeoFeature.Archipelago)
                ? Mathf.Clamp(cfg.seedCount, 1, unclaimed.Count)
                : 1;

            var seedPool = unclaimed;
            float density = occupied.Count > 0 ? (float)targetCount / occupied.Count : 0f;
            if (cfg != null && cfg.centerBiasFraction01 > 0f && density >= cfg.centerBiasMinDensity01)
            {
                var byCenterDist = new List<Vector2Int>(unclaimed);
                byCenterDist.Sort((a, b) =>
                    ((Vector2)a - gridCenter).sqrMagnitude.CompareTo(((Vector2)b - gridCenter).sqrMagnitude));
                int keep = Mathf.Clamp(Mathf.RoundToInt(unclaimed.Count * cfg.centerBiasFraction01), seedCount, unclaimed.Count);
                seedPool = byCenterDist.GetRange(0, keep);
            }

            var seeds = PickFarthestSeeds(seedPool, starCell, seedCount);

            unclaimed.Sort((a, b) => MinSqrDistToAny(a, seeds).CompareTo(MinSqrDistToAny(b, seeds)));

            int claim = Mathf.Min(targetCount, unclaimed.Count);
            for (int i = 0; i < claim; i++)
                _imprints.SetHiddenRole(unclaimed[i], voice.role, voice.voiceIndex);
            counts[voice] = claim;
            unclaimed.RemoveRange(0, claim);
        }

        // Shuffle before splitting so the never-regrow/quick-regrow sub-types interleave
        // across the leftover territory instead of one sub-type clumping wherever it
        // happened to land in claim order.
        for (int s = unclaimed.Count - 1; s > 0; s--)
        {
            int r = Random.Range(0, s + 1);
            (unclaimed[s], unclaimed[r]) = (unclaimed[r], unclaimed[s]);
        }
        int neverRegrowCount = Mathf.Clamp(Mathf.RoundToInt(unclaimed.Count * _genericFillNeverRegrowRatio), 0, unclaimed.Count);
        for (int i = 0; i < unclaimed.Count; i++)
        {
            bool neverRegrow = i < neverRegrowCount;
            _imprints.SetHiddenRoleGenericFill(unclaimed[i], neverRegrow ? config.genericFillTint : config.mazeTint, neverRegrow);
        }

        if (GameFlowManager.VerboseLogging)
        {
            string summary = string.Join(", ", counts.Select(kv => $"{kv.Key}={kv.Value}"));
            Debug.Log($"[MAZE] BuildMazeRoleImprints: exact-count mode, cells={occupied.Count}, claimed=({summary}), generic(None)={unclaimed.Count} (neverRegrow={neverRegrowCount}, quickRegrow={unclaimed.Count - neverRegrowCount})");
        }
    }

    // Universal filler pre-step: runs before role assignment in every mode (default Voronoi,
    // single-voice, exact-count). Uses the same Fisher-Yates-shuffle-then-take-N idiom as the
    // exact-count leftover split (below) rather than a seed+claim scatter (PickFarthestSeeds/
    // SelectRoleSeedsWithGeo), since those produce contiguous blob territories — the wrong shape
    // for isolated stippled filler cells.
    private List<Vector2Int> ReserveUniversalGenericFill(List<Vector2Int> occupied)
    {
        var pool = new List<Vector2Int>(occupied);
        for (int s = pool.Count - 1; s > 0; s--)
        {
            int r = Random.Range(0, s + 1);
            (pool[s], pool[r]) = (pool[r], pool[s]);
        }

        int fillCount = Mathf.Clamp(Mathf.RoundToInt(occupied.Count * _genericFillRatio01), 0, occupied.Count);
        int neverRegrowCount = Mathf.Clamp(Mathf.RoundToInt(fillCount * _genericFillNeverRegrowRatio), 0, fillCount);

        for (int i = 0; i < fillCount; i++)
        {
            bool neverRegrow = i < neverRegrowCount;
            _imprints.SetHiddenRoleGenericFill(pool[i], neverRegrow ? config.genericFillTint : config.mazeTint, neverRegrow);
        }

        var remaining = new List<Vector2Int>(occupied.Count - fillCount);
        for (int i = fillCount; i < pool.Count; i++) remaining.Add(pool[i]);
        return remaining;
    }

    // Greedy farthest-point seeding scoped to an arbitrary cell pool: first seed is farthest from
    // starCell, each subsequent seed maximizes its minimum distance to seeds already chosen.
    // Mirrors the same idiom used by SelectRoleSeedsWithGeo's fallback pass, just pool-scoped.
    private static List<Vector2Int> PickFarthestSeeds(List<Vector2Int> pool, Vector2Int starCell, int seedCount)
    {
        var chosen = new List<Vector2Int>();
        if (pool.Count == 0) return chosen;

        int bestIdx = 0;
        float bestDist = -1f;
        for (int i = 0; i < pool.Count; i++)
        {
            float d = (pool[i] - starCell).sqrMagnitude;
            if (d > bestDist) { bestDist = d; bestIdx = i; }
        }
        chosen.Add(pool[bestIdx]);

        while (chosen.Count < seedCount && chosen.Count < pool.Count)
        {
            int nextIdx = -1;
            float nextBest = -1f;
            for (int i = 0; i < pool.Count; i++)
            {
                var candidate = pool[i];
                if (chosen.Contains(candidate)) continue;
                float minDist = MinSqrDistToAny(candidate, chosen);
                if (minDist > nextBest) { nextBest = minDist; nextIdx = i; }
            }
            if (nextIdx < 0) break;
            chosen.Add(pool[nextIdx]);
        }
        return chosen;
    }

    private static float MinSqrDistToAny(Vector2Int cell, List<Vector2Int> seeds)
    {
        float best = float.MaxValue;
        for (int i = 0; i < seeds.Count; i++)
        {
            float d = (cell - seeds[i]).sqrMagnitude;
            if (d < best) best = d;
        }
        return best;
    }

    private static Vector2 ComputeBoundsCenter(List<Vector2Int> cells)
    {
        if (cells.Count == 0) return Vector2.zero;
        int minX = cells[0].x, maxX = cells[0].x, minY = cells[0].y, maxY = cells[0].y;
        for (int i = 1; i < cells.Count; i++)
        {
            if (cells[i].x < minX) minX = cells[i].x;
            if (cells[i].x > maxX) maxX = cells[i].x;
            if (cells[i].y < minY) minY = cells[i].y;
            if (cells[i].y > maxY) maxY = cells[i].y;
        }
        return new Vector2((minX + maxX) / 2f, (minY + maxY) / 2f);
    }

    // Cosmetic only — leaves imprint.role at MusicalRole.None (and therefore Role/targeting/
    // star-arming) untouched. When config.hideDustColorAtSpawn is false (default), gives each
    // cell a darkened preview of its secret hiddenRole color instead of flat mazeTint, so a
    // motif's dust reads as "roughly what it is" before any carving. Carving still promotes the
    // real Role and full color exactly as today (FinalizeVehicleCarve / PromoteHiddenRole).
    private void ApplyHiddenColorPreview(List<Vector2Int> cells)
    {
        if (config.hideDustColorAtSpawn) return;
        if (cells == null) return;

        float mazeAlpha = config.mazeTint.a;
        float darken = Mathf.Clamp01(config.hiddenRoleColorDarken01);

        for (int i = 0; i < cells.Count; i++)
        {
            var gp = cells[i];
            if (!_imprints.TryGetValue(gp, out var imp) || imp.hiddenRole == MusicalRole.None) continue;

            var profile = _roleDensity.GetResolvedRoleProfile(imp.hiddenRole);
            if (profile == null) continue;

            Color trueColor = profile.GetBaseColor();
            Color preview = Color.Lerp(trueColor, Color.black, darken);
            preview.a = mazeAlpha;

            imp.color = preview;
            _imprints[gp] = imp;
        }
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
        //
        // The seed BUDGET itself is a per-ROLE quantity (seedCount/density banding is authored/
        // computed at role granularity), so a role with multiple ByVoice voices (e.g. a 3-voice
        // chord Rhythm part) must not get that budget applied once per voice — that would give it
        // 3x the territory of a single-voice role for the same authored/density-implied budget.
        // Compute the total once per distinct role, then split it as evenly as possible across
        // that role's own voices. (The dedicated Ridge seeding pass below still grants a flat 2
        // seeds per Ridge voice independent of this array — a similar latent fan-out risk for a
        // multi-voice Ridge role, not fixed here since no current content hits it.)
        var voiceCountByRole = new Dictionary<MusicalRole, int>();
        foreach (var v in voicesList)
            voiceCountByRole[v.role] = voiceCountByRole.TryGetValue(v.role, out var c) ? c + 1 : 1;

        var roleTotals = new Dictionary<MusicalRole, int>();
        var roleVoiceOrdinal = new Dictionary<MusicalRole, int>();
        var desiredCounts = new int[voicesList.Count];
        for (int vi = 0; vi < voicesList.Count; vi++)
        {
            var voice = voicesList[vi];
            int roleIdx = roleIndexByRole[voice.role];

            if (!roleTotals.TryGetValue(voice.role, out int roleTotal))
            {
                MazeGeoFeature feature = ResolveGeoFeature(voice.role, roleIdx);
                MazeRoleGeoConfig cfg = ResolveGeoConfig(voice.role);
                roleTotal = feature switch
                {
                    MazeGeoFeature.Archipelago => cfg != null
                        ? Mathf.Clamp(cfg.seedCount, 2, 8)
                        : Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(2, 8, GetWeightNorm01ByRole(voice.role))), 2, 8),
                    MazeGeoFeature.Ridge => 2,
                    _ => 1,
                };
                roleTotals[voice.role] = roleTotal;
            }

            int voiceCount = voiceCountByRole[voice.role];
            int ordinal = roleVoiceOrdinal.TryGetValue(voice.role, out var o) ? o : 0;
            roleVoiceOrdinal[voice.role] = ordinal + 1;
            desiredCounts[vi] = roleTotal / voiceCount + (ordinal < roleTotal % voiceCount ? 1 : 0);
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

        // Same per-role-budget-then-split rationale as SelectRoleSeedsWithGeo's desiredCounts:
        // a Glade role's clearing count is authored/computed at role granularity, so a multi-voice
        // (ByVoice) role must not give away that many clearings PER voice.
        var voiceCountByRole = new Dictionary<MusicalRole, int>();
        foreach (var v in voicesList)
            voiceCountByRole[v.role] = voiceCountByRole.TryGetValue(v.role, out var c) ? c + 1 : 1;
        var gladeRoleTotals = new Dictionary<MusicalRole, int>();
        var gladeRoleOrdinal = new Dictionary<MusicalRole, int>();

        for (int vi = 0; vi < voicesList.Count; vi++)
        {
            RoleVoiceKey voice = voicesList[vi];
            MazeGeoFeature feature = ResolveGeoFeature(voice.role, roleIndexByRole[voice.role]);
            MazeRoleGeoConfig cfg = ResolveGeoConfig(voice.role);

            if (feature == MazeGeoFeature.Island || feature == MazeGeoFeature.Archipelago)
            {
                float radius = cfg != null ? cfg.radiusCells : Mathf.Lerp(4f, 9f, GetWeightNorm01ByVoice(voice));
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

                // Glade means yielding territory to another role, so — inverted vs. Island/
                // Archipelago's radius growth — a busy/dominant voice yields LESS (fewer, smaller
                // clearings) while a sparse voice yields more.
                if (!gladeRoleTotals.TryGetValue(voice.role, out int gladeRoleTotal))
                {
                    float gladeRoleWeight = GetWeightNorm01ByRole(voice.role);
                    gladeRoleTotal = cfg != null ? cfg.gladeCount : Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(4, 1, gladeRoleWeight)), 1, 4);
                    gladeRoleTotals[voice.role] = gladeRoleTotal;
                }
                int gladeVoiceCount = voiceCountByRole[voice.role];
                int gladeOrdinal = gladeRoleOrdinal.TryGetValue(voice.role, out var go) ? go : 0;
                gladeRoleOrdinal[voice.role] = gladeOrdinal + 1;
                int gladeCount2 = gladeRoleTotal / gladeVoiceCount + (gladeOrdinal < gladeRoleTotal % gladeVoiceCount ? 1 : 0);

                float gladeWeight = GetWeightNorm01ByVoice(voice);
                float gladeRadius2 = cfg != null ? cfg.gladeRadius : Mathf.Lerp(4f, 2f, gladeWeight);
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
