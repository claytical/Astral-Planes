using UnityEngine;

// =========================================================================
//  BinRingController
//
//  When a bin fills: spawn a flat circle ring, then immediately start the
//  deformation sequence. WaitAndLaunchDot handles per-note step-sync
//  internally, so deformation dots fire at the correct beat even if the
//  bridge fires before the bin's start step comes around.
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

        _ringApplicator?.ClearGameplayRings();

        if (_tracks != null)
            foreach (var t in _tracks)
                if (t != null)
                {
                    t.OnBinFilled += OnBinFilled;
                    t.OnCollectableBurstCleared += OnCollectableBurstCleared;
                }
    }

    void OnDestroy() => Teardown();

    private void Teardown()
    {
        _drumTrack = null;

        if (_tracks != null)
        {
            foreach (var t in _tracks)
                if (t != null)
                {
                    t.OnBinFilled -= OnBinFilled;
                    t.OnCollectableBurstCleared -= OnCollectableBurstCleared;
                }
            _tracks = null;
        }
    }

    // ── Public API ───────────────────────────────────────────────────────────

    public void CancelPendingDraw() { }

    // ── Event handlers ───────────────────────────────────────────────────────

    private void OnBinFilled(InstrumentTrack track, int binIndex)
    {
        if (_ringApplicator == null)
            _ringApplicator = GameFlowManager.Instance?.GetMotifRingGlyphApplicator();
        if (_ringApplicator == null) return;

        int totalSteps = (_drumTrack != null && _drumTrack.totalSteps > 0)
            ? _drumTrack.totalSteps : 16;

        var notes = track.GetBinNoteEntries(binIndex);

        _ringApplicator.SpawnBinRing(
            track.assignedRole, binIndex, track.DisplayColor,
            notes, totalSteps, track);

        _ringApplicator.BeginBinRingDeformation(
            binIndex, notes, totalSteps, track, track.assignedRole, track.DisplayColor);
    }

    // Fires once the last Collectable of a burst is resolved — placed (hadNotes)
    // or discarded/lost (!hadNotes) — synchronously after OnBinFilled when the burst
    // completed normally, so the remaining-progress rings animate in alongside the
    // completion ring rather than at spawn time.
    private void OnCollectableBurstCleared(InstrumentTrack track, int burstId, bool hadNotes)
    {
        if (_ringApplicator == null)
            _ringApplicator = GameFlowManager.Instance?.GetMotifRingGlyphApplicator();
        if (_ringApplicator == null) return;

        var motif    = GameFlowManager.Instance?.phaseTransitionManager?.currentMotif;
        int total    = Mathf.Max(1, motif?.nodesPerStar ?? 1);
        int captured = _drumTrack != null && _drumTrack._starPool != null
            ? _drumTrack._starPool.NodesCapturedThisMotif : 0;
        int remaining = Mathf.Max(0, total - captured);

        _ringApplicator.SetRemainingProgressRings(remaining);
    }
}
