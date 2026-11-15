using UnityEngine;

/// <summary>
/// What a path-choice exit leads to. Two axes, each read at a glance from the road:
/// scope (a motif in the current phase vs. a different phase) and explored (colour = new,
/// black-and-white = already played).
/// </summary>
public enum PathExitKind
{
    NewMotif,      // same-phase motif with no recorded ring yet
    VisitedMotif,  // same-phase motif already played
    NewPhase,      // a phase none of whose motifs has been played
    VisitedPhase   // a phase already visited (e.g. the previous phase)
}

/// <summary>
/// Single designer-facing config for path-choice exits: where they're placed, how each of the
/// four road kinds looks, how the road feels (vibration, capture surge, suction) and timing.
/// Consumed by PathExitParticleBuilder / PathExitPortal / PathChoiceCoordinator /
/// SceneFlowCoordinator via GameFlowManager.PathExitConfig. Every field has a working default,
/// so an empty asset (or none at all) plays fine.
/// </summary>
[CreateAssetMenu(menuName = "Astral Planes/Path Exit Config", fileName = "PathExitConfig")]
public class PathExitConfig : ScriptableObject
{
    public enum RoadPalette
    {
        Spectrum,    // full 7-colour spectrum
        MotifRoles   // the target motif's own role colours
    }

    public enum RoadPattern
    {
        Chevrons,    // outward-pointing chevrons flowing along the road
        Streaks      // long straight streaks flowing along each stripe
    }

    [System.Serializable]
    public class RoadLook
    {
        [Header("Colour")]
        [Tooltip("Which colours make up the road's stripes.")]
        public RoadPalette palette = RoadPalette.Spectrum;
        [Tooltip("Black-and-white version of the palette (used for explored roads).")]
        public bool monochrome = false;

        [Header("Shape & motion")]
        [Tooltip("Texture flowing along the road.")]
        public RoadPattern pattern = RoadPattern.Chevrons;
        [Tooltip("Road width as a multiple of the exit's passable opening. The capture trigger is unaffected.")]
        [Range(0.3f, 1.5f)] public float widthScale = 1f;
        [Tooltip("Road opacity (at and beyond the maze wall).")]
        [Range(0f, 1f)] public float roadAlpha = 0.9f;
        [Tooltip("Extra white in the flowing highlights (0 = flat stripes).")]
        [Range(0f, 1f)] public float highlight = 0.35f;
        [Tooltip("How fast the road texture flows outward (texture repeats per second).")]
        [Min(0f)] public float flowSpeed = 0.9f;

        [Header("Streamers (speed-lines along the road)")]
        [Tooltip("Streamers per second fired from the road's mouth.")]
        [Min(0f)] public float streamerRate = 20f;
        [Tooltip("Streamer thickness in world units.")]
        [Min(0.005f)] public float streamerSize = 0.03f;
        [Tooltip("Streamer length in world units (fixed; does not grow with speed).")]
        [Min(0.02f)] public float streamerLength = 0.5f;
        [Tooltip("Streamer opacity (0 = invisible). Independent of particleAlpha, no minimum.")]
        [Range(0f, 1f)] public float streamerAlpha = 0.25f;

        [Header("Halo (motes pulled into the mouth)")]
        [Tooltip("Halo motes per second.")]
        [Min(0f)] public float haloRate = 6f;
        [Tooltip("Halo mote size in world units.")]
        [Min(0.02f)] public float haloSize = 0.07f;
        [Tooltip("Seconds for a mote to fall into the road's mouth.")]
        [Min(0.3f)] public float haloFallSeconds = 1.4f;
        [Tooltip("Orbital swirl (radians/second) as motes fall in.")]
        public float haloSwirl = 0.4f;
        [Tooltip("Jitter on halo motes.")]
        [Range(0f, 2f)] public float haloNoise = 0.5f;
        [Tooltip("Strays (weak pull, they escape) as a fraction of the halo rate.")]
        [Range(0f, 0.6f)] public float haloStrayFraction = 0.15f;

        [Tooltip("Opacity of halo motes (floored at 0.6 for visibility). Streamers use streamerAlpha.")]
        [Range(0.1f, 1f)] public float particleAlpha = 1f;
    }

    // ── 1. Placement ──────────────────────────────────────────────────────────

