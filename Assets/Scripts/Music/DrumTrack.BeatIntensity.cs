using UnityEngine;

public partial class DrumTrack
{
    // LoopBoundary-mode entry point. In BinFill mode this is a no-op passthrough —
    // OnAnyTrackBinFilled (DrumTrack.BinFillPolling.cs) owns evaluation instead.
    private void HandleBeatSequencingAtLoopBoundary(float loopSeconds)
    {
        if (config != null && config.clipPollMode == ClipPollMode.BinFill) return;
        EvaluateAndScheduleClip();
    }

    // Computes depth/breadth/pressure, selects the nearest-match intensity clip, and
    // schedules it. Shared by both LoopBoundary and BinFill poll modes.
    private void EvaluateAndScheduleClip()
    {
        // --- hard gates (log on exit) ---
        if (!_driveFromDensity)
        {
            if (logBeatSeqGates) Debug.Log($"[DRUM][BeatSeq] exit: driveFromDensity=false motif={(_motif ? _motif.motifId : "null")}");
            return;
        }
        if (_gfm == null)
        {
            if (logBeatSeqGates) Debug.Log($"[DRUM][BeatSeq] exit: _gfm==null motif={(_motif ? _motif.motifId : "null")}");
            return;
        }
        if (_motif == null)
        {
            if (logBeatSeqGates) Debug.Log("[DRUM][BeatSeq] exit: _motif==null");
            return;
        }

        // 1) Respect entry window
        if (_entryLoopsRemaining > 0)
        {
            if (logBeatSeqGates) Debug.Log($"[DRUM][BeatSeq] exit: entry window active remain={_entryLoopsRemaining} motif={_motif.motifId}");
            _entryLoopsRemaining--;
            return;
        }

        // 1b) Hold intro until a vehicle has carried a collectable (one-way latch)
        if (!_carryLatched)
        {
            if (logBeatSeqGates) Debug.Log($"[DRUM][BeatSeq] exit: awaiting first carry motif={_motif.motifId}");
            return;
        }

        // 2) Need a tagged intensity pool
        int tagCt = (_intensityTags != null) ? _intensityTags.Count : 0;
        if (tagCt == 0)
        {
            if (logBeatSeqGates) Debug.Log($"[DRUM][BeatSeq] exit: intensityTags=0 motif={_motif.motifId}");
            return;
        }

        // 3) Compute the three axes
        if (_dust == null)
        {
            if (logBeatSeqGates) Debug.Log($"[DRUM][BeatSeq] exit: _dust==null motif={_motif.motifId}");
            return;
        }
        float depth01    = SmoothAxis(ComputeDepth01(),        ref _lastDepth01,    config.axisSmoothFactor, config.axisHysteresis);
        float breadth01  = SmoothAxis(ComputeBreadth01(),      ref _lastBreadth01,  config.axisSmoothFactor, config.axisHysteresis);
        float pressure01 = SmoothAxis(ComputeDrumPressure01(), ref _lastPressure01, config.axisSmoothFactor, config.axisHysteresis);

        if (logBeatSeqGates) Debug.Log(
            $"[DRUM][BeatSeq] motif={_motif.motifId} depth={depth01:F3} breadth={breadth01:F3} pressure={pressure01:F3} tags={tagCt}"
        );

        // 4) Select nearest-match target clip
        var targetClip = ResolveNearestIntensityClip(depth01, breadth01, pressure01);
        if (targetClip == null)
        {
            Debug.LogWarning($"[DRUM][BeatSeq] exit: ResolveNearestIntensityClip returned null depth={depth01:F3} breadth={breadth01:F3} pressure={pressure01:F3} motif={_motif.motifId}");
            return;
        }

        if (logBeatSeqGates) Debug.Log($"[DRUM][BeatSeq] chose clip={targetClip.name} depth={depth01:F3} breadth={breadth01:F3} pressure={pressure01:F3}");

        // 5) Avoid redundant scheduling
        if (_pendingDrumLoopArmed && _pendingDrumLoop == targetClip)
        {
            if (logBeatSeqGates) Debug.Log($"[DRUM][BeatSeq] exit: already armed clip={targetClip.name}");
            return;
        }
        var audibleClip = drumAudioSource != null ? drumAudioSource.clip : null;
        if (!_pendingDrumLoopArmed && audibleClip == targetClip)
        {
            if (logBeatSeqGates) Debug.Log($"[DRUM][BeatSeq] exit: already playing clip={targetClip.name}");
            return;
        }
        // If a swap is already scheduled, don't stack another one.
        if (_pendingDrumLoopDspStart > 0.0)
        {
            if (logBeatSeqGates) Debug.Log($"[DRUM][BeatSeq] exit: swap already scheduled dsp={_pendingDrumLoopDspStart:F3}");
            return;
        }

        // 6) Min-dwell gate: don't flip to a different clip too soon after the last swap,
        // even if it's now the nearest match — per-axis hysteresis alone doesn't prevent
        // two axes moving in opposite directions from flipping the selected clip.
        if (targetClip != audibleClip && _lastClipSwapFinalizedDsp > 0.0)
        {
            double dspNow = AudioSettings.dspTime;
            double sinceSwap = dspNow - _lastClipSwapFinalizedDsp;
            if (sinceSwap < config.minClipDwellSeconds)
            {
                if (logBeatSeqGates) Debug.Log($"[DRUM][BeatSeq] exit: min-dwell active ({sinceSwap:F2}s < {config.minClipDwellSeconds:F2}s) candidate={targetClip.name}");
                return;
            }
        }

        // 7) Schedule at boundary
        ScheduleDrumLoopChange(targetClip);
        if (logBeatSeqGates) Debug.Log($"[DRUM][BeatSeq] scheduled clip={targetClip.name}");
    }

