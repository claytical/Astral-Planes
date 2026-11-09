using System;
using System.Collections.Generic;
using UnityEngine;

public partial class InstrumentTrack
{
    // Steps claimed by auto-deposit notes that are still in flight (caught, not yet committed).
    // Equivalent of the manual path's "spokenFor" set so two catches never target the same slot.
    private readonly Dictionary<Collectable, int> _autoClaims = new();

    /// <summary>
    /// Auto-deposit path. The note is caught now, but is committed to the loop (lit, registered,
    /// burst accounting, ring/bin completion) only when it ARRIVES at its target step, using the
    /// same commit pipeline as manual release. Target = next unfilled placeholder ahead of the
    /// playhead at catch time (falls back to the authored step). Returns false if no carry was scheduled.
    /// </summary>
    public bool OnCollectableCollected(Vehicle vehicle, Collectable collectable, int reportedStep, int durationTicks, float force)
    {
        if (collectable == null || collectable.assignedInstrumentTrack != this) return false;
        controller.NotifyCollected(this);
        TutorialEvents.RaiseNoteCaught(collectable);
        NoteCollectedForRound(vehicle, collectable.burstId);

        if (drumTrack != null)
        {
            Vector2Int gridPos = drumTrack.WorldToGridPosition(collectable.transform.position);
            drumTrack.FreeSpawnCell(gridPos.x, gridPos.y);
            drumTrack.ResetSpawnCellBehavior(gridPos.x, gridPos.y);
        }

        int authoredStep = ResolveTargetStep(collectable, reportedStep);
        if (authoredStep < 0) return false;

        int finalTargetStep = ResolveAutoTargetStep(authoredStep);
        _autoClaims[collectable] = finalTargetStep;

        var pending = BuildPendingNote(collectable, reportedStep, durationTicks, force);

        // Keep in spawnedCollectables until it lands so in-flight gates (bin expansion, bridge) stay accurate.
        GameObject markerGo = null;
        if (controller?.noteVisualizer != null)
            markerGo = controller.noteVisualizer.PlacePersistentNoteMarker(this, finalTargetStep, lit: false, burstId: collectable.burstId);
        if (markerGo != null)
            collectable.ribbonMarker = markerGo.transform;

        double depositDsp = ComputeDepositDsp(finalTargetStep);
        if (GameFlowManager.VerboseLogging) Debug.Log($"[DEPOSIT] track={name} authored={authoredStep} target={finalTargetStep} dt={(depositDsp - AudioSettings.dspTime):F4}");

        collectable.BeginCarryThenDepositAtDsp(depositDsp, durationTicks, force, onArrived: () =>
        {
            _autoClaims.Remove(collectable);
            spawnedCollectables?.Remove(collectable.gameObject);
            collectable.MarkAsReportedCollected();

            if (controller?.noteVisualizer != null)
            {
                controller.noteVisualizer.ScheduleFirstPlayConfirm(
                    source: collectable.transform, track: this, step: finalTargetStep,
                    dspTime: depositDsp, noteDuration: durationTicks, color: DisplayColor);
                controller.noteVisualizer.RegisterNotePlayTint(this, DisplayColor, TicksToSeconds(durationTicks));
            }

            float occMult = vehicle != null ? vehicle.OccupiedStepVelocityMultiplier : 1f;
            bool occAccent = vehicle != null && vehicle.OccupiedStepOctaveAccent;
            CommitPendingNoteAtStep(pending, finalTargetStep, pending.velocity127, occMult, occAccent, vehicle);
            if (vehicle != null) vehicle.NotifyAutoNoteCommitted();
        });

        if (_currentBurstArmed && collectable.burstId == currentBurstId)
        {
            _currentBurstRemaining = Mathf.Max(0, _currentBurstRemaining - 1);
            if (_currentBurstRemaining == 0)
                _currentBurstArmed = false;
        }
        return true;
    }

