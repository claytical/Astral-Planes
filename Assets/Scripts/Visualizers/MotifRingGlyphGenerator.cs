using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// =========================================================================
//  MotifRingGlyphGenerator
//
//  Converts a MotifSnapshot into a list of GlyphPolylines, each representing
//  one (BinIndex, MusicalRole) ring.
//
//  Ring ordering: ascending BinIndex, then ascending MusicalRole enum value.
//  Given bin 0 {Bass, Harmony} and bin 1 {Lead, Harmony}:
//    ring 0 → Bass   (bin 0)  innermost
//    ring 1 → Harmony(bin 0)
//    ring 2 → Lead   (bin 1)
//    ring 3 → Harmony(bin 1)  outermost
//
//  Each ring is a closed polyline (first point duplicated at end).
//  Notes produce sinusoidal inward dips at their angular step position.
//  Dip depth = tugDepthFraction × baseRadius (always visible for every note).
//  CommitTime01 modulates dip angular width: fast-collected = narrower, slow = wider.
// =========================================================================
public static class MotifRingGlyphGenerator
{
    /// <summary>
    /// Generate ring polylines from a completed MotifSnapshot.
    /// </summary>
    public static List<GlyphPolyline> Generate(MotifSnapshot snap, RingGlyphConfig cfg)
    {
        var result = new List<GlyphPolyline>();
        if (snap == null || cfg == null) return result;

        // ── Determine effectiveBinSize ────────────────────────────────────────
        // snap.TotalSteps = drum.totalSteps (single-bin count).
        // Each bin covers that many steps regardless of loopMultiplier.
        // TrackBinData.BinIndex counts from 0, so we use TotalSteps as one bin.
        int effectiveBinSize = Mathf.Max(1, snap.TotalSteps);

        // ── Build ordered ring keys from TrackBins ────────────────────────────
        // De-duplicate by (BinIndex, Role) so a track that re-reports the same bin
        // doesn't generate two rings.
        var seen = new HashSet<(int binIndex, MusicalRole role)>();
        var ringKeys = new List<(int binIndex, MusicalRole role, Color color)>();

        // Debug: log every TrackBin and whether it passes the filter
        foreach (var bin in snap.TrackBins)
            UnityEngine.Debug.Log($"[RingGlyphGen] bin={bin.BinIndex} role={bin.Role} " +
                $"isFilled={bin.IsFilled} collectedSteps={bin.CollectedSteps.Count} " +
                $"→ passes={(bin.IsFilled || bin.CollectedSteps.Count > 0)}");

        foreach (var bin in snap.TrackBins
                     .Where(b => b.IsFilled || b.CollectedSteps.Count > 0)
                     .OrderBy(b => b.BinIndex)
                     .ThenBy(b => (int)b.Role))
        {
            var key = (bin.BinIndex, bin.Role);
            if (seen.Add(key))
                ringKeys.Add((bin.BinIndex, bin.Role, bin.TrackColor));
        }

        UnityEngine.Debug.Log($"[RingGlyphGen] totalBins={snap.TrackBins.Count} ringKeys={ringKeys.Count}");
        if (ringKeys.Count == 0) return result;

        // ── Pre-group notes by (BinIndex, TrackColor) ─────────────────────────
        // NoteEntry doesn't carry Role directly; TrackColor is the next best proxy
        // since each role has a unique color within a motif.
        var notesByBinAndColor = snap.CollectedNotes
            .GroupBy(n => (n.BinIndex, n.SerializedTrackColor.r, n.SerializedTrackColor.g, n.SerializedTrackColor.b))
            .ToDictionary(
                g => g.Key,
                g => g.ToList());

        // ── Generate one ring per key ─────────────────────────────────────────
        for (int ringIndex = 0; ringIndex < ringKeys.Count; ringIndex++)
        {
            var (binIndex, role, ringColor) = ringKeys[ringIndex];

            float radius = cfg.innerRadius + ringIndex * (cfg.ringSpacing + cfg.lineWidth);

            // Collect notes for this ring (match bin + color proxy for role)
            List<MotifSnapshot.NoteEntry> ringNotes;
            var lookupKey = (binIndex, ringColor.r, ringColor.g, ringColor.b);
            notesByBinAndColor.TryGetValue(lookupKey, out ringNotes);
            if (ringNotes == null) ringNotes = new List<MotifSnapshot.NoteEntry>();

            var pts = BuildRingPoints(ringNotes, radius, effectiveBinSize, cfg);

            result.Add(new GlyphPolyline
            {
                LayerName  = $"Ring_Bin{binIndex}_{role}",
                Role       = role,
                Points     = pts,
                LineWidth  = cfg.lineWidth,
                LineColor  = ringColor,
                SortOrder  = ringIndex,
                BinIndex   = binIndex,
            });
        }

        return result;
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
            float r = baseRadius;

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
                    r -= baseRadius * cfg.tugDepthFraction * bell;   // depth scales with ring radius
                }
            }

            pts.Add(new Vector2(Mathf.Cos(angle) * r, Mathf.Sin(angle) * r));
        }

        return pts;
    }
}
