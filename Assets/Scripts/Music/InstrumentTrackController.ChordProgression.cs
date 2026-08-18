using System.Collections;
using UnityEngine;
using Object = UnityEngine.Object;

// Chord-advance triggers: "all tracks maxed" (SuperNode completion, alt-chord staging) and
// per-cohort ascension completion, both of which end in HarmonyDirector advancing the chord.
public partial class InstrumentTrackController
{
    // Counts SuperNodeCompletionCoroutine instances currently mid-flight. While any are
    // pending, CheckAndTriggerAllTracksMaxed must not fire — otherwise a synchronous caller
    // (e.g. the SuperNode.OnResolved targetTrack mirror in PhaseStar.EjectionLogic.cs, which
    // fires the instant a track is already maxed) can race ahead of the coroutine's own
    // staged two-loop-boundary cleanup/ascend visual and trigger the motif bridge before that
    // choreography has had a chance to run.
    private int _pendingSuperNodeCompletions;

    // Shared precondition for both AllTracksMaxed paths below: resolves GameFlowManager
    // and the motif's alternate chord profile, and confirms every track whose role is
    // active in the current motif has reached its max loopMultiplier.
    private bool TryGetAllTracksMaxedAltProfile(out GameFlowManager gfm, out ChordProgressionProfile alt)
    {
        gfm = null;
        alt = null;
        if (tracks == null) return false;

        gfm = GameFlowManager.Instance;
        if (gfm == null) return false;

        var motif = gfm.phaseTransitionManager?.currentMotif;
        var candidateAlt = motif?.alternateChordProgressionProfile;
        if (candidateAlt == null) return false;

        var activeRoles = motif.GetActiveRoles();
        if (activeRoles == null || activeRoles.Count == 0) return false;

        // Only check tracks whose role is active in the current motif.
        foreach (var t in tracks)
        {
            if (t == null) continue;
            if (!activeRoles.Contains(t.assignedRole)) continue;
            if (t.loopMultiplier < t.maxLoopMultiplier) return false;
        }

        alt = candidateAlt;
        return true;
    }

    // Applies the alternate chord progression once every active-role track is maxed. Does NOT
    // end the motif — SuperNode capture always shifts the chord, but the motif only ends when
    // StarPool's nodesPerStar harvest budget is satisfied, either through normal play or a full
    // completionist shard sweep (see NotifySuperNodeResolved / TryCompleteFullSweepHarvestIfReady).
    public void CheckAndTriggerAllTracksMaxed()
    {
        if (_pendingSuperNodeCompletions > 0) return;
        if (!TryGetAllTracksMaxedAltProfile(out var gfm, out var alt)) return;
        if (gfm.GhostCycleInProgress || gfm.BridgePending) return;

        gfm.harmony?.SetActiveProfile(alt, applyImmediately: true);
    }

    // Sticky flag set when a SuperNode resolves with every shard caught. Consumed once
    // TryCompleteFullSweepHarvestIfReady finds no pending ascend-visual coroutines and all
    // active-role tracks maxed — i.e. a true completionist sweep, not just a partial catch.
    private bool _pendingFullSweepHarvestCompletion;

    public void NotifySuperNodeResolved(bool allShardsCaught)
    {
        if (allShardsCaught) _pendingFullSweepHarvestCompletion = true;
        TryCompleteFullSweepHarvestIfReady();
    }

    private void TryCompleteFullSweepHarvestIfReady()
    {
        if (!_pendingFullSweepHarvestCompletion) return;
        if (_pendingSuperNodeCompletions > 0) return;
        if (!TryGetAllTracksMaxedAltProfile(out var gfm, out _)) return;

        _pendingFullSweepHarvestCompletion = false;
        gfm?.activeDrumTrack?._starPool?.CompleteMotifHarvestBudget("SuperNodeFullSweep");
    }

    public void StageAltChordIfAllTracksMaxed()
    {
        if (!TryGetAllTracksMaxedAltProfile(out var gfm, out var alt)) return;
        gfm.harmony?.SetActiveProfile(alt, applyImmediately: false);
    }

    public void StartSuperNodeCompletionSequence(InstrumentTrack track, int fromBin, int toBin,
                                                   int ascendLoopsOverride, int binSz, DrumTrack drum)
    {
        if (track == null || fromBin >= toBin) return;
        _pendingSuperNodeCompletions++;
        StartCoroutine(SuperNodeCompletionCoroutine(track, fromBin, toBin, ascendLoopsOverride, binSz, drum));
    }

    private IEnumerator SuperNodeCompletionCoroutine(InstrumentTrack track, int fromBin, int toBin,
                                                      int ascendLoopsOverride, int binSz, DrumTrack drum)
    {
        yield return WaitForNextLoopBoundary(drum);

        // N+1 boundary: visual cleanup loop — explode remaining gameplay objects and fade dust
        // before the record appears next loop.
        var gfm = GameFlowManager.Instance;
        if (gfm != null)
        {
            foreach (var n in Object.FindObjectsByType<DiscoveryTrackNode>(FindObjectsSortMode.None))
            {
                if (n == null) continue;
                var ex = n.GetComponent<Explode>();
                if (ex != null) ex.Permanent();
                else Object.Destroy(n.gameObject);
            }
            gfm.activeDrumTrack?._starPool?.ExplodeAndClearAll();
            gfm.dustGenerator?.HardStopRegrowthForBridge(hideTransientDust: true);
            gfm.dustGenerator?.BeginSlowFadeAllDust(Mathf.Max(1f, GetEffectiveLoopLengthInSeconds()));
        }

        noteVisualizer?.TriggerStepRangeAscend(track, fromBin * binSz, toBin * binSz, ascendLoopsOverride);

        yield return WaitForNextLoopBoundary(drum);

        // N+2 boundary: record appears with alt chord.
        _pendingSuperNodeCompletions = Mathf.Max(0, _pendingSuperNodeCompletions - 1);
        CheckAndTriggerAllTracksMaxed();
        TryCompleteFullSweepHarvestIfReady();
    }

    private IEnumerator WaitForNextLoopBoundary(DrumTrack drum)
    {
        if (drum == null) yield break;
        bool fired = false;
        System.Action onBoundary = () => fired = true;
        drum.OnLoopBoundary += onBoundary;
        yield return new WaitUntil(() => fired);
        drum.OnLoopBoundary -= onBoundary;
    }

    private void HandleAscensionCohortCompleted(InstrumentTrack track, int start, int end)
    {
        if (_gfm == null) _gfm = GameFlowManager.Instance;
        var h = _gfm ? _gfm.harmony : null;
        if (h == null) { Debug.LogWarning("[CHORD][CTRLR] HarmonyDirector is NULL"); return; }

        // This is your “tick”: the armed cohort finished ascending on 'track'
        // 1) Optionally: small flourish / feedback hook could go here

        // 2) Ask HarmonyDirector to advance one chord and retune everyone
        _gfm?.harmony?.AdvanceChordAndRetuneAll(1);
    }
}
