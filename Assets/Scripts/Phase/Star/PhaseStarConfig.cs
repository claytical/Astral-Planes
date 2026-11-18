using System;
using UnityEngine;

/// <summary>
/// Everything about the PhaseStar that is the same for every star: charge display, look,
/// motion, targeting and the drain tentacles/vines. One asset (Resources/Config/PhaseStarConfig),
/// referenced by the PhaseStar, PhaseStarMotion2D, PhaseStarVisuals2D, PhaseStarCravingNavigator
/// and PhaseStarDustAffect components on Star.prefab.
///
/// What differs per star type (drift/hungry speed, keep-clear radius, ejection prefab) lives on
/// PhaseStarBehaviorProfile (the motif's starBehavior). Per-maze steering lives on
/// MazePatternConfig → StarNavigationParams.
/// </summary>
[CreateAssetMenu(fileName = "PhaseStarConfig", menuName = "Astral Planes/Phase & Star/PhaseStar Config (all stars)")]
public class PhaseStarConfig : ScriptableObject
{
    private const string ResourcesPath = "Config/PhaseStarConfig";
    private static PhaseStarConfig s_default;

    /// <summary>The assigned config, else Resources/Config/PhaseStarConfig, else field defaults. Never null.</summary>
    public static PhaseStarConfig OrDefault(PhaseStarConfig assigned, UnityEngine.Object context)
    {
        if (assigned != null) return assigned;
        if (s_default == null)
        {
            s_default = Resources.Load<PhaseStarConfig>(ResourcesPath);
            if (s_default == null)
            {
                Debug.LogError($"[PhaseStarConfig] {(context ? context.name : "?")}: no config assigned and none at Resources/{ResourcesPath}; using field defaults.");
                s_default = CreateInstance<PhaseStarConfig>();
                s_default.hideFlags = HideFlags.HideAndDontSave;
            }
        }
        return s_default;
    }

    // ───────────────────────── 1. Charge & shards (PhaseStar) ─────────────────────────

    [Header("1. Charge & Shards")]
    [Tooltip("Delay after the star becomes visible (dormant reveal) before tentacles begin hunting/growing.")]
    [Min(0f)] public float tentacleActivationDelay = 0.5f;
    [Tooltip("Minimum alpha for a shard with zero charge — keeps it ghost-visible.")]
    [Range(0f, 0.5f)] public float shardMinAlpha = 0.5f;
    [Tooltip("Lerp speed for the visual charge fill (units/sec in 0-1 space). Lower = slower, smoother rise from empty to full.")]
    [Min(0.1f)] public float chargeDisplayLerpSpeed = 2.5f;
    [Tooltip("Minimum diamond scale while tentacles are actively drawing/draining, so shards bloom out of the particle field before charge is visible.")]
    [Range(0f, 1f)] public float tentacleBloomMinScale = 0.22f;
    [Tooltip("Baseline star seed scale shown during dormant dust-calling so the particle force exists before tentacles finish charging.")]
    [Range(0f, 0.5f)] public float dormantSeedScale = 0.08f;
    [Tooltip("Accumulator diamond spin (deg/s). It turns opposite to the scout diamond.")]
    [Range(10f, 360f)] public float accumulatorRotSpeed = 90f;
    [Tooltip("Max tilt (± degrees) of the accumulator diamond while draining.")]
    public float drainTiltDeg = 5f;
    [Tooltip("Drain tilt oscillation (cycles/s).")]
    public float drainTiltSpeed = 1.8f;
    [Tooltip("A ship bumping a disarmed star pushes it at (ship impact speed × this).")]
    [Min(0f)] public float disarmedPushScale = 0.1f;

    // ───────────────────────── 2. Look (PhaseStarVisuals2D) ─────────────────────────

    [Header("2. Look")]
    [Tooltip("Seconds for the applied visual angle to settle to a new target angle.")]
    [Min(0.01f)] public float diamondAngleSmoothTime = 0.06f;
    [Tooltip("Max angular separation between the two counter-rotating diamonds at zero charge.")]
    public float dualMaxSeparationDeg = 45f;
    [Tooltip("Shard tint at zero charge (gray) and the alpha used while dim/hidden. Charging lerps from this to the role color.")]
    public Color dimShardTint = new Color(0.3584906f, 0.3584906f, 0.3584906f, 0.6431373f);
    [Tooltip("Disarmed look: RGB multiplier applied to the dim color (lower = darker).")]
    [Range(0f, 1f)] public float dimRgbMul = 0.08f;
    [Tooltip("Disarmed look: on = darken Dim Shard Tint; off = darken the role color.")]
    public bool dimUsesFixedTint = false;

    [Header("2. Look — poke not ready (head shake)")]
    [Min(0.05f)] public float notReadyShakeSeconds = 0.35f;
    [Min(0f)] public float notReadyShakeDeg = 18f;
    [Min(0.5f)] public float notReadyShakeHz = 7f;

