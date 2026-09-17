using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// ── Record ring API ──────────────────────────────────────────────────────
public partial class MotifRingGlyphApplicator
{
    /// <summary>
    /// Build filled + contour record rings from the full motif snapshot with
    /// staggered draw-in, note travel dots, and continuous rotation.
    /// </summary>
    public void AnimateApply(MotifSnapshot snapshot)
    {
        StopAllCoroutines();
        _recordFadingOut         = false;
        _gameplayFadingOut       = false;
        _superNodeMode           = false;
        _pendingDeformationCount = 0;
        foreach (Transform child in transform)
            Destroy(child.gameObject);
        _recordRings.Clear();
        _gameplayRings.Clear();
        _remainingRings.Clear();
        ResetHighlightState();

        if (snapshot == null || config == null) return;

        var ringKeys = BuildRingKeys(snapshot);
        if (ringKeys.Count == 0) return;

        var noteViz       = GameFlowManager.Instance?.controller?.noteVisualizer;
        var tracks        = GameFlowManager.Instance?.controller?.tracks;
        var trackByQColor = new Dictionary<Color, InstrumentTrack>();
        if (tracks != null)
            foreach (var tr in tracks)
                if (tr != null) { Color32 q = tr.DisplayColor; trackByQColor[(Color)q] = tr; }

        int segs    = Mathf.Max(16, config.segments);
        int binSize = Mathf.Max(1, snapshot.TotalSteps);
        var bandedRadii = ComputeBandedInnerRadii(ringKeys, config);
        int maxNoteCount = ringKeys.Count > 0 ? ringKeys.Max(k => k.noteCount) : 0;

        for (int i = 0; i < ringKeys.Count; i++)
        {
            var (binIndex, role, color, matchRatio, noteCount) = ringKeys[i];
            float innerR     = bandedRadii[i];
            float outerR     = innerR + config.ringThickness;
            bool  isLastRing = (i == ringKeys.Count - 1);

            // Captured per-iteration (not the loop's `i`) so each ring's rotation-gate closure,
            // read across many later frames, checks its own slot in _recordRings — not whatever
            // `i` happened to be when the loop finished.
            int ringIndex = i;

            var ringNotes = snapshot.CollectedNotes
                .Where(n => n.BinIndex == binIndex
                         && Mathf.Approximately(n.SerializedTrackColor.r, color.r)
                         && Mathf.Approximately(n.SerializedTrackColor.g, color.g)
                         && Mathf.Approximately(n.SerializedTrackColor.b, color.b))
                .ToList();

            // Last ring starts flat — its dips animate in as dots travel.
            // All other rings pre-bake their dips (no travel dot replay).
            var (fillOrder, contourOrder) = RecordRingSortingOrder(i, ringKeys.Count);
            var entry = BuildRingEntry($"RecordRing_Bin{binIndex}_{role}",
                innerR, outerR, segs, color, role, binIndex,
                isLastRing ? new List<MotifSnapshot.NoteEntry>() : ringNotes,
                snapshot.TotalSteps,
                fillSortingOrderOverride: fillOrder, contourSortingOrderOverride: contourOrder,
                contourWidthOverride: config.recordLineWidth, ringOrdinal: i);
            _recordRings.Add(entry);
            ApplyInitialNonHighlightedLook(entry);

            float densityNorm = maxNoteCount > 0 ? (float)noteCount / maxNoteCount : 0f;
            float rotDeg = RecordRingRotationSpeed(matchRatio, densityNorm);
            if (sphericalRotation)
            {
                // Distribute rings at evenly-spaced Y angles so they form tilted orbits.
                entry.Root.transform.localEulerAngles =
                    new Vector3(0f, (float)i / ringKeys.Count * 180f, 0f);
            }
            if (matchRatio < config.directionMatchRatioThreshold) rotDeg = -rotDeg;

            float tugR    = outerR * (1f - config.tugDepthFraction);
            var noteInfos = new List<NoteAnimInfo>();
            foreach (var n in ringNotes)
            {
                Color32 q = n.TrackColor;
                if (!trackByQColor.TryGetValue(q, out var track)) continue;
                int   localStep = n.Step % binSize;
                float angle     = localStep / (float)binSize * Mathf.PI * 2f
                    + config.ringStartAngleOffset + MotifRingGlyphGenerator.RingOrdinalAngleJitter(i);
                noteInfos.Add(new NoteAnimInfo
                {
                    Track        = track,
                    AbsStep      = n.Step,
                    NoteAngle    = angle,
                    RingLocalPos = new Vector3(Mathf.Cos(angle) * outerR, Mathf.Sin(angle) * outerR, 0f),
                    TugLocalPos  = new Vector3(Mathf.Cos(angle) * tugR,   Mathf.Sin(angle) * tugR,   0f),
                    DotColor     = color,
                    SourceNote   = n,
                });
            }
            noteInfos.Sort((a, b) => a.NoteAngle.CompareTo(b.NoteAngle));

            float delay = i * config.ringStaggerDelay;
            StartCoroutine(AnimateMeshFill(
                entry.Fill.GetComponent<MeshFilter>().sharedMesh,
                entry.FullTris, segs, delay, config.ringDrawInDuration));

            if (isLastRing)
            {
                StartCoroutine(AnimateLastRecordRing(
                    entry.Contour, entry.ContourPoints,
                    delay,
                    entry.Root.transform, rotDeg,
                    noteInfos, noteViz,
                    role, binIndex, color, binSize, outerR,
                    shouldStop: () => _recordFadingOut,
                    ringOrdinal: i,
                    spherical: sphericalRotation,
                    isHighlighted: () => _highlightedRingIndices.Contains(ringIndex)));
            }
            else
            {
                // Pre-baked rings draw their dipped shape; no travel dots replayed.
                StartCoroutine(AnimateSingleRing(
                    entry.Contour, entry.ContourPoints,
                    delay, config.ringDrawInDuration,
                    entry.Root.transform, rotDeg,
                    new List<NoteAnimInfo>(),
                    noteViz,
                    shouldStop: () => _recordFadingOut,
                    spherical: sphericalRotation,
                    isHighlighted: () => _highlightedRingIndices.Contains(ringIndex)));
            }
        }

        RefreshPlayAreaFit(_recordRings.Count, bandedRadii[bandedRadii.Length - 1] + config.ringThickness);
    }

