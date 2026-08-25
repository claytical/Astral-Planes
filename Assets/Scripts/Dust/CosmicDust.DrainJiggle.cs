using System.Collections;
using UnityEngine;

public partial class CosmicDust
{
    private Coroutine _drainBuildupRoutine;

    // Shudder writes through SetBaseSpriteScale, which mutates _dustSpriteBaseVisualScale —
    // so the pre-buildup base must be captured here for CancelDrainBuildup to restore.
    private Vector3 _drainBuildupBaseScale = Vector3.one;

    // Anticipation FX while a PhaseStar tentacle charges up its drain: scale shudder that
    // ramps up over the duration, plus a brightening toward white on the display lane only —
    // _currentTint stays authoritative so charge/tint events are unaffected.
    public void BeginDrainBuildup(float seconds)
    {
        if (_drainBuildupRoutine != null)
        {
            StopCoroutine(_drainBuildupRoutine);
            SetBaseSpriteScale(_drainBuildupBaseScale); // don't capture a mid-shudder scale as the new base
        }
        CancelTintPulse(restoreToBase: false);
        _drainBuildupRoutine = StartCoroutine(DrainBuildupRoutine(Mathf.Max(0.01f, seconds)));
    }

    public void CancelDrainBuildup()
    {
        if (_drainBuildupRoutine == null) return;
        StopCoroutine(_drainBuildupRoutine);
        _drainBuildupRoutine = null;
        SetBaseSpriteScale(_drainBuildupBaseScale);
        RestoreDisplayToBaseTint();
    }

    private IEnumerator DrainBuildupRoutine(float duration)
{
    const float frequency = 26f;   // oscillations per second
    const float amplitude = 0.10f; // peak scale offset at full ramp
    const float brighten  = 0.55f; // max lerp toward white at full ramp

    Vector3 baseScale = _dustSpriteBaseVisualScale;
    _drainBuildupBaseScale = baseScale;
    float elapsed = 0f;

    while (elapsed < duration)
    {
        elapsed += Time.deltaTime;
        float ramp   = Mathf.Clamp01(elapsed / duration);
        float offset = Mathf.Sin(elapsed * frequency * Mathf.PI * 2f) * amplitude * ramp;
        SetBaseSpriteScale(baseScale * (1f + offset));

        Color bright = Color.Lerp(_currentTint, Color.white, ramp * brighten);
        bright.a = _currentTint.a;
        ApplyDisplayedTint(bright);
        yield return null;
    }

    SetBaseSpriteScale(baseScale);
    ApplyDisplayedTint(_currentTint);
    _drainBuildupRoutine = null;
}
}