    // Next unfilled placeholder at/after the playhead, skipping slots claimed by in-flight auto notes.
    // Falls back to the authored step when no placeholder is available.
    private int ResolveAutoTargetStep(int authoredStep)
    {
        var viz = controller != null ? controller.noteVisualizer : null;
        if (viz == null || !controller.TryGetRawPlayheadAbsStep(out double rawAbs, out _, out _))
            return authoredStep;

        var claimed = new HashSet<int>(_autoClaims.Values);
        int effectiveTotal = Mathf.Max(GetTotalSteps(), loopMultiplier * Mathf.Max(1, drumTrack != null ? drumTrack.totalSteps : BinSize()));
        if (viz.TryGetNextUnlitStepExcluding(this, rawAbs, effectiveTotal, claimed, out int step))
            return step;
        return authoredStep;
    }

    // ---------------------------------------------------------------------
    // Manual Note Release integration
    // ---------------------------------------------------------------------

    /// <summary>Snapshot a caught collectable as a PendingCollectedNote (authored step identity, pitch, root).</summary>
    private Vehicle.PendingCollectedNote BuildPendingNote(Collectable collectable, int reportedBaseStep, int durationTicks, float velocity127)
    {
        int binSize = Mathf.Max(1, drumTrack != null ? drumTrack.totalSteps : BinSize());

        // Determine authored abs/local step identity.
        int authoredAbs = -1;
        if (collectable.intendedStep >= 0)
            authoredAbs = collectable.intendedStep;
        else if (collectable.intendedBin >= 0 && reportedBaseStep >= 0)
            authoredAbs = collectable.intendedBin * binSize + (((reportedBaseStep % binSize) + binSize) % binSize);

        int authoredLocal = (authoredAbs >= 0) ? (((authoredAbs % binSize) + binSize) % binSize) : (((reportedBaseStep % binSize) + binSize) % binSize);

        int collectedMidi = collectable.GetNote();

        // Anchor authoredRootMidi to the chord that was active at the authored bin.
        // Prefer the bin-specific NoteSet's stored authoredRootMidi (set by NoteSetFactory to
        // the exact bin chord root), so QuantizeNoteToBinChord sees rootDelta = 0.
        // Fall back to the HarmonyDirector chord root only when no bin-specific NoteSet is available.
        int rootInRegister = int.MinValue;
        if (authoredAbs >= 0)
        {
            int authoredBin = BinIndexForStep(authoredAbs);
            if (_binNoteSets != null && authoredBin >= 0 && authoredBin < _binNoteSets.Length && _binNoteSets[authoredBin] != null)
                rootInRegister = _binNoteSets[authoredBin].GetAuthoredRootMidi(authoredLocal);
        }
        if (rootInRegister == int.MinValue && rootShiftNotesByChord && authoredAbs >= 0)
        {
            if (_gfm == null) _gfm = GameFlowManager.Instance;
            var hd = _gfm?.harmony;
            if (hd != null)
            {
                int authoredBin = BinIndexForStep(authoredAbs);
                int authoredChordIdx = Harmony_GetChordIndexForBin(authoredBin);
                if (authoredChordIdx >= 0 && hd.TryGetChordAt(authoredChordIdx, out var authoredChord))
                    rootInRegister = authoredChord.rootNote; // exact register; no nudge, no clamp
            }
        }
        if (rootInRegister == int.MinValue)
            rootInRegister = GetAuthoredRootMidiExact();

        var pending = new Vehicle.PendingCollectedNote
        {
            track = this,
            collectable = collectable,
            authoredAbsStep = authoredAbs,
            authoredLocalStep = authoredLocal,
            collectedMidi = collectedMidi,
            durationTicks = durationTicks,
            velocity127 = Mathf.Clamp(velocity127, 1f, 127f),
            authoredRootMidi = rootInRegister,
            burstId = collectable.burstId
        };

        return pending;
    }

    /// <summary>Authored duration at the given step's template, else the fallback.</summary>
    public int ResolveTemplateDuration(int atStep, int fallbackTicks)
    {
        int binSize   = Mathf.Max(1, BinSize());
        int localStep = ((atStep % binSize) + binSize) % binSize;
        var noteSet   = GetNoteSetForBin(BinIndexForStep(atStep));
        if (noteSet != null && noteSet.TryGetTemplateTimingAtStep(localStep, out int authoredDur, out _))
            return authoredDur;
        return fallbackTicks;
    }

