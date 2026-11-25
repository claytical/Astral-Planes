using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Quaternion = UnityEngine.Quaternion;
using Vector2 = UnityEngine.Vector2;
using Vector3 = UnityEngine.Vector3;

[DisallowMultipleComponent]
public sealed class PhaseStarVisuals2D : MonoBehaviour
{
    [Header("Sprite pivots")]
    public Sprite diamond;
    public Sprite activeDiamond;
    public Sprite activeDiamondRhythm;

    [Tooltip("Shared PhaseStar tuning (Resources/Config/PhaseStarConfig). Unassigned = that asset is loaded.")]
    [SerializeField] private PhaseStarConfig config;
    private PhaseStarConfig _cfg;
    private PhaseStarConfig Cfg => _cfg ??= PhaseStarConfig.OrDefault(config, this);
    // Shared tuning (PhaseStarConfig → 2. Look)
    private float diamondAngleSmoothTime => Cfg.diamondAngleSmoothTime;
    private float dimRgbMul => Cfg.dimRgbMul;
    private bool dimUsesFixedTint => Cfg.dimUsesFixedTint;
    private float notReadyShakeSeconds => Cfg.notReadyShakeSeconds;
    private float notReadyShakeDeg => Cfg.notReadyShakeDeg;
    private float notReadyShakeHz => Cfg.notReadyShakeHz;
    private float sleepDesaturate01 => Cfg.sleepDesaturate01;
    private float sleepAlphaMul => Cfg.sleepAlphaMul;
    private Color dimShardTint => Cfg.dimShardTint;

    private float _notReadyShakeStart = -999f;
    public bool Sleeping { get; set; }

    public ParticleSystem particles;

    [Header("Particle Debug")]
    [SerializeField] private bool traceParticleState;
    [SerializeField, Min(0f)] private float particleAlphaDriftTolerance = 0.02f;
    private float _expectedParticleAlpha = -1f;

    private SpriteRenderer[] _shardSpriteRenderers;

    // The star's count diamonds (PhaseStar.DiamondBody.cs): tinted by PhaseStar itself, so the
    // generic shard tint (dim/bright) skips them.
    private readonly HashSet<SpriteRenderer> _starDiamonds = new HashSet<SpriteRenderer>();

    public void Initialize()
    {
        CacheShardRenderers();
    }

    public void RegisterStarDiamond(SpriteRenderer sr)   { if (sr) _starDiamonds.Add(sr); }
    public void UnregisterStarDiamond(SpriteRenderer sr) { if (sr) _starDiamonds.Remove(sr); }

    /// <summary>Sprite for a formed diamond of this role.</summary>
    public Sprite FormedDiamondSprite(MusicalRole role) =>
        (role == MusicalRole.Rhythm && activeDiamondRhythm != null) ? activeDiamondRhythm
        : (activeDiamond != null ? activeDiamond : diamond);

    /// <summary>Applies the sleeping (blocked elsewhere) desaturate and fade, if asleep.</summary>
    public Color ApplySleepTint(Color c)
    {
        if (!Sleeping) return c;
        Color grey = new Color(dimShardTint.r, dimShardTint.g, dimShardTint.b, 1f);
        float a = c.a * sleepAlphaMul;
        c = Color.Lerp(c, grey, sleepDesaturate01);
        c.a = a;
        return c;
    }

    /// <summary>The not-ready head shake's current angle (degrees), 0 when idle.</summary>
    public float CurrentShakeDeg()
    {
        float shakeT = Time.time - _notReadyShakeStart;
        if (shakeT < 0f || shakeT >= notReadyShakeSeconds) return 0f;
        float decay = 1f - shakeT / notReadyShakeSeconds;
        return Mathf.Sin(shakeT * notReadyShakeHz * Mathf.PI * 2f) * notReadyShakeDeg * decay;
    }

    public float DiamondAngleSmoothTime => diamondAngleSmoothTime;

    private void CacheShardRenderers()
    {
        var srs = GetComponentsInChildren<SpriteRenderer>(true);
        var list = new List<SpriteRenderer>(srs.Length);

        for (int i = 0; i < srs.Length; i++)
        {
            var sr = srs[i];
            if (!sr) continue;
            if (sr.gameObject.name == "Scout Visual") continue;
            list.Add(sr);
        }

        _shardSpriteRenderers = list.ToArray();
    }

    /// <summary>Clears the generic renderer cache (call after adding/removing child renderers).</summary>
    public void InvalidateShardCache()
    {
        _shardSpriteRenderers = null;
    }

    public void EjectParticles(GameObject ejectionPrefab)
    {
        if (ejectionPrefab != null)
            Instantiate(ejectionPrefab, transform.position, Quaternion.identity);
    }

    public void ShowBright(Color c)
    {
        SetTintWithParticles(c);
        ToggleShardRenderers(true);
        ApplyParticleAlpha(0.85f);

        EnsureParticlesVisible(playIfStopped: true);
    }

    public void ShowDim(Color tint)
    {
        ToggleShardRenderers(true);

        Color c;
        if (dimUsesFixedTint)
        {
            c = dimShardTint;
            c.r *= dimRgbMul;
            c.g *= dimRgbMul;
            c.b *= dimRgbMul;
            c.a = dimShardTint.a;
        }
        else
        {
            c = tint;
            c.r *= dimRgbMul;
            c.g *= dimRgbMul;
            c.b *= dimRgbMul;
            c.a = dimShardTint.a;
        }

        SetShardTint(c);

        Color particleTint = tint;
        if (particleTint.maxColorComponent <= 0.001f)
            particleTint = c;
        SetTintWithParticles(particleTint);
        ApplyParticleAlpha(0.35f);

        EnsureParticlesVisible(playIfStopped: true);
    }

