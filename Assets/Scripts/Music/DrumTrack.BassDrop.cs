using UnityEngine;

// Bass drop: when a Bass note is placed (committed to the loop — manual release or auto-deposit
// arrival), the drums jump to the motif's heaviest intensity clip on the next loop boundary —
// bypassing the entry window, phrase gate and min-dwell — hold for config.bassDropHoldLoops
// audible loops, then hand back to carve-driven selection (which drains from the top since
// _lastPressure01 is seeded to 1). Each further Bass placement during a drop refreshes the hold.
// (Was keyed off burst-cleared, which only fired once every collectable resolved — an uncaught
// note held it open until motif teardown.)
public partial class DrumTrack
{
    private AudioClip _bassDropClip;
    private int _bassDropLoopsRemaining;
    private bool _bassDropPending;
    // Visual tell (LEGIBILITY_AUDIT.md D1): fired once per drop, the moment the drop clip is
    // actually audible — not when it's triggered — so the shockwave lands on the beat.
    private bool _bassDropAnnounced;
    private Color _bassDropColor = Color.white;

    private bool _noteCommittedSubscribed;
    private InstrumentTrack[] _noteCommittedSubscribedTracks;

    private bool BassDropActive => _bassDropClip != null;

    private void EnsureNoteCommittedSubscription()
    {
        var tracks = _trackController != null ? _trackController.tracks : null;
        if (_noteCommittedSubscribed && tracks == _noteCommittedSubscribedTracks) return;

        if (_noteCommittedSubscribedTracks != null)
        {
            foreach (var t in _noteCommittedSubscribedTracks)
                if (t != null) t.OnNoteCommitted -= OnAnyTrackNoteCommitted;
        }

        if (tracks != null)
        {
            foreach (var t in tracks)
            {
                if (t == null) continue;
                t.OnNoteCommitted -= OnAnyTrackNoteCommitted;
                t.OnNoteCommitted += OnAnyTrackNoteCommitted;
            }
        }

        _noteCommittedSubscribedTracks = tracks;
        _noteCommittedSubscribed = true;
    }

    private void OnAnyTrackNoteCommitted(InstrumentTrack track, int burstId)
    {
        if (config == null || !config.bassDropEnabled) return;
        if (track == null || track.assignedRole != MusicalRole.Bass) return;
        if (!_driveFromDensity || _motif == null || _intensityTags == null || _intensityTags.Count == 0) return;

        if (BassDropActive)
        {
            _bassDropLoopsRemaining = Mathf.Max(1, config.bassDropHoldLoops);
            if (logBeatSeqGates) Debug.Log($"[DRUM][BassDrop] refresh track={track.name} burst={burstId} hold={_bassDropLoopsRemaining}");
            return;
        }

        var clip = ResolveBassDropClip();
        if (clip == null) return;

        _bassDropClip = clip;
        _bassDropLoopsRemaining = Mathf.Max(1, config.bassDropHoldLoops);
        _bassDropPending = false;
        _bassDropAnnounced = false;
        var bassProfile = track.ActiveProfile != null ? track.ActiveProfile : MusicalRoleProfileLibrary.GetProfile(MusicalRole.Bass);
        _bassDropColor = bassProfile != null ? bassProfile.GetBaseColor() : Color.white;
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
            // Already playing the heaviest clip: the drop is "audible" right now.
            if (!alreadyHeading) AnnounceBassDropIfAudible();
            return;
        }

        // A swap that's already PlayScheduled can't be cancelled safely — retry the moment it
        // finalizes (TickDeckSwap), falling back to the next leader boundary.
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

        // Fast-arm for the next BAR (one bin), not the next leader boundary — with an expanded
        // leader loop (2+ bins) waiting for the leader boundary put the drop up to N bars late.
        // Drum clips loop per bar, so a swap on any bar boundary inside the leader loop is on-grid.
        // Skip ahead a bar if the next one is too close for the crossfade window.
        double effLen = Mathf.Max(0.0001f, EffectiveLoopLengthSec);
        double barLen = Mathf.Max(0.0001f, GetAuthoredBaseLoopSeconds());
        double minLead = (config != null ? config.deckCrossfadeSeconds : 0.15) + 0.05;
        double earliest = AudioSettings.dspTime + minLead;
        double barsAhead = System.Math.Max(1.0, System.Math.Ceiling((earliest - leaderStartDspTime) / barLen));
        double nextBar = leaderStartDspTime + barsAhead * barLen;
        ArmPendingDrumLoopForNextLeaderBoundary(nextBar, effLen);
        if (logBeatSeqGates) Debug.Log($"[DRUM][BassDrop] scheduled clip={_bassDropClip.name} at next bar dsp={nextBar:F3} (in {(nextBar - AudioSettings.dspTime):F2}s)");
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
        _bassDropAnnounced = false;
    }

    // Called when the drop clip is confirmed audible (here, or from TickDeckSwap's finalize in
    // DrumTrack.Transport.cs). One shockwave per drop; refreshes during the hold don't re-fire.
    private void AnnounceBassDropIfAudible()
    {
        if (!BassDropActive || _bassDropAnnounced) return;
        var audible = drumAudioSource != null ? drumAudioSource.clip : null;
        if (audible != _bassDropClip) return;

        _bassDropAnnounced = true;
        if (config == null || !config.bassDropShockwaveEnabled || _dust == null) return;

        Color flash = Color.Lerp(_bassDropColor, Color.white, config.bassDropShockwaveWhiten01);
        _dust.BeginShockwaveFromCenter(flash, config.bassDropShockwaveCellsPerSecond, config.bassDropShockwaveFlashSeconds);
        if (logBeatSeqGates) Debug.Log($"[DRUM][BassDrop] shockwave clip={_bassDropClip.name}");
    }
}