    /// <summary>
    /// Shared commit for a carried note (manual release or auto-deposit arrival): resolves duration,
    /// cross-bin pitch swap, clash avoidance and occupied-step handling, commits to the loop with a
    /// lit marker, plays the confirm audio and pulses the marker.
    /// </summary>
    internal void CommitPendingNoteAtStep(Vehicle.PendingCollectedNote p, int targetAbsStep, float commitVel,
        float occupiedVelocityMultiplier, bool occupiedOctaveAccent, Vehicle placer)
    {
        int chosenMidi = p.collectedMidi;
        int resolvedDurationTicks = ResolveTemplateDuration(targetAbsStep, p.durationTicks);

        int spawnBin  = BinIndexForStep(p.authoredAbsStep);
        int targetBin = BinIndexForStep(targetAbsStep);
        bool crossBinComposition = spawnBin != targetBin;

        // Cross-bin: the collected note was authored for another bin's chord; use the target
        // step's authored note. Same-bin: keep the raw collected note.
        if (crossBinComposition)
            chosenMidi = GetAuthoredNoteAtAbsStep(targetAbsStep);

        chosenMidi = AvoidClashAtStep(targetAbsStep, chosenMidi);

        bool occupied = IsPersistentStepOccupied(targetAbsStep);
        if (occupied)
            commitVel = Mathf.Clamp(commitVel * occupiedVelocityMultiplier, 1f, 127f);

        // Cross-bin: store the target bin's chord root so later retune computes rootDelta=0.
        int commitAuthoredRoot = p.authoredRootMidi;
        if (crossBinComposition)
        {
            var targetNs = GetNoteSetForBin(targetBin);
            if (targetNs?.chordRegion != null && targetNs.chordRegion.Count > 0)
                commitAuthoredRoot = targetNs.chordRegion[ResolveChordRegionIndex(targetBin, targetNs.chordRegion.Count)].rootNote;
        }

        CommitManualReleasedNote(
            stepAbs: targetAbsStep,
            midiNote: chosenMidi,
            durationTicks: resolvedDurationTicks,
            velocity127: commitVel,
            authoredRootMidi: commitAuthoredRoot,
            burstId: p.burstId,
            lightMarkerNow: true,
            skipChordQuantize: true,
            placer: placer);

        PlayOneShotMidi(chosenMidi, commitVel, resolvedDurationTicks);

        var viz = controller != null ? controller.noteVisualizer : null;
        if (viz != null)
        {
            viz.PulseMarkerSpecial(this, targetAbsStep);
            viz.TriggerPlayheadReleasePulse(assignedRole);
        }

        if (occupied && occupiedOctaveAccent)
            PlayOneShotMidi(chosenMidi + 12, commitVel, resolvedDurationTicks);
    }

    /// <summary>
    /// Manual-release pickup path: frees the spawn cell and enqueues a note onto the vehicle,
    /// but does NOT commit anything to the loop yet.
    /// </summary>
    public void OnCollectablePickedUpForManualRelease(Vehicle vehicle, Collectable collectable, int reportedBaseStep, int durationTicks, float velocity127)
    {
        if (vehicle == null || collectable == null || collectable.assignedInstrumentTrack != this) return;

        TutorialEvents.RaiseNoteCaught(collectable);
        NoteCollectedForRound(vehicle, collectable.burstId);

        // Free the vacated grid cell (same as normal pickup path).
        if (drumTrack != null)
        {
            Vector2Int gridPos = drumTrack.WorldToGridPosition(collectable.transform.position);
            drumTrack.FreeSpawnCell(gridPos.x, gridPos.y);
            drumTrack.ResetSpawnCellBehavior(gridPos.x, gridPos.y);
        }

        // Keep the collectable in spawnedCollectables until it is released from the Vehicle.
        // Removal is deferred to OnManualReleaseConsumed / OnManualReleaseDiscarded so that
        // AnyCollectablesInFlightGlobal() stays true while the note is being carried.

        var pending = BuildPendingNote(collectable, reportedBaseStep, durationTicks, velocity127);

        vehicle.EnqueuePendingNote(pending);
    }

