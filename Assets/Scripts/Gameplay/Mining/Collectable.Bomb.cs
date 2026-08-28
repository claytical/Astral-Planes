using System;
using System.Collections;
using UnityEngine;

public partial class Collectable
{
    [Tooltip("Child transform carrying the bomb's cosmetic sprite/particle visuals. During the fuse and detonation, only this transform's scale swells/collapses — the root (and the solid dust-collision collider that lives on it) stays a fixed size the whole time, so the countdown swell no longer physically shoves overlapping vehicles. Falls back to the root transform if unassigned.")]
    [SerializeField] private Transform bombVisualRoot;

    // ---- Time bomb (discard) state ----
    private bool _isBomb;
    private bool _bombDetonated;
    private Coroutine _bombRoutine;
    private double _bombDetonateDsp;
    private double _bombFuseTotalSeconds;
    private double _bombArmedDsp;
    private Vector3 _bombBaseScale;
    private Color _bombBaseColor;
    private float _bombBaseVisualRadius;
    private float _bombEffectiveBlastRadius;
    private float _bombBlastRadiusCells;
    private ParticleSystem _bombCoreParticles;
    private Color _bombCoreBaseColor;
    private ParticleSystem[] _bombParticleSystems;
    private Vector3[] _bombParticleBaseScales;

    private Transform BombVisualRoot => bombVisualRoot != null ? bombVisualRoot : transform;

    /// <summary>
    /// Called when a carried note is discarded instead of committed to the loop. The note's
    /// energy was already processed, so leaving it out of the loop makes it unstable: it swells
    /// as its fuse burns down, then detonates (draining any Vehicle in its blast radius) at the
    /// next occurrence of its own encoded step (authoredAbsStep) — the same step-clock math used
    /// to schedule normal note deposits, so "just missed the window" naturally gives a full loop
    /// to get clear while "discarded right before the step" gives a short fuse.
    /// </summary>
    private void ArmAsTimeBomb(int authoredAbsStep)
    {
        EnsureTuning();

        if (_pulseRoutine != null) { StopCoroutine(_pulseRoutine); _pulseRoutine = null; }

        _isBomb = true;

        if (_rb == null) TryGetComponent(out _rb);
        if (_rb != null)
        {
            _rb.simulated = true;
            // A growing solid collider (swells during the fuse, then flashes to blastRadius at
            // detonation) left Dynamic in a dust field gets pushed by Box2D's own automatic
            // depenetration as it presses harder into surrounding dust the bigger it gets — this
            // happens at the physics-engine level, independent of any scripted force/velocity, so
            // zeroing velocity every tick alone can't prevent it (the position correction already
            // happened before that zeroing runs). This was the actual source of the bomb visibly
            // drifting off its discard position by detonation time. Kinematic bodies are exempt
            // from ALL physics-engine collision response (forces AND depenetration) while still
            // generating OnCollisionEnter2D against the Dynamic vehicle for bump-detonation — this
            // is what keeps the bomb pinned exactly in place for its whole lifetime.
            _rb.bodyType = RigidbodyType2D.Kinematic;
            _rb.linearVelocity = Vector2.zero;
        }
        // Re-enables the solid, non-trigger dust-collision colliders (cached by spawn-arrival
        // setup) so the bomb physically sits in the world like a live collectable.
        SetSpawnArrivalProtection(false);

        _bombBaseScale = BombVisualRoot.localScale;
        _bombBaseColor = energySprite != null ? energySprite.color : Color.white;
        // World-space visual radius at the current (base, pre-swell) scale, so Detonate() can
        // compute exactly what scale multiplier makes the rendered size equal blastRadius.
        _bombBaseVisualRadius = energySprite != null
            ? Mathf.Max(energySprite.bounds.extents.x, energySprite.bounds.extents.y)
            : 0.1f;

        // Resolved once here (not in Detonate()) so the radius-ring telegraph can show the true
        // blast radius from the moment the bomb is armed, not just once damage is about to land.
        ResolveAndCacheBombBlastRadius();
        EnsureBombTelegraphRing();
        UpdateBombTelegraphRing(0f);

        _bombCoreParticles = TryGetComponent(out CollectableParticles cp) ? cp.coreParticleSystem : null;
        _bombCoreBaseColor = _bombCoreParticles != null ? (Color)_bombCoreParticles.main.startColor.color : Color.white;

        // Particle systems on this prefab use Scaling Mode = Local (each reads only its own
        // transform.localScale, ignoring the root) — scaling the root alone is invisible on
        // them. Drive every attached particle system's own local scale in lockstep with the
        // root's swell/implosion, the same pattern AbsorbIntoVehicleRoutine already uses.
        _bombParticleSystems = GetComponentsInChildren<ParticleSystem>();
        _bombParticleBaseScales = new Vector3[_bombParticleSystems.Length];
        for (int i = 0; i < _bombParticleSystems.Length; i++)
            _bombParticleBaseScales[i] = _bombParticleSystems[i].transform.localScale;

        double dspNow = AudioSettings.dspTime;
        int fuseStep = authoredAbsStep >= 0 ? authoredAbsStep : intendedStep;
        double detonateDsp = (assignedInstrumentTrack != null && fuseStep >= 0)
            ? assignedInstrumentTrack.ComputeDepositDsp(fuseStep)
            : dspNow + tuning.bomb.minFuseSeconds;

        double fuseSeconds = Math.Max(tuning.bomb.minFuseSeconds, detonateDsp - dspNow);
        _bombDetonateDsp = dspNow + fuseSeconds;
        _bombFuseTotalSeconds = fuseSeconds;
        _bombArmedDsp = dspNow;

        if (_bombRoutine != null) StopCoroutine(_bombRoutine);
        _bombRoutine = StartCoroutine(BombFuseRoutine());
    }

