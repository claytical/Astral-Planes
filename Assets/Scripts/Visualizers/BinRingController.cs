using UnityEngine;

// =========================================================================
//  BinRingController
//
//  MonoBehaviour that spawns a single ring at the overlay whenever a bin
//  is completed during gameplay. Rings persist until the next drum loop
//  boundary, then fade out. At bridge time (BridgeCoordinator), gameplay
//  rings are cleared instantly before the full-motif record is shown.
//
//  Setup: place in scene, add to GameFlowManager inspector slot or let
//  it self-register. GameFlowManager calls Setup() after the drum track
//  starts (step 3 of HandleTrackSceneSetupAsync).
// =========================================================================
public class BinRingController : MonoBehaviour
{
    private DrumTrack _drumTrack;
    private InstrumentTrack[] _tracks;
    private MotifRingGlyphApplicator _ringApplicator;

    void Start()
    {
        GameFlowManager.Instance.RegisterBinRingController(this);
    }

    /// <summary>
    /// Called by GameFlowManager after the drum track and instrument tracks are ready.
    /// </summary>
    public void Setup(DrumTrack drumTrack, InstrumentTrack[] tracks)
    {
        Teardown();

        _drumTrack     = drumTrack;
        _tracks        = tracks;
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

    // ── Event handlers ───────────────────────────────────────────────────────

    private void OnBinFilled(InstrumentTrack track, int binIndex)
    {
        if (_ringApplicator == null)
            _ringApplicator = GameFlowManager.Instance?.GetMotifRingGlyphApplicator();
        if (_ringApplicator == null) return;

        int totalSteps = (_drumTrack != null && _drumTrack.totalSteps > 0)
            ? _drumTrack.totalSteps
            : 16;

        _ringApplicator.SpawnBinRing(
            track.assignedRole,
            binIndex,
            track.trackColor,
            track.GetBinNoteEntries(binIndex),
            totalSteps);
    }

    private void OnLoopBoundary()
    {
        if (_ringApplicator == null) return;
        var applicator = _ringApplicator;
        var config     = applicator.config;
        float duration = config != null ? config.fadeOutDuration : 0.5f;
        StartCoroutine(applicator.FadeAndClearGameplayRings(duration));
    }
}
