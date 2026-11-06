using System.Collections.Generic;
using UnityEngine;

// =========================================================================
//  MotifRingGlyphGenerator
//
//  Builds one ring's contour polyline (GlyphPolyline) at a time. Ring ordering/indexing
//  (which BinIndex+Role goes at which radius) is the caller's responsibility — see
//  MotifRingGlyphApplicator.RecordRings.cs's BuildRingKeys and RecordRingSortingOrder.
//
//  Each ring is a closed polyline (first point duplicated at end), rendered by a non-looped
//  LineRenderer (see MotifRingGlyphApplicator.RingBuilder.cs). This is the original, shipped
//  closure method — two later experiments (LineRenderer.loop=true, and leaving the ring one
//  segment short of closing instead of duplicating the point) each attempted to hide or shrink
//  the coincident-seam kink this leaves, and each made the visible result worse, not better, so
//  neither is used. The seam kink itself is real but minor; a ring stack sharing one base angle
//  would still line every ring's kink up into one visible radial line, so RingOrdinalAngleJitter
//  below staggers each ring's seam by the golden angle so they scatter instead of aligning. Note
//  this whole file's Contour output is skipped entirely for ring stacks that never have notes
//  (locked-preview / remaining-progress placeholders — see RecordRings.cs's ApplyLockedPreview
//  and GameplayRings.cs's SetRemainingProgressRings), which don't need a seam-prone LineRenderer
//  at all since the filled annulus mesh alone already reads as a plain circle.
//
//  Notes produce sinusoidal inward dips at their angular step position.
//  Dip depth = tugDepthFraction × baseRadius (always visible for every note).
//  CommitTime01 modulates dip angular width: fast-collected = narrower, slow = wider.
// =========================================================================
public static class MotifRingGlyphGenerator
{
    // Irrational angle step (~137.5°) — successive multiples never realign to the same angle
    // (mod 2π) regardless of how many rings are stacked, unlike a rational/fixed step which
    // would eventually repeat and re-align a subset of rings.
    private const float GoldenAngleRad = 2.399963f;

    /// <summary>
    /// Generate a single contour ring polyline at an explicit world radius.
    /// Used when the ring radius is determined externally (e.g. the outer edge of a
    /// filled annulus so the contour sits at the rim of the filled disc).
    /// </summary>
    /// <param name="ringOrdinal">This ring's position within its own stack (0 = innermost) —
    /// see MotifRingGlyphApplicator.RingEntry.RingOrdinal. Staggers this ring's seam angle away
    /// from its neighbors'; unrelated to binIndex, which can be shared or meaningless (-1) across
    /// an entire stack (locked-preview/remaining-progress placeholder rings).</param>
    public static GlyphPolyline GenerateSingleRingAtRadius(
        MusicalRole role, int binIndex, Color color,
        List<MotifSnapshot.NoteEntry> notes, int totalSteps,
        float radius, RingGlyphConfig cfg, int ringOrdinal = 0)
    {
        if (cfg == null) return null;
        int effectiveBinSize = Mathf.Max(1, totalSteps);
        var pts = BuildRingPoints(notes ?? new List<MotifSnapshot.NoteEntry>(), radius, effectiveBinSize, cfg, ringOrdinal);
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

    // Per-ring seam offset — see the class-level remarks on GoldenAngleRad above. Public so
    // callers computing a ring's note/travel-dot/tether/firework angle outside this class
    // (MotifRingGlyphApplicator.RecordRings.cs, .GameplayRings.cs, RingPreviewPlayer.cs) can
    // apply the exact same per-ring offset their contour points were built with.
    public static float RingOrdinalAngleJitter(int ringOrdinal) => ringOrdinal * GoldenAngleRad;

    // ─────────────────────────────────────────────────────────────────────────
    //  Ring point generation
    // ─────────────────────────────────────────────────────────────────────────

    private static List<Vector2> BuildRingPoints(
        List<MotifSnapshot.NoteEntry> notes,
        float baseRadius,
        int binSize,
        RingGlyphConfig cfg,
        int ringOrdinal)
    {
        int N = Mathf.Max(4, cfg.segments);
        var pts = new List<Vector2>(N + 1);
        float ringAngleOffset = cfg.ringStartAngleOffset + RingOrdinalAngleJitter(ringOrdinal);

        for (int i = 0; i <= N; i++) // +1: duplicate first point to close ring
        {
            float angle = i / (float)N * Mathf.PI * 2f + ringAngleOffset;
            float maxBell = 0f;

            foreach (var note in notes)
            {
                int localStep = note.Step % binSize;
                float noteAngle = localStep / (float)binSize * Mathf.PI * 2f + ringAngleOffset;

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
