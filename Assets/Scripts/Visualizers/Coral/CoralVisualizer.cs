using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public enum CoralState { Drawing, Rendered }

[System.Serializable]
public class CoralStyleProfile
{
    [Header("Scale")]
    public float coreRadius = 0.22f;

    [Tooltip("Base tentacle length in world units (before density/velocity scaling).")]
    public float baseBranchLength = 2.2f;

    [Tooltip("Extra tentacle length per summed velocity (0..127 velocities).")]
    public float velocityToLength = 0.006f;

    [Tooltip("Radius contribution per note count (thickness grows with density).")]
    public float densityToWidth  = 0.0028f;

    [Header("Connectivity / Spacing")]
    [Tooltip("How far above the core the shared stem rises before tentacles diverge.")]
    public float stemHeight = 0.75f;

    [Tooltip("0..1 portion of tentacle length that stays bundled near the stem before diverging.")]
    [Range(0.05f, 0.75f)]
    public float stemBlendPortion = 0.25f;

    [Tooltip("How far outward tentacles are allowed to drift at full growth.")]
    public float outwardRadius = 0.55f;

    [Tooltip("Extra outward radius based on track index (adds separation but stays cohesive).")]
    public float outwardRadiusJitter = 0.12f;

    [Header("Geometry")]
    [Tooltip("Number of rings along the tube. Higher = smoother but more verts.")]
    public int rings = 22;

    [Tooltip("Radial segments around the tube. 8–14 recommended.")]
    public int radialSegments = 10;

    [Tooltip("Taper exponent. Higher = sharper point.")]
    public float taperPower = 2.4f;

    [Tooltip("Minimum radius at the tip to avoid degeneracy.")]
    public float minTipRadius = 0.004f;

    [Tooltip("Organic bend (world units) applied to centerline.")]
    public float bendStrength = 0.35f;

    [Tooltip("Twist in degrees per unit length to create spiral feel.")]
    public float twistDegPerUnit = 80f;

    [Header("Color")]
    [Tooltip("Hue offset between tentacles (0..1).")]
    public float hueStep = 0.14f;

    [Tooltip("Hue change along the tentacle length (0..1).")]
    public float hueAlongLength = 0.75f;

    [Tooltip("Saturation for rainbow.")]
    [Range(0f, 1f)]
    public float saturation = 0.85f;

    [Tooltip("Value/brightness for rainbow.")]
    [Range(0f, 1f)]
    public float value = 1.0f;

    [Header("Fill/Outline")]
    [Range(0f, 1f)]
    public float fillAlpha = 0.16f;

    [Tooltip("Outline mesh is a slightly inflated copy of the fill mesh.")]
    public float outlineRadiusMul = 1.10f;

    [Header("Drawing Animation")]
    public float growSeconds = 1.35f;
    public AnimationCurve growCurve = AnimationCurve.EaseInOut(0, 0, 1, 1);

    [Header("Fallback")]
    public Gradient fallbackGradient;
}

public class CoralVisualizer : MonoBehaviour
{
    [Header("Style")]
    public CoralStyleProfile style = new CoralStyleProfile();

    [Header("Materials (optional; defaults created if null)")]
    public Material fillMaterial;
    public Material outlineMaterial;

    [Header("Layout")]
    public Vector3 origin = Vector3.zero;

    private Transform _root;

    // Runtime tentacle storage (for grow animation)
    private readonly List<TentacleRuntime> _tentacles = new();

    private class TentacleRuntime
    {
        public Mesh outlineMesh;
        public int[] outlineFullTris;
        public int outlineSegCount;

        public Mesh fillMesh;
        public int[] fillFullTris;
        public int fillSegCount;
    }

    private void Awake()
    {
        _root = new GameObject("CoralRoot").transform;
        _root.SetParent(transform, false);
    }

    private void Clear()
    {
        StopAllCoroutines();
        _tentacles.Clear();

        if (_root == null) return;
        for (int i = _root.childCount - 1; i >= 0; i--)
            Destroy(_root.GetChild(i).gameObject);
    }

