using UnityEngine;

public partial class CosmicDust
{
    // Fired on every regrow commit (see CosmicDustGenerator.cs CommitRegrowCell): pulses toward
    // a color reflecting this cell's role regrowthDelay, so players can learn "this color regrows
    // fast/slow" as a consistent rule instead of inferring it from repeated play. Reuses the same
    // tint-pulse machinery as PreviewResistance (CosmicDust.ResistancePreview.cs) — a non-destructive,
    // transient pulse on top of the settled role tint, never touching _currentTint/cell state.
    public void PreviewRegrowSpeed(float delaySeconds, float fastSeconds, float slowSeconds, Color fastColor, Color slowColor, float pulseSeconds, Color roleColor, float glowBoost01 = 0f, float speedTintAmount01 = 0.35f)
    {
        if (pulseSeconds <= 0f || slowSeconds <= fastSeconds) return;

        float t = Mathf.InverseLerp(fastSeconds, slowSeconds, delaySeconds);

        // Anchored on the cell's own role color, not a flat orange/blue swap — speed reads as a
        // modest warm/cool tint around that hue instead of replacing it, so this pulse can't be
        // mistaken for (or drown out) the role-identity reveal signal it's layered on top of.
        Color warm = Color.Lerp(roleColor, fastColor, speedTintAmount01);
        Color cool = Color.Lerp(roleColor, slowColor, speedTintAmount01);
        Color previewTint = Color.Lerp(warm, cool, Mathf.Clamp01(t));

        // Brightens toward white without discarding the role hue entirely, so a longer pulse
        // reads as a genuine glow/activation rather than just a slower flash of the same intensity.
        if (glowBoost01 > 0f)
            previewTint = Color.Lerp(previewTint, Color.white, Mathf.Clamp01(glowBoost01));

        CancelTintPulse(restoreToBase: false);
        _tintPulseToken++;
        int token = _tintPulseToken;
        _tintPulseRoutine = StartCoroutine(TintPulseRoutine(token, previewTint, pulseSeconds));
    }
}
