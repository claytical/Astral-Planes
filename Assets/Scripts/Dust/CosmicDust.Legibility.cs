using UnityEngine;

// Dust brightness = star readiness (LEGIBILITY_AUDIT.md A1/A2). One readable axis:
//   bright role color   -> bloomed (vehicle-carved regrow): PhaseStar tentacles can drain it
//   darkened role color -> a role is underneath but stars can't use it yet — carve it first.
//                          Covers both revealed-but-unbloomed role cells (zap/collectable/
//                          node-paint/void regrows) and None cells still hiding a Voronoi role
//                          (ForceGray regrows, neighbor-fallback regrows), via _hiddenHintColor.
//   plain gray          -> inert: None with no hidden role (generic filler, interstitial walls)
//
// Applied inside SetBaseTint so every recolor path (spawn, regrow, exhaust paint, void growth,
// diffusion, retint) inherits it. _restRgbRaw keeps the undarkened input so the rule can be
// re-evaluated when bloom/hint state changes, and so readers that feed a color back in
// (tint diffusion, RetintOver) work in raw space instead of compounding the darkening.
public partial class CosmicDust
{
    // Pushed from CosmicDustGeneratorConfig by CosmicDustGenerator.Start (hiddenRoleColorDarken01 /
    // hideDustColorAtSpawn) so the hidden-role preview and the unbloomed-role darkening match
    // the spawn-time ApplyHiddenColorPreview exactly.
    private static float s_latentDarken01 = 0.55f;
    private static bool s_showHiddenRoleHints = true;

    public static void SetLegibilityParams(float latentDarken01, bool showHiddenRoleHints)
    {
        s_latentDarken01 = Mathf.Clamp01(latentDarken01);
        s_showHiddenRoleHints = showHiddenRoleHints;
    }

    private Color _restRgbRaw = Color.white;

    /// <summary>Rest tint before the legibility darkening, with the live charge alpha. Use this
    /// (not CurrentTint) when blending a color and writing it back via SetTint.</summary>
    public Color RestTintRaw
    {
        get { var c = _restRgbRaw; c.a = _currentTint.a; return c; }
    }

    private Color ResolveLegibleRgb(Color raw)
    {
        if (IsPassageForm) return PassageGray(raw); // walled role's doorway: carvable, never star food

        if (Role != MusicalRole.None)
            return (HasBloomedFlower && !IsBlockForm) ? raw : DarkenLatent(raw); // blocks are never star food

        if (s_showHiddenRoleHints && _hiddenHintColor.a > 0f)
            return DarkenLatent(_hiddenHintColor);

        return raw;
    }

    private static Color DarkenLatent(Color c)
    {
        Color d = Color.Lerp(c, Color.black, s_latentDarken01);
        d.a = c.a;
        return d;
    }

    // Re-applies the rest tint after bloom/hint state changed. No-op if nothing visible changes.
    private void RefreshLegibleRestTint()
    {
        Color raw = _restRgbRaw;
        Color legible = ResolveLegibleRgb(raw);
        if (Mathf.Approximately(legible.r, _currentTint.r) &&
            Mathf.Approximately(legible.g, _currentTint.g) &&
            Mathf.Approximately(legible.b, _currentTint.b))
            return;

        // display:false — ApplyTintVisual below does the (single) particle rewrite.
        SetBaseTint(raw, applyImmediatelyIfNoPulse: false, display: false);
        if (!_tintPulseActive)
            ApplyTintVisual(_currentTint);
    }
}
