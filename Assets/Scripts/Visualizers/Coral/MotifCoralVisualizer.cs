using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// Renders a single motif as a semantic coral structure.
///
/// STRUCTURE
/// ─────────
///   Trunk anchor (sphere at base)
///   └─ 4 primary branches, one per MusicalRole (Bass / Harmony / Lead / Groove)
///      └─ Each branch has up to 4 segments, one per bin
///         ├─ If a bin has ONLY matched notes  → single segment continues upward
///         └─ If a bin has any unmatched notes → segment forks at its base:
///               • Matched sub-branch  (continues upward; next bin attaches here)
///               • Unmatched sub-branch (same length, terminates — a "barren" branch)
///
/// GEOMETRY PER SEGMENT
/// ─────────────────────
///   • Height  ∝ time taken to fill that bin (CompletionTime - MotifStartTime, normalised)
///   • Spiral radius ∝ step-spread of collected notes within the bin
///   • Tube radius   ∝ note count (density)
///   • Buds placed along the branch at positions derived from bin-local step index
///   • Matched buds: solid sphere, track colour
///   • Unmatched buds: wireframe-style ring, desaturated
///   • Root-note bud: larger + brighter
///
/// ANIMATION
/// ──────────
///   Branches grow from base to tip over growSeconds.
///   Player stick input modulates the fork angle in real time (see SetForkAngle).
///
/// WIRING
/// ───────
///   Call RenderMotifCoral(snapshot) to build and instantly show.
///   Call GrowMotifCoral(snapshot, duration, getSteer) to build and animate.
/// </summary>
[RequireComponent(typeof(MeshRenderer))]
public class MotifCoralVisualizer : MonoBehaviour
{
    // ═══════════════════════════════════════════════════════════════════════
    //  Inspector
    // ═══════════════════════════════════════════════════════════════════════

    [Header("Materials")]
    [Tooltip("Branch outline tube. Assign a Particles/Standard Unlit material (or leave null to auto-create one). Reads vertex colour.")]
    public Material branchMaterial;
    [Tooltip("Branch fill tube (inner translucent layer). Assign a Transparent Lit material from the inspector for organic depth. Reads vertex colour.")]
    public Material branchFillMaterial;
    [Tooltip("Bud spheres (matched notes). Assign Particles/Standard Unlit — same particle texture as branches. Reads vertex colour.")]
    public Material budMaterial;
    [Tooltip("Barren bud rings (unmatched notes). Assign Particles/Standard Unlit transparent. Reads vertex colour.")]
    public Material barrenBudMaterial;

    [Header("Organic Noise Texture")]
    [Tooltip("Size of the generated noise texture (power of 2). Higher = more detail but more memory.")]
    [Range(64, 512)] public int noiseTexSize = 128;
    [Tooltip("How strongly the cellular (Worley) noise pattern shows. 0 = none.")]
    [Range(0f, 1f)] public float cellularStrength = 0.55f;
    [Tooltip("How strongly the fbm (fractal) noise pattern shows. 0 = none.")]
    [Range(0f, 1f)] public float fbmStrength = 0.35f;
    [Tooltip("Scale of the noise pattern (larger = broader features).")]
    [Range(1f, 16f)] public float noiseScale = 4f;
    [Tooltip("Darkening at the edges of the tube (simulates subsurface depth). 0 = none.")]
    [Range(0f, 1f)] public float edgeDarken = 0.4f;
    [Header("Bud Signal Particles")]
    [SerializeField] private Material budParticleMaterial;
    [SerializeField] private GradientAlphaKey[] budParticleAlphaKeys = new GradientAlphaKey[]
    {
        new GradientAlphaKey(0.85f, 0f),
        new GradientAlphaKey(0.35f, 0.65f),
        new GradientAlphaKey(0f, 1f)
    };
    [Header("Transition Out")]
    [Min(0.05f)] public float transitionOutSeconds = 0.9f;
    public AnimationCurve transitionOutScaleCurve = AnimationCurve.EaseInOut(0f, 1f, 1f, 0f);
    public AnimationCurve transitionOutMoveCurve  = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

    [Tooltip("World-space direction the coral is whisked toward before teleporting.")]
    public Vector3 transitionOutDirection = new Vector3(0.8f, 1.2f, 0f);

    [Tooltip("How far the coral root moves during the transition.")]
    [Min(0f)] public float transitionOutDistance = 1.75f;

    [Tooltip("Adds a little spin during the whisk-away.")]
    [Min(0f)] public float transitionOutSpinDeg = 90f;

    [Header("Teleport Burst")]
    public bool spawnTeleportBurst = true;
    [Min(4)]  public int teleportBurstCount = 36;
    [Min(0.01f)] public float teleportBurstLifetime = 0.55f;
    [Min(0.01f)] public float teleportBurstSpeedMin = 0.8f;
    [Min(0.01f)] public float teleportBurstSpeedMax = 2.2f;
    [Min(0.001f)] public float teleportBurstSizeMin = 0.03f;
    [Min(0.001f)] public float teleportBurstSizeMax = 0.08f;
    public Color teleportBurstTint = new Color(0.8f, 0.95f, 1f, 1f);
    public Material teleportBurstMaterial;
    [SerializeField] private Color budMatchedColor = new Color(0.55f, 1f, 0.72f, 1f);
    [SerializeField] private Color budUnmatchedColor = new Color(1f, 0.35f, 0.45f, 1f);

    [SerializeField] private int budParticlesMin = 6;
    [SerializeField] private int budParticlesMax = 12;
    [SerializeField] private float budParticleLifetimeMin = 0.35f;
    [SerializeField] private float budParticleLifetimeMax = 0.7f;
    [SerializeField] private float budParticleSpeedMin = 0.08f;
    [SerializeField] private float budParticleSpeedMax = 0.32f;
    [SerializeField] private float budParticleSizeMin = 0.015f;
    [SerializeField] private float budParticleSizeMax = 0.05f;
    [SerializeField] private float budParticleRadiusMul = 0.65f;
    // Runtime noise textures — generated once in Awake, shared across all segments
    private Texture2D _outlineNoiseTex;
    private Texture2D _fillNoiseTex;
    private IMotifCoralGeometryBuilder _geometryBuilder;
    private IMotifCoralAnimationController _animationController;
    private IMotifCoralVisualResources _visualResources;

    [Header("Scale")]
    [Tooltip("Master world-space scale for all geometry. Set this to match your scene's unit size instead of scaling the GameObject transform. " +
             "All heights, radii, and spacing multiply by this value. Angles and ring counts are unaffected.")]
    [Min(0.01f)] public float worldScale = 1f;

    [Header("Trunk")]
    [Tooltip("World-space position of the trunk anchor (before worldScale).")]
    public Vector3 origin = Vector3.zero;
    [Tooltip("Radius of the trunk anchor sphere (before worldScale).")]
    [Min(0.01f)] public float trunkRadius = 0.18f;

