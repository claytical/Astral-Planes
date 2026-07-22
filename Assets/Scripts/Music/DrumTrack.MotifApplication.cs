using System;
using UnityEngine;

public partial class DrumTrack
{
    private AudioClip ChooseEntryClip()
    {
        if (_entryLoops == null || _entryLoops.Count == 0) return null;
        int i = UnityEngine.Random.Range(0, _entryLoops.Count);
        return _entryLoops[i];
    }

    private AudioClip ResolveIntensityClip(float intensity01)
    {
        if (_intensityLoops == null || _intensityLoops.Count == 0) return null;

        int n = _intensityLoops.Count;
        if (n == 1) return _intensityLoops[0];

        intensity01 = Mathf.Clamp01(intensity01);

        // Even mapping: 0 -> 0, 1 -> n-1
        int idx = Mathf.RoundToInt(intensity01 * (n - 1));
        idx = Mathf.Clamp(idx, 0, n - 1);

        return _intensityLoops[idx];
    }

    private void ApplyMotif(MotifProfile motif, bool armAtNextBoundary, string who, bool restartTransport = false)
    {
        string incomingId = motif ? motif.motifId : "null";
        TrackMotifApplication(incomingId, who);

        MotifProfile oldMotif = _motif;
        bool motifChanged = oldMotif != motif;
        bool stepsChanged = motif != null && oldMotif != null && oldMotif.stepsPerLoop != motif.stepsPerLoop;
        bool bpmChanged   = motif != null && oldMotif != null && Mathf.Abs(oldMotif.bpm - motif.bpm) > 0.001f;
        int  oldSteps     = oldMotif?.stepsPerLoop ?? totalSteps;
        float oldBpm      = oldMotif?.bpm ?? drumLoopBPM;

        CommitMotifRefs(motif, motifChanged, restartTransport, stepsChanged, bpmChanged);
        AudioClip clip = ChooseEntryClip();
        armAtNextBoundary = ResolveTimingCommit(motif, armAtNextBoundary, motifChanged, stepsChanged, bpmChanged,
                                                restartTransport, oldSteps, oldBpm, clip, who);
        ScheduleClipChange(clip, armAtNextBoundary, restartTransport, who);
    }

    private void TrackMotifApplication(string incomingId, string who)
    {
        double dspNow = AudioSettings.dspTime;
        if (_lastApplyMotifId == incomingId && _lastApplyMotifDsp > 0 && (dspNow - _lastApplyMotifDsp) < 10.0)
        {
            Debug.LogWarning(
                $"[DRUM][MOTIF][SPAM] Reapplying motif={incomingId} within {(dspNow - _lastApplyMotifDsp):F3}s by {who}\n" +
                Environment.StackTrace
            );
        }
        _lastApplyMotifId  = incomingId;
        _lastApplyMotifDsp = dspNow;
        _motifSetSerial++;
        if (GameFlowManager.VerboseLogging) Debug.Log($"[DRUM][MOTIF][SET#{_motifSetSerial}] by {who}: incoming motif={incomingId}");
    }