    [Header("1. Placement")]
    [Tooltip("Top/Bottom exits stay within this centred fraction of the grid width (0.333 = middle third). Left/Right exits are unaffected.")]
    [Range(0.1f, 1f)] public float topBottomExitCenterBand = 1f / 3f;
    [Tooltip("Left/Right exits only: world units the whole exit (trigger, road mouth, carry-out line) is moved in from the side wall along x so it sits on-screen. The road still runs out to the true screen edge.")]
    [Min(0f)] public float leftRightExitInset = 3.4f;
    [Tooltip("How many cells deep (inward from the wall) an exit's lane reaches — sets the road run-up and carry-out progress. The capture collider is sized separately (triggerDepthCells / triggerWidthFraction).")]
    [Min(1)] public int captureDepthCells = 4;
    [Tooltip("How many cells deep (inward from the wall face) the exit's capture collider reaches. Smaller = a ship has to actually enter the mouth to be captured.")]
    [Range(0.5f, 4f)] public float triggerDepthCells = 1.5f;
    [Tooltip("Capture collider width as a fraction of the carved opening width (centred on the road). Smaller = easier to slip past an exit without being captured.")]
    [Range(0.2f, 1f)] public float triggerWidthFraction = 0.6f;
    [Tooltip("How far the road reaches into the maze before the wall, as a fraction of the trigger depth. It fades from transparent there to fully opaque at the wall.")]
    [Range(0.05f, 2f)] public float roadInnerLength = 0.25f;

    // ── 2. Road kinds ─────────────────────────────────────────────────────────

    [Header("2. Road kinds")]
    [Tooltip("Road into a phase none of whose motifs has been played.")]
    public RoadLook newPhase = new RoadLook
    {
        palette = RoadPalette.Spectrum, monochrome = false, pattern = RoadPattern.Chevrons,
        widthScale = 1f, roadAlpha = 1f, highlight = 0.5f, flowSpeed = 1.3f,
        streamerRate = 22f, streamerSize = 0.03f, streamerLength = 0.6f, streamerAlpha = 0.3f,
        haloRate = 11f, haloSize = 0.09f, haloFallSeconds = 1.1f, haloSwirl = 0.6f,
        haloNoise = 0.6f, haloStrayFraction = 0.12f, particleAlpha = 1f
    };

    [Tooltip("Road into a phase already visited (e.g. the previous phase).")]
    public RoadLook visitedPhase = new RoadLook
    {
        palette = RoadPalette.Spectrum, monochrome = true, pattern = RoadPattern.Chevrons,
        widthScale = 1f, roadAlpha = 0.8f, highlight = 0.3f, flowSpeed = 0.9f,
        streamerRate = 12f, streamerSize = 0.025f, streamerLength = 0.5f, streamerAlpha = 0.2f,
        haloRate = 6f, haloSize = 0.08f, haloFallSeconds = 1.4f, haloSwirl = 0.4f,
        haloNoise = 0.5f, haloStrayFraction = 0.15f, particleAlpha = 0.8f
    };

    [Tooltip("Road to a motif in this phase that hasn't been played.")]
    public RoadLook newMotif = new RoadLook
    {
        palette = RoadPalette.MotifRoles, monochrome = false, pattern = RoadPattern.Streaks,
        widthScale = 0.7f, roadAlpha = 0.95f, highlight = 0.4f, flowSpeed = 1f,
        streamerRate = 18f, streamerSize = 0.025f, streamerLength = 0.5f, streamerAlpha = 0.25f,
        haloRate = 7f, haloSize = 0.07f, haloFallSeconds = 1.4f, haloSwirl = 0.4f,
        haloNoise = 0.5f, haloStrayFraction = 0.15f, particleAlpha = 0.95f
    };

    [Tooltip("Road to a motif in this phase that has already been played.")]
    public RoadLook visitedMotif = new RoadLook
    {
        palette = RoadPalette.MotifRoles, monochrome = true, pattern = RoadPattern.Streaks,
        widthScale = 0.7f, roadAlpha = 0.75f, highlight = 0.25f, flowSpeed = 0.7f,
        streamerRate = 10f, streamerSize = 0.02f, streamerLength = 0.4f, streamerAlpha = 0.15f,
        haloRate = 4f, haloSize = 0.06f, haloFallSeconds = 1.8f, haloSwirl = 0.3f,
        haloNoise = 0.4f, haloStrayFraction = 0.2f, particleAlpha = 0.75f
    };

    // ── 3. Road texture ───────────────────────────────────────────────────────