    // Exponential smoothing + hysteresis dead-zone, shared by all three axes.
    private float SmoothAxis(float raw, ref float last, float smoothFactor, float hysteresis)
    {
        float smoothed = Mathf.Lerp(last, raw, smoothFactor);
        if (Mathf.Abs(smoothed - last) < hysteresis)
            smoothed = last;
        last = smoothed;
        return smoothed;
    }

    // Depth: how long (in bars) the current leader loop is, relative to its cap.
    // 0 = no track has expanded past 1x; 1 = the loop is at its max multiplier.
    private float ComputeDepth01()
    {
        int mul = GetLeaderMultiplier();
        int cap = GetLeaderMultiplierCap();
        return cap <= 1 ? 1f : Mathf.Clamp01((mul - 1) / (float)(cap - 1));
    }

    // Breadth: fraction of allocated bins that are filled, across all InstrumentTracks.
    private float ComputeBreadth01()
    {
        return _trackController != null ? _trackController.GetBinCompletionFraction() : 0f;
    }

    // Pressure: weighted blend of maze dust density (high = high pressure) and fleet fuel
    // scarcity (low fuel = high pressure). Deliberately named to avoid confusion with
    // Vehicle._lastPressureFactor, an unrelated physical steering concept.
    private float ComputeDrumPressure01()
    {
        float dust01 = _dust != null ? _dust.GetSolidFraction() : 0f;
        float fuelScarcity01 = 1f - Vehicle.GetFleetFuelRatio01();
        float wSum = Mathf.Max(0.0001f, config.pressureDustWeight + config.pressureFuelWeight);
        return Mathf.Clamp01((dust01 * config.pressureDustWeight + fuelScarcity01 * config.pressureFuelWeight) / wSum);
    }

    public void ResetBeatSequencingState(string who)
    {
        // Do NOT clear _motif or loop lists. This is a state reset, not a motif reset.
        _lastDepth01 = 0f;
        _lastBreadth01 = 0f;
        _lastPressure01 = 0f;

        if (GameFlowManager.VerboseLogging) Debug.Log($"[DRUM][BeatSeq] Soft reset by {who} motif={(_motif ? _motif.motifId : "null")}");
    }
}
