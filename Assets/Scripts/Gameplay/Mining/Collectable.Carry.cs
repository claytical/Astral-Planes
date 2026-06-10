using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public partial class Collectable
{
    private void RegisterCarryOrbit()
    {
        if (_collector == null || _registeredInCarryOrbit) return;

        if (!_carryOrbitByCollector.TryGetValue(_collector, out var list) || list == null)
        {
            list = new List<Collectable>();
            _carryOrbitByCollector[_collector] = list;
        }

        if (!list.Contains(this))
            list.Add(this);

        _registeredInCarryOrbit = true;

        if (TryGetComponent(out CollectableParticles cp))
            cp.Captured();

        RefreshCarryOrbitIndices(_collector);
    }

    private void UnregisterCarryOrbit()
    {
        if (_collector == null || !_registeredInCarryOrbit) return;

        if (_carryOrbitByCollector.TryGetValue(_collector, out var list) && list != null)
        {
            list.Remove(this);

            if (list.Count == 0)
                _carryOrbitByCollector.Remove(_collector);
            else
                RefreshCarryOrbitIndices(_collector);
        }

        _registeredInCarryOrbit = false;
        _carryOrbitIndex = -1;
    }

    private static void RefreshCarryOrbitIndices(Transform collector)
    {
        if (collector == null) return;
        if (!_carryOrbitByCollector.TryGetValue(collector, out var list) || list == null) return;

        for (int i = list.Count - 1; i >= 0; i--)
            if (list[i] == null) list.RemoveAt(i);

        for (int i = 0; i < list.Count; i++)
            if (list[i] != null) list[i]._carryOrbitIndex = i;
    }

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

        foreach (var ps in GetComponentsInChildren<ParticleSystem>())
            ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);

        if (_collector != null)
        {
            _carryParent = _collector;

            transform.SetParent(_carryParent, worldPositionStays: true);

            Vector3 jitter = (carrySettings.localOffsetJitter > 0f)
                ? (Vector3)(UnityEngine.Random.insideUnitCircle * carrySettings.localOffsetJitter)
                : Vector3.zero;

            transform.localPosition = carrySettings.localOffset + jitter;
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

        try { UnregisterCarryOrbit(); } catch {}

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

        try { UnregisterCarryOrbit(); } catch {}

        if (_carryRoutine != null)
        {
            StopCoroutine(_carryRoutine);
            _carryRoutine = null;
        }

        assignedInstrumentTrack?.spawnedCollectables?.Remove(gameObject);

        Destroy(gameObject);
    }
}
