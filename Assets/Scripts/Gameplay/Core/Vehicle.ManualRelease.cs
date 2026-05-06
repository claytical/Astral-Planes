using System;
using System.Collections.Generic;
using UnityEngine;

public partial class Vehicle
{
    private HashSet<int> BuildManualReleaseSpokenFor(PendingCollectedNote pending = default)
    {
        var spokenFor = new HashSet<int>();
        foreach (var ar in _armedReleases)
            spokenFor.Add(ar.targetAbsStep);
        return spokenFor;
    }

    private bool TryResolveManualReleaseTargetStep(
        PendingCollectedNote pending,
        double rawAbs,
        int totalAbsSteps,
        out int targetAbsStep,
        out HashSet<int> spokenFor)
    {
        targetAbsStep = -1;
        spokenFor = BuildManualReleaseSpokenFor(pending);

        if (pending.track == null || totalAbsSteps <= 0)
            return false;

        if (gfm == null) gfm = GameFlowManager.Instance;
        var viz = gfm?.noteViz;
        if (viz == null)
            return false;

        return viz.TryGetNextUnlitStepExcluding(pending.track, rawAbs, totalAbsSteps, spokenFor, out targetAbsStep);
    }

    private void TickManualReleaseCue()
    {
        if (gfm == null) gfm = GameFlowManager.Instance;
        var viz = (gfm != null) ? gfm.noteViz : null;
        if (viz == null) return;

        var spokenFor = BuildManualReleaseSpokenFor();

        if (_armedReleases.Count > 0)
        {
            var a = _armedReleases.Peek();
            var armed = a.note;

            if (armed.track == null || armed.track.controller == null)
            {
                _armedReleases.Dequeue();
                return;
            }

            if (!armed.track.controller.TryGetRawPlayheadAbsStep(
                    out double rawAbs, out int floorAbs, out int totalStepsLive))
                return;

            int totalSteps = Mathf.Max(1, totalStepsLive);
            double fwd = (a.targetAbsStep - rawAbs) % totalSteps;
            if (fwd < 0) fwd += totalSteps;

            bool crossed = false;
            if (_hasLastRawAbsStep)
            {
                double last = (_lastRawAbsStep % totalSteps + totalSteps) % totalSteps;
                double now = (rawAbs % totalSteps + totalSteps) % totalSteps;
                if (last <= now)
                    crossed = (last <= a.targetAbsStep && a.targetAbsStep <= now);
                else
                    crossed = (a.targetAbsStep >= last) || (a.targetAbsStep <= now);
            }

            if (crossed || fwd <= vehicleConfig.manualReleaseAutoCommitEpsSteps)
            {
                CommitManualReleaseAtStep(armed, a.targetAbsStep, a.releaseVelocity);
                if (_armedReleases.Count == 0) return;
                _armedReleases.Dequeue();
                spokenFor.Remove(a.targetAbsStep);
            }

            _lastRawAbsStep = rawAbs;
            _hasLastRawAbsStep = true;

            if (_pendingNotes.Count > 0)
                viz.UpdateManualReleaseCueExcluding(transform, armed.track, rawAbs, floorAbs, totalStepsLive, spokenFor);
            else
                viz.ClearManualReleaseCue(transform);

            return;
        }

        if (_pendingNotes.Count <= 0)
        {
            viz.ClearManualReleaseCue(transform);
            return;
        }

        var queued = _pendingNotes.Peek();
        if (queued.track == null || queued.track.controller == null)
        {
            viz.ClearManualReleaseCue(transform);
            return;
        }

        if (!queued.track.controller.TryGetRawPlayheadAbsStep(
                out double rawAbsQ, out int floorAbsQ, out int totalStepsQ))
        {
            viz.ClearManualReleaseCue(transform);
            return;
        }

        var pendingSpokenFor = BuildManualReleaseSpokenFor(queued);
        viz.UpdateManualReleaseCueExcluding(transform, queued.track, rawAbsQ, floorAbsQ, totalStepsQ, pendingSpokenFor);

        _lastRawAbsStep = rawAbsQ;
        _hasLastRawAbsStep = true;
    }

