using System.Collections.Generic;
using UnityEngine;

// ── Ring construction & mesh generation ─────────────────────────────────
public partial class MotifRingGlyphApplicator
{
    private RingEntry BuildRingEntry(
        string goName, float innerR, float outerR, int segs,
        Color color, MusicalRole role, int binIndex,
        List<MotifSnapshot.NoteEntry> notes, int totalSteps,
        float? fillAlphaOverride = null, float? contourAlphaOverride = null,
        int? fillSortingOrderOverride = null, int? contourSortingOrderOverride = null,
        float? contourWidthOverride = null, int ringOrdinal = 0, bool skipContour = false)
    {
        var root = new GameObject(goName);
        root.transform.SetParent(transform, worldPositionStays: false);

        // ── Fill ─────────────────────────────────────────────────────────────
        var fillGo = new GameObject("Fill");
        fillGo.transform.SetParent(root.transform, worldPositionStays: false);

        var mesh     = BuildAnnulusMesh(innerR, outerR, segs);
        var fullTris = mesh.triangles;          // save before clearing
        mesh.SetTriangles(System.Array.Empty<int>(), 0);

        var mf        = fillGo.AddComponent<MeshFilter>();
        mf.sharedMesh = mesh;

        var mr               = fillGo.AddComponent<MeshRenderer>();
        mr.sharedMaterial    = config.ringMeshMaterial;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows    = false;
        // Above the backdrop discs (see CreateBackgroundDisc) so the record reads as a solid
        // black backing with the ring lines on top of it, not a see-through wireframe with the
        // background (e.g. CosmicDust) showing through. Callers building a stacked record (older
        // bins innermost) pass a higher override here for older rings so their deep note dips —
        // which visually intrude outward past their own band — still read as on top.
        mr.sortingOrder      = fillSortingOrderOverride ?? FillSortingOrder;

        SetFillColor(mr, new Color(color.r, color.g, color.b, fillAlphaOverride ?? config.ringAlpha), ScratchMpb);

        // Placeholder rings (locked preview / remaining progress) never draw a contour, so skip
        // generating its ~257 points and the LineRenderer.
        if (skipContour)
        {
            return new RingEntry
            {
                Root          = root,
                Fill          = mr,
                BaseColor     = color,
                FullTris      = fullTris,
                ContourPoints = new List<Vector2>(),
                BinIndex      = binIndex,
                Role          = role,
                OuterRadius   = outerR,
                RingOrdinal   = ringOrdinal,
                FillMesh      = mesh,
            };
        }

        // ── Contour ───────────────────────────────────────────────────────────
        var contourGo = new GameObject("Contour");
        contourGo.transform.SetParent(root.transform, worldPositionStays: false);

        var poly = MotifRingGlyphGenerator.GenerateSingleRingAtRadius(
            role, binIndex, color, notes, totalSteps, outerR, config, ringOrdinal);
        var pts = poly?.Points ?? new List<Vector2>();

        var lr = contourGo.AddComponent<LineRenderer>();
        if (lineMaterial != null) lr.sharedMaterial = lineMaterial;
        lr.useWorldSpace     = false;
        lr.loop              = false;
        // RingUnlit's edge softness reads V across the strip's width, so the UV layout must stay Stretch/View.
        lr.textureMode       = LineTextureMode.Stretch;
        lr.alignment         = LineAlignment.View;
        lr.widthMultiplier   = contourWidthOverride ?? config.lineWidth;
        // Rounds joints along the polyline (softens the sharp inward "V" at each note's tug-dip),
        // same technique as PhaseStarDustAffect.CreateTentacleLine. numCapVertices is deliberately
        // left at 0: the contour's point list already closes itself (BuildRingPoints duplicates
        // the first point at the end), so with loop=false those two coincident "end" points would
        // each draw a rounded cap, stacking into a stray dot at the seam. See
        // MotifRingGlyphGenerator.cs's class-level remarks for why this (imperfect but least-bad)
        // closure is used instead of LineRenderer.loop=true or a deliberate 1-segment gap — both
        // were tried and both looked worse. RingOrdinal/RingOrdinalAngleJitter below is what keeps
        // this seam's minor kink from lining up across a whole ring stack into something visible.
        lr.numCornerVertices = 4;
        var contourColor     = new Color(color.r, color.g, color.b, contourAlphaOverride ?? config.contourAlpha);
        lr.startColor        = contourColor;
        lr.endColor          = contourColor;
        lr.positionCount     = 0;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows    = false;
        // Topmost — the visible "groove" lines sit above the Fill and the backdrop discs.
        lr.sortingOrder      = contourSortingOrderOverride ?? ContourSortingOrder;

        return new RingEntry
        {
            Root          = root,
            Fill          = mr,
            Contour       = lr,
            BaseColor     = color,
            FullTris      = fullTris,
            ContourPoints = pts,
            BinIndex      = binIndex,
            Role          = role,
            OuterRadius   = outerR,
            RingOrdinal   = ringOrdinal,
            FillMesh      = mesh,
        };
    }

