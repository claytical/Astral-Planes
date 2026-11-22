using UnityEngine;

// ── Still render (album art) ─────────────────────────────────────────────
public partial class MotifRingGlyphApplicator
{
    /// <summary>
    /// Builds the record for <paramref name="snapshot"/> as one still frame. Used by the Ring Session
    /// Manager's album-art export. Uses the carousel's fixed-fit layout: the outermost ring's
    /// outer edge always lands at <paramref name="outerRadius"/> whatever the ring count. Ring
    /// thickness stays constant (up to 12 rings), and the gaps between rings and between bins
    /// stretch or shrink to fill. Lines keep config.recordLineWidth in world units, so with a
    /// constant radius every export has the same line weight. Every ring gets the playing-bin
    /// highlight colour (without its scale-up) and every note dip is at full baked depth. Safe in
    /// edit mode: it starts no coroutines and destroys nothing, so it must only be called on a
    /// freshly created glyph.
    /// </summary>
    public void ApplyStill(MotifSnapshot snapshot, float outerRadius)
    {
        if (snapshot == null || config == null) return;
        if (_recordRings.Count > 0)
        {
            Debug.LogWarning("[MotifRingGlyphApplicator] ApplyStill needs a fresh glyph; it already has rings.", this);
            return;
        }

        var ringKeys = BuildRingKeys(snapshot);
        if (ringKeys.Count == 0) return;

        // Unit stack (outer edge of the last ring = 1), scaled into the radii themselves rather than
        // the transform so the contour line width stays in plain world units.
        outerRadius = Mathf.Max(0.0001f, outerRadius);
        _stillOuterRadius = outerRadius;
        var unitRadii = ComputeFixedRadii(ringKeys.Count, ringKeys, config, out float unitThick);
        float ringThick = unitThick * outerRadius;
        int segs = Mathf.Max(16, config.segments);

        for (int i = 0; i < ringKeys.Count; i++)
        {
            var (binIndex, role, color, _, _) = ringKeys[i];
            float innerR = unitRadii[i] * outerRadius;
            float outerR = innerR + ringThick;

            var (fillOrder, contourOrder) = RecordRingSortingOrder(i, ringKeys.Count);
            var entry = BuildRingEntry($"StillRing_Bin{binIndex}_{role}",
                innerR, outerR, segs, color, role, binIndex,
                NotesForRing(snapshot, binIndex, color), snapshot.TotalSteps,
                fillSortingOrderOverride: fillOrder, contourSortingOrderOverride: contourOrder,
                contourWidthOverride: config.recordLineWidth, ringOrdinal: i);
            _recordRings.Add(entry);

            entry.FillMesh.SetTriangles(entry.FullTris, 0);
            entry.FillMesh.RecalculateBounds();
            ApplyContourPoints(entry.Contour, entry.ContourPoints); // baked shape = full dip depth

            if (entry.Fill != null)
                SetFillColor(entry.Fill, BrightenColor(entry.BaseColor, config.ringAlpha), ScratchMpb);
            if (entry.Contour != null)
            {
                var lc = BrightenColor(entry.BaseColor, config.contourAlpha);
                entry.Contour.startColor = lc;
                entry.Contour.endColor   = lc;
            }
        }

        transform.localScale = Vector3.one;
    }

    private float _stillOuterRadius;

    /// <summary>Framing radius for the still: the outermost ring's edge plus half its contour line,
    /// so the outer ring's line just touches the image edge. 0 if nothing was built.</summary>
    public float StillFrameRadius =>
        _recordRings.Count == 0 ? 0f : _stillOuterRadius + (config != null ? config.recordLineWidth * 0.5f : 0f);
}