    private void CommitManualReleaseAtStep(PendingCollectedNote p, int targetAbsStep, float releaseVelocity = -1f)
    {
        if (gfm == null) gfm = GameFlowManager.Instance;
        var viz = (gfm != null) ? gfm.noteViz : null;

        if (p.track == null || p.track.controller == null || p.track.drumTrack == null)
        {
            if (p.collectable != null) p.collectable.OnManualReleaseConsumed();
            return;
        }

        bool compositionMode = p.track.controller != null &&
                               p.track.controller.noteCommitMode == NoteCommitMode.Composition;
        int chosenMidi = compositionMode
            ? p.collectedMidi
            : p.track.GetAuthoredNoteAtAbsStep(targetAbsStep);

        int resolvedDurationTicks = p.durationTicks;
        int binSize = Mathf.Max(1, p.track.BinSize());
        int targetBin = p.track.BinIndexForStep(targetAbsStep);
        int localStep = ((targetAbsStep % binSize) + binSize) % binSize;
        var noteSetForBin = p.track.GetNoteSetForBin(targetBin);
        if (noteSetForBin != null && noteSetForBin.TryGetTemplateTimingAtStep(localStep, out int authoredDurationTicks, out _))
            resolvedDurationTicks = authoredDurationTicks;

        bool occupied = p.track.IsPersistentStepOccupied(targetAbsStep);
        float commitVel = releaseVelocity >= 0f ? releaseVelocity : p.velocity127;
        if (occupied)
            commitVel = Mathf.Clamp(commitVel * vehicleConfig.occupiedStepVelocityMultiplier, 1f, 127f);

        if (p.collectable != null) p.collectable.MarkAsReportedCollected();
        if (p.collectable != null) p.collectable.OnManualReleaseConsumed();

        p.track.CommitManualReleasedNote(
            stepAbs: targetAbsStep,
            midiNote: chosenMidi,
            durationTicks: resolvedDurationTicks,
            velocity127: commitVel,
            authoredRootMidi: p.authoredRootMidi,
            burstId: p.burstId,
            lightMarkerNow: true,
            skipChordQuantize: compositionMode
        );

        p.track.PlayOneShotMidi(chosenMidi, commitVel, resolvedDurationTicks);

        if (viz != null)
        {
            viz.PulseMarkerSpecial(p.track, targetAbsStep);
            viz.TriggerPlayheadReleasePulse(p.track.assignedRole);
        }

        if (occupied && vehicleConfig.occupiedStepOctaveAccent)
            p.track.PlayOneShotMidi(chosenMidi + 12, commitVel, resolvedDurationTicks);
    }

    private void OnStepTickForReleaseCue(int stepIndex, int leaderSteps)
    {
        if (releaseCue == null) return;

        int targetStep = -1;
        double gapDsp = 0;
        DrumTrack drum = null;

        if (_armedReleases.Count > 0)
        {
            var a = _armedReleases.Peek();
            targetStep = a.targetAbsStep;
            gapDsp = a.gapDurationDsp;
            drum = a.note.track?.drumTrack;
        }
        else if (_pendingNotes.Count > 0)
        {
            var p = _pendingNotes.Peek();
            drum = p.track?.drumTrack;
            if (p.track?.controller != null &&
                p.track.controller.TryGetRawPlayheadAbsStep(out double rawAbsP, out _, out int totalP) &&
                TryResolveManualReleaseTargetStep(p, rawAbsP, totalP, out int resolvedTargetStep, out _))
            {
                targetStep = resolvedTargetStep;
                int pBinSize = Mathf.Max(1, drum != null ? drum.totalSteps : leaderSteps);
                int pLeaderBins = Mathf.Max(1, Mathf.CeilToInt(leaderSteps / (float)pBinSize));
                double pLoopLen = (drum != null ? drum.GetLoopLengthInSeconds() : 1f) * pLeaderBins;
                double pStepDur = pLoopLen / Mathf.Max(1, leaderSteps);
                double fwdSteps = (targetStep - rawAbsP + totalP) % totalP;
                gapDsp = Math.Max(0.001, fwdSteps * pStepDur);
            }
        }

        if (targetStep < 0 || drum == null) return;

        int binSize = Mathf.Max(1, drum.totalSteps);
        int leaderBins = Mathf.Max(1, Mathf.CeilToInt(leaderSteps / (float)binSize));
        double loopLen = drum.GetLoopLengthInSeconds() * leaderBins;
        double stepDur = loopLen / Mathf.Max(1, leaderSteps);

        int gapStepsNow = Mathf.Max(1, Mathf.RoundToInt((float)(gapDsp / stepDur)));

        double fwd = (targetStep - stepIndex + (double)leaderSteps) % leaderSteps;
        int stepsLeft = Mathf.Max(0, Mathf.RoundToInt((float)fwd));

        releaseCue.SetBeatsRemaining(stepsLeft, gapStepsNow);
    }
}
