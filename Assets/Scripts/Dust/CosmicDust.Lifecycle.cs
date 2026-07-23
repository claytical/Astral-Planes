using System;
using System.Collections;
using UnityEngine;

public partial class CosmicDust
{
    private bool _isDespawned;
    private Coroutine  _fadeRoutine, _growInRoutine;
    private float _growInOverride = -1f;
    private Coroutine _emissionMulRoutine;

    public void SetGrowInDuration(float seconds)
    {
        _growInOverride = Mathf.Max(0.05f, seconds);
    }
    public void Begin()
    {
        if (!gameObject.activeInHierarchy)
        {
            Debug.LogWarning($"[COSMIC DUST] Begin called while inactive on {name}", this);
            return;
        }

        if (OnSpawnVisualRequested != null)
        {
            OnSpawnVisualRequested.Invoke();
            return;
        }

        // Backstop for lifecycle ordering: if Begin is called before the visual controller
        // subscribes (or if no controller is present), still run the spawn visuals.
        RunSpawnVisuals(ResolveGrowDurationSeconds());
    }
    public void DissipateAndHideVisualOnly(float fadeSeconds = -1f)
    {
        if (_isDespawned) return;
        _isDespawned = true;

        float d = (fadeSeconds > 0f) ? fadeSeconds : _timings.clearSpriteScaleOutSeconds;
        if (OnClearVisualRequested != null)
            OnClearVisualRequested.Invoke(d);
        else
            RunClearVisuals(d);

        // Notify generator after the carve-out read, so it can finalize state bookkeeping.
        if (_fadeRoutine != null) { StopCoroutine(_fadeRoutine); _fadeRoutine = null; }
        _fadeRoutine = StartCoroutine(NotifyGeneratorAfter(d));
    }
    internal float ResolveGrowDurationSeconds()
    {
        if (!_visualTimingsInitialized)
            throw new InvalidOperationException($"CosmicDust '{name}' must be initialized with InitializeVisuals before Begin.");
        float growSeconds = (_growInOverride > 0f) ? _growInOverride : _timings.regrowSpriteScaleInSeconds;
        growSeconds = Mathf.Max(0.01f, growSeconds);
        _growInOverride = -1f;
        return growSeconds;
    }
    private IEnumerator NotifyGeneratorAfter(float seconds)
    {
        if (seconds > 0f) yield return new WaitForSeconds(seconds);

        if (gen != null) gen.OnDustVisualFadedOut(this);
        _fadeRoutine = null;
    }
    internal void RunSpawnVisuals(float growSeconds)
    {
        SetVisualsEnabled(true);
        SetColorVariance();
        CaptureBaseVisual();
        ResetSpriteScaleTo(0f);
        AnimateSpriteScale(0f, _spriteScaleTarget, growSeconds);
        _particles.EnsureParticlesPlaying();
        SetEmissionMultiplier(1f, seconds: growSeconds);
        ApplyTintVisual(_currentTint);
    }
    internal void RunClearVisuals(float fadeSeconds)
    {
        // The clear owns the visuals from here: a still-running drain buildup would
        // stomp the scale-out through SetBaseSpriteScale. Stop it without restoring.
        if (_drainBuildupRoutine != null) { StopCoroutine(_drainBuildupRoutine); _drainBuildupRoutine = null; }
        SetTerrainColliderEnabled(false);
        AnimateSpriteScale(1f, 0f, fadeSeconds);
        SetEmissionMultiplier(0f, seconds: fadeSeconds);
        // Kill in-flight particles immediately so they don't outlive the sprite fade.
        var systems = _particles.GetAllParticleSystems();
        if (systems != null)
            for (int i = 0; i < systems.Length; i++)
                if (systems[i] != null) systems[i].Clear(true);
    }
    // Called by the generator when a Clearing -> Empty transition is finalized.
    public void FinalizeClearedVisuals()
    {
        SetTerrainColliderEnabled(false);
        var dormantTint = _currentTint;
        dormantTint.a = 0.35f;
        ApplyDisplayedTint(dormantTint);
        SetVisualsEnabled(true);
        ResetSpriteScaleTo(0f);
        _particles.ApplyEmissionMultiplierImmediate(0f);

    }
    private void StopAllVisualCoroutines()
    {
        if (_fadeRoutine        != null) { StopCoroutine(_fadeRoutine);        _fadeRoutine        = null; }
        if (_growInRoutine      != null) { StopCoroutine(_growInRoutine);      _growInRoutine      = null; }
        if (_spriteScaleRoutine != null) { StopCoroutine(_spriteScaleRoutine); _spriteScaleRoutine = null; }
        if (_emissionMulRoutine != null) { StopCoroutine(_emissionMulRoutine); _emissionMulRoutine = null; }
        if (_jiggleRoutine      != null) { StopCoroutine(_jiggleRoutine);      _jiggleRoutine      = null; }
        if (_drainBuildupRoutine != null) { StopCoroutine(_drainBuildupRoutine); _drainBuildupRoutine = null; }
        CancelTintPulse(restoreToBase: false);
    }
    public void PrepareForReuse()
    {
        StopAllVisualCoroutines();
        _isBreaking = false;
        regrowAlphaCapped = false;
        _isDespawned = false;
        _nonBoostClearSeconds = 0f;
        _growInOverride = -1f;
        _hiddenHintColor = Color.clear;
        SetWorkSigned01(0f);

        // Restore captured prefab/base scale instead of Vector3.one
        transform.localScale = (_baseLocalScale.sqrMagnitude > 0.0001f)
            ? _baseLocalScale
            : (visual.prefabReferenceScale.sqrMagnitude > 0.0001f ? visual.prefabReferenceScale : Vector3.one);

        // Invisible until Begin() is called.
        SetTerrainColliderEnabled(false);
        SetVisualsEnabled(true);
        ResetSpriteScaleTo(0f);

        // Re-enable particle renderers (HideVisualsInstant disables them; PrepareForReuse must
        // restore them so Begin()'s emission ramp is actually visible).
        if (visual.particleSystem != null)
        {
            var systems = _particles.GetAllParticleSystems();
            if (systems != null)
                for (int _i = 0; _i < systems.Length; _i++)
                {
                    if (systems[_i] == null) continue;
                    var _r = systems[_i].GetComponent<ParticleSystemRenderer>();
                    if (_r != null) _r.enabled = true;
                }
        }

        // Keep particles running but quiet until Begin() restores default emission.
        _particles.EnsureParticlesPlaying();
        _particles.ApplyEmissionMultiplierImmediate(0f);
        var dormantTint = _currentTint;
        dormantTint.a = 0.35f;
        ApplyDisplayedTint(dormantTint);
        clearing.drainResistance01 = 0f;
        _maxEnergyUnits            = 1;
        _currentEnergyUnits        = 1;
    }
    private void SetEmissionMultiplier(float targetMul, float seconds = 0f)
{
    targetMul = Mathf.Max(0f, targetMul);

    if (_emissionMulRoutine != null)
    {
        StopCoroutine(_emissionMulRoutine);
        _emissionMulRoutine = null;
    }

    if (seconds <= 0f)
    {
        _particles.ApplyEmissionMultiplierImmediate(targetMul);
        return;
    }

    _emissionMulRoutine = StartCoroutine(RunEmissionMultiplierLerp(targetMul, seconds));
}

