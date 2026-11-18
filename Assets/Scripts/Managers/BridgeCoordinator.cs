using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// Ownership: BridgeCoordinator may mutate only motif bridge orchestration state
/// (bridge snapshots, freeze/fade execution, and motif bridge sequencing).
/// </summary>
public sealed class BridgeCoordinator
{
    private readonly GameFlowManager _gameFlow;
    private readonly SessionStateCoordinator _session;
    private readonly List<MotifSnapshot> _motifSnapshots = new();
    private float _motifStartTime;

    public BridgeCoordinator(GameFlowManager gameFlow, SessionStateCoordinator session)
    {
        _gameFlow = gameFlow;
        _session = session;
    }

    public IReadOnlyList<MotifSnapshot> MotifSnapshots => _motifSnapshots;

    /// <summary>
    /// The motif that was in progress when the run ended (all players out of energy), or null if
    /// no notes had been placed in it. Display-only for TrackFinished — never added to
    /// MotifSnapshots or saved to disk, so it doesn't count as a completed motif.
    /// </summary>
    public MotifSnapshot UnfinishedSnapshot { get; private set; }

    public void ClearSessionMotifs()
    {
        _motifSnapshots.Clear();
        UnfinishedSnapshot = null;
    }

    /// <summary>
    /// Game over: records the in-progress motif's loop as an unfinished record. Must run before
    /// InstrumentTrackController.BeginGameOverFade (which rewrites every loop note's duration)
    /// and before the scene unload destroys the tracks.
    /// </summary>
    public void CaptureUnfinishedSnapshot()
    {
        var allTracks = (_gameFlow.controller?.tracks != null)
            ? _gameFlow.controller.tracks.Where(t => t != null).ToList()
            : new List<InstrumentTrack>();

        var snap = BuildPhaseSnapshotForBridge(allTracks, _gameFlow.activeDrumTrack);
        UnfinishedSnapshot = snap.CollectedNotes != null && snap.CollectedNotes.Count > 0 ? snap : null;
    }

