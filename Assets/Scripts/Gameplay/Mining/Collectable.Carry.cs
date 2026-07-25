using System;
using System.Collections;
using UnityEngine;

public partial class Collectable
{
    /// <summary>Sets the world-space target position for this note in the vehicle trail.</summary>
    public void SetTrailTarget(Vector3 worldPos) => _trailWorldTarget = worldPos;

    /// <summary>
    /// Drives the visual urgency of this note (0 = just collected, 1 = release imminent).
    /// </summary>
    public void SetReleasePulse(float pulse01) => _trailReleasePulse01 = Mathf.Clamp01(pulse01);

    private IEnumerator TrailFollowRoutine()
    {
        while (_trailFollowActive)
        {
            float dt = Time.deltaTime;
            float pulse = _trailReleasePulse01;

            // Radius shrinks as release approaches, focusing the energy like a coiling spring.
            float focusedRadius = Mathf.Lerp(trailDriftRadius, trailDriftRadius * trailDriftFocusMul, pulse);
            _trailDriftPhase += trailDriftSpeed * dt;

            // Lissajous figure: x and y use slightly different frequencies for organic "restless" feel.
            Vector3 driftOffset = new Vector3(
                Mathf.Sin(_trailDriftPhase) * focusedRadius,
                Mathf.Cos(_trailDriftPhase * 0.73f) * focusedRadius * 0.6f,
                0f
            );

            Vector3 desiredPos = _trailWorldTarget + driftOffset;
            float lerpT = Mathf.Clamp01(trailFollowLerp * dt);
            transform.position = Vector3.Lerp(transform.position, desiredPos, lerpT);

            if (_trailBaseScaleCaptured && energySprite != null)
            {
                float breathe = Mathf.Sin(Time.time * trailReadyPulseSpeed * (1f + pulse * 2f)) * 0.5f + 0.5f;
                float scaleTarget = Mathf.Lerp(trailIdleScaleMin, trailReadyScaleMax, pulse * breathe);
                transform.localScale = _trailBaseScale * scaleTarget;

                var col = energySprite.color;
                col.a = Mathf.Lerp(col.a, Mathf.Lerp(0.45f, 0.95f, pulse), 0.15f);
                energySprite.color = col;
            }

            yield return null;
        }
    }

