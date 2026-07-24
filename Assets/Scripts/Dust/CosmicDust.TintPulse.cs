using System;
using System.Collections;
using UnityEngine;
using Random = UnityEngine.Random;

public partial class CosmicDust
{
// Authoritative/resting tint of this dust cell.
// This is what retinting, charge drain, role assignment, etc. should modify.
    public Color CurrentTint => _currentTint;

// Currently displayed tint on the sprite.
// Temporary pulses modify this, but must not overwrite _currentTint.
    private Color _displayTint = Color.white;
    [SerializeField] private Color _chargeColor = Color.white;
    [SerializeField] private Color _denyColor = Color.magenta;
    private Color _hiddenHintColor; // Color.clear = no hint stored
    private bool _hasFeedbackColors = false;
    public bool regrowAlphaCapped = false;
    private const float kRegrowAlphaCap = 0.20f;
    [SerializeField] private float colliderDisabledAlpha = 0.08f;
    [SerializeField] private bool useWorkShaderParams = true;

[SerializeField, Tooltip("Fallback deny pulse duration when no explicit pulse length is provided.")]
private float denyPulseDefaultSeconds = 0.25f;

// Single managed tint pulse lane (charge + deny both use this).
private Coroutine _tintPulseRoutine;
private int _tintPulseToken = 0;
private bool _tintPulseActive = false;

// Canonical/rest tint.
    private Color _currentTint = Color.white;

    private Color _baseColor;
    private float _baseSize;
    private float _baseAlpha;
    private bool _baseCaptured;

    /// <summary>
    /// Syncs both the sprite renderer AND the particle system to _currentTint.
    /// Call after ApplyRoleAndCharge when the cell is already live (not growing in),
    /// e.g. after DiscoveryTrackNode tinting. ApplyRoleAndCharge alone only updates the sprite;
    /// particles keep their birth color until explicitly refreshed.
    /// </summary>
    public void SyncParticleColor()
    {
        if (useWorkShaderParams)
            ApplyWorkShaderParamsParticlesOnly(roleColor: _currentTint, workSigned01: 0f);
        else
            SetDustColorAllParticles(_currentTint);
    }
    private void ApplyDisplayedTint(Color tint)
    {
        _displayTint = tint;

        if (visual.sprite != null)
        {
            Color applied = tint;
            if (regrowAlphaCapped)
                applied.a = Mathf.Min(applied.a, kRegrowAlphaCap);
            visual.sprite.color = applied;
        }
    }
    private void SetBaseTint(Color tint, bool applyImmediatelyIfNoPulse = true)
    {
        // Preserve _currentTint.a — it is authoritative charge state managed only by
        // DrainCharge, ApplyRoleAndCharge, and EnsureMinSolidAlpha. Callers of SetBaseTint
        // (diffusion, retinting) must not reset drain progress.
        tint.a = _currentTint.a;
        _currentTint = tint;

        if (!_tintPulseActive || applyImmediatelyIfNoPulse)
            ApplyDisplayedTint(_currentTint);
    }
    private void RestoreDisplayToBaseTint()
    {
        _tintPulseActive = false;
        ApplyDisplayedTint(_currentTint);
    }
    private void CancelTintPulse(bool restoreToBase)
    {
        _tintPulseToken++;

        if (_tintPulseRoutine != null)
        {
            StopCoroutine(_tintPulseRoutine);
            _tintPulseRoutine = null;
        }

        _tintPulseActive = false;

        if (restoreToBase)
            RestoreDisplayToBaseTint();
    }
    public void SetTint(Color tint)
    {
        SetBaseTint(tint, applyImmediatelyIfNoPulse: true);
        OnTintStateChanged?.Invoke(_currentTint);

        // Particles: leave authored color/gradient/material alone.
        // (If you later want particle tinting, do it as an explicit opt-in path.)
    }
    private void SetDustColorAllParticles(Color target)
    {
        if (visual.particleSystem == null) return;

        var ps   = visual.particleSystem;
        var main = ps.main;
        var col  = ps.colorOverLifetime;

        col.enabled = true;
    }
    internal void ApplyTintVisual(Color tint)
    {
        ApplyDisplayedTint(tint);
        if (useWorkShaderParams)
            ApplyWorkShaderParamsParticlesOnly(roleColor: tint, workSigned01: 0f);
        else
            SetDustColorAllParticles(tint);
        var explode = GetComponentInChildren<Explode>(true);
        if (explode != null) explode.SetTint(tint);
    }
    public IEnumerator RetintOver(float seconds, Color toTint)
    {
        if (visual.particleSystem == null) yield break;

        var main = visual.particleSystem.main;
        Color from = (main.startColor.mode == ParticleSystemGradientMode.Color)
            ? main.startColor.color
            : Color.white;

        float t = 0f;
        while (t < seconds)
        {
            t += Time.deltaTime;
            float u = Mathf.SmoothStep(0f, 1f, t / Mathf.Max(0.0001f, seconds));
            Color now = Color.Lerp(from, toTint, u);
            SetBaseTint(now, applyImmediatelyIfNoPulse: false);
            yield return null;
        }
        SetBaseTint(toTint, applyImmediatelyIfNoPulse: true);
    }

