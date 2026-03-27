using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Debug = UnityEngine.Debug;

public partial class GameFlowManager
{
    public void BeginMotifBridge(string who)
    {
        StartCoroutine(PlayMotifBridgeAndRestart());
    }

    private IEnumerator PlayMotifBridgeAndRestart()
    {
        GhostCycleInProgress = true;
        BridgePending = true;
        FreezeGameplayForBridge();
        // Stop regrowth coroutines BEFORE deactivating activeDustRoot so StopCoroutine
        // calls inside HideVisualsInstant execute on active objects. Calling HardStop after
        // SetBridgeCinematicMode(true) deactivates the root, which causes StopCoroutine to
        // silently fail — handles are nulled but coroutines remain in flight. Those orphaned
        // coroutines resume when the root is re-enabled and run scale ramps to completion,
        // producing "sprite visible / emission off" artifacts at the next motif start.
        if (dustGenerator != null)
            dustGenerator.HardStopRegrowthForBridge(hideTransientDust: true);

        // --- Final loop: player hears their creation; dust fades away; boost is free ---
        float finalLoopSec = (controller != null)
            ? Mathf.Max(1f, controller.GetEffectiveLoopLengthInSeconds())
            : Mathf.Max(1f, motifBridgeHoldSeconds);

        if (dustGenerator != null)
            dustGenerator.BeginSlowFadeAllDust(finalLoopSec);

        if (vehicles != null)
            foreach (var v in vehicles)
                v?.SetBoostFree(true);

        yield return new WaitForSeconds(finalLoopSec);

        if (vehicles != null)
            foreach (var v in vehicles)
                v?.SetBoostFree(false);
        // --- End final loop ---
        // Snapshot BEFORE StartNextMotifInPhase → BeginNewMotif clears the tracks.
        var allTracks = (controller?.tracks != null)
            ? controller.tracks.Where(t => t != null).ToList()
            : new List<InstrumentTrack>();
        var motifSnap = BuildPhaseSnapshotForBridge(allTracks, activeDrumTrack);

        _motifSnapshots.Add(motifSnap);
        ConstellationMemoryStore.StoreSnapshot(_motifSnapshots);

        Debug.Log($"[MOTIF-BRIDGE] Snapshot committed: notes={motifSnap.CollectedNotes.Count} " +
                  $"bins={motifSnap.TrackBins.Count} snapshots={_motifSnapshots.Count}");

        /*
        SetBridgeCinematicMode(true); // hide maze, dust, vehicles — coral will be the only thing visible




        // Derive bridge duration from the actual musical loop length — same pattern as PlayPhaseBridge.
        // This makes the coral grow animation span exactly one full loop replay.
        float motifBridgeSec = (controller != null)
            ? Mathf.Max(1f, controller.GetEffectiveLoopLengthInSeconds())
            : Mathf.Max(1f, motifBridgeHoldSeconds);

        Debug.Log($"[MOTIF-BRIDGE] Bridge duration: {motifBridgeSec:F2}s (motifBridgeHoldSeconds fallback={motifBridgeHoldSeconds})");

        // Show GlyphApplicator — instant 2D glyph held for bridge duration.
        if (motifGlyphApplicator != null)
        {
            motifGlyphApplicator.gameObject.SetActive(true);
            if (activeDrumTrack != null && activeDrumTrack.TryGetPlayAreaWorld(out var playArea))
            {
                motifGlyphApplicator.FitToPlayArea(
                    playArea.width, playArea.height,
                    (playArea.left + playArea.right) * 0.5f,
                    (playArea.bottom + playArea.top) * 0.5f);
            }
            motifGlyphApplicator.Apply(motifSnap);
            yield return new WaitForSeconds(motifBridgeSec);
            motifGlyphApplicator.Clear();
            motifGlyphApplicator.gameObject.SetActive(false);
        }
        else
        {
            Debug.LogWarning("[MOTIF-BRIDGE] No GlyphApplicator assigned — holding without visual.");
            yield return new WaitForSeconds(motifBridgeSec);
        }

        // Advance motif — StartNextMotifInPhase calls controller.BeginNewMotif which clears track state.

        SetBridgeCinematicMode(false); // restore maze, dust, vehicles
        
        */
        yield return StartCoroutine(StartNextMotifInPhase());
        GhostCycleInProgress = false;
        BridgePending = false;
    }

