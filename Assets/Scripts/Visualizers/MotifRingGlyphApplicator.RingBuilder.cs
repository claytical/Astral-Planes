using System.Collections.Generic;
using UnityEngine;

// ── Ring construction & mesh generation ─────────────────────────────────
public partial class MotifRingGlyphApplicator
{
    private RingEntry BuildRingEntry(
        string goName, float innerR, float outerR, int segs,
        Color color, MusicalRole role, int binIndex,
        List<MotifSnapshot.NoteEntry> notes, int totalSteps,
        float? fillAlphaOverride = null, float? contourAlphaOverride = null)
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
        // background (e.g. CosmicDust) showing through.
        mr.sortingOrder      = FillSortingOrder;

        var mpb = new MaterialPropertyBlock();
        SetFillColor(mr, new Color(color.r, color.g, color.b, fillAlphaOverride ?? config.ringAlpha), mpb);

        // ── Contour ───────────────────────────────────────────────────────────
        var contourGo = new GameObject("Contour");
        contourGo.transform.SetParent(root.transform, worldPositionStays: false);

        var poly = MotifRingGlyphGenerator.GenerateSingleRingAtRadius(
            role, binIndex, color, notes, totalSteps, outerR, config);
        var pts = poly?.Points ?? new List<Vector2>();

        var lr = contourGo.AddComponent<LineRenderer>();
        if (lineMaterial != null) lr.material = lineMaterial;
        lr.useWorldSpace     = false;
        lr.loop              = false;
        lr.widthMultiplier   = config.lineWidth;
        var contourColor     = new Color(color.r, color.g, color.b, contourAlphaOverride ?? config.contourAlpha);
        lr.startColor        = contourColor;
        lr.endColor          = contourColor;
        lr.positionCount     = 0;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows    = false;
        // Topmost — the visible "groove" lines sit above the Fill and the backdrop discs.
        lr.sortingOrder      = ContourSortingOrder;

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
    private const int FullBgDiscSortingOrder   = 1;
    private const int PlacedBgDiscSortingOrder = 2;
    private const int FillSortingOrder         = 3;
    private const int ContourSortingOrder      = 4;
    private const int TravelDotSortingOrder    = 5;

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
        _placedBgDisc.GetComponent<MeshFilter>().sharedMesh = BuildDiscMesh(placedR, segs);
        SetFillColor(_placedBgDisc,
            new Color(config.backgroundDiscColor.r, config.backgroundDiscColor.g, config.backgroundDiscColor.b, config.placedBackgroundAlpha),
            _placedBgMpb);

        float fullR = totalCount > 0 ? RingInnerRadius(totalCount - 1) + config.ringThickness : 0f;
        _fullBgDisc.GetComponent<MeshFilter>().sharedMesh = BuildDiscMesh(fullR, segs);
        SetFillColor(_fullBgDisc,
            new Color(config.backgroundDiscColor.r, config.backgroundDiscColor.g, config.backgroundDiscColor.b, config.fullBackgroundAlpha),
            _fullBgMpb);
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

    private static MaterialPropertyBlock[] MakeMpbs(int count)
    {
        var arr = new MaterialPropertyBlock[count];
        for (int i = 0; i < count; i++) arr[i] = new MaterialPropertyBlock();
        return arr;
    }

    private void ApplyAlpha(RingEntry[] entries, float normalizedAlpha,
                            MaterialPropertyBlock[] mpbs)
    {
        for (int i = 0; i < entries.Length; i++)
        {
            Color c = entries[i].BaseColor;
            if (entries[i].Fill != null)
                SetFillColor(entries[i].Fill,
                    new Color(c.r, c.g, c.b, config.ringAlpha * normalizedAlpha), mpbs[i]);

            if (entries[i].Contour != null)
            {
                var lc = new Color(c.r, c.g, c.b, config.contourAlpha * normalizedAlpha);
                entries[i].Contour.startColor = lc;
                entries[i].Contour.endColor   = lc;
            }
        }
    }

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