    public IEnumerator TintFadeIn(float seconds, Color fromTint, Color toTint)
    {
        float t = 0f;
        while (t < seconds)
        {
            t += Time.deltaTime;
            float u = Mathf.SmoothStep(0f, 1f, t / Mathf.Max(0.0001f, seconds));
            ApplyTintVisual(Color.Lerp(fromTint, toTint, u));
            yield return null;
        }
        ApplyTintVisual(toTint);
    }

    private void TriggerChargeTintPulse()
{
    const float kFadeIn  = 0.03f;
    const float kFadeOut = 0.05f;

    CancelTintPulse(restoreToBase: false);

    _tintPulseToken++;
    int token = _tintPulseToken;
    _tintPulseRoutine = StartCoroutine(ChargeTintPulseRoutine(token, kFadeIn, kFadeOut));
}

    private IEnumerator ChargeTintPulseRoutine(int token, float fadeIn, float fadeOut)
{
    _tintPulseActive = true;

    Color chargeTint = _hasFeedbackColors ? _chargeColor : Color.white;
    chargeTint.a = _currentTint.a;

    Color startTint = _displayTint;
    float t = 0f;

    while (t < fadeIn)
    {
        if (token != _tintPulseToken) yield break;

        t += Time.deltaTime;
        float a = (fadeIn <= 0f) ? 1f : Mathf.Clamp01(t / fadeIn);

        // Rebuild target each frame so live drain alpha is respected.
        Color liveChargeTint = chargeTint;
        liveChargeTint.a = _currentTint.a;

        ApplyDisplayedTint(Color.Lerp(startTint, liveChargeTint, a));
        yield return null;
    }

    startTint = _displayTint;
    t = 0f;

    while (t < fadeOut)
    {
        if (token != _tintPulseToken) yield break;

        t += Time.deltaTime;
        float a = (fadeOut <= 0f) ? 1f : Mathf.Clamp01(t / fadeOut);

        // Fade back toward live base tint so ongoing charge drain is respected.
        ApplyDisplayedTint(Color.Lerp(startTint, _currentTint, a));
        yield return null;
    }

    if (token == _tintPulseToken)
    {
        RestoreDisplayToBaseTint();
        _tintPulseRoutine = null;
    }
}

    private void TriggerDenyTintPulse(float seconds = -1f)
{
    float dur = (seconds > 0f) ? seconds : denyPulseDefaultSeconds;
    if (dur <= 0f) return;
    CancelTintPulse(restoreToBase: false);
    _tintPulseToken++;
    int token = _tintPulseToken;
    Color denyTint = _hasFeedbackColors ? _denyColor : Color.black;
    _tintPulseRoutine = StartCoroutine(TintPulseRoutine(token, denyTint, dur));
}

    private void TriggerHiddenHintPulse()
{
    if (_hiddenHintColor.a <= 0f) return;
    CancelTintPulse(restoreToBase: false);
    _tintPulseToken++;
    int token = _tintPulseToken;
    _tintPulseRoutine = StartCoroutine(TintPulseRoutine(token, _hiddenHintColor, 0.5f));
}

