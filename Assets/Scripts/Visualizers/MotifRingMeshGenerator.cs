using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// =========================================================================
//  MotifRingMeshGenerator
//
//  Converts a MotifSnapshot into a list of closed torus Mesh objects, one
//  per (BinIndex, MusicalRole) ring — the 3D analog of MotifRingGlyphGenerator.
//
//  Ring ordering: ascending BinIndex, then ascending MusicalRole enum value
//  (identical to the 2D generator so ring indices stay in sync).
//
//  Each mesh is a closed torus tube in the local XZ plane.  Notes produce
//  inward dips along the major radius using the same cosine-bell formula as
//  BuildRingPoints in the 2D generator.
//
//  Triangle array is ordered by spine segment so the draw-in animation can
//  reveal segments progressively by increasing the SetTriangles count.
// =========================================================================
public static class MotifRingMeshGenerator
{
    // ── Public types ─────────────────────────────────────────────────────────

    public struct RingMeshData
    {
        /// <summary>The built torus Mesh (all vertices set; triangles initially included).</summary>
        public Mesh   Mesh;

        /// <summary>Full triangle array ordered by spine segment for progressive draw-in.
        /// Pass to mesh.SetTriangles(FullTriangles, 0, count, 0) where count grows each frame.</summary>
        public int[]  FullTriangles;

        /// <summary>Number of triangle indices per spine segment (tubeSides * 6).</summary>
        public int    TrisPerSegment;

        public Color  Color;
        public int    BinIndex;
        public string Name;
    }

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Returns how many rings Generate() would produce for a given snapshot,
    /// without actually building the meshes.  Used by GyroscopeOrb to pre-allocate
    /// orientation arrays.
    /// </summary>
    public static int CountRings(MotifSnapshot snap)
    {
        if (snap == null) return 0;
        var seen  = new HashSet<(int, MusicalRole)>();
        int count = 0;
        foreach (var bin in snap.TrackBins
                     .Where(b => b.IsFilled || b.CollectedSteps.Count > 0)
                     .OrderBy(b => b.BinIndex).ThenBy(b => (int)b.Role))
        {
            if (seen.Add((bin.BinIndex, bin.Role))) count++;
        }
        return count;
    }