    public void RenderPhaseCoral(PhaseSnapshot snapshot, CoralState state)
    {
        Clear();
        if (snapshot == null) return;

        var notes = snapshot.CollectedNotes?.Where(n => n != null).ToList();
        if (notes == null || notes.Count == 0) return;

        // 1) Build core
        BuildCore();

        // 2) Group notes by TrackColor (deterministic ordering by hue)
        var groups = notes
            .GroupBy(n => n.TrackColor)
            .Select(g => new
            {
                Color = g.Key,
                Notes = g.ToList(),
                NoteCount = g.Count(),
                VelocitySum = g.Sum(x => Mathf.Max(0f, x.Velocity))
            })
            .OrderBy(g =>
            {
                Color.RGBToHSV(g.Color, out float h, out _, out _);
                return h;
            })
            .ToList();

        // 3) Build one tentacle per group, all connected to the shared stem
        for (int i = 0; i < groups.Count; i++)
        {
            var g = groups[i];

            float length = style.baseBranchLength
                         + g.VelocitySum * style.velocityToLength;

            float baseRadius = Mathf.Max(0.02f, 0.03f + g.NoteCount * style.densityToWidth);

            // Deterministic direction around the stem based on index
            float angle = (groups.Count <= 1) ? 0f : (i / (float)groups.Count) * Mathf.PI * 2f;
            Vector3 dir = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)).normalized;

            float outward = style.outwardRadius
                          + (Hash01(1000 + i * 31) - 0.5f) * 2f * style.outwardRadiusJitter;

            // Build centerline points (spine)
            Vector3[] spine = BuildConnectedSpine(i, dir, outward, length);

            // Build pair (outline + fill)
            var pair = CreateTentaclePair($"Tentacle_{i}", i);

            var outlineMesh = new Mesh { name = $"TentacleOutline_{i}" };
            var fillMesh    = new Mesh { name = $"TentacleFill_{i}" };

            pair.outlineMF.sharedMesh = outlineMesh;
            pair.fillMF.sharedMesh    = fillMesh;

            // Outline: full alpha; Fill: transparent alpha
            var outlineData = BuildTaperedTube(
                outlineMesh,
                spine,
                baseRadius * style.outlineRadiusMul,
                seed: 200 + i * 97,
                vertexAlpha: 1f,
                hueOffset01: i * style.hueStep
            );

            var fillData = BuildTaperedTube(
                fillMesh,
                spine,
                baseRadius,
                seed: 200 + i * 97 + 1,
                vertexAlpha: style.fillAlpha,
                hueOffset01: i * style.hueStep
            );

            _tentacles.Add(new TentacleRuntime
            {
                outlineMesh = outlineMesh,
                outlineFullTris = outlineData.fullTriangles,
                outlineSegCount = outlineData.segmentCount,
                fillMesh = fillMesh,
                fillFullTris = fillData.fullTriangles,
                fillSegCount = fillData.segmentCount
            });