    [Header("2. Look — sleeping (blocked by another star's sequence)")]
    [Range(0f, 1f)] public float sleepDesaturate01 = 0.65f;
    [Range(0f, 1f)] public float sleepAlphaMul = 0.6f;

    // ───────────────────────── 3. Motion (PhaseStarMotion2D) ─────────────────────────

    [Serializable]
    public sealed class ScreenBoundsTuning
    {
        [Tooltip("Kinematic body (moves through physics instead of being pushed).")]
        public bool kinematicMode = false;
        [Tooltip("World-unit inset from the screen edge the star is kept inside.")]
        public float screenPadding = 0.5f;
    }

    [Serializable]
    public sealed class VehicleAvoidanceTuning
    {
        [Tooltip("Ships within this radius push the star's steering away.")]
        public float radius = 3.0f;
        [Tooltip("Falloff exponent of the avoidance push (higher = only very close ships matter).")]
        public float exponent = 2.0f;
        [Tooltip("Overall avoidance strength.")]
        public float strength = 1.0f;
        [Tooltip("Avoidance weight while hunting dust (multiplies Strength).")]
        [Range(0f, 2f)] public float huntWeight = 1.0f;
    }

    [Serializable]
    public sealed class SteeringTuning
    {
        [Tooltip("Steering acceleration while drifting.")]
        public float maxAccel = 4.0f;
        [Tooltip("Steering acceleration while hunting dust.")]
        public float huntAccel = 18.0f;
    }

    [Serializable]
    public sealed class NavigatorBlendTuning
    {
        [Tooltip("How much the sniffer's nearest-colored-dust direction bends the drift heading.")]
        [Range(0f, 1f)] public float snifferWeight = 0.734f;
    }

    [Serializable]
    public sealed class GhostDriftTuning
    {
        [Tooltip("Slow wander speed while dormant (world units/s).")]
        public float driftSpeed = 0.35f;
        [Tooltip("How quickly the star eases into its wander velocity.")]
        public float driftAccel = 1.5f;
        [Tooltip("Speed the star is flung away at when a ship passes through it.")]
        public float scatterSpeed = 4f;
        [Tooltip("How much of the ship's own heading the scatter inherits (0 = straight away from the ship).")]
        [Range(0f, 1f)] public float scatterFollowsShip = 0.5f;
        [Tooltip("Turbulence strength handed to the ship while it overlaps the star (0..1).")]
        [Range(0f, 1f)] public float turbulence01 = 0.8f;
        [Tooltip("How long the turbulence lingers after the ship leaves the star.")]
        public float turbulenceSeconds = 0.6f;
    }

    [Header("3. Motion")]
    public ScreenBoundsTuning bounds = new();
    public VehicleAvoidanceTuning avoidance = new();
    public SteeringTuning steering = new();
    public NavigatorBlendTuning navBlend = new();
    [Tooltip("Dormant (charging) stars are ghosts: ships pass through, get buffeted, and scatter the star.")]
    public GhostDriftTuning ghost = new();
    [Tooltip("How fast a shove from a ship decays (world units/s^2).")]
    [Min(0f)] public float pushDecayPerSecond = 5f;
    [Tooltip("While frozen (dormant charging, paused for a sibling) the star doesn't steer, but stays " +
             "pushable so a ship can never be pinned against it. A shove decays back to rest at this rate (world units/s^2).")]
    [Min(0f)] public float frozenSettleDeceleration = 20f;

    [Header("3. Motion — screen edge & anti-stuck")]
    [Tooltip("Snap the star back inside if it overshoots the screen bounds by more than this (world units).")]
    [Min(0f)] public float hardClampOvershootWorld = 1.0f;
    [Tooltip("How quickly velocity is damped back inside near the screen edge.")]
    [Min(0f)] public float softEdgeVelocityDamp = 8f;
    [Tooltip("Kick the star when it hasn't moved for Stuck Threshold Seconds.")]
    public bool enableUnstickImpulse = false;
    [Min(0.05f)] public float stuckThresholdSeconds = 0.22f;
    [Min(0f)] public float unstickImpulseSpeed = 1f;

    // ───────────────────────── 4. Targeting (PhaseStarCravingNavigator) ─────────────────────────

    [Header("4. Targeting")]
    [Tooltip("Fallback replan interval while hunting, used only when the motif has no PhaseStarBehaviorProfile " +
             "(normally PhaseStarBehaviorProfile.mazeNavReplanInterval × the maze's replan multiplier).")]
    public float replanIntervalSeconds = 0.5f;
    [Tooltip("If the current cell becomes invalid, immediately reacquire a new target.")]
    public bool retargetOnTargetLost = true;
    [Tooltip("How often ambient dust direction is rescanned when not lock-zapping.")]
    public float snifferIntervalSeconds = 2f;
    [Tooltip("When no colored dust is reachable, wander to an open cell at least this many steps away.")]
    [Min(1)] public int wanderMinHops = 6;