    private void CommitMotifRefs(MotifProfile motif, bool motifChanged, bool restartTransport, bool stepsChanged, bool bpmChanged)
    {
        _motif               = motif;
        _entryLoops          = _motif?.entryDrumLoops;
        _intensityLoops      = _motif?.intensityDrumLoops;
        _driveFromEnergy     = _motif != null && _motif.driveBeatsFromEnergy;

        // Carry latch is one-way for the session: survives motif changes unless the incoming
        // motif opts into re-arming the intro hold. A hard transport restart always re-arms.
        if (restartTransport || (motifChanged && _motif != null && _motif.resetFirstCarryOnStart))
            _carryLatched = false;

        // Only reset entry window and intensity sampling when the motif actually changed,
        // OR when restartTransport forces hard-reset semantics.
        if (motifChanged || restartTransport)
        {
            _entryLoopsRemaining  = _motif != null ? Mathf.Max(0, _motif.entryLoopCount) : 0;
            _lastTotalSpentSample = -1f;
            _burnTier             = 0f;
            // Preserve intensity when timing is identical — same BPM/steps means no perceptual
            // discontinuity and resetting would cause a spurious intensity dip.
            if (stepsChanged || bpmChanged)
                _lastIntensity01 = 0f;
        }

        if (_motif == null) return;
        if (!_motif.driveBeatsFromEnergy)
            Debug.LogWarning($"[DRUM][MOTIF] Motif {_motif.motifId} has driveBeatsFromEnergy=FALSE. Beat intensity will never run.");
        int entryCt = _motif.entryDrumLoops?.Count ?? 0;
        int intCt   = _motif.intensityDrumLoops?.Count ?? 0;
        if (entryCt == 0) Debug.LogWarning($"[DRUM][MOTIF] Motif {_motif.motifId} has 0 entryDrumLoops.");
        if (intCt   == 0) Debug.LogWarning($"[DRUM][MOTIF] Motif {_motif.motifId} has 0 intensityDrumLoops. Intensity changes cannot occur.");
    }

    // Applies deferred or immediate timing depending on transport state.
    // Returns the (possibly forced) armAtNextBoundary value for ScheduleClipChange.
    private bool ResolveTimingCommit(MotifProfile motif, bool armAtNextBoundary, bool motifChanged,
        bool stepsChanged, bool bpmChanged, bool restartTransport,
        int oldSteps, float oldBpm, AudioClip clip, string who)
    {
        if (_started && (stepsChanged || bpmChanged) && !restartTransport)
        {
            // Timing changed while transport is running — must arm at boundary to avoid mismatch.
            if (!armAtNextBoundary)
            {
                Debug.LogWarning(
                    $"[DRUM][MOTIF] Timing changed (steps {oldSteps}->{(motif ? motif.stepsPerLoop : oldSteps)}, " +
                    $"bpm {oldBpm}->{(motif ? motif.bpm : oldBpm)}) but armAtNextBoundary==false and restartTransport==false. " +
                    $"Forcing armAtNextBoundary=true to avoid transport mismatch."
                );
                armAtNextBoundary = true;
            }

            _pendingBpm         = motif != null ? motif.bpm : drumLoopBPM;
            _pendingTotalSteps  = motif != null ? motif.stepsPerLoop : totalSteps;
            _pendingTimingValid = true;

            if (GameFlowManager.VerboseLogging) Debug.Log(
                $"[DRUM][MOTIF] ApplyMotif(DEFERRED TIMING) by {who}: motif={(motif ? motif.motifId : "null")} " +
                $"steps {oldSteps}->{_pendingTotalSteps} bpm {oldBpm}->{_pendingBpm} " +
                $"clip={(clip ? clip.name : "null")} armAtNextBoundary={armAtNextBoundary} " +
                $"restart={restartTransport} motifChanged={motifChanged}"
            );
        }
        else
        {
            // Safe to commit timing immediately: not yet started, restartTransport, or no timing change.
            if (_motif != null)
            {
                drumLoopBPM = _motif.bpm;
                totalSteps  = _motif.stepsPerLoop;
            }
            _pendingTimingValid = false;

            if (GameFlowManager.VerboseLogging) Debug.Log(
                $"[DRUM][MOTIF] ApplyMotif by {who}: motif={(_motif ? _motif.motifId : "null")} " +
                $"entryLoops={_entryLoops?.Count ?? 0} intensityLoops={_intensityLoops?.Count ?? 0} " +
                $"entryLoopRemaining={_entryLoopsRemaining} drive={_driveFromEnergy} " +
                $"bpm={drumLoopBPM} steps={totalSteps} clip={(clip ? clip.name : "null")} " +
                $"armAtNextBoundary={armAtNextBoundary} restart={restartTransport} motifChanged={motifChanged}"
            );
        }

        return armAtNextBoundary;
    }