    /// <param name="endMatchAfterSpin">Competitive motif mode: after the ring spins off, show the match results instead of the interstitial maze.</param>
    public IEnumerator PlayMotifBridgeAndRestart(bool endMatchAfterSpin = false)
    {
        _session.SetGhostCycleInProgress(true);
        _session.SetBridgePending(true);

        FreezeGameplayForBridge();

        var allTracks = (_gameFlow.controller?.tracks != null)
            ? _gameFlow.controller.tracks.Where(t => t != null).ToList()
            : new List<InstrumentTrack>();

        var motifSnap = BuildPhaseSnapshotForBridge(allTracks, _gameFlow.activeDrumTrack);

        float effectiveLoopSec = (_gameFlow.controller != null)
            ? Mathf.Max(1f, _gameFlow.controller.GetEffectiveLoopLengthInSeconds())
            : Mathf.Max(1f, _gameFlow.GetMotifBridgeHoldSeconds());

        // Snapshot now so DrumTrack's leaderStartDspTime advancing at the loop boundary
        // mid-animation doesn't shift the target into the next cycle.
        double loopBoundaryDsp = (_gameFlow.activeDrumTrack != null && _gameFlow.activeDrumTrack.leaderStartDspTime > 0)
            ? _gameFlow.activeDrumTrack.leaderStartDspTime + effectiveLoopSec
            : 0;

        float remainingInLoop = effectiveLoopSec;
        if (_gameFlow.activeDrumTrack != null && _gameFlow.activeDrumTrack.leaderStartDspTime > 0)
        {
            float elapsed = (float)(AudioSettings.dspTime - _gameFlow.activeDrumTrack.leaderStartDspTime);
            remainingInLoop = Mathf.Max(0.1f, effectiveLoopSec - Mathf.Clamp(elapsed, 0f, effectiveLoopSec));
        }

        if (_gameFlow.dustGenerator != null)
            _gameFlow.dustGenerator.HardStopRegrowthForBridge(hideTransientDust: true);

        if (_gameFlow.dustGenerator != null)
            _gameFlow.dustGenerator.BeginSlowFadeAllDust(remainingInLoop);

        _motifSnapshots.Add(motifSnap);
        RingSessionStore.SaveRingToDisk(motifSnap);

        // SpinAndRollOffActiveRings waits out the rest of the current loop pass itself
        // (guarded by _spinOffPending, which also suppresses the ordinary per-bin auto-hide
        // for the whole wait) before it starts the visual spin/roll — see that method.
        var rings = _gameFlow.GetMotifRingGlyphApplicator();
        float spinDur = rings != null ? (rings.config?.spinOffDuration ?? 0.45f) : 0f;
        float rollDur = rings != null ? (rings.config?.rollOffDuration ?? 1.5f) : 0f;

        // A wisp bridge sample still playing from this motif fades out with the record
        // animation (rest of loop + spin + roll), so the next maze starts audio-clean and its
        // own wisp/sample isn't fighting this one.
        Wisp.FadeOutActiveBridgeSamples(remainingInLoop + spinDur + rollDur);

        if (rings != null)
        {
            yield return _gameFlow.StartCoroutine(rings.SpinAndRollOffActiveRings(spinDur, rollDur));
        }
        else
        {
            // No record to follow: still take the loop's markers off the timeline.
            _gameFlow.noteViz?.SendLoopMarkersWithRecord(null, 0f, 0f);
        }

        // Final safety sweep for any stray tether that didn't already retract gracefully
        // (e.g. DetachPendingRingTethers only covers ring preview tethers). Runs after the
        // spin-off, not before, so it no longer stomps on the graceful detach timed to it.
        _gameFlow.CleanupAllNoteTethers();

        // Let all bins finish their final cycle before clearing the loop.
        // If the ring animation already outlasted the loop, this is a no-op.
        if (loopBoundaryDsp > 0)
            while (AudioSettings.dspTime < loopBoundaryDsp)
                yield return null;

        if (endMatchAfterSpin)
        {
            // Clear the ghost-cycle guard first: TryBeginMatchEnd refuses while it is set.
            _session.SetGhostCycleInProgress(false);
            _session.SetBridgePending(false);
            _gameFlow.BeginCompetitiveMatchEnd(MatchEndReason.MotifComplete);
            yield break;
        }

        yield return _gameFlow.StartCoroutine(_gameFlow.PathChoice.RunPathChoiceAndAdvance());
        _session.SetGhostCycleInProgress(false);
        _session.SetBridgePending(false);

        // If every player ran out of energy inside the interstitial maze, the game-over check
        // fired from Vehicle.ConsumeEnergy was swallowed while GhostCycleInProgress was true —
        // re-run it now that the flag is clear.
        _gameFlow.CheckAllPlayersOutOfEnergy();
    }

