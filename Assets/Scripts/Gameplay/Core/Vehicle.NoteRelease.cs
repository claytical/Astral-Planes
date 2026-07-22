using UnityEngine;
using System.Collections.Generic;

public partial class Vehicle
{
    public static bool AnyVehicleCarrying() => _s_vehiclesCarrying > 0;
    public static bool AnyVehicleCarryingTrack(InstrumentTrack track)
        => _s_vehiclesCarryingByTrack.TryGetValue(track, out var count) && count > 0;

    public bool CanAcceptCollectable(InstrumentTrack track) =>
        _lockedTrack == null || _lockedTrack == track;

    private void AcquireTrackLock(InstrumentTrack track)
    {
        if (_lockedTrack != null) return;
        _lockedTrack = track;
        _s_vehiclesCarrying++;
        if (track != null)
            _s_vehiclesCarryingByTrack[track] =
                (_s_vehiclesCarryingByTrack.TryGetValue(track, out int c) ? c : 0) + 1;
    }

    private void ReleaseTrackLock()
    {
        if (_lockedTrack == null) return;
        if (_lockedTrack != null && _s_vehiclesCarryingByTrack.TryGetValue(_lockedTrack, out int c))
            _s_vehiclesCarryingByTrack[_lockedTrack] = Mathf.Max(0, c - 1);
        _lockedTrack = null;
        _s_vehiclesCarrying = Mathf.Max(0, _s_vehiclesCarrying - 1);
    }

    public void ClearPendingNotesForBridge()
    {
        _pendingNotes.Clear();
        _armedReleases.Clear();
        _releaseButtonHeld = false;
        _lastArmWasFromHold = false;
        ReleaseTrackLock();
        DestroyVehicleTether();
    }

    private void DiscardCarriedCollectables()
    {
        foreach (var armed in _armedReleases)
            armed.note.collectable?.OnManualReleaseDiscarded();
        _armedReleases.Clear();

        foreach (var pending in _pendingNotes)
            pending.collectable?.OnManualReleaseDiscarded();
        _pendingNotes.Clear();

        _releaseButtonHeld = false;
        _lastArmWasFromHold = false;
        ReleaseTrackLock();
        DestroyVehicleTether();
    }

    private void DestroyVehicleTether()
    {
        if (_vehicleTether == null) return;
        Destroy(_vehicleTether.gameObject);
        _vehicleTether = null;

        if (gfm == null) gfm = GameFlowManager.Instance;
        gfm?.noteViz?.ClearManualReleaseCue(transform);
    }

    private void ResolvePlaybackNote(PendingCollectedNote p, int atStep, out int midi, out int dur)
    {
        midi = p.collectedMidi;
        dur  = p.durationTicks;
        int binSize   = Mathf.Max(1, p.track.BinSize());
        int localStep = ((atStep % binSize) + binSize) % binSize;
        var noteSet   = p.track.GetNoteSetForBin(p.track.BinIndexForStep(atStep));
        if (noteSet != null && noteSet.TryGetTemplateTimingAtStep(localStep, out int authoredDur, out _))
            dur = authoredDur;
    }

    private void DiscardPendingNote(PendingCollectedNote p)
    {
        _pendingNotes.Dequeue();
        if (p.collectable != null) p.collectable.OnManualReleaseDiscarded();
        p.track.NotifyNoteDiscarded(p.burstId, p.authoredAbsStep);
    }

    // Enqueue a collected note for manual release. Returns true if queued.
    private bool EnqueuePendingCollectedNote(PendingCollectedNote p)
    {
        int cap = Mathf.Max(1, vehicleConfig.manualReleaseQueueCapacity);

        // If full, drop oldest (and clean up its visual carrier if still around)
        while (_pendingNotes.Count >= cap)
        {
            var dropped = _pendingNotes.Dequeue();
            if (dropped.collectable != null)
                dropped.collectable.OnManualReleaseDiscarded();
            dropped.track?.NotifyNoteDiscarded(dropped.burstId, dropped.authoredAbsStep);
        }

        AcquireTrackLock(p.track);
        _pendingNotes.Enqueue(p);
        return true;
    }


