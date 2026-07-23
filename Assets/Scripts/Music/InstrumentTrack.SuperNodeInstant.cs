using UnityEngine;

// Instant (non-coroutine) bin fill triggered by SuperNode collision, plus its supporting
// "clear notes but keep allocation" helper.
public partial class InstrumentTrack
{
    /// <summary>
    /// Instantly overwrites the entire track with perfect authored notes for every bin,
    /// following each bin's chord progression. Called by SuperNode on collision.
    /// </summary>
    public void InstantFillAllBins(bool toMaxCapacity = false)
    {
        int binSz = BinSize();
        int bins  = toMaxCapacity ? Mathf.Max(1, maxLoopMultiplier) : Mathf.Max(1, loopMultiplier);

        // When expanding to max capacity, only fill bins above the current count so that the
        // player's already-collected notes in existing bins survive the fill.
        int fillFrom = toMaxCapacity ? loopMultiplier : 0;

        if (bins > loopMultiplier)
        {
            loopMultiplier = bins;
            _totalSteps    = binSz * bins;
            EnsureBinList();
            for (int b = 0; b < bins; b++)
                SetBinAllocated(b, true);
        }

        // Clear only the bins we are about to fill (preserves existing bins when toMaxCapacity).
        for (int b = fillFrom; b < bins; b++)
            ClearBinNotesKeepAllocated(b);

        // Write authored notes bin-by-bin (new bins only when toMaxCapacity).
        for (int b = fillFrom; b < bins; b++)
        {
            var ns = GetNoteSetForBin(b);
            if (ns == null) continue;

            int binStart = b * binSz;

            if (ns.persistentTemplate != null && ns.persistentTemplate.Count > 0)
            {
                // Riff-authoritative: step is local to the bin (0..binSz-1)
                foreach (var (step, note, dur, vel, authoredRoot) in ns.persistentTemplate)
                {
                    int absStep = binStart + step;
                    AddNoteToLoop(absStep, note, dur, vel, lightMarkerNow: true, authoredRoot);
                }
            }
            else
            {
                // Generative fallback
                var steps = ns.GetStepList();
                foreach (int localStep in steps)
                {
                    int absStep = binStart + localStep;
                    int note    = ns.GetNoteForPhaseAndRole(this, localStep);
                    AddNoteToLoop(absStep, note, 120, 1.0f, lightMarkerNow: true, authoredRootMidi);
                }
            }

            SetBinFilled(b, true);
        }

        _loopCacheDirtyPending = true;
        controller?.EndGravityVoidForPendingExpand(this);
        controller?.UpdateVisualizer();
        // Sync leader bin count immediately when loopMultiplier expanded beyond the previous
        // committed value. Without this, GetCommittedBinCount() stays at the old value for up
        // to one full leader loop, causing barIndex >= committedLeaderBins to fire on the extra
        // bars and silence all tracks for half the extended loop.
        if (toMaxCapacity) controller?.ResyncLeaderBinsNow();
    }

    public void ClearBinNotesKeepAllocated(int binIdx)
    {
        int binSize = Mathf.Max(1, BinSize());
        int start = binIdx * binSize;
        int end   = start + binSize;

        for (int i = persistentLoopNotes.Count - 1; i >= 0; i--)
        {
            var (step, _, _, _, _) = persistentLoopNotes[i];
            if (step >= start && step < end)
                persistentLoopNotes.RemoveAt(i);
        }

        EnsureBinList();
        if (binIdx >= 0 && binIdx < _binFilled.Count)
        {
            _binFilled[binIdx] = false;
            if (_binCompletionTime != null && binIdx < _binCompletionTime.Length) _binCompletionTime[binIdx] = -1f;
            Harmony_OnBinEmptied(binIdx);
        }

        // CRITICAL: removed notes must stop playing immediately
        _loopCacheDirtyPending = true;
    }
}