    // Applies clip to the transport: hard-restarts decks if restartTransport, otherwise arms a pending swap.
    private void ScheduleClipChange(AudioClip clip, bool armAtNextBoundary, bool restartTransport, string who)
    {
        if (clip == null)
        {
            Debug.LogWarning($"[DRUM][MOTIF] No entry clip available for motif '{(_motif ? _motif.motifId : "null")}' (by {who}).");
            return;
        }

        if (!_started)
            return;

        if (restartTransport)
        {
            try { if (_activeDrum != null) _activeDrum.Stop(); } catch { }
            try { if (_inactiveDrum != null) _inactiveDrum.Stop(); } catch { }
            _pendingDrumLoop = null;
            _pendingDrumLoopArmed = false;

            if (_motif != null)
            {
                drumLoopBPM = _motif.bpm;
                totalSteps  = _motif.stepsPerLoop;
            }
            _pendingTimingValid = false;

            EnsureDualDrumSources();
            if (_activeDrum == null) return;

            _activeDrum.clip = clip;
            _activeDrum.loop = true;
            _clipLengthSec = Mathf.Max(clip.length, 0f);

            double dspStart = AudioSettings.dspTime + 0.05;
            _activeDrum.PlayScheduled(dspStart);
            drumAudioSource = _activeDrum;
            startDspTime = dspStart;
            leaderStartDspTime = dspStart;
            _currentDrumClip = clip;
            return;
        }

        _pendingDrumLoop = clip;
        _pendingDrumLoopArmed = armAtNextBoundary;
    }

    public void SetMotifBeatSequence(MotifProfile motif, bool armAtNextBoundary, string who, bool restartTransport = false) {
        ApplyMotif(motif, armAtNextBoundary, who, restartTransport);
    }

    private void ArmPendingDrumLoopForNextLeaderBoundary(double nextBoundaryDsp, double effectiveLoopLen)
    {
        if (!_pendingDrumLoopArmed || _pendingDrumLoop == null)
            return;

        EnsureDualDrumSources();
        if (_activeDrum == null || _inactiveDrum == null)
            return;

        double dspNow = AudioSettings.dspTime;

        // We *want* to change near the leader boundary, but we must not cut a looping drum clip mid-bar.
        // So: pick a swap time at/after nextBoundaryDsp that lands on a drum-bar boundary (clip boundary).
        double swapDsp = nextBoundaryDsp;

        if (_activeDrum.clip != null && _activeDrum.clip.length > 0.0001f)
        {
            double barLen = _activeDrum.clip.length;

            // Anchor bar counting off the current *leader* start so swaps stay musically consistent.
            // IMPORTANT: do NOT re-anchor leaderStartDspTime to a FUTURE swap time (that freezes transport).
            double t = swapDsp - leaderStartDspTime;
            if (t < 0) t = 0;

            double bars = System.Math.Ceiling(t / barLen);
            swapDsp = leaderStartDspTime + bars * barLen;
        }

        // Safety: must schedule in the future
        if (swapDsp <= dspNow + 0.01)
            swapDsp = dspNow + 0.05;

        var newClip = _pendingDrumLoop;
        if (newClip == null)
        {
            _pendingDrumLoopArmed = false;
            return;
        }


        _inactiveDrum.clip = newClip;
        _inactiveDrum.loop = true;
        _inactiveDrum.playOnAwake = false;

        // Try to end active at the swap boundary (avoids overlaps)
        try
        {
            _activeDrum.SetScheduledEndTime(swapDsp);
        }
        catch (Exception e)
        {
            Debug.LogError($"[DRUM] SetScheduledEndTime FAILED on active clip={(_activeDrum.clip ? _activeDrum.clip.name : "null")} swapDsp={swapDsp:F3}\n{e}");
        }
        // Schedule the new loop, but DO NOT swap references yet.
        // The currently-audible deck must remain authoritative until swapDsp arrives.
        // Note: assigning .clip above already stopped any prior playback on _inactiveDrum,
        // so there is no need to check isPlaying before calling PlayScheduled.
        _inactiveDrum.PlayScheduled(swapDsp);

        _pendingDrumLoopDspStart = swapDsp;

        // Keep flags so Update() can finalize the deck swap when DSP reaches swapDsp.
        _pendingDrumLoopArmed = false;
        _pendingDrumLoop = null;

        if (GameFlowManager.VerboseLogging) Debug.Log($"[DRUM] Armed drum loop swap for dsp={swapDsp:F3} clip={newClip.name}");
    }

