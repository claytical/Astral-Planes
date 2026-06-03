using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public partial class InstrumentTrack
{
    private void InitializeBinChords(int maxBins)
    {
        _binFillOrder = new List<int>(new int[maxBins]);
        _binChordIndex = new List<int>(Enumerable.Repeat(-1, maxBins));
        _nextFillOrdinal = 1;
    }
    private void EnsureBinList()
    {
        int want = Mathf.Max(1, maxLoopMultiplier);

        if (_binFilled == null)
            _binFilled = new List<bool>(want);

        while (_binFilled.Count < want)
            _binFilled.Add(false);

        if (_binFilled.Count > want)
            _binFilled.RemoveRange(want, _binFilled.Count - want);

        // Keep _binCompletionTime in sync with _binFilled size.
        if (_binCompletionTime == null || _binCompletionTime.Length != want)
        {
            var old = _binCompletionTime;
            _binCompletionTime = new float[want];
            for (int i = 0; i < want; i++)
                _binCompletionTime[i] = (old != null && i < old.Length) ? old[i] : -1f;
        }

        if (binAllocated == null || binAllocated.Length != maxLoopMultiplier) {
            var old = binAllocated;
            binAllocated = new bool[maxLoopMultiplier];
            if (old != null) {
                int n = Mathf.Min(old.Length, binAllocated.Length);
                for (int i = 0; i < n; i++) binAllocated[i] = old[i];
            }
        }
        Harmony_Bins_EnsureSize(_binFilled.Count);
    }
    public bool IsBinAllocated(int bin) {
        if (binAllocated == null) return false;
        if (bin < 0 || bin >= binAllocated.Length) return false;
        return binAllocated[bin];
    }
    private void SetBinAllocated(int bin, bool v) {
        EnsureBinList();
        if (bin < 0 || bin >= binAllocated.Length) return;
        binAllocated[bin] = v;
    }

    private int HighestFilledBinIndex()
    {
        EnsureBinList();
        for (int i = _binFilled.Count - 1; i >= 0; i--)
            if (_binFilled[i]) return i;
        return -1;
    }

    private int HighestAllocatedBinIndex()
    {
        EnsureBinList();
        int hi = GetHighestAllocatedBin(); // already respects binAllocated[]
        return hi;
    }

    /// <summary>
    /// Track span should be derived from ALLOCATED bins (timeline stability),
    /// not FILLED bins (content). Filled bins control silence vs sound.
    /// </summary>
    // Clears allocation, fill flag, completion time, and harmony state for every bin
    // from fromBin (inclusive) up to maxLoopMultiplier. Used by both collapse paths.
    private void ClearBinsAbove(int fromBin)
    {
        EnsureBinList();
        for (int b = fromBin; b < maxLoopMultiplier; b++)
        {
            SetBinAllocated(b, false);
            if (b < _binFilled.Count) _binFilled[b] = false;
            if (_binCompletionTime != null && b < _binCompletionTime.Length) _binCompletionTime[b] = -1f;
            Harmony_OnBinEmptied(b);
        }
    }

    // Advances the bin cursor to at least targetBin+1, clamped to loopMultiplier.
    // ByVoice tracks suppress cursor movement — their cursor advances via NotifyBinFilled.
    private void AdvanceCursorPastBin(int targetBin)
    {
        var rp = MusicalRoleProfileLibrary.GetProfile(assignedRole);
        if (rp != null && rp.configSelectionMode == RoleConfigSelectionMode.ByVoice) return;
        if (GetBinCursor() <= targetBin) SetBinCursor(targetBin + 1);
        if (loopMultiplier > 0 && GetBinCursor() > loopMultiplier) SetBinCursor(loopMultiplier);
    }

    private int EffectiveLoopBins()
    {
        int maxBins = Mathf.Max(1, maxLoopMultiplier);

        int hiAlloc = HighestAllocatedBinIndex();
        int binsFromAlloc = Mathf.Clamp(hiAlloc + 1, 1, maxBins);

        int hiFill = HighestFilledBinIndex();
        int binsFromFill = Mathf.Clamp(hiFill + 1, 1, maxBins);

        // While expanding/mapping, never let the span contract.
        if (_expansionCtrl != null && _expansionCtrl.IsExpandingAndMapping)
            return Mathf.Max(binsFromAlloc, Mathf.Max(1, loopMultiplier));
        // IMPORTANT: span = allocation (stable). Fill just makes bins audible or silent.
        return Mathf.Max(1, binsFromAlloc);
    }

    private void SyncSpanFromBins()
    {
        // Span is allocation-driven; do not shrink here.
        int want = EffectiveLoopBins();
        if (want > loopMultiplier)
            loopMultiplier = want;

        _totalSteps = loopMultiplier * BinSize();
    }

    private void SetBinFilled(int bin, bool filled)
    {
        EnsureBinList();
        if (bin < 0 || bin >= _binFilled.Count) return;
        if (_binFilled[bin] == filled) return;

        _binFilled[bin] = filled;

        // Record wall-clock completion time for the bridge visualizer.
        if (filled && _binCompletionTime != null && bin < _binCompletionTime.Length)
            _binCompletionTime[bin] = Time.time;

        // Commit the audible span to match filled bins.
        SyncSpanFromBins();

        // Force cache rebuild so PlayLoopedNotesInBin hears this bin immediately.
        if (filled) _loopCacheDirtyPending = true;

        if (filled) OnBinFilled?.Invoke(this, bin);
    }

    // Use clip length (one bar), not the full effective-leader length, so that
    // LeaderLengthSec() and TimeInLeader() measure the right cycle. Using
    // GetLoopLengthInSeconds() here caused a 3× compounding error: e.g. with a
    // 3-bin leader, BaseLoopSeconds returned 3×clipLen and LeaderMultiplier
    // returned 3 again, giving a 9-bar "leader" period — notes on a 1-bin track
    // were trimmed to 10 ms for 2 of every 3 leader loops (inaudible).
    private float BaseLoopSeconds() => drumTrack != null ? drumTrack.GetClipLengthInSeconds() : 0f;
    private int   LeaderMultiplier() => Mathf.Max(1, controller?.GetMaxLoopMultiplier() ?? 1);
    private int   MyMultiplier()     => Mathf.Max(1, loopMultiplier);
    private float TimeSinceStart() =>
        drumTrack != null ? (float)(AudioSettings.dspTime - drumTrack.startDspTime) : 0f;
    private float LeaderLengthSec() =>
        BaseLoopSeconds() * LeaderMultiplier();
    private float TimeInLeader() {
        if (drumTrack == null) return 0f;
        float L = LeaderLengthSec();
        if (L <= 0f) return 0f;
        // Anchor to leaderStartDspTime (the rolling loop boundary) so this resets to
        // 0 at each actual boundary. Using startDspTime caused drift: if leaderStartDspTime
        // advanced by more than 1 bar relative to startDspTime, Groove's 1-bar window
        // appeared to have already expired, trimming every note to 10 ms (inaudible).
        double anchor = drumTrack.leaderStartDspTime > 0.0
            ? drumTrack.leaderStartDspTime
            : drumTrack.startDspTime;
        float elapsed = Mathf.Max(0f, (float)(AudioSettings.dspTime - anchor));
        return elapsed % L;
    }
}