    private IEnumerator StartNextMotifInPhase()
    {
        // ============================================================
        // RESPONSIBILITY: motif-level reset + motif advance decision
        // ============================================================
        if (dustGenerator != null)
            dustGenerator.ResumeRegrowthAfterBridge();
        // 0) Hard reset only ONCE per motif boundary
        _motifStartTime = Time.time; // stamp before clearing tracks so coral height is correct
        // Full controller reset: clears persistentLoopNotes, _binNoteSets, burst state, step cursors,
        // calls DrumTrack.ResetBeatSequencingState, and internally calls noteViz.BeginNewMotif_ClearAll.
        // Previously only noteViz.BeginNewMotif_ClearAll was called here, leaving stale track state
        // that caused the ascension cohort to arm against old notes (stuck/unreleased note bug).
        controller?.BeginNewMotif("MotifBridge");

        if (phaseTransitionManager == null)
        {
            Debug.LogWarning("[GFM] No PhaseTransitionManager; cannot advance motif.");
            yield break;
        }

        // Ensure we're operating within the correct phase without reinitializing it.
//        phaseTransitionManager.EnsurePhaseLoaded("GFM/StartNextMotifInPhase");

        // 1) Try to advance within the phase
        var newMotif = phaseTransitionManager.AdvanceMotif("GFM/StartNextMotifInPhase");

        if (newMotif == null)
        {
            // Phase motifs exhausted (loopMotifs==false) -> advance to next phase
            phaseTransitionManager.AdvancePhase("GFM/StartNextMotifInPhase(Exhausted)");

            // World rebuild only (doHardReset=false; we've already reset for motif boundary)
            yield return StartCoroutine(StartNextPhaseMazeAndStar(doHardReset: false));
            yield break;
        }

        // Motif advanced within same chapter: rebuild world only (no extra reset)
        yield return StartCoroutine(StartNextPhaseMazeAndStar(doHardReset: false));
    }
    