    private IEnumerator RunEmissionMultiplierLerp(float targetMul, float seconds)
{
    yield return _particles.LerpEmissionMultiplier(_particles.CurrentMultiplier, targetMul, seconds);
    _emissionMulRoutine = null;
}

    public void HideVisualsInstant()
    {
        // Jiggle resumes after bridge root-reactivation and overwrites the zero scale set below.
        // Must be stopped here (not just in PrepareForReuse) because HideVisualsInstant is called
        // for solid cells that may never go through PrepareForReuse if they aren't in the new maze.
        StopAllVisualCoroutines();

        // Disable collisions immediately.
        SetTerrainColliderEnabled(false);

        // Hide sprite (authoritative for "solid" dust).
        // Use SetBaseSpriteScale(zero) instead of directly writing transform.localScale so that
        // _dustSpriteBaseVisualScale is also zeroed. TickVehicleCompression (Update) reads
        // _dustSpriteBaseVisualScale and writes it to srt.localScale every frame; if only the
        // transform is zeroed here, TickVehicleCompression will immediately restore a non-zero
        // scale after the bridge completes and the SpriteRenderer is re-enabled.
        if (visual.sprite != null)
        {
            SetBaseSpriteScale(Vector3.zero);
            visual.sprite.enabled = false;
        }

        // For pooling / true hiding, stop particles completely.
        if (visual.particleSystem != null)
        {
            var systems = _particles.GetAllParticleSystems();
            if (systems != null)
            {
                for (int i = 0; i < systems.Length; i++)
                {
                    var ps = systems[i];
                    if (ps == null) continue;

                    var em = ps.emission;
                    em.enabled = false;

                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                    ps.Clear(true);

                    var r = ps.GetComponent<ParticleSystemRenderer>();
                    if (r != null) r.enabled = false;
                }
            }
        }
    }
}
