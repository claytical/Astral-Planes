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
        bool have = _registry.GoToCell.TryGetValue(dust.gameObject, out gp);
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
        _registry.GoToCell[go] = gp;
        SetCellState(gp, DustCellState.Empty);

        return go;
    }

    public void CarveDustByVehicle(Vector2Int cell, float fadeSeconds, ShipMusicalProfile shipProfile = null)
    {
        if (!IsInBounds(cell)) return;
        if (HasCellFlag(cell, CellFlags.Solid)) return; // bypass-proof: no ship carveResistanceBypass01 can defeat this
        if (!TryGetCellState(cell, out var st) || st != DustCellState.Solid) return;

        var cellDust = _gridState.CellDust?[cell.x, cell.y];
        if (cellDust != null) SetDustCollision(cellDust, false);

        FinalizeVehicleCarve(cell, fadeSeconds, shipProfile);
    }

    public void ChipDustByVehicle(Vector2Int cell, int energyAmount, float fadeSeconds, float resistanceBypass01 = 0f, ShipMusicalProfile shipProfile = null)
    {
        if (!IsInBounds(cell)) return;
        if (HasCellFlag(cell, CellFlags.Solid)) return; // bypass-proof: no ship carveResistanceBypass01 can defeat this
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
        // dust.CurrentTint has usually been lerped toward gray by prior ChipEnergy calls
        // (see CosmicDust.Energy.cs) by the time this fires — capture the role's true,
        // undrained color for the explosion burst instead, mirroring how the tentacle-zap
        // path captures its tint before draining (PhaseStarDustAffect.TentacleLifecycle.cs).
        // Must use GetProspectiveRegrowRole, not dust.Role: a cell showing its darkened
        // hidden-role preview (StaggeredGrowthFitDuration's "gray start" branch) still has
        // dust.Role == None until this carve promotes it — dust.Role alone can't see the
        // color the player is actually looking at for an as-yet-unrevealed cell.
        Color? explosionTint = null;
        MusicalRole prospectiveRole = GetProspectiveRegrowRole(cell);
        if (prospectiveRole != MusicalRole.None)
        {
            var roleProfile = _roleDensity.GetResolvedRoleProfile(prospectiveRole);
            if (roleProfile != null) explosionTint = roleProfile.GetBaseColor();
        }

        _colliderSuppression.ClearSuppression(cell);
        _imprints.EnsureAllocated();
        if (!_imprints.RestoreVoronoiImprint(cell))
            _imprints.PromoteHiddenRole(cell);

        SetCellFlag(cell, CellFlags.PlayerCarved);
        TutorialEvents.RaiseCellCarvedByVehicle();

        bool wasVoidCell = ClearCellFlag(cell, CellFlags.VoidGrow);
        if (wasVoidCell) _imprints.Remove(cell);

        // The carving ship is a growth agent: its per-role multiplier scales the resolved
        // delay, so a Bass-accelerator ship makes Bass dust regrow sooner. Resolved after
        // the role promotion above so the multiplier keys off the role the cell regrows as.
        float delay = ResolveRegrowDelay(cell, DustClearSource.VehiclePlow, -1f);
        if (shipProfile != null && shipProfile.regrowDelayMultipliers != null)
            delay *= shipProfile.regrowDelayMultipliers.GetFor(GetProspectiveRegrowRole(cell));

        CarveCell(cell, fadeSeconds, scheduleRegrow: true, source: DustClearSource.VehiclePlow, regrowDelaySeconds: delay, runPreExplode: true, explosionTintOverride: explosionTint);
    }

    /// <summary>
    /// Regrow-delay precedence:
    /// 1. Explicit per-call override (>= 0).
    /// 2. The cell's prospective role's MusicalRoleProfile.regrowthDelay (>= 0) — consulted
    ///    regardless of DustClearSource or ForceGrayRegrow. Pacing is a separate concern from
    ///    identity: a role's authored regrow speed holds whether the cell was vehicle-plowed,
    ///    star-drained, jailed, or track-filled. ForceGrayRegrow (single-role-motif temporary
    ///    collectable-plow corridors) still forces the cell's *color* back to MusicalRole.None on
    ///    regrow (see ResolveRegrowVoice). ZapForceGray (star-drain harvest) no longer hides
    ///    color — a zapped cell regrows showing its true role tint immediately, since the anti-farm
    ///    invariant ("can't feed a star off dust you didn't carve") is fully enforced independently
    ///    by the flower/bloom gate (CosmicDust.HasBloomedFlower / IsVisuallyPresentForTargeting),
    ///    which only a vehicle plow or bomb blast can grant — a zap-regrown cell is visible but not
    ///    zap-eligible again until the player physically re-carves it.
    /// 3. Active maze pattern's per-source delay (>= 0) — reached for None-role cells, or a role
    ///    whose regrowthDelay is -1 ("use maze pattern").
    /// 4. Active maze pattern's base regrowDelay.
    /// 5. Hard fallback 8s (no active pattern).
    /// </summary>
    private float ResolveRegrowDelay(Vector2Int cell, DustClearSource source, float explicitOverride)
    {
        if (explicitOverride >= 0f)
            return explicitOverride;

        MusicalRole prospectiveRole = GetProspectiveRegrowRole(cell);
        if (prospectiveRole != MusicalRole.None)
        {
            var roleProfile = _roleDensity.GetResolvedRoleProfile(prospectiveRole);
            if (roleProfile != null && roleProfile.regrowthDelay >= 0f)
                return ApplyDensityRegrowEasing(roleProfile.regrowthDelay);
        }

        var timing = _activeMazePattern != null ? _activeMazePattern.dustTiming : null;
        if (timing == null)
            return ApplyDensityRegrowEasing(8f);

        float perSource = timing.GetDelayFor(source);
        float baseDelay = perSource >= 0f ? perSource : Mathf.Max(0f, timing.regrowDelay);
        return ApplyDensityRegrowEasing(baseDelay);
    }

    /// <summary>
    /// Breathing-room easing: as overall solid fraction rises past densityThrottleHalfAt/SkipAt
    /// (shared thresholds with GravityVoidController.ApplyDensityThrottle, which throttles new
    /// void-growth rings at the same points), regrow delay ramps smoothly up to
    /// regrowDensityEaseMaxMultiplier so already-cleared paths stay open longer as pressure
    /// builds. A smooth ramp is used rather than a hard step because this value is felt on every
    /// single cell, not just a discrete ring count. Only eases already-scheduled regrows; nothing
    /// thins existing solid cells — GetSolidFraction() only drops as the player keeps clearing.
    /// </summary>
    private float ApplyDensityRegrowEasing(float baseDelay)
    {
        float solidFraction = GetRoleRelevantSolidFraction();
        float t = Mathf.InverseLerp(config.densityThrottleHalfAt, config.densityThrottleSkipAt, solidFraction);
        return baseDelay * Mathf.Lerp(1f, config.regrowDensityEaseMaxMultiplier, Mathf.Clamp01(t));
    }

    // Excludes generic-fill (exact-count leftover) cells from the density-pressure figure that
    // paces regrow delay — hundreds of inert filler cells shouldn't make a role's authored
    // regrowthDelay read as inflated near motif start. GetSolidFraction() itself is left
    // untouched: GravityVoidController's void-growth throttle and DrumTrack's density-driven
    // beat intensity both intentionally key off the literal, filler-inclusive figure.
    private float GetRoleRelevantSolidFraction()
    {
        int w = _gridState.Width;
        int h = _gridState.Height;
        if (w <= 0 || h <= 0) return 0f;
        int relevantSolid = _roleDensity.TotalSolidCount() - _roleDensity.GenericFillSolidCount;
        return Mathf.Clamp01(Mathf.Max(0, relevantSolid) / (float)(w * h));
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
        // Collectable-driven clear: regrow gray so the hidden imprint stays unrevealed
        // (and the regrown cell can't become a PhaseStar tentacle target).
        SetCellFlag(gpCenter, CellFlags.ForceGrayRegrow);
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
            SetCellFlag(np, CellFlags.ForceGrayRegrow);
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

    // Sibling of BeginSlowFadeAllDust, but pulses every currently-solid cell's displayed
    // tint through a rainbow hue-cycle instead of fading it out — used by Wisp.Consume()
    // so the whole maze reacts visually, not just the wisp itself. Deliberately no
    // per-cell stagger (unlike the fade above): the maze should read as pulsing in
    // unison. Iterates the raw grid rather than GetColoredDustCells, since that helper
    // filters to Role != MusicalRole.None and would skip every wall in a blind
    // path-choice maze (built with role = MusicalRole.None) — exactly the cells that
    // need to pulse here.
    public void BeginRainbowPulseAllDust(float durationSeconds, float hueCyclesPerSecond, float saturation, float value)
    {
        if (_gridState.CellState == null || _gridState.CellGo == null || _gridState.Width <= 0 || _gridState.Height <= 0) return;

        for (int x = 0; x < _gridState.Width; x++)
        for (int y = 0; y < _gridState.Height; y++)
        {
            if (_gridState.CellState[x, y] != DustCellState.Solid) continue;
            var go = _gridState.CellGo[x, y];
            if (go == null) continue;
            var dust = go.GetComponent<CosmicDust>();
            dust?.BeginRainbowPulse(durationSeconds, hueCyclesPerSecond, saturation, value);
        }
    }

    private void RequestRegrowCellAt(Vector2Int gridPos, float delaySeconds = -1f, bool refreshIfPending = false, bool clearImprintOnRefresh = false)
    {
        if (_regrowthSuppressed)
            return;
        if (!IsInBounds(gridPos)) {
            _regrow?.CancelRegrow(gridPos);
            return;
        }

        // Held by a DiscoveryTrackNode's energy economy: only ReleaseHeldCells may schedule regrow
        // (it removes the cell from the set before requesting).
        if (_heldRegrowCells.Contains(gridPos))
            return;

        // Exact-count mode's permanent-filler (None) dust is permanent scenery for the rest of
        // the motif once cleared — it never re-enters the regrow cadence like quick-regrow
        // generic dust does. ClearMaze()'s _permanentClearCells.Clear() lifts this at the next
        // motif's maze generation.
        if (_imprints != null && _imprints.TryGetValue(gridPos, out var fillImp)
            && fillImp.hiddenRoleIsGenericFill && fillImp.genericFillNeverRegrows)
        {
            _permanentClearCells.Add(gridPos);
            _regrow?.CancelRegrow(gridPos);
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

    public void SetStarKeepClear(Vector2Int centerCell, int radiusCells, bool forceRemoveExisting) =>
        _vehicleReservation.SetStarKeepClear(centerCell, radiusCells, forceRemoveExisting);

    private void CarvePermanentDisk(Vector2Int center, int radiusCells)
    {
        if (drums == null) return;

        int w = drums.GetSpawnGridWidth();
        int h = drums.GetSpawnGridHeight();

        var cells = new HashSet<Vector2Int>();
        DustGridMath.FillDisk(cells, center, radiusCells, w, h);
        foreach (var gp in cells)
            DespawnDustAtAndMarkPermanent(gp);
    }
}
