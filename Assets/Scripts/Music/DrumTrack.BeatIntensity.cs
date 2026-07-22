using UnityEngine;

public partial class DrumTrack
{
    private void HandleBeatSequencingAtLoopBoundary(float loopSeconds)
    {
        // --- hard gates (log on exit) ---
        if (!_driveFromEnergy)
        {
            if (logBeatSeqGates) Debug.Log($"[DRUM][BeatSeq] exit: driveFromEnergy=false motif={(_motif ? _motif.motifId : "null")}");
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

        // 2) Need an intensity ladder
        int loopsCt = (_intensityLoops != null) ? _intensityLoops.Count : 0;
        if (loopsCt == 0)
        {
            if (logBeatSeqGates) Debug.Log($"[DRUM][BeatSeq] exit: intensityLoops=0 motif={_motif.motifId}");
            return;
        }

        // 3) Compute burn-tier intensity from aggregate energy spent
        float totalSpent = _gfm.GetTotalSpentEnergyTanks();
        if (!ComputeBurnIntensity(totalSpent, loopsCt, out float intensity01))
        {
            if (logBeatSeqGates) Debug.Log($"[DRUM][BeatSeq] baseline acquired motif={_motif.motifId}");
            return;
        }

        if (logBeatSeqGates) Debug.Log(
            $"[DRUM][BeatSeq] motif={_motif.motifId} tier={_burnTier:F0} intensity={intensity01:F3} loops={loopsCt}"
        );

        ApplyPerBinIntensityCeiling(ref intensity01);

        // 4) Select target clip
        var targetClip = ResolveIntensityClip(intensity01);
        if (targetClip == null)
        {
            Debug.LogWarning($"[DRUM][BeatSeq] exit: ResolveIntensityClip returned null intensity={intensity01:F3} motif={_motif.motifId}");
            return;
        }

        if (logBeatSeqGates) Debug.Log($"[DRUM][BeatSeq] chose clip={targetClip.name} intensity={intensity01:F3}");

        // 5) Avoid redundant scheduling
        if (_pendingDrumLoopArmed && _pendingDrumLoop == targetClip)
        {
            if (logBeatSeqGates) Debug.Log($"[DRUM][BeatSeq] exit: already armed clip={targetClip.name}");
            return;
        }
        if (!_pendingDrumLoopArmed && drumAudioSource != null && drumAudioSource.clip == targetClip)
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
        // 6) Schedule at boundary
        ScheduleDrumLoopChange(targetClip);
        if (logBeatSeqGates) Debug.Log($"[DRUM][BeatSeq] scheduled clip={targetClip.name}");
    }

    // Returns false if the baseline sample hasn't been acquired yet (caller should return early).
    // Updates _lastTotalSpentSample, _burnTier, _lastIntensity01; outputs smoothed intensity01.
    private bool ComputeBurnIntensity(float totalSpent, int clipCount, out float intensity01)
    {
        intensity01 = _lastIntensity01;

        if (_lastTotalSpentSample < 0f)
        {
            _lastTotalSpentSample = totalSpent;
            return false;
        }

        float delta = Mathf.Max(0f, totalSpent - _lastTotalSpentSample);
        _lastTotalSpentSample = totalSpent;

        float maxTier = clipCount - 1;
        if (delta > 0f)
            _burnTier = Mathf.Min(_burnTier + 1f, maxTier);
        else
            _burnTier = Mathf.Max(_burnTier - 1f, 0f);

        float rawIntensity = (clipCount > 1) ? _burnTier / maxTier : 0f;

        const float kIntensitySmooth = 0.35f;
        intensity01 = Mathf.Lerp(_lastIntensity01, rawIntensity, kIntensitySmooth);
        if (Mathf.Abs(intensity01 - _lastIntensity01) < config.intensityHysteresis)
            intensity01 = _lastIntensity01;
        _lastIntensity01 = intensity01;

        return true;
    }

    // Per-bin ceiling: clamps intensity based on how many tracks have filled bins.
    // Does NOT update _lastIntensity01 so energy-burn history is preserved across empty bins.
    // 0 filled tracks → ceiling 0, 1 → config.singleTrackIntensityCeiling, 2+ → uncapped.
    private void ApplyPerBinIntensityCeiling(ref float intensity01)
    {
        var ctrl = _gfm?.controller;
        if (ctrl?.tracks == null) return;

        int filledCount = 0;
        foreach (var track in ctrl.tracks)
        {
            if (track == null) continue;
            int upcomingBin = completedLoops % Mathf.Max(1, track.loopMultiplier);
            if (track.HasAnyNoteInBin(upcomingBin)) filledCount++;
        }
        float ceiling = filledCount == 0 ? 0f
                      : filledCount == 1 ? config.singleTrackIntensityCeiling
                      : 1f;
        intensity01 = Mathf.Min(intensity01, ceiling);
    }

    public void ResetBeatSequencingState(string who)
    {
        // Do NOT clear _motif or loop lists. This is a state reset, not a motif reset.
        _lastTotalSpentSample = -1f;
        _burnTier = 0f;
        _lastIntensity01 = 0f;

        if (GameFlowManager.VerboseLogging) Debug.Log($"[DRUM][BeatSeq] Soft reset by {who} motif={(_motif ? _motif.motifId : "null")}");
    }
}
