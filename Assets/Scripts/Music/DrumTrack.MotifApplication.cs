using System;
using UnityEngine;

public partial class DrumTrack
{
    // Floor for the incoming/outgoing deck crossfade window when ArmPendingDrumLoopForNextLeaderBoundary's
    // fallback engages (see the crossStart collapse check below) — keeps the two decks from converging
    // in near-zero time even under a stale-timing race.
    private const double kMinFadeWindowSec = 0.025;

    private AudioClip ChooseEntryClip()
    {
        if (_entryLoops == null || _entryLoops.Count == 0) return null;
        int i = UnityEngine.Random.Range(0, _entryLoops.Count);
        return _entryLoops[i];
    }

    // Nearest-match selection: picks whichever tagged clip's (depth,breadth,pressure) target
    // is closest to the live-computed axis point. Squared distance avoids a sqrt per
    // candidate since only comparisons are needed. Ties resolve to the first-in-list entry,
    // matching the determinism guarantee this pool needs (same as GetConfigForRoleAtBin).
    // excludeClip lets a caller drop the currently-overstaying clip (see IntensityClipTag.
    // maxConsecutiveLoopSeconds) out of the search so a different tag can take over.
    private AudioClip ResolveNearestIntensityClip(float depth01, float breadth01, float pressure01, AudioClip excludeClip = null)
    {
        if (_intensityTags == null || _intensityTags.Count == 0) return null;

        float bestDist = float.MaxValue;
        AudioClip best = null;
        for (int i = 0; i < _intensityTags.Count; i++)
        {
            var t = _intensityTags[i];
            if (t.clip == null) continue;
            if (excludeClip != null && t.clip == excludeClip) continue;
            float dd = depth01 - t.depth01;
            float db = breadth01 - t.breadth01;
            float dp = pressure01 - t.pressure01;
            float dist = dd * dd + db * db + dp * dp;
            if (dist < bestDist)
            {
                bestDist = dist;
                best = t.clip;
            }
        }
        return best;
    }

    // Looks up the authored max-consecutive-loop cap for a given clip (0 = no cap). If
    // multiple tags reference the same physical clip, only the first match is consulted.
    private float GetMaxConsecutiveLoopSeconds(AudioClip clip)
    {
        if (_intensityTags == null || clip == null) return 0f;
        for (int i = 0; i < _intensityTags.Count; i++)
            if (_intensityTags[i].clip == clip) return _intensityTags[i].maxConsecutiveLoopSeconds;
        return 0f;
    }

    // Decodes this motif's drum clips up front (well ahead of ArmPendingDrumLoopForNextLeaderBoundary
    // scheduling them), so the dual-deck crossfade never has to decode a cold CompressedInMemory
    // clip in the ~50-150ms window between clip assignment and PlayScheduled.
    private void PreloadMotifDrumClips()
    {
        if (_entryLoops != null)
        {
            for (int i = 0; i < _entryLoops.Count; i++)
                PreloadClip(_entryLoops[i]);
        }
        if (_intensityTags != null)
        {
            for (int i = 0; i < _intensityTags.Count; i++)
                PreloadClip(_intensityTags[i].clip);
        }
    }

