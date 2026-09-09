using System.Collections.Generic;
using UnityEngine;

// =========================================================================
//  MotifRingGlyphGenerator
//
//  Builds one ring's contour polyline (GlyphPolyline) at a time. Ring ordering/indexing
//  (which BinIndex+Role goes at which radius) is the caller's responsibility — see
//  MotifRingGlyphApplicator.RecordRings.cs's BuildRingKeys and RecordRingSortingOrder.
//
//  Each ring is a closed polyline (first point duplicated at end).
//  Notes produce sinusoidal inward dips at their angular step position.
//  Dip depth = tugDepthFraction × baseRadius (always visible for every note).
//  CommitTime01 modulates dip angular width: fast-collected = narrower, slow = wider.
// =========================================================================
public static class MotifRingGlyphGenerator
{
    /// <summary>
    /// Generate a single contour ring polyline at an explicit world radius.
    /// Used when the ring radius is determined externally (e.g. the outer edge of a
    /// filled annulus so the contour sits at the rim of the filled disc).
    /// </summary>
    public static GlyphPolyline GenerateSingleRingAtRadius(
        MusicalRole role, int binIndex, Color color,
        List<MotifSnapshot.NoteEntry> notes, int totalSteps,
        float radius, RingGlyphConfig cfg)
    {
        if (cfg == null) return null;
        int effectiveBinSize = Mathf.Max(1, totalSteps);
        var pts = BuildRingPoints(notes ?? new List<MotifSnapshot.NoteEntry>(), radius, effectiveBinSize, cfg);
        return new GlyphPolyline
        {
            LayerName = $"ContourRing_Bin{binIndex}_{role}",
            Role      = role,
            Points    = pts,
            LineWidth = cfg.lineWidth,
            LineColor = color,
            SortOrder = 0,
            BinIndex  = binIndex,
        };
    }

    /// <summary>
    /// Generate a single contour ring polyline using the ring-index radius formula.
    /// </summary>
    public static GlyphPolyline GenerateSingleRing(
        MusicalRole role, int binIndex, Color color,
        List<MotifSnapshot.NoteEntry> notes, int totalSteps,
        int ringIndex, RingGlyphConfig cfg)
    {
        if (cfg == null) return null;
        float radius = cfg.innerRadius + ringIndex * (cfg.ringThickness + cfg.ringSpacing) + cfg.ringThickness;
        return GenerateSingleRingAtRadius(role, binIndex, color, notes, totalSteps, radius, cfg);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Ring point generation
    // ─────────────────────────────────────────────────────────────────────────

    private static List<Vector2> BuildRingPoints(
        List<MotifSnapshot.NoteEntry> notes,
        float baseRadius,
        int binSize,
        RingGlyphConfig cfg)
    {
        int N = Mathf.Max(4, cfg.segments);
        var pts = new List<Vector2>(N + 1);

        for (int i = 0; i <= N; i++) // +1: duplicate first point to close ring
        {
            float angle = i / (float)N * Mathf.PI * 2f;
            float maxBell = 0f;

            foreach (var note in notes)
            {
                int localStep = note.Step % binSize;
                float noteAngle = localStep / (float)binSize * Mathf.PI * 2f;

                // Shortest angular distance (handles 0/2π wrap)
                float diff = Mathf.Abs(Mathf.DeltaAngle(
                    angle    * Mathf.Rad2Deg,
                    noteAngle * Mathf.Rad2Deg) * Mathf.Deg2Rad);

                // Width varies with CommitTime01: fast-collected = tighter dip, slow = wider.
                float halfWidth = cfg.tugHalfWidthRad * Mathf.Lerp(0.7f, 1.3f, note.CommitTime01);
                if (diff < halfWidth)
                {
                    float t    = diff / halfWidth;                    // 0 at note center, 1 at edge
                    float bell = Mathf.Cos(t * Mathf.PI * 0.5f);    // cosine bell ≈ half-sine arch
                    bell *= bell;                                      // square for tighter wavefront feel
                    if (bell > maxBell) maxBell = bell;
                }
            }

            // Deepest single note wins rather than summing — two overlapping notes (adjacent
            // steps) each keep their own distinct notch instead of pinching into one merged,
            // over-deep trough.
            float r = baseRadius - baseRadius * cfg.tugDepthFraction * maxBell;
            pts.Add(new Vector2(Mathf.Cos(angle) * r, Mathf.Sin(angle) * r));
        }

        return pts;
    }
}
