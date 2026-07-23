using System;

// Owns the "shrink this track's loop by one bin at the next loop boundary" concern.
// Extracted from InstrumentTrack — purely event-driven via DrumTrack.OnLoopBoundary, no
// coroutines, so unlike GravityVoidController this needs no `host: MonoBehaviour`.
public sealed class LoopCollapseController
{
    private readonly Func<int> _getLoopMultiplier;
    private readonly Action<int> _setLoopMultiplier;
    private readonly Func<int> _getBinSize;
    private readonly Action<int> _setTotalSteps;
    private readonly Func<bool> _isExpansionPending;
    private readonly Action<int> _clearBinsAbove;
    private readonly Action<int> _removeLoopNotesAtOrAfterStep;
    private readonly Action _markLoopCacheDirtyPending;
    private readonly Action _recomputeBinsFromLoop;
    private readonly Action _resetStepCursors;
    private readonly Func<DrumTrack> _getDrumTrack;
    private readonly Func<InstrumentTrackController> _getController;
    private readonly Action<int> _syncVisualizerAfterCollapse;

    private bool _pendingCollapse;
    private int  _collapseTargetMultiplier = 1;
    private bool _hookedBoundaryForCollapse;

    public LoopCollapseController(
        Func<int> getLoopMultiplier,
        Action<int> setLoopMultiplier,
        Func<int> getBinSize,
        Action<int> setTotalSteps,
        Func<bool> isExpansionPending,
        Action<int> clearBinsAbove,
        Action<int> removeLoopNotesAtOrAfterStep,
        Action markLoopCacheDirtyPending,
        Action recomputeBinsFromLoop,
        Action resetStepCursors,
        Func<DrumTrack> getDrumTrack,
        Func<InstrumentTrackController> getController,
        Action<int> syncVisualizerAfterCollapse)
    {
        _getLoopMultiplier = getLoopMultiplier;
        _setLoopMultiplier = setLoopMultiplier;
        _getBinSize = getBinSize;
        _setTotalSteps = setTotalSteps;
        _isExpansionPending = isExpansionPending;
        _clearBinsAbove = clearBinsAbove;
        _removeLoopNotesAtOrAfterStep = removeLoopNotesAtOrAfterStep;
        _markLoopCacheDirtyPending = markLoopCacheDirtyPending;
        _recomputeBinsFromLoop = recomputeBinsFromLoop;
        _resetStepCursors = resetStepCursors;
        _getDrumTrack = getDrumTrack;
        _getController = getController;
        _syncVisualizerAfterCollapse = syncVisualizerAfterCollapse;
    }

    private void UnhookCollapseBoundary()
    {
        var drumTrack = _getDrumTrack();
        if (!_hookedBoundaryForCollapse || drumTrack == null) return;
        drumTrack.OnLoopBoundary -= OnDrumDownbeat_CommitCollapse;
        _hookedBoundaryForCollapse = false;
    }

    private void OnDrumDownbeat_CommitCollapse()
    {
        if (!_pendingCollapse) { UnhookCollapseBoundary(); return; }

        var drumTrack = _getDrumTrack();
        var controller = _getController();
        int loopMultiplier = _getLoopMultiplier();

        int newMult = Math.Clamp(_collapseTargetMultiplier, 1, loopMultiplier);
        if (newMult != loopMultiplier)
        {
            _setLoopMultiplier(newMult);

            // Clear allocation/filled flags above the collapsed width so EffectiveLoopBins won't re-grow.
            _clearBinsAbove(newMult);

            // Use this track's bin size rather than DrumTrack.totalSteps.
            // DrumTrack.totalSteps can represent a different timing grid (e.g. 16),
            // while InstrumentTrack bins may be authored at a smaller width (e.g. 8).
            // If we multiply by DrumTrack.totalSteps during collapse, notes in trimmed
            // bins can remain inside _totalSteps and survive pruning, causing
            // stale harmony/marker artifacts at the right edge after loop contraction.
            int totalSteps = _getBinSize() * newMult;
            _setTotalSteps(totalSteps);

            // Remove any loop notes that are now outside the audible window
            _removeLoopNotesAtOrAfterStep(totalSteps);

            _markLoopCacheDirtyPending();
            _recomputeBinsFromLoop();

            // ---- VISUAL AUTHORITY (subtractive-safe) ----
            // 1) snap the grid to the new leader width immediately (prevents stale X mapping)
            // 2) prune any stale loop-owned markers and re-add missing ones
            _syncVisualizerAfterCollapse(totalSteps);

            // Let controller update other tracks if needed (hash-driven).
            controller?.UpdateVisualizer();

            // Sync DrumTrack._binCount so audio transport and committed-leader queries agree.
            controller?.ResyncLeaderBinsNow();
        }

        _pendingCollapse = false;
        UnhookCollapseBoundary();
        _resetStepCursors();
    }

    /// <summary>
    /// Requests that this track's loop shrink by one bin at the next loop boundary.
    /// Safe to call multiple times — ignored if already at minimum or a collapse is pending.
    /// </summary>
    public void RequestLoopCollapseByOne()
    {
        int loopMultiplier = _getLoopMultiplier();
        if (loopMultiplier <= 1 || _pendingCollapse || _isExpansionPending()) return;
        _collapseTargetMultiplier = loopMultiplier - 1;
        _pendingCollapse = true;

        var drumTrack = _getDrumTrack();
        if (!_hookedBoundaryForCollapse && drumTrack != null)
        {
            drumTrack.OnLoopBoundary += OnDrumDownbeat_CommitCollapse;
            _hookedBoundaryForCollapse = true;
        }
    }
}
