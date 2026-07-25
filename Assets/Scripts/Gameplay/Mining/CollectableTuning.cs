using System;
using UnityEngine;

[CreateAssetMenu(fileName = "CollectableTuning", menuName = "Astral Planes/Collectable Tuning")]
public class CollectableTuning : ScriptableObject
{
    [Header("Base Motion")]
    public BaseMotionParams baseMotion = new();

    [Header("Deposit Timing")]
    public DepositTimingParams depositTiming = new();

    [Header("Absorb Shield Effect")]
    public AbsorbVfxParams absorbVfx = new();

    [Header("Idle Pulse")]
    public IdlePulseParams idlePulse = new();

    [Header("Note Trail (Manual Release + Drift)")]
    public NoteTrailParams noteTrail = new();

    [Header("Dust Collision")]
    public DustCollisionParams dustCollision = new();

    public void Validate()
    {
        depositTiming ??= new DepositTimingParams();
        absorbVfx ??= new AbsorbVfxParams();
        idlePulse ??= new IdlePulseParams();
        noteTrail ??= new NoteTrailParams();
        dustCollision ??= new DustCollisionParams();
        baseMotion ??= new BaseMotionParams();

        depositTiming.Validate();
        absorbVfx.Validate();
        dustCollision.Validate();
    }

    private void OnValidate() => Validate();
}

[Serializable]
public sealed class BaseMotionParams
{
    [Tooltip("World units/sec of linear drift at multiplier 1.0 (base speed before role multiplier and MIDI-velocity modulation).")]
    public float baseLinearSpeed = 2.4f;
}

[Serializable]
public sealed class DepositTimingParams
{
    [Tooltip("How long the tether travel should take under normal circumstances (seconds).")]
    public float depositTravelSeconds = 1.6f;

    [Tooltip("Hard minimum travel time, unless the deposit moment is sooner than this.")]
    public float minDepositTravelSeconds = 1.0f;

    [Tooltip("Hard maximum travel time (prevents overly slow zips).")]
    public float maxDepositTravelSeconds = 2.2f;

    [Tooltip("Minimum lead time so travel doesn't start 'late' due to frame jitter.")]
    public float carryMinLeadSeconds = 0.02f;

    public void Validate()
    {
        minDepositTravelSeconds = Mathf.Max(0.01f, minDepositTravelSeconds);
        maxDepositTravelSeconds = Mathf.Max(minDepositTravelSeconds, maxDepositTravelSeconds);
        depositTravelSeconds = Mathf.Clamp(depositTravelSeconds, minDepositTravelSeconds, maxDepositTravelSeconds);
        carryMinLeadSeconds = Mathf.Max(0f, carryMinLeadSeconds);
    }
}

[Serializable]
public sealed class AbsorbVfxParams
{
    [Tooltip("Duration of the scale-up/move-toward-vehicle phase (seconds).")]
    public float absorbGrowSeconds = 0.08f;

    [Tooltip("Pause at peak scale before shrinking back down (seconds).")]
    public float absorbHoldSeconds = 0.5f;

    [Tooltip("Duration of the scale-down/move-to-destination phase (seconds).")]
    public float absorbShrinkSeconds = 0.1f;

    [Tooltip("Extra multiplier applied on top of the vehicle-matched peak scale (1 = exact match).")]
    public float absorbScalePeakMultiplier = 1.1f;

    public void Validate()
    {
        absorbGrowSeconds = Mathf.Max(0.001f, absorbGrowSeconds);
        absorbHoldSeconds = Mathf.Max(0f, absorbHoldSeconds);
        absorbShrinkSeconds = Mathf.Max(0.001f, absorbShrinkSeconds);
    }
}

[Serializable]
public sealed class IdlePulseParams
{
    public float pulseSpeed = 1.6f;
    public float minAlpha = 0.25f;
    public float maxAlpha = 0.65f;
    public float pulseScale = 1.1f;
}

[Serializable]
public sealed class NoteTrailParams
{
    [Tooltip("How quickly the note lerps toward its trail slot position (world space).")]
    public float trailFollowLerp = 12f;

    [Tooltip("Scale pulse min when idle in trail.")]
    public float trailIdleScaleMin = 0.85f;

    [Tooltip("Scale pulse max at full release-ready glow.")]
    public float trailReadyScaleMax = 1.25f;

    [Tooltip("Glow pulse speed (radians/sec) when release window is near.")]
    public float trailReadyPulseSpeed = 6f;

    [Tooltip("Max world-space radius of idle drift orbit around the slot position.")]
    public float trailDriftRadius = 0.18f;

    [Tooltip("Speed of the drift orbit (radians/sec). Each collectable gets a random phase.")]
    public float trailDriftSpeed = 1.1f;

    [Tooltip("Drift radius shrinks to this fraction as release pulse approaches 1 (energy focuses).")]
    public float trailDriftFocusMul = 0.15f;
}

[Serializable]
public sealed class DustCollisionParams
{
    public float dustCollisionEnterImpulse = 0.85f;
    public float dustCollisionStayForce = 2.25f;

    [Tooltip("Fade time for dust cells carved along the arrival flight path.")]
    public float arrivalCarveFadeSeconds = 0.15f;

    public void Validate()
    {
        dustCollisionEnterImpulse = Mathf.Max(0f, dustCollisionEnterImpulse);
        dustCollisionStayForce = Mathf.Max(0f, dustCollisionStayForce);
        arrivalCarveFadeSeconds = Mathf.Max(0f, arrivalCarveFadeSeconds);
    }
}