    [Header("3. Road texture")]
    [Tooltip("Fraction of each stripe (per side) that blends softly into its neighbour.")]
    [Range(0f, 0.5f)] public float stripeBlend = 0.35f;
    [Tooltip("How close to white each stripe's glowing core gets.")]
    [Range(0f, 1f)] public float coreGlow = 0.55f;
    [Tooltip("Core sharpness: higher = thinner bright core with a longer soft falloff.")]
    [Range(1f, 6f)] public float corePower = 2.2f;
    [Tooltip("Texture repeats per world unit along the road.")]
    [Min(0.05f)] public float textureTilesPerUnit = 0.5f;
    [Tooltip("How far (world units) the road continues past the screen edge.")]
    [Min(0f)] public float offscreenTailLength = 6f;

    // ── 4. Gravity vibration ──────────────────────────────────────────────────

    [Header("4. Gravity vibration (road sides)")]
    [Tooltip("Fraction of the road's width on each side that feathers out softly instead of a hard edge.")]
    [Range(0f, 0.3f)] public float edgeFeather = 0.14f;
    [Tooltip("How far the road's sides ripple, as a fraction of its width.")]
    [Range(0f, 0.3f)] public float vibrationAmplitude = 0.07f;
    [Tooltip("Ripple waves per world unit along the road.")]
    [Min(0f)] public float vibrationFrequency = 1.6f;
    [Tooltip("How fast the ripples travel outward (world units/second).")]
    [Min(0f)] public float vibrationSpeed = 3.5f;

    // ── 5. Capture ────────────────────────────────────────────────────────────

    [Header("5. Capture (a vehicle enters the trigger)")]
    [Tooltip("How much wider the chosen road gets (x its width).")]
    [Range(1f, 2.2f)] public float swallowWidthScale = 1.35f;
    [Tooltip("Particle emission multiplier once captured.")]
    [Range(1f, 10f)] public float captureEmissionBoost = 4f;
    [Tooltip("Particles burst from each emitter the instant of capture.")]
    [Min(0)] public int captureBurst = 40;
    [Tooltip("Outward acceleration (world units/s²) pulling captured vehicles down the road.")]
    [Min(0f)] public float pullAcceleration = 18f;
    [Tooltip("How hard vehicles on a road are snapped onto its centre line (per second; higher = locks on faster). Applies when sucked in and when riding the entry road in the next motif.")]
    [Min(0f)] public float centerPull = 10f;
    [Tooltip("Road progress (0 = road's inner end, 1 = screen edge) at which the vehicle starts shrinking away.")]
    [Range(0f, 0.95f)] public float shrinkStartProgress = 0.35f;

    // ── 6. Entry (next motif) ─────────────────────────────────────────────────

    [Header("6. Entry (riding the road into the next motif)")]
    [Tooltip("Speed (world units/s) vehicles are flung into the maze as they come off the end of the entry road.")]
    [Min(0f)] public float spitSpeed = 9f;

    // ── 7. Timing ─────────────────────────────────────────────────────────────

    [Header("7. Timing")]
    [Tooltip("Seconds for a road to unroll when it opens.")]
    [Min(0.01f)] public float openSeconds = 0.5f;
    [Tooltip("Seconds for the chosen road to widen and brighten on capture.")]
    [Min(0.01f)] public float swallowSeconds = 0.6f;
    [Tooltip("Seconds a losing road takes to narrow away once sealed.")]
    [Min(0f)] public float sealFadeSeconds = 0.35f;
    [Tooltip("Seconds the chosen road takes to close after the vehicles are gone.")]
    [Min(0f)] public float closeSeconds = 0.6f;
    [Tooltip("Seconds the old interstitial maze takes to dissolve (runs alongside the road closing).")]
    [Min(0f)] public float dissolveSeconds = 0.5f;
    [Tooltip("Held beat after the vehicles vanish, before the road closes.")]
    [Min(0f)] public float postSwallowBeatSeconds = 0.2f;
    [Tooltip("Seconds the entry road on the next maze's mirrored edge takes to close once vehicles are spat out.")]
    [Min(0f)] public float entryRoadCloseSeconds = 0.6f;
    [Tooltip("World units past the screen edge a vehicle must clear to count as off-screen (exit carry-out and next-maze entry staging).")]
    [Min(0f)] public float offscreenMargin = 2f;
    [Tooltip("Max seconds to drive vehicles out through an exit (or in at the next maze) before snapping them.")]
    [Min(0f)] public float travelTimeoutSeconds = 3f;

    public RoadLook Look(PathExitKind kind) => kind switch
    {
        PathExitKind.NewPhase     => newPhase,
        PathExitKind.VisitedPhase => visitedPhase,
        PathExitKind.VisitedMotif => visitedMotif,
        _                         => newMotif
    };
}
