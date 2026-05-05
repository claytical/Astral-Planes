using System.Collections;
using UnityEngine;

// =========================================================================
//  BinRingController
//
//  Single-boundary lifecycle per bin fill:
//
//  OnBinFilled    → queue a draw; cancel any in-progress roll-off.
//  Boundary 1     → draw all completed-bin rings with the normal draw-in,
//                   then immediately queue the roll-off to start once the
//                   bounce animation finishes (~ringDrawInDuration +
//                   bouncePressDuration + bounceSettleDuration).
//
//  If a new bin fills while a roll-off is running, the roll-off is
//  cancelled, rings are cleared, and the cycle restarts from the next
//  boundary.
//
//  At bridge time, ClearGameplayRings() removes them instantly before the
//  full-motif record is shown via AnimateApply.
// =========================================================================
public class BinRingController : MonoBehaviour
{
    private DrumTrack                _drumTrack;
    private InstrumentTrack[]        _tracks;
    private MotifRingGlyphApplicator _ringApplicator;

    private bool      _pendingDraw;
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
    /// Cancel any queued draw and running roll-off without clearing already-visible rings.
    /// Called by BridgeCoordinator before AnimateApply so the bin-completion ring is never
    /// drawn after the full record takes over.
    /// </summary>
    public void CancelPendingDraw()
    {
        if (_rollOffCoroutine != null) { StopCoroutine(_rollOffCoroutine); _rollOffCoroutine = null; }
        _pendingDraw = false;
    }

    // ── Event handlers ───────────────────────────────────────────────────────

    private void OnBinFilled(InstrumentTrack track, int binIndex)
    {
        if (_ringApplicator == null)
            _ringApplicator = GameFlowManager.Instance?.GetMotifRingGlyphApplicator();
        if (_ringApplicator == null) return;

        // Cancel any running roll-off and clear rings; redraw at the next boundary.
        if (_rollOffCoroutine != null) { StopCoroutine(_rollOffCoroutine); _rollOffCoroutine = null; }
        _ringApplicator.ClearGameplayRings();
        _pendingDraw = true;
    }

    private void OnLoopBoundary()
    {
        if (_ringApplicator == null) return;

        if (_pendingDraw)
        {
            SpawnAllBinRings();
            _pendingDraw      = false;
            _rollOffCoroutine = StartCoroutine(_ringApplicator.RollOffGameplayRingsAfterBounce());
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void SpawnAllBinRings()
    {
        if (_ringApplicator == null || _tracks == null) return;

        int totalSteps = (_drumTrack != null && _drumTrack.totalSteps > 0)
            ? _drumTrack.totalSteps : 16;

        _ringApplicator.ClearGameplayRings();

        foreach (var t in _tracks)
        {
            if (t == null) continue;
            int allocatedBins = Mathf.Max(1, t.loopMultiplier);
            for (int b = 0; b < allocatedBins; b++)
            {
                if (!t.IsBinFilled(b)) continue;
                _ringApplicator.SpawnBinRing(
                    t.assignedRole, b, t.trackColor,
                    t.GetBinNoteEntries(b), totalSteps);
            }
        }
    }
}
