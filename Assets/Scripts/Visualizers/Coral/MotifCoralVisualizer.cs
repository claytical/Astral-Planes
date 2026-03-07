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
    [Tooltip("Unlit vertex-colour material for branch tubes.")]
    public Material branchMaterial;
    [Tooltip("Unlit vertex-colour material for semi-transparent fill tubes.")]
    public Material branchFillMaterial;
    [Tooltip("Unlit material for bud spheres (matched notes).")]
    public Material budMaterial;
    [Tooltip("Unlit material for barren bud rings (unmatched notes).")]
    public Material barrenBudMaterial;

    [Header("Trunk")]
    [Tooltip("World-space position of the trunk anchor.")]
    public Vector3 origin = Vector3.zero;
    [Tooltip("Radius of the trunk anchor sphere.")]
    [Min(0.01f)] public float trunkRadius = 0.18f;

    [Header("Branch Layout")]
    [Tooltip("Base angle (degrees) between matched and unmatched fork branches.")]
    [Range(5f, 60f)] public float defaultForkAngleDeg = 22f;
    [Tooltip("Maximum extra fork angle the player stick can add (degrees).")]
    [Range(0f, 40f)] public float maxSteerForkExtra = 18f;
    [Tooltip("Base outward spread of the four primary branches around the trunk (degrees offset from vertical).")]
    [Range(0f, 45f)] public float primaryBranchLean = 18f;
    [Tooltip("Twist increment per segment (degrees around Y), giving the upward spiral feel.")]
    [Range(0f, 90f)] public float segmentTwistDeg = 20f;

    [Header("Segment Height")]
    [Tooltip("Minimum segment height in world units (even an instant fill gets this).")]
    [Min(0.05f)] public float segHeightMin = 0.25f;
    [Tooltip("Maximum segment height in world units (slowest fill maps here).")]
    [Min(0.1f)]  public float segHeightMax = 2.2f;
    [Tooltip("Expected worst-case bin fill time in seconds (maps to segHeightMax).")]
    [Min(1f)]    public float maxExpectedFillSeconds = 60f;

    [Header("Segment Width / Spiral")]
    [Tooltip("Base tube radius for a branch with 1 note.")]
    [Min(0.005f)] public float tubeRadiusBase = 0.04f;
    [Tooltip("Extra tube radius per note in the bin.")]
    [Min(0f)]     public float tubeRadiusPerNote = 0.008f;
    [Tooltip("Minimum spiral coil radius (step-spread = 0).")]
    [Min(0f)]     public float coilRadiusMin = 0.08f;
    [Tooltip("Maximum spiral coil radius (step-spread = full bin).")]
    [Min(0f)]     public float coilRadiusMax = 0.32f;
    [Tooltip("Number of full turns the branch makes per unit of height.")]
    [Min(0f)]     public float coilTurnsPerUnit = 0.9f;

    [Header("Tube Geometry")]
    [Tooltip("Rings along each tube segment. Higher = smoother bend.")]
    [Range(4, 32)] public int tubeRings = 14;
    [Tooltip("Radial sides per tube ring.")]
    [Range(4, 16)] public int tubeSides = 8;
    [Tooltip("Taper power (1 = linear, >1 = sharper tip).")]
    [Range(0.5f, 4f)] public float tapePower = 1.8f;
    [Tooltip("Minimum tube radius at tip.")]
    [Min(0.001f)] public float minTipRadius = 0.003f;

    [Header("Buds")]
    [Tooltip("Base radius for a matched bud sphere.")]
    [Min(0.01f)] public float budRadiusBase = 0.055f;
    [Tooltip("Scale multiplier for root-note buds.")]
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

    private readonly List<TubeSegmentRuntime> _segments = new();
    private readonly List<GameObject>          _buds     = new();

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
        // We need a root transform parented here for clean hierarchy
        var rootGO = new GameObject("MotifCoralRoot");
        rootGO.transform.SetParent(transform, false);
        rootGO.transform.localPosition = origin;
        _root = rootGO.transform;

        _forkAngleDeg = defaultForkAngleDeg;

        // The [RequireComponent] MeshRenderer on this GO is unused visually;
        // disable it so it doesn't render a blank mesh.
        var mr = GetComponent<MeshRenderer>();
        if (mr) mr.enabled = false;
    }

    private void OnDestroy() => ClearAll();

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
        SpawnTrunkAnchor();

        if (snapshot.TrackBins == null || snapshot.TrackBins.Count == 0) return;

        // Group bins by role; sort bins within each group by BinIndex
        var byRole = snapshot.TrackBins
            .GroupBy(b => b.Role)
            .ToDictionary(g => g.Key, g => g.OrderBy(b => b.BinIndex).ToList());

        // Count total segments across all tracks for grow-time distribution
        int totalSegs = 0;
        foreach (var pair in byRole)
            foreach (var bin in pair.Value)
                totalSegs += (bin.UnmatchedNoteCount > 0) ? 2 : 1; // fork = 2 segments

        float segNormWidth = totalSegs > 0 ? 1f / totalSegs : 1f;
        int   segIndex     = 0;

        foreach (var pair in byRole)
        {
            MusicalRole role = pair.Key;
            var bins = pair.Value;

            float azimuthDeg = RoleAzimuth.TryGetValue(role, out float az) ? az : 0f;

            // Starting direction for this track's primary branch
            // Lean outward from vertical by primaryBranchLean, around the trunk
            Vector3 primaryDir = AzimuthLeanDir(azimuthDeg, primaryBranchLean);

            // Traverse bins, keeping track of where the matched branch tip is
            Vector3 tipPos        = _root.position + origin; // world space start at trunk top
            Vector3 currentDir    = primaryDir;
            float   cumulativeTwist = 0f;

            for (int bi = 0; bi < bins.Count; bi++)
            {
                var bin = bins[bi];

                // Segment height from fill time
                float fillDuration = (bin.CompletionTime > 0f)
                    ? Mathf.Max(0f, bin.CompletionTime - bin.MotifStartTime)
                    : 0f;
                float segHeight = Mathf.Lerp(segHeightMin, segHeightMax,
                    Mathf.Clamp01(fillDuration / maxExpectedFillSeconds));

                // Tube radius from note density
                int totalNotes = bin.MatchedNoteCount + bin.UnmatchedNoteCount;
                float tubeRadius = tubeRadiusBase + tubeNoteRadius(totalNotes);

                // Coil radius from step spread
                float stepSpread = StepSpread(bin.CollectedSteps, snapshot.TotalSteps > 0 ? snapshot.TotalSteps : 16);
                float coilRadius = Mathf.Lerp(coilRadiusMin, coilRadiusMax, stepSpread);

                // Twist accumulates per segment for the upward spiral feel
                cumulativeTwist += segmentTwistDeg;

                // Rotate the branch direction by cumulativeTwist around Y
                Vector3 segDir = RotateAroundY(currentDir, cumulativeTwist);

                // Segment endpoints
                Vector3 segStart = tipPos;
                Vector3 segEnd   = segStart + segDir * segHeight;

                bool hasFork = bin.UnmatchedNoteCount > 0;

                // ── Matched segment (always present) ──────────────────────────
                {
                    float t0 = segIndex * segNormWidth;
                    float t1 = t0 + segNormWidth;

                    Vector3[] spine = BuildSpiralSpine(segStart, segDir, segHeight, coilRadius, bin.CollectedSteps, snapshot.TotalSteps);
                    Color     col   = bin.TrackColor;

                    SpawnTubeSegment($"Branch_R{role}_B{bi}_Match", spine, tubeRadius, col,
                                     vertexAlpha: 1f, t0, t1);

                    // Matched buds along this segment
                    var matchedNotes = snapshot.CollectedNotes?
                        .Where(n => n.TrackColor == bin.TrackColor && n.BinIndex == bin.BinIndex && n.IsMatched)
                        .ToList();
                    SpawnBuds(matchedNotes, spine, bin.BinIndex, snapshot, isMatched: true);

                    segIndex++;
                }

                // ── Unmatched fork segment (only if imperfect bin) ────────────
                if (hasFork)
                {
                    // Fork direction: rotate away from matched direction by forkAngleDeg
                    Vector3 forkDir = RotateAroundY(segDir, _forkAngleDeg);
                    Vector3 forkEnd = segStart + forkDir * segHeight;

                    float t0 = segIndex * segNormWidth;
                    float t1 = t0 + segNormWidth;

                    Vector3[] forkSpine = BuildSpiralSpine(segStart, forkDir, segHeight, coilRadius * 0.7f,
                                                            bin.CollectedSteps, snapshot.TotalSteps);
                    Color barrenCol = Desaturate(bin.TrackColor, 0.35f);

                    SpawnTubeSegment($"Branch_R{role}_B{bi}_Unmatch", forkSpine, tubeRadius * 0.7f, barrenCol,
                                     vertexAlpha: barrenBudAlpha, t0, t1);

                    // Unmatched buds
                    var unmatchedNotes = snapshot.CollectedNotes?
                        .Where(n => n.TrackColor == bin.TrackColor && n.BinIndex == bin.BinIndex && !n.IsMatched)
                        .ToList();
                    SpawnBuds(unmatchedNotes, forkSpine, bin.BinIndex, snapshot, isMatched: false);

                    segIndex++;
                }

                // Next bin starts at the tip of the matched segment
                tipPos     = segEnd;
                currentDir = segDir;
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Spine construction
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Builds the spine points for one branch segment.
    /// The branch follows a gentle helical coil around the main direction,
    /// with coil radius derived from note step-spread.
    /// </summary>
    private Vector3[] BuildSpiralSpine(Vector3 start, Vector3 dir, float height,
                                        float coilRadius, List<int> steps, int totalSteps)
    {
        int rings = Mathf.Max(4, tubeRings);
        var spine = new Vector3[rings];

        // Build a local frame: dir is "up axis", perpendicular for coil orbit
        Vector3 up   = dir.normalized;
        Vector3 side = Vector3.Cross(up, Vector3.forward);
        if (side.sqrMagnitude < 1e-5f) side = Vector3.Cross(up, Vector3.right);
        side.Normalize();
        Vector3 fwd = Vector3.Cross(side, up).normalized;

        float turns = coilTurnsPerUnit * height;

        for (int r = 0; r < rings; r++)
        {
            float t   = (rings <= 1) ? 0f : r / (float)(rings - 1);
            float ang = t * turns * Mathf.PI * 2f;

            // Coil tapers to zero at tip (the branch point is crisp)
            float coilT = Mathf.Sin(t * Mathf.PI); // 0 at base, peaks midway, 0 at tip
            float cr    = coilRadius * coilT;

            Vector3 coilOffset = (Mathf.Cos(ang) * side + Mathf.Sin(ang) * fwd) * cr;

            spine[r] = start + up * (t * height) + coilOffset;
        }

        return spine;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Tube mesh
    // ═══════════════════════════════════════════════════════════════════════

    private void SpawnTubeSegment(string segName, Vector3[] spine, float baseRadius,
                                   Color col, float vertexAlpha,
                                   float growStart01, float growEnd01)
    {
        if (spine == null || spine.Length < 2) return;

        var go = new GameObject(segName);
        go.transform.SetParent(_root, false);

        // ── Outline tube ─────────────────────────────────────────────────
        var outlineGO = new GameObject("Outline");
        outlineGO.transform.SetParent(go.transform, false);
        var outlineMF = outlineGO.AddComponent<MeshFilter>();
        var outlineMR = outlineGO.AddComponent<MeshRenderer>();
        outlineMR.sharedMaterial = GetOrCreateMaterial(branchMaterial, transparent: false);

        var outlineMesh = new Mesh { name = segName + "_Outline" };
        outlineMF.sharedMesh = outlineMesh;
        var outlineData = BuildTube(outlineMesh, spine, baseRadius * outlineRadiusMul, col, vertexAlpha: 1f);

        // ── Fill tube ─────────────────────────────────────────────────────
        var fillGO = new GameObject("Fill");
        fillGO.transform.SetParent(go.transform, false);
        var fillMF = fillGO.AddComponent<MeshFilter>();
        var fillMR = fillGO.AddComponent<MeshRenderer>();
        fillMR.sharedMaterial = GetOrCreateMaterial(branchFillMaterial, transparent: true);

        var fillMesh = new Mesh { name = segName + "_Fill" };
        fillMF.sharedMesh = fillMesh;
        var fillData = BuildTube(fillMesh, spine, baseRadius, col, vertexAlpha: fillAlpha * vertexAlpha);

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
            float taper  = Mathf.Max(minTipRadius / baseRadius, Mathf.Pow(1f - t, tapePower));
            float radius = baseRadius * taper;

            // Colour: darken slightly toward tip, keep track hue
            Color ringCol = Color.Lerp(col, col * 0.6f, t);
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
                            int binIndex, PhaseSnapshot snapshot, bool isMatched)
    {
        if (notes == null || notes.Count == 0 || spine == null || spine.Length < 2) return;

        int binSize   = Mathf.Max(1, snapshot.TotalSteps);
        int rootMidi  = snapshot.MotifKeyRootMidi;

        foreach (var note in notes)
        {
            // Position the bud along the spine based on bin-local step
            int   localStep = note.Step % binSize;
            float t01       = binSize <= 1 ? 0.5f : (float)localStep / (binSize - 1);
            t01 = Mathf.Clamp01(t01);

            // Interpolate position along spine
            float   spineT    = t01 * (spine.Length - 1);
            int     spineIdx  = Mathf.Min((int)spineT, spine.Length - 2);
            float   spineBlend = spineT - spineIdx;
            Vector3 budPos    = Vector3.Lerp(spine[spineIdx], spine[spineIdx + 1], spineBlend);

            bool isRoot = (note.Note == rootMidi);
            float radius = budRadiusBase * (isRoot ? rootNoteScaleBoost : 1f);
            if (!isMatched) radius *= 0.75f;

            Color budCol = isMatched ? note.TrackColor : Desaturate(note.TrackColor, 0.2f);
            if (!isMatched) budCol.a = barrenBudAlpha;

            GameObject bud = isMatched
                ? SpawnBudSphere(budPos, radius, budCol, isRoot)
                : SpawnBudRing(budPos, radius, budCol);

            _buds.Add(bud);
        }
    }

    private GameObject SpawnBudSphere(Vector3 pos, float radius, Color col, bool isRoot)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = isRoot ? "Bud_Root" : "Bud_Match";
        go.transform.SetParent(_root, false);
        go.transform.position   = pos;
        go.transform.localScale = Vector3.one * radius * 2f;

        // Remove collider
        var coll = go.GetComponent<Collider>();
        if (coll) Destroy(coll);

        var mr  = go.GetComponent<MeshRenderer>();
        var mat = GetOrCreateMaterial(budMaterial, transparent: !Mathf.Approximately(col.a, 1f));
        mr.sharedMaterial = mat;
        var mpb = new MaterialPropertyBlock();
        if (mat.HasProperty("_Color")) mpb.SetColor("_Color", col);
        mr.SetPropertyBlock(mpb);

        // Root note: add a small halo ring for emphasis
        if (isRoot)
        {
            var halo = SpawnBudRing(pos, radius * 1.6f, col * 1.3f);
            halo.transform.SetParent(go.transform, true);
        }

        return go;
    }

    private GameObject SpawnBudRing(Vector3 pos, float radius, Color col)
    {
        // Build a thin torus-like ring from a tube whose spine is a circle
        var go = new GameObject("Bud_Ring");
        go.transform.SetParent(_root, false);
        go.transform.position = pos;

        var mf = go.AddComponent<MeshFilter>();
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = GetOrCreateMaterial(barrenBudMaterial, transparent: true);

        int   ringSegs = 16;
        var   spine    = new Vector3[ringSegs + 1];
        float ringR    = radius;
        for (int i = 0; i <= ringSegs; i++)
        {
            float a = (i / (float)ringSegs) * Mathf.PI * 2f;
            spine[i] = pos + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * ringR;
        }

        var mesh = new Mesh { name = "BudRing" };
        mf.sharedMesh = mesh;
        BuildTube(mesh, spine, radius * 0.12f, col, barrenBudAlpha);

        return go;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Trunk anchor
    // ═══════════════════════════════════════════════════════════════════════

    private void SpawnTrunkAnchor()
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = "TrunkAnchor";
        go.transform.SetParent(_root, false);
        go.transform.localPosition = origin;
        go.transform.localScale    = Vector3.one * Mathf.Max(0.02f, trunkRadius) * 2f;

        var coll = go.GetComponent<Collider>();
        if (coll) Destroy(coll);

        var mr  = go.GetComponent<MeshRenderer>();
        mr.sharedMaterial = GetOrCreateMaterial(null, transparent: false);
        var mpb = new MaterialPropertyBlock();
        if (mr.sharedMaterial.HasProperty("_Color"))
            mpb.SetColor("_Color", new Color(1f, 1f, 1f, 1f));
        mr.SetPropertyBlock(mpb);
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

            // Reveal each segment proportionally within its grow window
            foreach (var seg in _segments)
            {
                // How far through THIS segment's window are we?
                float segU = Mathf.InverseLerp(seg.growStartNorm, seg.growEndNorm, eased);
                RevealSegment(seg, segU);
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
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Cleanup
    // ═══════════════════════════════════════════════════════════════════════

    private void ClearAll()
    {
        StopAllCoroutines();
        _segments.Clear();

        foreach (var b in _buds) if (b) Destroy(b);
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

    private float tubeNoteRadius(int noteCount)
        => noteCount * tubeRadiusPerNote;

    private static Color Desaturate(Color c, float satMul)
    {
        Color.RGBToHSV(c, out float h, out float s, out float v);
        return Color.HSVToRGB(h, s * satMul, v);
    }

    private Material GetOrCreateMaterial(Material source, bool transparent)
    {
        if (source != null) return source;

        Shader sh = Shader.Find("Sprites/Default")
                 ?? Shader.Find("Unlit/Texture")
                 ?? Shader.Find("Unlit/Color");

        var mat = new Material(sh);
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", Color.white);
        mat.renderQueue = transparent ? 3100 : 2000;
        return mat;
    }
}