            if (state == CoralState.Drawing)
            {
                outlineMesh.SetTriangles(System.Array.Empty<int>(), 0, true);
                fillMesh.SetTriangles(System.Array.Empty<int>(), 0, true);
            }
        }

        if (state == CoralState.Drawing)
            StartCoroutine(AnimateGrowth());
        else
            ForceFullTriangles();
    }

    // ----------------------------
    // Core + Stem Connectivity
    // ----------------------------

    private void BuildCore()
    {
        var core = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        core.name = "MotifCore";
        core.transform.SetParent(_root, false);
        core.transform.localPosition = origin;
        core.transform.localRotation = Quaternion.identity;
        core.transform.localScale = Vector3.one * Mathf.Max(0.06f, style.coreRadius);

        var col = core.GetComponent<Collider>();
        if (col) Destroy(col);

        var mr = core.GetComponent<MeshRenderer>();
        mr.sharedMaterial = CreateDefaultVertexColorMaterial();

        // Core is a soft white to avoid muddying the palette
        if (mr.sharedMaterial.HasProperty("_Color"))
            mr.sharedMaterial.SetColor("_Color", new Color(1f, 1f, 1f, 0.85f));
    }

    private Vector3[] BuildConnectedSpine(int index, Vector3 dir, float outwardRadius, float length)
    {
        int rings = Mathf.Max(6, style.rings);
        var spine = new Vector3[rings];

        Vector3 stemBase = origin;
        Vector3 stemTop  = origin + Vector3.up * style.stemHeight;

        float stemPortion = Mathf.Clamp01(style.stemBlendPortion);

        // Deterministic “curl” axis
        Vector3 side = Vector3.Cross(Vector3.up, dir);
        if (side.sqrMagnitude < 1e-6f) side = Vector3.right;
        side.Normalize();

        for (int r = 0; r < rings; r++)
        {
            float t = (rings <= 1) ? 0f : r / (float)(rings - 1);

            // How far along length we are (0..1)
            float along = t;

            // Stem phase: keep bundled near the stem early, then diverge smoothly
            float stemT = Mathf.InverseLerp(0f, stemPortion, along);
            stemT = Mathf.Clamp01(stemT);
            stemT = SmoothStep01(stemT);

            // Position along the stem (bundled)
            float stemAlong = (stemPortion <= 0.0001f) ? 1f : Mathf.Clamp01(along / stemPortion);
            Vector3 bundled = Vector3.Lerp(stemBase, stemTop, stemAlong);

            // Diverged target (organic curl + outward)
            float y = style.stemHeight + along * length;

            // Spiral twist around Y as it grows
            float twistDeg = style.twistDegPerUnit * along * length;
            Quaternion twist = Quaternion.AngleAxis(twistDeg, Vector3.up);
            Vector3 outwardDir = (twist * dir).normalized;

            // Gentle bend and lateral curl
            float bend = style.bendStrength * (along * along);
            float curl = (Hash01(500 + index * 71) - 0.5f) * 0.25f;

            Vector3 diverged =
                origin
                + Vector3.up * y
                + outwardDir * (outwardRadius * along)
                + side * (curl * along)
                + outwardDir * bend;

            // Final: blend from bundled stem to diverged form
            spine[r] = Vector3.Lerp(bundled, diverged, stemT);
        }

        return spine;
    }

    // ----------------------------
    // Mesh generation: tapered tube with rainbow vertex colors
    // ----------------------------

    private (int[] fullTriangles, int segmentCount) BuildTaperedTube(
        Mesh mesh,
        Vector3[] spine,
        float baseRadius,
        int seed,
        float vertexAlpha,
        float hueOffset01)
    {
        if (mesh == null || spine == null || spine.Length < 2)
        {
            if (mesh != null) mesh.Clear();
            return (System.Array.Empty<int>(), 0);
        }

        int rings = spine.Length;
        int segCount = rings - 1;
        int sides = Mathf.Max(3, style.radialSegments);

        int vCount = rings * sides;
        int idxCount = segCount * sides * 6;

        var verts = new Vector3[vCount];
        var norms = new Vector3[vCount];
        var uvs   = new Vector2[vCount];
        var cols  = new Color[vCount];
        var tris  = new int[idxCount];

        Vector3 prevNormal = Vector3.up;
        Vector3 prevTangent = (spine[1] - spine[0]).normalized;
        if (prevTangent.sqrMagnitude < 1e-6f) prevTangent = Vector3.up;

        if (Mathf.Abs(Vector3.Dot(prevTangent, prevNormal)) > 0.95f)
            prevNormal = Vector3.right;

        for (int r = 0; r < rings; r++)
        {
            Vector3 p = spine[r];

            Vector3 tangent =
                (r == rings - 1) ? (spine[r] - spine[r - 1]) : (spine[r + 1] - spine[r]);

            if (tangent.sqrMagnitude < 1e-6f) tangent = prevTangent;
            tangent.Normalize();

            Vector3 normal = prevNormal - Vector3.Dot(prevNormal, tangent) * tangent;
            if (normal.sqrMagnitude < 1e-6f)
            {
                normal = Vector3.Cross(tangent, Vector3.right);
                if (normal.sqrMagnitude < 1e-6f) normal = Vector3.Cross(tangent, Vector3.up);
            }
            normal.Normalize();

            Vector3 binormal = Vector3.Cross(tangent, normal).normalized;

            prevNormal = normal;
            prevTangent = tangent;

            float t = (rings <= 1) ? 0f : r / (float)(rings - 1);

            // Taper to point
            float taper = Mathf.Pow(1f - t, Mathf.Max(0.1f, style.taperPower));
            float pulse = 1f + (Hash01(seed * 131 + r * 17) - 0.5f) * 0.06f;
            float radius = Mathf.Max(style.minTipRadius, baseRadius * taper * pulse);

            // Rainbow gradient (vertex colors)
            float hue = Mathf.Repeat(hueOffset01 + t * style.hueAlongLength, 1f);
            Color ringColor = Color.HSVToRGB(hue, style.saturation, style.value);
            ringColor.a = vertexAlpha;

            int baseIndex = r * sides;
            for (int s = 0; s < sides; s++)
            {
                float ang = (s / (float)sides) * Mathf.PI * 2f;
                Vector3 dir = Mathf.Cos(ang) * normal + Mathf.Sin(ang) * binormal;

                verts[baseIndex + s] = p + dir * radius;
                norms[baseIndex + s] = dir;
                uvs[baseIndex + s]   = new Vector2(s / (float)sides, t);
                cols[baseIndex + s]  = ringColor;
            }
        }

        int ti = 0;
        for (int r = 0; r < segCount; r++)
        {
            int r0 = r * sides;
            int r1 = (r + 1) * sides;

            for (int s = 0; s < sides; s++)
            {
                int s0 = s;
                int s1 = (s + 1) % sides;

                int v00 = r0 + s0;
                int v01 = r0 + s1;
                int v10 = r1 + s0;
                int v11 = r1 + s1;

                tris[ti++] = v00; tris[ti++] = v10; tris[ti++] = v01;
                tris[ti++] = v01; tris[ti++] = v10; tris[ti++] = v11;
            }
        }

        mesh.Clear();
        mesh.vertices = verts;
        mesh.normals  = norms;
        mesh.uv       = uvs;
        mesh.colors   = cols;
        mesh.triangles = tris;
        mesh.RecalculateBounds();

        return (tris, segCount);
    }

    // ----------------------------
    // Rendering objects + materials
    // ----------------------------

    private (MeshFilter outlineMF, MeshFilter fillMF) CreateTentaclePair(string name, int index)
    {
        var rootGO = new GameObject(name);
        rootGO.transform.SetParent(_root, false);
        rootGO.transform.localPosition = Vector3.zero;
        rootGO.transform.localRotation = Quaternion.identity;
        rootGO.transform.localScale = Vector3.one;

        // Outline
        var outlineGO = new GameObject("Outline");
        outlineGO.transform.SetParent(rootGO.transform, false);
        var outlineMF = outlineGO.AddComponent<MeshFilter>();
        var outlineMR = outlineGO.AddComponent<MeshRenderer>();

        Material oMat = outlineMaterial != null ? new Material(outlineMaterial) : CreateDefaultVertexColorMaterial();
        CoerceToVertexColorUnlit(oMat, transparent: false);
        outlineMR.sharedMaterial = oMat;

        // Fill
        var fillGO = new GameObject("Fill");
        fillGO.transform.SetParent(rootGO.transform, false);
        var fillMF = fillGO.AddComponent<MeshFilter>();
        var fillMR = fillGO.AddComponent<MeshRenderer>();

        Material fMat = fillMaterial != null ? new Material(fillMaterial) : CreateDefaultVertexColorMaterial();
        CoerceToVertexColorUnlit(fMat, transparent: true);
        fillMR.sharedMaterial = fMat;
        fillMR.sharedMaterial = fMat;
        return (outlineMF, fillMF);
    }
    private void CoerceToVertexColorUnlit(Material mat, bool transparent)
    {
        if (mat == null) return;

        // Prefer a shader that respects vertex colors and is unlit.
        Shader sh =
            Shader.Find("Particles/Standard Unlit") ??
            Shader.Find("Sprites/Default") ??
            Shader.Find("Unlit/Texture") ??
            Shader.Find("Unlit/Color");

        if (sh != null && mat.shader != sh)
            mat.shader = sh;

        // Ensure material tint is neutral so vertex colors drive hue.
        if (mat.HasProperty("_Color"))
            mat.SetColor("_Color", Color.white);

        // If the previous shader was Standard (or similar), ensure emission isn't washing things out.
        if (mat.HasProperty("_EmissionColor"))
        {
            mat.SetColor("_EmissionColor", Color.black);
            mat.DisableKeyword("_EMISSION");
        }

        // Render queue: keep outline behind fill.
        mat.renderQueue = transparent ? 3100 : 2000;
    }

    private void ForceEmissionIfPresent(Material mat)
    {
        if (mat == null) return;

        // Standard uses _EmissionColor and _EMISSION keyword.
        if (mat.HasProperty("_EmissionColor"))
        {
            mat.EnableKeyword("_EMISSION");
            // White emission so your vertex colors / albedo remain visible even in darkness.
            mat.SetColor("_EmissionColor", Color.white);
        }
    }

    private Material CreateDefaultVertexColorMaterial()
    {
        // In Built-in, Sprites/Default respects vertex colors and is effectively unlit.
        Shader sh = Shader.Find("Sprites/Default");
        if (sh == null) sh = Shader.Find("Unlit/Texture");
        if (sh == null) sh = Shader.Find("Unlit/Color");

        var mat = new Material(sh);
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", Color.white);
        return mat;
    }

    // ----------------------------
    // Grow animation
    // ----------------------------

    private IEnumerator AnimateGrowth()
    {
        float dur = Mathf.Max(0.01f, style.growSeconds);
        float t = 0f;

        while (t < dur)
        {
            t += Time.deltaTime;
            float u = Mathf.Clamp01(t / dur);
            float eased = style.growCurve != null ? style.growCurve.Evaluate(u) : u;

            for (int i = 0; i < _tentacles.Count; i++)
            {
                var te = _tentacles[i];
                RevealMeshSegments(te.outlineMesh, te.outlineFullTris, te.outlineSegCount, eased);
                RevealMeshSegments(te.fillMesh, te.fillFullTris, te.fillSegCount, eased);
            }

            yield return null;
        }

        ForceFullTriangles();
    }

    private void ForceFullTriangles()
    {
        for (int i = 0; i < _tentacles.Count; i++)
        {
            var te = _tentacles[i];
            if (te.outlineMesh != null && te.outlineFullTris != null)
                te.outlineMesh.SetTriangles(te.outlineFullTris, 0, true);
            if (te.fillMesh != null && te.fillFullTris != null)
                te.fillMesh.SetTriangles(te.fillFullTris, 0, true);
        }
    }

    private void RevealMeshSegments(Mesh mesh, int[] fullTriangles, int segCount, float eased01)
    {
        if (mesh == null || fullTriangles == null || fullTriangles.Length == 0 || segCount <= 0)
            return;

        int sides = Mathf.Max(3, style.radialSegments);
        int indicesPerSeg = sides * 6;

        int segVisible = Mathf.Clamp(Mathf.RoundToInt(eased01 * segCount), 0, segCount);
        int idxCount = segVisible * indicesPerSeg;

        if (idxCount <= 0)
        {
            mesh.SetTriangles(System.Array.Empty<int>(), 0, true);
            return;
        }

        idxCount = Mathf.Min(idxCount, fullTriangles.Length);
        var tris = new int[idxCount];
        System.Array.Copy(fullTriangles, tris, idxCount);
        mesh.SetTriangles(tris, 0, true);
    }

    // ----------------------------
    // Utility
    // ----------------------------

    private static float SmoothStep01(float x)
    {
        x = Mathf.Clamp01(x);
        return x * x * (3f - 2f * x);
    }

    private float Hash01(int x)
    {
        unchecked
        {
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
        }
        uint ux = (uint)x;
        return (ux % 100000u) / 100000f;
    }
}