    /// <summary>
    /// Resolves and caches the blast radius (both cell-count and world-space) once at arm time,
    /// off the bomb's own role profile — uniform across the whole blast, same as the radius
    /// already worked before this change, just moved earlier so the telegraph ring (which needs
    /// the true radius from the moment the bomb is armed) and Detonate() share one computation
    /// instead of Detonate() re-deriving it after the fact.
    /// </summary>
    private void ResolveAndCacheBombBlastRadius()
    {
        var drums = assignedInstrumentTrack != null ? assignedInstrumentTrack.drumTrack : null;
        float cellSize = drums != null ? Mathf.Max(0.001f, drums.GetCellWorldSize()) : 1f;
        _bombBlastRadiusCells = tuning.bomb.blastRadius / cellSize;
        _bombEffectiveBlastRadius = _bombBlastRadiusCells * cellSize;
    }

    private void ApplyParticleScaleLockstep(float ratio)
    {
        for (int i = 0; i < _bombParticleSystems.Length; i++)
            if (_bombParticleSystems[i] != null)
                _bombParticleSystems[i].transform.localScale = _bombParticleBaseScales[i] * ratio;
    }

    /// <summary>
    /// A vehicle physically bumping the ticking bomb (Collectable.Movement.cs OnCollisionEnter2D)
    /// detonates it immediately instead of just bouncing off it — skips whatever's left of the
    /// fuse straight to Detonate().
    /// </summary>
    private void TriggerEarlyDetonation()
    {
        if (!_isBomb || _bombDetonated) return;

        // While carried, Rigidbody2D.simulated is false (Collectable.Carry.cs), so the note can
        // end up positioned overlapping/adjacent to the discarding vehicle with no collision ever
        // detected. ArmAsTimeBomb re-enables simulation immediately, which reads as a brand-new
        // bump against that same vehicle the instant the bomb is armed. Ignore bumps for a short
        // grace window so physics has a moment to separate them before bump-detonation goes live.
        if (AudioSettings.dspTime - _bombArmedDsp < tuning.bomb.earlyDetonationGraceSeconds) return;

        if (_bombRoutine != null) { StopCoroutine(_bombRoutine); _bombRoutine = null; }
        Detonate();
    }