    /// <summary>
    /// Commit a released note into the persistent loop at an absolute step. This is where burst
    /// completion and bin fill logic occurs for manual release.
    /// </summary>
    public void CommitManualReleasedNote(int stepAbs, int midiNote, int durationTicks, float velocity127, int authoredRootMidi, int burstId, bool lightMarkerNow, bool skipChordQuantize = false, Vehicle placer = null)
    {
        if (drumTrack == null) return;

        controller?.NotifyCollected(this);

        // Burst energy meter: count at COMMIT time (release), not pickup time.
        IncrementBurstCollectedMeter(burstId);

        // Snapshot leader bins before first write (for cross-track nudge)
        SnapshotBurstLeaderBins(burstId, stepAbs);

        // Replace-or-add: enforce one persistent note per (track, stepAbs) for stability.
        persistentLoopNotes.RemoveAll(t => t.stepIndex == stepAbs);
        AddNoteToLoop(stepAbs, midiNote, durationTicks, velocity127, lightMarkerNow, authoredRootMidi, skipChordQuantize);
        NotifyNoteCommittedForDust();
        TutorialEvents.RaiseNotePlaced();
        AwardCompetitiveNote(placer, burstId);

        int targetBin = BinIndexForStep(stepAbs);
        RegisterBurstStep(burstId, stepAbs);

        if (burstId != 0 && _bursts.TryGetValue(burstId, out var state))
        {
            state.remaining--;
            if (state.remaining <= 0)
            {
                int filledBin = state.wroteBin >= 0 ? state.wroteBin : targetBin;
                CompleteBurst(burstId, filledBin, hadNotes: true, ascendBinCount: BinSize(), removePlaceholders: true);
            }
        }
    }

    /// <summary>
    /// Call when a manually-queued note was discarded (outside window, queue overflow, etc.).
    /// Decrements the burst counter. Placeholder removal is deferred: when all collectables
    /// for the burst have left the vehicle, any remaining placeholders are cleared together.
    /// </summary>
    public void NotifyNoteDiscarded(int burstId, int authoredAbsStep)
    {
        TutorialEvents.RaiseNoteDiscarded();
        if (burstId == 0) return;
        if (!_bursts.TryGetValue(burstId, out var state)) return;

        state.remaining--;
        if (state.remaining > 0) return;

        int capturedDiscardedStep = authoredAbsStep;
        int capturedBurstId = burstId;
        EnqueueNextFrame(() =>
        {
            if (controller?.noteVisualizer != null && capturedDiscardedStep >= 0)
                controller.noteVisualizer.RemoveOrphanMarkerAtStep(this, capturedDiscardedStep, capturedBurstId);
        });

        bool hadNotes = state.wroteBin >= 0;

        if (hadNotes)
        {
            CompleteBurst(burstId, state.wroteBin, hadNotes: true, ascendBinCount: BinSize(), removePlaceholders: true);
        }
        else
        {
            if (state.targetBin >= 0)
            {
                SetBinAllocated(state.targetBin, false);
                if (GetBinCursor() > state.targetBin)
                    SetBinCursor(state.targetBin);
            }
            CompleteBurst(burstId, filledBin: 0, hadNotes: false, ascendBinCount: BinSize(), removePlaceholders: true);
        }
    }

    /// <summary>
    /// Called at each loop boundary: if a bin has notes in the persistent loop but is still
    /// marked unfilled AND no live collectables for that bin remain in the world, the burst
    /// tracking counter drifted — resolve it now so audio unblocks within 1 boundary.
    /// </summary>
    public void ResolveStrandedBursts()
    {
        for (int b = 0; b < maxLoopMultiplier; b++)
        {
            if (IsBinFilled(b)) continue;
            if (!HasAnyNoteInBin(b)) continue;
            bool hasLiveCollectable = false;
            for (int i = 0; i < spawnedCollectables.Count; i++)
            {
                var go = spawnedCollectables[i];
                if (go == null) continue;
                if (go.TryGetComponent<Collectable>(out var c) && c.intendedBin == b)
                {
                    hasLiveCollectable = true;
                    break;
                }
            }
            if (!hasLiveCollectable)
            {
                if (GameFlowManager.VerboseLogging) Debug.Log($"[SYNC:RESOLVE] {name} bin={b} has notes but unfilled with no live collectables — resolving.");
                SetBinFilled(b, true);
                controller?.ResyncLeaderBinsNow();
            }
        }
    }