    /// <summary>
    /// Generate closed-torus RingMeshData for every (BinIndex, Role) pair in the snapshot.
    /// </summary>
    public static List<RingMeshData> Generate(MotifSnapshot snap, RopeRingConfig cfg)
    {
        var result = new List<RingMeshData>();
        if (snap == null || cfg == null) return result;

        int   effectiveBinSize = Mathf.Max(1, snap.TotalSteps);
        float tubeRadius       = cfg.innerRadius * cfg.tubeRadiusFraction;

        // ── Build ordered ring keys (mirrors MotifRingGlyphGenerator) ─────────
        var seen     = new HashSet<(int, MusicalRole)>();
        var ringKeys = new List<(int binIndex, MusicalRole role, Color color)>();

        foreach (var bin in snap.TrackBins
                     .Where(b => b.IsFilled || b.CollectedSteps.Count > 0)
                     .OrderBy(b => b.BinIndex).ThenBy(b => (int)b.Role))
        {
            if (seen.Add((bin.BinIndex, bin.Role)))
                ringKeys.Add((bin.BinIndex, bin.Role, bin.TrackColor));
        }

        if (ringKeys.Count == 0) return result;

        // ── Pre-group notes by (BinIndex, TrackColor) — mirrors 2D generator ──
        var notesByBinAndColor = snap.CollectedNotes
            .GroupBy(n => (n.BinIndex,
                           n.SerializedTrackColor.r,
                           n.SerializedTrackColor.g,
                           n.SerializedTrackColor.b))
            .ToDictionary(g => g.Key, g => g.ToList());

        // ── Generate one torus mesh per ring key ──────────────────────────────
        for (int ringIndex = 0; ringIndex < ringKeys.Count; ringIndex++)
        {
            var (binIndex, role, ringColor) = ringKeys[ringIndex];

            // Radius grows outward: tube diameter = tubeRadius * 2 takes space between rings
            float ringRadius = cfg.innerRadius + ringIndex * (cfg.ringSpacing + tubeRadius * 2f);

            List<MotifSnapshot.NoteEntry> ringNotes;
            var lookupKey = (binIndex, ringColor.r, ringColor.g, ringColor.b);
            notesByBinAndColor.TryGetValue(lookupKey, out ringNotes);
            if (ringNotes == null) ringNotes = new List<MotifSnapshot.NoteEntry>();

            var data = BuildTorusMesh(ringNotes, ringRadius, tubeRadius, effectiveBinSize, cfg, ringColor);
            data.Color    = ringColor;
            data.BinIndex = binIndex;
            data.Name     = $"RopeRing_Bin{binIndex}_{role}";

            result.Add(data);
        }

        return result;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Torus mesh generation
    // ─────────────────────────────────────────────────────────────────────────

    private static RingMeshData BuildTorusMesh(
        List<MotifSnapshot.NoteEntry> notes,
        float baseRadius,
        float tubeRadius,
        int   binSize,
        RopeRingConfig cfg,
        Color ringColor)
    {
        int S = Mathf.Max(16, cfg.ringSegments);  // spine segments (major)
        int T = Mathf.Max(4,  cfg.tubeSides);      // tube sides (minor)

        // ── Compute per-spine-point major radii with note dips ────────────────
        // Same cosine-bell formula as BuildRingPoints in MotifRingGlyphGenerator.
        var spineRadii = new float[S];
        for (int s = 0; s < S; s++)
        {
            float theta = s / (float)S * Mathf.PI * 2f;
            float r     = baseRadius;

            foreach (var note in notes)
            {
                int   localStep = note.Step % binSize;
                float noteAngle = localStep / (float)binSize * Mathf.PI * 2f;

                // Shortest angular distance, handles 0/2π wrap
                float diff = Mathf.Abs(Mathf.DeltaAngle(
                    theta     * Mathf.Rad2Deg,
                    noteAngle * Mathf.Rad2Deg) * Mathf.Deg2Rad);

                float halfWidth = cfg.tugHalfWidthRad * Mathf.Lerp(0.7f, 1.3f, note.CommitTime01);
                if (diff < halfWidth)
                {
                    float t    = diff / halfWidth;
                    float bell = Mathf.Cos(t * Mathf.PI * 0.5f);
                    bell *= bell;
                    r -= baseRadius * cfg.tugDepthFraction * bell;
                }
            }

            spineRadii[s] = r;
        }

        // ── Build vertices ─────────────────────────────────────────────────────
        int      vCount = S * T;
        var      verts  = new Vector3[vCount];
        var      norms  = new Vector3[vCount];
        var      uvs    = new Vector2[vCount];
        var      cols   = new Color[vCount];

        for (int s = 0; s < S; s++)
        {
            float theta   = s / (float)S * Mathf.PI * 2f;
            float r       = spineRadii[s];
            var   spinePos = new Vector3(Mathf.Cos(theta) * r, 0f, Mathf.Sin(theta) * r);

            // Fixed frame for a planar ring in XZ:
            //   tangent  = derivative of (cos θ, 0, sin θ), normalised
            //   normal   = Y-up — always perpendicular to the XZ ring plane, no drift
            //   binormal = cross(tangent, normal) → outward radial direction
            var tangent  = new Vector3(-Mathf.Sin(theta), 0f, Mathf.Cos(theta));
            var frameN   = Vector3.up;
            var binormal = Vector3.Cross(tangent, frameN).normalized;

            for (int t = 0; t < T; t++)
            {
                float phi  = t / (float)T * Mathf.PI * 2f;
                Vector3 off = (frameN * Mathf.Cos(phi) + binormal * Mathf.Sin(phi)) * tubeRadius;

                int vi    = s * T + t;
                verts[vi] = spinePos + off;
                norms[vi] = off.normalized;
                uvs[vi]   = new Vector2((float)t / T, (float)s / S);
                cols[vi]  = ringColor;
            }
        }

        // ── Build triangles — closed topology (last ring connects back to ring 0) ─
        int triPerSeg = T * 6;
        int triCount  = S * triPerSeg;
        var tris      = new int[triCount];
        int ti        = 0;

        for (int s = 0; s < S; s++)
        {
            int sNext = (s + 1) % S;   // wraps last spine segment to 0
            for (int t = 0; t < T; t++)
            {
                int tNext = (t + 1) % T;
                int a = s     * T + t;
                int b = s     * T + tNext;
                int c = sNext * T + t;
                int d = sNext * T + tNext;
                tris[ti++] = a; tris[ti++] = c; tris[ti++] = b;
                tris[ti++] = b; tris[ti++] = c; tris[ti++] = d;
            }
        }

        var mesh = new Mesh { name = "RopeRingMesh" };
        mesh.SetVertices(verts);
        mesh.SetNormals(norms);
        mesh.SetUVs(0, uvs);
        mesh.SetColors(cols);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateBounds();

        return new RingMeshData
        {
            Mesh           = mesh,
            FullTriangles  = tris,
            TrisPerSegment = triPerSeg,
        };
    }
}