    [Header("Branch Layout")]
    [Tooltip("Base angle (degrees) between matched and unmatched fork branches.")]
    [Range(5f, 60f)] public float defaultForkAngleDeg = 22f;
    [Tooltip("Maximum extra fork angle the player stick can add (degrees).")]
    [Range(0f, 40f)] public float maxSteerForkExtra = 18f;
    [Tooltip("Base elevation angle (degrees above/below equator) for the first bin on each role. " +
             "Successive bins alternate above and below, multiplied by bin index, filling the sphere.")]
    [Range(0f, 45f)] public float primaryBranchLean = 18f;
    [Tooltip("How many degrees each successive bin on a role rotates around the sphere. " +
             "45° means a filled 4-bin track produces branches at N, NE, E, SE etc. " +
             "Combined across 4 roles you get up to 16 evenly-distributed branches.")]
    [Range(15f, 90f)] public float binRotationDeg = 45f;
    [Tooltip("Controls how much the branch direction wanders organically. Small = gentle drift, large = adventurous curves.")]
    [Range(0f, 45f)] public float segmentTwistDeg = 12f;

    [Header("Segment Height")]
    [Tooltip("Minimum segment height (before worldScale).")]
    [Min(0.05f)] public float segHeightMin = 1.2f;
    [Tooltip("Maximum segment height (before worldScale).")]
    [Min(0.1f)]  public float segHeightMax = 4.0f;
    [Tooltip("Expected worst-case bin fill time in seconds (maps to segHeightMax).")]
    [Min(1f)]    public float maxExpectedFillSeconds = 60f;
    [Tooltip("Extra height per note in the bin so buds never overlap (before worldScale).")]
    [Min(0f)]    public float budSpacingPerNote = 0.18f;

    [Header("Segment Width / Spiral")]
    [Tooltip("Base tube radius for a branch with 1 note (before worldScale).")]
    [Min(0.005f)] public float tubeRadiusBase = 0.04f;
    [Tooltip("Extra tube radius per note in the bin (before worldScale).")]
    [Min(0f)]     public float tubeRadiusPerNote = 0.008f;
    [Tooltip("Minimum spiral coil radius (before worldScale).")]
    [Min(0f)]     public float coilRadiusMin = 0.08f;
    [Tooltip("Maximum spiral coil radius (before worldScale).")]
    [Min(0f)]     public float coilRadiusMax = 0.32f;
    [Tooltip("Number of full turns the branch makes per unit of height (unscaled — already relative to height).")]
    [Min(0f)]     public float coilTurnsPerUnit = 0.9f;

    [Header("Tube Geometry")]
    [Tooltip("Rings along each tube segment. More rings = smoother organic curves. 20+ recommended.")]
    [Range(4, 48)] public int tubeRings = 24;
    [Tooltip("Radial sides per tube ring.")]
    [Range(4, 16)] public int tubeSides = 8;
    [Tooltip("Taper power (1 = linear, >1 = sharper tip).")]
    [Range(0.5f, 4f)] public float tapePower = 1.8f;
    [Tooltip("Minimum tube radius at tip (before worldScale).")]
    [Min(0.001f)] public float minTipRadius = 0.003f;

    [Header("Buds")]
    [Tooltip("Base radius for a matched bud sphere (before worldScale).")]
    [Min(0.01f)] public float budRadiusBase = 0.055f;
    [Tooltip("Scale multiplier for root-note buds (ratio, unaffected by worldScale).")]
    [Min(1f)]    public float rootNoteScaleBoost = 1.55f;
    [Tooltip("Alpha for unmatched/barren bud rings.")]
    [Range(0f, 1f)] public float barrenBudAlpha = 0.45f;

    [Header("Fill Tube")]
    [Tooltip("Alpha for the translucent fill tube inside the outline.")]
    [Range(0f, 1f)] public float fillAlpha = 0.18f;
    [Tooltip("Radius multiplier for the outline vs fill tube.")]
    [Min(1f)] public float outlineRadiusMul = 1.08f;

    [Header("Grow Animation")]
    public AnimationCurve growCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

    [Header("Note Shot FX")]
    [Tooltip("Duration in seconds for a collected note marker to fly from the grid to its coral bud.")]
    [Min(0.05f)] public float noteShotDuration = 0.25f;
    [Tooltip("Radius of the flying dot (before worldScale).")]
    [Min(0.005f)] public float noteShotScale = 0.055f;

    // ═══════════════════════════════════════════════════════════════════════
    //  Private state
    // ═══════════════════════════════════════════════════════════════════════

    private Transform _root;

    // One entry per tube segment spawned (for grow animation)
    private struct TubeSegmentRuntime
    {
        public Mesh   outlineMesh;
        public int[]  outlineFullTris;
        public int    outlineSegCount;
        public Mesh   fillMesh;
        public int[]  fillFullTris;
        public int    fillSegCount;
        public float  growStartNorm; // 0..1 when this segment starts appearing
        public float  growEndNorm;   // 0..1 when fully revealed
    }

    private struct BudRuntime
    {
        public GameObject go;
        public float showAtNorm;
        public bool isMatched;
        public MotifSnapshot.NoteEntry note; // used to match origin position for shot FX
    }

    private readonly List<TubeSegmentRuntime> _segments = new();
    private readonly List<BudRuntime>          _buds     = new();

    // Current fork angle (player can modulate this live)
    private float _forkAngleDeg;

    // Role → fixed azimuth around trunk (deterministic)
    private static readonly Dictionary<MusicalRole, float> RoleAzimuth = new()
    {
        { MusicalRole.Bass,    0f   },
        { MusicalRole.Harmony, 90f  },
        { MusicalRole.Lead,    180f },
        { MusicalRole.Groove,  270f },
    };

    // ═══════════════════════════════════════════════════════════════════════
    //  Unity lifecycle
    // ═══════════════════════════════════════════════════════════════════════

    private void Awake()
    {
        // worldScale is the only intended size knob. If the GameObject's transform was
        // scaled to compensate, reset it and absorb the scale into worldScale instead.
        if (transform.localScale != Vector3.one)
        {
            float inheritedScale = transform.localScale.x; // assume uniform
            if (!Mathf.Approximately(inheritedScale, 1f))
            {
                worldScale *= inheritedScale;
                Debug.Log($"[MotifCoral] Absorbed transform scale {inheritedScale:F2} into worldScale → {worldScale:F2}. Reset transform to (1,1,1).");
            }
            transform.localScale = Vector3.one;
        }

        var rootGO = new GameObject("MotifCoralRoot");
        rootGO.transform.SetParent(transform, false);
        rootGO.transform.localPosition = Vector3.zero;
        _root = rootGO.transform;

        _forkAngleDeg = defaultForkAngleDeg;

        _outlineNoiseTex = GenerateOrganicNoise(noiseTexSize, darkenEdges: true);
        _fillNoiseTex    = GenerateOrganicNoise(noiseTexSize, darkenEdges: false);
        _visualResources = new MotifCoralVisualResources(_outlineNoiseTex, _fillNoiseTex);
        _geometryBuilder ??= new MotifCoralGeometryBuilder();
        _animationController ??= new MotifCoralAnimationController();

        var mr = GetComponent<MeshRenderer>();
        if (mr) mr.enabled = false;
    }

    private void OnDestroy() => ClearAll();