    // ───────────────────────── 5. Tentacles & vines (PhaseStarDustAffect) ─────────────────────────

    [Header("5. Tentacles — zap cadence")]
    [Tooltip("Hold time on target before a zap is committed.")]
    public float minContactTime = 1f;
    [Tooltip("Blend duration from seek colors into active drain colors.")]
    public float colorTransitionTime = 0.75f;
    [Tooltip("Optional pacing between successive tentacle activations. 0 = every available carved cell grows a tentacle immediately, up to the node payload; > 0 = one activation per interval.")]
    [Min(0f)] public float interTentacleStartInterval = 0f;
    [Tooltip("Tentacle count used only when the star can't report its desired count.")]
    [Min(1)] public int fallbackTentaclesPerRole = 8;
    [Tooltip("Frames a tentacle tolerates an invalid target before re-targeting.")]
    [Min(1)] public int invalidTargetRetryFrames = 3;
    [Tooltip("Milliseconds a tentacle tolerates an invalid target before re-targeting.")]
    [Min(0f)] public float invalidTargetRetryMs = 120f;

    [Header("5. Tentacles — drain polish")]
    [Tooltip("Anticipation window after contact: the cell shudders/brightens before the drain fires. 0 = instant chip.")]
    [Min(0f)] public float drainBuildupSeconds = 0.35f;
    [Tooltip("Travel time of the siphon energy packet from tentacle tip to the star root.")]
    [Min(0.05f)] public float siphonTravelSeconds = 0.5f;
    [Tooltip("How far the siphon packet color is pushed toward white.")]
    [Range(0f, 1f)] public float siphonBrightness = 0.85f;

    [Header("5. Tentacles — motion")]
    [Tooltip("Tentacle extension speed (progress per second, 0-1 range).")]
    public float tentacleGrowSpeed = 1.5f;
    [Tooltip("Retract speed after zap resolve or target loss.")]
    public float tentacleRetractSpeed = 4f;
    [Tooltip("Speed the color gradient flows along the tentacle.")]
    public float tentacleFlowSpeed = 0.5f;
    [Tooltip("Seconds a tentacle takes to dissolve.")]
    public float dissolveDuration = 1f;

    [Header("5. Tentacles — line look")]
    public float tentacleWidth = 0.15f;
    [Range(0f, 1f)] public float growingRootAlpha = 0.669f;
    [Range(0f, 1f)] public float growingTipAlpha = 0.554f;
    [Range(0f, 1f)] public float drainingBaseAlpha = 0.751f;
    [Tooltip("Sideways wobble amplitude (× cell size, capped).")]
    public float noiseAmp = 0.15f;
    public float noiseSpeed = 1.5f;
    public float weaveAmplitude = 0.15f;
    [Min(0.01f)] public float weaveWavelength = 2f;
    public float weaveSpeed = 1.01f;

    [Header("5. Tentacles — shimmer particles")]
    public bool shimmerEnabled = true;
    public float shimmerRatePerUnit = 5f;
    public float shimmerLifetime = 0.35f;
    public float shimmerSize = 0.04f;
    public float shimmerAlpha = 0.45f;

    [Header("5. Tentacles — vine routing")]
    [Tooltip("Pause at each dust cell the vine has to break through (no open route to the target).")]
    [Min(0f)] public float boreSeconds = 0.25f;
    [Tooltip("Fade time of a dust cell broken by a vine (regrows normally, as StarBurrow).")]
    [Min(0.01f)] public float boreFadeSeconds = 0.15f;
    [Tooltip("A blocking cell still solid after this long (Cosmic Block / uncarveable) makes the vine give up and retract.")]
    [Min(0.1f)] public float boreTimeout = 1.0f;
    [Tooltip("How quickly the drawn vine catches up to its route when the star is bumped. Lower = looser, more whip.")]
    [Min(0.5f)] public float vineFollowSharpness = 10f;
    [Tooltip("Curviness: sideways bend offset as a fraction of the run between bends. 0 = straight runs between corners.")]
    [Range(0f, 1f)] public float vineMeander = 0.35f;
    [Tooltip("Max sideways offset of a bend, in cells (bends are also shrunk to stay off dust).")]
    [Min(0f)] public float vineMeanderMaxCells = 1.25f;
    [Tooltip("Roughly one bend per this many cells of vine length. Lower = wigglier.")]
    [Min(1f)] public float vineMeanderSpacingCells = 2.5f;
    [Tooltip("Star within this many cells of a root breadcrumb → the vine shortens to it (pushed back along itself).")]
    [Min(0.1f)] public float vineRootSlackCells = 0.5f;
}