    // "Not ready": the star's diamonds shake their head. Purely rotational, so it never fights
    // the charge-scale or body-tint updates (and never leaves the star stuck dim).
    public void PlayNotReadyShake()
    {
        _notReadyShakeStart = Time.time;
    }

    public void HideAll()
    {
        ToggleShardRenderers(false);

        var pss = GetComponentsInChildren<ParticleSystem>(true);
        for (int i = 0; i < pss.Length; i++)
        {
            var ps = pss[i];
            if (!ps || !ps.isPlaying) continue;
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        }
    }

    // Called every frame from PhaseStar.Update() to continuously lerp body color.
    public void LerpBodyColor(Color roleColor, float t)
    {
        Color dimColor = dimUsesFixedTint ? dimShardTint : roleColor;
        dimColor.r *= dimRgbMul;
        dimColor.g *= dimRgbMul;
        dimColor.b *= dimRgbMul;
        dimColor.a = dimShardTint.a;

        Color brightColor = roleColor;
        brightColor.a = 0.85f;

        SetShardTint(Color.Lerp(dimColor, brightColor, t));
    }

    private void SetShardTint(Color c)
    {
        if (_shardSpriteRenderers == null || _shardSpriteRenderers.Length == 0 || _shardSpriteRenderers[0] == null)
            CacheShardRenderers();

        for (int i = 0; i < _shardSpriteRenderers.Length; i++)
        {
            var sr = _shardSpriteRenderers[i];
            if (!sr) continue;
            if (_starDiamonds.Contains(sr)) continue;
            sr.color = c;
        }
    }

    private void SetTintWithParticles(Color c)
    {
        c.a = 1f;

        var pss = GetComponentsInChildren<ParticleSystem>(true);
        for (int i = 0; i < pss.Length; i++)
        {
            var ps = pss[i];
            if (!ps) continue;

            var main = ps.main;
            var start = c;
            main.startColor = new ParticleSystem.MinMaxGradient(start);

            var col = ps.colorOverLifetime;
            col.enabled = true;

            var grad = new Gradient();
            grad.SetKeys(
                new[] { new GradientColorKey(start, 0f), new GradientColorKey(start, 1f) },
                new[]
                {
                    new GradientAlphaKey(0, 0f),
                    new GradientAlphaKey(.5f, .5f),
                    new GradientAlphaKey(0f, 1f)
                }
            );
            col.color = new ParticleSystem.MinMaxGradient(grad);
        }

        TraceParticleState("SetTintWithParticles");
    }

    private void ApplyParticleAlpha(float a)
    {
        _expectedParticleAlpha = a;

        var pss = GetComponentsInChildren<ParticleSystem>(true);
        foreach (var ps in pss)
        {
            if (!ps) continue;
            var main = ps.main;
            var c = main.startColor.color;
            c.a = a;
            main.startColor = new ParticleSystem.MinMaxGradient(c);
        }

        TraceParticleState($"ApplyParticleAlpha({a:0.###})");
    }

    private void LateUpdate()
    {
        if (!traceParticleState || _expectedParticleAlpha < 0f) return;

        var pss = GetComponentsInChildren<ParticleSystem>(true);
        for (int i = 0; i < pss.Length; i++)
        {
            var ps = pss[i];
            if (!ps) continue;

            float actualAlpha = ps.main.startColor.color.a;
            if (Mathf.Abs(actualAlpha - _expectedParticleAlpha) > particleAlphaDriftTolerance)
            {
                Debug.LogWarning($"[PhaseStarVisuals2D] Particle alpha drift on '{ps.name}'. Expected {_expectedParticleAlpha:0.###}, actual {actualAlpha:0.###}. Another system may be overwriting startColor.", ps);
            }
        }
    }

    private void TraceParticleState(string reason)
    {
        if (!traceParticleState) return;

        var pss = GetComponentsInChildren<ParticleSystem>(true);
        for (int i = 0; i < pss.Length; i++)
        {
            var ps = pss[i];
            if (!ps) continue;
            var main = ps.main;
            Color c = main.startColor.color;
            if (GameFlowManager.VerboseLogging) Debug.Log($"[PhaseStarVisuals2D] {reason} :: '{ps.name}' activeSelf={ps.gameObject.activeSelf} playing={ps.isPlaying} rendererEnabled={ps.GetComponent<ParticleSystemRenderer>()?.enabled} startRGBA=({c.r:0.###},{c.g:0.###},{c.b:0.###},{c.a:0.###})", ps);
        }
    }

    private void EnsureParticlesVisible(bool playIfStopped)
    {
        var pss = GetComponentsInChildren<ParticleSystem>(true);
        if (pss == null || pss.Length == 0) return;

        if (!particles)
            particles = pss[0];

        for (int i = 0; i < pss.Length; i++)
        {
            var ps = pss[i];
            if (!ps) continue;

            if (!ps.gameObject.activeSelf)
                ps.gameObject.SetActive(true);

            var renderer = ps.GetComponent<ParticleSystemRenderer>();
            if (renderer) renderer.enabled = true;

            if (playIfStopped && !ps.isPlaying)
                ps.Play();
        }

        TraceParticleState($"EnsureParticlesVisible(playIfStopped={playIfStopped})");
    }
    public void ToggleShardRenderers(bool on)
    {
        if (_shardSpriteRenderers == null || _shardSpriteRenderers.Length == 0 || _shardSpriteRenderers[0] == null)
            CacheShardRenderers();

        for (int i = 0; i < _shardSpriteRenderers.Length; i++)
        {
            if (_shardSpriteRenderers[i])
                _shardSpriteRenderers[i].enabled = on;
        }
    }
}