    private void PreloadClip(AudioClip clip)
    {
        if (clip == null || _preloadedClips.Contains(clip)) return;
        clip.LoadAudioData();
        _preloadedClips.Add(clip);
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
        _intensityTags       = _motif?.intensityClipTags;
        _driveFromDensity    = _motif != null && _motif.driveBeatsFromDensity;

        PreloadMotifDrumClips();

        // Carry latch is one-way for the session: survives motif changes unless the incoming
        // motif opts into re-arming the intro hold. A hard transport restart always re-arms.
        if (restartTransport || (motifChanged && _motif != null && _motif.resetFirstCarryOnStart))
            _carryLatched = false;

        // Only reset entry window and intensity sampling when the motif actually changed,
        // OR when restartTransport forces hard-reset semantics.
        if (motifChanged || restartTransport)
        {
            _entryLoopsRemaining  = _motif != null ? Mathf.Max(0, _motif.entryLoopCount) : 0;
            // Preserve intensity when timing is identical — same BPM/steps means no perceptual
            // discontinuity and resetting would cause a spurious intensity dip.
            if (stepsChanged || bpmChanged)
            {
                _lastDepth01 = 0f;
                _lastBreadth01 = 0f;
                _lastPressure01 = 0f;
            }
        }

        if (_motif == null) return;
        if (!_motif.driveBeatsFromDensity)
            Debug.LogWarning($"[DRUM][MOTIF] Motif {_motif.motifId} has driveBeatsFromDensity=FALSE. Beat intensity will never run.");
        int entryCt = _motif.entryDrumLoops?.Count ?? 0;
        int tagCt   = _motif.intensityClipTags?.Count ?? 0;
        if (entryCt == 0) Debug.LogWarning($"[DRUM][MOTIF] Motif {_motif.motifId} has 0 entryDrumLoops.");
        if (tagCt   == 0) Debug.LogWarning($"[DRUM][MOTIF] Motif {_motif.motifId} has 0 intensityClipTags. Intensity changes cannot occur.");
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
                $"entryLoops={_entryLoops?.Count ?? 0} intensityTags={_intensityTags?.Count ?? 0} " +
                $"entryLoopRemaining={_entryLoopsRemaining} drive={_driveFromDensity} " +
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

        WarnIfClipTimingMismatch(_motif, clip);

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

        if (_pendingDrumLoopDspStart > 0.0)
        {
            // A swap is already physically scheduled (inactive deck has a PlayScheduled call
            // pending). Remember the latest target but don't touch the decks again until that
            // swap finishes — stomping it here would desync the in-flight PlayScheduled/
            // SetScheduledEndTime pair.
            if (GameFlowManager.VerboseLogging) Debug.Log($"[DRUM] ScheduleClipChange: swap already in-flight for dsp={_pendingDrumLoopDspStart:F3}; deferring new target clip={clip.name}");
            _pendingDrumLoop = clip;
            _pendingDrumLoopArmed = armAtNextBoundary;
            _pendingSwapPhraseTarget = ResolvePhraseLengthLoops(_motif);
            return;
        }

        _pendingDrumLoop = clip;
        _pendingDrumLoopArmed = armAtNextBoundary;
        _pendingSwapPhraseTarget = ResolvePhraseLengthLoops(_motif);
        _pendingSwapLoopsElapsed = 0;
    }

    // Resolves how many completed leader-loop boundaries to wait before an armed swap is
    // allowed to fire, so a transition gets a full musical phrase of runway instead of
    // arming after just one bar. Per-motif override wins; falls back to the config default.
    private int ResolvePhraseLengthLoops(MotifProfile motif)
    {
        int overrideVal = motif != null ? motif.phraseLengthLoopsOverride : 0;
        if (overrideVal > 0) return overrideVal;
        return config != null ? Mathf.Max(1, config.defaultTransitionPhraseLengthLoops) : 1;
    }

