using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
//  MotifGlyphGenerator
//  Produces one integrated LineRenderer-compatible glyph from a MotifSnapshot.
//
//  Pipeline:
//    MotifSnapshot → DerivedMetrics → RoleContributions → GlyphPasses → Polylines
//
//  Entry point: GenerateGlyph(MotifSnapshot snapshot)
//  Returns:     GlyphOutput (list of named polylines + render hints)
// ─────────────────────────────────────────────────────────────────────────────

// =========================================================================
//  Output types
// =========================================================================

public class GlyphPolyline
{
    public string        LayerName;    // e.g. "Bass_Body", "Harmony_Branch_0"
    public MusicalRole?  Role;
    public List<Vector2> Points;
    public float         LineWidth;
    public Color         LineColor;
    public int           SortOrder;   // lower = drawn first (behind)
    public int           BinIndex = -1; // ring bin index; -1 for non-ring glyphs
}

public class GlyphOutput
{
    public List<GlyphPolyline> Polylines = new();
    public Color               AtmosphereColor;
    public MusicalRole         DominantRole;
    public int                 ActiveRoleCount;
}

// =========================================================================
//  Derived metrics — computed once, shared across all passes
// =========================================================================

internal class DerivedMetrics
{
    // Motif-level
    public float MotifDensity;          // notes / max(1, TotalSteps)
    public float MotifAccuracy;         // matched / total notes
    public float MotifBinFillRate;      // filled bins / total bins
    public int   ActiveRoleCount;
    public MusicalRole DominantRole;
    public List<MusicalRole> ActiveRoles = new();

    // Per-role (indexed by MusicalRole)
    public Dictionary<MusicalRole, RoleMetrics> Role = new();
}

internal class RoleMetrics
{
    public float RoleScore;
    public int   NoteCount;
    public float VelocityMean;
    public float VelocityMax;
    public float MatchedRatio;
    public int   FilledBinCount;
    public int   BinCount;
    public float AverageFillDuration;
    public float BinIndexSpread;        // max - min BinIndex
    public float StepSpreadNormalized;  // spread of note steps across [0,1]
    public Color TrackColor;
    public List<MotifSnapshot.TrackBinData> Bins  = new();
    public List<MotifSnapshot.NoteEntry>    Notes = new();
}

// =========================================================================
//  Species / Subspecies parameter schema
//  Expose these as presets; override per-motif via data-derived tuning.
// =========================================================================

[Serializable]
public class GlyphSpeciesParams
{
    // Bass species
    public int   BassMinLoops       = 1;
    public int   BassMaxLoops       = 4;
    public float BassBaseRadius     = 0.35f;
    public float BassWidthBase      = 0.025f;

    // Harmony species
    public int   HarmonyMinBranches = 1;
    public int   HarmonyMaxBranches = 6;
    public float HarmonyBranchLen   = 0.25f;
    public float HarmonyWidthBase   = 0.018f;

    // Lead species
    public int   LeadControlPoints  = 12;
    public float LeadAmplitude      = 0.30f;
    public float LeadWidthBase      = 0.015f;

    // Groove species
    public int   GrooveMinTicks     = 3;
    public int   GrooveMaxTicks     = 16;
    public float GrooveTickLen      = 0.08f;
    public float GrooveWidthBase    = 0.010f;

    // Global scale for glyph inside a unit square
    public float GlyphRadius        = 0.45f;  // half-extent in normalized space
}

// =========================================================================
//  Main generator
// =========================================================================

