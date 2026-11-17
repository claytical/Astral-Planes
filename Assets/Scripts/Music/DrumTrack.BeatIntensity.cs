using UnityEngine;

public partial class DrumTrack
{
    private float _nextIntensityPollTime;

    // Called every loop boundary regardless of poll mode. Owns the entry-window countdown
    // (one tick per loop — it used to tick inside EvaluateAndScheduleClip, which BinFill and
    // Continuous polling would burn through in seconds). In BinFill mode evaluation is owned by
    // OnAnyTrackBinFilled (DrumTrack.BinFillPolling.cs); LoopBoundary and Continuous also
    // evaluate here.
    private void HandleBeatSequencingAtLoopBoundary(float loopSeconds)
    {
        // A held bass drop owns clip selection (DrumTrack.BassDrop.cs).
        if (TickBassDropAtLoopBoundary()) return;

        if (_entryLoopsRemaining > 0)
        {
            _entryLoopsRemaining--;
            if (logBeatSeqGates) Debug.Log($"[DRUM][BeatSeq] entry window tick remain={_entryLoopsRemaining} motif={(_motif ? _motif.motifId : "null")}");
            return;
        }
        if (config != null && config.clipPollMode == ClipPollMode.BinFill) return;
        EvaluateAndScheduleClip();
    }

    // Continuous poll mode: re-evaluate on a short timer so carving moves the drums on the
    // next leader boundary rather than one loop later. Existing guards (armed/scheduled swap,
    // min-dwell, bar-quantized arming) still prevent thrash.
    private void TickContinuousIntensityPoll()
    {
        if (config == null || config.clipPollMode != ClipPollMode.Continuous) return;
        if (config.intensityPollIntervalSeconds <= 0f) return;
        if (Time.time < _nextIntensityPollTime) return;
        _nextIntensityPollTime = Time.time + config.intensityPollIntervalSeconds;
        EvaluateAndScheduleClip();
    }

    // Static role defaults read by CosmicDustGenerator at carve time (DrumIntensityTuning).
    private void PushDrumIntensityDefaults()
    {
        if (config == null) return;
        DrumIntensityTuning.BassCarveWeight    = config.bassCarveWeight;
        DrumIntensityTuning.OtherCarveWeight   = config.otherCarveWeight;
    }

    // Computes depth/breadth/intensity, selects the nearest-match intensity clip, and
    // schedules it. Shared by all poll modes.
    private void EvaluateAndScheduleClip()
    {
        // --- hard gates (log on exit) ---
        if (BassDropActive)
        {
            if (logBeatSeqGates) Debug.Log($"[DRUM][BeatSeq] exit: bass drop holding clip={_bassDropClip.name} remain={_bassDropLoopsRemaining}");
            return;
        }
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

        // 1) Respect entry window (counted down per loop in HandleBeatSequencingAtLoopBoundary)
        if (_entryLoopsRemaining > 0)
        {
            if (logBeatSeqGates) Debug.Log($"[DRUM][BeatSeq] exit: entry window active remain={_entryLoopsRemaining} motif={_motif.motifId}");
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
        float rawDepth01   = SmoothAxis(ComputeDepth01(),   ref _lastDepth01,   config.axisSmoothFactor, config.axisHysteresis);
        float rawBreadth01 = SmoothAxis(ComputeBreadth01(), ref _lastBreadth01, config.axisSmoothFactor, config.axisHysteresis);
        float rawIntensity01 = ComputeIntensity01();
        // Half the axis dead-zone so the smoothed value can actually settle near 0 when idle.
        float intensity01 = SmoothAxis(rawIntensity01, ref _lastPressure01, config.intensitySmoothFactor, config.axisHysteresis * 0.5f);

        // Carving is the throttle: depth/breadth only add flavor in proportion to intensity,
        // so an idle player drains to the clip tagged nearest (0,0,0) no matter how long the
        // loop or how full the bins. Intensity itself occupies the pressure01 tag axis.
        float depth01    = rawDepth01 * intensity01;
        float breadth01  = rawBreadth01 * intensity01;
        float pressure01 = intensity01;

        if (logBeatSeqGates) Debug.Log(
            $"[DRUM][BeatSeq] motif={_motif.motifId} intensity={intensity01:F3} (raw carve energy={rawIntensity01:F3}) " +
            $"depth={rawDepth01:F3}->{depth01:F3} breadth={rawBreadth01:F3}->{breadth01:F3} tags={tagCt}"
        );

        var audibleClip = drumAudioSource != null ? drumAudioSource.clip : null;

        // 3.5) A clip authored with a max-loop cap gets excluded from selection once it's
        // overstayed, letting the next-nearest tag take over. This resets naturally: the
        // exclusion only ever applies to whatever's currently audible, and
        // _lastClipSwapFinalizedDsp advances on every real swap, so the excluded clip is
        // eligible again once something else becomes audible.
        AudioClip excludeClip = null;
        if (audibleClip != null && _lastClipSwapFinalizedDsp > 0.0)
        {
            float cap = GetMaxConsecutiveLoopSeconds(audibleClip);
            if (cap > 0f && (AudioSettings.dspTime - _lastClipSwapFinalizedDsp) >= cap)
            {
                excludeClip = audibleClip;
                if (logBeatSeqGates) Debug.Log($"[DRUM][BeatSeq] clip={audibleClip.name} hit maxConsecutiveLoopSeconds={cap:F1}s, excluding from selection");
            }
        }

        // 4) Select nearest-match target clip
        var targetClip = ResolveNearestIntensityClip(depth01, breadth01, pressure01, excludeClip);
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
        // Bypassed when the switch is forced by the audible clip's own maxConsecutiveLoopSeconds
        // cap (excludeClip != null): an authored cap is an explicit override of this general
        // anti-thrash guard, not something the guard should be allowed to block. Without this,
        // a cap shorter than minClipDwellSeconds could never actually fire — the clip would just
        // keep looping until minClipDwellSeconds elapsed anyway.
        if (excludeClip == null && targetClip != audibleClip && _lastClipSwapFinalizedDsp > 0.0)
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

    // Intensity = carve energy, nothing else (LEGIBILITY_AUDIT.md D1): a pooled, role-weighted,
    // leaky count of recent vehicle carves (CosmicDustGenerator.GetCarveEnergy01). Boosting
    // through dust pushes the drums up; stop carving and they drain back to idle. The only
    // other drum event is the bass drop (DrumTrack.BassDrop.cs), which has its own visual.
    // (The former driving-role ceiling + Bass-fill lift was cut — players had no way to see it.)
    private float ComputeIntensity01()
    {
        float energy01 = _dust != null
            ? _dust.GetCarveEnergy01(config.carveActivityHalfLifeSeconds, config.carveActivitySaturation)
            : 0f;
        return Mathf.Clamp01(energy01);
    }

    public void ResetBeatSequencingState(string who)
    {
        // Do NOT clear _motif or loop lists. This is a state reset, not a motif reset.
        ClearBassDropState();
        _lastDepth01 = 0f;
        _lastBreadth01 = 0f;
        _lastPressure01 = 0f;

        if (GameFlowManager.VerboseLogging) Debug.Log($"[DRUM][BeatSeq] Soft reset by {who} motif={(_motif ? _motif.motifId : "null")}");
    }
}
