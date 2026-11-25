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
    [Tooltip("Lerp speed for the visual charge fill (units/sec in 0-1 space). Lower = slower, smoother rise from empty to full.")]
    [Min(0.1f)] public float chargeDisplayLerpSpeed = 2.5f;
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
        // Dormant movement itself is the swim (SwimTuning); this only covers ship pass-through.
        [Tooltip("Push-away acceleration (world units/s²) for a star still overlapping a ship as it stops being a ghost (armed). Contact while charging is the soft bump (swim → Ship bump).")]
        public float scatterSpeed = 1.5f;
        [Tooltip("How much of the ship's own heading the scatter inherits (0 = straight away from the ship).")]
        [Range(0f, 1f)] public float scatterFollowsShip = 0.5f;
        [Tooltip("Turbulence strength handed to the ship while it overlaps the star (0..1).")]
        [Range(0f, 1f)] public float turbulence01 = 0.15f;
        [Tooltip("How long the turbulence lingers after the ship leaves the star.")]
        public float turbulenceSeconds = 0.6f;
    }

    /// <summary>One role's swim rhythm: the star strokes on these drum steps.</summary>
    [Serializable]
    public sealed class RoleSwimStyle
    {
        public MusicalRole role;
        [Tooltip("Stroke every N drum steps (16th-note steps: 4 = every beat, 16 = once a bar).")]
        [Min(1)] public int stepsPerStroke = 8;
        [Tooltip("Step offset within the stroke cycle (e.g. 6 of 8 = a syncopated 'and').")]
        [Min(0)] public int stepOffset = 0;
        [Tooltip("Speed each stroke adds, eased in over Thrust Seconds (world units/s).")]
        [Min(0f)] public float strokeImpulse = 0.7f;
        [Tooltip("How fast the glide bleeds off after a stroke (1/s). Lower = longer, floatier glide.")]
        [Min(0f)] public float glideDrag = 0.8f;
        [Tooltip("How far the tendrils pull in on the stroke (0 = not at all, 1 = fully).")]
        [Range(0f, 1f)] public float contractAmount = 0.45f;

        public RoleSwimStyle() { }
        public RoleSwimStyle(MusicalRole r, int steps, int offset, float impulse, float drag, float contract)
        { role = r; stepsPerStroke = steps; stepOffset = offset; strokeImpulse = impulse; glideDrag = drag; contractAmount = contract; }
    }

    /// <summary>
    /// Charging stars swim like jellyfish in time with the music: tendrils contract on the role's
    /// stroke step, the star kicks forward, then glides with its tendrils trailing. They are curious
    /// about ships (lean toward one nearby) but shy (dart away when one gets close).
    /// </summary>
    [Serializable]
    public sealed class SwimTuning
    {
        [Tooltip("Per-role stroke rhythm. A role missing here uses the first entry.")]
        public RoleSwimStyle[] roles =
        {
            new RoleSwimStyle(MusicalRole.Bass,    16, 0, 0.9f,  0.6f, 0.55f),
            new RoleSwimStyle(MusicalRole.Harmony,  8, 0, 0.7f,  0.8f, 0.45f),
            new RoleSwimStyle(MusicalRole.Lead,     4, 0, 0.45f, 1.1f, 0.35f),
            new RoleSwimStyle(MusicalRole.Groove,   8, 6, 0.6f,  0.9f, 0.45f),
        };
        [Tooltip("Seconds the tendrils take to pull in before the push.")]
        [Min(0.02f)] public float contractSeconds = 0.12f;
        [Tooltip("Seconds each stroke's push is eased over (sine in/out). Longer = softer.")]
        [Min(0.05f)] public float thrustSeconds = 0.5f;
        [Tooltip("Strokes stop adding speed above this (world units/s). Never brakes a faster glide.")]
        [Min(0.1f)] public float maxSwimSpeed = 1.4f;
        [Tooltip("How fast the star's heading can turn (degrees/s). Lower = wider, floatier curves.")]
        [Min(1f)] public float turnDegreesPerSecond = 70f;
        [Tooltip("Sideways flick added to the tendrils on beats that aren't stroke steps (× budWiggleAmplitude).")]
        [Min(0f)] public float beatFlickAmount = 2.5f;
        [Tooltip("How strongly gliding tendrils stream behind the star (0 = keep their resting spread).")]
        [Range(0f, 1f)] public float trailStrength = 0.6f;
        [Tooltip("Glide speed (world units/s) at which the trail is fully applied.")]
        [Min(0.05f)] public float trailFullSpeed = 1.2f;
        [Tooltip("Stroke impulse multiplier while a vine is out — the star holds on instead of swimming.")]
        [Range(0f, 1f)] public float vineHoldImpulseMul = 0.15f;
        [Tooltip("How far ahead (cells) a stroke looks for open space in the maze.")]
        [Min(1f)] public float strokeProbeCells = 4f;
        [Tooltip("Less open room than this (cells) in every forward direction → the star pulses in place instead of stroking.")]
        [Min(0f)] public float minStrokeRoomCells = 0.75f;
        [Tooltip("Seconds between picking a new wander heading.")]
        public Vector2 headingRechooseSeconds = new Vector2(2.5f, 5f);

        [Header("Ship curiosity")]
        [Tooltip("A ship within this many cells draws the star's tendrils and heading toward it.")]
        [Min(0f)] public float curiousRadiusCells = 5f;
        [Tooltip("How much the heading bends toward a curious-range ship (0..1).")]
        [Range(0f, 1f)] public float curiousHeadingBlend = 0.6f;
        [Tooltip("How far tendrils lean toward a curious-range ship (0..1 of a full turn toward it).")]
        [Range(0f, 1f)] public float curiousTendrilLean = 0.45f;
        [Tooltip("Inside this many cells of a ship the star curls up progressively and drifts away from it.")]
        [Min(0f)] public float repelRadiusCells = 3f;
        [Tooltip("Push-away acceleration at point-blank range (world units/s²); falls off as (1 − d/R)².")]
        [Min(0f)] public float repelAccel = 2.5f;

        [Header("Ship bump (soft contact)")]
        [Tooltip("Contact starts this many cells before the star and hull actually touch.")]
        [Min(0f)] public float bumpSkinCells = 0.15f;
        [Tooltip("Push on the star per cell of overlap (world units/s²). The star yields most.")]
        [Min(0f)] public float bumpStarAccel = 14f;
        [Tooltip("Shove back on the ship per cell of overlap (world units/s²).")]
        [Min(0f)] public float bumpShipAccel = 10f;
        [Tooltip("How quickly the ship's closing speed into the star is soaked up (1/s). Higher = firmer.")]
        [Min(0f)] public float bumpShipDamping = 8f;

        [Header("Entrance")]
        [Tooltip("New stars swim in from the left, right or top edge of the play area (passing through walls). Off = they appear at their spawn cell.")]
        public bool entranceEnabled = true;
        [Tooltip("Stroke impulse multiplier while swimming in from off-screen to its spawn point.")]
        [Min(0f)] public float entranceImpulseMul = 2.2f;
        [Tooltip("Entrance ends when the star is within this many cells of its spawn point.")]
        [Min(0.1f)] public float entranceArriveCells = 0.6f;
        [Tooltip("Safety: entrance ends after this many seconds regardless.")]
        [Min(0.5f)] public float entranceMaxSeconds = 8f;
        [Tooltip("How far outside the play-area edge the star starts, in cells.")]
        [Min(0f)] public float entranceStartOutsideCells = 1.5f;

        [Header("Spawn spread")]
        [Tooltip("Stars spawn at least this many cells from each other when possible.")]
        [Min(0f)] public float minStarSeparationCells = 6f;
        [Tooltip("Stars spawn in an open (dust-free) pocket of at least this many connected cells when one exists, so they have room to swim. Pockets sealed off from the ship by Cosmic Blocks or the vault are always avoided.")]
        [Min(1)] public int minSpawnOpenCells = 8;

        public RoleSwimStyle For(MusicalRole role)
        {
            if (roles == null || roles.Length == 0) return null;
            for (int i = 0; i < roles.Length; i++)
                if (roles[i] != null && roles[i].role == role) return roles[i];
            return roles[0];
        }
    }

    [Header("3. Motion")]
    [Tooltip("Charging stars swim in time with the music and react to ships (see SwimTuning).")]
    public SwimTuning swim = new();
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
    [Tooltip("A flower a vine failed to reach (\"vine blocked\") is skipped for this many seconds so other flowers get a turn.")]
    [Min(0f)] public float blockedTargetCooldownSeconds = 3f;

    [Header("5. Tentacles — idle tendrils (one per note still needed)")]
    [Tooltip("Length of each idle tendril, in cells.")]
    [Min(0.05f)] public float budLengthCells = 1.2f;
    [Tooltip("Tendril opacity at the root (the tip fades to 60%).")]
    [Range(0f, 1f)] public float budAlpha = 0.85f;
    [Tooltip("Gap between the star's centre and where the tendrils start, in cells.")]
    [Min(0f)] public float budRootOffsetCells = 0.12f;
    [Tooltip("How far each tendril curls sideways toward its tip, in cells.")]
    [Min(0f)] public float budCurlAmplitude = 0.3f;
    [Tooltip("Tendril width at the root, in world units (tapers toward the tip).")]
    [Min(0.005f)] public float budWidthWorld = 0.07f;
    [Tooltip("Most tendrils shown at once (a star blocked on its count can't sprout dozens).")]
    [Range(1, 64)] public int budMaxShown = 16;
    [Tooltip("Tendril opacity multiplier while the star sleeps or is paused for a sibling.")]
    [Range(0f, 1f)] public float budDimmedAlphaMul = 0.4f;
    [Tooltip("Sideways wiggle at the bud tip, in cells.")]
    [Min(0f)] public float budWiggleAmplitude = 0.08f;
    [Tooltip("How fast the ring of buds slowly turns (degrees per second).")]
    public float budSpinDegreesPerSecond = 12f;
    [Tooltip("Tutorial callout (\"These are discoveries.\"): bud opacity at the peak of its pulse.")]
    [Range(0f, 1f)] public float budHighlightAlpha = 1f;
    [Tooltip("Tutorial callout: bud width multiplier at the peak of its pulse.")]
    [Min(1f)] public float budHighlightWidthMul = 2.6f;
    [Tooltip("Tutorial callout: pulses per second.")]
    [Min(0.01f)] public float budHighlightPulseHz = 1.2f;
}
