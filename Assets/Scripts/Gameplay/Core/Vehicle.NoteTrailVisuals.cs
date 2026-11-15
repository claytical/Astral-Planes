using System;
using UnityEngine;

public partial class Vehicle
{
    /// <summary>
    /// Called after a screen-wrap teleport. Resets the position history ring buffer and
    /// clears the TrailRenderer so no line is drawn across the screen between the old
    /// and new positions.
    /// </summary>
    public void ClearTrailForWrap()
    {
        // Clear the rendered trail geometry.
        if (_activeTrailRenderer != null)
            _activeTrailRenderer.Clear();
    }
    // ── Shared timing helpers ──────────────────────────────────────────────

    private static double ComputeStepDuration(DrumTrack drum, int totalAbsSteps)
    {
        int total      = Mathf.Max(1, totalAbsSteps);
        int binSize    = drum != null ? Mathf.Max(1, drum.totalSteps) : total;
        int leaderBins = Mathf.Max(1, Mathf.CeilToInt(total / (float)binSize));
        double loopLen = (drum != null ? drum.GetLoopLengthInSeconds() : 1.0) * leaderBins;
        return loopLen / total;
    }

    private bool TryComputeArmedPulse(ArmedRelease a,
        out float pulse01, out bool inWindow, out bool atExact)
    {
        pulse01 = 0f; inWindow = false; atExact = false;
        if (a.note.track?.controller == null) return false;
        if (!a.note.track.controller.TryGetRawPlayheadAbsStep(out double rawAbs, out _, out int totalRaw))
            return false;
        int    total    = Mathf.Max(1, totalRaw);
        double fwdSteps = (a.targetAbsStep - rawAbs + total) % total;
        double stepDur  = ComputeStepDuration(a.note.track.drumTrack, total);
        double fwdDsp   = fwdSteps * stepDur;
        double gapDsp   = Math.Max(0.001, a.gapDurationDsp);
        pulse01  = 1f - Mathf.Clamp01((float)(fwdDsp / gapDsp));
        inWindow = fwdDsp <= gapDsp;
        atExact  = fwdSteps <= 0.025;
        return true;
    }

    // ──────────────────────────────────────────────────────────────────────

