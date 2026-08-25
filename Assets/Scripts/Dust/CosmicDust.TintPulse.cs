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

// Single managed tint pulse lane (charge + deny both use this).
private Coroutine _tintPulseRoutine;
private int _tintPulseToken = 0;
private bool _tintPulseActive = false;

// Reused scratch buffer for SetDustColorAllParticles' live-particle rewrite — avoids a
// per-call allocation since a maze-wide pulse calls this on every solid cell every frame.
private ParticleSystem.Particle[] _colorParticleBuffer;

// True whenever this cell's particle color is currently controlled by an in-flight or
// abandoned rainbow pulse (the only caller of SetRainbowParticleColor). Checked by both
// RestoreDisplayToBaseTint and PrepareForReuse so a pulse interrupted mid-flight (e.g. the
// interstitial maze tears down and this cell gets pooled into the next motif before its
// ~1s pulse would have finished naturally) can't leave a stray hue stuck on particles that
// nothing else ever resets.
private bool _rainbowParticleDirty;

private void ClearRainbowParticleDirtyIfNeeded()
{
    if (!_rainbowParticleDirty) return;
    SetRainbowParticleColor(_baseCaptured ? _baseColor : _currentTint);
    _rainbowParticleDirty = false;
}

// Canonical/rest tint.
    private Color _currentTint = Color.white;

    private Color _baseColor;
    private float _baseSize;
    private float _baseAlpha;
    private bool _baseCaptured;

    /// <summary>
    /// Forces the particle system to re-sync with _currentTint. Call after ApplyRoleAndCharge
    /// when the cell is already live (not growing in), e.g. after DiscoveryTrackNode tinting —
    /// particles otherwise keep their birth color until explicitly refreshed.
    /// </summary>
    public void SyncParticleColor()
    {
        ApplyWorkShaderParamsParticlesOnly(roleColor: _currentTint, workSigned01: 0f);
    }
    private void ApplyDisplayedTint(Color tint)
    {
        _displayTint = tint;

        Color applied = tint;
        if (regrowAlphaCapped)
            applied.a = Mathf.Min(applied.a, kRegrowAlphaCap);
        SetDustColorAllParticles(applied);
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
        ClearRainbowParticleDirtyIfNeeded();
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

        // A new pulse should own the display exclusively — an in-flight drain-buildup writes
        // the same display tint every frame via its own coroutine and would otherwise keep
        // fighting the new pulse until it finishes on its own.
        if (_drainBuildupRoutine != null)
            CancelDrainBuildup();

        if (restoreToBase)
            RestoreDisplayToBaseTint();
    }
    public void SetTint(Color tint)
    {
        SetBaseTint(tint, applyImmediatelyIfNoPulse: true);
        ApplyTintVisual(_currentTint);
    }
    // Now the real particle tint path (dust is going particle-only — see CosmicDust migration
    // plan). Mirrors SetRainbowParticleColor: sets startColor for future emissions and rewrites
    // every currently-live particle so the change reads immediately. An earlier attempt at this
    // pushed near-black role tints onto particles that read as invisible under an additive blend
    // mode, breaking every interstitial maze — verify in-editor that the particle material
    // actually alpha-blends (not additive) before relying on this for solid/dim tints.
    private void SetDustColorAllParticles(Color target)
    {
        if (visual.particleSystem == null) return;

        var ps = visual.particleSystem;
        var main = ps.main;
        main.startColor = target;

        int count = ps.particleCount;
        if (count <= 0) return;

        if (_colorParticleBuffer == null || _colorParticleBuffer.Length < count)
            _colorParticleBuffer = new ParticleSystem.Particle[Mathf.Max(count, 32)];

        int actual = ps.GetParticles(_colorParticleBuffer);
        for (int i = 0; i < actual; i++)
            _colorParticleBuffer[i].startColor = target;
        ps.SetParticles(_colorParticleBuffer, actual);
    }

    // Isolated particle recolor used only by the rainbow pulse — never routed through by
    // any other caller (see SetDustColorAllParticles above), so it can't affect ordinary
    // cell spawn/reuse or other pulses. Sets startColor for future emissions and rewrites
    // every currently-live particle so the change reads immediately.
    private void SetRainbowParticleColor(Color target)
    {
        if (visual.particleSystem == null) return;

        var ps = visual.particleSystem;
        var main = ps.main;
        main.startColor = target;

        int count = ps.particleCount;
        if (count <= 0) return;

        if (_colorParticleBuffer == null || _colorParticleBuffer.Length < count)
            _colorParticleBuffer = new ParticleSystem.Particle[Mathf.Max(count, 32)];

        int actual = ps.GetParticles(_colorParticleBuffer);
        for (int i = 0; i < actual; i++)
            _colorParticleBuffer[i].startColor = target;
        ps.SetParticles(_colorParticleBuffer, actual);
    }
    internal void ApplyTintVisual(Color tint)
    {
        ApplyDisplayedTint(tint);
        ApplyWorkShaderParamsParticlesOnly(roleColor: tint, workSigned01: 0f);
        var explode = GetComponentInChildren<Explode>(true);
        if (explode != null) explode.SetTint(tint);
    }
    private Coroutine _retintOverRoutine;
    private Coroutine _tintFadeInRoutine;

    // Tracked entry point for RetintOver — starts the coroutine on this cell (not the caller)
    // and remembers the handle so StopAllVisualCoroutines/PrepareForReuse can cancel it. An
    // untracked, caller-owned version of this coroutine could keep animating a pooled-and-
    // reused cell's tint after the cell was handed to a new motif.
    public void RetintOverManaged(float seconds, Color toTint)
    {
        if (_retintOverRoutine != null) StopCoroutine(_retintOverRoutine);
        _retintOverRoutine = StartCoroutine(RetintOverAndClear(seconds, toTint));
    }
    private IEnumerator RetintOverAndClear(float seconds, Color toTint)
    {
        yield return RetintOver(seconds, toTint);
        _retintOverRoutine = null;
    }

    // Tracked entry point for TintFadeIn — same rationale as RetintOverManaged above.
    public void TintFadeInManaged(float seconds, Color fromTint, Color toTint)
    {
        if (_tintFadeInRoutine != null) StopCoroutine(_tintFadeInRoutine);
        _tintFadeInRoutine = StartCoroutine(TintFadeInAndClear(seconds, fromTint, toTint));
    }
    private IEnumerator TintFadeInAndClear(float seconds, Color fromTint, Color toTint)
    {
        yield return TintFadeIn(seconds, fromTint, toTint);
        _tintFadeInRoutine = null;
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

    // Non-boost bump feedback: a brief brighten-and-settle on top of whatever the cell's already
    // showing (its true role color, always — there's no hidden state to reveal anymore). Purely
    // transient; RestoreDisplayToBaseTint (end of TintPulseRoutine) snaps back to _currentTint.
    private void TriggerBumpPulse()
    {
        Color.RGBToHSV(_currentTint, out float h, out float s, out float v);
        // Kept small so this reads as a soft accent, not a flash — the vehicle-nose
        // push/displacement (CosmicDust.VehicleCompression.cs) is the primary bump cue.
        Color pulseTint = Color.HSVToRGB(h, s, Mathf.Clamp01(v + 0.15f));
        pulseTint.a = _currentTint.a;
        CancelTintPulse(restoreToBase: false);
        _tintPulseToken++;
        int token = _tintPulseToken;
        _tintPulseRoutine = StartCoroutine(TintPulseRoutine(token, pulseTint, 0.4f));
    }

    // Triggered maze-wide by CosmicDustGenerator.BeginRainbowPulseAllDust on wisp consume.
    // Same fade-in/hold/fade-out shape as TintPulseRoutine, except "hold" continuously
    // advances an HSV hue cycle instead of holding one constant color.
    public void BeginRainbowPulse(float seconds, float hueCyclesPerSecond, float saturation, float value)
{
    if (seconds <= 0f) return;
    CancelTintPulse(restoreToBase: false);
    _tintPulseToken++;
    int token = _tintPulseToken;
    _tintPulseRoutine = StartCoroutine(RainbowPulseRoutine(token, seconds, hueCyclesPerSecond, saturation, value));
}

    private Color RainbowColorAt(float elapsedSeconds, float hueCyclesPerSecond, float saturation, float value)
{
    float hue = Mathf.Repeat(elapsedSeconds * hueCyclesPerSecond, 1f);
    Color c = Color.HSVToRGB(hue, saturation, value);
    c.a = _currentTint.a;
    return c;
}

    private IEnumerator RainbowPulseRoutine(int token, float seconds, float hueCyclesPerSecond, float saturation, float value)
{
    _tintPulseActive = true;

    // Particles rest at their authored base color (_baseColor), not _currentTint — a
    // role's near-black tint (e.g. path-choice maze walls' solidDustTint) is fine for the
    // opaque sprite but reads as invisible once pushed onto particles under an additive
    // blend, which is exactly the regression this isolates against (see
    // SetDustColorAllParticles' comment). The bright rainbow hues themselves are safe
    // either way — only the rest color needs to avoid _currentTint.
    if (!_baseCaptured) CaptureBaseVisual();
    Color particleRestColor = _baseCaptured ? _baseColor : _currentTint;
    _rainbowParticleDirty = true;

    float fadeIn  = Mathf.Clamp(seconds * 0.15f, 0.05f, 0.3f);
    float fadeOut = Mathf.Clamp(seconds * 0.25f, 0.1f, 0.5f);
    float hold    = Mathf.Max(0f, seconds - fadeIn - fadeOut);

    Color startTint = _displayTint;
    float t = 0f;

    while (t < fadeIn)
    {
        if (token != _tintPulseToken) yield break;

        t += Time.deltaTime;
        float a = (fadeIn <= 0f) ? 1f : Mathf.Clamp01(t / fadeIn);
        Color rc = RainbowColorAt(t, hueCyclesPerSecond, saturation, value);

        ApplyDisplayedTint(Color.Lerp(startTint, rc, a));
        SetRainbowParticleColor(Color.Lerp(particleRestColor, rc, a));
        yield return null;
    }

    float holdElapsed = 0f;
    while (holdElapsed < hold)
    {
        if (token != _tintPulseToken) yield break;

        holdElapsed += Time.deltaTime;
        Color rc = RainbowColorAt(fadeIn + holdElapsed, hueCyclesPerSecond, saturation, value);
        ApplyDisplayedTint(rc);
        SetRainbowParticleColor(rc);
        yield return null;
    }

    // Sprite and particles were driven identically through fade-in/hold, so _displayTint
    // is a valid shared starting point for both fade-out lerps below even though they
    // diverge to different end targets.
    startTint = _displayTint;
    t = 0f;

    while (t < fadeOut)
    {
        if (token != _tintPulseToken) yield break;

        t += Time.deltaTime;
        float a = (fadeOut <= 0f) ? 1f : Mathf.Clamp01(t / fadeOut);

        ApplyDisplayedTint(Color.Lerp(startTint, _currentTint, a));
        SetRainbowParticleColor(Color.Lerp(startTint, particleRestColor, a));
        yield return null;
    }

    if (token == _tintPulseToken)
    {
        // RestoreDisplayToBaseTint checks _rainbowParticleDirty and cleans up particles
        // itself — same path a forced cancel (OnDisable) uses, so there's one place that
        // decides how to reset particles after a rainbow pulse touched them.
        RestoreDisplayToBaseTint();
        _tintPulseRoutine = null;
    }
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
