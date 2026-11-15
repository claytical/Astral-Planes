using System;
using System.Collections;
using UnityEngine;

public partial class Collectable
{
    /// <summary>
    /// Sends the note toward a world-space target (e.g. an armed release flying to its marker).
    /// Releases any core lock.
    /// </summary>
    public void SetTrailTarget(Vector3 worldPos)
    {
        _trailWorldTarget = worldPos;
        _coreAnchor = null;
        _coreLocked = false;
    }

    /// <summary>
    /// Holds the note inside the vehicle's hollow core — rigidly locked to the anchor's
    /// position with no lag or drift, like fuel sitting in a tank.
    /// </summary>
    public void HoldInCore(Transform anchor)
    {
        if (_coreAnchor != anchor) _coreLocked = false;
        _coreAnchor = anchor;
    }

    private Vector3 CoreOrTrailTarget()
    {
        if (_coreAnchor == null) return _trailWorldTarget;
        Vector3 p = _coreAnchor.position;
        p.z = transform.position.z;
        return p;
    }

    private const float CoreLockSnapDistance = 0.02f;

    /// <summary>
    /// Drives the visual urgency of this note (0 = just collected, 1 = release imminent).
    /// </summary>
    public void SetReleasePulse(float pulse01) => _trailReleasePulse01 = Mathf.Clamp01(pulse01);

