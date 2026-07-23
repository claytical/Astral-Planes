using System;
using System.Collections;
using UnityEngine;

/// <summary>
/// Owns base-emission capture and multiplier scaling for a CosmicDust cell's particle systems.
/// Particle systems stay running (no Stop/Clear "loop pops"); presence is driven purely by
/// modulating rateOverTime via a multiplier.
/// </summary>
public sealed class CosmicDustParticleEmissionSystem
{
    private readonly Func<ParticleSystem> _root;

    private ParticleSystem.MinMaxCurve[] _baseRateOverTime;
    private bool[] _baseEmissionEnabled;
    private bool _baseParticleEmissionCaptured;

    // Captured from the prefab at runtime (root particle system) for preview effects.
    private ParticleSystem.MinMaxCurve _baseEmissionCurve;
    private bool _baseEmissionCurveCaptured;

    private float _baseEmission;
    private float _emissionMulCurrent = 1f;

    public float CurrentMultiplier => _emissionMulCurrent;
    public float BaseEmissionScalar => _baseEmission;
    public ParticleSystem.MinMaxCurve BaseEmissionCurve => _baseEmissionCurve;
    public bool BaseEmissionCurveCaptured => _baseEmissionCurveCaptured;

    public CosmicDustParticleEmissionSystem(Func<ParticleSystem> root, float initialBaseEmissionScalar)
    {
        _root = root;
        _baseEmission = initialBaseEmissionScalar;
    }

    public ParticleSystem[] GetAllParticleSystems()
    {
        var rootPs = _root();
        if (rootPs == null) return null;
        return rootPs.GetComponentsInChildren<ParticleSystem>(true);
    }

    public void EnsureBaseParticleEmissionCaptured()
    {
        var rootPs = _root();
        var systems = GetAllParticleSystems();
        if (systems == null || systems.Length == 0) return;

        if (_baseParticleEmissionCaptured && _baseRateOverTime != null &&
            _baseEmissionEnabled != null &&
            _baseRateOverTime.Length == systems.Length &&
            _baseEmissionEnabled.Length == systems.Length)
            return;

        _baseRateOverTime = new ParticleSystem.MinMaxCurve[systems.Length];
        _baseEmissionEnabled = new bool[systems.Length];

        for (int i = 0; i < systems.Length; i++)
        {
            var ps = systems[i];
            if (ps == null) continue;

            var em = ps.emission;
            _baseEmissionEnabled[i] = em.enabled;
            _baseRateOverTime[i] = em.rateOverTime;

            // Capture a single "base emission" curve for preview effects from the root system.
            if (!_baseEmissionCurveCaptured && ps == rootPs)
            {
                _baseEmissionCurve = em.rateOverTime;

                // Maintain the legacy scalar for any code paths that still use it.
                // Prefer constant if possible; otherwise fall back to max constant.
                try
                {
                    if (_baseEmissionCurve.mode == ParticleSystemCurveMode.Constant)
                        _baseEmission = _baseEmissionCurve.constant;
                    else if (_baseEmissionCurve.mode == ParticleSystemCurveMode.TwoConstants)
                        _baseEmission = _baseEmissionCurve.constantMax;
                }
                catch { /* defensive */ }

                _baseEmissionCurveCaptured = true;
            }
        }

        _baseParticleEmissionCaptured = true;
    }

    private static ParticleSystem.MinMaxCurve ScaleCurve(ParticleSystem.MinMaxCurve c, float mul)
    {
        // Scale without destroying the authored curve modes.
        switch (c.mode)
        {
            case ParticleSystemCurveMode.Constant:
                return new ParticleSystem.MinMaxCurve(c.constant * mul);

            case ParticleSystemCurveMode.TwoConstants:
                return new ParticleSystem.MinMaxCurve(c.constantMin * mul, c.constantMax * mul);

            case ParticleSystemCurveMode.Curve:
                return new ParticleSystem.MinMaxCurve(c.curveMultiplier * mul, c.curve);

            case ParticleSystemCurveMode.TwoCurves:
                return new ParticleSystem.MinMaxCurve(c.curveMultiplier * mul, c.curveMin, c.curveMax);

            default:
                return c;
        }
    }

    public void EnsureParticlesPlaying()
    {
        var rootPs = _root();
        if (rootPs == null) return;

        var systems = GetAllParticleSystems();
        if (systems == null || systems.Length == 0) return;

        for (int i = 0; i < systems.Length; i++)
        {
            var ps = systems[i];
            if (ps == null) continue;

            // Do NOT touch main module settings or renderer flags here.
            // Only ensure the system is running so emission ramps are continuous.
            if (!ps.isPlaying)
                ps.Play(true);
        }
    }

    public void ApplyEmissionMultiplierImmediate(float mul01)
    {
        var rootPs = _root();
        if (rootPs == null) return;

        EnsureBaseParticleEmissionCaptured();
        EnsureParticlesPlaying();

        var systems = GetAllParticleSystems();
        if (systems == null || systems.Length == 0) return;

        float mul = Mathf.Max(0f, mul01);
        _emissionMulCurrent = mul;

        for (int i = 0; i < systems.Length; i++)
        {
            var ps = systems[i];
            if (ps == null) continue;

            var em = ps.emission;
            em.enabled = true;

            if (_baseRateOverTime != null && i < _baseRateOverTime.Length)
                em.rateOverTime = ScaleCurve(_baseRateOverTime[i], mul);
        }
    }

    public IEnumerator LerpEmissionMultiplier(float from, float to, float seconds)
    {
        seconds = Mathf.Max(0.01f, seconds);
        float t = 0f;

        while (t < seconds)
        {
            t += Time.deltaTime;
            float u = Mathf.SmoothStep(0f, 1f, t / seconds);
            ApplyEmissionMultiplierImmediate(Mathf.Lerp(from, to, u));
            yield return null;
        }

        ApplyEmissionMultiplierImmediate(to);
    }
}
