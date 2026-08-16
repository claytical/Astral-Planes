using UnityEngine;

public partial class DrumTrack
{
    private void HandleBeatSequencingAtLoopBoundary(float loopSeconds)
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

        // 2) Need an intensity ladder
        int loopsCt = (_intensityLoops != null) ? _intensityLoops.Count : 0;
        if (loopsCt == 0)
        {
            if (logBeatSeqGates) Debug.Log($"[DRUM][BeatSeq] exit: intensityLoops=0 motif={_motif.motifId}");
            return;
        }

        // 3) Compute intensity: track fullness picks the tier, live CosmicDust density nudges within it
        if (_dust == null)
        {
            if (logBeatSeqGates) Debug.Log($"[DRUM][BeatSeq] exit: _dust==null motif={_motif.motifId}");
            return;
        }
        float fullness01 = ComputeTrackFullness01();
        ComputeDensityIntensity(_dust.GetSolidFraction(), out float density01);
        float intensity01 = Mathf.Clamp01(fullness01 + (density01 - 0.5f) * config.densityNudgeRange);

        if (logBeatSeqGates) Debug.Log(
            $"[DRUM][BeatSeq] motif={_motif.motifId} fullness={fullness01:F3} density={density01:F3} intensity={intensity01:F3} loops={loopsCt}"
        );

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

    // Updates _lastDensity01; outputs smoothed density01 from live CosmicDust density.
    private void ComputeDensityIntensity(float solidFraction, out float density01)
    {
        float rawDensity = Mathf.InverseLerp(config.minDensityForIntensity, config.maxDensityForIntensity, solidFraction);

        const float kIntensitySmooth = 0.35f;
        density01 = Mathf.Lerp(_lastDensity01, rawDensity, kIntensitySmooth);
        if (Mathf.Abs(density01 - _lastDensity01) < config.intensityHysteresis)
            density01 = _lastDensity01;
        _lastDensity01 = density01;
    }

    // Primary intensity tier driver: average how built-out each instrument track's loop is
    // (loopMultiplier vs its own ceiling) across all currently-spawned tracks.
    private float ComputeTrackFullness01()
    {
        var ctrl = _gfm?.controller;
        if (ctrl?.tracks == null || ctrl.tracks.Length == 0) return 0f;

        float sum = 0f;
        int counted = 0;
        foreach (var track in ctrl.tracks)
        {
            if (track == null) continue;
            int maxMul = Mathf.Max(1, track.maxLoopMultiplier);
            sum += Mathf.Clamp01(track.loopMultiplier / (float)maxMul);
            counted++;
        }
        return counted > 0 ? sum / counted : 0f;
    }

    public void ResetBeatSequencingState(string who)
    {
        // Do NOT clear _motif or loop lists. This is a state reset, not a motif reset.
        _lastDensity01 = 0f;

        if (GameFlowManager.VerboseLogging) Debug.Log($"[DRUM][BeatSeq] Soft reset by {who} motif={(_motif ? _motif.motifId : "null")}");
    }
}
