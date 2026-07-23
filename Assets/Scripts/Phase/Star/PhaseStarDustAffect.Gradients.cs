using UnityEngine;

public partial class PhaseStarDustAffect
{
    private void BuildGrowingGradient(Tentacle tentacle, Color roleColor)
    {
        float alpha = tentacle.alphaScale;

        tentacle.gradient.SetKeys(
            new[]
            {
                new GradientColorKey(roleColor * 0.6f, 0f),
                new GradientColorKey(Color.white,      0.5f),
                new GradientColorKey(roleColor,        1f),
            },
            new[]
            {
                new GradientAlphaKey(growingRootAlpha * alpha * 0.1f, 0f),
                new GradientAlphaKey(growingRootAlpha * alpha,        0.12f),
                new GradientAlphaKey(drainingBaseAlpha * alpha,       0.5f),
                new GradientAlphaKey(growingTipAlpha * alpha * 0.7f,  0.88f),
                new GradientAlphaKey(0f,                              1f),
            }
        );
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

                tentacle.gradient.SetKeys(
                    new[]
                    {
                        new GradientColorKey(roleColor * 0.6f, 0f),
                        new GradientColorKey(Color.white,      edge0),
                        new GradientColorKey(roleColor,        edge1),
                        new GradientColorKey(dustTipColor,     0.92f),
                        new GradientColorKey(dustTipColor,     1f),
                    },
                    new[]
                    {
                        new GradientAlphaKey(0.1f * alpha,                    0f),
                        new GradientAlphaKey(drainingBaseAlpha * alpha,       edge0),
                        new GradientAlphaKey(0.85f * alpha,                   edge1),
                        new GradientAlphaKey(dustCharge01 * 0.5f * alpha,     0.92f),
                        new GradientAlphaKey(0f,                              1f),
                    }
                );
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

        tentacle.gradient.SetKeys(
            new[]
            {
                new GradientColorKey(roleColor,    0f),
                new GradientColorKey(bright,       p1),
                new GradientColorKey(roleColor,    p2),
                new GradientColorKey(dustTipColor, 0.92f),
                new GradientColorKey(dustTipColor, 1f),
            },
            new[]
            {
                new GradientAlphaKey(drainingBaseAlpha * alpha,        0f),
                new GradientAlphaKey(drainingBaseAlpha * 0.5f * alpha, p0),
                new GradientAlphaKey(pulsePeakAlpha,                   p1),
                new GradientAlphaKey(drainingBaseAlpha * 0.5f * alpha, p2),
                new GradientAlphaKey(tipAlpha,                         0.92f),
                new GradientAlphaKey(0f,                               1f),
            }
        );
    }

    private static Color GetRoleColor(MusicalRole role)
    {
        var rp = MusicalRoleProfileLibrary.GetProfile(role);
        return rp != null
            ? new Color(rp.dustColors.baseColor.r, rp.dustColors.baseColor.g, rp.dustColors.baseColor.b, 1f)
            : Color.white;
    }
}
