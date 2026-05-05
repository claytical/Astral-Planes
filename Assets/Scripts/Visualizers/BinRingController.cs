using UnityEngine;

// =========================================================================
//  BinRingController
//
//  On each bin completion: clears any existing gameplay rings and re-spawns
//  one ring for every bin completed so far across all tracks. This means
//  completing bin 2 shows rings for bin 1 AND bin 2 together.
//
//  At the loop boundary all gameplay rings fade out. The next bin
//  completion will re-show the full cumulative set again.
//
//  At bridge time, ClearGameplayRings() removes them instantly before the
//  full-motif record is shown via AnimateApply.
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
        if (_ringApplicator == null || _tracks == null) return;

        int totalSteps = (_drumTrack != null && _drumTrack.totalSteps > 0)
            ? _drumTrack.totalSteps : 16;

        // Clear any rings currently visible (including a running fade) and
        // re-spawn one ring per completed bin across all tracks.
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

    private void OnLoopBoundary()
    {
        if (_ringApplicator == null) return;
        float duration = _ringApplicator.config != null
            ? _ringApplicator.config.fadeOutDuration : 0.5f;
        StartCoroutine(_ringApplicator.FadeAndClearGameplayRings(duration));
    }
}
