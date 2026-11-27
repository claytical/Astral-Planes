using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Owns every in-flight dust-growth coroutine and the regrow pipeline's timing:
/// - per-cell delay coroutine (Empty → PendingRegrow)
/// - step-gated promotion (PendingRegrow → CommitRegrowCell, run by the host)
/// - tracking of the host's commit coroutines and Gravity Void grow-in coroutines,
///   so one <see cref="CancelAll"/> stops everything.
///
/// Does NOT own authoritative cell state; it calls back into CosmicDustGenerator via delegates.
/// Whether a cell may regrow right now is a single host predicate (<see cref="IsRegrowBlocked"/>),
/// used at every stage (delay coroutine, step gate, and the host's commit) so the vetoes can't drift.
/// </summary>
public sealed class CosmicDustRegrowthController
{
    private readonly MonoBehaviour _host;

    // --- Coroutine tracking ---
    private readonly Dictionary<Vector2Int, Coroutine> _delayCoroutines = new();
    private readonly Dictionary<Vector2Int, Coroutine> _commitCoroutines = new();
    private readonly Dictionary<Vector2Int, Coroutine> _voidGrowCoroutines = new();

    // --- Step gate ---
    private readonly Queue<Vector2Int> _pendingStepRegrow = new();
    private readonly HashSet<Vector2Int> _pendingStepRegrowSet = new();
    private int _lastStepGateStep = -9999;
    private int _stepGateBudget = 0;

    // --- Delegates (policy + authority hooks) ---
    private readonly Func<Vector2Int, bool> _isInBounds;
    private readonly Func<Vector2Int, bool> _isPermanentClear;
    private readonly Func<Vector2Int, bool> _hasDustAt;
    private readonly Func<Vector2Int, bool> _isKeepClearCell;  // subset of _isRegrowBlocked: wait instead of polling
    private readonly Func<Vector2Int, bool> _isRegrowBlocked;
    private readonly Func<Vector2Int, bool> _tryGetPendingRegrow; // true if state == PendingRegrow
    private readonly Action<Vector2Int> _setCellEmpty;
    private readonly Action<Vector2Int> _setCellPendingRegrow;
    private readonly Func<Vector2Int, IEnumerator> _commitRegrowCell;
    private readonly Func<float> _getRegrowVetoRetryDelaySeconds;
    private readonly Func<int> _getRegrowCellsPerStep;

    public CosmicDustRegrowthController(
        MonoBehaviour host,
        Func<Vector2Int, bool> isInBounds,
        Func<Vector2Int, bool> isPermanentClear,
        Func<Vector2Int, bool> hasDustAt,
        Func<Vector2Int, bool> isKeepClearCell,
        Func<Vector2Int, bool> isRegrowBlocked,
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
        _isRegrowBlocked = isRegrowBlocked;
        _tryGetPendingRegrow = tryGetPendingRegrow;
        _setCellEmpty = setCellEmpty;
        _setCellPendingRegrow = setCellPendingRegrow;
        _commitRegrowCell = commitRegrowCell;
        _getRegrowVetoRetryDelaySeconds = getRegrowVetoRetryDelaySeconds;
        _getRegrowCellsPerStep = getRegrowCellsPerStep;
    }

    /// <summary>The single "may this cell regrow right now?" veto, shared by every stage.</summary>
    public bool IsRegrowBlocked(Vector2Int cell) => _isRegrowBlocked(cell);

    // ------------------------------------------------------------------
    // Cancellation
    // ------------------------------------------------------------------

    /// <summary>Stops a cell's pending delay coroutine (not an in-flight commit).</summary>
    public void CancelRegrow(Vector2Int cell)
    {
        if (_delayCoroutines.TryGetValue(cell, out var c) && c != null)
            _host.StopCoroutine(c);

        _delayCoroutines.Remove(cell);
        _pendingStepRegrowSet.Remove(cell);
        // NOTE: queue removal is not trivial; we leave it and ignore when dequeued.
    }

    /// <summary>
    /// Stops every delay, commit and void-grow coroutine and empties the step queue.
    /// Cell state is left to the caller (HardStopRegrowthForBridge / ClearMaze).
    /// </summary>
    public void CancelAll()
    {
        StopAll(_delayCoroutines);
        StopAll(_commitCoroutines);
        StopAll(_voidGrowCoroutines);
        _pendingStepRegrow.Clear();
        _pendingStepRegrowSet.Clear();
        _stepGateBudget = 0;
        _lastStepGateStep = -9999;
    }

    public int InFlightGrowCount => _commitCoroutines.Count + _voidGrowCoroutines.Count;

    private void StopAll(Dictionary<Vector2Int, Coroutine> map)
    {
        foreach (var kv in map)
            if (kv.Value != null) _host.StopCoroutine(kv.Value);
        map.Clear();
    }

