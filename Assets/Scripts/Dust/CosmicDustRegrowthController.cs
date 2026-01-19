using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Orchestrates regrowth timing:
/// - delay coroutine per cell
/// - step-gated promotion from PendingRegrow to CommitRegrowCell
///
/// Does NOT own authoritative cell state data structures. It calls back into CosmicDustGenerator via delegates.
/// </summary>
public sealed class CosmicDustRegrowthController
{
    private readonly MonoBehaviour _host;

    // --- Scheduling state ---
    private readonly Dictionary<Vector2Int, Coroutine> _regrowthCoroutines = new Dictionary<Vector2Int, Coroutine>();
    private readonly Queue<Vector2Int> _pendingStepRegrow = new Queue<Vector2Int>();
    private readonly HashSet<Vector2Int> _pendingStepRegrowSet = new HashSet<Vector2Int>();

    private int _lastStepGateStep = -9999;
    private int _stepGateBudget = 0;

    // --- Delegates (policy + authority hooks) ---
    private readonly Func<Vector2Int, bool> _isInBounds;
    private readonly Func<Vector2Int, bool> _isPermanentClear;
    private readonly Func<Vector2Int, bool> _hasDustAt;
    private readonly Func<Vector2Int, bool> _isKeepClearCell;
    private readonly Func<Vector2Int, bool> _isDustSpawnBlocked;
    private readonly Func<Vector2Int, bool> _isCollectableCellFree;
    private readonly Func<Vector2Int, bool> _isSpawnCellAvailable;
    private readonly Func<Vector2Int, bool> _isVehicleOverlappingCell;

    private readonly Func<Vector2Int, bool> _tryGetPendingRegrow; // true if state == PendingRegrow
    private readonly Action<Vector2Int> _setCellEmpty;            // used for permanent veto on step gate
    private readonly Action<Vector2Int> _setCellPendingRegrow;    // used by delay coroutine
    private readonly Func<Vector2Int, IEnumerator> _commitRegrowCell;

    // --- Tunables provided per tick / call ---
    private readonly Func<float> _getRegrowVetoRetryDelaySeconds;
    private readonly Func<int> _getRegrowCellsPerStep;

    public CosmicDustRegrowthController(
        MonoBehaviour host,
        Func<Vector2Int, bool> isInBounds,
        Func<Vector2Int, bool> isPermanentClear,
        Func<Vector2Int, bool> hasDustAt,
        Func<Vector2Int, bool> isKeepClearCell,
        Func<Vector2Int, bool> isDustSpawnBlocked,
        Func<Vector2Int, bool> isCollectableCellFree,
        Func<Vector2Int, bool> isSpawnCellAvailable,
        Func<Vector2Int, bool> isVehicleOverlappingCell,
        Func<Vector2Int, bool> tryGetPendingRegrow,
        Action<Vector2Int> setCellEmpty,
        Action<Vector2Int> setCellPendingRegrow,
        Func<Vector2Int, IEnumerator> commitRegrowCell,
        Func<float> getRegrowVetoRetryDelaySeconds,
        Func<int> getRegrowCellsPerStep)
    {
        _host = host;

        _isInBounds = isInBounds;
        _isPermanentClear = isPermanentClear;
        _hasDustAt = hasDustAt;
        _isKeepClearCell = isKeepClearCell;
        _isDustSpawnBlocked = isDustSpawnBlocked;
        _isCollectableCellFree = isCollectableCellFree;
        _isSpawnCellAvailable = isSpawnCellAvailable;
        _isVehicleOverlappingCell = isVehicleOverlappingCell;

        _tryGetPendingRegrow = tryGetPendingRegrow;
        _setCellEmpty = setCellEmpty;
        _setCellPendingRegrow = setCellPendingRegrow;
        _commitRegrowCell = commitRegrowCell;

        _getRegrowVetoRetryDelaySeconds = getRegrowVetoRetryDelaySeconds;
        _getRegrowCellsPerStep = getRegrowCellsPerStep;
    }

    public void CancelRegrow(Vector2Int cell)
    {
        if (_regrowthCoroutines.TryGetValue(cell, out var c) && c != null)
            _host.StopCoroutine(c);

        _regrowthCoroutines.Remove(cell);
        _pendingStepRegrowSet.Remove(cell);
        // NOTE: queue removal is not trivial; we leave it and ignore when dequeued.
    }