    /// <summary>
    /// Plays a quick "energy shield" absorb beat: the collectable grows to roughly the
    /// vehicle's own size while flying toward its center, holds briefly, then shrinks back
    /// down while completing its move to wherever <paramref name="getDestinationWorldPos"/>
    /// reports. Both the vehicle and the destination are re-sampled every frame (not snapshot
    /// once) so the effect tracks a moving vehicle instead of animating toward a stale point.
    /// </summary>
    private IEnumerator AbsorbIntoVehicleRoutine(Vehicle vehicle, Func<Vector3> getDestinationWorldPos)
    {
        // PulseEnergySprite runs for the collectable's whole life and also drives
        // transform.localScale every frame; pause it so it can't fight the much
        // larger absorb scale, then hand scale ownership back once we're done.
        if (_pulseRoutine != null) { StopCoroutine(_pulseRoutine); _pulseRoutine = null; }

        Vector3 startScale = transform.localScale;
        Vector3 startWorldPos = transform.position;
        Transform vehicleTransform = vehicle != null ? vehicle.transform : null;

        if (vehicleTransform == null)
        {
            transform.position = getDestinationWorldPos();
            StopCollectableParticles();
            if (energySprite != null) _pulseRoutine = StartCoroutine(PulseEnergySprite());
            yield break;
        }

        Vector3 peakScale = startScale * absorbScalePeakMultiplier;
        if (energySprite != null)
        {
            Bounds vb = vehicle.GetVisualBounds();
            Bounds cb = energySprite.bounds;
            float vehicleDiameter = Mathf.Max(vb.size.x, vb.size.y);
            float collectableDiameter = Mathf.Max(cb.size.x, cb.size.y);
            if (vehicleDiameter > 0f && collectableDiameter > 0f)
                peakScale = startScale * (vehicleDiameter / collectableDiameter) * absorbScalePeakMultiplier;
        }

        // The prefab's particle systems all use Scaling Mode = Local, which reads only each
        // system's OWN transform.localScale and ignores ancestor scale — animating the root
        // alone is invisible on them. Drive each particle child's local scale in lockstep.
        var particleSystems = GetComponentsInChildren<ParticleSystem>();
        var particleBaseScales = new Vector3[particleSystems.Length];
        for (int i = 0; i < particleSystems.Length; i++)
            particleBaseScales[i] = particleSystems[i].transform.localScale;

        float peakRatio = Mathf.Abs(startScale.x) > 0.0001f ? peakScale.x / startScale.x : 1f;

        void ApplyParticleScale(float ratio)
        {
            for (int i = 0; i < particleSystems.Length; i++)
                if (particleSystems[i] != null)
                    particleSystems[i].transform.localScale = particleBaseScales[i] * ratio;
        }

        // Grow + move toward the vehicle's (live, moving) center — the "passing through" beat.
        float t = 0f;
        while (t < absorbGrowSeconds && vehicleTransform != null)
        {
            t += Time.deltaTime;
            float u = Mathf.SmoothStep(0f, 1f, t / absorbGrowSeconds);
            transform.localScale = Vector3.Lerp(startScale, peakScale, u);
            transform.position = Vector3.Lerp(startWorldPos, vehicleTransform.position, u);
            ApplyParticleScale(Mathf.Lerp(1f, peakRatio, u));
            yield return null;
        }
        transform.localScale = peakScale;
        if (vehicleTransform != null) transform.position = vehicleTransform.position;
        ApplyParticleScale(peakRatio);

        // Hold at peak (the "shield flash" beat), staying glued to the vehicle if it's still moving.
        float held = 0f;
        while (held < absorbHoldSeconds)
        {
            held += Time.deltaTime;
            if (vehicleTransform != null) transform.position = vehicleTransform.position;
            yield return null;
        }

        // Shrink + move on to the (live, moving) final destination.
        Vector3 shrinkStartPos = transform.position;
        t = 0f;
        while (t < absorbShrinkSeconds)
        {
            t += Time.deltaTime;
            float u = Mathf.SmoothStep(0f, 1f, t / absorbShrinkSeconds);
            transform.localScale = Vector3.Lerp(peakScale, startScale, u);
            transform.position = Vector3.Lerp(shrinkStartPos, getDestinationWorldPos(), u);
            ApplyParticleScale(Mathf.Lerp(peakRatio, 1f, u));
            yield return null;
        }
        transform.localScale = startScale;
        transform.position = getDestinationWorldPos();
        ApplyParticleScale(1f);

        StopCollectableParticles();
        if (energySprite != null) _pulseRoutine = StartCoroutine(PulseEnergySprite());
    }

    private void StopCollectableParticles()
    {
        if (TryGetComponent(out CollectableParticles cp))
            cp.Captured();
        else
            foreach (var ps in GetComponentsInChildren<ParticleSystem>())
                ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
    }

    private IEnumerator AbsorbThenTrailFollowRoutine(Vehicle vehicle)
    {
        // _trailWorldTarget is refreshed every frame elsewhere (Vehicle.TickNoteTrail), so
        // reading it live each frame here tracks the vehicle instead of a stale snapshot.
        yield return StartCoroutine(AbsorbIntoVehicleRoutine(vehicle, () => _trailWorldTarget));
        yield return StartCoroutine(TrailFollowRoutine());
    }

    public void BeginCarryThenDepositAtDsp(
        double depositDspTime,
        int durationTicks,
        float force,
        Action onArrived)
    {
        if (_carryRoutine != null) StopCoroutine(_carryRoutine);
        _carryRoutine = StartCoroutine(CarryAndDepositRoutine(depositDspTime, durationTicks, force, onArrived));
    }

