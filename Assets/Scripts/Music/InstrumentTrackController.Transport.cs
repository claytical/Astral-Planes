using UnityEngine;

// Transport/timing: derives the current audible bin from DSP time, plus leader-bin resync.
public partial class InstrumentTrackController
{
    /// <summary>
    /// Single source of truth for which bin is currently audible,
    /// derived ONLY from DSP time and the drum clip length.
    /// </summary>
    public TransportFrame GetTransportFrame()
    {
        if (_gfm == null) _gfm = GameFlowManager.Instance;
        var drum = _gfm?.activeDrumTrack;
        if (drum == null) return default;

        double dspNow = AudioSettings.dspTime;

        // Short-circuit: if called multiple times within the same DSP sample (multiple tracks per frame),
        // return the cached result instead of recalculating.
        // Also invalidate if leaderStartDspTime advanced mid-frame (loop boundary fired after some
        // InstrumentTracks already ran Update), which would cause stale barIndex + fresh start mismatch.
        double leaderDspNow = (drum.leaderStartDspTime > 0.0) ? drum.leaderStartDspTime : drum.startDspTime;
        if (_hasLastTransport && dspNow == _lastTransportDsp && leaderDspNow == _lastTransportLeaderDsp)
            return _lastTransport;

        double start = leaderDspNow;
        if (start <= 0.0) return default;

        float clipLen = drum.GetClipLengthInSeconds();
        if (clipLen <= 0f) return default;

        // --- tolerate "start is slightly in the future" due to PlayScheduled lead time ---
        const double kFutureStartEpsilon = 0.050; // 50ms
        double delta = dspNow - start;

        if (delta < 0.0)
        {
            if (delta > -kFutureStartEpsilon)
            {
                dspNow = start;
                delta = 0.0;
            }
            else
            {
                return new TransportFrame
                {
                    barIndex = 0,
                    playheadBin = 0,
                    boundarySerial = drum.GetBoundarySerial()
                };
            }
        }

        int barIndex = (int)System.Math.Floor(delta / (double)clipLen);

        // IMPORTANT: use committed/audible bins, not visual bins.
        int leaderBins = Mathf.Max(1, drum.GetCommittedBinCount());
        int playheadBin = (leaderBins <= 1) ? 0 : (barIndex % leaderBins);

        var tf = new TransportFrame
        {
            barIndex = barIndex,
            playheadBin = playheadBin,
            boundarySerial = drum.GetBoundarySerial()
        };

        _lastTransport = tf;
        _lastTransportDsp = dspNow;
        _lastTransportLeaderDsp = leaderDspNow;
        _hasLastTransport = true;
        return tf;
    }

    /// <summary>
    /// Returns the current playhead position as an absolute step within the current leader loop.
    /// This is a continuous value (rawAbsStep) plus its floor (floorAbsStep) and total length (totalAbsSteps).
    /// Safe to call during transient transport re-wiring; returns false if timing can't be resolved.
    /// </summary>
    public bool TryGetRawPlayheadAbsStep(out double rawAbsStep, out int floorAbsStep, out int totalAbsSteps)
    {
        rawAbsStep = 0;
        floorAbsStep = 0;
        totalAbsSteps = 0;

        if (_gfm == null) _gfm = GameFlowManager.Instance;
        var drum = _gfm?.activeDrumTrack;
        if (drum == null) return false;

        // NOTE: Manual release timing must be evaluated on the *leader* loop timeline.
        // If GetLoopLengthInSeconds() is a single bin/bar length (e.g., 16 steps), then
        // dividing it by leaderSteps (e.g., 64) makes stepDur 4x too small and rawAbsStep
        // advances 4x too fast, effectively shrinking your window.
        double baseLoopLen = drum.GetClipLengthInSeconds();
        if (baseLoopLen <= 0.0) return false;

        double dspNow = AudioSettings.dspTime;

        double transportStart = (drum.leaderStartDspTime > 0.0) ? drum.leaderStartDspTime : drum.startDspTime;
        if (transportStart <= 0.0) return false;

        int leaderSteps = Mathf.Max(1, drum.GetLeaderSteps());
        totalAbsSteps = leaderSteps;

        int binSize = Mathf.Max(1, drum.totalSteps);
        int leaderBins = Mathf.Max(1, Mathf.CeilToInt(leaderSteps / (float)binSize));
        double leaderLoopLen = baseLoopLen * leaderBins;

        // Effective loop boundary (leader loop)
        double loops = System.Math.Floor((dspNow - transportStart) / leaderLoopLen);
        double loopStart = transportStart + loops * leaderLoopLen;
        if (loopStart > dspNow) loopStart -= leaderLoopLen;

        double tPos = (dspNow - loopStart) % leaderLoopLen;
        if (tPos < 0) tPos += leaderLoopLen;

        // leaderLoopLen/leaderSteps == baseLoopLen/binSize
        double stepDur = leaderLoopLen / totalAbsSteps;
        rawAbsStep = tPos / stepDur;
        floorAbsStep = (int)System.Math.Floor(rawAbsStep) % totalAbsSteps;
        if (floorAbsStep < 0) floorAbsStep += totalAbsSteps;

        return true;
    }

    /// <summary>
    /// Immediate re-sync of drum binning + note grid to the committed leader bins.
    /// Call this when a track commits an expand/collapse mid-frame so the UI/audio
    /// cannot spend a whole loop visually desynchronized.
    /// </summary>
    public void ResyncLeaderBinsNow()
    {
        if (_gfm == null) _gfm = GameFlowManager.Instance;
        var drum = _gfm?.activeDrumTrack;
        if (drum == null) return;

        int bins = Mathf.Max(1, GetMaxActiveLoopMultiplier());
        drum.SetBinCount(bins);

        if (noteVisualizer != null)
        {
            int baseSteps = Mathf.Max(1, drum.totalSteps);
            noteVisualizer.RequestLeaderGridChange(bins * baseSteps);
        }
    }

    public int GetCommittedLeaderBins()
    {
        if (_gfm == null) _gfm = GameFlowManager.Instance;
        var drum = _gfm?.activeDrumTrack;
        if (drum == null) { if (GameFlowManager.VerboseLogging) Debug.Log("[ITC:GET_LEADER_BINS] drum=NULL → returning 1"); return 1; }
        int count = drum.GetCommittedBinCount();
        return Mathf.Max(1, count);
    }

    public float GetEffectiveLoopLengthInSeconds()
    {
        if (_gfm == null) _gfm = GameFlowManager.Instance;
        var drum = _gfm != null ? _gfm.activeDrumTrack : null;
        if (drum == null)
            return 0f;

        // IMPORTANT: use the *clip* length, not DrumTrack.GetLoopLengthInSeconds()
        float clipLen = drum.GetClipLengthInSeconds();
        int   totalSteps = drum.totalSteps;
        if (clipLen <= 0f || totalSteps <= 0)
            return clipLen;

        // LeaderSteps already looks at track loopMultipliers
        int leaderSteps = drum.GetLeaderSteps();
        if (leaderSteps <= 0)
            return clipLen;

        float stepDuration = clipLen / totalSteps;
        return stepDuration * leaderSteps;
    }
}
