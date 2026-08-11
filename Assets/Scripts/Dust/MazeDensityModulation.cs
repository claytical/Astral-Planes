using UnityEngine;

/// <summary>
/// Scales each MazePatternConfig knob around its authored value by MazeTopologyService.Context.
/// Busyness01 (motif-wide note density, see MotifNoteDensityAnalyzer). Every formula collapses
/// to the authored value unchanged at busyness01=0.5 (Context's default), so a caller that never
/// sets Busyness01 gets byte-identical behavior to before this system existed. Never mutates the
/// MazePatternConfig instance passed in — config assets are shared across multiple motifs.
/// </summary>
public static class MazeDensityModulation
{
    // Multiplicative (not additive) so a sparse motif can genuinely escape an authored baseline
    // that's already extreme (e.g. 0.829) instead of only ever nudging by a flat +/-0.15 — see
    // the "CellularAutomata's density lever was too weak" plan addendum.
    public static float CellularAutomataFillChance(float authored, float busyness01) =>
        Mathf.Clamp01(authored * Mathf.Lerp(0.3f, 1.7f, busyness01));

    // BuildCA's survive-on-[2,4]/born-on-3 rule is homeostatic: an initial fill above ~0.66
    // (hex grid, 6 neighbors/cell) puts average neighbor count above the survive-4 ceiling, so
    // the first iteration culls it back toward the rule's natural stable density regardless of
    // exactly how high the seed was. Iterations is a second, independent lever — fewer iterations
    // means the seed noise never gets the chance to homeostatically re-densify.
    public static int CellularAutomataIterations(int authored, float busyness01) =>
        Mathf.Clamp(Mathf.RoundToInt(authored * Mathf.Lerp(0.4f, 1.6f, busyness01)), 1, 10);

    public static int RingSpacing(int authored, float busyness01) =>
        Mathf.Max(1, Mathf.RoundToInt(authored * Mathf.Lerp(1.3f, 0.7f, busyness01)));

    // Clamped strictly below the (already-modulated) spacing so rings can never merge into full solid.
    public static int RingThickness(int modulatedSpacing, int authoredThickness, float busyness01) =>
        Mathf.Min(
            Mathf.Max(1, Mathf.RoundToInt(authoredThickness * Mathf.Lerp(0.8f, 1.3f, busyness01))),
            Mathf.Max(1, modulatedSpacing - 1));

    public static int DrunkenStrokesCount(int authored, float busyness01) =>
        Mathf.Max(1, Mathf.RoundToInt(authored * Mathf.Lerp(0.7f, 1.3f, busyness01)));

    // Same multiplicative fix as CellularAutomataFillChance — the additive form left this stuck
    // near the authored baseline for any motif whose dilate value isn't already close to 0.5.
    public static float DrunkenStrokesDilate(float authored, float busyness01) =>
        Mathf.Clamp01(authored * Mathf.Lerp(0.3f, 1.7f, busyness01));

    public static int DiagonalLanesStep(int authored, float busyness01) =>
        Mathf.Max(2, Mathf.RoundToInt(authored * Mathf.Lerp(1.3f, 0.7f, busyness01)));

    public static int TunnelsCorridorWidth(int authored, float busyness01) =>
        Mathf.Max(1, Mathf.RoundToInt(authored * Mathf.Lerp(1.15f, 0.75f, busyness01)));

    // Mirrors TunnelsParams.Validate()'s own corridorWidth<=corridorStep invariant, applied to
    // locals only — the authored asset's fields are never written.
    public static int TunnelsCorridorStep(int authoredStep, int modulatedWidth) =>
        modulatedWidth > authoredStep ? modulatedWidth : authoredStep;

    // BuildClearBoxes' boxes are holes punched into an otherwise-solid field, so busier motifs
    // get FEWER/SMALLER boxes (less open space), inverted from every other pattern's direction.
    public static int ClearBoxesCount(int authored, float busyness01) =>
        Mathf.Max(1, Mathf.RoundToInt(authored * Mathf.Lerp(1.3f, 0.7f, busyness01)));

    public static int ClearBoxesWidth(int authored, float busyness01) =>
        Mathf.Max(1, Mathf.RoundToInt(authored * Mathf.Lerp(1.25f, 0.8f, busyness01)));

    public static int ClearBoxesHeight(int authored, float busyness01) =>
        Mathf.Max(1, Mathf.RoundToInt(authored * Mathf.Lerp(1.25f, 0.8f, busyness01)));
}
