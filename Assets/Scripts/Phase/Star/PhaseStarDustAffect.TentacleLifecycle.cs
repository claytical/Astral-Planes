using UnityEngine;

public partial class PhaseStarDustAffect
{
    private void TickTentacle(Tentacle tentacle, float dt)
    {
        if (_gfm == null) _gfm = GameFlowManager.Instance;
        var gen    = _gfm?.dustGenerator;
        var drum   = _gfm?.activeDrumTrack;
        Vector2 starPos = transform.position;

        switch (tentacle.state)
        {
            case TentacleState.Idle:
            {
                break;
            }

            case TentacleState.Growing:
            {
                if (drum != null)
                    tentacle.targetWorldPos = drum.GridToWorldPosition(tentacle.targetCell);

                if (!TryValidateOrHoldTarget(tentacle, dt, out var invalidReason))
                {
                    if (_navigator != null && drum != null && _navigator.TryGetTargetForRole(tentacle.role, out var newCell) &&
                        newCell != tentacle.targetCell && IsTargetValid(newCell, tentacle.role, tentacle, out _) && TryReserveCell(tentacle, newCell))
                    {
                        tentacle.targetCell      = newCell;
                        tentacle.targetWorldPos  = drum.GridToWorldPosition(newCell);
                        ResetInvalidTargetRetry(tentacle);
                    }
                    else
                    {
                        BeginRetractingTentacle(tentacle, starPos, invalidReason);
                        break;
                    }
                }

                tentacle.growProgress = Mathf.Clamp01(tentacle.growProgress + tentacleGrowSpeed * dt);
                UpdateTentacleLine(tentacle, starPos, dt);

                if (tentacle.growProgress >= 1f)
                {
                    tentacle.tipPos      = tentacle.targetWorldPos;
                    TransitionTentacleState(tentacle, TentacleState.Draining, "reached target");
                    tentacle.contactTimer = 0f;

                    if (!tentacle.notifiedDrainLock)
                    {
                        _navigator?.NotifyDraining(tentacle.targetCell);
                        tentacle.notifiedDrainLock = true;
                    }
                }

                break;
            }

            case TentacleState.Draining:
            {
                if (!TryValidateOrHoldTarget(tentacle, dt, out var invalidReason))
                {
                    BeginRetractingTentacle(tentacle, starPos, invalidReason);
                    break;
                }

                tentacle.tipPos          = tentacle.targetWorldPos;
                tentacle.drainFlashTimer = Mathf.Max(0f, tentacle.drainFlashTimer - dt);
                TickSiphon(tentacle, dt);

                UpdateTentacleLine(tentacle, starPos, dt);

                tentacle.contactTimer += dt;
                if (tentacle.contactTimer >= minContactTime)
                {
                    if (!tentacle.buildupStarted)
                    {
                        tentacle.buildupStarted = true;
                        // Cell HP is drain-time flavor: harder cells take proportionally
                        // longer to drain, but every cell still counts as exactly one zap.
                        tentacle.effectiveBuildupSeconds = drainBuildupSeconds;
                        if (gen != null &&
                            gen.TryGetDustAt(tentacle.targetCell, out var buildupDust) && buildupDust != null)
                        {
                            tentacle.effectiveBuildupSeconds =
                                drainBuildupSeconds * Mathf.Max(1, buildupDust.maxEnergyUnits);
                            if (tentacle.effectiveBuildupSeconds > 0f)
                                buildupDust.BeginDrainBuildup(tentacle.effectiveBuildupSeconds);
                        }
                    }

                    if (tentacle.contactTimer >= minContactTime + tentacle.effectiveBuildupSeconds)
                        HandleDrainContactAndClear(tentacle, dt, starPos);
                }

                break;
            }

            case TentacleState.Retracting:
            {
                tentacle.tipPos = Vector2.MoveTowards(tentacle.tipPos, starPos, tentacleRetractSpeed * dt);
                tentacle.targetWorldPos = tentacle.tipPos;
                UpdateTentacleLine(tentacle, starPos, dt);

                if (Vector2.Distance(tentacle.tipPos, starPos) < 0.05f)
                {
                    TransitionTentacleState(tentacle, TentacleState.Idle, "fully retracted");
                    tentacle.line.enabled = false;
                    TryNotifyAllTentaclesRetracted();
                }

                break;
            }

            case TentacleState.Dissolving:
            {
                ReleaseDrainLock(tentacle);
                ReleaseReservation(tentacle, tentacle.targetCell);

                tentacle.dissolveTimer  += dt;
                tentacle.drainFlashTimer = Mathf.Max(0f, tentacle.drainFlashTimer - dt);
                TickSiphon(tentacle, dt);
                tentacle.alphaScale      = Mathf.Clamp01(1f - tentacle.dissolveTimer / dissolveDuration);
                tentacle.tipPos          = tentacle.targetWorldPos;
                UpdateTentacleLine(tentacle, starPos, dt);

                if (tentacle.dissolveTimer >= dissolveDuration)
                {
                    TransitionTentacleState(tentacle, TentacleState.Idle, "dissolve complete");
                    tentacle.alphaScale   = 1f;
                    tentacle.dissolveTimer = 0f;
                    tentacle.growProgress = 0f;
                    tentacle.line.enabled = false;
                    tentacle.line.widthMultiplier = tentacleWidth;
                }

                break;
            }
        }
    }