    /// <summary>
    /// Render rings from a snapshot instantly with no draw-in animation or rotation.
    /// Used for non-highlighted carousel slots in the PhaseLibrary scene.
    /// Passing null clears the slot. <paramref name="alphaScale"/> dims all layers (0–1).
    /// </summary>
    public void ApplyStatic(MotifSnapshot snapshot, float alphaScale = 1f)
    {
        StopAllCoroutines();
        _recordFadingOut   = false;
        _gameplayFadingOut = false;
        DestroyList(_recordRings);
        ResetHighlightState();

        if (snapshot == null || config == null) return;

        var ringKeys = BuildRingKeys(snapshot);
        if (ringKeys.Count == 0) return;

        int segs = Mathf.Max(16, config.segments);
        var bandedRadii = ComputeBandedInnerRadii(ringKeys, config);

        for (int i = 0; i < ringKeys.Count; i++)
        {
            var (binIndex, role, color, _, _) = ringKeys[i];
            float innerR = bandedRadii[i];
            float outerR = innerR + config.ringThickness;

            var ringNotes = snapshot.CollectedNotes
                .Where(n => n.BinIndex == binIndex
                         && Mathf.Approximately(n.SerializedTrackColor.r, color.r)
                         && Mathf.Approximately(n.SerializedTrackColor.g, color.g)
                         && Mathf.Approximately(n.SerializedTrackColor.b, color.b))
                .ToList();

            var (fillOrder, contourOrder) = RecordRingSortingOrder(i, ringKeys.Count);
            var entry = BuildRingEntry($"StaticRing_Bin{binIndex}_{role}",
                innerR, outerR, segs, color, role, binIndex, ringNotes, snapshot.TotalSteps,
                fillSortingOrderOverride: fillOrder, contourSortingOrderOverride: contourOrder,
                contourWidthOverride: config.recordLineWidth, ringOrdinal: i);
            _recordRings.Add(entry);

            // Render mesh and contour immediately — no coroutines.
            var mesh = entry.Fill.GetComponent<MeshFilter>().sharedMesh;
            mesh.SetTriangles(entry.FullTris, 0);
            mesh.RecalculateBounds();

            entry.Contour.positionCount = entry.ContourPoints.Count;
            for (int j = 0; j < entry.ContourPoints.Count; j++)
                entry.Contour.SetPosition(j, new Vector3(
                    entry.ContourPoints[j].x, entry.ContourPoints[j].y, 0f));
        }

        if (alphaScale < 1f)
        {
            var mpbs = MakeMpbs(_recordRings.Count);
            ApplyAlpha(_recordRings.ToArray(), alphaScale, mpbs);
        }

        RefreshPlayAreaFit(_recordRings.Count, bandedRadii[bandedRadii.Length - 1] + config.ringThickness);
    }