    private void TickNoteTrail()
    {
        if (gfm == null) gfm = GameFlowManager.Instance;
        var viz = gfm != null ? gfm.noteViz : null;

        _carriedTracksScratch.Clear();

        if (_pendingNotes.Count == 0 && _armedReleases.Count == 0)
        {
            ReleaseTrackLock();
            DestroyVehicleTether();
            if (viz != null) viz.UpdateCarryHighlights(_carriedTracksScratch);
            UpdateVehiclePlacementResonance(0f, null);
            return;
        }

        // ------------------------------------------------------------------
        // Compute pulse01 — how close the playhead is to the next target step.
        // Armed note takes priority over pending; both use the same approach:
        // remaining DSP time / gap DSP time, so the ramp is gap-normalised and
        // stable across bin expansions.
        // ------------------------------------------------------------------
        float pulse01 = 0f;
        bool isAuthoritative = true;
        bool inTimingWindow = false;
        bool atExactStep = false;

        if (_armedReleases.Count > 0)
        {
            var a = _armedReleases.Peek();
            if (TryComputeArmedPulse(a, out pulse01, out inTimingWindow, out atExactStep))
            {
                if (a.note.track.IsExpansionPending)
                    pulse01 = Mathf.Min(pulse01, 0.3f);

                var  aDrum        = a.note.track.drumTrack;
                int  aBinSize     = aDrum != null ? Mathf.Max(1, aDrum.totalSteps) : 1;
                int  aTargetLocal = ((a.targetAbsStep % aBinSize) + aBinSize) % aBinSize;
                bool aMatchesAuthored = (a.note.authoredAbsStep < 0) || (a.targetAbsStep == a.note.authoredAbsStep);
                isAuthoritative = aMatchesAuthored || (aTargetLocal == a.note.authoredLocalStep);
            }
        }
        else if (_pendingNotes.Count > 0)
        {
            // Use the same next-unlit-step the ghost cue is pointing at, so the
            // ring tracks the real release window rather than the authored step.
            var p = _pendingNotes.Peek();
            if (p.track != null && p.track.controller != null && p.track.drumTrack != null &&
                p.track.controller.TryGetRawPlayheadAbsStep(out double rawAbsP, out _, out int totalP))
            {
                int total = Mathf.Max(1, totalP);

                // Reuse the spoken-for set so we agree with the ghost cue.
                _spokenForScratch.Clear();
                foreach (var ar in _armedReleases)
                    _spokenForScratch.Add(ar.targetAbsStep);

                if (viz != null && viz.TryGetNextUnlitStepExcluding(
                        p.track, rawAbsP, total, _spokenForScratch, out int nextStep))
                {
                    double fwdSteps = (nextStep - rawAbsP + total) % total;

                    var    pDrum    = p.track.drumTrack;
                    int    pBinSize = pDrum != null ? Mathf.Max(1, pDrum.totalSteps) : 1;
                    double pStepDur = ComputeStepDuration(pDrum, total);
                    double fwdDsp   = fwdSteps * pStepDur;

                    // Ring window is always effectiveArmSteps wide regardless of
                    // whether the target is in an expansion bin. The +lead gives a visible
                    // heads-up before the commit gate opens.
                    const float ringWindowLead = 1.5f;
                    double windowDsp = (vehicleConfig.EffectiveArmAheadSteps(pStepDur) + ringWindowLead) * pStepDur;
                    pulse01 = 1f - Mathf.Clamp01((float)(fwdDsp / Math.Max(0.001, windowDsp)));
                    inTimingWindow = fwdDsp <= windowDsp;
                    atExactStep = fwdSteps <= 0.025;

                    int nextLocal = ((nextStep % pBinSize) + pBinSize) % pBinSize;
                    bool pMatchesAuthored = (p.authoredAbsStep >= 0) && (nextStep == p.authoredAbsStep);
                    isAuthoritative = pMatchesAuthored || (nextLocal == p.authoredLocalStep);
                }
            }
        }

        InstrumentTrack cueTrack = null;
        if (_armedReleases.Count > 0)
            cueTrack = _armedReleases.Peek().note.track;
        else if (_pendingNotes.Count > 0)
            cueTrack = _pendingNotes.Peek().track;

        UpdateVehiclePlacementResonance(pulse01, cueTrack, isAuthoritative);

        // Armed notes: fly orb toward its target marker (or stay in the core until it exists).
        int armedSlot = 0;
        foreach (var ar in _armedReleases)
        {
            if (ar.note.collectable == null) { armedSlot++; continue; }

            Vector3 markerWorld = Vector3.zero;
            bool hasMarkerPos = false;
            if (viz != null && ar.note.track != null &&
                viz.noteMarkers != null &&
                viz.noteMarkers.TryGetValue((ar.note.track, ar.targetAbsStep), out var markerTr) && markerTr != null)
            {
                markerWorld = markerTr.position;
                hasMarkerPos = true;
            }

            if (hasMarkerPos)
                ar.note.collectable.SetTrailTarget(markerWorld);
            else
                ar.note.collectable.HoldInCore(NoteCoreAnchor);

            ar.note.collectable.SetReleasePulse(armedSlot == 0 ? pulse01 : 0f);
            armedSlot++;
        }

        // Pending notes: carried rigidly in the vehicle's hollow core, like fuel in a tank —
        // no trailing slot, no lag, no drift.
        int slot = armedSlot;
        foreach (var p in _pendingNotes)
        {
            if (p.collectable == null) { slot++; continue; }

            p.collectable.HoldInCore(NoteCoreAnchor);
            p.collectable.SetReleasePulse(slot == 0 && _armedReleases.Count == 0 ? pulse01 : 0f);
            slot++;
        }

        // Single Vehicle-owned tether: create, update, or destroy.
        bool hasNotes = _pendingNotes.Count > 0 || _armedReleases.Count > 0;
        if (!hasNotes)
        {
            DestroyVehicleTether();
        }
        else
        {
            if (_vehicleTether == null && viz != null && viz.noteTetherPrefab != null)
            {
                var go = Instantiate(viz.noteTetherPrefab);
                _vehicleTether = go.GetComponent<NoteTether>() ?? go.AddComponent<NoteTether>();
                Color col = Color.white;
                if (_pendingNotes.Count > 0 && _pendingNotes.Peek().track != null)
                    col = _pendingNotes.Peek().track.DisplayColor;
                else if (_armedReleases.Count > 0 && _armedReleases.Peek().note.track != null)
                    col = _armedReleases.Peek().note.track.DisplayColor;
                _vehicleTether.SetEndpoints(null, null, col);
            }

            if (_vehicleTether != null)
            {
                // Tether leaves from the hull edge on the side facing its placeholder step.
                _vehicleTether.SetStartAnchorCircle(transform.position, GetHullRadiusWorld());
                UpdateVehicleTether(viz);

                if (_vehicleTether.IsShown)
                    viz?.SetManualReleaseCuePosition(transform, _vehicleTether.EvaluatePosition01(_vehicleTether.ReleaseProgress01), _vehicleTether.IsApproaching);
                else
                    viz?.ClearManualReleaseCue(transform);
            }
        }

        foreach (var ar in _armedReleases) if (ar.note.track != null) _carriedTracksScratch.Add(ar.note.track);
        foreach (var p in _pendingNotes)   if (p.track != null)       _carriedTracksScratch.Add(p.track);
        if (viz != null) viz.UpdateCarryHighlights(_carriedTracksScratch);
    }