    private MotifSnapshot BuildPhaseSnapshotForBridge(List<InstrumentTrack> retained, DrumTrack drum)
    {
        var snapshot = new MotifSnapshot { Timestamp = Time.time };

        snapshot.Pattern = _gameFlow.phaseTransitionManager != null
            ? _gameFlow.phaseTransitionManager.currentPhase
            : MazeArchetype.Windows;
        snapshot.Color = _gameFlow.dustGenerator != null ? _gameFlow.dustGenerator.MazeColor() : Color.white;
        snapshot.TotalSteps = (drum != null && drum.totalSteps > 0) ? drum.totalSteps : 16;
        snapshot.Bpm = (drum != null && drum.drumLoopBPM > 0f) ? drum.drumLoopBPM : 120f;
        snapshot.StepDurationSec = drum != null ? drum.GetAuthoredStepSeconds() : 0f; // 0 ⇒ preview falls back
        snapshot.MotifKeyRootMidi = (_gameFlow.phaseTransitionManager != null && _gameFlow.phaseTransitionManager.currentMotif != null)
            ? _gameFlow.phaseTransitionManager.currentMotif.keyRootMidi
            : 60;
        snapshot.PhaseId     = _gameFlow.phaseTransitionManager?.CurrentPhaseId;
        snapshot.PhaseIndex  = _gameFlow.phaseTransitionManager?.CurrentPhaseIndex ?? 0;
        snapshot.MotifIndex  = _gameFlow.phaseTransitionManager?.currentMotifIndex ?? 0;
        snapshot.MotifId     = _gameFlow.phaseTransitionManager?.currentMotif?.motifId;

        if (retained == null || retained.Count == 0) return snapshot;

        snapshot.LeaderBins = Mathf.Max(1, retained.Max(t => t != null ? t.loopMultiplier : 1));

        float motifStartTime = _motifStartTime > 0f ? _motifStartTime : snapshot.Timestamp;

        foreach (var track in retained)
        {
            if (!track) continue;
            var notes = track.GetPersistentLoopNotes();
            if (notes == null || notes.Count == 0) continue;
            int binSize = Mathf.Max(1, track.BinSize());

            foreach (var n in notes.OrderBy(n => n.stepIndex))
            {
                int binIndex = n.stepIndex / binSize;
                int localStep = n.stepIndex % binSize;
                float commitTime01 = (binSize > 1) ? localStep / (float)(binSize - 1) : 0.5f;

                var entry = new MotifSnapshot.NoteEntry(
                    step: n.stepIndex,
                    note: n.note,
                    velocity: n.velocity,
                    trackColor: track.DisplayColor,
                    binIndex: binIndex,
                    isMatched: true,
                    commitTime01: commitTime01,
                    durationTicks: n.duration);
                entry.Role = track.assignedRole;
                entry.HasRole = true;
                snapshot.CollectedNotes.Add(entry);
            }

            var prof = track.ActiveProfile ?? MusicalRoleProfileLibrary.GetProfile(track.assignedRole);
            int allocatedBins = Mathf.Max(1, track.loopMultiplier);
            for (int b = 0; b < allocatedBins; b++)
            {
                snapshot.TrackBins.Add(new MotifSnapshot.TrackBinData
                {
                    TrackColor = track.DisplayColor,
                    Role = track.assignedRole,
                    HasPlaybackProfile = prof != null,
                    MidiPreset = prof != null ? prof.midiPreset : 0,
                    VolumeMultiplier = prof != null ? prof.volumeMultiplier : 1f,
                    MinVelocity = prof != null ? prof.minVelocity : 60,
                    MaxVelocity = prof != null ? prof.maxVelocity : 127,
                    BinIndex = b,
                    IsFilled = track.IsBinFilled(b),
                    CompletionTime = track.GetBinCompletionTime(b),
                    MotifStartTime = motifStartTime,
                });
            }
        }

        return snapshot;
    }

    /// <summary>Competitive match end: same teardown as a bridge, but the rings are left playing.</summary>
    public void FreezeGameplayForMatchEnd() => FreezeGameplayForBridge();

    private void FreezeGameplayForBridge()
    {
        // Ring preview tethers are no longer hard-killed here — they detach gracefully in sync
        // with the ring's spin-off (see DetachPendingRingTethers, called from
        // SpinAndRollOffActiveRings). Vehicle carry-tethers are torn down below via
        // ClearPendingNotesForBridge -> DestroyVehicleTether. A final CleanupAllNoteTethers()
        // safety sweep still runs after the spin-off completes (see PlayMotifBridgeAndRestart).
        foreach (var v in _gameFlow.GetVehicles())
            v?.ClearPendingNotesForBridge();

        foreach (var c in Object.FindObjectsByType<Collectable>())
            if (c != null) Object.Destroy(c.gameObject);

        // A carried wisp lives only until its motif's first loop ends; never into the next bridge.
        foreach (var w in Object.FindObjectsByType<Wisp>())
            if (w != null) w.Dismiss();

        foreach (var n in Object.FindObjectsByType<DiscoveryTrackNode>())
        {
            if (n == null) continue;
            var explode = n.GetComponent<Explode>();
            if (explode != null) explode.Permanent();
            else Object.Destroy(n.gameObject);
        }

        if (_gameFlow.activeDrumTrack?._starPool != null)
        {
            _gameFlow.activeDrumTrack._starPool.ExplodeAndClearAll();
            Object.Destroy(_gameFlow.activeDrumTrack._starPool.gameObject);
            _gameFlow.activeDrumTrack._starPool = null;
        }
    }

    public void StampMotifStartTime() => _motifStartTime = Time.time;
}