    // Starts a tracked coroutine. The key is reserved before starting so a routine that finishes
    // synchronously (yield break before its first yield) and calls its *Finished hook doesn't get
    // a stale, already-dead entry written back after StartCoroutine returns.
    private void StartTracked(Dictionary<Vector2Int, Coroutine> map, Vector2Int cell, IEnumerator routine)
    {
        map[cell] = null;
        var c = _host.StartCoroutine(routine);
        if (map.ContainsKey(cell)) map[cell] = c;
    }

    // ------------------------------------------------------------------
    // Host-run coroutines (commit, void growth)
    // ------------------------------------------------------------------

    /// <summary>Host's CommitRegrowCell must call this on every exit path.</summary>
    public void CommitFinished(Vector2Int cell) => _commitCoroutines.Remove(cell);

    public bool IsVoidGrowing(Vector2Int cell) => _voidGrowCoroutines.ContainsKey(cell);

    public void StartVoidGrow(Vector2Int cell, IEnumerator routine) => StartTracked(_voidGrowCoroutines, cell, routine);

    /// <summary>Host's VoidGrowCellNow must call this on every exit path.</summary>
    public void VoidGrowFinished(Vector2Int cell) => _voidGrowCoroutines.Remove(cell);

    // ------------------------------------------------------------------
    // Regrow pipeline
    // ------------------------------------------------------------------

    public void RequestRegrowCellAt(Vector2Int gridPos, float delaySeconds, bool refreshIfPending)
    {
        if (!_isInBounds(gridPos))
        {
            CancelRegrow(gridPos);
            return;
        }

        if (_isPermanentClear(gridPos)) return;
        if (_hasDustAt(gridPos)) return;

        if (!refreshIfPending && _delayCoroutines.ContainsKey(gridPos))
            return;

        if (refreshIfPending)
            CancelRegrow(gridPos);

        StartTracked(_delayCoroutines, gridPos, RegrowCellAfterDelay(gridPos, Mathf.Max(0f, delaySeconds)));
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
            // Accumulate: add the new step's allocation on top of any unspent budget
            // rather than discarding it, so a queue that fills later in the same step window
            // isn't starved. Capped at two steps' worth: an unbounded bank (built up while the
            // queue sat empty) let a whole carved line commit in one frame — a burst arrival
            // instead of a beat-staggered regrow.
            int perStep = Mathf.Max(0, _getRegrowCellsPerStep());
            _stepGateBudget = Mathf.Min(_stepGateBudget + perStep, perStep * 2);
        }

        while (_stepGateBudget > 0 && _pendingStepRegrow.Count > 0)
        {
            var gp = _pendingStepRegrow.Dequeue();
            _pendingStepRegrowSet.Remove(gp);
            _stepGateBudget--;

            if (!_tryGetPendingRegrow(gp)) continue;

            if (_isPermanentClear(gp))
            {
                _setCellEmpty(gp);
                continue;
            }

            if (_isRegrowBlocked(gp))
            {
                EnqueueStepRegrow(gp);
                continue;
            }

            StartTracked(_commitCoroutines, gp, _commitRegrowCell(gp));
        }
    }

    private IEnumerator RegrowCellAfterDelay(Vector2Int gridPos, float initialDelaySeconds)
    {
        float delaySeconds = initialDelaySeconds;

        while (true)
        {
            if (!_isInBounds(gridPos))
            {
                _delayCoroutines.Remove(gridPos);
                yield break;
            }

            yield return new WaitForSeconds(delaySeconds);

            if (_isPermanentClear(gridPos) || _hasDustAt(gridPos))
            {
                _delayCoroutines.Remove(gridPos);
                yield break;
            }

            // Keep-clear (ship pocket/trail, star pocket, claims): wait until released, then
            // re-check immediately rather than polling on the retry interval.
            if (_isKeepClearCell(gridPos))
            {
                yield return new WaitUntil(() =>
                    !_isKeepClearCell(gridPos) ||
                    _isPermanentClear(gridPos) ||
                    !_isInBounds(gridPos));

                if (!_isInBounds(gridPos) || _isPermanentClear(gridPos))
                {
                    _delayCoroutines.Remove(gridPos);
                    yield break;
                }

                delaySeconds = 0f;
                continue;
            }

            // Any other veto: retry with backoff.
            if (_isRegrowBlocked(gridPos))
            {
                delaySeconds = Mathf.Max(0.05f, _getRegrowVetoRetryDelaySeconds());
                continue;
            }

            _setCellPendingRegrow(gridPos);
            EnqueueStepRegrow(gridPos);

            _delayCoroutines.Remove(gridPos);
            yield break;
        }
    }
}