    private void UpdateVehicleTether(NoteVisualizer viz)
    {
        if (_vehicleTether == null) return;

        // ── Armed-release state ─────────────────────────────────────────────────
        if (_armedReleases.Count > 0)
        {
            var a = _armedReleases.Peek();
            if (a.note.track?.controller == null) return;
            if (!TryComputeArmedPulse(a, out float pulse, out bool inWin, out bool atExact)) return;
            _vehicleTether.BindByStep(a.note.track, a.targetAbsStep, viz);
            _vehicleTether.SetReleaseProgress(pulse);
            _vehicleTether.SetTimingState(pulse, inWin, atExact);
            _vehicleTether.SetApproaching(inWin);
            return;
        }

        // ── Pending-note state ──────────────────────────────────────────────────
        if (_pendingNotes.Count == 0) return;
        var p = _pendingNotes.Peek();
        if (p.track?.controller == null || p.track.drumTrack == null) return;
        if (!p.track.controller.TryGetRawPlayheadAbsStep(out double rawAbsP, out _, out int totalP)) return;

        int    tot      = Mathf.Max(1, totalP);
        double pStepDur = ComputeStepDuration(p.track.drumTrack, tot);
        double tetherWin   = vehicleConfig.EffectiveArmAheadSteps(pStepDur) * pStepDur;
        double graceDsp    = vehicleConfig.manualReleaseGracePeriodSteps * pStepDur;
        double playheadInLoop = rawAbsP % tot;

        // Find the nearest forward unlit step. No FIFO exclusions — one tether shows one step at a time.
        int  nextTarget = -1;
        bool resolved   = viz != null &&
            viz.TryGetNextUnlitStepExcluding(p.track, rawAbsP, tot, null, out nextTarget);

        int   currentBound  = _vehicleTether.boundStep;
        float notePulse     = 0f;
        bool  noteInWindow  = false;

        // Grace hold: stay on the just-passed step until its grace window expires.
        bool inGraceForBound = false;
        if (graceDsp > 0 && currentBound >= 0 && resolved && nextTarget != currentBound)
        {
            double fwdToBound    = (currentBound - rawAbsP + tot) % tot;
            double backFromBound = tot - fwdToBound;
            double backDspBound  = backFromBound * pStepDur;
            // Suppress grace that crosses the loop boundary (from a previous iteration).
            if (backDspBound <= graceDsp && backFromBound <= playheadInLoop + 0.001)
            {
                inGraceForBound = true;
                noteInWindow    = true;
                notePulse       = 1f - Mathf.Clamp01((float)(backDspBound / graceDsp));
            }
        }

        if (!inGraceForBound && resolved)
            _vehicleTether.BindByStep(p.track, nextTarget, viz);

        if (!inGraceForBound)
        {
            int visualStep = _vehicleTether.boundStep >= 0 ? _vehicleTether.boundStep : p.authoredAbsStep;
            if (visualStep >= 0)
            {
                double fwdSteps2 = (visualStep - rawAbsP + tot) % tot;
                double backSteps = tot - fwdSteps2;
                double fwdDsp2   = fwdSteps2 * pStepDur;
                double backDsp   = backSteps  * pStepDur;
                bool   inArmWin  = fwdDsp2 <= tetherWin;
                // Suppress cross-boundary grace (step passed in a previous iteration).
                bool   inGrace   = graceDsp > 0 && backDsp <= graceDsp && backSteps <= playheadInLoop + 0.001;
                noteInWindow = inArmWin || inGrace;
                bool atExact = fwdSteps2 <= 0.025;
                notePulse = inArmWin
                    ? 1f - Mathf.Clamp01((float)(fwdDsp2 / System.Math.Max(0.001, tetherWin)))
                    : inGrace
                        ? 1f - Mathf.Clamp01((float)(backDsp / System.Math.Max(0.001, graceDsp)))
                        : 0f;
                // Suppress the arm window for a step only reachable by crossing the loop boundary.
                if (inArmWin && playheadInLoop + fwdSteps2 >= tot)
                {
                    noteInWindow = false;
                    notePulse    = 0f;
                }
                _vehicleTether.SetReleaseProgress(notePulse);
                _vehicleTether.SetTimingState(notePulse, noteInWindow, atExact);
                _vehicleTether.SetApproaching(inArmWin);
                return;
            }
        }

        _vehicleTether.SetReleaseProgress(notePulse);
        _vehicleTether.SetTimingState(notePulse, noteInWindow, false);
        _vehicleTether.SetApproaching(false);
    }

