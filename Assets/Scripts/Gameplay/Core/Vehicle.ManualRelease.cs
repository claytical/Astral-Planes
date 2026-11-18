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
                    out double rawAbs, out _, out int totalStepsLive))
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
                bool cancelHoldArm = _lastArmWasFromHold && !_releaseButtonHeld;
                _lastArmWasFromHold = false;

                if (cancelHoldArm)
                {
                    _armedReleases.Dequeue();
                    _pendingNotes.Enqueue(armed);
                    return;
                }

                CommitManualReleaseAtStep(armed, a.targetAbsStep, a.releaseVelocity);
                if (_armedReleases.Count == 0) return;
                _armedReleases.Dequeue();
                if (_releaseButtonHeld && _armedReleases.Count == 0 && _pendingNotes.Count > 0)
                {
                    // Don't sacrifice — if next note's step isn't in window yet, leave it
                    // in the queue and try again next tick when the step comes around.
                    TryReleaseQueuedNote(allowSacrifice: false);
                    _lastArmWasFromHold = _armedReleases.Count > 0;
                }
            }

            _lastRawAbsStep = rawAbs;
            _hasLastRawAbsStep = true;
            return;
        }

        if (_pendingNotes.Count <= 0)
            return;

        var queued = _pendingNotes.Peek();
        if (queued.track == null || queued.track.controller == null)
            return;

        if (!queued.track.controller.TryGetRawPlayheadAbsStep(
                out double rawAbsQ, out _, out _))
            return;

        _lastRawAbsStep = rawAbsQ;
        _hasLastRawAbsStep = true;
    }

    private void CommitManualReleaseAtStep(PendingCollectedNote p, int targetAbsStep, float releaseVelocity = -1f)
    {
        if (p.track == null || p.track.controller == null || p.track.drumTrack == null)
        {
            if (p.collectable != null) p.collectable.OnManualReleaseConsumed();
            return;
        }

        float commitVel = releaseVelocity >= 0f ? releaseVelocity : p.velocity127;

        if (p.collectable != null) p.collectable.MarkAsReportedCollected();
        if (p.collectable != null) p.collectable.OnManualReleaseConsumed();

        // Pitch/duration resolution, commit, audio and marker pulses are shared with the
        // auto-deposit path (InstrumentTrack.CommitPendingNoteAtStep).
        p.track.CommitPendingNoteAtStep(p, targetAbsStep, commitVel,
            vehicleConfig.occupiedStepVelocityMultiplier, vehicleConfig.occupiedStepOctaveAccent, this);

        GrantPostCommitRingCollisionImmunity();
    }

    // ── Approach ring (LEGIBILITY_AUDIT.md C3) ────────────────────────────────────────
    // While carrying a manual note whose release window hasn't opened yet, a ring in the
    // track color contracts toward the hull (VehicleReleaseCue). It vanishes when the window
    // opens — the release tether only shows while the window is open and takes over from there.
    private const float kApproachPollSeconds = 0.05f;
    private float _nextApproachPoll;
    private bool _approachHas;
    private bool _approachOpen;
    private float _approachSecondsUntilOpen;
    private float _approachPolledAt;
    private Color _approachColor = Color.white;

    // Enclosing radius of the ship's own sprite (unrotated local bounds × world scale) — not the
    // collider or any particle/child effect, which can be much larger than the hull.
    private float GetShipSpriteRadiusWorld()
    {
        if (baseSprite != null && baseSprite.sprite != null)
        {
            Vector3 ext = baseSprite.sprite.bounds.extents;
            Vector3 ls = baseSprite.transform.lossyScale;
            float r = Mathf.Max(ext.x * Mathf.Abs(ls.x), ext.y * Mathf.Abs(ls.y));
            if (r > 0f) return r;
        }
        return GetHullRadiusWorld();
    }

    private void TickReleaseApproachCue()
    {
        if (releaseCue == null)
        {
            releaseCue = GetComponent<VehicleReleaseCue>();
            if (releaseCue == null) releaseCue = gameObject.AddComponent<VehicleReleaseCue>();
        }
        releaseCue.centerAnchor = NoteCoreAnchor;
        releaseCue.config = vehicleConfig;

        // Armed releases don't block the cue: the window state skips armed steps, so it reports the
        // NEXT queued note — keeping the open fill up through a quick run of notes while held.
        bool eligible = !_isDead && ManualNoteReleaseEnabled && _pendingNotes.Count > 0;
        if (!eligible) { _approachHas = false; releaseCue.Hide(); return; }

        if (Time.time >= _nextApproachPoll)
        {
            _nextApproachPoll = Time.time + kApproachPollSeconds;
            _approachHas = TryGetReleaseWindowState(out var w) && w.track != null;
            if (_approachHas)
            {
                _approachOpen = w.isOpen;
                _approachSecondsUntilOpen = w.secondsUntilOpen;
                _approachPolledAt = Time.time;
                _approachColor = w.track.DisplayColor;
            }
        }

        if (!_approachHas) { releaseCue.Hide(); return; }

        float secs = _approachSecondsUntilOpen - (Time.time - _approachPolledAt); // extrapolate between polls
        if (_approachOpen || secs <= 0f)
        {
            // Window open (or opened since the last poll): filled pulsing disc = press / hold now.
            releaseCue.SetOpen(_approachColor, GetShipSpriteRadiusWorld());
            return;
        }

        // Approach outline only when nothing is armed, so it doesn't fight the armed note's tether.
        float lookahead = Mathf.Max(0.05f, vehicleConfig.releaseApproachLookaheadSeconds);
        if (_armedReleases.Count > 0 || secs > lookahead) { releaseCue.Hide(); return; }
        releaseCue.SetApproach(_approachColor, 1f - secs / lookahead, GetShipSpriteRadiusWorld());
    }
}
