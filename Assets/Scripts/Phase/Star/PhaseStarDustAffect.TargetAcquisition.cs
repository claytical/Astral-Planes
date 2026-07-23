using System.Collections.Generic;
using UnityEngine;

public partial class PhaseStarDustAffect
{
    /// <summary>
    /// Tentacles currently holding a slot of the zap budget: growing/draining toward a
    /// cell. Retracting/dissolving tentacles hold nothing — their zap was already
    /// credited at drain-clear time, so RemainingZapCount reflects it.
    /// </summary>
    private int CountZapBudgetInFlight()
    {
        int n = 0;
        foreach (var tentacle in _tentacles)
        {
            if (tentacle.role != _attunedRole) continue;
            if (tentacle.state == TentacleState.Growing || tentacle.state == TentacleState.Draining)
                n++;
        }
        return n;
    }

    private void AssignIdleTentacleTargets()
    {
        if (!_acquisitionEnabled || _navigator == null || _attunedRole == MusicalRole.None)
            return;
        if (_gfm == null) _gfm = GameFlowManager.Instance;
        var drum = _gfm?.activeDrumTrack;
        if (drum == null) return;

        // Zap budget: one live tentacle per note still missing from the node payload.
        // Each available carved cell grows a tentacle immediately, up to that cap —
        // carve 3 bass cells against a 5-note payload and 3 tentacles grow at once.
        int remainingZaps = _star != null ? _star.RemainingZapCount : int.MaxValue;
        int budget = remainingZaps - CountZapBudgetInFlight();
        if (budget <= 0) return;

        // Optional pacing between successive activations (0 = immediate).
        if (interTentacleStartInterval > 0f && Time.time - _lastTentacleStartTime < interTentacleStartInterval)
            return;

        var idleTentacles = new List<Tentacle>();
        foreach (var tentacle in _tentacles)
        {
            if (tentacle.state == TentacleState.Idle && tentacle.role == _attunedRole)
                idleTentacles.Add(tentacle);
        }
        if (idleTentacles.Count == 0) return;

        var excluded = BuildExcludedCells(requester: null);
        var seededTargets = new List<Vector2Int>(budget);
        if (_navigator.TryGetTargetsForRole(_attunedRole, budget, excluded, out var targetCells) && targetCells != null)
            seededTargets.AddRange(targetCells);

        int targetCursor = 0;
        foreach (var tentacle in idleTentacles)
        {
            if (budget <= 0) break;

            Vector2Int cell;
            bool activated = false;

            while (targetCursor < seededTargets.Count)
            {
                cell = seededTargets[targetCursor++];
                if (!IsTargetValid(cell, tentacle.role, tentacle, out _))
                    continue;
                if (!TryReserveCell(tentacle, cell))
                    continue;
                BeginGrowingTentacle(tentacle, cell, drum, transform.position);
                _lastTentacleStartTime = Time.time;
                activated = true;
                break;
            }

            if (!activated &&
                _navigator.TryGetTargetForRole(tentacle.role, out cell) &&
                IsTargetValid(cell, tentacle.role, tentacle, out _) &&
                TryReserveCell(tentacle, cell))
            {
                BeginGrowingTentacle(tentacle, cell, drum, transform.position);
                _lastTentacleStartTime = Time.time;
                activated = true;
            }

            if (!activated) break;           // no valid target left anywhere
            budget--;

            if (interTentacleStartInterval > 0f)
                break;                       // paced mode: one activation per interval
        }
    }

    private bool TryValidateOrHoldTarget(Tentacle tentacle, float dt, out string invalidReason)
    {
        if (IsTargetValid(tentacle.targetCell, tentacle.role, tentacle, out invalidReason))
        {
            ResetInvalidTargetRetry(tentacle);
            return true;
        }

        tentacle.invalidTargetFrames++;
        tentacle.invalidTargetMs += dt * 1000f;
        bool withinFrameGrace = tentacle.invalidTargetFrames < invalidTargetRetryFrames;
        bool withinTimeGrace = tentacle.invalidTargetMs < invalidTargetRetryMs;
        return withinFrameGrace || withinTimeGrace;
    }

    private void ResetInvalidTargetRetry(Tentacle tentacle)
    {
        tentacle.invalidTargetFrames = 0;
        tentacle.invalidTargetMs = 0f;
    }

    private bool IsTargetValid(Vector2Int cell, MusicalRole role, Tentacle requester, out string reason)
    {
        if (_gfm == null) _gfm = GameFlowManager.Instance;
        var gen = _gfm?.dustGenerator;
        if (gen == null)
        {
            reason = "missing generator";
            return false;
        }

        if (!gen.HasDustAt(cell))
        {
            reason = "HasDustAt false";
            return false;
        }

        if (!gen.TryGetDustAt(cell, out var dust) || dust == null)
        {
            reason = "missing dust";
            return false;
        }

        if (dust.Role != role)
        {
            reason = "role mismatch";
            return false;
        }

        if (dust.currentEnergyUnits <= 0)
        {
            reason = "energy 0";
            return false;
        }

        if (!dust.IsVisuallyPresentForTargeting())
        {
            reason = "visually hidden";
            return false;
        }

        if (IsReservedByAnotherTentacle(cell, requester))
        {
            reason = "reserved by another tentacle";
            return false;
        }

        if (_zappedThisCycle.Contains(cell) || (_navigator != null && _navigator.WasCellZappedThisCycle(cell)))
        {
            reason = "already zapped this cycle";
            return false;
        }

        reason = "";
        return true;
    }

    private bool IsReservedByAnotherTentacle(Vector2Int cell, Tentacle requester)
        => _reservedCells.TryGetValue(cell, out var owner) && owner != null && owner != requester;

    private bool TryReserveCell(Tentacle tentacle, Vector2Int cell)
    {
        if (IsReservedByAnotherTentacle(cell, tentacle))
            return false;

        _reservedCells[cell] = tentacle;
        _navigator?.ReserveCell(cell);
        return true;
    }

    private void ReleaseReservation(Tentacle tentacle, Vector2Int cell)
    {
        if (_reservedCells.TryGetValue(cell, out var owner) && owner == tentacle)
            _reservedCells.Remove(cell);
        _navigator?.ReleaseCellReservation(cell);
    }

    private HashSet<Vector2Int> BuildExcludedCells(Tentacle requester)
    {
        var excluded = new HashSet<Vector2Int>();
        foreach (var kv in _reservedCells)
        {
            if (requester == null || (kv.Value != null && kv.Value != requester))
                excluded.Add(kv.Key);
        }

        foreach (var zappedCell in _zappedThisCycle)
            excluded.Add(zappedCell);

        return excluded;
    }
}