    private void UpdateVehiclePlacementResonance(float pulse01, InstrumentTrack cueTrack, bool isAuthoritative = true)
    {
        if (!vehicleConfig.useVehiclePlacementResonance)
            return;

        Color roleColor = _vehicleDefaultColor;
        if (cueTrack != null)
            roleColor = cueTrack.DisplayColor;

        float tint01 = 0f;
        if (cueTrack != null && pulse01 > 0f)
            tint01 = Mathf.Clamp01(Mathf.Max(vehicleConfig.vehiclePlacementMinTint, pulse01));

        // -----------------------------------------------------------------
        // Root vehicle sprite: color resonance only
        // -----------------------------------------------------------------
        // Skipped while a damage flicker/thud coroutine owns baseSprite.color — it snapshots
        // and hard-restores color on its own schedule (VisualFeedbackUtility), so writing here
        // concurrently would just get stomped and cause a visible pop when the flicker ends.
        if (baseSprite != null && !isFlickering)
        {
            Color targetVehicleColor = Color.Lerp(_vehicleDefaultColor, roleColor, tint01);
            baseSprite.color = Color.Lerp(
                baseSprite.color,
                targetVehicleColor,
                vehicleConfig.vehiclePlacementColorLerpSpeed * Time.deltaTime
            );
        }

        // -----------------------------------------------------------------
        // Soul clone: hidden by default, scales outward as readiness grows
        // -----------------------------------------------------------------
        if (soulSprite != null)
        {
            bool active = cueTrack != null;

            if (!active)
            {
                soulSprite.transform.localScale = Vector3.Lerp(
                    soulSprite.transform.localScale,
                    Vector3.one * profile.soulMaxScale,
                    profile.soulScaleLerpSpeed * Time.deltaTime
                );

                Color soulFade = soulSprite.color;
                soulFade.r = roleColor.r;
                soulFade.g = roleColor.g;
                soulFade.b = roleColor.b;
                soulFade.a = Mathf.Lerp(soulFade.a, 0f, profile.soulScaleLerpSpeed * Time.deltaTime);
                soulSprite.color = soulFade;

                if (soulFade.a <= 0.01f)
                    soulSprite.enabled = false;

                return;
            }

            soulSprite.enabled = true;

            float soulScale = Mathf.Lerp(profile.soulMinScale, profile.soulMaxScale, pulse01);
            soulSprite.transform.localScale = Vector3.Lerp(
                soulSprite.transform.localScale,
                Vector3.one * soulScale,
                profile.soulScaleLerpSpeed * Time.deltaTime
            );

            float soulAlpha = Mathf.Lerp(profile.soulAlphaMin, profile.soulAlphaMax, pulse01);

            Color targetSoulColor = isAuthoritative ? Color.white : roleColor;
            targetSoulColor.a = soulAlpha;

            soulSprite.color = Color.Lerp(
                soulSprite.color,
                targetSoulColor,
                profile.soulScaleLerpSpeed * Time.deltaTime
            );
        }
    }
}
