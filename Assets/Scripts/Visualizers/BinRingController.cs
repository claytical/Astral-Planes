using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// =========================================================================
//  BinRingController
//
//  Single-boundary lifecycle per bin fill:
//
//  OnBinFilled    → record which bin just filled; cancel any in-progress
//                   roll-off; clear visible rings.
//  Boundary       → spawn a ring only for the bin(s) that filled since the
//                   last spawn, then queue the roll-off.
//
//  Only the newly completed bin is ever shown — not the full history.
//  If a new bin fills while a roll-off is running, the roll-off is
//  cancelled, the current ring is cleared, and the new bin is queued.
//
//  At bridge time, CancelPendingDraw() discards the queue before the
//  full-motif record is shown via AnimateApply.
// =========================================================================
public class BinRingController : MonoBehaviour
{
    private DrumTrack                _drumTrack;
    private InstrumentTrack[]        _tracks;
    private MotifRingGlyphApplicator _ringApplicator;

    private readonly List<(InstrumentTrack track, int binIndex)> _pendingBins = new();
    private Coroutine _rollOffCoroutine;

    void Start()
    {
        GameFlowManager.Instance.RegisterBinRingController(this);
    }

    public void Setup(DrumTrack drumTrack, InstrumentTrack[] tracks)
    {
        Teardown();

        _drumTrack      = drumTrack;
        _tracks         = tracks;
        _ringApplicator = GameFlowManager.Instance?.GetMotifRingGlyphApplicator();

        if (_drumTrack != null)
            _drumTrack.OnLoopBoundary += OnLoopBoundary;

        if (_tracks != null)
            foreach (var t in _tracks)
                if (t != null) t.OnBinFilled += OnBinFilled;
    }

    void OnDestroy() => Teardown();

    private void Teardown()
    {
        _pendingBins.Clear();

        if (_rollOffCoroutine != null) { StopCoroutine(_rollOffCoroutine); _rollOffCoroutine = null; }

        if (_drumTrack != null)
        {
            _drumTrack.OnLoopBoundary -= OnLoopBoundary;
            _drumTrack = null;
        }
        if (_tracks != null)
        {
            foreach (var t in _tracks)
                if (t != null) t.OnBinFilled -= OnBinFilled;
            _tracks = null;
        }
    }

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Discard any queued draw and stop any running roll-off without clearing
    /// already-visible rings. Called by BridgeCoordinator before AnimateApply
    /// so a pending bin ring is never drawn after the full record takes over.
    /// </summary>
    public void CancelPendingDraw()
    {
        if (_rollOffCoroutine != null) { StopCoroutine(_rollOffCoroutine); _rollOffCoroutine = null; }
        _pendingBins.Clear();
    }

    // ── Event handlers ───────────────────────────────────────────────────────

    private void OnBinFilled(InstrumentTrack track, int binIndex)
    {
        if (_ringApplicator == null)
            _ringApplicator = GameFlowManager.Instance?.GetMotifRingGlyphApplicator();
        if (_ringApplicator == null) return;

        if (_rollOffCoroutine != null) { StopCoroutine(_rollOffCoroutine); _rollOffCoroutine = null; }
        _ringApplicator.ClearGameplayRings();
        _pendingBins.Add((track, binIndex));
    }

    private void OnLoopBoundary()
    {
        if (_ringApplicator == null) return;

        if (_pendingBins.Count > 0)
        {
            SpawnPendingBinRings();
            _rollOffCoroutine = StartCoroutine(_ringApplicator.RollOffGameplayRingsAfterBounce());
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void SpawnPendingBinRings()
    {
        if (_ringApplicator == null) return;

        int totalSteps = (_drumTrack != null && _drumTrack.totalSteps > 0)
            ? _drumTrack.totalSteps : 16;

        _ringApplicator.ClearGameplayRings();

        foreach (var (t, b) in _pendingBins)
        {
            if (t == null) continue;
            if (!t.IsBinFilled(b)) continue;
            _ringApplicator.SpawnBinRing(
                t.assignedRole, b, t.trackColor,
                t.GetBinNoteEntries(b), totalSteps, t);
        }

        _pendingBins.Clear();
    }
}
