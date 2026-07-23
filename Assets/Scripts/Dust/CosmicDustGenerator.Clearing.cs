using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public partial class CosmicDustGenerator
{
    public void ClearCell(Vector2Int gp, DustClearMode mode, float fadeSeconds, bool scheduleRegrow, DustClearSource source = DustClearSource.System, float regrowDelaySeconds = -1f, bool runPreExplode = false, Color? explosionTintOverride = null, Vector2 burstDirection = default, float preExplodeScaleOutSeconds = -1f) {
        if (!TryGetCellState(gp, out var st)) return;
        if (st == DustCellState.Empty || st == DustCellState.Clearing || st == DustCellState.PendingRegrow) {
            // Optionally refresh regrow timer even if already empty.
            if (scheduleRegrow)
                RequestRegrowCellAt(gp, ResolveRegrowDelay(gp, source, regrowDelaySeconds), refreshIfPending: true);
            return;
        }
        if (!TryGetCellGo(gp, out var go) || go == null) {
            SetCellState(gp, DustCellState.Empty);
            if (scheduleRegrow)
                RequestRegrowCellAt(gp, ResolveRegrowDelay(gp, source, regrowDelaySeconds), refreshIfPending: true);
            return;
        }

        // Immediately stop being terrain.
        SetCellState(gp, DustCellState.Clearing);
        if (go.TryGetComponent<CosmicDust>(out var dust) && dust != null){
           SetDustCollision(dust, false);
        }

         // Visual policy
         if (dust != null) {
             if (runPreExplode)
             {
                 var explode = go.GetComponentInChildren<Explode>(true);
                 if (explode != null)
                 {
                     var tint = explosionTintOverride ?? dust.CurrentTint;
                     tint.a = 1f;
                     explode.SetTint(tint);
                     // Always set the direction: zero clears stale state left on this
                     // pooled instance by a previous directed burst.
                     explode.SetBurstDirection(burstDirection);
                     explode.ZapExplode();
                 }
             }

             if (mode == DustClearMode.HideInstant){
                 if (runPreExplode)
                     StartCoroutine(DeferredHideAfterPreExplode(gp, dust));
                 else
                 {
                     dust.HideVisualsInstant();
                     SetCellState(gp, DustCellState.Empty);
                 }
             }
             else {
                if (runPreExplode)
                    StartCoroutine(DeferredDissipateAfterPreExplode(dust, preExplodeScaleOutSeconds > 0f ? preExplodeScaleOutSeconds : DustTimings.clearSpriteScaleOutSeconds));
                else
                    dust.DissipateAndHideVisualOnly(Mathf.Max(0.01f, fadeSeconds));
                // OnDustVisualFadedOut will finalize Empty + hide visuals
             }
         }
         else { // Fallback: no CosmicDust component
             var col = go.GetComponent<Collider2D>();
         if (col) col.enabled = false;
         FadeAndHideCellGO(go);
         SetCellState(gp, DustCellState.Empty);
         }

         if (scheduleRegrow)
         {
             RequestRegrowCellAt(gp, ResolveRegrowDelay(gp, source, regrowDelaySeconds), refreshIfPending: true);
             // TODO: density conservation — fill a frontier cell when one is eroded so total
             // coverage stays constant. Deferred: TryQueueFrontierCompensation() interfered with
             // role-assignment logic when multiple stars were active. Re-enable only after
             // role-aware frontier selection is in place.
//             TryQueueFrontierCompensation();
         }
    }
    private IEnumerator DeferredDissipateAfterPreExplode(CosmicDust dust, float fadeSeconds)
    {
        yield return null;
        if (dust == null) yield break;
        dust.DissipateAndHideVisualOnly(fadeSeconds);
    }

    private IEnumerator DeferredHideAfterPreExplode(Vector2Int gp, CosmicDust dust)
    {
        yield return null;
        if (dust == null) yield break;
        dust.HideVisualsInstant();
        SetCellState(gp, DustCellState.Empty);
    }

    // Carve mode (Vehicle/MineNode):
    // - Role imprint changes are allowed before clearing (e.g. MineNode paint restore/promote).
    // - Regrow is normally scheduled, except for permanent clear systems that explicitly disable it.
    // - Void-grown exception applies: vehicle carve removes void-grow imprint so regrow can re-resolve role.
    // - Visual fade duration is caller-provided (resistance/tuning aware).
    public void CarveCellPreserveGray(Vector2Int cell, float fadeSeconds, DustClearSource source, float regrowDelaySeconds = -1f, bool runPreExplode = false)
    {
        if (!IsInBounds(cell)) return;
        SetCellFlag(cell, CellFlags.ForceGrayRegrow);
        CarveCell(cell, fadeSeconds, scheduleRegrow: true, source: source, regrowDelaySeconds: regrowDelaySeconds, runPreExplode: runPreExplode);
    }

    public void CarveCell(Vector2Int cell, float fadeSeconds, bool scheduleRegrow = true, DustClearSource source = DustClearSource.VehiclePlow, float regrowDelaySeconds = -1f, bool runPreExplode = true)
    {
        var req = new DustClearRequest(DustInteractionMode.Carve, DustClearMode.FadeAndHide, fadeSeconds, scheduleRegrow, source, regrowDelaySeconds, runPreExplode);
        ClearCellByInteraction(cell, req);
    }

    private void ClearCellByInteraction(Vector2Int cell, in DustClearRequest request)
    {
        if (!IsInBounds(cell)) return;
        if (!TryGetCellState(cell, out var st) || st != DustCellState.Solid) return;

        ClearCell(
            cell,
            request.ClearMode,
            request.FadeSeconds,
            request.ScheduleRegrow,
            source: request.Source,
            regrowDelaySeconds: request.RegrowDelaySeconds,
            runPreExplode: request.RunPreExplode,
            explosionTintOverride: request.ExplosionTintOverride,
            burstDirection: request.BurstDirection,
            preExplodeScaleOutSeconds: request.PreExplodeScaleOutSeconds);
    }

    /// <summary>
    /// Zap-clear a cell whose energy is being consumed by a PhaseStar: no regrow is
    /// scheduled and the cell is held empty until <see cref="ReleaseHeldCells"/>.
    /// </summary>
    public void ZapClearCellHeld(Vector2Int cell, Color? explosionTint = null, Vector2 burstDirection = default)
    {
        if (!IsInBounds(cell)) return;
        if (!TryGetCellState(cell, out var st) || st != DustCellState.Solid) return;

        SetCellFlag(cell, CellFlags.ZapForceGray);
        var req = new DustClearRequest(DustInteractionMode.Zap, DustClearMode.FadeAndHide, config.zapFadeSeconds, false, DustClearSource.StarDrain, -1f, true, explosionTint, burstDirection, config.zapScaleOutSeconds);
        ClearCellByInteraction(cell, req);
        _heldRegrowCells.Add(cell);
    }

    /// <summary>
    /// Release cells previously held by <see cref="ZapClearCellHeld"/> and schedule their
    /// regrow (StarDrain delay unless delayOverride >= 0). Cells not currently held are skipped.
    /// </summary>
    public void ReleaseHeldCells(IReadOnlyList<Vector2Int> cells, float delayOverride = -1f)
    {
        if (cells == null) return;
        for (int i = 0; i < cells.Count; i++)
        {
            var c = cells[i];
            if (!_heldRegrowCells.Remove(c)) continue;
            RequestRegrowCellAt(c, ResolveRegrowDelay(c, DustClearSource.StarDrain, delayOverride), refreshIfPending: true);
        }
    }

    private void DespawnDustAtAndMarkPermanent(Vector2Int gridPos)
    {
        bool wasAlreadyPermanent = _permanentClearCells.Contains(gridPos);
        _permanentClearCells.Add(gridPos);

        DespawnDustAt(gridPos);
        _regrow?.CancelRegrow(gridPos);
    }

    private void RemoveActiveAt(Vector2Int grid, GameObject go) {
        // Logical authority: the moment a cell is cleared, it stops contributing to the maze.
        SetCellState(grid, DustCellState.Empty);

        // Drop from the reverse-lookup map.
        if (go != null)
            _registry.GoToCell.Remove(go);

        // No pooling: hide immediately. Any fade-out behavior should be handled by CosmicDust.
        if (go != null)
        {
            if (go.TryGetComponent<CosmicDust>(out var dust) && dust != null)
                SetDustCollision(dust, false);
            HideCellGO(go);
        }

        // Diffusion prep: removing dust can expose the base tint and create hard seams.
        MarkTintDirty(grid, config.tintDirtyMarkRadius);

        // (no composite collider rebuild)
    }
    private void HideCellGO(GameObject go)
    {
        if (!go) return;

        if (go.TryGetComponent<CosmicDust>(out var dust) && dust != null)
        {
            SetDustCollision(dust, false);
            dust.regrowAlphaCapped = false;
            dust.HideVisualsInstant();
        }
    }
    private void FadeAndHideCellGO(GameObject go)
    {
        if (!go) return;

        if (go.TryGetComponent<CosmicDust>(out var dust) && dust != null)
        {
            SetDustCollision(dust, false);
            dust.regrowAlphaCapped = false;
            dust.DissipateAndHideVisualOnly(.5f);
        }
        else
        {
            HideCellGO(go);
        }
    }

    private void DespawnDustAt(Vector2Int gridPos)
    {
        if (_permanentClearCells.Contains(gridPos)) return;

        // Immediate logical clear (no fade). This is used for hard removals
        // (eg. collectable pockets) where we want topology open immediately.
        if (TryGetCellGo(gridPos, out var go) && go != null)
        {
            if (go.TryGetComponent<CosmicDust>(out var dust) && dust != null)
                SetDustCollision(dust, false);
            HideCellGO(go);
        }

        SetCellState(gridPos, DustCellState.Empty);

        // CRITICAL: schedule regrow.
        RequestRegrowCellAt(gridPos, refreshIfPending: true);
    }
}