    private IEnumerator CarryAndDepositRoutine(
        double depositDspTime,
        int durationTicks,
        float force,
        Action onArrived)
    {
        _inCarry = true;

        if (_rb != null) _rb.simulated = false;
        if (TryGetComponent(out Collider2D col)) col.enabled = false;

        if (_collector != null)
        {
            _carryParent = _collector;

            Vector3 jitter = (carrySettings.localOffsetJitter > 0f)
                ? (Vector3)(UnityEngine.Random.insideUnitCircle * carrySettings.localOffsetJitter)
                : Vector3.zero;
            Vector3 targetLocalPos = carrySettings.localOffset + jitter;

            _carryParent.TryGetComponent(out Vehicle vehicle);
            // TransformPoint is re-evaluated live each frame inside the routine (via this
            // closure) so the target tracks the vehicle's current position/rotation instead
            // of the point it happened to be at when the absorb effect started.
            yield return StartCoroutine(AbsorbIntoVehicleRoutine(vehicle, () => _carryParent.TransformPoint(targetLocalPos)));

            transform.SetParent(_carryParent, worldPositionStays: true);
            transform.localPosition = targetLocalPos;
            transform.localRotation = Quaternion.identity;
        }

        double dspNow = AudioSettings.dspTime;

        if (depositDspTime <= dspNow + 0.0005)
        {
            transform.SetParent(null, worldPositionStays: true);
            _carryParent = null;

            if (ribbonMarker) transform.position = ribbonMarker.position;
            onArrived?.Invoke();
            _inCarry = false;
            yield break;
        }

        float desiredTravel = Mathf.Clamp(depositTravelSeconds, minDepositTravelSeconds, maxDepositTravelSeconds);
        double timeUntilDeposit = depositDspTime - dspNow;
        float travelSeconds = Mathf.Clamp(desiredTravel, 0.02f, (float)timeUntilDeposit);
        double travelStartDsp = depositDspTime - travelSeconds;

        double minLaunch = dspNow + carryMinLeadSeconds;
        if (travelStartDsp < minLaunch)
            travelStartDsp = minLaunch;

        while (_carryParent != null && AudioSettings.dspTime < travelStartDsp)
            yield return null;

        transform.SetParent(null, worldPositionStays: true);
        _carryParent = null;

        yield return StartCoroutine(TravelRoutine(depositDspTime, durationTicks, force, onArrived));
        _inCarry = false;
    }

    private IEnumerator TravelRoutine(
        double depositDspTime,
        int durationTicks,
        float force,
        Action onArrived)
    {
        _inCarry = true;

        if (_rb != null) _rb.simulated = false;
        if (TryGetComponent(out Collider2D col)) col.enabled = false;

        double dspNow = AudioSettings.dspTime;

        if (depositDspTime <= dspNow + 0.00075)
        {
            if (ribbonMarker) transform.position = ribbonMarker.position;
            onArrived?.Invoke();
            _inCarry = false;
            yield break;
        }

        while (AudioSettings.dspTime < depositDspTime)
            yield return null;

        if (ribbonMarker) transform.position = ribbonMarker.position;
        onArrived?.Invoke();

        _inCarry = false;

        var ml = ribbonMarker ? ribbonMarker.GetComponent<MarkerLight>() : null;
        if (ml) ml.PulseOnly();

        ReportedCollected = true;
        NotifyDestroyedOnce();
        Destroy(gameObject);
    }

    private IEnumerator PulseEnergySprite()
    {
        Vector3 startScale = transform.localScale;
        while (true)
        {
            float t = (Mathf.Sin(Time.time * pulseSpeed) * 0.5f) + 0.5f;
            float a = Mathf.Lerp(minAlpha, maxAlpha, t);
            if (energySprite != null)
            {
                var col = energySprite.color; col.a = a; energySprite.color = col;
            }
            transform.localScale = startScale * Mathf.Lerp(1f, pulseScale, t);
            yield return null;
        }
    }

    public void OnManualReleaseDiscarded()
    {
        ReportedCollected = true;

        _trailFollowActive = false;
        if (_trailFollowRoutine != null) { StopCoroutine(_trailFollowRoutine); _trailFollowRoutine = null; }

        if (_carryRoutine != null) { StopCoroutine(_carryRoutine); _carryRoutine = null; }

        var ex = GetComponent<Explode>();
        if (ex != null)
        {
            ex.SetTint(new Color(1f, 0.2f, 0.2f), multiply: false);
            ex.Permanent(false);
        }

        if (energySprite != null)
            energySprite.color = new Color(1f, 0.2f, 0.2f, 0.85f);

        assignedInstrumentTrack?.spawnedCollectables?.Remove(gameObject);

        Destroy(gameObject);
    }

    public void OnManualReleaseConsumed()
    {
        _trailFollowActive = false;
        if (_trailFollowRoutine != null) { StopCoroutine(_trailFollowRoutine); _trailFollowRoutine = null; }

        if (_carryRoutine != null)
        {
            StopCoroutine(_carryRoutine);
            _carryRoutine = null;
        }

        assignedInstrumentTrack?.spawnedCollectables?.Remove(gameObject);

        Destroy(gameObject);
    }
}