    public void RequestRegrowCellAt(
        Vector2Int gridPos,
        float delaySeconds,
        bool refreshIfPending)
    {
        if (!_isInBounds(gridPos))
        {
            CancelRegrow(gridPos);
            return;
        }

        // Permanent veto: never schedule.
        if (_isPermanentClear(gridPos))
            return;

        // Already solid: don't schedule.
        if (_hasDustAt(gridPos))
            return;

        // If there's already a coroutine and we're not refreshing, do nothing.
        if (!refreshIfPending && _regrowthCoroutines.ContainsKey(gridPos))
            return;

        // If refreshing, cancel the prior coroutine.
        if (refreshIfPending)
            CancelRegrow(gridPos);

        float delay = Mathf.Max(0f, delaySeconds);
        _regrowthCoroutines[gridPos] = _host.StartCoroutine(RegrowCellAfterDelay(gridPos, delay));
    }

    public void EnqueueStepRegrow(Vector2Int gp)
    {
        if (_pendingStepRegrowSet.Add(gp))
            _pendingStepRegrow.Enqueue(gp);
    }

    public void ProcessStepGate(int stepNow)
    {
        if (stepNow != _lastStepGateStep)
        {
            _lastStepGateStep = stepNow;
            _stepGateBudget = Mathf.Max(0, _getRegrowCellsPerStep());
        }

        while (_stepGateBudget > 0 && _pendingStepRegrow.Count > 0)
        {
            var gp = _pendingStepRegrow.Dequeue();
            _pendingStepRegrowSet.Remove(gp);
            _stepGateBudget--;

            // Must still be pending.
            if (!_tryGetPendingRegrow(gp)) continue;

            // Permanent veto: collapse to Empty.
            if (_isPermanentClear(gp))
            {
                _setCellEmpty(gp);
                continue;
            }

            // Vetoes: keep PendingRegrow and retry later by re-enqueue.
            if (_isKeepClearCell(gp)) { EnqueueStepRegrow(gp); continue; }
            if (_isDustSpawnBlocked(gp)) { EnqueueStepRegrow(gp); continue; }
            if (!_isCollectableCellFree(gp)) { EnqueueStepRegrow(gp); continue; }
            if (!_isSpawnCellAvailable(gp)) { EnqueueStepRegrow(gp); continue; }

            if (_isVehicleOverlappingCell(gp))
            {
                EnqueueStepRegrow(gp);
                continue;
            }

            _host.StartCoroutine(_commitRegrowCell(gp));
        }
    }

    private IEnumerator RegrowCellAfterDelay(Vector2Int gridPos, float initialDelaySeconds)
    {
        if (!_isInBounds(gridPos))
        {
            _regrowthCoroutines.Remove(gridPos);
            yield break;
        }

        float delaySeconds = Mathf.Max(0.0f, initialDelaySeconds);

        while (true)
        {
            if (!_isInBounds(gridPos))
            {
                _regrowthCoroutines.Remove(gridPos);
                yield break;
            }

            yield return new WaitForSeconds(delaySeconds);

            // Permanent veto: end pipeline.
            if (_isPermanentClear(gridPos))
            {
                _regrowthCoroutines.Remove(gridPos);
                yield break;
            }

            // If already solid, we're done.
            if (_hasDustAt(gridPos))
            {
                _regrowthCoroutines.Remove(gridPos);
                yield break;
            }

            // Keep-clear: wait until released, then retry immediately.
            if (_isKeepClearCell(gridPos))
            {
                yield return new WaitUntil(() =>
                    !_isKeepClearCell(gridPos) ||
                    _isPermanentClear(gridPos) ||
                    !_isInBounds(gridPos));

                if (!_isInBounds(gridPos) || _isPermanentClear(gridPos))
                {
                    _regrowthCoroutines.Remove(gridPos);
                    yield break;
                }

                delaySeconds = 0f;
                continue;
            }

            // Other vetoes: retry with backoff.
            if (_isDustSpawnBlocked(gridPos) ||
                !_isSpawnCellAvailable(gridPos) ||
                _isVehicleOverlappingCell(gridPos) ||
                !_isCollectableCellFree(gridPos))
            {
                delaySeconds = Mathf.Max(0.05f, _getRegrowVetoRetryDelaySeconds());
                continue;
            }

            // Success: mark PendingRegrow + enqueue step-gate promotion.
            _setCellPendingRegrow(gridPos);
            EnqueueStepRegrow(gridPos);

            _regrowthCoroutines.Remove(gridPos);
            yield break;
        }
    }
}
