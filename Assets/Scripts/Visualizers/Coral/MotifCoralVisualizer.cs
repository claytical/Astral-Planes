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

    // Runtime noise textures — generated once in Awake, shared across all segments
    private Texture2D _outlineNoiseTex;
    private Texture2D _fillNoiseTex;

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

    // One entry per bud spawned — hidden initially, revealed when grow clock reaches showAtNorm
    private struct BudRuntime
    {
        public GameObject go;
        public float      showAtNorm; // 0..1 normalised grow time when this bud pops visible
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
//            transform.localScale = Vector3.one;
        }

        var rootGO = new GameObject("MotifCoralRoot");
        rootGO.transform.SetParent(transform, false);
        rootGO.transform.localPosition = origin;
        _root = rootGO.transform;

        _forkAngleDeg = defaultForkAngleDeg;

        _outlineNoiseTex = GenerateOrganicNoise(noiseTexSize, darkenEdges: true);
        _fillNoiseTex    = GenerateOrganicNoise(noiseTexSize, darkenEdges: false);

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
    /// The trunk is placed at the lower-center of the area; arms grow upward into view.
    /// </summary>
    public void FitToPlayArea(float areaWidth, float areaHeight, float areaCenterX, float areaCenterY)
    {
        // The coral is a ball: center sphere + branches radiating in all directions.
        // Total extent = sphereRadius + max branch length, in every direction.
        // We want the whole ball to fit within the smaller of width/height * 0.75.
        float targetExtent = Mathf.Min(areaWidth, areaHeight) * 0.75f * 0.5f; // radius of bounding sphere

        // Raw extent at worldScale=1: trunk radius + longest possible arm
        float rawBranchLen = 4f * segHeightMax; // 4 bins at max height
        float rawExtent    = trunkRadius + rawBranchLen;

        worldScale = rawExtent > 0f
            ? Mathf.Clamp(targetExtent / rawExtent, 0.05f, 50f)
            : 1f;

        // Center the ball in the play area
        origin = new Vector3(areaCenterX, areaCenterY, 0f);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Public API
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Instantly build and display the full coral for this snapshot.</summary>
    public void RenderMotifCoral(PhaseSnapshot snapshot)
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
    public IEnumerator GrowMotifCoral(PhaseSnapshot snapshot, float durationSec, Func<Vector2> getSteer = null)
    {
        ClearAll();
        if (snapshot == null) { yield return new WaitForSeconds(durationSec); yield break; }

        BuildCoral(snapshot);
        yield return StartCoroutine(AnimateGrowth(durationSec, getSteer));
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

    private void BuildCoral(PhaseSnapshot snapshot)
    {
        if (snapshot.TrackBins == null || snapshot.TrackBins.Count == 0) return;

        // ── Central sphere ────────────────────────────────────────────────
        // All branches radiate from the surface of this sphere.
        // Its radius is worldScale * trunkRadius so it scales proportionally.
        Vector3 center     = origin;
        float   sphereR    = S(trunkRadius);
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
                float azimuth   = baseAz + bi * binRotationDeg;

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

    /// <summary>
    /// Generates 5 control points that wander gently off the main axis using
    /// overlapping sine waves seeded from the branch's world position,
    /// so every branch has a unique personality while staying predictable.
    /// Drift is enveloped to zero at base and tip so branches connect cleanly.
    /// </summary>
    private Vector3[] GenerateOrganicControlPoints(Vector3 start, Vector3 dir, float height, float driftRadius)
    {
        Vector3 up   = dir.normalized;
        Vector3 side = Vector3.Cross(up, Vector3.forward);
        if (side.sqrMagnitude < 1e-5f) side = Vector3.Cross(up, Vector3.right);
        side.Normalize();
        Vector3 fwd = Vector3.Cross(side, up).normalized;

        float safeDrift = Mathf.Min(driftRadius, height * 0.38f);

        // Deterministic per-branch seed from world position
        float seed = start.x * 1.7f + start.y * 3.1f + start.z * 2.3f;

        const int n = 6;
        var pts = new Vector3[n];
        pts[0]     = start;
        pts[n - 1] = start + up * height;

        for (int i = 1; i < n - 1; i++)
        {
            float t        = i / (float)(n - 1);
            float envelope = Mathf.Sin(t * Mathf.PI); // tapers to 0 at both ends
            float ds       = Mathf.Sin(seed + t * 5.3f) * safeDrift;
            float df       = Mathf.Sin(seed * 1.4f + t * 3.7f + 1.1f) * safeDrift * 0.55f;
            pts[i] = start + up * (t * height)
                           + side * (ds * envelope)
                           + fwd  * (df * envelope);
        }

        return pts;
    }

    /// <summary>
    /// Samples a clamped Catmull-Rom spline through control points into
    /// evenly-spaced spine positions.
    /// </summary>
    private static Vector3[] CatmullRomSpine(Vector3[] pts, int ringCount)
    {
        var spine = new Vector3[ringCount];
        int n     = pts.Length;

        for (int r = 0; r < ringCount; r++)
        {
            float global = (float)r / (ringCount - 1) * (n - 1);
            int   i1     = Mathf.Clamp(Mathf.FloorToInt(global), 0, n - 2);
            float lt     = global - i1;

            Vector3 p0 = pts[Mathf.Max(i1 - 1, 0)];
            Vector3 p1 = pts[i1];
            Vector3 p2 = pts[Mathf.Min(i1 + 1, n - 1)];
            Vector3 p3 = pts[Mathf.Min(i1 + 2, n - 1)];

            spine[r] = CatmullRom(p0, p1, p2, p3, lt);
        }

        return spine;
    }

    private static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        float t2 = t * t, t3 = t2 * t;
        return 0.5f * (
              2f * p1
            + (-p0 + p2) * t
            + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2
            + (-p0 + 3f * p1 - 3f * p2 + p3) * t3
        );
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Tube mesh
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
        var fillData = BuildTube(fillMesh, spine, baseRadius * 0.55f, col, vertexAlpha: fillAlpha * vertexAlpha);

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
        int rings    = spine.Length;
        int segCount = rings - 1;
        int sides    = Mathf.Max(3, tubeSides);

        int vCount   = rings * sides;
        int idxCount = segCount * sides * 6;

        var verts = new Vector3[vCount];
        var norms = new Vector3[vCount];
        var uvs   = new Vector2[vCount];
        var cols  = new Color[vCount];
        var tris  = new int[idxCount];

        // Parallel-transport frame
        Vector3 prevNorm = Vector3.up;
        if ((spine[1] - spine[0]).normalized is Vector3 initTan
            && Mathf.Abs(Vector3.Dot(initTan, prevNorm)) > 0.95f)
            prevNorm = Vector3.right;

        for (int r = 0; r < rings; r++)
        {
            Vector3 tangent = r < rings - 1
                ? (spine[r + 1] - spine[r])
                : (spine[r] - spine[r - 1]);
            if (tangent.sqrMagnitude < 1e-6f) tangent = prevNorm;
            tangent.Normalize();

            Vector3 normal = prevNorm - Vector3.Dot(prevNorm, tangent) * tangent;
            if (normal.sqrMagnitude < 1e-6f)
            {
                normal = Vector3.Cross(tangent, Vector3.right);
                if (normal.sqrMagnitude < 1e-6f) normal = Vector3.Cross(tangent, Vector3.up);
            }
            normal.Normalize();
            Vector3 binormal = Vector3.Cross(tangent, normal).normalized;
            prevNorm = normal;

            float t      = rings <= 1 ? 0f : r / (float)(rings - 1);
            float taper  = Mathf.Max(S(minTipRadius) / baseRadius, Mathf.Pow(1f - t, tapePower));
            float radius = baseRadius * taper;

            // Colour: very subtle darkening toward tip so the taper reads without muddying the hue
            Color ringCol = Color.Lerp(col, col * 0.88f, t);
            ringCol.a = vertexAlpha;

            int baseIdx = r * sides;
            for (int s = 0; s < sides; s++)
            {
                float ang = (s / (float)sides) * Mathf.PI * 2f;
                Vector3 dir = Mathf.Cos(ang) * normal + Mathf.Sin(ang) * binormal;
                verts[baseIdx + s] = spine[r] + dir * radius;
                norms[baseIdx + s] = dir;
                uvs[baseIdx + s]   = new Vector2(s / (float)sides, t);
                cols[baseIdx + s]  = ringCol;
            }
        }

        int ti = 0;
        for (int r = 0; r < segCount; r++)
        {
            int r0 = r * sides, r1 = (r + 1) * sides;
            for (int s = 0; s < sides; s++)
            {
                int s1 = (s + 1) % sides;
                tris[ti++] = r0 + s;  tris[ti++] = r1 + s;  tris[ti++] = r0 + s1;
                tris[ti++] = r0 + s1; tris[ti++] = r1 + s;  tris[ti++] = r1 + s1;
            }
        }

        mesh.Clear();
        mesh.vertices  = verts;
        mesh.normals   = norms;
        mesh.uv        = uvs;
        mesh.colors    = cols;
        mesh.triangles = tris;
        mesh.RecalculateBounds();

        return (tris, segCount);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Bud spawning
    // ═══════════════════════════════════════════════════════════════════════

    private void SpawnBuds(List<PhaseSnapshot.NoteEntry> notes, Vector3[] spine,
                            int binIndex, PhaseSnapshot snapshot, bool isMatched,
                            Transform segParent, float growStart, float growEnd)
    {
        if (notes == null || notes.Count == 0 || spine == null || spine.Length < 2) return;

        // binSize is how many steps fit in ONE bin on ONE track.
        // snapshot.TotalSteps = drum.totalSteps (e.g. 16 or 64 for the base loop).
        // loopMultiplier tells us how many bins this track has.
        // For a 1-bin track: binSize = TotalSteps. For a 4-bin track: binSize = TotalSteps/4.
        // We infer it from the number of unique BinIndexes present for this segment's role.
        // Simplest safe fallback: binSize = TotalSteps (1 bin). TrackBinData.BinIndex tells us which bin.
        // The absolute step for bin b spans [b*binSize .. (b+1)*binSize - 1].
        // So bin-local step = note.Step - (binIndex * binSize).
        // We need binSize. Count bins for this track by scanning TrackBins for same role+color.
        int binsPerTrack = 1;
        if (snapshot.TrackBins != null && notes.Count > 0)
        {
            var roleColor = notes[0].TrackColor;
            binsPerTrack = Mathf.Max(1,
                snapshot.TrackBins.Count(b => b.TrackColor == roleColor));
        }
        int binSize  = Mathf.Max(1, snapshot.TotalSteps > 0 ? snapshot.TotalSteps / binsPerTrack : 16);
        int rootMidi = snapshot.MotifKeyRootMidi;

        foreach (var note in notes)
        {
            // Bin-local step: subtract the absolute offset of this bin's start
            int   localStep = note.Step - (binIndex * binSize);
            localStep = Mathf.Clamp(localStep, 0, binSize - 1);
            float t01 = binSize <= 1 ? 0.5f : (float)localStep / (binSize - 1);
            t01 = Mathf.Clamp01(t01);

            // World position on the spine
            float   spineT     = t01 * (spine.Length - 1);
            int     spineIdx   = Mathf.Min((int)spineT, spine.Length - 2);
            float   spineBlend = spineT - spineIdx;
            Vector3 budPos     = Vector3.Lerp(spine[spineIdx], spine[spineIdx + 1], spineBlend);

            // Offset the bud radially away from the spine so it sits on the branch surface,
            // not buried inside it. Direction = outward from the spine tangent's normal plane.
            Vector3 tangent = (spine[Mathf.Min(spineIdx + 1, spine.Length - 1)] - spine[spineIdx]).normalized;
            if (tangent.sqrMagnitude < 1e-5f) tangent = Vector3.up;
            Vector3 outDir = Vector3.Cross(tangent, Vector3.right);
            if (outDir.sqrMagnitude < 1e-5f) outDir = Vector3.Cross(tangent, Vector3.forward);
            outDir.Normalize();
            // Rotate outward direction per-note so buds orbit the branch instead of all facing the same way
            float orbitAngle = (note.Note % 12) * 30f; // 12 semitones → 360° spread
            outDir = Quaternion.AngleAxis(orbitAngle, tangent) * outDir;
            budPos += outDir * (S(tubeRadiusBase) * 3.5f);

            bool  isRoot = (note.Note == rootMidi);
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

            _buds.Add(new BudRuntime { go = bud, showAtNorm = showAtNorm });
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

    private GameObject SpawnBudRing(Vector3 worldPos, float radius, Color col, Transform parent)
    {
        var go = new GameObject("Bud_Ring");
        go.transform.SetParent(parent ?? _root, false);
        go.transform.position = worldPos;

        var mf = go.AddComponent<MeshFilter>();
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = GetOrCreateMaterial(barrenBudMaterial, transparent: true);
        ApplyColorToRenderer(mr, col, alpha: barrenBudAlpha);

        int   ringSegs = 16;
        float ringR    = radius * 0.6f;
        var spine = new Vector3[ringSegs + 1];
        for (int i = 0; i <= ringSegs; i++)
        {
            float a = (i / (float)ringSegs) * Mathf.PI * 2f;
            spine[i] = worldPos + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * ringR;
        }

        var mesh = new Mesh { name = "BudRing" };
        mf.sharedMesh = mesh;
        BuildTube(mesh, spine, Mathf.Max(S(0.005f), radius * 0.08f), col, barrenBudAlpha);

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
        float az  = azimuthDeg  * Mathf.Deg2Rad;
        float el  = elevationDeg * Mathf.Deg2Rad;
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

    private IEnumerator AnimateGrowth(float durationSec, Func<Vector2> getSteer)
    {
        float dur     = Mathf.Max(0.05f, durationSec);
        float elapsed = 0f;

        while (elapsed < dur)
        {
            elapsed += Time.deltaTime;
            float u     = Mathf.Clamp01(elapsed / dur);
            float eased = growCurve != null ? growCurve.Evaluate(u) : u;

            // Player stick modulates fork angle (stick X → spread)
            if (getSteer != null)
            {
                Vector2 stick = getSteer();
                SetForkAngle(Mathf.Clamp01(stick.magnitude));
            }

            // Reveal each tube segment proportionally within its grow window
            foreach (var seg in _segments)
            {
                float segU = Mathf.InverseLerp(seg.growStartNorm, seg.growEndNorm, eased);
                RevealSegment(seg, segU);
            }

            // Pop buds visible when the grow front reaches their position
            foreach (var bud in _buds)
            {
                if (bud.go != null && !bud.go.activeSelf && eased >= bud.showAtNorm)
                    bud.go.SetActive(true);
            }

            yield return null;
        }

        ForceFullReveal();
    }

    private void RevealSegment(in TubeSegmentRuntime seg, float t01)
    {
        t01 = Mathf.Clamp01(t01);
        int sides = Mathf.Max(3, tubeSides);
        int idxPerRing = sides * 6;

        RevealTube(seg.outlineMesh, seg.outlineFullTris, seg.outlineSegCount, t01, idxPerRing);
        RevealTube(seg.fillMesh,    seg.fillFullTris,    seg.fillSegCount,    t01, idxPerRing);
    }

    private static void RevealTube(Mesh mesh, int[] fullTris, int segCount, float t01, int idxPerRing)
    {
        if (mesh == null || fullTris == null || fullTris.Length == 0 || segCount <= 0) return;

        int visSegs  = Mathf.Clamp(Mathf.RoundToInt(t01 * segCount), 0, segCount);
        int idxCount = Mathf.Min(visSegs * idxPerRing, fullTris.Length);

        if (idxCount <= 0) { mesh.SetTriangles(Array.Empty<int>(), 0, true); return; }

        var tris = new int[idxCount];
        Array.Copy(fullTris, tris, idxCount);
        mesh.SetTriangles(tris, 0, true);
    }

    private void ForceFullReveal()
    {
        foreach (var seg in _segments)
        {
            if (seg.outlineMesh != null && seg.outlineFullTris != null)
                seg.outlineMesh.SetTriangles(seg.outlineFullTris, 0, true);
            if (seg.fillMesh != null && seg.fillFullTris != null)
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
        float az  = azimuthDeg * Mathf.Deg2Rad;
        float lean = leanDeg  * Mathf.Deg2Rad;
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
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, mipChain: true);
        tex.wrapMode   = TextureWrapMode.Repeat;
        tex.filterMode = FilterMode.Bilinear;

        // Scatter cell points for Worley noise (tileable by wrapping)
        const int cellCount = 12;
        var cells = new Vector2[cellCount];
        var rng = new System.Random(42);
        for (int i = 0; i < cellCount; i++)
            cells[i] = new Vector2((float)rng.NextDouble(), (float)rng.NextDouble());

        var pixels = new Color[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float u = x / (float)size;
                float v = y / (float)size;

                // ── Cellular / Worley ────────────────────────────────────────
                float worley = WorleyTileable(u, v, cells, noiseScale);
                // Invert so cell centres are bright, edges dark — coral polyp look
                float cell = 1f - Mathf.Clamp01(worley * 2f);
                // Re-map to keep only the cell-interior bright bands
                cell = Mathf.SmoothStep(0.25f, 0.75f, cell);

                // ── FBM noise ────────────────────────────────────────────────
                float fbm = FbmNoise(u * noiseScale, v * noiseScale, octaves: 4);

                // ── Combine ──────────────────────────────────────────────────
                float value = Mathf.Lerp(1f, cell, cellularStrength)
                            * Mathf.Lerp(1f, fbm,  fbmStrength);
                value = Mathf.Clamp01(value);

                // ── Edge darkening (simulates tube curvature / subsurface) ───
                if (darkenEdges)
                {
                    // U maps around the tube circumference (0..1 = full ring)
                    // darken toward u=0 and u=1 edges
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
            // Check wrapped neighbours for tileability
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
        mpb.SetTexture("_MainTex",  noiseTex);
        mpb.SetTexture("_BaseMap",  noiseTex);  // URP
        mr.SetPropertyBlock(mpb);
    }

}
