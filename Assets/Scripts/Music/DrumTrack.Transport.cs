using UnityEngine;

public partial class DrumTrack
{
    // 1) Late-bind motif is ONE-SHOT (only when _motif==null). It will NOT re-apply repeatedly.
    // 2) Transport boundary catch-up remains the same.
    // 3) Finalize A/B deck swap occurs BEFORE step/bin calculations (so events reflect the audible deck ASAP).
    // 4) Watchdog remains at end.
    // 5) Guards are consolidated so we don’t early-return before finalizing a swap.
    private void Update()
    {
        if (_gfm == null || !_gfm.ReadyToPlay())
            return;

        TickMotifLateBind();
        TickGridValidation();

        if (drumAudioSource == null || totalSteps <= 0)
            return;
        if (!HasValidClipLen)
            return;

        if (leaderStartDspTime <= 0.0)
            leaderStartDspTime = startDspTime;

        if (!_carryLatched && Vehicle.AnyVehicleCarrying())
            _carryLatched = true;

        TickLoopBoundaries();
        TickDeckSwap();
        if (!TickStepAndBinIndexing()) return;
        TickWatchdog();

        if (activeMineNodes != null && activeMineNodes.Count > 0)
            activeMineNodes.RemoveAll(n => n == null);
    }

    // ONE SHOT ONLY: only if we truly have no motif (late-bind recovery).
    // Do NOT late-bind just because intensityLoops is empty, drive is false, etc.
    private void TickMotifLateBind()
    {
        _lateBindMotifTimer += Time.deltaTime;
        if (_lateBindMotifTimer < kLateBindMotifInterval) return;
        _lateBindMotifTimer = 0f;

        if (_motif != null) return;

        var ptm = _phaseTransitionManager != null ? _phaseTransitionManager : GameFlowManager.Instance?.phaseTransitionManager;
        var m = ptm != null ? ptm.currentMotif : null;
        if (m != null)
        {
            if (GameFlowManager.VerboseLogging) Debug.Log($"[DRUM][MOTIF] Late-bind applying PTM motif={m.motifId}");
            ApplyMotif(m, armAtNextBoundary: true, who: "DrumTrack/LateBind", restartTransport: false);
        }
    }

    private void TickGridValidation()
    {
        _gridCheckTimer += Time.deltaTime;
        if (_gridCheckTimer < _gridCheckInterval) return;
        _gridMapper.ValidateSpawnGrid();
        _gridCheckTimer = 0f;
    }

    private void TickLoopBoundaries()
    {
        double effLen = Mathf.Max(0.0001f, EffectiveLoopLengthSec);
        const int kMaxBoundaryCatchup = 4;
        int catchup = 0;

        while (AudioSettings.dspTime - leaderStartDspTime >= effLen && catchup < kMaxBoundaryCatchup)
        {
            leaderStartDspTime += effLen;
            completedLoops++;
            _boundarySerial++;

            double boundaryDsp = leaderStartDspTime;

            if (logBeatSeqGates) Debug.Log($"[DRUM] Loop boundary dsp={boundaryDsp:F3}");
            HandleBeatSequencingAtLoopBoundary((float)effLen);

            OnLoopBoundary?.Invoke();

            effLen = Mathf.Max(0.0001f, EffectiveLoopLengthSec);

            double nextBoundaryDsp = boundaryDsp + effLen;
            double dspNow = AudioSettings.dspTime;
            const double kMinLead = 0.010;
            if (nextBoundaryDsp <= dspNow + kMinLead)
                nextBoundaryDsp = dspNow + 0.050;

            ArmPendingDrumLoopForNextLeaderBoundary(nextBoundaryDsp, effLen);
            catchup++;
        }
    }

    // Finalizes the pending A/B deck swap as soon as the new deck becomes audible.
    // Must run before step/bin indexing so downstream listeners see the current deck ASAP.
    private void TickDeckSwap()
    {
        if (_pendingDrumLoopDspStart <= 0.0) return;

        double dspNow = AudioSettings.dspTime;
        const double kSwapEps = 0.002;
        if (dspNow + kSwapEps < _pendingDrumLoopDspStart) return;

        var prevActive = _activeDrum;
        _activeDrum = _inactiveDrum;
        _inactiveDrum = prevActive;
        drumAudioSource = _activeDrum;
        if (_inactiveDrum != null)
        {
            try { _inactiveDrum.Stop(); } catch { }
        }

        StopAllOtherDrumSources(keepPlaying: _activeDrum);
        _clipLengthSec = (_activeDrum != null && _activeDrum.clip != null)
            ? Mathf.Max(_activeDrum.clip.length, 0f)
            : 0f;

        _currentDrumClip = (_activeDrum != null) ? _activeDrum.clip : null;

        if (_pendingTimingValid)
        {
            drumLoopBPM = _pendingBpm;
            totalSteps  = Mathf.Max(1, _pendingTotalSteps);
            _pendingTimingValid = false;

            if (logBeatSeqGates) Debug.Log(
                $"[DRUM][MOTIF] Committed pending timing at swap: bpm={drumLoopBPM} steps={totalSteps} " +
                $"clip={(_currentDrumClip ? _currentDrumClip.name : "null")}"
            );
        }

        if (logBeatSeqGates) Debug.Log($"[DRUM] Finalized drum loop swap at dsp={_pendingDrumLoopDspStart:F3} clip={(_currentDrumClip ? _currentDrumClip.name : "null")}");
        _pendingDrumLoopDspStart = -1.0;
    }