    private MotifSnapshot BuildPhaseSnapshotForBridge(List<InstrumentTrack> retained, DrumTrack drum)
    {
        var snapshot = new MotifSnapshot { Timestamp = Time.time };

        snapshot.Pattern = phaseTransitionManager != null
            ? phaseTransitionManager.currentPhase
            : MazeArchetype.Windows;
        snapshot.Color = dustGenerator.MazeColor();

        // Coral animation timing: normalise step → bridge time using the live drum totalSteps.
        snapshot.TotalSteps = (drum != null && drum.totalSteps > 0) ? drum.totalSteps : 16;

        // Root-note highlight: used to give distinct visual treatment to key-root notes.
        snapshot.MotifKeyRootMidi = (phaseTransitionManager != null && phaseTransitionManager.currentMotif != null)
            ? phaseTransitionManager.currentMotif.keyRootMidi
            : 60;

        if (retained == null || retained.Count == 0) return snapshot;

        float motifStartTime = _motifStartTime > 0f ? _motifStartTime : snapshot.Timestamp;

        foreach (var track in retained)
        {
            if (!track) continue;

            var notes = track.GetPersistentLoopNotes();
            if (notes == null || notes.Count == 0) continue;

            Color c = QuantizeToColor32(ResolveTrackColor(track));
            int binSize = Mathf.Max(1, track.BinSize());

            // Build per-bin template: step → authored note (for full matched check).
            // A note is "matched" only if the player hit the correct beat AND the correct pitch.
            var templateNoteByBinStep = new Dictionary<int, Dictionary<int, int>>(); // binIndex → (localStep → note)
            for (int b = 0; b < track.maxLoopMultiplier; b++)
            {
                var ns = track.GetNoteSetForBin(b);
                if (ns?.persistentTemplate == null) continue;
                var stepNoteMap = new Dictionary<int, int>();
                foreach (var t in ns.persistentTemplate)
                    stepNoteMap[t.step] = t.note; // template steps are bin-local (0..binSize-1)
                templateNoteByBinStep[b] = stepNoteMap;
            }

            // Emit NoteEntries with BinIndex and IsMatched populated.
            // IsMatched = player hit the authored step AND played the authored note at that step.
            foreach (var n in notes.OrderBy(n => n.stepIndex))
            {
                int binIndex = n.stepIndex / binSize;
                int localStep = n.stepIndex % binSize;
                bool isMatched = templateNoteByBinStep.TryGetValue(binIndex, out var stepNoteMap)
                                 && stepNoteMap.TryGetValue(localStep, out int authoredNote)
                                 && n.note == authoredNote;

                snapshot.CollectedNotes.Add(new MotifSnapshot.NoteEntry(
                    step: n.stepIndex,
                    note: n.note,
                    velocity: n.velocity,
                    trackColor: c,
                    binIndex: binIndex,
                    isMatched: isMatched
                ));
            }

            // Emit TrackBinData entries — one per allocated bin on this track.
            int allocatedBins = Mathf.Max(1, track.loopMultiplier);
            for (int b = 0; b < allocatedBins; b++)
            {
                var binNotes = notes.Where(n => n.stepIndex / binSize == b).ToList();
                templateNoteByBinStep.TryGetValue(b, out var tplMap);

                int matched = 0;
                int unmatched = 0;
                var collectedSteps = new List<int>();
                foreach (var n in binNotes)
                {
                    int localStep = n.stepIndex % binSize;
                    collectedSteps.Add(localStep);
                    bool noteMatched = tplMap != null
                                       && tplMap.TryGetValue(localStep, out int authoredNote)
                                       && n.note == authoredNote;
                    if (noteMatched) matched++;
                    else unmatched++;
                }

                snapshot.TrackBins.Add(new MotifSnapshot.TrackBinData
                {
                    TrackColor = c,
                    Role = track.assignedRole,
                    BinIndex = b,
                    IsFilled = track.IsBinFilled(b),
                    CompletionTime = track.GetBinCompletionTime(b),
                    MotifStartTime = motifStartTime,
                    MatchedNoteCount = matched,
                    UnmatchedNoteCount = unmatched,
                    CollectedSteps = collectedSteps,
                });
            }
        }

        int matchedTotal = snapshot.CollectedNotes.Count(n => n.IsMatched);
        int unmatchedTotal = snapshot.CollectedNotes.Count - matchedTotal;
        Debug.Log($"[PHASE SNAPSHOT FINALIZE] phase={snapshot.Pattern} notes={snapshot.CollectedNotes.Count} " +
                  $"matched={matchedTotal} unmatched={unmatchedTotal} bins={snapshot.TrackBins.Count} " +
                  $"totalSteps={snapshot.TotalSteps} rootMidi={snapshot.MotifKeyRootMidi}");
        return snapshot;
    }

    /// <summary>
    /// Adapter to obtain the per-track color for snapshot entries.
    /// </summary>
    private Color ResolveTrackColor(InstrumentTrack t)
    {
        // Per your API: InstrumentTrack.trackColor
        return t != null ? t.trackColor : Color.white;
    }

    private void FreezeGameplayForBridge()
    {
        CleanupAllNoteTethers();

        // Clear any notes the player collected but hasn't released yet.
        // Stale _pendingNotes entries (collectable destroyed) block TickNoteTrail
        // from computing a non-zero pulse01, so the vehicle ring never highlights
        // in the new motif.
        if (vehicles != null)
            foreach (var v in vehicles)
                if (v != null) v.ClearPendingNotesForBridge();

        // Despawn collectables
        foreach (var c in FindObjectsOfType<Collectable>())
        {
            if (c != null) Destroy(c.gameObject);
        }

        // Despawn MineNodes (critical)
        foreach (var n in FindObjectsOfType<MineNode>())
        {
            if (n != null) Destroy(n.gameObject);
        }

        // Optional but recommended: remove all existing PhaseStars during the bridge
        foreach (var s in FindObjectsOfType<PhaseStar>())
        {
            if (s != null) Destroy(s.gameObject);
        }

        // Ensure DrumTrack doesn't keep an old reference
        if (activeDrumTrack != null)
        {
            activeDrumTrack.isPhaseStarActive = false;
        }
    }
}
