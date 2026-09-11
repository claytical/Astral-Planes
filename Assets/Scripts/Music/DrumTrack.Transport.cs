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
        EnsureBinFillSubscription();

        if (drumAudioSource == null || totalSteps <= 0)
            return;
        if (!HasValidClipLen)
            return;

        if (leaderStartDspTime <= 0.0)
            leaderStartDspTime = startDspTime;

        if (!_carryLatched && Vehicle.AnyVehicleCarrying())
            _carryLatched = true;

        TickLoopBoundaries();
        TickDeckCrossfade();
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

            if (_pendingDrumLoopArmed && _pendingDrumLoop != null)
            {
                _pendingSwapLoopsElapsed++;
                if (_pendingSwapLoopsElapsed >= _pendingSwapPhraseTarget)
                    ArmPendingDrumLoopForNextLeaderBoundary(nextBoundaryDsp, effLen);
            }
            catchup++;
        }
    }

    // Ramps outgoing/incoming deck volume across the crossfade window armed by
    // ArmPendingDrumLoopForNextLeaderBoundary. Since _crossfadeEndDsp == _pendingDrumLoopDspStart,
    // this always reaches t=1 before TickDeckSwap finalizes the pointer swap below — no ordering
    // hazard between the two. Not DSP-sample-accurate (Update() cadence), which is fine for a
    // short audible volume envelope; only the swap instant itself needs sample accuracy.
    private void TickDeckCrossfade()
    {
        if (_crossfadeStartDsp <= 0.0) return;
        double dspNow = AudioSettings.dspTime;
        if (dspNow < _crossfadeStartDsp) return;

        double span = _crossfadeEndDsp - _crossfadeStartDsp;
        float t = span > 0.0001 ? Mathf.Clamp01((float)((dspNow - _crossfadeStartDsp) / span)) : 1f;

        if (_activeDrum != null)   _activeDrum.volume   = Mathf.Lerp(_crossfadeBaseVolume, 0f, t);
        if (_inactiveDrum != null) _inactiveDrum.volume  = Mathf.Lerp(0f, _crossfadeBaseVolume, t);

        if (t >= 1f)
        {
            if (_activeDrum != null)   _activeDrum.volume   = 0f;
            if (_inactiveDrum != null) _inactiveDrum.volume  = _crossfadeBaseVolume;
            if (GameFlowManager.VerboseLogging) Debug.LogWarning($"[DRUM][TRACE] Crossfade ramp complete: active(pre-swap)={_activeDrum?.name}(vol={_activeDrum?.volume:F3}) " +
                $"inactive(pre-swap, about to become active)={_inactiveDrum?.name}(vol={_inactiveDrum?.volume:F3},playing={_inactiveDrum?.isPlaying},mute={_inactiveDrum?.mute})");
            _crossfadeStartDsp = -1.0;
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

        // This can fire up to kSwapEps before TickDeckCrossfade's own t>=1 completion (that check
        // has no matching epsilon). If the crossfade hasn't finalized yet, force it now on the
        // CURRENT (pre-swap) references — otherwise a late TickDeckCrossfade call on a later frame
        // would apply its "ramp complete" volumes to the ALREADY-SWAPPED decks, silencing the deck
        // that just became active instead of the one that just went inactive. Confirmed via trace:
        // this produced exactly that — the newly-active deck getting set to volume 0.
        if (_crossfadeStartDsp > 0.0)
        {
            if (_activeDrum != null)   _activeDrum.volume   = 0f;
            if (_inactiveDrum != null) _inactiveDrum.volume  = _crossfadeBaseVolume;
            _crossfadeStartDsp = -1.0;
        }

        var prevActive = _activeDrum;
        _activeDrum = _inactiveDrum;
        _inactiveDrum = prevActive;
        drumAudioSource = _activeDrum;
        if (_inactiveDrum != null)
        {
            try { _inactiveDrum.Stop(); } catch { }
        }

        if (GameFlowManager.VerboseLogging) Debug.LogWarning($"[DRUM][TRACE] Deck swap finalized: newActive={_activeDrum?.name}(vol={_activeDrum?.volume:F3},mute={_activeDrum?.mute},playing={_activeDrum?.isPlaying},clip={(_activeDrum!=null&&_activeDrum.clip?_activeDrum.clip.name:"null")}) " +
            $"mixerGroup={(_activeDrum!=null?_activeDrum.outputAudioMixerGroup?.name:"null")} AudioListener.volume={AudioListener.volume}");

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
        else if (_motif != null)
        {
            // Intensity-tag swaps (ScheduleDrumLoopChange, DrumTrack.MotifApplication.cs) never
            // stage pending timing the way a real motif change does — reassert the current
            // motif's authored bpm/stepsPerLoop here so totalSteps/drumLoopBPM can't go stale
            // relative to _clipLengthSec (which always updates above regardless of swap path).
            // Tag clips are authored to share the motif's timing (MotifProfile.ValidateTimingAgainstClips),
            // so this keeps every step-duration consumer (GetEffectiveLoopLengthInSeconds,
            // GetDspStepIndexInBar, TicksToSeconds, TickStepAndBinIndexing) reading a consistent pair.
            drumLoopBPM = _motif.bpm;
            totalSteps  = Mathf.Max(1, _motif.stepsPerLoop);
        }

        if (logBeatSeqGates) Debug.Log($"[DRUM] Finalized drum loop swap at dsp={_pendingDrumLoopDspStart:F3} clip={(_currentDrumClip ? _currentDrumClip.name : "null")}");
        _lastClipSwapFinalizedDsp = _pendingDrumLoopDspStart;
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

    // Base step count for the currently-committed (or, during a deferred BPM/step transition,
    // about-to-commit) motif grid. Shared by GetLeaderMultiplier/GetLeaderSteps so the two never
    // read from different step-count epochs in the same call.
    private int CurrentOrPendingTotalSteps() => Mathf.Max(1, _pendingTimingValid ? _pendingTotalSteps : totalSteps);

    // Largest live-track loop multiplier, driving both leader-loop length and the depth axis.
    public int GetLeaderMultiplier()
    {
        if (_trackController == null || _trackController.tracks == null || _trackController.tracks.Length == 0)
            return 1;

        // RebuildTracksForMotif rebuilds tracks for the incoming motif's step grid immediately,
        // but totalSteps itself only commits at the eventual deck-swap boundary when a tempo/step
        // change is in flight (see ResolveTimingCommit's deferred-timing branch). Comparing the
        // freshly-rebuilt tracks against the stale, not-yet-committed totalSteps here inflates the
        // multiplier by the old/new step ratio for the whole deferred window — use the pending
        // value instead so the comparison matches what the tracks were actually built for.
        int baseSteps = CurrentOrPendingTotalSteps();
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
        return maxMul;
    }

    // Largest maxLoopMultiplier cap among live tracks — the denominator for the depth axis.
    public int GetLeaderMultiplierCap()
    {
        if (_trackController == null || _trackController.tracks == null || _trackController.tracks.Length == 0)
            return 1;

        int cap = 1;
        foreach (var t in _trackController.tracks)
        {
            if (t == null) continue;
            cap = Mathf.Max(cap, Mathf.Max(1, t.maxLoopMultiplier));
        }
        return cap;
    }

    public int GetLeaderSteps() => CurrentOrPendingTotalSteps() * GetLeaderMultiplier();

    public float GetTimeToLoopEnd(bool effective = true)
    {
        float L = effective ? EffectiveLoopLengthSec : _clipLengthSec;
        if (L <= 0f) return 0f;
        float elapsed = (float)((AudioSettings.dspTime - startDspTime) % L);
        return Mathf.Max(0f, L - elapsed);
    }
}