    /// <summary>
    /// Render rings from a snapshot instantly (no draw-in) as a flat spinning vinyl disc.
    /// Used for non-highlighted carousel slots that should look like rotating records rather
    /// than static or spherical displays. Each ring spins on its local Z-axis.
    /// </summary>
    public void ApplyVinyl(MotifSnapshot snapshot, float alphaScale = 1f)
    {
        StopAllCoroutines();
        _recordFadingOut   = false;
        _gameplayFadingOut = false;
        DestroyList(_recordRings);
        ResetHighlightState();

        if (snapshot == null || config == null) return;

        var ringKeys = BuildRingKeys(snapshot);
        if (ringKeys.Count == 0) return;

        int segs = Mathf.Max(16, config.segments);
        var bandedRadii = ComputeBandedInnerRadii(ringKeys, config);
        int maxNoteCount = ringKeys.Count > 0 ? ringKeys.Max(k => k.noteCount) : 0;

        for (int i = 0; i < ringKeys.Count; i++)
        {
            var (binIndex, role, color, matchRatio, noteCount) = ringKeys[i];
            float innerR = bandedRadii[i];
            float outerR = innerR + config.ringThickness;

            var ringNotes = snapshot.CollectedNotes
                .Where(n => n.BinIndex == binIndex
                         && Mathf.Approximately(n.SerializedTrackColor.r, color.r)
                         && Mathf.Approximately(n.SerializedTrackColor.g, color.g)
                         && Mathf.Approximately(n.SerializedTrackColor.b, color.b))
                .ToList();

            var (fillOrder, contourOrder) = RecordRingSortingOrder(i, ringKeys.Count);
            var entry = BuildRingEntry($"VinylRing_Bin{binIndex}_{role}",
                innerR, outerR, segs, color, role, binIndex, ringNotes, snapshot.TotalSteps,
                fillSortingOrderOverride: fillOrder, contourSortingOrderOverride: contourOrder,
                contourWidthOverride: config.recordLineWidth, ringOrdinal: i);
            _recordRings.Add(entry);

            var mesh = entry.Fill.GetComponent<MeshFilter>().sharedMesh;
            mesh.SetTriangles(entry.FullTris, 0);
            mesh.RecalculateBounds();

            entry.Contour.positionCount = entry.ContourPoints.Count;
            for (int j = 0; j < entry.ContourPoints.Count; j++)
                entry.Contour.SetPosition(j, new Vector3(
                    entry.ContourPoints[j].x, entry.ContourPoints[j].y, 0f));

            float densityNorm = maxNoteCount > 0 ? (float)noteCount / maxNoteCount : 0f;
            float rotDeg = RecordRingRotationSpeed(matchRatio, densityNorm);
            if (matchRatio < config.directionMatchRatioThreshold) rotDeg = -rotDeg;
            StartCoroutine(SpinRingContinuous(entry.Root.transform, rotDeg));
        }

        if (alphaScale < 1f)
        {
            var mpbs = MakeMpbs(_recordRings.Count);
            ApplyAlpha(_recordRings.ToArray(), alphaScale, mpbs);
        }

        RefreshPlayAreaFit(_recordRings.Count, bandedRadii[bandedRadii.Length - 1] + config.ringThickness);
    }

