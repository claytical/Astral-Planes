using UnityEngine;

// Bass drop: when the last note of a Bass collectable burst commits (burst cleared with notes),
// the drums jump to the motif's heaviest intensity clip on the next loop boundary — bypassing
// the entry window, phrase gate and min-dwell — hold for config.bassDropHoldLoops audible loops,
// then hand back to carve-driven selection (which drains from the top since _lastPressure01
// is seeded to 1).
public partial class DrumTrack
{
    private AudioClip _bassDropClip;
    private int _bassDropLoopsRemaining;
    private bool _bassDropPending;

    private bool _burstClearedSubscribed;
    private InstrumentTrack[] _burstClearedSubscribedTracks;

    private bool BassDropActive => _bassDropClip != null;

    private void EnsureBurstClearedSubscription()
    {
        var tracks = _trackController != null ? _trackController.tracks : null;
        if (_burstClearedSubscribed && tracks == _burstClearedSubscribedTracks) return;

        if (_burstClearedSubscribedTracks != null)
        {
            foreach (var t in _burstClearedSubscribedTracks)
                if (t != null) t.OnCollectableBurstCleared -= OnAnyTrackBurstCleared;
        }

        if (tracks != null)
        {
            foreach (var t in tracks)
            {
                if (t == null) continue;
                t.OnCollectableBurstCleared -= OnAnyTrackBurstCleared;
                t.OnCollectableBurstCleared += OnAnyTrackBurstCleared;
            }
        }

        _burstClearedSubscribedTracks = tracks;
        _burstClearedSubscribed = true;
    }

    private void OnAnyTrackBurstCleared(InstrumentTrack track, int burstId, bool hadNotes)
    {
        if (config == null || !config.bassDropEnabled) return;
        if (!hadNotes || track == null || track.assignedRole != MusicalRole.Bass) return;
        if (!_driveFromDensity || _motif == null || _intensityTags == null || _intensityTags.Count == 0) return;

        var clip = ResolveBassDropClip();
        if (clip == null) return;

        _bassDropClip = clip;
        _bassDropLoopsRemaining = Mathf.Max(1, config.bassDropHoldLoops);
        _bassDropPending = false;
        _entryLoopsRemaining = 0;   // a drop ends the intro window
        _lastPressure01 = 1f;       // ease down from the top once the drop releases

        if (logBeatSeqGates) Debug.Log($"[DRUM][BassDrop] trigger clip={clip.name} track={track.name} burst={burstId} hold={_bassDropLoopsRemaining}");
        TryScheduleBassDrop();
    }

    // Highest pressure01 wins; ties go to the larger depth+breadth (the "fullest" heavy clip).
    private AudioClip ResolveBassDropClip()
    {
        AudioClip best = null;
        float bestP = float.MinValue, bestDB = float.MinValue;
        for (int i = 0; i < _intensityTags.Count; i++)
        {
            var t = _intensityTags[i];
            if (t.clip == null) continue;
            float db = t.depth01 + t.breadth01;
            if (t.pressure01 > bestP || (Mathf.Approximately(t.pressure01, bestP) && db > bestDB))
            {
                bestP = t.pressure01;
                bestDB = db;
                best = t.clip;
            }
        }
        return best;
    }

    private void TryScheduleBassDrop()
    {
        if (!BassDropActive) return;

        var audible = drumAudioSource != null ? drumAudioSource.clip : null;
        // Once armed, ArmPendingDrumLoopForNextLeaderBoundary nulls _pendingDrumLoop and the clip
        // lives on _inactiveDrum until TickDeckSwap finalizes.
        bool alreadyHeading = (_pendingDrumLoopArmed && _pendingDrumLoop == _bassDropClip)
                              || (_pendingDrumLoopDspStart > 0.0 && _inactiveDrum != null && _inactiveDrum.clip == _bassDropClip);
        if ((!_pendingDrumLoopArmed && _pendingDrumLoopDspStart <= 0.0 && audible == _bassDropClip) || alreadyHeading)
        {
            _bassDropPending = false;
            if (logBeatSeqGates) Debug.Log($"[DRUM][BassDrop] already audible/scheduled clip={_bassDropClip.name}");
            return;
        }

        // A swap that's already PlayScheduled can't be cancelled safely — retry next boundary.
        if (_pendingDrumLoopDspStart > 0.0)
        {
            _bassDropPending = true;
            if (logBeatSeqGates) Debug.Log($"[DRUM][BassDrop] pending: swap already scheduled dsp={_pendingDrumLoopDspStart:F3}");
            return;
        }

        // Replace any armed-but-unscheduled clip with the drop clip.
        _pendingDrumLoopArmed = false;
        _pendingDrumLoop = null;
        ScheduleDrumLoopChange(_bassDropClip);
        _bassDropPending = false;

        // Fast-arm for the upcoming boundary rather than waiting a full phrase. If the boundary
        // is too close for the crossfade window, leave it armed — TickLoopBoundaries arms it for
        // the following boundary (avoids the near-instant, off-grid fallback).
        double effLen = Mathf.Max(0.0001f, EffectiveLoopLengthSec);
        double nextBoundary = leaderStartDspTime + effLen;
        double minLead = (config != null ? config.deckCrossfadeSeconds : 0.15) + 0.05;
        if (nextBoundary - AudioSettings.dspTime > minLead)
        {
            ArmPendingDrumLoopForNextLeaderBoundary(nextBoundary, effLen);
            if (logBeatSeqGates) Debug.Log($"[DRUM][BassDrop] scheduled clip={_bassDropClip.name} at next boundary dsp={nextBoundary:F3}");
        }
        else
        {
            _pendingSwapPhraseTarget = 1;
            _pendingSwapLoopsElapsed = 0;
            if (logBeatSeqGates) Debug.Log($"[DRUM][BassDrop] scheduled clip={_bassDropClip.name} (boundary too close, arming at following boundary)");
        }
    }

    // Called once per loop boundary while a drop is active. Returns true while the drop
    // still owns clip selection.
    private bool TickBassDropAtLoopBoundary()
    {
        if (!BassDropActive) return false;

        if (_bassDropPending)
        {
            TryScheduleBassDrop();
            return true;
        }

        var audible = drumAudioSource != null ? drumAudioSource.clip : null;
        if (audible == _bassDropClip && !_pendingDrumLoopArmed && _pendingDrumLoopDspStart <= 0.0)
        {
            _bassDropLoopsRemaining--;
            if (logBeatSeqGates) Debug.Log($"[DRUM][BassDrop] hold remain={_bassDropLoopsRemaining}");
            if (_bassDropLoopsRemaining <= 0)
            {
                if (logBeatSeqGates) Debug.Log($"[DRUM][BassDrop] released clip={_bassDropClip.name}");
                ClearBassDropState();
                return false;
            }
        }
        return true;
    }

    private void ClearBassDropState()
    {
        _bassDropClip = null;
        _bassDropLoopsRemaining = 0;
        _bassDropPending = false;
    }
}