    // Returns false if the effective loop length is not yet valid (caller should return early).
    private bool TickStepAndBinIndexing()
    {
        int leaderSteps = GetLeaderSteps();
        float effectiveLen = EffectiveLoopLengthSec;
        if (effectiveLen <= 0f) return false;

        float elapsedTime = (float)(AudioSettings.dspTime - leaderStartDspTime);
        float stepDuration = (leaderSteps > 0) ? (effectiveLen / leaderSteps) : 0f;
        if (stepDuration <= 0f || float.IsInfinity(stepDuration)) return false;

        float tInLoop = elapsedTime % effectiveLen;
        int absoluteStep = Mathf.FloorToInt(tInLoop / stepDuration);
        currentStep = absoluteStep % leaderSteps;

        if (currentStep != _lastStepIdx)
        {
            _lastStepIdx = currentStep;
            OnStepChanged?.Invoke(currentStep, leaderSteps);
        }

        if (effectiveLen > kMinLen)
        {
            int bins = Mathf.Max(1, _binCount);
            double dsp = AudioSettings.dspTime;
            double pos = (dsp - leaderStartDspTime) % effectiveLen;
            if (pos < 0) pos += effectiveLen;

            double binDur = effectiveLen / bins;
            const double Eps = 1e-5;
            int idx = (int)((pos + Eps) / binDur);
            if (idx >= bins) idx -= bins;

            if (idx != _binIdx)
                _binIdx = idx;
        }

        return true;
    }

    private void TickWatchdog()
    {
        if (_activeDrum == null || _activeDrum.clip == null) return;
        if (leaderStartDspTime <= 0.0 || _activeDrum.isPlaying) return;

        double dspNow = AudioSettings.dspTime;
        double restart = dspNow + 0.05;

        try { _activeDrum.Stop(); } catch { }
        _activeDrum.loop = true;
        _activeDrum.PlayScheduled(restart);

        startDspTime = restart;
        leaderStartDspTime = restart;
        _clipLengthSec = Mathf.Max(_activeDrum.clip.length, 0f);

        Debug.LogWarning($"[DRUM] Watchdog restart: clip={_activeDrum.clip.name} dsp={restart:F3}");
    }

    public bool TryGetNextBaseStepDsp(out double nextStepDsp, out float stepDurationSec, int stepOffset = 1)
    {
        nextStepDsp = 0;
        stepDurationSec = 0f;

        if (leaderStartDspTime <= 0.0) return false;

        double effLen = Mathf.Max(0.0001f, EffectiveLoopLengthSec);
        int steps = Mathf.Max(1, GetLeaderSteps());  // NOT totalSteps
        stepDurationSec = (float)(effLen / steps);
        if (stepDurationSec <= 0f || float.IsInfinity(stepDurationSec)) return false;

        double dspNow = AudioSettings.dspTime;
        double elapsed = dspNow - leaderStartDspTime;
        if (elapsed < 0) elapsed = 0;

        double tInLoop = elapsed % effLen;

        int curStep = Mathf.FloorToInt((float)(tInLoop / stepDurationSec));
        int targetStep = curStep + Mathf.Max(1, stepOffset);

        nextStepDsp = leaderStartDspTime + (targetStep * stepDurationSec);

        // ensure future
        const double kMinLead = 0.005;
        if (nextStepDsp <= dspNow + kMinLead)
            nextStepDsp = dspNow + 0.010;

        return true;
    }

    public int GetLeaderSteps()
    {
        int baseSteps = Mathf.Max(1, totalSteps);

        if (_trackController == null || _trackController.tracks == null || _trackController.tracks.Length == 0)
            return baseSteps;

        int maxMul = 1;
        foreach (var t in _trackController.tracks)
        {
            if (t == null) continue;

            // Prefer deriving the multiplier from the track's declared total steps,
            // since loopMultiplier may lag behind during expand/commit transitions.
            int trackSteps = Mathf.Max(1, t.GetTotalSteps());
            int mulFromSteps = Mathf.Max(1, Mathf.RoundToInt(trackSteps / (float)baseSteps));

            // Still consider loopMultiplier as a fallback (and for non-step-based cases).
            int mul = Mathf.Max(mulFromSteps, Mathf.Max(1, t.loopMultiplier));
            maxMul = Mathf.Max(maxMul, mul);
        }
        return baseSteps * maxMul;
    }

    public float GetTimeToLoopEnd(bool effective = true)
    {
        float L = effective ? EffectiveLoopLengthSec : _clipLengthSec;
        if (L <= 0f) return 0f;
        float elapsed = (float)((AudioSettings.dspTime - startDspTime) % L);
        return Mathf.Max(0f, L - elapsed);
    }
}
