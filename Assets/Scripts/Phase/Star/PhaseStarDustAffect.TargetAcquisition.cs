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

        // Perf-only throttle: skip the idle-tentacle/grid-scan work entirely if we just
        // did it. Independent of interTentacleStartInterval (a player-visible pacing knob).
        if (Time.time < _nextAcquisitionScanAt)
            return;

        if (_gfm == null) _gfm = GameFlowManager.Instance;
        var drum = _gfm?.activeDrumTrack;
        var gen = _gfm?.dustGenerator;
        if (drum == null || gen == null) return;

        // Nothing of this role has bloomed yet: skip the exclusion set and reach BFS entirely
        // (stars wait up front for every role, so this is the common case).
        if (gen.FlowerCount(_attunedRole) == 0) return;

        // Zap budget: one live tentacle per note still missing from the node payload.
        // Each available carved cell grows a tentacle immediately, up to that cap —
        // carve 3 bass cells against a 5-note payload and 3 tentacles grow at once.
        int remainingZaps = _star != null ? _star.RemainingZapCount : int.MaxValue;
        int budget = remainingZaps - CountZapBudgetInFlight();
        if (budget <= 0) return;

        // Optional pacing between successive activations (0 = immediate).
        if (interTentacleStartInterval > 0f && Time.time - _lastTentacleStartTime < interTentacleStartInterval)
            return;

        _nextAcquisitionScanAt = Time.time + kAcquisitionScanCooldown;

        var idleTentacles = _idleTentaclesScratch;
        idleTentacles.Clear();
        foreach (var tentacle in _tentacles)
        {
            if (tentacle.state == TentacleState.Idle && tentacle.role == _attunedRole)
                idleTentacles.Add(tentacle);
        }
        if (idleTentacles.Count == 0) return;

        var excluded = BuildExcludedCells(requester: null);
        var seededTargets = _seededTargetsScratch;
        seededTargets.Clear();
        // A few spares past the budget, so a flower that fails the visual check doesn't leave a
        // tentacle idle while another flower is available. Ranked open-path-first (navigator).
        _navigator.TryGetTargetsForRole(_attunedRole, budget + 4, excluded, seededTargets);

        Vector2 starPos = transform.position;
        foreach (var cell in seededTargets)
        {
            if (budget <= 0 || idleTentacles.Count == 0) break;

            // The bud pointing closest to this flower grows out toward it.
            Vector2 toTarget = (Vector2)drum.GridToWorldPosition(cell) - starPos;
            float targetAngle = Mathf.Atan2(toTarget.y, toTarget.x) * Mathf.Rad2Deg;
            int bestIdx = 0;
            float bestDiff = float.MaxValue;
            for (int i = 0; i < idleTentacles.Count; i++)
            {
                var t = idleTentacles[i];
                float diff = t.budVisible ? Mathf.Abs(Mathf.DeltaAngle(t.budAngle, targetAngle)) : 360f;
                if (diff < bestDiff) { bestDiff = diff; bestIdx = i; }
            }
            var tentacle = idleTentacles[bestIdx];

            if (!IsTargetValid(cell, tentacle.role, tentacle, out var rejectReason))
            {
                LogTargetReject(cell, rejectReason);
                continue;
            }
            if (!TryReserveCell(tentacle, cell))
                continue;

            BeginGrowingTentacle(tentacle, cell, drum, starPos);
            _lastTentacleStartTime = Time.time;
            idleTentacles.RemoveAt(bestIdx);
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

        // The role walled by the last completed ring (CosmicDustGenerator.Blocks.cs) is never
        // star food — catches any stray cell of that role the wall/passage pass missed.
        if (gen.BlockedRole != MusicalRole.None && dust.Role == gen.BlockedRole)
        {
            reason = "role walled";
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
        var excluded = _excludedCellsScratch;
        excluded.Clear();
        foreach (var kv in _reservedCells)
        {
            if (requester == null || (kv.Value != null && kv.Value != requester))
                excluded.Add(kv.Key);
        }

        foreach (var zappedCell in _zappedThisCycle)
            excluded.Add(zappedCell);

        // Flowers a vine just failed to reach sit out a short cooldown so the next scan picks
        // a different flower instead of re-boring the same wall.
        if (_blockedUntil.Count > 0)
        {
            float now = Time.time;
            _blockedExpiredScratch.Clear();
            foreach (var kv in _blockedUntil)
            {
                if (kv.Value > now) excluded.Add(kv.Key);
                else _blockedExpiredScratch.Add(kv.Key);
            }
            foreach (var c in _blockedExpiredScratch) _blockedUntil.Remove(c);
        }

        return excluded;
    }

    private readonly Dictionary<Vector2Int, float> _blockedUntil = new();
    private readonly List<Vector2Int> _blockedExpiredScratch = new();
    private string _lastRejectLog;

    private void MarkTargetBlocked(Vector2Int cell)
    {
        _blockedUntil[cell] = Time.time + Mathf.Max(0f, Cfg.blockedTargetCooldownSeconds);
        if (GameFlowManager.VerboseLogging)
            Debug.Log($"[STAR:TARGET] vine blocked cell={cell} role={_attunedRole} cooldown={Cfg.blockedTargetCooldownSeconds:0.##}s");
    }

    private void LogTargetReject(Vector2Int cell, string reason)
    {
        if (!GameFlowManager.VerboseLogging) return;
        string line = $"[STAR:TARGET] reject cell={cell} role={_attunedRole} reason={reason}";
        if (line == _lastRejectLog) return;
        _lastRejectLog = line;
        Debug.Log(line);
    }
}
