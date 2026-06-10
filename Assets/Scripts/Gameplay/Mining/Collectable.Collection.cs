using System;
using UnityEngine;

public partial class Collectable
{
    private static double EffectiveLoopStart(double transportStartDsp, double loopLen, double dspNow)
    {
        if (transportStartDsp <= 0.0 || loopLen <= 0.0) return 0.0;

        double loopsElapsed = Math.Floor((dspNow - transportStartDsp) / loopLen);
        double effective = transportStartDsp + loopsElapsed * loopLen;

        if (effective > dspNow) effective -= loopLen;

        return effective;
    }

    private static double NextDepositDspForBaseStep(
        double loopStartDsp,
        double loopLen,
        int baseSteps,
        int targetBaseStep,
        double dspNow,
        double eps = 0.010)
    {
        baseSteps = Mathf.Max(1, baseSteps);
        targetBaseStep = ((targetBaseStep % baseSteps) + baseSteps) % baseSteps;

        double loopsSince = (dspNow - loopStartDsp) / loopLen;
        long curLoopIndex = (long)Math.Floor(loopsSince);
        if (curLoopIndex < 0) curLoopIndex = 0;

        double tPos = (dspNow - loopStartDsp) - (curLoopIndex * loopLen);
        tPos %= loopLen;
        if (tPos < 0) tPos += loopLen;

        double stepDur = loopLen / baseSteps;

        int curStep = (int)Math.Floor(tPos / stepDur);
        curStep = Math.Clamp(curStep, 0, baseSteps - 1);

        long depositLoopIndex = curLoopIndex + ((targetBaseStep <= curStep) ? 1 : 0);

        double deposit = loopStartDsp + depositLoopIndex * loopLen + targetBaseStep * stepDur;

        while (deposit <= dspNow + eps)
            deposit += loopLen;

        return deposit;
    }

    private void OnTriggerEnter2D(Collider2D coll)
    {
        var vehicle = coll.GetComponent<Vehicle>();
        if (vehicle == null || _handled) return;

        if (!vehicle.CanAcceptCollectable(assignedInstrumentTrack)) return;

        _collector = vehicle.transform;

        if (assignedInstrumentTrack == null)
        {
            Debug.LogWarning($"[COLLECT] Ignored: assignedInstrumentTrack is null (name={name}).");
            return;
        }

        var drumTrack = assignedInstrumentTrack.drumTrack;
        if (drumTrack == null)
        {
            Debug.LogWarning($"[COLLECT] Ignored: track '{assignedInstrumentTrack.name}' has no DrumTrack yet (name={name}).");
            return;
        }

        double dspNow = AudioSettings.dspTime;

        double loopLen = drumTrack.GetLoopLengthInSeconds();
        if (loopLen <= 0.0)
        {
            Debug.LogWarning($"[COLLECT] Deferred: loopLen <= 0 (loopLen={loopLen:F6}) name={name}");
            return;
        }

        double transportStart = drumTrack.leaderStartDspTime;
        if (transportStart <= 0.0) transportStart = drumTrack.startDspTime;
        if (transportStart <= 0.0)
        {
            Debug.LogWarning($"[COLLECT] Deferred: drum transport not anchored yet. name={name}");
            return;
        }

        double loopStart = EffectiveLoopStart(transportStart, loopLen, dspNow);
        if (loopStart <= 0.0)
        {
            Debug.LogWarning($"[COLLECT] Deferred: effective loopStart invalid. name={name}");
            return;
        }

        _handled = true;

        if (_spawnArrivalRoutine != null)
        {
            StopCoroutine(_spawnArrivalRoutine);
            _spawnArrivalRoutine = null;
            if (_rb != null && _rb.bodyType == RigidbodyType2D.Kinematic)
                _rb.bodyType = RigidbodyType2D.Dynamic;
        }

        RegisterCarryOrbit();

        int baseSteps   = Mathf.Max(1, drumTrack.totalSteps);
        int leaderSteps = Mathf.Max(1, drumTrack.GetLeaderSteps());

        int mul = Mathf.Max(1, Mathf.RoundToInt(leaderSteps / (float)baseSteps));

        double tPos = dspNow - loopStart;
        if (tPos < 0) tPos = 0;
        if (tPos >= loopLen) tPos %= loopLen;

        double leaderStepDur = loopLen / leaderSteps;
        double timingWin = leaderStepDur * (drumTrack.timingWindowSteps * mul) * 0.5;

        int matchedStep = -1;
        double bestErr = timingWin;

        for (int i = 0; i < sharedTargetSteps.Count; i++)
        {
            int baseStep = sharedTargetSteps[i];
            int leaderStep = baseStep * mul;
            double stepPos = (leaderStep * leaderStepDur) % loopLen;

            double delta = Math.Abs(stepPos - tPos);
            if (delta > loopLen * 0.5) delta = loopLen - delta;

            if (delta < bestErr)
            {
                bestErr = delta;
                matchedStep = baseStep;
            }
        }

        float force = vehicle.GetForceAsMidiVelocity();
        vehicle.CollectEnergy(amount);
        int stepToReportBase =
            (matchedStep >= 0) ? matchedStep :
            (intendedStep >= 0) ? (((intendedStep % baseSteps) + baseSteps) % baseSteps) :
            0;
        assignedInstrumentTrack.PlayQuantizedNoteForStep(stepToReportBase, assignedNote, noteDurationTicks, force);
        float velocity127 = ComputeHitVelocity127(vehicle);
        OnCollected?.Invoke(noteDurationTicks, force);

        if (vehicle.ManualNoteReleaseEnabled)
        {
            HandleManualPickup(vehicle, stepToReportBase, velocity127);
            return;
        }

        HandleAutoDeposit(stepToReportBase, velocity127, force, baseSteps, drumTrack);
    }

