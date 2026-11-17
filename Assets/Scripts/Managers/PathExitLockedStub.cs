using System.Collections.Generic;
using UnityEngine;

// Locked phase-exit dust gate (LEGIBILITY_AUDIT.md E2). In the path-choice interstitial, Up/Down
// (phase-change) exits only open once EVERY motif of the current phase has a recorded ring
// (PathChoiceCoordinator.AllMotifsRecordedInPhase). Until then the exit used to simply not exist.
// Now the dust wall on that edge, across the centre band where the road will open, shimmers:
// random solid cells along the gate flash toward white, and the shimmer gets denser, faster and
// brighter with each motif recorded in the phase (intensity = recorded / total). Purely in-world —
// no overlay, no collider; it only borrows the dust's own tint-pulse lane (CosmicDust.BeginColorFlash).
//
// (Class name kept from the first pass, which drew a bar + pips overlay instead.)
public sealed class PathExitLockedStub : MonoBehaviour
{
    private const float kIntensityFloor01   = 0.12f; // a gate with 0 recorded still reads faintly
    private const float kIntervalSlow       = 0.35f; // seconds between shimmer waves at low intensity
    private const float kIntervalFast       = 0.08f; // …and at full intensity
    private const float kMaxShareOfCells    = 0.3f;  // at full intensity, flash this share of gate cells per wave
    private const float kFlashSeconds       = 0.9f;
    private const float kWhitenMin          = 0.25f;
    private const float kWhitenMax          = 0.8f;

    private CosmicDustGenerator _dust;
    private List<Vector2Int> _cells;
    private float _intensity01;
    private Color _baseColor;
    private float _nextWave;

    public static PathExitLockedStub Create(CosmicDustGenerator dust, List<Vector2Int> gateCells, int recorded, int total, Color baseColor)
    {
        var go = new GameObject("PathExit_LockedGate");
        var gate = go.AddComponent<PathExitLockedStub>();
        gate._dust = dust;
        gate._cells = gateCells ?? new List<Vector2Int>();
        float frac = total > 0 ? Mathf.Clamp01(recorded / (float)total) : 0f;
        gate._intensity01 = Mathf.Lerp(kIntensityFloor01, 1f, frac);
        gate._baseColor = baseColor;
        return gate;
    }

    private void Update()
    {
        if (_dust == null || _cells == null || _cells.Count == 0) return;
        if (Time.time < _nextWave) return;
        _nextWave = Time.time + Mathf.Lerp(kIntervalSlow, kIntervalFast, _intensity01);

        int k = Mathf.Max(1, Mathf.RoundToInt(_cells.Count * kMaxShareOfCells * _intensity01));
        Color flash = Color.Lerp(_baseColor, Color.white, Mathf.Lerp(kWhitenMin, kWhitenMax, _intensity01));
        for (int i = 0; i < k; i++)
        {
            var cell = _cells[Random.Range(0, _cells.Count)];
            _dust.TryFlashSolidCell(cell, flash, kFlashSeconds);
        }
    }
}
