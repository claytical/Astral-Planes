using UnityEngine;

// Instant (non-coroutine) bin fill triggered by SuperNode collision, plus its supporting
// "clear notes but keep allocation" helper.
public partial class InstrumentTrack
{
    /// <summary>
    /// Instantly overwrites the entire track with perfect authored notes for every bin,
    /// following each bin's chord progression. Called by SuperNode on collision.
    /// Returns the [fromBin, toBin) range of bins that actually received new notes this call
    /// (toBin &lt;= fromBin means nothing new was committed) — callers must use this, not a
    /// loopMultiplier before/after comparison, to decide whether to register an ascend sequence:
    /// a track already at maxLoopMultiplier can still have previously-unfilled bins filled here.
    /// </summary>
    public (int fromBin, int toBin) InstantFillAllBins(bool toMaxCapacity = false)
    {
        bool wasEmpty = persistentLoopNotes.Count == 0;
        int binSz = BinSize();
        int bins  = toMaxCapacity ? Mathf.Max(1, maxLoopMultiplier) : Mathf.Max(1, loopMultiplier);

        if (bins > loopMultiplier)
        {
            loopMultiplier = bins;
            _totalSteps    = binSz * bins;
            EnsureBinList();
            for (int b = 0; b < bins; b++)
                SetBinAllocated(b, true);
        }

        // Fill every bin across the whole track that isn't already filled — including bins
        // behind the playhead that were allocated but never collected during normal play —
        // instead of only the newly-expanded bins above the old loopMultiplier. Already-filled
        // bins are left untouched so the player's collected notes in them survive the fill.
        int fromBin = -1;
        int toBin   = -1;
        for (int b = 0; b < bins; b++)
        {
            if (toMaxCapacity && IsBinFilled(b)) continue;

            ClearBinNotesKeepAllocated(b);

            var ns = GetNoteSetForBin(b);
            if (ns == null) continue;

            int binStart = b * binSz;
            bool addedAnyNote = false;

            if (ns.persistentTemplate != null && ns.persistentTemplate.Count > 0)
            {
                // Riff-authoritative: step is local to the bin (0..binSz-1)
                foreach (var (step, note, dur, vel, authoredRoot) in ns.persistentTemplate)
                {
                    int absStep = binStart + step;
                    AddNoteToLoop(absStep, note, dur, vel, lightMarkerNow: true, authoredRoot);
                    addedAnyNote = true;
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

                    int authoredRoot = ns.GetAuthoredRootMidi(localStep);
                    if (authoredRoot == int.MinValue) authoredRoot = authoredRootMidi;

                    if (!ns.TryGetTemplateTimingAtStep(localStep, out int dur, out float vel))
                    {
                        dur = 480;
                        vel = 100f;
                    }

                    AddNoteToLoop(absStep, note, dur, vel, lightMarkerNow: true, authoredRoot);
                    addedAnyNote = true;
                }
            }

            SetBinFilled(b, true);

            if (addedAnyNote)
            {
                if (fromBin < 0) fromBin = b;
                toBin = b + 1;
            }
        }

        if (toMaxCapacity && wasEmpty && toBin > fromBin && midiVoice != null)
        {
            // Fades the channel from a low fraction up to unity; volumeMultiplier itself is
            // applied live per-note in PlayNote127, so this ramp targets 1f, not the profile value.
            const float kRampInSeconds = 1.5f;
            midiVoice.RampInVolume(1f, kRampInSeconds);
        }

        _loopCacheDirtyPending = true;
        controller?.EndGravityVoidForPendingExpand(this);
        controller?.UpdateVisualizer();
        // Sync leader bin count immediately when loopMultiplier expanded beyond the previous
        // committed value. Without this, GetCommittedBinCount() stays at the old value for up
        // to one full leader loop, causing barIndex >= committedLeaderBins to fire on the extra
        // bars and silence all tracks for half the extended loop.
        if (toMaxCapacity) controller?.ResyncLeaderBinsNow();

        // This role's dust has no further use while the track is fully filled — clear it out
        // and hold it until this track's notes start ascending, freeing grid capacity for
        // other roles in the meantime.
        if (toMaxCapacity) ClearRoleDustUntilAscension();

        return fromBin < 0 ? (0, 0) : (fromBin, toBin);
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