    private void ScheduleDrumLoopChange(AudioClip newLoop)
    {
        if (newLoop == null)
        {
            Debug.LogWarning("[DrumTrack] ScheduleDrumLoopChange called with null clip.");
            return;
        }

        // If a swap is already scheduled (inactive already has PlayScheduled),
        // DO NOT clear _pendingDrumLoopDspStart, or we will never finalize the swap.
        if (_pendingDrumLoopDspStart > 0.0)
        {
            // If you want, you can remember a "next-next" clip here. For now: ignore.
            if (GameFlowManager.VerboseLogging) Debug.Log($"[DRUM] ScheduleDrumLoopChange ignored; swap already scheduled for dsp={_pendingDrumLoopDspStart:F3} new={newLoop.name}");
            return;
        }

        _pendingDrumLoop = newLoop;
        _pendingDrumLoopArmed = true;

        // Only clear this when we are truly arming a not-yet-scheduled swap.
        _pendingDrumLoopDspStart = -1.0;
    }

    private void EnsureDualDrumSources()
    {
        if (drumAudioSource == null)
        {
            Debug.LogError("[DrumTrack] EnsureDualDrumSources: drumAudioSource is null.");
            return;
        }

        // Deck A is ALWAYS the inspector-assigned drumAudioSource.
        if (_drumA == null) _drumA = drumAudioSource;

        var go = _drumA.gameObject;
        var all = go.GetComponents<AudioSource>();

        // Find a different AudioSource to use as Deck B.
        AudioSource candidateB = null;
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i] != null && all[i] != _drumA)
            {
                candidateB = all[i];
                break;
            }
        }

        if (candidateB == null)
        {
            candidateB = go.AddComponent<AudioSource>();

            // Clone core settings
            candidateB.playOnAwake = false;
            candidateB.outputAudioMixerGroup = _drumA.outputAudioMixerGroup;
            candidateB.volume = _drumA.volume;
            candidateB.pitch = _drumA.pitch;
            candidateB.panStereo = _drumA.panStereo;
            candidateB.spatialBlend = _drumA.spatialBlend;
            candidateB.reverbZoneMix = _drumA.reverbZoneMix;
            candidateB.dopplerLevel = _drumA.dopplerLevel;
            candidateB.spread = _drumA.spread;
            candidateB.rolloffMode = _drumA.rolloffMode;
            candidateB.minDistance = _drumA.minDistance;
            candidateB.maxDistance = _drumA.maxDistance;
            candidateB.priority = _drumA.priority;
        }

        _drumB = candidateB;

        if (_activeDrum == null) _activeDrum = _drumA;
        if (_activeDrum != _drumA && _activeDrum != _drumB) _activeDrum = _drumA;

        _inactiveDrum = (_activeDrum == _drumA) ? _drumB : _drumA;

        drumAudioSource = _activeDrum;
    }

    private void StopAllOtherDrumSources(AudioSource keepPlaying)
    {
        var sources = gameObject.GetComponents<AudioSource>();
        for (int i = 0; i < sources.Length; i++)
        {
            var s = sources[i];
            if (s == null) continue;
            if (s == keepPlaying) continue;

            if (s.isPlaying)
            {
                try { s.Stop(); } catch { }
            }
        }
    }
}
