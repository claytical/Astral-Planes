using UnityEngine;

public partial class InstrumentTrack
{
    // Shared terminal step for all three note-resolution paths:
    // OnCollectableCollected, CommitManualReleasedNote, NotifyNoteDiscarded.
    //
    // Handles: bin fill, visualizer grid snap, cursor unlock, ascend fuse,
    // dict cleanup, and the burst-cleared event.
    //
    // Caller responsibilities that stay outside:
    //   OnCollectableCollected  — TriggerPlayheadReleasePulse, Harmony_OnBinFilled,
    //                             _burstTotalSpawned/Collected cleanup, AdvanceBinCursor,
    //                             AdvanceOtherTrackCursors
    //   CommitManualReleasedNote — nothing extra
    //   NotifyNoteDiscarded     — bin rollback on 0-note discard, _gateReleasedBurstIds cleanup
    private void CompleteBurst(int burstId, int filledBin, bool hadNotes, int ascendBinCount, bool removePlaceholders = false)
    {
        if (hadNotes)
        {
            SetBinFilled(filledBin, true);
            controller?.NotifyBinFilled(this, filledBin);
            controller?.ResyncLeaderBinsNow();

            if (controller != null && controller.noteVisualizer != null && drumTrack != null)
            {
                int bSize = Mathf.Max(1, drumTrack.totalSteps);
                int needBinsFromThisTrack = Mathf.Max(1, filledBin + 1);
                int needLeaderBins = Mathf.Max(needBinsFromThisTrack, controller.GetMaxLoopMultiplier());
                controller.noteVisualizer.RequestLeaderGridChange(needLeaderBins * bSize);
            }

            controller?.AllowAdvanceNextBurst(this);
        }

        float ascendSeconds = drumTrack != null
            ? drumTrack.GetLoopLengthInSeconds() * ComputeEffectiveAscendLoops(ascendBinCount)
            : 0f;
        int capturedBurstId = burstId;
        bool capturedRemove = removePlaceholders;
        float capturedSec = ascendSeconds;
        EnqueueNextFrame(() =>
        {
            if (controller?.noteVisualizer != null)
            {
                if (capturedRemove)
                    controller.noteVisualizer.RemoveAllPlaceholdersForBurst(this, capturedBurstId);
                controller.noteVisualizer.TriggerBurstAscend(this, capturedBurstId, capturedSec);
            }
        });

        _burstRemaining.Remove(burstId);
        _burstLeaderBinsBeforeWrite.Remove(burstId);
        _burstWroteBin.Remove(burstId);
        _burstTargetBin.Remove(burstId);

        if (GameFlowManager.VerboseLogging) Debug.Log(
            $"[TRKDBG] {name} CompleteBurst: burstId={burstId} hadNotes={hadNotes} " +
            $"(_burstRemaining={_burstRemaining?.Count ?? -1})");

        OnCollectableBurstCleared?.Invoke(this, burstId, hadNotes);
    }

    // Snapshots leader bin count before the first note of a burst writes to the loop.
    // Used later to determine whether this burst extended the global leader (cross-track nudge).
    private void SnapshotBurstLeaderBins(int burstId, int targetStep)
    {
        if (burstId == 0) return;
        if (_burstLeaderBinsBeforeWrite.ContainsKey(burstId)) return;
        int leaderBins = controller != null ? Mathf.Max(1, controller.GetMaxLoopMultiplier()) : loopMultiplier;
        _burstLeaderBinsBeforeWrite[burstId] = leaderBins;
        _burstWroteBin[burstId] = BinIndexForStep(targetStep);
    }

    // Looks up the authored MIDI root stored in the per-bin NoteSet for a given absolute step.
    // Returns int.MinValue when no bin-specific NoteSet exists (caller uses default quantization).
    private int LookUpAuthoredRootMidi(int finalTargetStep)
    {
        int binIdx = BinIndexForStep(finalTargetStep);
        int localStep = finalTargetStep % Mathf.Max(1, BinSize());
        if (_binNoteSets != null && binIdx >= 0 && binIdx < _binNoteSets.Length && _binNoteSets[binIdx] != null)
            return _binNoteSets[binIdx].GetAuthoredRootMidi(localStep);
        return int.MinValue;
    }