    // -----------------------------------------------------------------------
    // Burst lifecycle helpers (only used by the collection/pickup pipeline above)
    // -----------------------------------------------------------------------

    // Shared terminal step for all three note-resolution paths:
    // OnCollectableCollected, CommitManualReleasedNote, NotifyNoteDiscarded.
    //
    // Handles: bin fill, harmony advancement, visualizer grid snap, cursor unlock,
    // ascend fuse, dict cleanup, and the burst-cleared event.
    //
    // Caller responsibilities that stay outside:
    //   OnCollectableCollected  — TriggerPlayheadReleasePulse,
    //                             burst collected/total accounting, AdvanceBinCursor,
    //                             AdvanceOtherTrackCursors
    //   CommitManualReleasedNote — nothing extra
    //   NotifyNoteDiscarded     — bin rollback on 0-note discard
    private void CompleteBurst(int burstId, int filledBin, bool hadNotes, int ascendBinCount, bool removePlaceholders = false)
    {
        if (hadNotes)
        {
            SetBinFilled(filledBin, true);
            if (_gfm == null) _gfm = GameFlowManager.Instance;
            Harmony_OnBinFilled(filledBin, _gfm?.harmony?.ProgressionLength ?? 0);
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

        int ascendLoops = ComputeEffectiveAscendLoops(ascendBinCount);
        int capturedBurstId = burstId;
        bool capturedRemove = removePlaceholders;
        int capturedLoops = ascendLoops;
        EnqueueNextFrame(() =>
        {
            if (controller?.noteVisualizer != null)
            {
                if (capturedRemove)
                    controller.noteVisualizer.RemoveAllPlaceholdersForBurst(this, capturedBurstId);
                controller.noteVisualizer.TriggerBurstAscend(this, capturedBurstId, capturedLoops);
            }
        });

        _bursts.Remove(burstId);

        if (GameFlowManager.VerboseLogging) Debug.Log(
            $"[TRKDBG] {name} CompleteBurst: burstId={burstId} hadNotes={hadNotes} " +
            $"(_bursts={_bursts?.Count ?? -1})");

        RaiseBurstCleared(burstId, hadNotes);
    }

    // Snapshots leader bin count before the first note of a burst writes to the loop.
    // Used later to determine whether this burst extended the global leader (cross-track nudge).
    private void SnapshotBurstLeaderBins(int burstId, int targetStep)
    {
        if (burstId == 0) return;
        if (!_bursts.TryGetValue(burstId, out var state)) return;
        if (state.leaderBins != 0) return; // already snapshotted
        state.leaderBins = controller != null ? Mathf.Max(1, controller.GetMaxLoopMultiplier()) : loopMultiplier;
        state.wroteBin = BinIndexForStep(targetStep);
    }

    // Increments the per-burst collection counter and pushes the playhead energy UI.
    private void IncrementBurstCollectedMeter(int burstId)
    {
        if (burstId == 0) return;
        if (!_bursts.TryGetValue(burstId, out var state)) return;
        state.collected++;

        if (controller != null && controller.noteVisualizer != null && state.totalSpawned > 0)
        {
            float frac = Mathf.Clamp01(state.collected / (float)state.totalSpawned);
            controller.noteVisualizer.SetPlayheadEnergy01(frac);
        }
    }

    // Computes the DSP time at which a collectable should deposit its note.
    // Anchors to the next occurrence of finalTargetStep in leader-step space so that
    // bin expansion does not shift the confirm timing relative to persistentLoopNotes.
    // Internal: also used by Collectable.Bomb.cs to schedule a discarded note's detonation
    // at the next occurrence of its encoded step.
    internal double ComputeDepositDsp(int finalTargetStep)
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