    private IEnumerator BombFuseRoutine()
    {
        while (AudioSettings.dspTime < _bombDetonateDsp)
        {
            // Kinematic bodies are immune to physics-engine collision RESPONSE, but Unity still
            // moves a Kinematic body by whatever linearVelocity is currently set — and dust's
            // OnCollisionStay2D directly sets velocity (not just AddForce) on contact. Re-zero it
            // every frame so a dust bump can't impart scripted motion either.
            if (_rb != null) _rb.linearVelocity = Vector2.zero;

            float remaining = (float)(_bombDetonateDsp - AudioSettings.dspTime);
            float t = Mathf.Clamp01(1f - remaining / Mathf.Max(0.001f, (float)_bombFuseTotalSeconds));

            float hz = Mathf.Lerp(tuning.bomb.minPulseHz, tuning.bomb.maxPulseHz, t);
            float pulse = (Mathf.Sin(Time.time * hz * Mathf.PI * 2f) * 0.5f) + 0.5f;
            // maxPulseScale is the whole swell target now (e.g. 2 = "about twice its size") —
            // root and every attached particle system scale together, so the sprite and the
            // ambient particles visibly swell as one connected object, not two disconnected
            // effects.
            float scaleMul = Mathf.Lerp(1f, tuning.bomb.maxPulseScale, t * pulse);
            BombVisualRoot.localScale = _bombBaseScale * scaleMul;
            ApplyParticleScaleLockstep(scaleMul);
            UpdateBombTelegraphRing(t);

            if (energySprite != null)
                energySprite.color = Color.Lerp(_bombBaseColor, tuning.bomb.warningColor, t);

            if (_bombCoreParticles != null)
            {
                var coreMain = _bombCoreParticles.main;
                coreMain.startColor = Color.Lerp(_bombCoreBaseColor, tuning.bomb.warningColor, t);
            }

            yield return null;
        }

        Detonate();
    }

    private void Detonate()
    {
        if (_bombDetonated) return; // guards against fuse-out and a bump racing in the same frame
        _bombDetonated = true;

        // Nothing can hit it once it's detonating — disable immediately so the collider
        // disappears at the same moment as the natural fuse-out, not just on bump-triggered
        // early detonation.
        if (_solidColliders != null)
            for (int i = 0; i < _solidColliders.Length; i++)
                if (_solidColliders[i] != null) _solidColliders[i].enabled = false;

        // The telegraph's job (showing danger before impact) is done now that damage/knockback
        // are about to land.
        HideBombTelegraphRing();

        // Dealing damage must never be able to skip cleanup: DrainEnergy can cascade into a
        // vehicle dying (Explode, OnVehicleDied, discarding ITS carried notes, ...), and if any
        // of that throws, an unguarded call here would abort before StartCoroutine(ImplodeAndDestroy())
        // ever runs — leaving this object (and its particle systems) alive and active forever,
        // which permanently blocks anything gated on "no collectables in flight" (e.g. PhaseStar
        // growth via GameFlowManager.AnyCollectablesInFlightGlobal()).
        try
        {
            if (_gfm == null) _gfm = GameFlowManager.Instance;
            var vehicles = _gfm != null ? _gfm.GetVehicles() : null;

            // Radius was already resolved and cached at arm time (ResolveAndCacheBombBlastRadius,
            // called from ArmAsTimeBomb) so the telegraph ring and this call agree on one number.
            float radius = _bombEffectiveBlastRadius;

            if (vehicles != null)
            {
                float baseDrain = amount * tuning.bomb.drainMultiplier;
                float roleScale = _roleProfile != null
                    ? Mathf.Lerp(1f, 0.1f, Mathf.Clamp01(_roleProfile.drainResistance01))
                    : 1f;

                for (int i = 0; i < vehicles.Count; i++)
                {
                    var v = vehicles[i];
                    if (v == null) continue;
                    float dist = Vector2.Distance(v.transform.position, transform.position);
                    if (dist > radius) continue;

                    float falloff = radius > 0f
                        ? Mathf.Lerp(1f, tuning.bomb.edgeDamageFraction, Mathf.Clamp01(dist / radius))
                        : 1f;
                    v.DrainEnergy(baseDrain * roleScale * falloff, "TimeBombCollectable");
                    v.TriggerDrainFeedback(tuning.bomb.warningColor);

                    Vector2 away = (Vector2)v.transform.position - (Vector2)transform.position;
                    away = away.sqrMagnitude > 0.0001f ? away.normalized : UnityEngine.Random.insideUnitCircle.normalized;
                    float knockbackFalloff = radius > 0f
                        ? Mathf.Lerp(1f, tuning.bomb.knockbackEdgeFraction, Mathf.Clamp01(dist / radius))
                        : 1f;
                    v.ApplyKnockback(away * tuning.bomb.knockbackImpulse * knockbackFalloff);
                }
            }

            var drums = assignedInstrumentTrack != null ? assignedInstrumentTrack.drumTrack : null;
            if (drums != null)
            {
                var centerGP = drums.CellOf(transform.position);
                int radiusCellsInt = Mathf.Max(1, Mathf.RoundToInt(_bombBlastRadiusCells));
                _gfm?.dustGenerator?.TriggerDiscardBlast(centerGP, radiusCellsInt, source: DustClearSource.CollectableBombBlast);
            }
        }
        catch (Exception ex)
        {
            Debug.LogException(ex, this);
        }

        StartCoroutine(FlashThenImplode());
    }

