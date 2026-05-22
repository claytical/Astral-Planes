using UnityEngine;

// =========================================================================
//  BinRingController
//
//  Spawns a ring immediately when a bin fills — no loop-boundary delay.
//  Each ring manages its own full lifecycle (appear → rotate → press →
//  spin-off) inside AnimateNoteReveal on the applicator.
//
//  At bridge time, CancelPendingDraw() is a no-op; AnimateApply on the
//  applicator calls StopAllCoroutines + ClearGameplayRings itself.
// =========================================================================
public class BinRingController : MonoBehaviour
{
    private DrumTrack                _drumTrack;
    private InstrumentTrack[]        _tracks;
    private MotifRingGlyphApplicator _ringApplicator;

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

        if (_tracks != null)
            foreach (var t in _tracks)
                if (t != null) t.OnBinFilled += OnBinFilled;
    }

    void OnDestroy() => Teardown();

    private void Teardown()
    {
        if (_drumTrack != null)
            _drumTrack = null;

        if (_tracks != null)
        {
            foreach (var t in _tracks)
                if (t != null) t.OnBinFilled -= OnBinFilled;
            _tracks = null;
        }
    }

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Called by BridgeCoordinator before AnimateApply. Rings self-manage;
    /// AnimateApply handles cleanup via StopAllCoroutines + ClearGameplayRings.
    /// </summary>
    public void CancelPendingDraw() { }

    // ── Event handlers ───────────────────────────────────────────────────────

    private void OnBinFilled(InstrumentTrack track, int binIndex)
    {
        if (_ringApplicator == null)
            _ringApplicator = GameFlowManager.Instance?.GetMotifRingGlyphApplicator();
        if (_ringApplicator == null) return;
        if (!track.IsBinFilled(binIndex)) return;

        // Clear the previous bin's ring immediately so only the current bin is shown.
        // Old ring coroutines exit via the ring.Root == null guard in their next frame.
        _ringApplicator.ClearGameplayRings();

        int totalSteps = (_drumTrack != null && _drumTrack.totalSteps > 0)
            ? _drumTrack.totalSteps : 16;

        _ringApplicator.SpawnBinRing(
            track.assignedRole, binIndex, track.trackColor,
            track.GetBinNoteEntries(binIndex), totalSteps, track);
    }
}