    // ═══════════════════════════════════════════════════════════════════════
    //  Play-area fitting
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Fits the coral to a world-space play area so it never overflows the screen.
    /// Call once before GrowMotifCoral / RenderMotifCoral.
    /// The coral is a sphere: trunk + arms radiate in all directions.
    /// worldScale is derived so the full ball diameter fills 80% of the shorter axis.
    /// </summary>
    public void FitToPlayArea(float areaWidth, float areaHeight, float areaCenterX, float areaCenterY)
    {
        float targetDiameter = Mathf.Min(areaWidth, areaHeight) * 0.80f;

        // Actual structure is a sphere with many independent outward branches.
        // Bins do NOT chain end-to-end, so use the max extent of one branch.
        float rawRadius =
            trunkRadius +
            segHeightMax +
            coilRadiusMax +
            Mathf.Max(tubeRadiusBase + tubeRadiusPerNote * 8f, budRadiusBase * rootNoteScaleBoost);

        float rawDiam = rawRadius * 2f;

        worldScale = rawDiam > 0f
            ? Mathf.Clamp(targetDiameter / rawDiam, 0.05f, 200f)
            : 1f;

        origin = new Vector3(areaCenterX, areaCenterY, 0f);

        if (_root != null)
            _root.localPosition = Vector3.zero;

        Debug.Log($"[MotifCoral] FitToPlayArea: area={areaWidth:F2}x{areaHeight:F2} " +
                  $"targetDiam={targetDiameter:F2} rawDiam={rawDiam:F2} worldScale={worldScale:F2} " +
                  $"origin=({areaCenterX:F2},{areaCenterY:F2})");
    }
    // ═══════════════════════════════════════════════════════════════════════
    //  Public API
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Instantly build and display the full coral for this snapshot.</summary>
    public void RenderMotifCoral(MotifSnapshot snapshot)
    {
        ClearAll();
        if (snapshot == null) return;
        BuildCoral(snapshot);
        ForceFullReveal();
    }

    /// <summary>
    /// Build the coral and animate it growing over <paramref name="durationSec"/> seconds.
    /// <paramref name="getSteer"/> is polled each frame; stick-X modulates the fork angle.
    /// </summary>
    public IEnumerator GrowMotifCoral(MotifSnapshot snapshot, float durationSec, Func<Vector2> getSteer = null,
        IReadOnlyDictionary<(int step, Color32 trackColor), Vector3> noteOrigins = null)
    {
        ClearAll();
        if (snapshot == null) { yield return new WaitForSeconds(durationSec); yield break; }

        BuildCoral(snapshot);
        yield return StartCoroutine(AnimateGrowth(durationSec, getSteer, noteOrigins));
    }

