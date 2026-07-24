using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public partial class CosmicDustGenerator
{
     public int GrowVoidDustDiskFromGrid(
        Vector2Int centerGP,
        int outerRadiusCells,
        MusicalRole imprintRole,
        Color hueRgb,
        float energyAtCenter01,
        float falloffExp,
        float growInSeconds,
        int fillWedges01To4,
        List<Vector2Int> vehicleCells,
        int vehicleNoSpawnRadiusCells,
        int maxCellsThisCall = -1,
        int innerRadiusCellsExclusive = -1,
        bool hideRole = false)
    {
        EnsureCellGrid();
        _imprints.EnsureAllocated(2048);

        if (outerRadiusCells <= 0) return 0;

        energyAtCenter01 = Mathf.Clamp01(energyAtCenter01);
        falloffExp = Mathf.Max(0.01f, falloffExp);
        growInSeconds = Mathf.Max(0.01f, growInSeconds);
        fillWedges01To4 = Mathf.Clamp(fillWedges01To4, 1, 4);

        int processed = 0;
        int rOuterSq = outerRadiusCells * outerRadiusCells;
        int rInnerSq = innerRadiusCellsExclusive >= 0
            ? innerRadiusCellsExclusive * innerRadiusCellsExclusive
            : -1;

        for (int dy = -outerRadiusCells; dy <= outerRadiusCells; dy++)
        {
            for (int dx = -outerRadiusCells; dx <= outerRadiusCells; dx++)
            {
                int dSq = dx * dx + dy * dy;
                if (dSq > rOuterSq) continue;
                if (rInnerSq >= 0 && dSq <= rInnerSq) continue;
                if (!IsInFilledWedge(dx, dy, fillWedges01To4)) continue;

                Vector2Int gp = new Vector2Int(centerGP.x + dx, centerGP.y + dy);
                if (!IsInBounds(gp)) continue;

                // Budget
                if (maxCellsThisCall >= 0 && processed >= maxCellsThisCall)
                    return processed;
    // -------------------------------
    // Radiating ring alpha (annulus-local)
    // Strong at inner edge of NEW ring, fades toward outer edge.
    // This matches incremental growth using innerRadiusCellsExclusive.
    // -------------------------------
                float d = Mathf.Sqrt(dSq);

                float inner = (innerRadiusCellsExclusive >= 0) ? innerRadiusCellsExclusive : 0f;
                float outer = Mathf.Max(1f, outerRadiusCells);
                float span  = Mathf.Max(0.0001f, outer - inner);

    // u=0 at inner edge of this ring, u=1 at outer edge
                float u = Mathf.Clamp01((d - inner) / span);

    // Energy: bright at u=0, fades toward u=1
                float energy01 = Mathf.Clamp01(energyAtCenter01 * Mathf.Pow(1f - u, falloffExp));

    // IMPORTANT: avoid "invisible SOLID" tiles (especially when logging with F2).
    // This is a *visual* floor; it also prevents sprite alpha=0 tiles that are physically present.
    // Set high enough that the role color reads clearly even at the outer edge of a burst.
                const float kMinVisibleAlpha = 0.55f;
                float visibleAlpha = Mathf.Max(energy01, kMinVisibleAlpha);

                Color c = hueRgb;
                c.a = visibleAlpha;
                if (dx == 0 && dy == outerRadiusCells) // pick a consistent sample
                {
                    if (GameFlowManager.VerboseLogging) Debug.Log($"[VOID_RING] rIn={innerRadiusCellsExclusive} rOut={outerRadiusCells} d={d:F2} u={u:F2} a={c.a:F2}");
                }
                // Vehicle pocket is a hard exclusion — no imprint, no spawn, no visual update.
                if (IsNearAnyVehicle(gp, vehicleCells, vehicleNoSpawnRadiusCells)) continue;

                // When hideRole is true, spawn gray so PhaseStar cannot detect the cell
                // until the vehicle reveals it by carving. hiddenRole is baked into the imprint.
                MusicalRole spawnRole = imprintRole;
                Color spawnColor = c;
                MusicalRole storedHidden = MusicalRole.None;
                if (hideRole && imprintRole != MusicalRole.None)
                {
                    storedHidden = imprintRole;
                    spawnRole = MusicalRole.None;
                    spawnColor = config.mazeTint;
                    spawnColor.a = c.a;
                }

                // Always write persistent imprint (so regrow picks it up later)
                _imprints[gp] = new DustImprint
                {
                    color = spawnColor,
                    role = spawnRole,
                    hiddenRole = storedHidden,
                };
                processed++;

    // 1) If dust already exists, ALWAYS refresh visuals (even if keep-clear/blocked/etc).
                if (TryGetCellGo(gp, out var existingGo) && existingGo != null &&
                    existingGo.TryGetComponent<CosmicDust>(out var existingDust) && existingDust != null &&
                    HasDustAt(gp))
                {
                    existingDust.ApplyRoleAndCharge(MusicalRole.None, config.mazeTint, c.a);
                    _imprints.ApplyHiddenHintToDust(gp, existingDust);
                    var resistance = ResolveResistanceProfile(gp, imprintRole, context: "GrowVoidDustDisk:existing");
                    existingDust.clearing.drainResistance01 = resistance.drainResistance01;
                    continue;
                }

    // 2) Only after that, decide whether we’re allowed to SPAWN dust into empty space.
                if (_permanentClearCells.Contains(gp)) continue;
                if (IsKeepClearCell(gp)) continue;
                if (dustClaims != null && dustClaims.IsBlocked(gp)) continue;
                if (IsDustSpawnBlocked(gp)) continue;

    // 3) Spawn/regrow if empty
                if (_regrowthScheduler.VoidGrowCoroutines.ContainsKey(gp))
                    continue;

                SetCellFlag(gp, CellFlags.VoidGrow);
                _regrowthScheduler.VoidGrowCoroutines[gp] = StartCoroutine(VoidGrowCellNow(gp, spawnRole, spawnColor, growInSeconds));
            }
        }

        return processed;
    }

    private static bool IsNearAnyVehicle(Vector2Int gp, System.Collections.Generic.List<Vector2Int> vehicleCells, int radiusCells)
    {
        if (radiusCells <= 0) return false;
        if (vehicleCells == null || vehicleCells.Count == 0) return false;

        for (int i = 0; i < vehicleCells.Count; i++)
        {
            var vc = vehicleCells[i];
            int dx = Mathf.Abs(gp.x - vc.x);
            int dy = Mathf.Abs(gp.y - vc.y);
            if (dx <= radiusCells && dy <= radiusCells) return true;
        }
        return false;
    }

    private static bool IsInFilledWedge(int dx, int dy, int fillWedges01To4)
    {
        // Quadrant order: 0=NE, 1=NW, 2=SW, 3=SE
        int quad;
        if (dy >= 0)
            quad = (dx >= 0) ? 0 : 1;
        else
            quad = (dx < 0) ? 2 : 3;

        return quad < fillWedges01To4;
    }

    // Appends trap ring/disk cells directly into the maze stagger list so they grow in
    // alongside every other cell. Call from the onBeforeGrowth callback inside
    // GenerateMazeForPhaseWithPaths — _imprints is already initialised at
    // that point and will not be cleared again before the stagger runs.
    public void InjectTrapCellsIntoStagger(
        List<(Vector2Int, Vector3)> cellsToFill,
        Vector2Int centerGP,
        int outerRadiusCells,
        int innerRadiusCellsExclusive,
        MusicalRole hiddenRole)
    {
        if (drums == null || cellsToFill == null || outerRadiusCells <= 0) return;
        EnsureCellGrid();
        _imprints.EnsureAllocated(2048);

        int rOuterSq = outerRadiusCells * outerRadiusCells;
        int rInnerSq = innerRadiusCellsExclusive >= 0 ? innerRadiusCellsExclusive * innerRadiusCellsExclusive : -1;

        for (int dy = -outerRadiusCells; dy <= outerRadiusCells; dy++)
        {
            for (int dx = -outerRadiusCells; dx <= outerRadiusCells; dx++)
            {
                int dSq = dx * dx + dy * dy;
                if (dSq > rOuterSq) continue;
                if (rInnerSq >= 0 && dSq <= rInnerSq) continue;

                var gp = new Vector2Int(centerGP.x + dx, centerGP.y + dy);
                if (!IsInBounds(gp)) continue;
                if (_permanentClearCells.Contains(gp)) continue;

                _imprints[gp] = new DustImprint
                {
                    role              = MusicalRole.None,
                    color             = config.mazeTint,
                    carveResistance01 = 0f,
                    drainResistance01 = 0f,
                    maxEnergyUnits    = 1,
                    healDelay         = 0f,
                    hiddenRole        = hiddenRole,
                };

                Vector3 worldPos = drums.GridToWorldPosition(gp);
                cellsToFill.Add((gp, worldPos));
            }
        }
    }

    public void InjectTrapCellsFromList(
        List<(Vector2Int, Vector3)> cellsToFill,
        IEnumerable<Vector2Int> trapCells,
        MusicalRole hiddenRole)
    {
        if (drums == null || cellsToFill == null || trapCells == null) return;
        EnsureCellGrid();
        _imprints.EnsureAllocated(256);

        foreach (var gp in trapCells)
        {
            if (!IsInBounds(gp)) continue;
            if (_permanentClearCells.Contains(gp)) continue;

            _imprints[gp] = new DustImprint
            {
                role              = MusicalRole.None,
                color             = config.mazeTint,
                carveResistance01 = 0f,
                drainResistance01 = 0f,
                maxEnergyUnits    = 1,
                healDelay         = 0f,
                hiddenRole        = hiddenRole,
            };

            Vector3 worldPos = drums.GridToWorldPosition(gp);
            cellsToFill.Add((gp, worldPos));
        }
    }

    public void SpawnDustAtCells(
        IReadOnlyList<Vector2Int> cells,
        MusicalRole role, Color hue, float energy01, float growInSeconds,
        bool hideRole = false)
    {
        if (cells == null || cells.Count == 0) return;
        EnsureCellGrid();
        _imprints.EnsureAllocated(256);
        const float kMinVisibleAlpha = 0.55f;
        Color c = hue;
        c.a = Mathf.Max(energy01, kMinVisibleAlpha);

        for (int i = 0; i < cells.Count; i++)
        {
            var gp = cells[i];
            if (!IsInBounds(gp)) continue;

            MusicalRole spawnRole = role;
            Color spawnColor = c;
            MusicalRole hiddenRole2 = MusicalRole.None;
            if (hideRole && role != MusicalRole.None)
            {
                hiddenRole2 = role;
                spawnRole = MusicalRole.None;
                spawnColor = config.mazeTint;
                spawnColor.a = c.a;
            }

            _imprints[gp] = new DustImprint { color = spawnColor, role = spawnRole, hiddenRole = hiddenRole2 };

            if (TryGetCellGo(gp, out var existingGo) && existingGo != null &&
                existingGo.TryGetComponent<CosmicDust>(out var existingDust) &&
                existingDust != null && HasDustAt(gp))
            {
                existingDust.ApplyRoleAndCharge(MusicalRole.None, config.mazeTint, c.a);
                _imprints.ApplyHiddenHintToDust(gp, existingDust);
                var res = ResolveResistanceProfile(gp, role, context: "SpawnDustAtCells:existing");
                existingDust.clearing.drainResistance01 = res.drainResistance01;
                continue;
            }

            if (_permanentClearCells.Contains(gp)) continue;
            if (IsKeepClearCell(gp)) continue;
            if (dustClaims != null && dustClaims.IsBlocked(gp)) continue;
            if (IsDustSpawnBlocked(gp)) continue;
            if (_regrowthScheduler.VoidGrowCoroutines.ContainsKey(gp)) continue;

            SetCellFlag(gp, CellFlags.VoidGrow);
            _regrowthScheduler.VoidGrowCoroutines[gp] =
                StartCoroutine(VoidGrowCellNow(gp, spawnRole, spawnColor, growInSeconds));
        }
    }

    private void StopActiveStaggeredGrowth()
    {
        if (_spawnRoutine != null)
        {
            StopCoroutine(_spawnRoutine);
            _spawnRoutine = null;
        }
    }

    private void EnterRuntimeVoidOnlyDustCreationMode() {
        _runtimeVoidOnlyDustCreation = true;
        StopActiveStaggeredGrowth();
    }

    /// <summary>
    /// Promotes hidden Solid cells within <paramref name="radiusCells"/> of <paramref name="centerGP"/>
    /// whose Voronoi role matches <paramref name="role"/> to their true role color,
    /// firing OnRoleChanged so PhaseStar can target them (player retry path after DiscoveryTrackNode expiry).
    /// </summary>
    public void RevealHiddenDustByRole(Vector2Int centerGP, int radiusCells, MusicalRole role)
    {
        if (radiusCells <= 0 || _gridState.CellState == null) return;

        var profile = MusicalRoleProfileLibrary.GetProfile(role);
        if (profile == null) return;
        Color roleColor = profile.GetBaseColor();

        int rSq = radiusCells * radiusCells;
        for (int dy = -radiusCells; dy <= radiusCells; dy++)
        for (int dx = -radiusCells; dx <= radiusCells; dx++)
        {
            if (dx * dx + dy * dy > rSq) continue;
            var gp = new Vector2Int(centerGP.x + dx, centerGP.y + dy);
            if (!IsInBounds(gp)) continue;
            if (!_imprints.TryGetValue(gp, out var hiddenImp) || hiddenImp.hiddenRole != role) continue;
            if (!TryGetCellState(gp, out var st) || st != DustCellState.Solid) continue;
            if (!TryGetDustAt(gp, out var dust) || dust.Role != MusicalRole.None) continue;

            if (!_imprints.PromoteHiddenRole(gp)) continue;
            dust.ApplyRoleAndCharge(role, roleColor, dust.Charge01);
        }
    }

    /// <summary>
    /// Called by CosmicDust.DrainCharge when a cell's visual alpha drops below the
    /// solid-visibility threshold (0.55). The cell is physically drained but was
    /// never explicitly "cleared" by gameplay — this bridges that gap so the
    /// collider doesn't linger as an invisible wall.
    /// </summary>
    private IEnumerator VoidGrowCellNow(Vector2Int gp, MusicalRole role, Color tintWithAlpha, float growInSeconds)
    {
        if (!IsInBounds(gp)) { _regrowthScheduler.VoidGrowCoroutines.Remove(gp); yield break; }

        bool veto0_perm        = _permanentClearCells.Contains(gp);
        bool veto0_spawnBlocked = IsDustSpawnBlocked(gp);
        bool veto0_claim       = (dustClaims != null && dustClaims.IsBlocked(gp));
        bool veto0_keep        = IsKeepClearCell(gp);

        // Permanent/spawn-block/claim are hard vetoes for even showing visuals.
        // Keep-clear is NOT a veto for visuals (it only prevents solid/collision).
        if (veto0_perm || veto0_spawnBlocked || veto0_claim)
        {
            _regrowthScheduler.VoidGrowCoroutines.Remove(gp);
            yield break;
        }

        var go = GetOrCreateCellGO(gp);
        if (go == null)
        {
            _regrowthScheduler.VoidGrowCoroutines.Remove(gp);
            yield break;
        }

        SetCellState(gp, DustCellState.Regrowing);

        CosmicDust dust = null;
        if (go.TryGetComponent(out dust) && dust != null)
        {
            dust.PrepareForReuse();
            dust.InitializeVisuals(DustTimings);

            dust.SetGrowInDuration(config.voidDustGrowInSeconds);
            var resistance = ResolveResistanceProfile(gp, role, context: "VoidGrowCellNow");
            dust.clearing.drainResistance01 = resistance.drainResistance01;
            dust.ApplyRoleAndCharge(role, tintWithAlpha, tintWithAlpha.a);
            if (role == MusicalRole.None) _imprints.ApplyHiddenHintToDust(gp, dust);
            dust.SetFeedbackColors(Color.white, Color.darkGray);
            dust.regrowAlphaCapped = true;
            dust.Begin();
            EnsureDustSpriteRendererEnabled(dust);

            // Organic grow-in: start at maze gray and fade to role color over the full visual duration.
            Color dormantStart = config.mazeTint;
            dormantStart.a = tintWithAlpha.a;
            dust.ApplyTintVisual(dormantStart);
            dust.StartCoroutine(dust.TintFadeIn(config.voidDustGrowInSeconds, dormantStart, tintWithAlpha));

            // Always non-colliding during grow
            SetDustCollision(dust, false);
        }
        float enableDelay = Mathf.Max(config.regrowColliderEnableDelaySeconds, growInSeconds * 0.85f);
        yield return new WaitForSeconds(enableDelay);

        if (dust != null)
            EnsureDustSpriteRendererEnabled(dust);

        if (!IsInBounds(gp) || _permanentClearCells.Contains(gp))
        {
            SetCellState(gp, DustCellState.Empty);
            FadeAndHideCellGO(go);
            _regrowthScheduler.VoidGrowCoroutines.Remove(gp);
            yield break;
        }

        bool veto1_spawnBlocked = IsDustSpawnBlocked(gp);
        bool veto1_vehicle      = IsVehicleOverlappingCell(gp);
        bool veto1_claim        = (dustClaims != null && dustClaims.IsBlocked(gp));
        bool veto1_keep         = IsKeepClearCell(gp);

        if (veto1_spawnBlocked || veto1_vehicle || veto1_claim)
        {
            if (GameFlowManager.VerboseLogging) Debug.Log($"[VOID_GROW] ABORT_END gp={gp} keep={veto1_keep} spawnBlocked={veto1_spawnBlocked} vehicle={veto1_vehicle} claim={veto1_claim}");
            SetCellState(gp, DustCellState.Empty);
            FadeAndHideCellGO(go);
            _regrowthScheduler.VoidGrowCoroutines.Remove(gp);
            yield break;
        }

        // Keep-clear at end: allow visuals, but never become solid/colliding.
        if (veto1_keep)
        {
            if (GameFlowManager.VerboseLogging) Debug.Log($"[VOID_GROW] VISUAL_ONLY gp={gp} (keep-clear)");
            SetCellState(gp, DustCellState.Regrowing); // or define a VisualOnly state later
            if (dust != null) SetDustCollision(dust, false);
            _regrowthScheduler.VoidGrowCoroutines.Remove(gp);
            yield break;
        }

        // Otherwise: become solid.
        SetCellState(gp, DustCellState.Solid);
        if (dust != null)
        {
            dust.regrowAlphaCapped = false;
            dust.EnsureMinSolidAlpha(0.55f);
            EnsureDustSpriteRendererEnabled(dust);
            SetDustCollision(dust, true);
        }

        _regrowthScheduler.VoidGrowCoroutines.Remove(gp);
    }

    private static void EnsureDustSpriteRendererEnabled(CosmicDust dust)
    {
        if (dust == null) return;
        var spriteRenderer = dust.GetComponentInChildren<SpriteRenderer>(true);
        if (spriteRenderer != null)
            spriteRenderer.enabled = true;
    }
}