public static class MotifGlyphGenerator
{
    // Stateless — call from any context.
    public static GlyphOutput GenerateGlyph(
        MotifSnapshot snapshot,
        GlyphSpeciesParams species = null)
    {
        species ??= new GlyphSpeciesParams();

        var metrics = ComputeDerivedMetrics(snapshot);
        var output  = new GlyphOutput
        {
            AtmosphereColor = snapshot.Color,
            DominantRole    = metrics.DominantRole,
            ActiveRoleCount = metrics.ActiveRoleCount,
        };

        // Build passes in canonical order: Groove → Bass → Harmony → Lead
        // Each pass appends polylines to output.
        if (metrics.ActiveRoles.Contains(MusicalRole.Groove))
            BuildGroovePass(metrics, species, snapshot, output);

        if (metrics.ActiveRoles.Contains(MusicalRole.Bass))
            BuildBassPass(metrics, species, snapshot, output);

        if (metrics.ActiveRoles.Contains(MusicalRole.Harmony))
            BuildHarmonyPass(metrics, species, snapshot, output);

        if (metrics.ActiveRoles.Contains(MusicalRole.Lead))
            BuildLeadPass(metrics, species, snapshot, output);

        return output;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  STEP 1 — Derived metrics
    // ─────────────────────────────────────────────────────────────────────

    private static DerivedMetrics ComputeDerivedMetrics(MotifSnapshot s)
    {
        var m = new DerivedMetrics();

        // Group bins and notes per role
        foreach (MusicalRole role in Enum.GetValues(typeof(MusicalRole)))
        {
            var bins  = s.TrackBins?.Where(b => b.Role == role).ToList()
                        ?? new List<MotifSnapshot.TrackBinData>();
            float score = (s.TrackScores != null && s.TrackScores.TryGetValue(role, out var sc))
                          ? sc : 0f;

            // Resolve notes for this role by matching NoteEntry.BinIndex against bins for this role.
            // (CollectedSteps stores local steps, not global, so cannot be used directly with n.Step.)
            var binIndices = bins.Select(b => b.BinIndex).ToHashSet();
            var notes = s.CollectedNotes?
                .Where(n => binIndices.Contains(n.BinIndex))
                .ToList() ?? new List<MotifSnapshot.NoteEntry>();

            // Only register role if meaningful evidence exists
            bool isActive = score > 0.01f || bins.Count > 0;
            if (!isActive) continue;

            var rm = new RoleMetrics
            {
                RoleScore   = score,
                Bins        = bins,
                Notes       = notes,
                NoteCount   = notes.Count,
                BinCount    = bins.Count,
                FilledBinCount = bins.Count(b => b.IsFilled),
                MatchedRatio   = notes.Count > 0
                                 ? notes.Count(n => n.IsMatched) / (float)notes.Count
                                 : bins.Count > 0 ? bins.Average(b => b.MatchRatio) : 0f,
                VelocityMean = notes.Count > 0 ? notes.Average(n => n.Velocity) : 0.5f,
                VelocityMax  = notes.Count > 0 ? notes.Max(n => n.Velocity)     : 0.5f,
                AverageFillDuration = bins.Count > 0 ? bins.Average(b => b.FillDurationSeconds) : 0f,
                BinIndexSpread = bins.Count > 1
                                 ? bins.Max(b => b.BinIndex) - bins.Min(b => b.BinIndex)
                                 : 0,
                TrackColor = bins.Count > 0 ? bins[0].TrackColor
                             : notes.Count > 0 ? notes[0].TrackColor
                             : Color.white,
            };

            if (notes.Count > 0 && s.TotalSteps > 1)
            {
                float minStep = notes.Min(n => n.Step) / (float)(s.TotalSteps - 1);
                float maxStep = notes.Max(n => n.Step) / (float)(s.TotalSteps - 1);
                rm.StepSpreadNormalized = maxStep - minStep;
            }

            m.Role[role] = rm;
            m.ActiveRoles.Add(role);
        }

        m.ActiveRoleCount = m.ActiveRoles.Count;

        int totalNotes   = s.CollectedNotes?.Count ?? 0;
        int matchedNotes = s.CollectedNotes?.Count(n => n.IsMatched) ?? 0;
        int totalBins    = s.TrackBins?.Count ?? 0;
        int filledBins   = s.TrackBins?.Count(b => b.IsFilled) ?? 0;

        m.MotifDensity     = totalNotes / (float)Math.Max(1, s.TotalSteps);
        m.MotifAccuracy    = totalNotes > 0 ? matchedNotes / (float)totalNotes : 0f;
        m.MotifBinFillRate = totalBins  > 0 ? filledBins   / (float)totalBins  : 0f;

        // Dominant role by ownership priority: Bass > Harmony > Lead > Groove
        MusicalRole[] priority = { MusicalRole.Bass, MusicalRole.Harmony,
                                   MusicalRole.Lead, MusicalRole.Groove };
        foreach (var role in priority)
            if (m.ActiveRoles.Contains(role)) { m.DominantRole = role; break; }

        return m;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  STEP 2 — Groove pass  (cadence scaffold, anchor ticks)
    // ─────────────────────────────────────────────────────────────────────

    private static void BuildGroovePass(
        DerivedMetrics m, GlyphSpeciesParams sp, MotifSnapshot s, GlyphOutput out_)
    {
        var rm   = m.Role[MusicalRole.Groove];
        bool dominant = m.DominantRole == MusicalRole.Groove;

        // Derive tick count from note periodicity + binIndexSpread
        int tickCount = Mathf.Clamp(
            dominant ? rm.NoteCount : Mathf.RoundToInt(rm.NoteCount * 0.6f),
            sp.GrooveMinTicks, sp.GrooveMaxTicks);

        // When Groove is dominant, arrange ticks in a grid-like scaffold
        // When secondary, distribute ticks along a circle as subtle anchors
        float R = sp.GlyphRadius;
        float width = sp.GrooveWidthBase
                    * Mathf.Lerp(0.7f, 1.4f, rm.MatchedRatio)
                    * (dominant ? 1.5f : 0.8f);

        // Rhythm regularity: matched → even spacing; unmatched → jitter
        float jitter = (1f - rm.MatchedRatio) * 0.06f;
        var rng = new System.Random(SeedFromPattern(s.Pattern) ^ 0xABCD);

        if (dominant)
        {
            // Dominant Groove: visible orthogonal cadence scaffold
            // N horizontal tick-lines evenly spaced in [-R, R] square
            float yStep = (2f * R) / (tickCount + 1);
            for (int i = 0; i < tickCount; i++)
            {
                float y    = -R + yStep * (i + 1) + (float)(rng.NextDouble() - 0.5) * jitter;
                float xLen = R * 0.5f * Mathf.Lerp(0.4f, 1.0f, rm.FilledBinCount / (float)Math.Max(1, rm.BinCount));
                float dlen = rm.AverageFillDuration > 0
                             ? Mathf.Clamp(sp.GrooveTickLen / rm.AverageFillDuration, 0.03f, sp.GrooveTickLen)
                             : sp.GrooveTickLen;

                bool filled = i < rm.FilledBinCount;
                var pts = new List<Vector2>
                {
                    new(-xLen, y),
                    new( xLen, y),
                };
                out_.Polylines.Add(MakeLine("Groove_Tick_" + i, MusicalRole.Groove,
                    pts, width * (filled ? 1f : 0.5f), rm.TrackColor, sortOrder: 0));
            }
        }
        else
        {
            // Secondary Groove: radial anchor ticks on the body perimeter
            for (int i = 0; i < tickCount; i++)
            {
                float t     = i / (float)tickCount;
                float angle = t * Mathf.PI * 2f + (float)(rng.NextDouble() - 0.5) * jitter;
                float r0    = R * 0.85f;
                float r1    = r0 + sp.GrooveTickLen * (rm.FilledBinCount > i ? 1f : 0.4f);

                var pts = new List<Vector2>
                {
                    new(Mathf.Cos(angle) * r0, Mathf.Sin(angle) * r0),
                    new(Mathf.Cos(angle) * r1, Mathf.Sin(angle) * r1),
                };
                out_.Polylines.Add(MakeLine("Groove_Tick_" + i, MusicalRole.Groove,
                    pts, width, rm.TrackColor, sortOrder: 0));
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    //  STEP 3 — Bass pass  (main body: loop / spiral / orbit)
    // ─────────────────────────────────────────────────────────────────────

    private static void BuildBassPass(
        DerivedMetrics m, GlyphSpeciesParams sp, MotifSnapshot s, GlyphOutput out_)
    {
        var rm = m.Role[MusicalRole.Bass];

        // Body prominence from roleScore; fall back to bin fill rate when score is unavailable.
        float prominence = rm.RoleScore > 0.01f
            ? Mathf.Clamp01(rm.RoleScore)
            : (rm.BinCount > 0 ? (float)rm.FilledBinCount / rm.BinCount : 0.75f);
        float R  = sp.GlyphRadius * Mathf.Lerp(0.55f, 1.0f, prominence);

        // Loop count from note count (drives complexity)
        int loops = Mathf.Clamp(
            Mathf.RoundToInt(Mathf.Lerp(sp.BassMinLoops, sp.BassMaxLoops,
                rm.NoteCount / (float)Math.Max(1, 20))),
            sp.BassMinLoops, sp.BassMaxLoops);

        // Spiral tightness vs openness from step spread
        float tightness = 1f - Mathf.Clamp01(rm.StepSpreadNormalized);

        // Smoothness from match ratio (high = clean circle/ellipse, low = wobbly)
        float wobble = (1f - rm.MatchedRatio) * 0.12f;

        // Line width from average velocity
        float width = sp.BassWidthBase * Mathf.Lerp(0.7f, 2.0f, rm.VelocityMean);

        // Root-note modifier: note events at root pitch produce a knot / wider node
        int rootPitch = s.MotifKeyRootMidi % 12;
        var rootNotes = rm.Notes.Where(n => n.Note % 12 == rootPitch).ToList();

        int segments = 80;
        var rng = new System.Random(SeedFromPattern(s.Pattern) ^ 0x1234);

        if (loops <= 1)
        {
            // Single orbit: simple closed loop, possibly elliptical
            float aspect = Mathf.Lerp(0.6f, 1.0f, rm.StepSpreadNormalized);
            var pts = BuildOrbit(R, R * aspect, segments, wobble, rootNotes, s.TotalSteps, rng);
            out_.Polylines.Add(MakeLine("Bass_Body", MusicalRole.Bass,
                pts, width, rm.TrackColor, sortOrder: 1));
        }
        else
        {
            // Spiral: winds inward from R to R*tightness over `loops` turns
            float innerR = R * Mathf.Lerp(0.15f, 0.6f, tightness);
            var pts = BuildSpiral(R, innerR, loops, segments * loops, wobble,
                                  rootNotes, s.TotalSteps, rng);
            out_.Polylines.Add(MakeLine("Bass_Body", MusicalRole.Bass,
                pts, width, rm.TrackColor, sortOrder: 1));
        }

        // Bin fill deformation: subtle notch or indent at each unfilled bin position
        foreach (var bin in rm.Bins.Where(b => !b.IsFilled))
        {
            float t     = bin.BinIndex / (float)Math.Max(1, rm.BinCount - 1);
            float angle = t * Mathf.PI * 2f;
            // Small inward poke to mark the unfilled gap
            float poke  = R * 0.06f;
            var notch = new List<Vector2>
            {
                new(Mathf.Cos(angle) * R, Mathf.Sin(angle) * R),
                new(Mathf.Cos(angle) * (R - poke), Mathf.Sin(angle) * (R - poke)),
            };
            out_.Polylines.Add(MakeLine("Bass_Notch_" + bin.BinIndex, MusicalRole.Bass,
                notch, width * 0.4f, rm.TrackColor, sortOrder: 1));
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    //  STEP 4 — Harmony pass  (branches, forks, support arcs)
    // ─────────────────────────────────────────────────────────────────────

    private static void BuildHarmonyPass(
        DerivedMetrics m, GlyphSpeciesParams sp, MotifSnapshot s, GlyphOutput out_)
    {
        var rm = m.Role[MusicalRole.Harmony];

        // Branch count from filled bin count
        int branches = Mathf.Clamp(rm.FilledBinCount, sp.HarmonyMinBranches, sp.HarmonyMaxBranches);
        if (branches == 0) branches = 1;

        // Branch length from fill duration: fast fill → decisive/short, slow → long reaching
        float branchLen = sp.HarmonyBranchLen
            * Mathf.Lerp(1.2f, 0.6f, Mathf.Clamp01(rm.AverageFillDuration / 10f));

        // Symmetry from match ratio: high → evenly spaced, low → angular offset
        float symmetryBias = rm.MatchedRatio;
        float baseAngleOffset = (1f - symmetryBias) * 0.3f;

        float width = sp.HarmonyWidthBase * Mathf.Lerp(0.7f, 1.5f, rm.VelocityMean);
        float R     = sp.GlyphRadius;
        var rng     = new System.Random(SeedFromPattern(s.Pattern) ^ 0x5678);

        for (int i = 0; i < branches; i++)
        {
            // Attach angle: distribute from bin index positions
            float t         = i / (float)branches;
            float baseAngle = t * Mathf.PI * 2f + baseAngleOffset * (float)(rng.NextDouble() - 0.5);
            float attachR   = R * 0.5f;   // attach to body mid-radius

            Vector2 attach = new(Mathf.Cos(baseAngle) * attachR,
                                 Mathf.Sin(baseAngle) * attachR);

            // Paired fork: high accuracy → symmetric pair, low accuracy → single
            bool paired = rm.MatchedRatio > 0.5f;
            float forkAngle = Mathf.PI * 0.18f;

            var bin = i < rm.Bins.Count ? rm.Bins[i] : null;
            float maturity = bin != null ? Mathf.Clamp01(bin.MatchRatio) : 0.5f;
            float len = branchLen * maturity;

            if (paired)
            {
                out_.Polylines.Add(MakeLine("Harmony_Branch_" + i + "_A", MusicalRole.Harmony,
                    BuildBranch(attach, baseAngle + forkAngle, len, rm.MatchedRatio, rng),
                    width, rm.TrackColor, sortOrder: 2));
                out_.Polylines.Add(MakeLine("Harmony_Branch_" + i + "_B", MusicalRole.Harmony,
                    BuildBranch(attach, baseAngle - forkAngle, len, rm.MatchedRatio, rng),
                    width, rm.TrackColor, sortOrder: 2));
            }
            else
            {
                out_.Polylines.Add(MakeLine("Harmony_Branch_" + i, MusicalRole.Harmony,
                    BuildBranch(attach, baseAngle, len, rm.MatchedRatio, rng),
                    width, rm.TrackColor, sortOrder: 2));
            }

            // Unmatched notes add a broken / stub support arc
            if (bin != null && bin.UnmatchedNoteCount > 0)
            {
                float stubAngle = baseAngle + Mathf.PI * 0.5f;
                float stubLen   = len * 0.3f;
                var stub = new List<Vector2>
                {
                    attach,
                    attach + new Vector2(Mathf.Cos(stubAngle), Mathf.Sin(stubAngle)) * stubLen,
                };
                out_.Polylines.Add(MakeLine("Harmony_Stub_" + i, MusicalRole.Harmony,
                    stub, width * 0.35f, rm.TrackColor, sortOrder: 2));
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    //  STEP 5 — Lead pass  (traversing directional phrase path)
    // ─────────────────────────────────────────────────────────────────────

    private static void BuildLeadPass(
        DerivedMetrics m, GlyphSpeciesParams sp, MotifSnapshot s, GlyphOutput out_)
    {
        var rm = m.Role[MusicalRole.Lead];

        int   ctrlCount  = sp.LeadControlPoints;
        float amplitude  = sp.LeadAmplitude * Mathf.Lerp(0.4f, 1.0f, rm.StepSpreadNormalized);
        float width      = sp.LeadWidthBase  * Mathf.Lerp(0.7f, 2.0f, rm.VelocityMax);
        float smoothness = rm.MatchedRatio;   // high → smooth catmull; low → jaggy
        float R          = sp.GlyphRadius;

        // Sort notes by step for phrase progression
        var sortedNotes = rm.Notes.OrderBy(n => n.Step).ToList();
        int  totalSteps = Math.Max(1, s.TotalSteps - 1);
        int  rootPitch  = s.MotifKeyRootMidi % 12;

        // Entry left (-R), exit right (+R), traversing the glyph space
        var controlPts = new List<Vector2>();
        controlPts.Add(new Vector2(-R, 0f)); // entry

        if (sortedNotes.Count > 0)
        {
            // Place control points at note positions
            foreach (var n in sortedNotes)
            {
                float x     = Mathf.Lerp(-R * 0.9f, R * 0.9f, n.Step / (float)totalSteps);
                // Pitch mapped to vertical position: normalize MIDI pitch 0..127 to [-amp, +amp]
                float y     = Mathf.Lerp(-amplitude, amplitude, n.Note / 127f);
                // Root-note bias: push toward center
                if (n.Note % 12 == rootPitch) y *= 0.5f;
                // Velocity → local swell (handled in width pass below)
                controlPts.Add(new Vector2(x, y));
            }
        }
        else
        {
            // No notes: produce a simple arc shaped by matched ratio
            for (int i = 1; i < ctrlCount - 1; i++)
            {
                float t = i / (float)(ctrlCount - 1);
                float x = Mathf.Lerp(-R, R, t);
                float y = Mathf.Sin(t * Mathf.PI) * amplitude * (1f - smoothness * 0.5f);
                controlPts.Add(new Vector2(x, y));
            }
        }
        controlPts.Add(new Vector2(R, 0f)); // exit

        // Low match → add jitter to control points
        if (smoothness < 0.6f)
        {
            var rng  = new System.Random(SeedFromPattern(s.Pattern) ^ 0x9ABC);
            float jitterAmt = (1f - smoothness) * 0.07f;
            for (int i = 1; i < controlPts.Count - 1; i++)
            {
                controlPts[i] += new Vector2(
                    (float)(rng.NextDouble() - 0.5) * jitterAmt,
                    (float)(rng.NextDouble() - 0.5) * jitterAmt);
            }
        }

        // Expand to smooth polyline via Catmull-Rom
        var path = CatmullRomChain(controlPts, segmentsPerSpan: 8);

        // Produce variable-width polyline by splitting at velocity peaks
        // (simple approach: one polyline with base width, add wider stub at velocity peaks)
        out_.Polylines.Add(MakeLine("Lead_Path", MusicalRole.Lead,
            path, width, rm.TrackColor, sortOrder: 3));

        // Root note emphasis: small perpendicular crosshatch at root-note positions
        foreach (var n in sortedNotes.Where(n => n.Note % 12 == rootPitch))
        {
            float x  = Mathf.Lerp(-R * 0.9f, R * 0.9f, n.Step / (float)totalSteps);
            float y  = Mathf.Lerp(-amplitude, amplitude, n.Note / 127f) * 0.5f;
            float hw = width * 2f;
            out_.Polylines.Add(MakeLine("Lead_Root_" + n.Step, MusicalRole.Lead,
                new List<Vector2> { new(x, y - hw), new(x, y + hw) },
                width * 1.5f, rm.TrackColor, sortOrder: 3));
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Geometry helpers
    // ─────────────────────────────────────────────────────────────────────

    private static List<Vector2> BuildOrbit(
        float rx, float ry, int segments, float wobble,
        List<MotifSnapshot.NoteEntry> rootNotes, int totalSteps, System.Random rng)
    {
        var pts = new List<Vector2>(segments + 1);
        int rootPitchLocal = rootNotes.Count > 0
            ? rootNotes[0].Note % 12 : -1;

        for (int i = 0; i <= segments; i++)
        {
            float t     = i / (float)segments;
            float angle = t * Mathf.PI * 2f;
            float wr    = rx + (float)(rng.NextDouble() - 0.5) * wobble;
            float wy    = ry + (float)(rng.NextDouble() - 0.5) * wobble;
            float x     = Mathf.Cos(angle) * wr;
            float y     = Mathf.Sin(angle) * wy;

            // Root note bias: slight outward swell at matching angular position
            foreach (var rn in rootNotes)
            {
                float rt = rn.Step / (float)Math.Max(1, totalSteps - 1);
                if (Mathf.Abs(t - rt) < 0.04f)
                {
                    x *= 1.08f; y *= 1.08f;
                }
            }
            pts.Add(new Vector2(x, y));
        }
        return pts;
    }

    private static List<Vector2> BuildSpiral(
        float outerR, float innerR, int loops, int segments,
        float wobble, List<MotifSnapshot.NoteEntry> rootNotes, int totalSteps,
        System.Random rng)
    {
        var pts = new List<Vector2>(segments + 1);
        for (int i = 0; i <= segments; i++)
        {
            float t     = i / (float)segments;
            float angle = t * loops * Mathf.PI * 2f;
            float r     = Mathf.Lerp(outerR, innerR, t);
            float wr    = r + (float)(rng.NextDouble() - 0.5) * wobble * r;
            float x     = Mathf.Cos(angle) * wr;
            float y     = Mathf.Sin(angle) * wr;

            foreach (var rn in rootNotes)
            {
                float rt = rn.Step / (float)Math.Max(1, totalSteps - 1);
                if (Mathf.Abs(t - rt) < 0.03f) { x *= 1.1f; y *= 1.1f; }
            }
            pts.Add(new Vector2(x, y));
        }
        return pts;
    }

    private static List<Vector2> BuildBranch(
        Vector2 attach, float angle, float length, float smoothness,
        System.Random rng)
    {
        // Simple two-segment branch with optional subtle curve
        int segs = 6;
        var pts  = new List<Vector2>(segs + 1);
        float jitter = (1f - smoothness) * 0.03f;
        for (int i = 0; i <= segs; i++)
        {
            float t  = i / (float)segs;
            float a  = angle + (float)(rng.NextDouble() - 0.5) * jitter * t * Mathf.PI;
            float r  = t * length;
            pts.Add(attach + new Vector2(Mathf.Cos(a) * r, Mathf.Sin(a) * r));
        }
        return pts;
    }

    // Catmull-Rom spline chain through a list of control points
    private static List<Vector2> CatmullRomChain(List<Vector2> ctrl, int segmentsPerSpan)
    {
        if (ctrl.Count < 2) return ctrl;
        var result = new List<Vector2>();
        // Extend endpoints for full chain
        var pts = new List<Vector2> { ctrl[0] };
        pts.AddRange(ctrl);
        pts.Add(ctrl[ctrl.Count - 1]);

        for (int i = 1; i < pts.Count - 2; i++)
        {
            for (int j = 0; j <= segmentsPerSpan; j++)
            {
                float t = j / (float)segmentsPerSpan;
                result.Add(CatmullRom(pts[i - 1], pts[i], pts[i + 1], pts[i + 2], t));
            }
        }
        return result;
    }

    private static Vector2 CatmullRom(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
    {
        float t2 = t * t, t3 = t2 * t;
        return 0.5f * (
            (2f * p1) +
            (-p0 + p2) * t +
            (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
            (-p0 + 3f * p1 - 3f * p2 + p3) * t3
        );
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Utility
    // ─────────────────────────────────────────────────────────────────────

    private static GlyphPolyline MakeLine(
        string name, MusicalRole? role, List<Vector2> pts,
        float width, Color color, int sortOrder)
    {
        return new GlyphPolyline
        {
            LayerName = name,
            Role      = role,
            Points    = pts,
            LineWidth = width,
            LineColor = color,
            SortOrder = sortOrder,
        };
    }

    private static int SeedFromPattern(MazeArchetype pattern) => (int)pattern;
}

