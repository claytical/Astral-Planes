using UnityEngine;

// BinFill poll mode: re-evaluates clip selection whenever any InstrumentTrack fills a bin,
// instead of waiting for the next leader-loop boundary. The actual audio swap remains
// bar-quantized via ArmPendingDrumLoopForNextLeaderBoundary regardless of poll mode —
// this only changes when the selection decision is made.
public partial class DrumTrack
{
    private double _lastBinFillEvalDsp = -1.0;
    private bool _binFillSubscribed;
    private InstrumentTrack[] _binFillSubscribedTracks;

    private void EnsureBinFillSubscription()
    {
        var tracks = _trackController != null ? _trackController.tracks : null;
        if (_binFillSubscribed && tracks == _binFillSubscribedTracks) return;

        // Re-subscribing: unhook from the previous track set first (it may have been replaced
        // wholesale by a motif rebuild).
        if (_binFillSubscribedTracks != null)
        {
            foreach (var t in _binFillSubscribedTracks)
            {
                if (t != null) t.OnBinFilled -= OnAnyTrackBinFilled;
            }
        }

        if (tracks != null)
        {
            foreach (var t in tracks)
            {
                if (t == null) continue;
                t.OnBinFilled -= OnAnyTrackBinFilled;
                t.OnBinFilled += OnAnyTrackBinFilled;
            }
        }

        _binFillSubscribedTracks = tracks;
        _binFillSubscribed = true;
    }

    private void OnAnyTrackBinFilled(InstrumentTrack track, int binIndex)
    {
        if (config == null || config.clipPollMode != ClipPollMode.BinFill) return;

        double now = AudioSettings.dspTime;
        if (_lastBinFillEvalDsp > 0.0 && now - _lastBinFillEvalDsp < config.binFillPollDebounceSeconds) return;
        _lastBinFillEvalDsp = now;

        EvaluateAndScheduleClip();
    }
}
