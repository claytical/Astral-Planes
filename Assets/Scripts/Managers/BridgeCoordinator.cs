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

    public IEnumerator PlayMotifBridgeAndRestart()
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

        float remainingInLoop = effectiveLoopSec;
        if (_gameFlow.activeDrumTrack != null && _gameFlow.activeDrumTrack.leaderStartDspTime > 0)
        {
            float elapsed = (float)(AudioSettings.dspTime - _gameFlow.activeDrumTrack.leaderStartDspTime);
            remainingInLoop = Mathf.Max(0.1f, effectiveLoopSec - Mathf.Clamp(elapsed, 0f, effectiveLoopSec));
        }

        _motifSnapshots.Add(motifSnap);
        ConstellationMemoryStore.StoreSnapshot(_motifSnapshots);
        _gameFlow.GetMotifRingGlyphApplicator()?.AnimateApply(motifSnap);

        if (_gameFlow.dustGenerator != null)
            _gameFlow.dustGenerator.HardStopRegrowthForBridge(hideTransientDust: true);

        if (_gameFlow.dustGenerator != null)
            _gameFlow.dustGenerator.BeginSlowFadeAllDust(remainingInLoop);

        foreach (var v in _gameFlow.GetVehicles())
            v?.SetBoostFree(true);

        yield return new WaitForSeconds(remainingInLoop * 2);

        foreach (var v in _gameFlow.GetVehicles())
            v?.SetBoostFree(false);

        var rings = _gameFlow.GetMotifRingGlyphApplicator();
        if (rings != null)
            _gameFlow.StartCoroutine(rings.FadeOutAndClear(rings.config?.fadeOutDuration ?? 0.75f));

        yield return _gameFlow.StartCoroutine(_gameFlow.SceneFlow.StartNextMotifInPhase());
        _session.SetGhostCycleInProgress(false);
        _session.SetBridgePending(false);
    }

    private MotifSnapshot BuildPhaseSnapshotForBridge(List<InstrumentTrack> retained, DrumTrack drum)
    {
        var snapshot = new MotifSnapshot { Timestamp = Time.time };

        snapshot.Pattern = _gameFlow.phaseTransitionManager != null
            ? _gameFlow.phaseTransitionManager.currentPhase
            : MazeArchetype.Windows;
        snapshot.Color = _gameFlow.dustGenerator.MazeColor();
        snapshot.TotalSteps = (drum != null && drum.totalSteps > 0) ? drum.totalSteps : 16;
        snapshot.MotifKeyRootMidi = (_gameFlow.phaseTransitionManager != null && _gameFlow.phaseTransitionManager.currentMotif != null)
            ? _gameFlow.phaseTransitionManager.currentMotif.keyRootMidi
            : 60;

        if (retained == null || retained.Count == 0) return snapshot;

        float motifStartTime = _motifStartTime > 0f ? _motifStartTime : snapshot.Timestamp;

        foreach (var track in retained)
        {
            if (!track) continue;
            var notes = track.GetPersistentLoopNotes();
            if (notes == null || notes.Count == 0) continue;

            Color c = _gameFlow.QuantizeToColor32(track.trackColor);
            int binSize = Mathf.Max(1, track.BinSize());

            foreach (var n in notes.OrderBy(n => n.stepIndex))
            {
                int binIndex = n.stepIndex / binSize;
                int localStep = n.stepIndex % binSize;
                float commitTime01 = (binSize > 1) ? localStep / (float)(binSize - 1) : 0.5f;

                snapshot.CollectedNotes.Add(new MotifSnapshot.NoteEntry(
                    step: n.stepIndex,
                    note: n.note,
                    velocity: n.velocity,
                    trackColor: c,
                    binIndex: binIndex,
                    isMatched: true,
                    commitTime01: commitTime01));
            }

            int allocatedBins = Mathf.Max(1, track.loopMultiplier);
            for (int b = 0; b < allocatedBins; b++)
            {
                snapshot.TrackBins.Add(new MotifSnapshot.TrackBinData
                {
                    TrackColor = c,
                    Role = track.assignedRole,
                    BinIndex = b,
                    IsFilled = track.IsBinFilled(b),
                    CompletionTime = track.GetBinCompletionTime(b),
                    MotifStartTime = motifStartTime,
                });
            }
        }

        return snapshot;
    }

    private void FreezeGameplayForBridge()
    {
        _gameFlow.CleanupAllNoteTethers();

        foreach (var v in _gameFlow.GetVehicles())
            v?.ClearPendingNotesForBridge();

        foreach (var c in Object.FindObjectsOfType<Collectable>())
            if (c != null) Object.Destroy(c.gameObject);

        foreach (var n in Object.FindObjectsOfType<MineNode>())
            if (n != null) Object.Destroy(n.gameObject);

        if (_gameFlow.activeDrumTrack?._starPool != null)
        {
            _gameFlow.activeDrumTrack._starPool.DespawnAll();
            Object.Destroy(_gameFlow.activeDrumTrack._starPool.gameObject);
            _gameFlow.activeDrumTrack._starPool = null;
        }
    }

    public void StampMotifStartTime() => _motifStartTime = Time.time;
}