    // ── Background discs ────────────────────────────────────────────────────
    // Two solid backdrop discs behind the whole ring stack: inner sized to placed
    // (completed) rings, outer sized to the entire record (placed + remaining).
    //
    // Sorting order for the whole ring-glyph stack, back-to-front. All of these render in the
    // same ("Default") sorting layer as everything else in the play scene, including the
    // CosmicDust maze — the discs must sit above dust's sortingOrder (0) or the record reads as
    // a see-through wireframe with dust showing through it instead of a solid backing; Fill and
    // Contour then sit above the discs so the ring lines stay visible on top of that backing.
    //
    // FillSortingOrder/ContourSortingOrder are also the base pair for a *stacked* multi-ring
    // record (see AnimateApply/ApplyVinyl in RecordRings.cs): each older ring gets a
    // fill/contour pair 2 higher than the next-newer ring, so an older (innermost) ring's note
    // dips — which visually intrude outward past its own band — still render on top of a newer
    // (outer) ring rather than being covered by it. TravelDotSortingOrder is set far above any
    // realistic stack depth so travel dots stay visible over every ring regardless of ring count.
    private const int FullBgDiscSortingOrder   = 1;
    private const int PlacedBgDiscSortingOrder = 2;
    private const int FillSortingOrder         = 3;
    private const int ContourSortingOrder      = 4;
    private const int TravelDotSortingOrder    = 10000;

    private void EnsureBackgroundDiscs()
    {
        if (_fullBgDisc == null)   _fullBgDisc   = CreateBackgroundDisc("FullRecordBackdrop", FullBgDiscSortingOrder);
        if (_placedBgDisc == null) _placedBgDisc = CreateBackgroundDisc("PlacedRecordBackdrop", PlacedBgDiscSortingOrder);
        _placedBgMpb ??= new MaterialPropertyBlock();
        _fullBgMpb   ??= new MaterialPropertyBlock();
    }

    private MeshRenderer CreateBackgroundDisc(string goName, int sortingOrder)
    {
        var go = new GameObject(goName);
        go.transform.SetParent(transform, worldPositionStays: false);

        var mf = go.AddComponent<MeshFilter>();
        mf.sharedMesh = new Mesh();

        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial    = config.ringMeshMaterial;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows    = false;
        mr.sortingOrder      = sortingOrder;
        return mr;
    }

    private void UpdateBackgroundDiscRadii(int placedCount, int totalCount)
    {
        if (config == null) return;
        EnsureBackgroundDiscs();
        int segs = Mathf.Max(16, config.segments);

        float placedR = placedCount > 0 ? RingInnerRadius(placedCount - 1) + config.ringThickness : 0f;
        ReplaceDiscMesh(_placedBgDisc, BuildDiscMesh(placedR, segs));
        SetFillColor(_placedBgDisc,
            new Color(config.backgroundDiscColor.r, config.backgroundDiscColor.g, config.backgroundDiscColor.b, config.placedBackgroundAlpha),
            _placedBgMpb);

        float fullR = totalCount > 0 ? RingInnerRadius(totalCount - 1) + config.ringThickness : 0f;
        ReplaceDiscMesh(_fullBgDisc, BuildDiscMesh(fullR, segs));
        SetFillColor(_fullBgDisc,
            new Color(config.backgroundDiscColor.r, config.backgroundDiscColor.g, config.backgroundDiscColor.b, config.fullBackgroundAlpha),
            _fullBgMpb);
    }

