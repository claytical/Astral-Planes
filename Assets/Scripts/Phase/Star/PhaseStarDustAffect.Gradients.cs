using UnityEngine;

public partial class PhaseStarDustAffect
{
    // Scratch key arrays: Gradient.SetKeys copies them, so they are reused across tentacles
    // and frames (the drain pulse rebuilds its gradient every frame).
    private static readonly GradientColorKey[] s_colorKeys3 = new GradientColorKey[3];
    private static readonly GradientAlphaKey[] s_alphaKeys5 = new GradientAlphaKey[5];
    private static readonly GradientColorKey[] s_colorKeys5 = new GradientColorKey[5];
    private static readonly GradientAlphaKey[] s_alphaKeys6 = new GradientAlphaKey[6];

    private void BuildGrowingGradient(Tentacle tentacle, Color roleColor)
    {
        float alpha = tentacle.alphaScale;

        // Growing-state keys only depend on roleColor/alphaScale, which are stable for
        // the whole growth animation (unlike the drain-pulse gradient, which is
        // deliberately re-derived from Time.time every call) — skip the SetKeys
        // allocation when nothing here has actually changed.
        if (roleColor == tentacle.lastGrowingGradientRoleColor && alpha == tentacle.lastGrowingGradientAlphaScale)
            return;
        tentacle.lastGrowingGradientRoleColor = roleColor;
        tentacle.lastGrowingGradientAlphaScale = alpha;

        var ck = s_colorKeys3;
        ck[0] = new GradientColorKey(roleColor * 0.6f, 0f);
        ck[1] = new GradientColorKey(Color.white,      0.5f);
        ck[2] = new GradientColorKey(roleColor,        1f);
        var ak = s_alphaKeys5;
        ak[0] = new GradientAlphaKey(growingRootAlpha * alpha * 0.1f, 0f);
        ak[1] = new GradientAlphaKey(growingRootAlpha * alpha,        0.12f);
        ak[2] = new GradientAlphaKey(drainingBaseAlpha * alpha,       0.5f);
        ak[3] = new GradientAlphaKey(growingTipAlpha * alpha * 0.7f,  0.88f);
        ak[4] = new GradientAlphaKey(0f,                              1f);
        tentacle.gradient.SetKeys(ck, ak);
    }

    private void BuildGradient(Tentacle tentacle, Color roleColor, float dustCharge01 = 1f)
    {
        float alpha = tentacle.alphaScale;

        if (tentacle.state == TentacleState.Draining)
        {
            float colorFill  = Mathf.Clamp01(tentacle.contactTimer / Mathf.Max(0.001f, colorTransitionTime));
            float colorFront = 1f - colorFill;

            if (colorFill < 0.999f)
            {
                float edge0 = Mathf.Max(0f, colorFront - 0.08f);
                float edge1 = Mathf.Min(1f, colorFront + 0.08f);
                Color dustTipColor = Color.Lerp(Color.gray, roleColor, dustCharge01);

                var ck = s_colorKeys5;
                ck[0] = new GradientColorKey(roleColor * 0.6f, 0f);
                ck[1] = new GradientColorKey(Color.white,      edge0);
                ck[2] = new GradientColorKey(roleColor,        edge1);
                ck[3] = new GradientColorKey(dustTipColor,     0.92f);
                ck[4] = new GradientColorKey(dustTipColor,     1f);
                var ak = s_alphaKeys5;
                ak[0] = new GradientAlphaKey(0.1f * alpha,                0f);
                ak[1] = new GradientAlphaKey(drainingBaseAlpha * alpha,   edge0);
                ak[2] = new GradientAlphaKey(0.85f * alpha,               edge1);
                ak[3] = new GradientAlphaKey(dustCharge01 * 0.5f * alpha, 0.92f);
                ak[4] = new GradientAlphaKey(0f,                          1f);
                tentacle.gradient.SetKeys(ck, ak);
                return;
            }

            BuildDrainPulseGradient(tentacle, roleColor, alpha, dustCharge01);
            return;
        }

        if (tentacle.state == TentacleState.Dissolving)
        {
            BuildDrainPulseGradient(tentacle, roleColor, alpha, dustCharge01);
            return;
        }

        BuildGrowingGradient(tentacle, roleColor);
    }

    private void BuildDrainPulseGradient(Tentacle tentacle, Color roleColor, float alpha, float dustCharge01 = 1f)
    {
        // While a siphon packet is in flight, it replaces the ambient flow pulse:
        // a brighter, wider packet whose position is driven by siphonT (tip -> root)
        // instead of the looping flow phase. Same key structure, no extra keys.
        bool siphon = tentacle.siphonActive;

        float pulse;
        float halfWidth;
        Color bright;
        if (siphon)
        {
            pulse     = Mathf.Clamp01(tentacle.siphonT);
            halfWidth = 0.14f;
            bright    = Color.Lerp(roleColor, Color.white, siphonBrightness);
        }
        else
        {
            float rawT = (Time.time * tentacleFlowSpeed + tentacle.flowOffset) % 1f;
            pulse      = 1f - rawT;
            halfWidth  = 0.1f;
            float flashBoost = tentacle.drainFlashTimer > 0f ? 0.5f : 0.4f;
            bright     = Color.Lerp(roleColor, Color.white, flashBoost);
        }

        float p0 = Mathf.Clamp01(pulse - halfWidth);
        float p1 = pulse;
        float p2 = Mathf.Clamp01(pulse + halfWidth);

        float pulsePeakAlpha = siphon ? 1.0f : 1.0f * alpha;

        Color dustTipColor = Color.Lerp(Color.gray, roleColor, dustCharge01);
        float tipAlpha     = dustCharge01 * 0.6f * alpha;

        var ck = s_colorKeys5;
        ck[0] = new GradientColorKey(roleColor,    0f);
        ck[1] = new GradientColorKey(bright,       p1);
        ck[2] = new GradientColorKey(roleColor,    p2);
        ck[3] = new GradientColorKey(dustTipColor, 0.92f);
        ck[4] = new GradientColorKey(dustTipColor, 1f);
        var ak = s_alphaKeys6;
        ak[0] = new GradientAlphaKey(drainingBaseAlpha * alpha,        0f);
        ak[1] = new GradientAlphaKey(drainingBaseAlpha * 0.5f * alpha, p0);
        ak[2] = new GradientAlphaKey(pulsePeakAlpha,                   p1);
        ak[3] = new GradientAlphaKey(drainingBaseAlpha * 0.5f * alpha, p2);
        ak[4] = new GradientAlphaKey(tipAlpha,                         0.92f);
        ak[5] = new GradientAlphaKey(0f,                               1f);
        tentacle.gradient.SetKeys(ck, ak);
    }

    private Color GetRoleColor(MusicalRole role)
    {
        if (_star != null) return _star.GetResolvedRoleColor(role);

        var rp = MusicalRoleProfileLibrary.GetProfile(role);
        return rp != null
            ? new Color(rp.dustColors.baseColor.r, rp.dustColors.baseColor.g, rp.dustColors.baseColor.b, 1f)
            : Color.white;
    }
}