    /// <summary>
    /// Render gray/unlit placeholder rings for a motif that hasn't been played yet — no
    /// snapshot data exists, so ring count comes directly from <paramref name="ringCount"/>
    /// (the motif's nodesPerStar) rather than recorded bins. Used for the "next up" locked
    /// entry in the PhaseLibrary carousel: selectable, but nothing to preview-play.
    /// </summary>
    public void ApplyLockedPreview(int ringCount, float alphaScale = 1f)
    {
        StopAllCoroutines();
        _recordFadingOut   = false;
        _gameplayFadingOut = false;
        DestroyList(_recordRings);
        ResetHighlightState();

        if (config == null) return;

        ringCount = Mathf.Max(0, ringCount);
        int segs  = Mathf.Max(16, config.segments);
        alphaScale = Mathf.Clamp01(alphaScale);

        for (int i = 0; i < ringCount; i++)
        {
            float innerR = RingInnerRadius(i);
            float outerR = innerR + config.ringThickness;

            var entry = BuildRingEntry($"LockedRing_{i}", innerR, outerR, segs,
                config.remainingRingColor, default, -1,
                new List<MotifSnapshot.NoteEntry>(), 0,
                config.remainingRingAlpha * alphaScale, config.remainingContourAlpha * alphaScale,
                ringOrdinal: i);
            _recordRings.Add(entry);

            StartCoroutine(AnimateMeshFill(
                entry.Fill.GetComponent<MeshFilter>().sharedMesh,
                entry.FullTris, segs, delay: i * config.ringStaggerDelay, config.ringDrawInDuration));

            // No Contour draw-in here: a locked-preview ring never has notes (see the empty note
            // list passed to BuildRingEntry above), so there are no dips to trace — it's just a
            // plain circle. The filled annulus mesh above already IS that plain circle (a solid,
            // seam-free mesh, unlike the Contour's LineRenderer — see MotifRingGlyphGenerator.cs's
            // class-level remarks for why that LineRenderer's closure is fragile), so it alone is
            // "the ring." entry.Contour still exists (BuildRingEntry always creates it) but is left
            // at its construction default of positionCount = 0 — present, but drawing nothing.
        }

        RefreshPlayAreaFit(_recordRings.Count);
    }

    /// <summary>
    /// Pulses the ring(s) whose BinIndex matches <paramref name="binIndex"/> (any role) up to
    /// config.highlightScaleMultiplier, and shrinks back to normal any previously-highlighted
    /// ring that no longer matches. Driven by RingPreviewPlayer.OnBinIndexChanged for the
    /// centered PhaseLibraryCarousel slot only.
    /// </summary>
    public void SetPlayingBin(int binIndex)
    {
        if (config == null) return;

        for (int i = 0; i < _recordRings.Count; i++)
        {
            bool shouldHighlight = _recordRings[i].BinIndex == binIndex;
            bool isHighlighted   = _highlightedRingIndices.Contains(i);
            if (shouldHighlight == isHighlighted) continue;

            if (_ringPulseCoroutines.TryGetValue(i, out var co) && co != null) StopCoroutine(co);
            _ringPulseCoroutines[i] = StartCoroutine(AnimateRingHighlight(_recordRings[i], shouldHighlight));

            if (shouldHighlight) _highlightedRingIndices.Add(i);
            else                 _highlightedRingIndices.Remove(i);
        }
    }

    /// <summary>Un-highlights every ring — used when preview playback stops.</summary>
    public void ClearPlayingBinHighlight() => SetPlayingBin(int.MinValue); // no ring ever matches this bin