    /// <summary>
    /// Adjust the fork angle directly (e.g. from player stick).
    /// 0 = tight, 1 = maximum spread.
    /// </summary>
    public void SetForkAngle(float t01)
    {
        _forkAngleDeg = Mathf.Lerp(defaultForkAngleDeg, defaultForkAngleDeg + maxSteerForkExtra,
                                    Mathf.Clamp01(t01));
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Build
    // ═══════════════════════════════════════════════════════════════════════

    private void BuildCoral(MotifSnapshot snapshot)
    {
        if (snapshot.TrackBins == null || snapshot.TrackBins.Count == 0) return;

        // ── Central sphere ────────────────────────────────────────────────
        // All branches radiate from the surface of this sphere.
        // Its radius is worldScale * trunkRadius so it scales proportionally.
        Vector3 center  = origin;
        float   sphereR = S(trunkRadius);
        SpawnCenterSphere(center, sphereR);

        // ── Collect all bins, assign a unique surface direction to each ───
        // Layout logic:
        //   Each role owns a cardinal axis (N/E/S/W = 0°/90°/180°/270° azimuth).
        //   Each successive bin on that role rotates the outward direction by
        //   binRotationDeg (default 45°) around the sphere's vertical axis,
        //   interleaving roles so a filled 4-role × 4-bin track produces 16
        //   branches spread evenly around the ball.
        //
        //   The elevation from horizontal is also varied per bin so branches
        //   spread above AND below the equator, filling a true 3-D sphere.

        var byRole = snapshot.TrackBins
            .GroupBy(b => b.Role)
            .ToDictionary(g => g.Key, g => g.OrderBy(b => b.BinIndex).ToList());

        // Count total segments for grow-time budget
        int totalSegs = 0;
        foreach (var pair in byRole)
            foreach (var bin in pair.Value)
                totalSegs += (bin.UnmatchedNoteCount > 0) ? 2 : 1;

        float segNormWidth = totalSegs > 0 ? 1f / totalSegs : 1f;
        int   segIndex     = 0;

        foreach (var pair in byRole)
        {
            MusicalRole role    = pair.Key;
            var         bins    = pair.Value;
            float       baseAz  = RoleAzimuth.TryGetValue(role, out float az) ? az : 0f;
            float       roleSeed = baseAz * 0.031f + (int)role * 1.37f;

            for (int bi = 0; bi < bins.Count; bi++)
            {
                var bin = bins[bi];

                // ── Surface direction for this bin ────────────────────────
                // Azimuth: base cardinal + (bi * binRotationDeg) so each new
                // bin on this arm rotates around the sphere.
                float azimuth = baseAz + bi * binRotationDeg;

                // Elevation: alternate above/below equator per bin so the
                // ball fills out in 3-D rather than all branches being flat.
                // bin 0 → equator, bin 1 → up, bin 2 → equator, bin 3 → down, etc.
                float elevSign  = (bi % 2 == 0) ? 1f : -1f;
                float elevation = elevSign * primaryBranchLean * (1f + bi * 0.3f);
                elevation = Mathf.Clamp(elevation, -75f, 75f);

                // Convert azimuth + elevation to a unit direction
                Vector3 surfaceDir = AzimuthElevationDir(azimuth, elevation);

                // Branch root sits on the sphere surface
                Vector3 segStart = center + surfaceDir * sphereR;

                // ── Segment sizing ────────────────────────────────────────
                int   totalNotes   = bin.MatchedNoteCount + bin.UnmatchedNoteCount;
                float fillDuration = bin.CompletionTime > 0f
                    ? Mathf.Max(0f, bin.CompletionTime - bin.MotifStartTime) : 0f;
                float timeHeight   = Mathf.Lerp(S(segHeightMin), S(segHeightMax),
                    Mathf.Clamp01(fillDuration / maxExpectedFillSeconds));
                float spacingH     = totalNotes * S(budSpacingPerNote);
                float segHeight    = Mathf.Max(timeHeight, spacingH + S(segHeightMin));
                float tubeRadius   = S(tubeRadiusBase) + S(tubeNoteRadius(totalNotes));
                float stepSpread   = StepSpread(bin.CollectedSteps,
                    snapshot.TotalSteps > 0 ? snapshot.TotalSteps : 16);
                float driftRadius  = Mathf.Lerp(S(coilRadiusMin), S(coilRadiusMax), stepSpread);

                // Organic drift on the outgoing direction (same noise as before)
                float noiseA   = Mathf.Sin(roleSeed + bi * 1.9f)  * segmentTwistDeg * Mathf.Deg2Rad * 0.4f;
                float noiseB   = Mathf.Sin(roleSeed * 1.3f + bi * 2.7f + 0.8f) * segmentTwistDeg * Mathf.Deg2Rad * 0.25f;
                Vector3 sideAxis = Vector3.Cross(surfaceDir, Vector3.up);
                if (sideAxis.sqrMagnitude < 1e-4f) sideAxis = Vector3.right;
                sideAxis.Normalize();
                Vector3 fwdAxis = Vector3.Cross(surfaceDir, sideAxis).normalized;
                Vector3 segDir  = (surfaceDir + sideAxis * noiseA + fwdAxis * noiseB).normalized;

                bool hasFork = bin.UnmatchedNoteCount > 0;

                // ── Matched branch ────────────────────────────────────────
                {
                    float t0 = segIndex * segNormWidth;
                    float t1 = t0 + segNormWidth;

                    Vector3[] spine = BuildSpiralSpine(segStart, segDir, segHeight,
                        driftRadius, bin.CollectedSteps, snapshot.TotalSteps);

                    var segGO = SpawnTubeSegment($"Branch_R{role}_B{bi}_Match", spine,
                        tubeRadius, bin.TrackColor, vertexAlpha: 1f, t0, t1);

                    var matchedNotes = snapshot.CollectedNotes?
                        .Where(n => n.TrackColor == bin.TrackColor
                                 && n.BinIndex   == bin.BinIndex
                                 && n.IsMatched)
                        .ToList();
                    SpawnBuds(matchedNotes, spine, bin.BinIndex, snapshot, isMatched: true,
                              segParent: segGO.transform, growStart: t0, growEnd: t1);
                    segIndex++;
                }

                // ── Unmatched fork ────────────────────────────────────────
                if (hasFork)
                {
                    Vector3 forkDir = (segDir
                        + sideAxis * Mathf.Sin(_forkAngleDeg * Mathf.Deg2Rad)
                        + fwdAxis  * Mathf.Sin(_forkAngleDeg * Mathf.Deg2Rad * 0.4f)).normalized;

                    float t0 = segIndex * segNormWidth;
                    float t1 = t0 + segNormWidth;

                    Vector3[] forkSpine = BuildSpiralSpine(segStart, forkDir, segHeight * 0.85f,
                        driftRadius * 0.7f, bin.CollectedSteps, snapshot.TotalSteps);
                    Color barrenCol = Desaturate(bin.TrackColor, 0.35f);

                    var forkGO = SpawnTubeSegment($"Branch_R{role}_B{bi}_Unmatch", forkSpine,
                        tubeRadius * 0.7f, barrenCol, vertexAlpha: barrenBudAlpha, t0, t1);

                    var unmatchedNotes = snapshot.CollectedNotes?
                        .Where(n => n.TrackColor == bin.TrackColor
                                 && n.BinIndex   == bin.BinIndex
                                 && !n.IsMatched)
                        .ToList();
                    SpawnBuds(unmatchedNotes, forkSpine, bin.BinIndex, snapshot, isMatched: false,
                              segParent: forkGO.transform, growStart: t0, growEnd: t1);
                    segIndex++;
                }
            }
        }
    }
    public IEnumerator TransitionOutAndClear(float? durationOverride = null)
    {
        if (_root == null || _root.childCount == 0)
            yield break;

        yield return StartCoroutine(AnimateTransitionOut(durationOverride ?? transitionOutSeconds));
        ClearAll();
    }
    private IEnumerator AnimateTransitionOut(float durationSec)
    {
        float dur = Mathf.Max(0.05f, durationSec);

        Vector3 startPos   = _root.position;
        Vector3 startScale = _root.localScale;
        Quaternion startRot = _root.rotation;

        Vector3 dir = transitionOutDirection.sqrMagnitude > 0.0001f
            ? transitionOutDirection.normalized
            : Vector3.up;

        Vector3 endPos = startPos + dir * S(transitionOutDistance);

        float elapsed = 0f;
        bool burstPlayed = false;

        while (elapsed < dur)
        {
            elapsed += Time.deltaTime;
            float u = Mathf.Clamp01(elapsed / dur);

            float moveT  = transitionOutMoveCurve  != null ? transitionOutMoveCurve.Evaluate(u)  : u;
            float scaleT = transitionOutScaleCurve != null ? transitionOutScaleCurve.Evaluate(u) : (1f - u);

            _root.position = Vector3.LerpUnclamped(startPos, endPos, moveT);
            _root.localScale = startScale * Mathf.Max(0f, scaleT);

            if (transitionOutSpinDeg > 0f)
            {
                float spin = transitionOutSpinDeg * moveT;
                _root.rotation = startRot * Quaternion.AngleAxis(spin, dir);
            }

            // Fire burst near the end so the final pop masks the last collapse.
            if (!burstPlayed && u >= 0.82f)
            {
                burstPlayed = true;
                if (spawnTeleportBurst)
                    SpawnTeleportBurst(_root.position);
            }

            yield return null;
        }

        // Snap to final collapsed state.
        _root.position = endPos;
        _root.localScale = Vector3.zero;
    }
    private void SpawnTeleportBurst(Vector3 worldPos)
{
    var burstGO = new GameObject("CoralTeleportBurst");
    burstGO.transform.position = worldPos;
    burstGO.transform.rotation = Quaternion.identity;

    var ps = burstGO.AddComponent<ParticleSystem>();
    var psr = burstGO.GetComponent<ParticleSystemRenderer>();

    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
    ps.Clear(true);

    var main = ps.main;
    main.playOnAwake = false;
    main.loop = false;
    main.duration = teleportBurstLifetime;
    main.startLifetime = new ParticleSystem.MinMaxCurve(teleportBurstLifetime * 0.7f, teleportBurstLifetime);
    main.startSpeed = new ParticleSystem.MinMaxCurve(S(teleportBurstSpeedMin), S(teleportBurstSpeedMax));
    main.startSize = new ParticleSystem.MinMaxCurve(S(teleportBurstSizeMin), S(teleportBurstSizeMax));
    main.simulationSpace = ParticleSystemSimulationSpace.World;
    main.maxParticles = Mathf.Max(teleportBurstCount, 8);

    var emission = ps.emission;
    emission.enabled = false;

    var shape = ps.shape;
    shape.enabled = true;
    shape.shapeType = ParticleSystemShapeType.Sphere;
    shape.radius = S(0.06f);
    shape.arcMode = ParticleSystemShapeMultiModeValue.Random;

    var col = ps.colorOverLifetime;
    col.enabled = true;

    Gradient g = new Gradient();
    g.SetKeys(
        new[]
        {
            new GradientColorKey(Color.white, 0f),
            new GradientColorKey(teleportBurstTint, 0.35f),
            new GradientColorKey(new Color(teleportBurstTint.r, teleportBurstTint.g, teleportBurstTint.b, 1f), 1f)
        },
        new[]
        {
            new GradientAlphaKey(0.95f, 0f),
            new GradientAlphaKey(0.65f, 0.25f),
            new GradientAlphaKey(0f, 1f)
        }
    );
    col.color = new ParticleSystem.MinMaxGradient(g);

    var sizeOverLifetime = ps.sizeOverLifetime;
    sizeOverLifetime.enabled = true;
    sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(
        1f,
        new AnimationCurve(
            new Keyframe(0f, 0.25f),
            new Keyframe(0.15f, 1f),
            new Keyframe(1f, 0.05f)
        )
    );

    var noise = ps.noise;
    noise.enabled = true;
    noise.strength = new ParticleSystem.MinMaxCurve(0.08f);
    noise.frequency = 0.4f;
    noise.damping = true;

    if (psr != null)
    {
        psr.renderMode = ParticleSystemRenderMode.Billboard;
        psr.sortMode = ParticleSystemSortMode.Distance;
        psr.alignment = ParticleSystemRenderSpace.View;

        if (teleportBurstMaterial != null)
            psr.material = teleportBurstMaterial;
        else
            psr.material = GetOrCreateMaterial(branchMaterial, transparent: true);
    }

    ps.Emit(teleportBurstCount);
    ps.Play();

    Destroy(burstGO, teleportBurstLifetime + 0.5f);
}
    // ═══════════════════════════════════════════════════════════════════════
    //  Spine construction
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Builds an organic wandering spine for one branch segment using Catmull-Rom
    /// interpolation through noise-drifted control points, giving soft S-curves
    /// and lazy bends instead of a rigid helix.
    /// </summary>
    private Vector3[] BuildSpiralSpine(Vector3 start, Vector3 dir, float height,
                                        float driftRadius, List<int> steps, int totalSteps)
    {
        int rings = Mathf.Max(16, tubeRings);
        var controlPts = GenerateOrganicControlPoints(start, dir, height, driftRadius);
        return CatmullRomSpine(controlPts, rings);
    }
private void AttachBudSignalParticles(GameObject budGo, Color branchColor, bool isMatched)
{
    if (budGo == null) return;

    var ps = budGo.GetComponent<ParticleSystem>();
    if (ps == null)
        ps = budGo.AddComponent<ParticleSystem>();

    // Critical: stop immediately before touching duration/start values.
    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
    ps.Clear(true);

    var psr = budGo.GetComponent<ParticleSystemRenderer>();
    if (psr == null)
        psr = budGo.AddComponent<ParticleSystemRenderer>();

    var main = ps.main;
    main.playOnAwake = false;
    main.loop = false;
    main.duration = budParticleLifetimeMax;
    main.startLifetime = new ParticleSystem.MinMaxCurve(budParticleLifetimeMin, budParticleLifetimeMax);
    main.startSpeed = new ParticleSystem.MinMaxCurve(S(budParticleSpeedMin), S(budParticleSpeedMax));
    main.startSize = new ParticleSystem.MinMaxCurve(S(budParticleSizeMin), S(budParticleSizeMax));
    main.simulationSpace = ParticleSystemSimulationSpace.Local;
    main.maxParticles = 24;

    if (psr != null)
    {
        psr.renderMode = ParticleSystemRenderMode.Billboard;
        psr.sortMode = ParticleSystemSortMode.Distance;
        psr.alignment = ParticleSystemRenderSpace.View;

        if (budParticleMaterial != null)
            psr.material = budParticleMaterial;
    }

    var emission = ps.emission;
    emission.enabled = false;

    var shape = ps.shape;
    shape.enabled = true;
    shape.shapeType = ParticleSystemShapeType.Circle;
    shape.radius = Mathf.Max(S(0.0025f), budParticleRadiusMul * 0.05f);
    shape.arcMode = ParticleSystemShapeMultiModeValue.Random;

    var vel = ps.velocityOverLifetime;
    vel.enabled = true;

    if (isMatched)
    {
        vel.x = new ParticleSystem.MinMaxCurve(-S(0.02f), S(0.02f));
        vel.y = new ParticleSystem.MinMaxCurve(S(0.02f), S(0.10f));
        vel.z = new ParticleSystem.MinMaxCurve(-S(0.02f), S(0.02f));
    }
    else
    {
        vel.x = new ParticleSystem.MinMaxCurve(-S(0.05f), S(0.05f));
        vel.y = new ParticleSystem.MinMaxCurve(0f, S(0.04f));
        vel.z = new ParticleSystem.MinMaxCurve(-S(0.05f), S(0.05f));
    }

    var col = ps.colorOverLifetime;
    col.enabled = true;

    Gradient g = new Gradient();
    Color target = isMatched ? budMatchedColor : budUnmatchedColor;
    g.SetKeys(
        new[]
        {
            new GradientColorKey(branchColor, 0f),
            new GradientColorKey(Color.Lerp(branchColor, target, 0.5f), 0.45f),
            new GradientColorKey(target, 1f)
        },
        new[]
        {
            new GradientAlphaKey(0.85f, 0f),
            new GradientAlphaKey(0.35f, 0.65f),
            new GradientAlphaKey(0f, 1f)
        }
    );
    col.color = new ParticleSystem.MinMaxGradient(g);

    var sizeOverLifetime = ps.sizeOverLifetime;
    sizeOverLifetime.enabled = true;
    sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(
        1f,
        new AnimationCurve(
            new Keyframe(0f, 0.5f),
            new Keyframe(0.2f, 1f),
            new Keyframe(1f, 0.2f)
        )
    );

    var noise = ps.noise;
    noise.enabled = true;
    noise.strength = new ParticleSystem.MinMaxCurve(isMatched ? 0.05f : 0.11f);
    noise.frequency = 0.35f;
    noise.scrollSpeed = 0.15f;
    noise.damping = true;

    // Leave it clean and idle until reveal time.
    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
}
private void PlayBudSignal(GameObject budGo, bool isMatched)
{
    if (budGo == null) return;

    var ps = budGo.GetComponent<ParticleSystem>();
    if (ps == null) return;

    int burstCount = UnityEngine.Random.Range(budParticlesMin, budParticlesMax + 1);
    ps.Emit(burstCount);
}
    /// <summary>
    /// Generates 5 control points that wander gently off the main axis using
    /// overlapping sine waves seeded from the branch's world position,
    /// so every branch has a unique personality while staying predictable.
    /// </summary>
    private Vector3[] GenerateOrganicControlPoints(Vector3 start, Vector3 dir, float height, float driftRadius)
    {
        // Build a stable local frame from dir
        Vector3 up    = dir;
        Vector3 right = Vector3.Cross(dir, Vector3.up);
        if (right.sqrMagnitude < 1e-4f) right = Vector3.Cross(dir, Vector3.forward);
        right.Normalize();
        Vector3 fwd = Vector3.Cross(up, right).normalized;

        // Seed from position to give each branch its own wander personality
        float seed = start.x * 13.7f + start.y * 7.3f + start.z * 17.1f;

        const int numPts = 5;
        var pts = new Vector3[numPts];
        for (int i = 0; i < numPts; i++)
        {
            float t      = i / (float)(numPts - 1);
            float angle  = seed + t * Mathf.PI * 3.1f;
            float radius = driftRadius * Mathf.Sin(t * Mathf.PI); // zero at ends, max at middle

            // Two overlapping sine waves for organic, non-repeating drift
            float ox = Mathf.Sin(angle * 1.0f) * radius + Mathf.Sin(angle * 1.7f + 1.3f) * radius * 0.4f;
            float oz = Mathf.Cos(angle * 1.0f) * radius + Mathf.Cos(angle * 2.1f + 0.7f) * radius * 0.4f;

            pts[i] = start + up * (height * t) + right * ox + fwd * oz;
        }
        return pts;
    }

    /// <summary>
    /// Interpolates through control points using Catmull-Rom splines,
    /// producing <paramref name="totalRings"/> evenly-spaced sample points.
    /// </summary>
    private static Vector3[] CatmullRomSpine(Vector3[] pts, int totalRings)
    {
        int n = pts.Length;
        if (n < 2) return pts;

        var result = new Vector3[totalRings];
        int segs   = n - 1;

        for (int ri = 0; ri < totalRings; ri++)
        {
            float globalT = (float)ri / (totalRings - 1) * segs;
            int   seg     = Mathf.Min(Mathf.FloorToInt(globalT), segs - 1);
            float t       = globalT - seg;

            Vector3 p0 = pts[Mathf.Max(seg - 1, 0)];
            Vector3 p1 = pts[seg];
            Vector3 p2 = pts[Mathf.Min(seg + 1, n - 1)];
            Vector3 p3 = pts[Mathf.Min(seg + 2, n - 1)];

            result[ri] = 0.5f * (
                2f * p1 +
                (-p0 + p2) * t +
                (2f * p0 - 5f * p1 + 4f * p2 - p3) * t * t +
                (-p0 + 3f * p1 - 3f * p2 + p3) * t * t * t
            );
        }
        return result;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Tube segment
    // ═══════════════════════════════════════════════════════════════════════

    private GameObject SpawnTubeSegment(string segName, Vector3[] spine, float baseRadius,
                                         Color col, float vertexAlpha,
                                         float growStart01, float growEnd01)
    {
        var go = new GameObject(segName);
        go.transform.SetParent(_root, false);

        if (spine == null || spine.Length < 2) return go;

        // ── Outline tube ──────────────────────────────────────────────────────
        var outlineGO = new GameObject("Outline");
        outlineGO.transform.SetParent(go.transform, false);
        var outlineMF = outlineGO.AddComponent<MeshFilter>();
        var outlineMR = outlineGO.AddComponent<MeshRenderer>();
        outlineMR.sharedMaterial = GetOrCreateMaterial(branchMaterial, transparent: false);
        ApplyColorToRenderer(outlineMR, col, alpha: 1f);
        ApplyOrganicTexture(outlineMR, col, _outlineNoiseTex);

        var outlineMesh = new Mesh { name = segName + "_Outline" };
        outlineMF.sharedMesh = outlineMesh;
        var outlineData = BuildTube(outlineMesh, spine, baseRadius * outlineRadiusMul, col, vertexAlpha: 1f);

        // ── Fill tube ─────────────────────────────────────────────────────────
        var fillGO = new GameObject("Fill");
        fillGO.transform.SetParent(go.transform, false);
        var fillMF = fillGO.AddComponent<MeshFilter>();
        var fillMR = fillGO.AddComponent<MeshRenderer>();
        fillMR.sharedMaterial = GetOrCreateMaterial(branchFillMaterial, transparent: true);
        ApplyColorToRenderer(fillMR, col, alpha: fillAlpha);
        ApplyOrganicTexture(fillMR, col, _fillNoiseTex);

        var fillMesh = new Mesh { name = segName + "_Fill" };
        fillMF.sharedMesh = fillMesh;
        var fillData = _geometryBuilder.BuildTube(fillMesh, spine, baseRadius * 0.55f, tubeSides, tapePower, minTipRadius, worldScale, col, fillAlpha * vertexAlpha);

        // Hide initially for animation
        outlineMesh.SetTriangles(Array.Empty<int>(), 0, true);
        fillMesh.SetTriangles(Array.Empty<int>(), 0, true);

        _segments.Add(new TubeSegmentRuntime
        {
            outlineMesh      = outlineMesh,
            outlineFullTris  = outlineData.fullTris,
            outlineSegCount  = outlineData.segCount,
            fillMesh         = fillMesh,
            fillFullTris     = fillData.fullTris,
            fillSegCount     = fillData.segCount,
            growStartNorm    = growStart01,
            growEndNorm      = growEnd01,
        });

        return go;
    }

    private (int[] fullTris, int segCount) BuildTube(Mesh mesh, Vector3[] spine,
                                                       float baseRadius, Color col, float vertexAlpha)
    {
        _geometryBuilder ??= new MotifCoralGeometryBuilder();
        return _geometryBuilder.BuildTube(mesh, spine, baseRadius, tubeSides, tapePower, minTipRadius, worldScale, col, vertexAlpha);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Buds
    // ═══════════════════════════════════════════════════════════════════════

    private void SpawnBuds(List<MotifSnapshot.NoteEntry> notes, Vector3[] spine,
                            int binIndex, MotifSnapshot snapshot,
                            bool isMatched, Transform segParent,
                            float growStart, float growEnd)
    {
        if (notes == null || notes.Count == 0 || spine == null || spine.Length < 2) return;

        int binSteps = snapshot.TotalSteps > 0 ? snapshot.TotalSteps : 16;

        for (int ni = 0; ni < notes.Count; ni++)
        {
            var note = notes[ni];

            // Map the note's step to a 0..1 position along this segment's spine
            float t01 = binSteps > 1
                ? Mathf.Clamp01((float)(note.Step % binSteps) / (binSteps - 1))
                : 0.5f;

            // Interpolate position along spine
            float spineT  = t01 * (spine.Length - 1);
            int   spineI  = Mathf.Min(Mathf.FloorToInt(spineT), spine.Length - 2);
            float spineFr = spineT - spineI;
            Vector3 budPos = Vector3.Lerp(spine[spineI], spine[spineI + 1], spineFr);

            bool isRoot = snapshot.MotifKeyRootMidi > 0 && (note.Note % 12) == (snapshot.MotifKeyRootMidi % 12);
            float radius = S(budRadiusBase) * (isRoot ? rootNoteScaleBoost : 1f);
            if (!isMatched) radius *= 0.75f;

            Color budCol = isMatched ? note.TrackColor : Desaturate(note.TrackColor, 0.2f);
            budCol.a = isMatched ? 0.85f : barrenBudAlpha;

            GameObject bud = isMatched
                ? SpawnBudSphere(budPos, radius, budCol, isRoot, segParent)
                : SpawnBudRing(budPos, radius, budCol, segParent);

            // Start hidden — AnimateGrowth will reveal when grow clock passes showAtNorm
            bud.SetActive(false);

            // Show bud when the grow front reaches its position on this segment
            float showAtNorm = Mathf.Lerp(growStart, growEnd, t01);

            _buds.Add(new BudRuntime
            {
                go         = bud,
                showAtNorm = showAtNorm,
                isMatched  = isMatched,
                note       = note,
            });
            
        }
    }

    private GameObject SpawnBudSphere(Vector3 worldPos, float radius, Color col, bool isRoot, Transform parent)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = isRoot ? "Bud_Root" : "Bud_Match";
        go.transform.SetParent(parent ?? _root, false);
        go.transform.position   = worldPos;
        go.transform.localScale = Vector3.one * radius * 2f;

        var coll = go.GetComponent<Collider>();
        if (coll) Destroy(coll);

        var mr = go.GetComponent<MeshRenderer>();
        mr.sharedMaterial = GetOrCreateMaterial(budMaterial, transparent: true);
        ApplyColorToRenderer(mr, col, alpha: col.a);
        ApplyOrganicTexture(mr, col, _outlineNoiseTex);

        if (isRoot)
        {
            var halo = SpawnBudRing(worldPos, radius * 1.6f, col * 1.3f, go.transform);
            halo.SetActive(true);
        }

        return go;
    }

    private GameObject SpawnBudRing(Vector3 worldPos, float radius, Color col, Transform parent, bool isMatched = false)
    {
        var go = new GameObject(isMatched ? "Bud_Ring_Matched" : "Bud_Ring_Unmatched");
        go.transform.SetParent(parent ?? _root, false);
        go.transform.position = worldPos;
        go.transform.localRotation = Quaternion.identity;
        go.transform.localScale = Vector3.one;

        var mf = go.AddComponent<MeshFilter>();
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = GetOrCreateMaterial(barrenBudMaterial, transparent: true);
        ApplyColorToRenderer(mr, col, alpha: barrenBudAlpha);

        int ringSegs = 16;
        float ringR  = radius * 0.6f;

        // IMPORTANT: local-space ring around this bud object's origin
        var spine = new Vector3[ringSegs + 1];
        for (int i = 0; i <= ringSegs; i++)
        {
            float a = (i / (float)ringSegs) * Mathf.PI * 2f;
            spine[i] = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * ringR;
        }

        var mesh = new Mesh { name = "BudRing" };
        mf.sharedMesh = mesh;
        BuildTube(mesh, spine, Mathf.Max(S(0.005f), radius * 0.08f), col, barrenBudAlpha);

        AttachBudSignalParticles(go, col, isMatched);

        return go;
    }
    // ═══════════════════════════════════════════════════════════════════════
    //  Center sphere & helpers
    // ═══════════════════════════════════════════════════════════════════════

    private void SpawnCenterSphere(Vector3 center, float radius)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = "CoralCenter";
        go.transform.SetParent(_root, false);
        go.transform.position   = center;
        go.transform.localScale = Vector3.one * radius * 2f;

        var coll = go.GetComponent<Collider>();
        if (coll) Destroy(coll);

        var mr = go.GetComponent<MeshRenderer>();
        mr.sharedMaterial = GetOrCreateMaterial(branchMaterial, transparent: false);
        ApplyColorToRenderer(mr, Color.white, alpha: 1f);
        ApplyOrganicTexture(mr, Color.white, _outlineNoiseTex);
    }

    /// <summary>
    /// Converts azimuth (degrees around Y) and elevation (degrees above/below equator)
    /// into a unit direction vector. Elevation 0 = horizontal, +90 = straight up.
    /// </summary>
    private static Vector3 AzimuthElevationDir(float azimuthDeg, float elevationDeg)
    {
        float az    = azimuthDeg   * Mathf.Deg2Rad;
        float el    = elevationDeg * Mathf.Deg2Rad;
        float cosEl = Mathf.Cos(el);
        return new Vector3(
            cosEl * Mathf.Sin(az),
            Mathf.Sin(el),
            cosEl * Mathf.Cos(az)
        ).normalized;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Grow animation
    // ═══════════════════════════════════════════════════════════════════════

    private IEnumerator AnimateGrowth(float durationSec, Func<Vector2> getSteer,
        IReadOnlyDictionary<(int step, Color32 trackColor), Vector3> noteOrigins = null)
    {
        float dur     = Mathf.Max(0.05f, durationSec);
        float elapsed = 0f;

        while (elapsed < dur)
        {
            elapsed += Time.deltaTime;
            float u     = Mathf.Clamp01(elapsed / dur);
            float eased = growCurve != null ? growCurve.Evaluate(u) : u;

            // Modulate fork angle from stick input
            if (getSteer != null)
            {
                Vector2 steer = getSteer();
                SetForkAngle(steer.x * 0.5f + 0.5f);
            }

            // Reveal tube segments progressively
            foreach (var seg in _segments)
            {
                if (eased < seg.growStartNorm) continue;

                float segU = seg.growEndNorm > seg.growStartNorm
                    ? Mathf.Clamp01((eased - seg.growStartNorm) / (seg.growEndNorm - seg.growStartNorm))
                    : 1f;

                int outlineReveal = Mathf.RoundToInt(segU * seg.outlineSegCount);
                int fillReveal    = Mathf.RoundToInt(segU * seg.fillSegCount);

                if (seg.outlineFullTris != null && outlineReveal > 0)
                    seg.outlineMesh.SetTriangles(seg.outlineFullTris, 0, outlineReveal * tubeSides * 6, 0, true);

                if (seg.fillFullTris != null && fillReveal > 0)
                    seg.fillMesh.SetTriangles(seg.fillFullTris, 0, fillReveal * tubeSides * 6, 0, true);
            }

            // Reveal buds when grow front passes their showAtNorm
            foreach (var bud in _buds)
                if (bud.go != null && !bud.go.activeSelf && eased >= bud.showAtNorm)
                {
                    bud.go.SetActive(true);
                    PlayBudSignal(bud.go, bud.isMatched);

                    // Shoot the collected note marker from the grid toward this bud
                    if (noteOrigins != null && bud.note != null)
                    {
                        var key = (bud.note.Step, (Color32)bud.note.TrackColor);
                        if (noteOrigins.TryGetValue(key, out Vector3 origin))
                            StartCoroutine(AnimateNoteShot(origin, bud.go.transform.position, bud.note.TrackColor));
                    }
                }
            yield return null;
        }

        // Ensure fully revealed
        ForceFullReveal();
    }

    private IEnumerator AnimateNoteShot(Vector3 from, Vector3 to, Color col, float dur = -1f)
    {
        if (dur < 0f) dur = noteShotDuration;
        dur = Mathf.Max(0.05f, dur);

        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = "NoteShot";
        go.transform.SetParent(_root, false);
        go.transform.position = from;

        var coll = go.GetComponent<Collider>();
        if (coll) Destroy(coll);

        float radius = S(noteShotScale);
        go.transform.localScale = Vector3.one * radius * 2f;

        var mr = go.GetComponent<MeshRenderer>();
        mr.sharedMaterial = GetOrCreateMaterial(budMaterial, transparent: true);
        col.a = 0.85f;
        ApplyColorToRenderer(mr, col, alpha: col.a);

        float t = 0f;
        while (t < dur)
        {
            if (!go) yield break;
            t += Time.deltaTime;
            float u     = Mathf.Clamp01(t / dur);
            float eased = 1f - (1f - u) * (1f - u); // ease-out: fast start, slow arrival

            go.transform.position   = Vector3.Lerp(from, to, eased);
            go.transform.localScale = Vector3.one * radius * 2f * Mathf.Lerp(1f, 0.2f, u);
            yield return null;
        }

        if (go) Destroy(go);
    }

    private void ForceFullReveal()
    {
        foreach (var seg in _segments)
        {
            if (seg.outlineFullTris != null && seg.outlineMesh != null)
                seg.outlineMesh.SetTriangles(seg.outlineFullTris, 0, true);
            if (seg.fillFullTris != null && seg.fillMesh != null
                && seg.fillFullTris != null)
                seg.fillMesh.SetTriangles(seg.fillFullTris, 0, true);
        }
        foreach (var bud in _buds)
            if (bud.go != null) bud.go.SetActive(true);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Cleanup
    // ═══════════════════════════════════════════════════════════════════════

    private void ClearAll()
    {
        StopAllCoroutines();
        _segments.Clear();

        foreach (var b in _buds) if (b.go) Destroy(b.go);
        _buds.Clear();

        if (_root == null) return;

        _root.localScale = Vector3.one;
        _root.localRotation = Quaternion.identity;

        for (int i = _root.childCount - 1; i >= 0; i--)
            Destroy(_root.GetChild(i).gameObject);
    }
    // ═══════════════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns a direction leaned <paramref name="leanDeg"/> degrees away from vertical,
    /// pointing in the horizontal direction given by <paramref name="azimuthDeg"/>.
    /// </summary>
    private static Vector3 AzimuthLeanDir(float azimuthDeg, float leanDeg)
    {
        float az   = azimuthDeg * Mathf.Deg2Rad;
        float lean = leanDeg   * Mathf.Deg2Rad;
        return new Vector3(
            Mathf.Sin(lean) * Mathf.Cos(az),
            Mathf.Cos(lean),
            Mathf.Sin(lean) * Mathf.Sin(az)
        ).normalized;
    }

    /// <summary>Rotates <paramref name="dir"/> by <paramref name="deg"/> around the world Y axis.</summary>
    private static Vector3 RotateAroundY(Vector3 dir, float deg)
        => Quaternion.AngleAxis(deg, Vector3.up) * dir;

    /// <summary>
    /// Returns the normalised step spread of <paramref name="steps"/> within a bin of
    /// <paramref name="binSteps"/> steps. 0 = all on one step, 1 = spread across full bin.
    /// </summary>
    private static float StepSpread(List<int> steps, int binSteps)
    {
        if (steps == null || steps.Count <= 1 || binSteps <= 1) return 0f;
        int lo = steps.Min(), hi = steps.Max();
        return Mathf.Clamp01((hi - lo) / (float)(binSteps - 1));
    }

    /// <summary>Applies worldScale to a dimension value.</summary>
    private float S(float v) => v * worldScale;

    private float tubeNoteRadius(int noteCount)
        => noteCount * tubeRadiusPerNote;

    private static Color Desaturate(Color c, float satMul)
    {
        Color.RGBToHSV(c, out float h, out float s, out float v);
        // Compensate brightness slightly when desaturating so unmatched branches
        // read as pale/ghostly rather than dark/muddy.
        float vBoost = Mathf.Lerp(1f, 1.25f, 1f - satMul);
        return Color.HSVToRGB(h, s * satMul, Mathf.Min(1f, v * vBoost));
    }

    private Material GetOrCreateMaterial(Material source, bool transparent)
    {
        if (source != null) return source;

        Shader sh = transparent
            ? (Shader.Find("Particles/Standard Unlit") ?? Shader.Find("Particles/Alpha Blended") ?? Shader.Find("Sprites/Default"))
            : (Shader.Find("Particles/Standard Unlit") ?? Shader.Find("Unlit/Color"));

        var mat = new Material(sh != null ? sh : Shader.Find("Standard"));
        if (mat.HasProperty("_Color"))     mat.SetColor("_Color",     Color.white);
        if (mat.HasProperty("_TintColor")) mat.SetColor("_TintColor", Color.white);
        mat.renderQueue = transparent ? 3100 : 2000;
        if (transparent)
        {
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite",   0);
        }
        return mat;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Organic noise texture generation
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Generates a tileable organic noise texture combining:
    ///   - Cellular (Worley) noise  → cell-wall / coral-polyp pattern
    ///   - FBM (fractal Brownian motion) → surface irregularity
    ///   - Optional radial edge darkening → tube cross-section depth cue
    /// Result is a greyscale+alpha Texture2D. Multiply it against track colour in the shader.
    /// </summary>
    private Texture2D GenerateOrganicNoise(int size, bool darkenEdges)
    {
        size = Mathf.Max(4, size);
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, mipChain: true);
        tex.wrapMode = TextureWrapMode.Repeat;

        // Pre-generate Worley cell centres (tileable)
        int numCells = 12;
        var cells = new Vector2[numCells];
        var rng = new System.Random(42);
        for (int i = 0; i < numCells; i++)
            cells[i] = new Vector2((float)rng.NextDouble(), (float)rng.NextDouble());

        var pixels = new Color[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float u = (float)x / size;
                float v = (float)y / size;

                float cell = WorleyTileable(u, v, cells, noiseScale);
                float fbm  = FbmNoise(u * noiseScale, v * noiseScale, 4);

                float value = Mathf.Lerp(1f, cell, cellularStrength)
                            * Mathf.Lerp(1f, fbm,  fbmStrength);
                value = Mathf.Clamp01(value);

                // ── Edge darkening (simulates tube curvature / subsurface) ───
                if (darkenEdges)
                {
                    float edgeFactor = Mathf.Sin(u * Mathf.PI);
                    value = Mathf.Lerp(value * (1f - edgeDarken), value, edgeFactor);
                }

                pixels[y * size + x] = new Color(value, value, value, 1f);
            }
        }

        tex.SetPixels(pixels);
        tex.Apply(updateMipmaps: true);
        return tex;
    }

    // Tileable Worley noise: returns F1 distance to nearest cell, wrapping at [0,1]
    private static float WorleyTileable(float u, float v, Vector2[] cells, float scale)
    {
        float su = u * scale % 1f;
        float sv = v * scale % 1f;
        float minDist = float.MaxValue;
        foreach (var c in cells)
        {
            for (int ox = -1; ox <= 1; ox++)
            for (int oy = -1; oy <= 1; oy++)
            {
                float dx = su - (c.x + ox);
                float dy = sv - (c.y + oy);
                float d  = Mathf.Sqrt(dx * dx + dy * dy);
                if (d < minDist) minDist = d;
            }
        }
        return Mathf.Clamp01(minDist);
    }

    // Simple fractal Brownian motion over a value noise grid
    private static float FbmNoise(float x, float y, int octaves)
    {
        float val = 0f, amp = 0.5f, freq = 1f, max = 0f;
        for (int i = 0; i < octaves; i++)
        {
            val += ValueNoise(x * freq, y * freq) * amp;
            max  += amp;
            amp  *= 0.5f;
            freq *= 2.1f;
        }
        return val / max;
    }

    // Smooth value noise via quintic interpolation
    private static float ValueNoise(float x, float y)
    {
        int ix = Mathf.FloorToInt(x), iy = Mathf.FloorToInt(y);
        float fx = x - ix, fy = y - iy;
        float ux = fx * fx * fx * (fx * (fx * 6f - 15f) + 10f);
        float uy = fy * fy * fy * (fy * (fy * 6f - 15f) + 10f);
        float a = Hash(ix,     iy);
        float b = Hash(ix + 1, iy);
        float c = Hash(ix,     iy + 1);
        float d = Hash(ix + 1, iy + 1);
        return Mathf.Lerp(Mathf.Lerp(a, b, ux), Mathf.Lerp(c, d, ux), uy);
    }

    private static float Hash(int x, int y)
    {
        int n = x * 127 + y * 311;
        n = (n << 13) ^ n;
        return (1f - ((n * (n * n * 15731 + 789221) + 1376312589) & 0x7fffffff) / 1073741824f) * 0.5f + 0.5f;
    }

    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Applies the track colour via MaterialPropertyBlock so it works regardless
    /// of whether the material uses _Color, _BaseColor, or _TintColor.
    /// </summary>
    private static void ApplyColorToRenderer(MeshRenderer mr, Color col, float alpha)
    {
        col.a = alpha;
        var mpb = new MaterialPropertyBlock();
        mr.GetPropertyBlock(mpb);
        mpb.SetColor("_Color",     col);
        mpb.SetColor("_BaseColor", col);  // URP Lit
        mpb.SetColor("_TintColor", col);  // Legacy particles
        mr.SetPropertyBlock(mpb);
    }

    /// <summary>
    /// Applies the organic noise texture to a renderer via MPB.
    /// The texture is set as _MainTex / _BaseMap so it multiplies over the track colour.
    /// </summary>
    private static void ApplyOrganicTexture(MeshRenderer mr, Color col, Texture2D noiseTex)
    {
        if (noiseTex == null) return;
        var mpb = new MaterialPropertyBlock();
        mr.GetPropertyBlock(mpb);
        mpb.SetTexture("_MainTex", noiseTex);
        mpb.SetTexture("_BaseMap", noiseTex);  // URP
        mr.SetPropertyBlock(mpb);
    }
}