    public bool HasCapturedCollectablesPendingRelease()
    {
        return _pendingNotes.Count > 0 || _armedReleases.Count > 0;
    }

    // Back-compat with earlier patches / external callers.
    public bool EnqueuePendingNote(PendingCollectedNote p) => EnqueuePendingCollectedNote(p);

    public bool TryReleaseQueuedNote(bool allowSacrifice = true)
{
    if (_pendingNotes.Count <= 0) return false;

    if (gfm == null) gfm = GameFlowManager.Instance;
    var viz = (gfm != null) ? gfm.noteViz : null;

    // Peek first — only dequeue once we know the window passes.
    // An early press must leave the note in the queue so the cue remains visible.
    var p = _pendingNotes.Peek();
    if (p.track == null || p.track.controller == null || p.track.drumTrack == null)
    {
        _pendingNotes.Dequeue();
        if (p.collectable != null) p.collectable.OnManualReleaseConsumed();
        viz?.BlastManualReleaseCue(transform);
        return false;
    }

    if (!p.track.controller.TryGetRawPlayheadAbsStep(out double rawAbs, out int floorAbs, out int totalSteps))
    {
        _pendingNotes.Dequeue();
        if (p.collectable != null) p.collectable.OnManualReleaseDiscarded();
        p.track.NotifyNoteDiscarded(p.burstId, p.authoredAbsStep);
        CollectEnergy(p.collectable.amount * .25f);
//sacrifice note to gain small amount of energy instead of specific failure
        //        viz?.BlastManualReleaseCueFailure(transform, p.track, p.authoredAbsStep);
        return false;
    }

    // Build the set of steps already spoken for by in-flight armed releases.
    // These steps have not committed yet so their markers still say isPlaceholder=true,
    // but they are no longer available targets.
    var spokenFor = new HashSet<int>();
    foreach (var ar in _armedReleases)
        spokenFor.Add(ar.targetAbsStep);

    int effectiveTotal = Mathf.Max(totalSteps, p.track.GetTotalSteps());

    // Find nearest forward unlit placeholder that isn't already armed.
    if (viz == null || !viz.TryGetNextUnlitStepExcluding(p.track, rawAbs, effectiveTotal, spokenFor, out int targetAbsStep))
    {
        ResolvePlaybackNote(p, floorAbs, out int midiNoStep, out int durNoStep);
        p.track.PlayOneShotMidi(midiNoStep, p.velocity127, durNoStep);
        DiscardPendingNote(p);
        if (p.collectable != null) CollectEnergy(p.collectable.amount * .25f);

        //        viz?.BlastManualReleaseCueFailure(transform, p.track, p.authoredAbsStep);
        CollectionSoundManager.Instance?.PlayReleaseFailure();
        return false;
    }

    double fwdToTarget = (targetAbsStep - rawAbs + effectiveTotal) % effectiveTotal;

    // Hoist stepDur here so effectiveArmSteps can use it for the minimum-seconds floor,
    // and so the arm-lock path below can reuse it without a second GetLoopLengthInSeconds call.
    double stepDur = ComputeStepDuration(p.track.drumTrack, effectiveTotal);
    float effectiveArmSteps = vehicleConfig.EffectiveArmAheadSteps(stepDur);

    bool inAheadWindow = fwdToTarget <= effectiveArmSteps;
    double backFromTarget = effectiveTotal - fwdToTarget;
    bool inGraceWindow = vehicleConfig.manualReleaseGracePeriodSteps > 0f &&
                         backFromTarget <= vehicleConfig.manualReleaseGracePeriodSteps;
    bool pass = inAheadWindow || inGraceWindow;

    if (!pass && viz != null &&
        viz.TryGetNearestUnlitStepExcluding(p.track, rawAbs, effectiveTotal, spokenFor, out int nearestAbsStep, out double nearestFwd))
    {
        double nearestBack = effectiveTotal - nearestFwd;
        bool nearestPass = nearestFwd <= effectiveArmSteps ||
                           (vehicleConfig.manualReleaseGracePeriodSteps > 0f && nearestBack <= vehicleConfig.manualReleaseGracePeriodSteps);
        if (nearestPass)
        {
            targetAbsStep = nearestAbsStep;
            fwdToTarget = nearestFwd;
            backFromTarget = nearestBack;
            inAheadWindow = fwdToTarget <= effectiveArmSteps;
            inGraceWindow = vehicleConfig.manualReleaseGracePeriodSteps > 0f &&
                            backFromTarget <= vehicleConfig.manualReleaseGracePeriodSteps;
            pass = true;
            if (GameFlowManager.VerboseLogging) Debug.Log($"[RELEASE_RETARGET] oldTarget rejected, newTarget={targetAbsStep} rawAbs={rawAbs:F2} fwd={fwdToTarget:F2} back={backFromTarget:F2} PASS=True");
        }
    }
    if (GameFlowManager.VerboseLogging) Debug.Log($"[RELEASE_GATE] target={targetAbsStep} rawAbs={rawAbs:F2} fwd={fwdToTarget:F2} back={backFromTarget:F2} window={effectiveArmSteps:F1} grace={vehicleConfig.manualReleaseGracePeriodSteps:F1} effectiveTotal={effectiveTotal} PASS={pass}");

    if (!pass)
    {
        // Hold-cascade callers pass allowSacrifice=false: leave the note in queue so it
        // can be armed on a later tick when its step enters the window.
        if (!allowSacrifice) return false;

        if (GameFlowManager.VerboseLogging) Debug.Log($"[SACRIFICE] target={targetAbsStep} rawAbs={rawAbs:F2} fwd={fwdToTarget:F2} — note sacrificed outside timing window");
        ResolvePlaybackNote(p, targetAbsStep, out int midiToPlay, out int durToPlay);
        p.track.PlayOneShotMidi(midiToPlay, p.velocity127, durToPlay);
        DiscardPendingNote(p);
        Vector3 blastPos = p.collectable != null ? p.collectable.transform.position : transform.position;
        viz?.BlastManualReleaseCueFailure(transform, blastPos, p.track.DisplayColor);
        if (p.collectable != null) CollectEnergy(p.collectable.amount * .25f);
        return false;
    }

    // Window passed — now consume the note.
    _pendingNotes.Dequeue();

    bool lateGracePass = inGraceWindow && !inAheadWindow;

    // Timing-based velocity: 0 = earliest window open (~vel 40), 1 = exact step (vel 127).
    float releaseWindowLerp = inAheadWindow
        ? 1f - Mathf.Clamp01((float)(fwdToTarget / Mathf.Max(0.001f, effectiveArmSteps)))
        : inGraceWindow
            ? 1f - Mathf.Clamp01((float)(backFromTarget / Mathf.Max(0.001f, vehicleConfig.manualReleaseGracePeriodSteps)))
            : 0f;
    float releaseVelocity = Mathf.Lerp(40f, 127f, releaseWindowLerp);

    if (pass && vehicleConfig.manualReleaseUseArmLock && !lateGracePass)
    {
        // stepDur and leaderBins already computed above for the gate checks.
        double gapDsp  = fwdToTarget * stepDur;

        _armedReleases.Enqueue(new ArmedRelease
        {
            note            = p,
            targetAbsStep   = targetAbsStep,
            totalAbsSteps   = effectiveTotal,
            gapDurationDsp  = gapDsp,
            releaseVelocity = releaseVelocity
        });
        return true;
    }

    if (pass)
    {
        CommitManualReleaseAtStep(p, targetAbsStep, releaseVelocity);
        viz?.BlastManualReleaseCue(transform);
        return true;
    }

    // Defensive guard: all non-pass paths should have returned above.
    if (GameFlowManager.VerboseLogging) Debug.Log($"[RELEASE_BLOCKED] target={targetAbsStep} rawAbs={rawAbs:F2} fwd={fwdToTarget:F2} window={effectiveArmSteps:F1} PASS=False commitSkipped=True");
    return false;
}
}