    private void HandleManualPickup(Vehicle vehicle, int stepToReportBase, float velocity127)
    {
        assignedInstrumentTrack.OnCollectablePickedUpForManualRelease(vehicle, this, stepToReportBase, noteDurationTicks, velocity127);

        if (_rb == null) TryGetComponent(out _rb);
        if (_rb != null) _rb.simulated = false;
        if (TryGetComponent(out Collider2D c2d)) c2d.enabled = false;

        var ex = GetComponent<Explode>();
        if (ex != null) ex.Permanent(false);

        transform.SetParent(null, worldPositionStays: true);
        _trailWorldTarget = transform.position;
        _trailFollowActive = true;
        _trailReleasePulse01 = 0f;
        _trailDriftPhase = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
        if (!_trailBaseScaleCaptured) { _trailBaseScale = transform.localScale; _trailBaseScaleCaptured = true; }
        if (_trailFollowRoutine != null) StopCoroutine(_trailFollowRoutine);
        _trailFollowRoutine = StartCoroutine(TrailFollowRoutine());
    }

    private void HandleAutoDeposit(int stepToReportBase, float velocity127, float force, int baseSteps, DrumTrack drumTrack)
    {
        assignedInstrumentTrack.OnCollectableCollected(this, stepToReportBase, noteDurationTicks, velocity127);

        if (_rb == null) TryGetComponent(out _rb);
        if (_rb != null) _rb.simulated = false;
        if (TryGetComponent(out Collider2D col)) col.enabled = false;

        var explode = GetComponent<Explode>();
        if (explode != null) explode.Permanent(false);

        double dspNow = AudioSettings.dspTime;
        double loopLen = drumTrack.GetLoopLengthInSeconds();
        if (loopLen <= 0.0)
        {
            Debug.LogWarning($"[COLLECT] loopLen <= 0 while scheduling deposit; fallback travel. name={name}");
            BeginCarryThenDepositAtDsp(dspNow + minDepositTravelSeconds, noteDurationTicks, force, onArrived: null);
            return;
        }

        double transportStart = drumTrack.leaderStartDspTime;
        if (transportStart <= 0.0) transportStart = drumTrack.startDspTime;
        if (transportStart <= 0.0)
        {
            Debug.LogWarning($"[COLLECT] No valid transportStart; fallback travel. name={name}");
            BeginCarryThenDepositAtDsp(dspNow + minDepositTravelSeconds, noteDurationTicks, force, onArrived: null);
            return;
        }

        double loopStart = EffectiveLoopStart(transportStart, loopLen, dspNow);
        double depositDsp = NextDepositDspForBaseStep(loopStart, loopLen, baseSteps, stepToReportBase, dspNow);

        double minNeeded = minDepositTravelSeconds + 0.01;
        while ((depositDsp - dspNow) < minNeeded)
            depositDsp += loopLen;

        BeginCarryThenDepositAtDsp(depositDsp, noteDurationTicks, force, onArrived: null);
    }

    private float ComputeHitVelocity127(Vehicle vehicle)
    {
        if (vehicle == null) return 127f;

        var vRb = vehicle.rb;
        if (vRb == null) return 127f;

        Vector2 relVel = vRb.linearVelocity;
        if (_rb != null)
            relVel -= _rb.linearVelocity;

        Vector2 to = (Vector2)transform.position - vRb.position;
        float dist = to.magnitude;
        if (dist > 0.0001f) to /= dist;
        else to = Vector2.zero;

        float approachSpeed = Mathf.Max(0f, Vector2.Dot(relVel, to));
        float maxApproach = Mathf.Max(0.01f, vehicle.arcadeMaxSpeed * vehicle.HitVelocityMultiplier);

        float x = Mathf.Clamp01(approachSpeed / maxApproach);
        x = Mathf.Pow(x, 0.7f);

        return Mathf.Lerp(60f, 120f, x);
    }
}
