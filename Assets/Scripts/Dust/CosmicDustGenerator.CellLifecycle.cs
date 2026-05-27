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
            dust.SetCellSizeDrivenScale(Mathf.Max(0.001f, drums.GetCellWorldSize()), dustFootprintMul, cellClearanceWorld);
            dust.HideVisualsInstant();
            SetDustCollision(dust, false);
        }

        _gridState.CellGo[gp.x, gp.y] = go;
        _gridState.CellDust[gp.x, gp.y] = dust;
        _goToCell[go] = gp;
        SetCellState(gp, DustCellState.Empty);

        return go;
    }

    public void CarveDustByVehicle(Vector2Int cell, float fadeSeconds)
    {
        if (!IsInBounds(cell)) return;
        if (!TryGetCellState(cell, out var st) || st != DustCellState.Solid) return;

        _imprints ??= new Dictionary<Vector2Int, DustImprint>();
        if (!RestoreVoronoiImprint(cell))
            PromoteHiddenRole(cell);

        _playerCarvedCells.Add(cell);

        var cellGo = _gridState.CellGo?[cell.x, cell.y];

        bool wasVoidCell = _voidGrowCells.Remove(cell);
        if (wasVoidCell)
            _imprints?.Remove(cell);
        var explode = cellGo.GetComponentInChildren<Explode>(true);
        Debug.Log($"[DUST-CLEAR] explode={(explode != null ? explode.name : "NULL")} cell={cell} go={cellGo.name}");
        CarveCell(cell, fadeSeconds, scheduleRegrow: true, runPreExplode: true);

        if (_hiddenImprints != null && _hiddenImprints.TryGetValue(cell, out var hiddenRole))
        {
            var roleProfile = MusicalRoleProfileLibrary.GetProfile(hiddenRole);
            if (roleProfile != null && roleProfile.regrowthDelay >= 0f)
                RequestRegrowCellAt(cell, roleProfile.regrowthDelay, refreshIfPending: true);
        }
    }

    public void ChipDustByVehicle(Vector2Int cell, int energyAmount, float fadeSeconds, float resistanceBypass01 = 0f)
    {
        if (!IsInBounds(cell)) return;
        if (!TryGetCellState(cell, out var st) || st != DustCellState.Solid) return;

        var cellGo = _gridState.CellGo?[cell.x, cell.y];
        if (cellGo == null) return;
        if (!cellGo.TryGetComponent<CosmicDust>(out var dust) || dust == null) return;

        float effectiveResistance = dust.clearing.carveResistance01 * (1f - Mathf.Clamp01(resistanceBypass01));
        float resistMul = 1f - effectiveResistance;
        int effectiveChip = Mathf.Max(1, Mathf.RoundToInt(energyAmount * resistMul));

        int removed = dust.ChipEnergy(effectiveChip);
        if (removed <= 0) return;

        if (dust.currentEnergyUnits <= 0)
        {
            _imprints ??= new Dictionary<Vector2Int, DustImprint>();
            if (!RestoreVoronoiImprint(cell))
                PromoteHiddenRole(cell);

            _playerCarvedCells.Add(cell);

            bool wasVoidCell = _voidGrowCells.Remove(cell);
            if (wasVoidCell) _imprints?.Remove(cell);

            CarveCell(cell, fadeSeconds, scheduleRegrow: true, runPreExplode: true);

            if (_hiddenImprints != null && _hiddenImprints.TryGetValue(cell, out var hiddenRole))
            {
                var roleProfile = MusicalRoleProfileLibrary.GetProfile(hiddenRole);
                if (roleProfile != null && roleProfile.regrowthDelay >= 0f)
                    RequestRegrowCellAt(cell, roleProfile.regrowthDelay, refreshIfPending: true);
            }
        }
    }

    public void CreateJailCenterForCollectable(
        Vector2Int gpCenter,
        float holdSeconds,
        int ownerId,
        DustClearMode mode = DustClearMode.HideInstant,
        float fadeSeconds = 0.10f,
        float regrowDelaySeconds = -1f)
    {
        ClearCell(gpCenter, mode, fadeSeconds, scheduleRegrow: true, regrowDelaySeconds: regrowDelaySeconds);

        if (dustClaims != null && holdSeconds > 0f)
        {
            string owner = $"Collectable#{ownerId}";
            dustClaims.ClaimCell(owner, gpCenter, DustClaimType.TemporaryCarve, seconds: holdSeconds, refresh: true);
        }

        float neighborRegrow = regrowDelaySeconds >= 0f ? regrowDelaySeconds : holdSeconds;
        var neighbors = new Vector2Int[] { new(1,0), new(-1,0), new(0,1), new(0,-1) };
        foreach (var n in neighbors)
        {
            var np = gpCenter + n;
            if (!IsInBounds(np)) continue;
            if (!HasDustAt(np)) continue;
            ClearCell(np, mode, fadeSeconds, scheduleRegrow: true, regrowDelaySeconds: neighborRegrow);
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
        for (int dy = -radiusCells; dy <= radiusCells; dy++)
        for (int dx = -radiusCells; dx <= radiusCells; dx++)
        {
            if ((dx * dx + dy * dy) > radiusCells * radiusCells) continue;
            var gp = new Vector2Int(centerCell.x + dx, centerCell.y + dy);
            if (gp.x < 0 || gp.y < 0 || gp.x >= w || gp.y >= h) continue;
            next.Add(gp);
        }

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
        if (drums == null)
            return;

        int w = drums.GetSpawnGridWidth();
        int h = drums.GetSpawnGridHeight();

        for (int dx = -radiusCells; dx <= radiusCells; dx++)
        {
            for (int dy = -radiusCells; dy <= radiusCells; dy++)
            {
                var gp = new Vector2Int(center.x + dx, center.y + dy);

                if (gp.x < 0 || gp.y < 0 || gp.x >= w || gp.y >= h)
                    continue;

                if (dx * dx + dy * dy > radiusCells * radiusCells)
                    continue;

                DespawnDustAtAndMarkPermanent(gp);
            }
        }
    }
}