    private void BeginGrowingTentacle(Tentacle tentacle, Vector2Int cell, DrumTrack drum, Vector2 starPos)
    {
        if (tentacle.targetCell != cell)
            ReleaseReservation(tentacle, tentacle.targetCell);

        tentacle.targetCell     = cell;
        tentacle.targetWorldPos = drum.GridToWorldPosition(cell);
        tentacle.tipPos         = starPos;
        tentacle.growProgress   = 0f;
        tentacle.contactTimer   = 0f;
        tentacle.dissolveTimer  = 0f;
        tentacle.alphaScale     = 1f;
        tentacle.notifiedDrainLock = false;
        tentacle.clearStarted = false;
        tentacle.clearTimer = 0f;
        tentacle.clearDuration = 0f;
        tentacle.buildupStarted = false;
        tentacle.effectiveBuildupSeconds = 0f;
        tentacle.siphonActive = false;
        tentacle.siphonT = 0f;

        Vector3 root3 = new Vector3(starPos.x, starPos.y, transform.position.z);
        for (int i = 0; i < SplinePoints; i++)
            tentacle.linePts[i] = root3;

        TransitionTentacleState(tentacle, TentacleState.Growing, "acquired target");
        tentacle.line.enabled = true;
        UpdateTentacleLine(tentacle, starPos, 0f);
    }

    // Advances the siphon energy packet from tip toward root; on arrival, fires the
    // existing root drain flash as the "energy absorbed by star" beat.
    private void TickSiphon(Tentacle tentacle, float dt)
    {
        if (!tentacle.siphonActive) return;

        tentacle.siphonT -= dt / Mathf.Max(0.05f, siphonTravelSeconds);
        if (tentacle.siphonT <= 0f)
        {
            tentacle.siphonActive = false;
            tentacle.siphonT = 0f;
            tentacle.drainFlashTimer = DrainFlashDuration;
        }
    }

    private void HandleDrainContactAndClear(Tentacle tentacle, float dt, Vector2 starPos)
    {
        if (_gfm == null) _gfm = GameFlowManager.Instance;
        var gen = _gfm?.dustGenerator;

        if (!tentacle.clearStarted)
        {
            tentacle.clearStarted = true;
            tentacle.clearTimer = 0f;
            tentacle.clearDuration = Mathf.Max(0.01f, dissolveDuration);

            if (gen != null && gen.TryGetDustAt(tentacle.targetCell, out var dust) && dust != null)
            {
                // Capture the vivid role tint BEFORE ChipEnergy lerps it toward gray,
                // so the clear explosion fires in the cell's true color.
                Color preDrainTint = dust.CurrentTint;
                // Drain the cell's full remaining energy — one cell = one zap; the cell's
                // HP already paid its cost as drain time.
                int drainedUnits = dust.ChipEnergy(dust.maxEnergyUnits);
                if (drainedUnits > 0)
                {
                    // Drained energy is held by the MineNode the star builds from it;
                    // the cell regrows when that node dies (or the star is destroyed first).
                    Vector2 burstDir = starPos - tentacle.targetWorldPos; // blow toward the star
                    gen.ZapClearCellHeld(tentacle.targetCell, preDrainTint, burstDir);
                    _star?.RegisterHeldDrainCell(tentacle.targetCell);
                    tentacle.siphonActive = true;
                    tentacle.siphonT = 1f;
                    CreditZap(tentacle, drainedUnits);
                }
            }
        }

        tentacle.clearTimer += dt;
        if (tentacle.clearTimer >= tentacle.clearDuration)
            BeginRetractingTentacle(tentacle, starPos, "dust faded out");
    }