    private IEnumerator TintPulseRoutine(int token, Color pulseColor, float seconds)
{
    if (seconds <= 0f) yield break;

    _tintPulseActive = true;

    Color denyTint = pulseColor;
    denyTint.a = _currentTint.a;

    float fadeIn  = Mathf.Clamp(seconds * 0.25f, 0.03f, 0.10f);
    float fadeOut = Mathf.Clamp(seconds * 0.35f, 0.05f, 0.14f);
    float hold    = Mathf.Max(0f, seconds - fadeIn - fadeOut);

    Color startTint = _displayTint;
    float t = 0f;

    while (t < fadeIn)
    {
        if (token != _tintPulseToken) yield break;

        t += Time.deltaTime;
        float a = (fadeIn <= 0f) ? 1f : Mathf.Clamp01(t / fadeIn);

        Color liveDenyTint = denyTint;
        liveDenyTint.a = _currentTint.a;

        ApplyDisplayedTint(Color.Lerp(startTint, liveDenyTint, a));
        yield return null;
    }

    if (hold > 0f)
    {
        float end = Time.time + hold;
        while (Time.time < end)
        {
            if (token != _tintPulseToken) yield break;

            Color liveDenyTint = denyTint;
            liveDenyTint.a = _currentTint.a;

            ApplyDisplayedTint(liveDenyTint);
            yield return null;
        }
    }

    startTint = _displayTint;
    t = 0f;

    while (t < fadeOut)
    {
        if (token != _tintPulseToken) yield break;

        t += Time.deltaTime;
        float a = (fadeOut <= 0f) ? 1f : Mathf.Clamp01(t / fadeOut);

        ApplyDisplayedTint(Color.Lerp(startTint, _currentTint, a));
        yield return null;
    }

    if (token == _tintPulseToken)
    {
        RestoreDisplayToBaseTint();
        _tintPulseRoutine = null;
    }
}
    private void CaptureBaseVisual()
    {
        if (visual.particleSystem == null) return;
        _particles.EnsureBaseParticleEmissionCaptured();
        var main = visual.particleSystem.main;

        _baseColor = main.startColor.color;
        _baseSize  = main.startSize.constant;
        _baseAlpha = _baseColor.a;
        _baseCaptured = true;

    }
    private void ApplyWorkShaderParamsParticlesOnly(Color roleColor, float workSigned01)
    {
        // Shader is retired; interpret workSigned01 as:
        //  0 -> base tint (roleColor)
        // >0 -> charge tint
        // <0 -> deny tint

        if (visual.particleSystem == null) return;
        if (!_baseCaptured) CaptureBaseVisual();

        float w = Mathf.Clamp(workSigned01, -1f, 1f);

        Color baseCol = roleColor;
        baseCol.a = _baseAlpha;

        if (Mathf.Abs(w) < 0.0001f)
        {
            SetDustColorAllParticles(baseCol);
            return;
        }

        if (w > 0f)
        {
            Color target = _hasFeedbackColors ? _chargeColor : Color.white;
            target.a = _baseAlpha;
            SetDustColorAllParticles(Color.Lerp(baseCol, target, w));
        }
        else
        {
            Color target = _hasFeedbackColors ? _denyColor : Color.black;
            target.a = _baseAlpha;
            SetDustColorAllParticles(Color.Lerp(baseCol, target, -w));
        }
    }
    private void SetWorkSigned01(float workSigned01)
    {
        // Retained for callers like ResetVisualToBase().
        ApplyWorkShaderParamsParticlesOnly(roleColor: _currentTint, workSigned01: workSigned01);
    }
    public void SetFeedbackColors(Color chargeColor, Color denyColor)
    {
        _chargeColor = chargeColor;
        _denyColor = denyColor;
        _hasFeedbackColors = true;
    }
    public void SetHiddenHintColor(Color c) { _hiddenHintColor = c; }
    private void OnDisable()
    {
        CancelTintPulse(restoreToBase: true);
    }
    private void ResetVisualToBase()
    {
        CancelTintPulse(restoreToBase: true);

        // Reset shader param #2 back to neutral.
        SetWorkSigned01(0f);

        if (visual.particleSystem == null) return;
        if (!_baseCaptured) return;

        var main = visual.particleSystem.main;
        main.startSize = _baseSize;

        // Restore emission if we captured it.
        var emission = visual.particleSystem.emission;
        _particles.EnsureBaseParticleEmissionCaptured();
        if (_particles.BaseEmissionCurveCaptured)
            emission.rateOverTime = _particles.BaseEmissionCurve;
        else
            emission.rateOverTime = _particles.BaseEmissionScalar;
    }
    private void SetColorVariance()
    {
        var c = _currentTint;
        float variation = Random.Range(-.02f, .02f);
        c.r = Mathf.Clamp01(c.r + variation);
        c.g = Mathf.Clamp01(c.g + variation);
        c.b = Mathf.Clamp01(c.b + variation);
        SetTint(c);
    }
}