    /// <summary>
    /// Spawns a one-shot particle effect at the ring position where a note is playing, tinted
    /// with that ring's role color. Driven by RingPreviewPlayer.OnNoteFired for the centered
    /// PhaseLibraryCarousel slot only. angleRad uses the same convention as the note-dip
    /// placement built in AnimateApply (0 = +X axis, counter-clockwise).
    /// </summary>
    public void SpawnNoteFirework(MusicalRole role, int binIndex, float angleRad)
    {
        if (notePlaybackFireworkPrefab == null || config == null) return;

        for (int i = 0; i < _recordRings.Count; i++)
        {
            var ring = _recordRings[i];
            if (ring.BinIndex != binIndex || ring.Role != role || ring.Root == null) continue;

            // Match the contour's actual tug-dip depth at this angle (same tugR math as the
            // noteInfos built in AnimateApply) rather than the ring's plain, undipped radius —
            // otherwise the firework sits on the circle the ring would be at with no notes,
            // not the pulled-in position the note's dip is actually drawn at.
            float tugR = ring.OuterRadius * (1f - config.tugDepthFraction);
            // angleRad (from RingPreviewPlayer) already carries the global ringStartAngleOffset,
            // but not this specific ring's ordinal jitter — add it here where ring.RingOrdinal is
            // actually resolved, so the firework lands on the same dip the contour drew.
            float jitteredAngle = angleRad + MotifRingGlyphGenerator.RingOrdinalAngleJitter(ring.RingOrdinal);
            var localPos = new Vector3(Mathf.Cos(jitteredAngle) * tugR, Mathf.Sin(jitteredAngle) * tugR, 0f);
            var worldPos = ring.Root.transform.TransformPoint(localPos);

            // Parented to the ring (unlike SpawnBreakEffect in GameplayRings.cs, which stays
            // unparented since it marks a past one-off event) — the ring keeps rotating/tumbling
            // after spawn, so the effect needs to inherit that motion to stay glued to the note's
            // spot for its whole lifetime. Tradeoff: if this ring is destroyed/rebuilt mid-effect
            // (e.g. carousel navigation), the firework is cut off early along with it.
            var go = Instantiate(notePlaybackFireworkPrefab, worldPos, Quaternion.identity, ring.Root.transform);
            var ps = go.GetComponentInChildren<ParticleSystem>();
            if (ps != null)
            {
                var main = ps.main;
                main.startColor = ring.BaseColor;
                // Parenting the GameObject only carries the emission point with the ring — each
                // emitted particle still simulates in whatever space the prefab was authored with.
                // Force Local so already-emitted particles keep rotating/moving with the ring too,
                // reading as energy welded to that spot on the ring rather than sparks that drift
                // off into world space independent of the ring's spin.
                main.simulationSpace = ParticleSystemSimulationSpace.Local;

                // A motif with many total rings can push RecordRingSortingOrder well past this
                // renderer's unset default (Unity's default is low, e.g. 0), so on a heavily-
                // stacked record the firework would otherwise render fully behind the ring stack
                // — spawned in the right place but invisible. Match TravelDotSortingOrder's
                // "far above any realistic stack depth" approach for the same reason.
                var psRenderer = ps.GetComponent<ParticleSystemRenderer>();
                if (psRenderer != null) psRenderer.sortingOrder = TravelDotSortingOrder;
            }
            return;
        }
    }