    // Credits the zap the moment the cell is consumed. The siphon/retraction that follows
    // is purely visual — a disarm or dissolve mid-siphon can no longer lose a paid-for zap.
    private void CreditZap(Tentacle tentacle, int drainedUnits)
    {
        if (_star != null)
        {
            bool wasUnattuned = _star.AttunedRole == MusicalRole.None;
            onDelivery?.Invoke(tentacle.role, drainedUnits);
            if (wasUnattuned)
                OnAttuned?.Invoke(tentacle.role);
        }

        tentacle.drainFlashTimer = DrainFlashDuration;
        Vector2Int zappedCell = tentacle.targetCell;
        _zappedThisCycle.Add(zappedCell);
        _navigator?.NotifyCellZappedThisCycle(zappedCell);
        _navigator?.ClearLockOn(zappedCell);
        tentacle.notifiedDrainLock = false;
        _star?.OnTentacleZapResolved(tentacle.role, zappedCell);
    }

    private void BeginRetractingTentacle(Tentacle tentacle, Vector2 starPos, string reason)
    {
        // Buildup started but the drain never fired (target lost/invalidated):
        // restore the cell's normal scale and tint. If the clear started, the
        // clear visuals own the cell — leave it alone.
        if (tentacle.buildupStarted && !tentacle.clearStarted)
        {
            var gen = _gfm?.dustGenerator;
            if (gen != null && gen.TryGetDustAt(tentacle.targetCell, out var buildupDust) && buildupDust != null)
                buildupDust.CancelDrainBuildup();
        }
        tentacle.buildupStarted = false;

        ReleaseDrainLock(tentacle);
        ReleaseReservation(tentacle, tentacle.targetCell);

        tentacle.tipPos = tentacle.targetWorldPos;
        tentacle.contactTimer = 0f;
        tentacle.targetWorldPos = tentacle.tipPos;

        TransitionTentacleState(tentacle, TentacleState.Retracting, reason);
        tentacle.line.enabled = true;
        UpdateTentacleLine(tentacle, starPos, 0f);
    }

    private void TransitionTentacleState(Tentacle tentacle, TentacleState nextState, string reason)
    {
        if (tentacle.state == nextState) return;
        tentacle.state = nextState;
        ResetInvalidTargetRetry(tentacle);
    }

    private void ResetTentacleState(Tentacle tentacle, Vector2 starPos, bool destroyVisual)
    {
        ReleaseDrainLock(tentacle);
        ReleaseReservation(tentacle, tentacle.targetCell);
        TransitionTentacleState(tentacle, TentacleState.Idle, "reset");
        tentacle.tipPos       = starPos;
        tentacle.growProgress = 0f;
        tentacle.contactTimer = 0f;
        tentacle.dissolveTimer = 0f;
        tentacle.alphaScale   = 1f;
        tentacle.drainFlashTimer = 0f;
        tentacle.clearStarted = false;
        tentacle.clearTimer = 0f;
        tentacle.clearDuration = 0f;
        tentacle.buildupStarted = false;
        tentacle.effectiveBuildupSeconds = 0f;
        tentacle.siphonActive = false;
        tentacle.siphonT = 0f;

        if (tentacle.line != null)
        {
            if (destroyVisual)
                Destroy(tentacle.line.gameObject);
            else
            {
                tentacle.line.enabled         = false;
                tentacle.line.widthMultiplier = tentacleWidth;
            }
        }
    }

    private void ReleaseDrainLock(Tentacle tentacle)
    {
        if (!tentacle.notifiedDrainLock) return;
        _navigator?.ClearLockOn(tentacle.targetCell);
        tentacle.notifiedDrainLock = false;
    }

    private void TryNotifyAllTentaclesRetracted()
    {
        if (!_isRetractAllInProgress)
            return;

        foreach (var tentacle in _tentacles)
        {
            if (tentacle.state != TentacleState.Idle)
                return;

            if (tentacle.line != null && tentacle.line.enabled)
                return;
        }

        _isRetractAllInProgress = false;
        OnAllTentaclesRetracted?.Invoke();
    }
}
