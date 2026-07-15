using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public partial class CosmicDustGenerator
{
    public void OnDustVisualFadedOut(CosmicDust dust)
    {
        if (dust == null) return;

        SetDustCollision(dust, false);
        Vector2Int gp = default;
        bool have = _goToCell.TryGetValue(dust.gameObject, out gp);
        if (!have && drums != null)
        {
            gp = drums.WorldToGridPosition(dust.transform.position);
            have = IsInBounds(gp);
        }

        if (have)
        {
            if (TryGetCellState(gp, out var st) && st == DustCellState.Clearing)
                SetCellState(gp, DustCellState.Empty);
        }
        dust.FinalizeClearedVisuals();
    }

    private GameObject GetOrCreateCellGO(Vector2Int gp)
    {
        EnsureCellGrid();
        if (!IsInBounds(gp)) return null;
        Transform root = activeDustRoot != null ? activeDustRoot : transform;

        if (root != null && !root.gameObject.activeSelf)
            root.gameObject.SetActive(true);

        var existing = _gridState.CellGo[gp.x, gp.y];
        if (existing != null)
        {
            if (!existing.activeSelf)
                existing.SetActive(true);

            return existing;
        }
        if (dustPrefab == null) return null;

        var go = Instantiate(dustPrefab, drums != null ? drums.GridToWorldPosition(gp) : Vector3.zero, Quaternion.identity, root);
        go.name = $"Cosmic Dust ({gp.x},{gp.y})";

        var dust = go.GetComponent<CosmicDust>();
        if (dust != null)
        {
            dust.SetTrackBundle(this, drums);
            dust.SetCellSizeDrivenScale(Mathf.Max(0.001f, drums.GetCellWorldSize()), config.dustFootprintMul, cellClearanceWorld);
            dust.HideVisualsInstant();
            SetDustCollision(dust, false);
        }

        _gridState.CellGo[gp.x, gp.y] = go;
        _gridState.CellDust[gp.x, gp.y] = dust;
        _goToCell[go] = gp;
        SetCellState(gp, DustCellState.Empty);

        return go;
    }

    public void CarveDustByVehicle(Vector2Int cell, float fadeSeconds, ShipMusicalProfile shipProfile = null)
    {
        if (!IsInBounds(cell)) return;
        if (!TryGetCellState(cell, out var st) || st != DustCellState.Solid) return;

        var cellDust = _gridState.CellDust?[cell.x, cell.y];
        if (cellDust != null) SetDustCollision(cellDust, false);

        FinalizeVehicleCarve(cell, fadeSeconds, shipProfile);
    }

    public void ChipDustByVehicle(Vector2Int cell, int energyAmount, float fadeSeconds, float resistanceBypass01 = 0f, ShipMusicalProfile shipProfile = null)
    {
        if (!IsInBounds(cell)) return;
        if (!TryGetCellState(cell, out var st) || st != DustCellState.Solid) return;

        var cellGo = _gridState.CellGo?[cell.x, cell.y];
        if (cellGo == null) return;
        if (!cellGo.TryGetComponent<CosmicDust>(out var dust) || dust == null) return;

        var resistProfile = ResolveResistanceProfile(cell, dust.Role, "ChipByVehicle");
        float effectiveResistance = resistProfile.carveResistance01 * (1f - Mathf.Clamp01(resistanceBypass01));
        float resistMul = 1f - effectiveResistance;

        _carveAccumulator.TryGetValue(cell, out float acc);
        acc += energyAmount * resistMul;
        int effectiveChip = Mathf.FloorToInt(acc);
        _carveAccumulator[cell] = acc - effectiveChip;
        if (effectiveChip <= 0) return;

        int removed = dust.ChipEnergy(effectiveChip);
        if (removed <= 0) return;

        if (dust.currentEnergyUnits <= 0)
        {
            _carveAccumulator.Remove(cell);
            FinalizeVehicleCarve(cell, fadeSeconds, shipProfile);
        }
    }

    private void FinalizeVehicleCarve(Vector2Int cell, float fadeSeconds, ShipMusicalProfile shipProfile = null)
    {
        _imprints ??= new Dictionary<Vector2Int, DustImprint>();
        if (!RestoreVoronoiImprint(cell))
            PromoteHiddenRole(cell);

        SetCellFlag(cell, CellFlags.PlayerCarved);

        bool wasVoidCell = ClearCellFlag(cell, CellFlags.VoidGrow);
        if (wasVoidCell) _imprints?.Remove(cell);

        // The carving ship is a growth agent: its per-role multiplier scales the resolved
        // delay, so a Bass-accelerator ship makes Bass dust regrow sooner. Resolved after
        // the role promotion above so the multiplier keys off the role the cell regrows as.
        float delay = ResolveRegrowDelay(cell, DustClearSource.VehiclePlow, -1f);
        if (shipProfile != null && shipProfile.regrowDelayMultipliers != null)
            delay *= shipProfile.regrowDelayMultipliers.GetFor(GetProspectiveRegrowRole(cell));

        CarveCell(cell, fadeSeconds, scheduleRegrow: true, source: DustClearSource.VehiclePlow, regrowDelaySeconds: delay, runPreExplode: true);
    }

    /// <summary>
    /// Regrow-delay precedence:
    /// 1. Explicit per-call override (>= 0).
    /// 2. VehiclePlow only: the carved cell's role profile regrowthDelay (>= 0).
    /// 3. Active maze pattern's per-source delay (>= 0).
    /// 4. Active maze pattern's base regrowDelay.
    /// 5. Hard fallback 8s (no active pattern).
    /// </summary>
    private float ResolveRegrowDelay(Vector2Int cell, DustClearSource source, float explicitOverride)
    {
        if (explicitOverride >= 0f)
            return explicitOverride;

        if (source == DustClearSource.VehiclePlow &&
            _imprints != null && _imprints.TryGetValue(cell, out var imp) && imp.hiddenRole != MusicalRole.None)
        {
            var roleProfile = MusicalRoleProfileLibrary.GetProfile(imp.hiddenRole);
            if (roleProfile != null && roleProfile.regrowthDelay >= 0f)
                return roleProfile.regrowthDelay;
        }

        var timing = _activeMazePattern != null ? _activeMazePattern.dustTiming : null;
        if (timing == null)
            return 8f;

        float perSource = timing.GetDelayFor(source);
        return perSource >= 0f ? perSource : Mathf.Max(0f, timing.regrowDelay);
    }

    /// <summary>
    /// Cheap pre-commit guess of the role a cell will regrow as: imprint role, else hidden
    /// Voronoi role, else None (gray). Commit-time ResolveRegrowRole stays authoritative.
    /// </summary>
    private MusicalRole GetProspectiveRegrowRole(Vector2Int gp)
    {
        if (_imprints != null && _imprints.TryGetValue(gp, out var imp))
        {
            if (imp.role != MusicalRole.None) return imp.role;
            if (imp.hiddenRole != MusicalRole.None) return imp.hiddenRole;
        }
        return MusicalRole.None;
    }

    public void CreateJailCenterForCollectable(
        Vector2Int gpCenter,
        float holdSeconds,
        int ownerId,
        DustClearMode mode = DustClearMode.HideInstant,
        float fadeSeconds = 0.10f,
        float regrowDelaySeconds = -1f)
    {
        ClearCell(gpCenter, mode, fadeSeconds, scheduleRegrow: true, source: DustClearSource.Jail, regrowDelaySeconds: regrowDelaySeconds);

        if (dustClaims != null && holdSeconds > 0f)
        {
            string owner = $"Collectable#{ownerId}";
            dustClaims.ClaimCell(owner, gpCenter, DustClaimType.TemporaryCarve, seconds: holdSeconds, refresh: true);
        }

        var neighbors = new Vector2Int[] { new(1,0), new(-1,0), new(0,1), new(0,-1) };
        foreach (var n in neighbors)
        {
            var np = gpCenter + n;
            if (!IsInBounds(np)) continue;
            if (!HasDustAt(np)) continue;
            ClearCell(np, mode, fadeSeconds, scheduleRegrow: true, source: DustClearSource.Jail, regrowDelaySeconds: regrowDelaySeconds);
        }
    }

    public void BeginSlowFadeAllDust(float durationSeconds)
    {
        if (_gridState.CellState == null || _gridState.CellGo == null || _gridState.Width <= 0 || _gridState.Height <= 0) return;

        for (int x = 0; x < _gridState.Width; x++)
        for (int y = 0; y < _gridState.Height; y++)
        {
            if (_gridState.CellState[x, y] != DustCellState.Solid) continue;
            var go = _gridState.CellGo[x, y];
            if (go == null) continue;
            float delay = Random.Range(0f, durationSeconds * 0.75f);
            StartCoroutine(DelayedFadeCell(new Vector2Int(x, y), delay));
        }
    }

    private IEnumerator DelayedFadeCell(Vector2Int gp, float delay)
    {
        if (delay > 0f) yield return new WaitForSeconds(delay);
        if (_gridState.CellState == null || _gridState.CellState[gp.x, gp.y] != DustCellState.Solid) yield break;
        ClearCell(gp, DustClearMode.FadeAndHide, fadeSeconds: 0.5f, scheduleRegrow: false);
    }

    private void RequestRegrowCellAt(Vector2Int gridPos, float delaySeconds = -1f, bool refreshIfPending = false, bool clearImprintOnRefresh = false)
    {
        if (_regrowthSuppressed)
            return;
        if (!IsInBounds(gridPos)) {

            if (_regrowthScheduler.RegrowthCoroutines != null && _regrowthScheduler.RegrowthCoroutines.TryGetValue(gridPos, out var pending))
            {
                if (pending != null) StopCoroutine(pending);
                _regrowthScheduler.RegrowthCoroutines.Remove(gridPos);
            }
            return;
        }

        // Held by a MineNode's energy economy: only ReleaseHeldCells may schedule regrow
        // (it removes the cell from the set before requesting).
        if (_heldRegrowCells.Contains(gridPos))
            return;

        bool shouldSchedule = !_permanentClearCells.Contains(gridPos);

        if (shouldSchedule && HasDustAt(gridPos))
            shouldSchedule = false;

        if (shouldSchedule && (_imprints == null || !_imprints.ContainsKey(gridPos)))
            shouldSchedule = false;

        if (!shouldSchedule)
            return;

        float delay = delaySeconds >= 0f ? delaySeconds : (_activeMazePattern != null ? _activeMazePattern.dustTiming.regrowDelay : 8f);

        EnsureRegrowController();
        _regrow?.RequestRegrowCellAt(gridPos, delay, refreshIfPending);
    }

    public void SetStarKeepClear(Vector2Int centerCell, int radiusCells, bool forceRemoveExisting)
    {
        if (drums == null) return;

        int w = drums.GetSpawnGridWidth();
        int h = drums.GetSpawnGridHeight();
        if (w <= 0 || h <= 0) return;

        radiusCells = Mathf.Max(0, radiusCells);

        var next = new HashSet<Vector2Int>();
        FillDisk(next, centerCell, radiusCells, w, h);

        _exclusions.UpdateStarPocket(next, _tmpReleased, _tmpClaimed);

        const string claimOwner = "PhaseStarPocket";
        if (dustClaims != null)
        {
            for (int i = 0; i < _tmpReleased.Count; i++)
                dustClaims.ReleaseCell(claimOwner, _tmpReleased[i], DustClaimType.KeepClear);

            for (int i = 0; i < _tmpClaimed.Count; i++)
                dustClaims.ClaimCell(claimOwner, _tmpClaimed[i], DustClaimType.KeepClear, seconds: -1f, refresh: true);
        }

        for (int i = 0; i < _tmpReleased.Count; i++)
        {
            var cell = _tmpReleased[i];
            RequestRegrowCellAt(cell, delaySeconds: -1f, refreshIfPending: true, clearImprintOnRefresh: false);
        }

        if (forceRemoveExisting)
        {
            for (int i = 0; i < _tmpClaimed.Count; i++)
            {
                var cell = _tmpClaimed[i];
                if (TryGetCellGo(cell, out var go) && go != null)
                    ClearCell(cell, DustClearMode.FadeAndHide, fadeSeconds: 2f, scheduleRegrow: false);
            }
        }
    }

    private void CarvePermanentDisk(Vector2Int center, int radiusCells)
    {
        if (drums == null) return;

        int w = drums.GetSpawnGridWidth();
        int h = drums.GetSpawnGridHeight();

        var cells = new HashSet<Vector2Int>();
        FillDisk(cells, center, radiusCells, w, h);
        foreach (var gp in cells)
            DespawnDustAtAndMarkPermanent(gp);
    }
}