    // Scale-pulses ring.Root and, in step, lerps Fill/Contour color toward white (brightness
    // boost) and back — same on/off transition, driven by config.highlightPulseDuration.
    private IEnumerator AnimateRingHighlight(RingEntry ring, bool highlightOn)
    {
        if (ring.Root == null) yield break;
        Transform t = ring.Root.transform;

        Vector3 startScale = t.localScale;
        Vector3 endScale   = highlightOn ? Vector3.one * config.highlightScaleMultiplier : Vector3.one;

        var scratchMpb = new MaterialPropertyBlock();
        Color fillStart = Color.clear, fillEnd = Color.clear;
        if (ring.Fill != null)
        {
            ring.Fill.GetPropertyBlock(scratchMpb);
            fillStart = scratchMpb.GetColor(BasePropId);
            fillEnd   = highlightOn
                ? BrightenColor(ring.BaseColor, config.ringAlpha)
                : DimColor(ring.BaseColor, config.ringAlpha * config.nonHighlightedAlphaMultiplier);
        }

        Color contourStart = Color.clear, contourEnd = Color.clear;
        if (ring.Contour != null)
        {
            contourStart = ring.Contour.startColor;
            contourEnd   = highlightOn
                ? BrightenColor(ring.BaseColor, config.contourAlpha)
                : DimColor(ring.BaseColor, config.contourAlpha * config.nonHighlightedAlphaMultiplier);
        }

        // Fading out shares the pulse-in's smooth feel but runs on its own (usually shorter)
        // duration, so an outgoing ring can clear away before the next one finishes brightening
        // instead of the two overlapping into a lingering half-dark, half-transparent smear.
        float duration = highlightOn ? config.highlightPulseDuration : config.nonHighlightedTransitionDuration;

        float elapsed = 0f;
        while (elapsed < duration)
        {
            if (t == null) yield break;
            elapsed += Time.deltaTime;
            float u = Mathf.Clamp01(elapsed / duration);

            t.localScale = Vector3.Lerp(startScale, endScale, u);
            if (ring.Fill != null)    SetFillColor(ring.Fill, Color.Lerp(fillStart, fillEnd, u), scratchMpb);
            if (ring.Contour != null)
            {
                var lc = Color.Lerp(contourStart, contourEnd, u);
                ring.Contour.startColor = lc;
                ring.Contour.endColor   = lc;
            }
            yield return null;
        }

        t.localScale = endScale;
        if (ring.Fill != null)    SetFillColor(ring.Fill, fillEnd, scratchMpb);
        if (ring.Contour != null) { ring.Contour.startColor = contourEnd; ring.Contour.endColor = contourEnd; }
    }

    // Lerps toward white by config.highlightBrightness, preserving the requested alpha.
    private Color BrightenColor(Color baseColor, float alpha)
    {
        Color bright = Color.Lerp(baseColor, Color.white, config.highlightBrightness);
        bright.a = alpha;
        return bright;
    }

    // Lerps toward black by config.nonHighlightedDarkenAmount, preserving the requested alpha.
    private Color DimColor(Color baseColor, float alpha)
    {
        Color dim = Color.Lerp(baseColor, Color.black, config.nonHighlightedDarkenAmount);
        dim.a = alpha;
        return dim;
    }

    // Sets a freshly-built ring straight to its resting (non-highlighted) look — dimmed and more
    // transparent — so rings don't flash at full brightness before the first SetPlayingBin call
    // (playback's first bin) arrives. AnimateRingHighlight reads this back as its lerp start.
    private void ApplyInitialNonHighlightedLook(RingEntry entry)
    {
        if (config == null) return;

        if (entry.Fill != null)
        {
            var mpb = new MaterialPropertyBlock();
            SetFillColor(entry.Fill,
                DimColor(entry.BaseColor, config.ringAlpha * config.nonHighlightedAlphaMultiplier), mpb);
        }
        if (entry.Contour != null)
        {
            var lc = DimColor(entry.BaseColor, config.contourAlpha * config.nonHighlightedAlphaMultiplier);
            entry.Contour.startColor = lc;
            entry.Contour.endColor   = lc;
        }
    }

    // Older (inner) rings' note dips visually intrude outward past their own band, so without
    // this an outer/newer ring — drawn later in the build loop — covers them. Stacks the fill
    // and contour of ring `ringIndex` (0 = oldest/innermost) two sortingOrder slots above the
    // next ring out, so the whole record renders oldest-on-top regardless of build order. The
    // newest/outermost ring (stackSlot 0) lands exactly on the shared FillSortingOrder/
    // ContourSortingOrder pair used by non-stacked callers (gameplay rings, locked preview).
    private static (int fill, int contour) RecordRingSortingOrder(int ringIndex, int ringCount)
    {
        int stackSlot = ringCount - 1 - ringIndex;
        int fill      = FillSortingOrder + stackSlot * 2;
        return (fill, fill + 1);
    }