    private IEnumerator TrailFollowRoutine()
    {
        var particles = new ParticleScaleLockstep();
        particles.Capture(this);

        while (_trailFollowActive)
        {
            float dt = Time.deltaTime;
            float pulse = _trailReleasePulse01;

            if (_coreAnchor != null)
            {
                // Carried in the vehicle core: rigid lock, no drift. If we're re-entering the
                // core from afar (a cancelled armed release), ease back first, then hard-lock.
                Vector3 core = CoreOrTrailTarget();
                if (!_coreLocked && (transform.position - core).sqrMagnitude <= CoreLockSnapDistance * CoreLockSnapDistance)
                    _coreLocked = true;

                transform.position = _coreLocked
                    ? core
                    : Vector3.Lerp(transform.position, core, Mathf.Clamp01(trailFollowLerp * dt));
            }
            else
            {
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
            }

            if (_trailBaseScaleCaptured && energySprite != null)
            {
                float breathe = Mathf.Sin(Time.time * trailReadyPulseSpeed * (1f + pulse * 2f)) * 0.5f + 0.5f;
                float scaleTarget = Mathf.Lerp(trailIdleScaleMin, trailReadyScaleMax, pulse * breathe);
                transform.localScale = _trailBaseScale * scaleTarget;
                particles.Apply(scaleTarget);

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
        // PulseEnergySprite (and the note-play pulse, if one is mid-swell) runs for the
        // collectable's whole life and also drives transform.localScale every frame; pause
        // it so it can't fight the much larger absorb scale, then hand scale ownership back
        // once we're done.
        if (_pulseRoutine != null) { StopCoroutine(_pulseRoutine); _pulseRoutine = null; }
        if (_notePlayPulseRoutine != null) { StopCoroutine(_notePlayPulseRoutine); _notePlayPulseRoutine = null; }

        Vector3 startScale = _baseRootScale;
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
        var particles = new ParticleScaleLockstep();
        particles.Capture(this);

        float peakRatio = Mathf.Abs(startScale.x) > 0.0001f ? peakScale.x / startScale.x : 1f;

        // Grow + move toward the vehicle's (live, moving) center — the "passing through" beat.
        float t = 0f;
        while (t < absorbGrowSeconds && vehicleTransform != null)
        {
            t += Time.deltaTime;
            float u = Mathf.SmoothStep(0f, 1f, t / absorbGrowSeconds);
            transform.localScale = Vector3.Lerp(startScale, peakScale, u);
            transform.position = Vector3.Lerp(startWorldPos, vehicleTransform.position, u);
            particles.Apply(Mathf.Lerp(1f, peakRatio, u));
            yield return null;
        }
        transform.localScale = peakScale;
        if (vehicleTransform != null) transform.position = vehicleTransform.position;
        particles.Apply(peakRatio);

        // Hold at peak (the "shield flash" beat), staying glued to the vehicle if it's still moving.
        // The sprite snaps to lockFlashColor at the instant of capture, then settles into a
        // tinted "armed" color (between the role color and the flash color) rather than fully
        // reverting — this lingering tint is the whole-carry "captured" signature, since the
        // note is destroyed at commit and nothing else ever touches its RGB again.
        Color roleColor = energySprite != null ? energySprite.color : Color.white;
        Color armedColor = Color.Lerp(roleColor, lockFlashColor, capturedTintAmount);
        if (energySprite != null) energySprite.color = lockFlashColor;

        float held = 0f;
        while (held < absorbHoldSeconds)
        {
            held += Time.deltaTime;
            if (vehicleTransform != null) transform.position = vehicleTransform.position;

            if (energySprite != null && lockFlashSeconds > 0f)
            {
                float flashT = Mathf.Clamp01(held / lockFlashSeconds);
                energySprite.color = Color.Lerp(lockFlashColor, armedColor, flashT);
            }

            yield return null;
        }
        if (energySprite != null) energySprite.color = armedColor;

        // Shrink + move on to the (live, moving) final destination.
        Vector3 shrinkStartPos = transform.position;
        t = 0f;
        while (t < absorbShrinkSeconds)
        {
            t += Time.deltaTime;
            float u = Mathf.SmoothStep(0f, 1f, t / absorbShrinkSeconds);
            transform.localScale = Vector3.Lerp(peakScale, startScale, u);
            transform.position = Vector3.Lerp(shrinkStartPos, getDestinationWorldPos(), u);
            particles.Apply(Mathf.Lerp(peakRatio, 1f, u));
            yield return null;
        }
        transform.localScale = startScale;
        transform.position = getDestinationWorldPos();
        particles.Apply(1f);

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
        // Destination is read live each frame: the vehicle core while carried, or the target
        // Vehicle.TickNoteTrail refreshes (e.g. an armed release's marker).
        yield return StartCoroutine(AbsorbIntoVehicleRoutine(vehicle, CoreOrTrailTarget));
        // The absorb ends exactly on the core, so start hard-locked if still carried.
        _coreLocked = _coreAnchor != null;
        yield return StartCoroutine(TrailFollowRoutine());
    }

    public void BeginCarryThenDepositAtDsp(
        double depositDspTime,
        int durationTicks,
        float force,
        Action onArrived)
    {
        _s_inDepositFlight.Add(this); // removed in OnDestroy (every deposit path ends in Destroy)
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

            _carryParent.TryGetComponent(out Vehicle vehicle);

            // Carried in the vehicle's hollow core (its NoteCoreAnchor, or the pivot if none is
            // authored) — rigid, no offset or jitter. The anchor is a child, so its offset in the
            // vehicle's local space is constant.
            Vector3 targetLocalPos = vehicle != null
                ? _carryParent.InverseTransformPoint(vehicle.NoteCoreAnchor.position)
                : Vector3.zero;
            targetLocalPos.z = 0f;
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

            ReportedCollected = true;
            NotifyDestroyedOnce();
            Destroy(gameObject);
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

            ReportedCollected = true;
            NotifyDestroyedOnce();
            Destroy(gameObject);
            yield break;
        }

        // Fly from the release point to the (live) marker position, arriving exactly at depositDsp.
        Vector3 startPos = transform.position;
        double startDsp = dspNow;
        double span = Math.Max(0.0001, depositDspTime - startDsp);

        double nowDsp;
        while ((nowDsp = AudioSettings.dspTime) < depositDspTime)
        {
            if (ribbonMarker)
            {
                float t = Mathf.Clamp01((float)((nowDsp - startDsp) / span));
                float eased = t * t * (3f - 2f * t); // smoothstep
                transform.position = Vector3.Lerp(startPos, ribbonMarker.position, eased);
            }
            yield return null;
        }

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
        var particles = new ParticleScaleLockstep();
        particles.Capture(this);

        while (true)
        {
            float t = (Mathf.Sin(Time.time * pulseSpeed) * 0.5f) + 0.5f;
            float a = Mathf.Lerp(minAlpha, maxAlpha, t);
            if (energySprite != null)
            {
                var col = energySprite.color; col.a = a; energySprite.color = col;
            }
            float ratio = Mathf.Lerp(1f, pulseScale, t);
            transform.localScale = _baseRootScale * ratio;
            particles.Apply(ratio);
            yield return null;
        }
    }

    public void OnManualReleaseDiscarded(int authoredAbsStep)
    {
        ReportedCollected = true;

        _trailFollowActive = false;
        if (_trailFollowRoutine != null) { StopCoroutine(_trailFollowRoutine); _trailFollowRoutine = null; }

        if (_carryRoutine != null) { StopCoroutine(_carryRoutine); _carryRoutine = null; }

        ArmAsTimeBomb(authoredAbsStep);
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