    // Swaps in a freshly built disc mesh, destroying the one it replaces.
    private static void ReplaceDiscMesh(MeshRenderer disc, Mesh mesh)
    {
        var mf  = disc.GetComponent<MeshFilter>();
        var old = mf.sharedMesh;
        mf.sharedMesh = mesh;
        if (old != null) Destroy(old);
    }

    private static Mesh BuildDiscMesh(float radius, int segments)
    {
        int n     = Mathf.Max(3, segments);
        var verts = new Vector3[n + 1];
        var uvs   = new Vector2[n + 1];
        verts[0]  = Vector3.zero;
        uvs[0]    = new Vector2(0.5f, 0.5f);

        for (int i = 0; i < n; i++)
        {
            float angle = i / (float)n * Mathf.PI * 2f;
            float cos = Mathf.Cos(angle), sin = Mathf.Sin(angle);
            verts[i + 1] = new Vector3(cos * radius, sin * radius, 0f);
            uvs[i + 1]   = new Vector2(cos * 0.5f + 0.5f, sin * 0.5f + 0.5f);
        }

        var tris = new int[n * 3];
        for (int i = 0; i < n; i++)
        {
            int next = (i + 1) % n;
            int t    = i * 3;
            tris[t] = 0; tris[t + 1] = i + 1; tris[t + 2] = next + 1;
        }

        var mesh = new Mesh();
        mesh.vertices  = verts;
        mesh.uv        = uvs;
        mesh.triangles = tris;
        mesh.RecalculateBounds();
        return mesh;
    }

    // Shared scratch block — SetPropertyBlock copies its contents, so one instance serves every
    // caller that sets a color and immediately applies it (single-threaded, never held across a yield).
    // Created lazily: a static field initializer runs in the type's cctor, which Unity can trigger
    // from a MonoBehaviour constructor, where creating native objects (MaterialPropertyBlock) throws.
    private static MaterialPropertyBlock _scratchMpb;
    private static MaterialPropertyBlock ScratchMpb => _scratchMpb ??= new MaterialPropertyBlock();

    private static void SetFillColor(MeshRenderer mr, Color c, MaterialPropertyBlock mpb)
    {
        mpb.SetColor(BasePropId,  c);
        mpb.SetColor(ColorPropId, c);
        mr.SetPropertyBlock(mpb);
    }

    private static Mesh BuildAnnulusMesh(float innerR, float outerR, int segments)
    {
        int n     = segments;
        var verts = new Vector3[n * 2];
        var uvs   = new Vector2[n * 2];

        for (int i = 0; i < n; i++)
        {
            float angle = i / (float)n * Mathf.PI * 2f;
            float cos = Mathf.Cos(angle), sin = Mathf.Sin(angle);
            verts[i]     = new Vector3(cos * outerR, sin * outerR, 0f);
            verts[n + i] = new Vector3(cos * innerR, sin * innerR, 0f);
            uvs[i]       = new Vector2(i / (float)n, 1f);
            uvs[n + i]   = new Vector2(i / (float)n, 0f);
        }

        var tris = new int[n * 6];
        for (int i = 0; i < n; i++)
        {
            int next = (i + 1) % n;
            int t    = i * 6;
            tris[t]     = i;        tris[t + 1] = next;     tris[t + 2] = n + i;
            tris[t + 3] = next;     tris[t + 4] = n + next; tris[t + 5] = n + i;
        }

        var mesh = new Mesh();
        mesh.vertices  = verts;
        mesh.uv        = uvs;
        mesh.triangles = tris;
        mesh.RecalculateBounds();
        return mesh;
    }
}
