using UnityEngine;

public partial class CosmicDust
{
    // Non-destructive preview cue for Vehicle's scouting telegraph (see Vehicle.Plow.cs
    // UpdateScoutingPreview): briefly pulses toward a resistance-weighted color so players can see
    // a route's cost before contact instead of discovering it by draining fuel. Reuses the same
    // tint-pulse machinery as the deny-pulse feedback (TriggerDenyTintPulse), just parameterized by
    // live resistance instead of a fixed deny color — it never touches _currentTint/cell state.
    public void PreviewResistance(float resistance01, float pulseSeconds = 0.18f)
    {
        float r = Mathf.Clamp01(resistance01);
        if (r <= 0f || pulseSeconds <= 0f) return;

        Color previewTint = _hasFeedbackColors
            ? Color.Lerp(_chargeColor, _denyColor, r)
            : Color.Lerp(Color.white, Color.magenta, r);

        CancelTintPulse(restoreToBase: false);
        _tintPulseToken++;
        int token = _tintPulseToken;
        _tintPulseRoutine = StartCoroutine(TintPulseRoutine(token, previewTint, pulseSeconds));
    }
}
