using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public partial class CosmicDustGenerator
{
    private IEnumerator StaggeredGrowthFitDuration(List<(Vector2Int grid, Vector3 pos)> cells, float totalDuration) {

        // Keep pacing similar, but enforce a per-frame millisecond budget
        float deadlineStep = Mathf.Max(0.0f, totalDuration / Mathf.Max(1, cells.Count));
        try
        {
            if (drums == null)
            {
                if (_gfm == null) _gfm = GameFlowManager.Instance;
                drums = _gfm != null ? _gfm.ResolveDrumTrack() : FindAnyObjectByType<DrumTrack>();
            }
            if (drums == null || dustPrefab == null) yield break;

            drums.SyncTileWithScreen();
            float cellWorldSize = Mathf.Max(0.001f, drums.GetCellWorldSize());

            float lastPacedAt = Time.realtimeSinceStartup;
            int i = 0;

            // Track which dust we spawned so we can re-enable colliders in a controlled way.
            // Track which dust we spawned so we can re-enable colliders in a controlled way.
            var spawnedDust = new List<CosmicDust>(cells.Count);

            while (i < cells.Count)
            {
                float frameStart  = Time.realtimeSinceStartup;
                float frameBudget = Mathf.Max(0f, config.maxSpawnMillisPerFrame) / 1000f;

                while (i < cells.Count && (Time.realtimeSinceStartup - frameStart) < frameBudget)
                {
                    var (grid, pos) = cells[i++];

                    // ---------------------------
                    // GATING
                    // ---------------------------
                    if (_permanentClearCells.Contains(grid)) continue;
                    if (IsKeepClearCell(grid)) continue;
                    // Skip cells already queued for void-growth (e.g. vehicle trap ring spawned
                    // via onBeforeGrowth callback). VoidGrow flag is set synchronously so
                    // this check is race-free regardless of coroutine scheduling order.
                    if (HasCellFlag(grid, CellFlags.VoidGrow)) continue;
                    if (TryGetCellState(grid, out var existSt) &&
                        (existSt == DustCellState.Solid || existSt == DustCellState.Regrowing)) continue;
                    if (IsDustSpawnBlocked(grid)) continue;
                    // IMPORTANT
                    // // During startup maze bootstrap, cells blocked by a vehicle should remain
                    // unspawned/neutral, not be pushed into the generic regrow pipeline.
                    // Otherwise they later come back through CommitRegrowCell() and get assigned
                    // a musical role (plurality / least-dense), which is the wrong behavior for
                    // initial maze fill.
                    if (IsVehicleOverlappingCell(grid)) {
                        if (_isBootstrappingMaze)
                            continue;
                        RequestRegrowCellAt(grid, refreshIfPending: true);
                        continue;
                    }                    // ---------------------------
                    // SPAWN + REGISTER (VISUAL FIRST)
                    // ---------------------------
                    var hex = GetOrCreateCellGO(grid);
                    if (hex.TryGetComponent<CosmicDust>(out var dust))
                    {
                        dust.SetTrackBundle(this, drums);
                        dust.SetCellSizeDrivenScale(cellWorldSize, config.dustFootprintMul, cellClearanceWorld);

                        dust.PrepareForReuse();
                        dust.InitializeVisuals(DustTimings);
                        dust.SetGrowInDuration(config.hexGrowInSeconds);

                        // GetCellVisualColor reads from _imprints if available, otherwise config.mazeTint.
                        Color cellColor = GetCellVisualColor(grid);
                        var resistance = ResolveResistanceProfile(grid, MusicalRole.None, context: "SpawnDust");
                        dust.clearing.drainResistance01 = resistance.drainResistance01;

                        // Apply role AND color together so dust.Role is set from birth.
                        // SetTint alone leaves dust.Role = None, which means RetintExisting
                        // cannot distinguish role-colored cells from plain maze cells and
                        // would overwrite them with the flat config.mazeTint (gray).
                        if (_imprints != null && _imprints.TryGetValue(grid, out var spawnImprint)
                            && spawnImprint.role != MusicalRole.None)
                        {
                            dust.ApplyRoleAndCharge(spawnImprint.role, cellColor, 1f);
                        }
                        else
                        {
                            // Gray start: initial cells spawn with no role (MusicalRole.None) and maze tint.
                            // Roles are earned dynamically through vehicle carving + regrowth.
                            // dust.Role must be None here so TickDrain's Role guard treats these as
                            // inert gray cells and the star cannot drain them while dormant.
                            // Full charge (1f) — plow energy system requires non-zero units to chip.
                            dust.ApplyRoleAndCharge(MusicalRole.None, cellColor, 1f);
                            _imprints.ApplyHiddenHintToDust(grid, dust);
                        }

                        // Use the role's shadow color as the deny feedback color.
                        // dustColors.denyColor may be unset on assets; shadowColor is the
                        // authored "darkened role memory" hue and is more reliably set.
                        Color denyColor = Color.darkGray;
                        if (_imprints != null && _imprints.TryGetValue(grid, out var imp) && imp.role != MusicalRole.None)
                        {
                            var rp = MusicalRoleProfileLibrary.GetProfile(imp.role);
                            if (rp != null)
                            {
                                var shadow = rp.dustColors.shadowColor;
                                // Only use shadow if it's meaningfully dark (not unset black or magenta).
                                denyColor = (shadow != Color.clear && shadow != Color.magenta)
                                    ? shadow
                                    : Color.darkGray;
                            }
                        }
                        dust.SetFeedbackColors(Color.white, denyColor);
                        dust.Begin();

                        // Critical: keep collider OFF during bulk topology changes.
                        // PrepareForReuse already disables the collider; this is a safety guard for
                        // edge cases. regrowAlphaCapped is intentionally NOT set here — Begin() already
                        // set sprite alpha to _currentTint.a, and the physics phase restores that same
                        // value, so no cap is needed (and setting it would cause a visible pop on lift).
                        SetDustCollision(dust, false);
                        spawnedDust.Add(dust);
                    }

                    // Register into the authoritative grid (during bulk) without forcing per-cell composite rebuilds.
                    RegisterHex(grid, hex);
                }

                // Pacing
                float elapsedSinceLast = Time.realtimeSinceStartup - lastPacedAt;
                if (elapsedSinceLast < deadlineStep)
                    yield return new WaitForSeconds(deadlineStep - elapsedSinceLast);
                else
                    yield return null;

                lastPacedAt = Time.realtimeSinceStartup;
            }

            // --------------------------------------------------------------------
            // PHYSICS PHASE: re-enable colliders gradually
            // --------------------------------------------------------------------
            int j = 0;
            while (j < spawnedDust.Count)
            {
                float frameStart  = Time.realtimeSinceStartup;
                float frameBudget = Mathf.Max(0f, config.maxSpawnMillisPerFrame) / 1000f;

                while (j < spawnedDust.Count && (Time.realtimeSinceStartup - frameStart) < frameBudget)
                {
                    var d = spawnedDust[j++];
                    if (d == null) continue;

// Resolve which cell this dust belongs to (authoritative mapping).
                    if (!_registry.GoToCell.TryGetValue(d.gameObject, out var gp))
                        continue;

// Only enable collision if the generator still considers this cell solid.
                    if (!TryGetCellState(gp, out var st) || st != DustCellState.Solid)
                        continue;

// Respect DustClaimManager / exclusion vetoes (PhaseStar pocket, etc.)
                    if (IsKeepClearCell(gp) || IsDustSpawnBlocked(gp))
                        continue;

// Never enable a collider on top of a vehicle — hand off to the step-gate retry path.
                    if (IsVehicleOverlappingCell(gp))
                    {
                        SetCellState(gp, DustCellState.PendingRegrow);
                        FadeAndHideCellGO(d.gameObject);
                        EnqueueStepRegrow(gp);
                        continue;
                    }

// At this point, it is legitimately solid terrain.
                    // SetTerrainColliderEnabled(true) now restores _currentTint.a directly, so
                    // regrowAlphaCapped and EnsureMinSolidAlpha are not needed here.
                    SetDustCollision(d, true);
                }

                yield return null;
            }

            // No composite collider to rebuild.
        }
        finally
        {

        }
    }

    private void RegisterHex(Vector2Int gridPos, GameObject hex)
    {
        // Ensure the authoritative grid is ready.
        EnsureCellGrid();

        // If something already occupies this cell, hide it (no pooling).
        if (TryGetCellGo(gridPos, out var existing) && existing != null && existing != hex)
        {
            _registry.GoToCell.Remove(existing);
            if (existing.TryGetComponent<CosmicDust>(out var exDust) && exDust != null)
                SetDustCollision(exDust, false);

            HideCellGO(existing);
        }

        // Register in the authoritative grid.
        if (IsInBounds(gridPos))
        {
            _gridState.CellGo[gridPos.x, gridPos.y] = hex;
            var d = hex != null ? hex.GetComponent<CosmicDust>() : null;
            _gridState.CellDust[gridPos.x, gridPos.y] = d;
            if (hex != null) _registry.GoToCell[hex] = gridPos;
            SetCellState(gridPos, DustCellState.Solid);
        }

        // No composite collider rebuild (per-cell terrain only).
    }

    private List<Vector2Int> GetHexDirections(int row)
    {
        // Even-q offset coordinates
        return row % 2 == 0 ? new List<Vector2Int>
        {
            new(1, 0), new(0, 1), new(-1, 1),
            new(-1, 0), new(-1, -1), new(0, -1)
        } : new List<Vector2Int>
        {
            new(1, 0), new(1, 1), new(0, 1),
            new(-1, 0), new(0, -1), new(1, -1)
        };
    }
}