    // Warns when a chosen clip's real length isn't a whole-number subdivision of
    // motif.bpm/stepsPerLoop's authored phrase length. Runtime counterpart to
    // MotifProfile.ValidateTimingAgainstClips — needed because intensityClipTags are chosen
    // dynamically (ResolveNearestIntensityClip) and won't re-trigger OnValidate.
    //
    // A clip shorter than the authored phrase is expected to loop natively N times underneath
    // it (see DrumTrack.GetAuthoredBaseLoopSeconds) — only a non-integer ratio is a real mismatch.
    private void WarnIfClipTimingMismatch(MotifProfile motif, AudioClip clip)
    {
        if (motif == null || clip == null || clip.length <= 0f || motif.bpm <= 0f || motif.stepsPerLoop <= 0) return;
        float impliedLoopSec = motif.stepsPerLoop * (60f / motif.bpm) / 4f;
        int n = Mathf.Max(1, Mathf.RoundToInt(impliedLoopSec / clip.length));
        float expectedClipSec = impliedLoopSec / n;
        float delta = Mathf.Abs(clip.length - expectedClipSec);
        float tol = Mathf.Max(0.02f, expectedClipSec * 0.02f);
        if (delta > tol)
            Debug.LogWarning($"[DRUM][MOTIF][TIMING] motif={motif.motifId} bpm={motif.bpm} stepsPerLoop={motif.stepsPerLoop} implies phrase={impliedLoopSec:F3}s (nearest valid subdivision: {n}x{expectedClipSec:F3}s) but clip '{clip.name}' is {clip.length:F3}s (Δ={delta:F3}s).");
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

        // Safety: must schedule in the future. Reaching this branch means the bar-aligned swapDsp
        // computed above landed at/before dspNow (stale leaderStartDspTime after a frame hitch, or
        // FP drift between this method's barLen and EffectiveLoopLengthSec's independently-computed
        // step duration) — the near-instant fallback below can collapse the crossfade window to ~0
        // and race Unity's PlayScheduled/SetScheduledEndTime DSP-buffer latency, which is a
        // suspected cause of an audible dropout at the swap. Gated behind VerboseLogging since the
        // synchronous console write itself, firing right at the swap instant, was found to compound
        // the audible hiccup — flip verboseLogging on if you need to look for this again.
        if (swapDsp <= dspNow + 0.02)
        {
            if (GameFlowManager.VerboseLogging) Debug.LogWarning($"[DRUM] ArmPendingDrumLoopForNextLeaderBoundary: bar-aligned swapDsp={swapDsp:F3} was <= dspNow={dspNow:F3}+0.02 — falling back to near-instant schedule. clip={_pendingDrumLoop?.name} leaderStartDspTime={leaderStartDspTime:F3}");
            swapDsp = dspNow + 0.08;
        }

        var newClip = _pendingDrumLoop;
        if (newClip == null)
        {
            _pendingDrumLoopArmed = false;
            _pendingSwapLoopsElapsed = 0;
            return;
        }

        // Crossfade window straddling the swap: the incoming deck pre-rolls silently and ramps
        // up while the outgoing deck ramps down, both converging exactly at swapDsp — the
        // bar-aligned instant everything else (SetScheduledEndTime, TickDeckSwap's pointer/
        // timing commit) still keys off unchanged. Only the audio pre-roll start moves earlier.
        float fadeWin = config != null ? Mathf.Max(0f, config.deckCrossfadeSeconds) : 0f;
        fadeWin = Mathf.Min(fadeWin, newClip.length * 0.5f);
        double crossStart = swapDsp - fadeWin;
        if (crossStart <= dspNow + 0.01)
        {
            // Floor is no longer near-zero: PreloadMotifDrumClips() (DrumTrack.MotifApplication.cs)
            // now decodes clips at motif-apply time, well before this method runs, so decode time
            // is no longer part of this race — a real ~25ms fade window is safe to guarantee here.
            double collapsed = System.Math.Max(dspNow + 0.01, swapDsp - kMinFadeWindowSec);
            if (GameFlowManager.VerboseLogging) Debug.LogWarning($"[DRUM] ArmPendingDrumLoopForNextLeaderBoundary: crossfade window collapsed (wanted crossStart={crossStart:F3}, using {collapsed:F3}, swapDsp={swapDsp:F3}) — incoming/outgoing decks converge in {(swapDsp - collapsed):F3}s instead of {fadeWin:F3}s.");
            crossStart = collapsed;
            fadeWin = (float)System.Math.Max(0.0, swapDsp - crossStart);
        }

        _inactiveDrum.clip = newClip;
        _inactiveDrum.loop = true;
        _inactiveDrum.playOnAwake = false;
        _inactiveDrum.volume = 0f;

        // Start the incoming deck fadeWin seconds INTO its own bar rather than at sample 0, so its
        // native loop-wrap — i.e. its own downbeat — lands exactly at swapDsp instead of at
        // crossStart. Starting from sample 0 at crossStart would play the new pattern's opening hits
        // fadeWin seconds before the bar-aligned instant every other system (leaderStartDspTime
        // advancement, InstrumentTrack's note grid, SetScheduledEndTime below) treats as the true
        // downbeat — audible as the drums coming in early, ahead of the instrument note timing.
        // The audible content during the fade-in becomes the tail of the new clip's own bar instead,
        // which is rhythmically continuous since every clip for a motif shares the same bar length.
        _inactiveDrum.time = Mathf.Clamp(newClip.length - fadeWin, 0f, Mathf.Max(0f, newClip.length - 0.0001f));

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
        _inactiveDrum.PlayScheduled(crossStart);

        _crossfadeBaseVolume = _activeDrum.volume;
        _crossfadeStartDsp = crossStart;
        _crossfadeEndDsp = swapDsp;

        _pendingDrumLoopDspStart = swapDsp;

        // Keep flags so Update() can finalize the deck swap when DSP reaches swapDsp.
        _pendingDrumLoopArmed = false;
        _pendingDrumLoop = null;
        _pendingSwapLoopsElapsed = 0;

        // Full-lifecycle trace, gated behind VerboseLogging — captures the volume baseline this
        // whole ramp is keyed off, useful when chasing an audible-dropout report where the arm
        // itself logged a correct clip but nothing was heard. Previously unconditional; gated
        // since firing this synchronous console write right at the arm instant was itself found
        // to compound the audible hiccup being investigated.
        if (GameFlowManager.VerboseLogging) Debug.LogWarning($"[DRUM][TRACE] Armed swap: newClip={newClip.name} swapDsp={swapDsp:F3} crossStart={crossStart:F3} " +
            $"_crossfadeBaseVolume={_crossfadeBaseVolume:F3} activeDrum={_activeDrum.name}(vol={_activeDrum.volume:F3},mute={_activeDrum.mute},clip={(_activeDrum.clip?_activeDrum.clip.name:"null")}) " +
            $"inactiveDrum={_inactiveDrum.name}(vol={_inactiveDrum.volume:F3},mute={_inactiveDrum.mute},clip={(_inactiveDrum.clip?_inactiveDrum.clip.name:"null")},isPlaying={_inactiveDrum.isPlaying})");

        if (GameFlowManager.VerboseLogging) Debug.Log($"[DRUM] Armed drum loop swap for dsp={swapDsp:F3} clip={newClip.name} crossStart={crossStart:F3}");
    }

