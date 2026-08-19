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

    [Header("Discard → Time Bomb")]
    public BombParams bomb = new();

    public void Validate()
    {
        depositTiming ??= new DepositTimingParams();
        absorbVfx ??= new AbsorbVfxParams();
        idlePulse ??= new IdlePulseParams();
        noteTrail ??= new NoteTrailParams();
        dustCollision ??= new DustCollisionParams();
        baseMotion ??= new BaseMotionParams();
        bomb ??= new BombParams();

        depositTiming.Validate();
        absorbVfx.Validate();
        dustCollision.Validate();
        bomb.Validate();
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
    public float absorbGrowSeconds = 0.05f;

    [Tooltip("Pause at peak scale before shrinking back down (seconds).")]
    public float absorbHoldSeconds = 0.5f;

    [Tooltip("Duration of the scale-down/move-to-destination phase (seconds).")]
    public float absorbShrinkSeconds = 0.16f;

    [Tooltip("Extra multiplier applied on top of the vehicle-matched peak scale (1 = exact match).")]
    public float absorbScalePeakMultiplier = 1.1f;

    [Tooltip("Color the collectable snaps to at the peak-hold instant — the 'locked in' flash. Settles toward a tinted armed color (see capturedTintAmount) over lockFlashSeconds, not fully back to the role color, so 'captured' reads as visibly distinct from 'wild' for the rest of the carry.")]
    public Color lockFlashColor = Color.white;

    [Tooltip("Seconds over which the lock-flash color settles into the armed carry tint, at the start of the peak hold.")]
    public float lockFlashSeconds = 0.12f;

    [Tooltip("How far the carried note's persistent color sits between the role color (0) and lockFlashColor (1) once armed. This is the 'captured/armed' state's lingering signature, distinguishing it from the pre-pickup role color — the note is destroyed at commit, so nothing reverts it.")]
    [Range(0f, 1f)] public float capturedTintAmount = 0.35f;

    public void Validate()
    {
        absorbGrowSeconds = Mathf.Max(0.001f, absorbGrowSeconds);
        absorbHoldSeconds = Mathf.Max(0f, absorbHoldSeconds);
        absorbShrinkSeconds = Mathf.Max(0.001f, absorbShrinkSeconds);
        lockFlashSeconds = Mathf.Clamp(lockFlashSeconds, 0f, absorbHoldSeconds);
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
    public float trailFollowLerp = 16f;

    [Tooltip("Scale pulse min when idle in trail.")]
    public float trailIdleScaleMin = 0.85f;

    [Tooltip("Scale pulse max at full release-ready glow.")]
    public float trailReadyScaleMax = 1.25f;

    [Tooltip("Glow pulse speed (radians/sec) when release window is near.")]
    public float trailReadyPulseSpeed = 6f;

    [Tooltip("Max world-space radius of idle drift orbit around the slot position.")]
    public float trailDriftRadius = 0.1f;

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

[Serializable]
public sealed class BombParams
{
    [Tooltip("World-unit radius scanned for vehicles at detonation.")]
    public float blastRadius = 5f;

    [Tooltip("Base drain, as a multiplier of the note's own processed energy value (Collectable.amount).")]
    public float drainMultiplier = 2f;

    [Tooltip("Fraction of full drain dealt at the edge of the blast radius (1 = no falloff, full damage everywhere in range).")]
    [Range(0f, 1f)] public float edgeDamageFraction = 0.25f;

    [Tooltip("Minimum seconds between discard and detonation, regardless of how close the encoded step already was.")]
    public float minFuseSeconds = 0.75f;

    [Tooltip("Seconds after arming during which a vehicle bump is ignored instead of triggering early detonation. While carried, the note's Rigidbody2D is not simulated, so it can end up positioned overlapping the discarding vehicle with no collision detected; ArmAsTimeBomb re-enables simulation immediately, and without this grace window that overlap reads as a brand-new bump the instant the bomb is armed. Physics still naturally separates the (Kinematic bomb, Dynamic vehicle) pair during this window.")]
    public float earlyDetonationGraceSeconds = 0.25f;

    [Tooltip("Pulse frequency (Hz) at the start of the fuse.")]
    public float minPulseHz = 0.6f;

    [Tooltip("Pulse frequency (Hz) right before detonation.")]
    public float maxPulseHz = 4f;

    [Tooltip("Peak scale multiplier the collectable (and its particle systems, kept in lockstep) swells to right before detonation. 2 = about twice its normal size.")]
    public float maxPulseScale = 2f;

    [Tooltip("Color the collectable and its core particle system tint toward as the fuse burns down, and flash to at the moment of detonation.")]
    public Color warningColor = new Color(1f, 0.2f, 0.2f, 0.85f);

    [Tooltip("Seconds the detonation flash takes: right after damage is dealt, the collectable (and its particle systems) scale up from wherever the countdown swell left them to a size matching the real blastRadius, before the implosion collapse begins.")]
    public float detonationFlashSeconds = 0.15f;

    [Tooltip("Seconds the post-detonation collapse takes: the flashed-out collectable (and its particle systems) shrink back to nothing together before being destroyed.")]
    public float implosionSeconds = 0.25f;

    [Tooltip("Peak alpha the blast-radius telegraph ring reaches at fuse end. The ring is sized to the true blast radius throughout the fuse (unlike the body/particle swell, which is a fixed cosmetic multiplier), so this is the player's actual read on the danger zone.")]
    [Range(0f, 1f)] public float telegraphMaxAlpha = 0.35f;

    [Tooltip("Pulse frequency (Hz) of the telegraph ring — independent of the body's ramping pulse.")]
    public float telegraphPulseHz = 1.5f;

    [Tooltip("Minimum scale multiplier of the ring's pulse. Max is always exactly 1.0 (true radius), so the ring never overstates the real danger zone.")]
    [Range(0.5f, 1f)] public float telegraphPulseMin = 0.9f;

    [Tooltip("Impulse magnitude (Rigidbody2D.AddForce, ForceMode2D.Impulse) applied to each vehicle in the blast radius at detonation, directed away from the blast center. Mass-respecting.")]
    public float knockbackImpulse = 6f;

    [Tooltip("Fraction of full knockback dealt at the edge of the blast radius (same linear falloff shape as edgeDamageFraction).")]
    [Range(0f, 1f)] public float knockbackEdgeFraction = 0.2f;

    public void Validate()
    {
        blastRadius = Mathf.Max(0f, blastRadius);
        drainMultiplier = Mathf.Max(0f, drainMultiplier);
        minFuseSeconds = Mathf.Max(0.05f, minFuseSeconds);
        earlyDetonationGraceSeconds = Mathf.Max(0f, earlyDetonationGraceSeconds);
        minPulseHz = Mathf.Max(0.01f, minPulseHz);
        maxPulseHz = Mathf.Max(minPulseHz, maxPulseHz);
        maxPulseScale = Mathf.Max(1f, maxPulseScale);
        detonationFlashSeconds = Mathf.Max(0.01f, detonationFlashSeconds);
        implosionSeconds = Mathf.Max(0.01f, implosionSeconds);
        telegraphMaxAlpha = Mathf.Clamp01(telegraphMaxAlpha);
        telegraphPulseHz = Mathf.Max(0.01f, telegraphPulseHz);
        telegraphPulseMin = Mathf.Clamp(telegraphPulseMin, 0.5f, 1f);
        knockbackImpulse = Mathf.Max(0f, knockbackImpulse);
        knockbackEdgeFraction = Mathf.Clamp01(knockbackEdgeFraction);
    }
}