    // Rings sharing a BinIndex band tightly together (ringSpacing gap); a bin transition gets the
    // larger interBinSpacing gap instead. Relies on ringKeys already being sorted by (BinIndex,
    // Role) — see BuildRingKeys — so consecutive same-BinIndex entries are exactly the ones that
    // should band.
    private static float[] ComputeBandedInnerRadii(
        List<(int binIndex, MusicalRole role, Color color, float matchRatio, int noteCount)> ringKeys,
        RingGlyphConfig config)
    {
        var radii = new float[ringKeys.Count];
        float r = config.innerRadius;
        for (int i = 0; i < ringKeys.Count; i++)
        {
            if (i > 0)
            {
                bool sameBin = ringKeys[i].binIndex == ringKeys[i - 1].binIndex;
                r += config.ringThickness + (sameBin ? config.ringSpacing : config.interBinSpacing);
            }
            radii[i] = r;
        }
        return radii;
    }

    private IEnumerator SpinRingContinuous(Transform t, float rotDegPerSec)
    {
        while (!_recordFadingOut)
        {
            if (t == null) yield break;
            t.Rotate(0f, 0f, rotDegPerSec * Time.deltaTime);
            yield return null;
        }
    }

    // Recorded-session-driven rotation speed: a ring's density (relative to the busiest bin in
    // this recording) and match ratio both push speed up toward rotSpeedMax.
    private float RecordRingRotationSpeed(float matchRatio, float densityNorm) =>
        Mathf.Lerp(config.rotSpeedBase, config.rotSpeedMax,
            Mathf.Clamp01(0.5f * densityNorm + 0.5f * matchRatio));

    // Builds ordered ring keys (ascending BinIndex, then MusicalRole) from a motif snapshot,
    // with a fallback for legacy snapshots saved before TrackBins serialization was added
    // (derives keys directly from CollectedNotes grouped by (binIndex, trackColor)).
    private static List<(int binIndex, MusicalRole role, Color color, float matchRatio, int noteCount)>
        BuildRingKeys(MotifSnapshot snapshot)
    {
        var seen     = new HashSet<(int, MusicalRole)>();
        var ringKeys = new List<(int binIndex, MusicalRole role, Color color, float matchRatio, int noteCount)>();

        // Picks whichever bin has the most notes as the representative per (binIndex, color) —
        // duplicate bins sharing a key are rare, but when they occur the more complete one is
        // the more informative source for the speed/direction signals below.
        var binStatsByKey = new Dictionary<(int, float, float, float), (float matchRatio, int noteCount)>();
        foreach (var bin in snapshot.TrackBins)
        {
            Color c     = bin.TrackColor;
            var   k     = (bin.BinIndex, c.r, c.g, c.b);
            int   count = bin.MatchedNoteCount + bin.UnmatchedNoteCount;
            if (!binStatsByKey.TryGetValue(k, out var existing) || count > existing.noteCount)
                binStatsByKey[k] = (bin.MatchRatio, count);
        }

        foreach (var bin in snapshot.TrackBins
                     .Where(b => b.IsFilled || b.CollectedSteps.Count > 0)
                     .OrderBy(b => b.BinIndex).ThenBy(b => (int)b.Role))
        {
            var key = (bin.BinIndex, bin.Role);
            if (!seen.Add(key)) continue;
            Color c2 = bin.TrackColor;
            binStatsByKey.TryGetValue((bin.BinIndex, c2.r, c2.g, c2.b), out var stats);
            ringKeys.Add((bin.BinIndex, bin.Role, c2, stats.matchRatio, stats.noteCount));
        }

        if (ringKeys.Count == 0)
        {
            var seenLegacy = new HashSet<(int, float, float, float)>();
            foreach (var n in snapshot.CollectedNotes.OrderBy(n => n.BinIndex))
            {
                Color c = n.TrackColor;
                var   ck = (n.BinIndex, c.r, c.g, c.b);
                if (!seenLegacy.Add(ck)) continue;
                // No TrackBinData to read for legacy snapshots — default to base speed / forward
                // direction (matchRatio 1f, noteCount 0), matching pre-change behavior.
                ringKeys.Add((n.BinIndex, MusicalRole.None, c, 1f, 0));
            }
        }

        return ringKeys;
    }
}