    private void ScheduleDrumLoopChange(AudioClip newLoop)
    {
        if (newLoop == null)
        {
            Debug.LogWarning("[DrumTrack] ScheduleDrumLoopChange called with null clip.");
            return;
        }

        // Unlike the entry-clip path (ScheduleClipChange), intensity-tag clips picked here by
        // EvaluateAndScheduleClip never went through this check — a mismatched clip silently
        // redefines EffectiveLoopLengthSec (derived from the clip's real length, not from
        // bpm/stepsPerLoop) the moment it becomes active, which reads as the whole motif's
        // tempo drifting mid-playback instead of a one-off warning at the actual cause.
        WarnIfClipTimingMismatch(_motif, newLoop);

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

        // Unlike ScheduleClipChange's pending-path, this was never setting these — meaning every
        // intensity-tag swap ran the phrase-length gate (TickLoopBoundaries) off whatever stale
        // _pendingSwapPhraseTarget/_pendingSwapLoopsElapsed a PRIOR motif/entry-clip change left
        // behind (or, on a fresh session, the raw field initializers), instead of freshly resolving
        // this motif's actual phraseLengthLoopsOverride. Set them here too so every swap path is
        // consistent and phraseLengthLoopsOverride actually takes effect for intensity swaps.
        _pendingSwapPhraseTarget = ResolvePhraseLengthLoops(_motif);
        _pendingSwapLoopsElapsed = 0;
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