    /// <summary>
    /// The resolution beat after damage is dealt, in two parts: first a quick flash-grow to the
    /// actual blast radius (scale multiplier computed from _bombBaseVisualRadius so the rendered
    /// size, in world units, equals tuning.bomb.blastRadius — this is the only point the visual
    /// ties to blastRadius; the countdown swell is a fixed cosmetic multiplier, unrelated), then
    /// a collapse: everything shrinks back to nothing together with an accelerating "sucked in"
    /// curve rather than a linear shrink, so it reads as an implosion rather than a fade.
    /// </summary>
    private IEnumerator FlashThenImplode()
    {
        // Explicit != null throughout (not ?.) — Unity overloads == / != to correctly detect a
        // destroyed-but-still-referenced Object, but the ?. null-conditional operator bypasses
        // that overload and can throw MissingReferenceException on one, which — same as an
        // exception in Detonate() above — would silently abort this coroutine before it ever
        // reaches Destroy(gameObject) below.
        try
        {
            if (energySprite != null) energySprite.color = tuning.bomb.warningColor;
            if (_bombCoreParticles != null)
            {
                var coreMain = _bombCoreParticles.main;
                coreMain.startColor = tuning.bomb.warningColor;
            }

            // No new particles mid-flash/collapse — only what's already alive rides along with it.
            for (int i = 0; i < _bombParticleSystems.Length; i++)
                if (_bombParticleSystems[i] != null)
                    _bombParticleSystems[i].Stop(false, ParticleSystemStopBehavior.StopEmitting);
        }
        catch (Exception ex)
        {
            Debug.LogException(ex, this);
        }

        Vector3 flashStartRootScale = BombVisualRoot.localScale;
        var flashStartParticleScales = new Vector3[_bombParticleSystems.Length];
        for (int i = 0; i < _bombParticleSystems.Length; i++)
            flashStartParticleScales[i] = _bombParticleSystems[i] != null ? _bombParticleSystems[i].transform.localScale : Vector3.zero;

        float blastRatio = _bombBaseVisualRadius > 0.0001f
            ? _bombEffectiveBlastRadius / _bombBaseVisualRadius
            : tuning.bomb.maxPulseScale;
        Vector3 flashTargetRootScale = _bombBaseScale * blastRatio;

        float flashDuration = Mathf.Max(0.01f, tuning.bomb.detonationFlashSeconds);
        float flashElapsed = 0f;
        while (flashElapsed < flashDuration)
        {
            flashElapsed += Time.deltaTime;
            float u = Mathf.Clamp01(flashElapsed / flashDuration);

            BombVisualRoot.localScale = Vector3.Lerp(flashStartRootScale, flashTargetRootScale, u);
            for (int i = 0; i < _bombParticleSystems.Length; i++)
                if (_bombParticleSystems[i] != null)
                    _bombParticleSystems[i].transform.localScale =
                        Vector3.Lerp(flashStartParticleScales[i], _bombParticleBaseScales[i] * blastRatio, u);

            yield return null;
        }

        Vector3 peakRootScale = BombVisualRoot.localScale;
        var peakParticleScales = new Vector3[_bombParticleSystems.Length];
        for (int i = 0; i < _bombParticleSystems.Length; i++)
            peakParticleScales[i] = _bombParticleSystems[i] != null ? _bombParticleSystems[i].transform.localScale : Vector3.zero;

        float duration = Mathf.Max(0.01f, tuning.bomb.implosionSeconds);
        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float u = Mathf.Clamp01(elapsed / duration);
            float remainingFrac = 1f - (u * u); // accelerating collapse, not linear

            BombVisualRoot.localScale = peakRootScale * remainingFrac;
            for (int i = 0; i < _bombParticleSystems.Length; i++)
                if (_bombParticleSystems[i] != null)
                    _bombParticleSystems[i].transform.localScale = peakParticleScales[i] * remainingFrac;

            yield return null;
        }

        // Destroy(gameObject) must run no matter what — it's the single line every "no
        // collectables in flight" gate ultimately depends on (PruneSpawnedCollectables treats a
        // destroyed object as gone even if the explicit list removal below fails for any reason).
        try
        {
            assignedInstrumentTrack?.spawnedCollectables?.Remove(gameObject);
            NotifyDestroyedOnce();
        }
        finally
        {
            Destroy(gameObject);
        }
    }
}