    // Increments the per-burst collection counter and pushes the playhead energy UI.
    private void IncrementBurstCollectedMeter(int burstId)
    {
        if (burstId == 0) return;
        if (_burstCollected.TryGetValue(burstId, out var c))
            _burstCollected[burstId] = c + 1;
        else
            _burstCollected[burstId] = 1;

        if (controller != null && controller.noteVisualizer != null &&
            _burstTotalSpawned.TryGetValue(burstId, out var total) && total > 0)
        {
            float frac = Mathf.Clamp01(_burstCollected[burstId] / (float)total);
            controller.noteVisualizer.SetPlayheadEnergy01(frac);
        }
    }

    // Computes the DSP time at which a collectable should deposit its note.
    // Anchors to the next occurrence of finalTargetStep in leader-step space so that
    // bin expansion does not shift the confirm timing relative to persistentLoopNotes.
    private double ComputeDepositDsp(int finalTargetStep)
    {
        if (drumTrack == null)
            return AudioSettings.dspTime + 0.05;

        int leaderSteps = Mathf.Max(1, drumTrack.GetLeaderSteps());
        int targetLeaderStep = ((finalTargetStep % leaderSteps) + leaderSteps) % leaderSteps;

        double effLen = System.Math.Max(0.0001, drumTrack.GetLoopLengthInSeconds());
        double stepDur = effLen / leaderSteps;
        double dspNow = AudioSettings.dspTime;
        double elapsed = dspNow - drumTrack.leaderStartDspTime;
        if (elapsed < 0) elapsed = 0;

        double tInLoop = elapsed % effLen;
        int curLeaderStep = Mathf.FloorToInt((float)(tInLoop / stepDur));
        int deltaSteps = targetLeaderStep - curLeaderStep;

        // IMPORTANT: if already at/past the step this frame, push to NEXT occurrence.
        if (deltaSteps <= 0) deltaSteps += leaderSteps;

        double depositDsp = drumTrack.leaderStartDspTime + (curLeaderStep + deltaSteps) * stepDur;

        const double kMinLead = 0.005;
        if (depositDsp <= dspNow + kMinLead)
            depositDsp = dspNow + 0.010;

        return depositDsp;
    }

    // Resolves the authoritative absolute step from a collected collectable.
    // Returns -1 if no valid step can be determined (caller should abort).
    private int ResolveTargetStep(Collectable collectable, int reportedStep)
    {
        if (collectable.intendedStep >= 0)
            return collectable.intendedStep;

        if (reportedStep >= 0)
        {
            Debug.LogWarning($"[COLLECT:FALLBACK] {name} using reportedStep={reportedStep} because intendedStep was missing. burstId={collectable.burstId}");
            int binSize = Mathf.Max(1, BinSize());
            int local = ((reportedStep % binSize) + binSize) % binSize;
            int bin = collectable.intendedBin;
            if (bin < 0)
            {
                var tf = controller != null ? controller.GetTransportFrame() : default;
                bin = tf.playheadBin;
                Debug.LogWarning($"[COLLECT:BASE->ABS:FALLBACK_BIN] {name} missing intendedBin; using playheadBin={bin} (nondeterministic) burstId={collectable.burstId}");
            }
            int finalStep = bin * binSize + local;
            Debug.LogWarning($"[COLLECT:BASE->ABS] {name} mapped baseStep={reportedStep} into absStep={finalStep} using intendedBin={collectable.intendedBin}");
            return finalStep;
        }

        Debug.LogWarning($"[COLLECT:ABORT] {name} no valid step (intendedStep={collectable.intendedStep}, reportedStep={reportedStep}) burstId={collectable.burstId}");
        return -1;
    }
}
