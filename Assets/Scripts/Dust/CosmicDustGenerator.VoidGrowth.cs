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
        int innerRadiusCellsExclusive = -1)
    {
        // Motif done (record ready) or bridging: the field doesn't grow.
        if (_regrowthSuppressed) return 0;
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

                MusicalRole spawnRole = imprintRole;
                Color spawnColor = c;
                MusicalRole storedHidden = MusicalRole.None;

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
                    _imprints.ApplyHiddenHintToDust(gp, existingDust, _resolveRoleProfileDelegate);
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
                if (_regrow.IsVoidGrowing(gp))
                    continue;

                // Ripple: cells nearer the ring's inner edge (u≈0) start growing sooner than
                // cells near the outer edge (u≈1), so the ring visibly sweeps outward instead
                // of every cell popping in on the same frame. Delayed cells get a shorter tween
                // so they still finish around the same time as early ones.
                float rippleWindowSeconds = growInSeconds * config.voidRingRippleFraction;
                float startDelaySeconds = u * rippleWindowSeconds;
                float effectiveGrowIn = Mathf.Max(0.05f, growInSeconds - startDelaySeconds);

                SetCellFlag(gp, CellFlags.VoidGrow);
                _regrow.StartVoidGrow(gp, VoidGrowCellNow(gp, spawnRole, spawnColor, effectiveGrowIn, startDelaySeconds));
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
        bool solid = false, bool bloomed = false)
    {
        // bloomed: cells rest at full role brightness (no latent darkening) — used by the
        // control sandbox, where colour IS the lesson.
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

            var imprint = new DustImprint { color = spawnColor, role = spawnRole, hiddenRole = hiddenRole2 };
            if (solid)
            {
                imprint.carveResistance01 = 1f;
                imprint.drainResistance01 = 1f;
            }
            _imprints[gp] = imprint;
            if (solid) SetCellFlag(gp, CellFlags.Solid);

            if (TryGetCellGo(gp, out var existingGo) && existingGo != null &&
                existingGo.TryGetComponent<CosmicDust>(out var existingDust) &&
                existingDust != null && HasDustAt(gp))
            {
                existingDust.ApplyRoleAndCharge(MusicalRole.None, config.mazeTint, c.a);
                _imprints.ApplyHiddenHintToDust(gp, existingDust);
                if (solid) existingDust.HideFlower(); // bypass-proof wall: no bud
                var res = ResolveResistanceProfile(gp, role, context: "SpawnDustAtCells:existing");
                existingDust.clearing.drainResistance01 = solid ? 1f : res.drainResistance01;
                continue;
            }

            if (_permanentClearCells.Contains(gp)) continue;
            // A standing vault shell cell overrides every claim (keep-clear pockets, trails,
            // travel corridors): a claim there would otherwise leave a permanent hole in the
            // sealed shell. VoidGrowCellNow still waits out a ship physically on the cell.
            bool vaultShell = IsSealedVaultShell(gp);
            if (!vaultShell && (dustClaims != null && dustClaims.IsBlocked(gp))) continue;
            if (_regrow.IsVoidGrowing(gp)) continue;

            SetCellFlag(gp, CellFlags.VoidGrow);
            _regrow.StartVoidGrow(gp, VoidGrowCellNow(gp, spawnRole, spawnColor, growInSeconds, 0f, bloomed));
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
        StopActiveStaggeredGrowth();
    }

    /// <summary>
    /// Promotes hidden Solid cells within <paramref name="radiusCells"/> of <paramref name="centerGP"/>
    /// whose Voronoi role matches <paramref name="role"/> to their true role color,
    /// so PhaseStar can target them (player retry path after DiscoveryTrackNode expiry).
    /// </summary>
    public void RevealHiddenDustByRole(Vector2Int centerGP, int radiusCells, MusicalRole role)
    {
        if (radiusCells <= 0 || _gridState.CellState == null) return;

        var profile = _roleDensity.GetResolvedRoleProfile(role);
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
    private IEnumerator VoidGrowCellNow(Vector2Int gp, MusicalRole role, Color tintWithAlpha, float growInSeconds, float startDelaySeconds = 0f, bool bloomed = false)
    {
        if (!IsInBounds(gp)) { _regrow.VoidGrowFinished(gp); yield break; }

        // Ripple delay: wait before evaluating vetoes/spawning so a cell delayed by A1's ring
        // ripple checks current state rather than stale state from ring-imprint time.
        if (startDelaySeconds > 0f) yield return new WaitForSeconds(startDelaySeconds);

        // Standing vault shell cells ignore claims (see SpawnDustAtCells); only a ship on the cell stops them.
        bool vaultShell0       = IsSealedVaultShell(gp);
        bool veto0_perm        = _permanentClearCells.Contains(gp);
        // IsDustSpawnBlocked / IsKeepClearCell are the same claim lookup as dustClaims.IsBlocked.
        bool veto0_claim       = !vaultShell0 && dustClaims != null && dustClaims.IsBlocked(gp);

        // Permanent clear and claims are hard vetoes for even showing visuals.
        if (veto0_perm || veto0_claim)
        {
            _regrow.VoidGrowFinished(gp);
            yield break;
        }

        var go = GetOrCreateCellGO(gp);
        if (go == null)
        {
            _regrow.VoidGrowFinished(gp);
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
            if (bloomed && role != MusicalRole.None) dust.BloomFlower(tintWithAlpha);
            if (role == MusicalRole.None) _imprints.ApplyHiddenHintToDust(gp, dust, _resolveRoleProfileDelegate);
            dust.SetFeedbackColors(Color.white, Color.darkGray);
            dust.Begin();

            // Organic grow-in: start at maze gray and fade to role color over the full visual duration.
            Color dormantStart = config.mazeTint;
            dormantStart.a = tintWithAlpha.a;
            dust.ApplyTintVisual(dormantStart);
            // Fade to the legibility-resolved rest tint (darkened unless bloomed / hint preview
            // for None) rather than the raw role color, or the grow-in would end brighter than
            // the cell's actual rest state.
            Color fadeTo = dust.CurrentTint;
            fadeTo.a = tintWithAlpha.a;
            dust.TintFadeInManaged(config.voidDustGrowInSeconds, dormantStart, fadeTo);

            // Always non-colliding during grow
            SetDustCollision(dust, false);
        }
        float enableDelay = Mathf.Max(config.regrowColliderEnableDelaySeconds, growInSeconds * 0.85f);
        yield return new WaitForSeconds(enableDelay);

        if (!IsInBounds(gp) || _permanentClearCells.Contains(gp))
        {
            SetCellState(gp, DustCellState.Empty);
            FadeAndHideCellGO(go);
            _regrow.VoidGrowFinished(gp);
            yield break;
        }

        bool veto1_vehicle      = IsVehicleOverlappingCell(gp);
        bool veto1_claim        = !IsSealedVaultShell(gp) && dustClaims != null && dustClaims.IsBlocked(gp);

        // (There was a "keep-clear at end → visual only" branch here; it could never run, since
        // keep-clear IS a claim and the claim veto above already aborted. Removed 2026-11-25.)
        if (veto1_vehicle || veto1_claim)
        {
            if (GameFlowManager.VerboseLogging) Debug.Log($"[VOID_GROW] ABORT_END gp={gp} vehicle={veto1_vehicle} claim={veto1_claim}");
            SetCellState(gp, DustCellState.Empty);
            FadeAndHideCellGO(go);
            _regrow.VoidGrowFinished(gp);
            yield break;
        }

        // Otherwise: become solid.
        SetCellState(gp, DustCellState.Solid);
        if (dust != null)
        {
            dust.EnsureMinSolidAlpha(0.30f);
            SetDustCollision(dust, true);
        }

        _regrow.VoidGrowFinished(gp);
    }
}
