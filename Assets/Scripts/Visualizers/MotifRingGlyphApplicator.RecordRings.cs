using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// ── Record ring API ──────────────────────────────────────────────────────
public partial class MotifRingGlyphApplicator
{
    // True while the record rings are the gray locked-preview look (alpha comes from the remaining-ring settings).
    private bool _lockedLook;

    /// <summary>
    /// Build filled + contour record rings from the full motif snapshot with
    /// staggered draw-in, note travel dots, and continuous rotation.
    /// </summary>
    public void AnimateApply(MotifSnapshot snapshot, bool replayLastRing = true)
    {
        StopAllCoroutines();
        _lockedLook              = false;
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
        var bandedRadii = ComputeRadii(ringKeys, out float ringThick);
        int maxNoteCount = ringKeys.Count > 0 ? ringKeys.Max(k => k.noteCount) : 0;

        for (int i = 0; i < ringKeys.Count; i++)
        {
            var (binIndex, role, color, matchRatio, noteCount) = ringKeys[i];
            float innerR     = bandedRadii[i];
            float outerR     = innerR + ringThick;
            bool  isLastRing = replayLastRing && i == ringKeys.Count - 1;

            // Captured per-iteration (not the loop's `i`) so each ring's rotation-gate closure,
            // read across many later frames, checks its own slot in _recordRings — not whatever
            // `i` happened to be when the loop finished.
            int ringIndex = i;

            var ringNotes = NotesForRing(snapshot, binIndex, color);

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
                    isHighlighted: () => _highlightedRingIndices.Contains(ringIndex)));
            }
        }

        RefreshPlayAreaFit(_recordRings.Count, bandedRadii[bandedRadii.Length - 1] + ringThick);
    }

    /// <summary>
    /// Render rings from a snapshot instantly (no draw-in) as a flat spinning vinyl disc.
    /// Used for non-highlighted carousel slots that should look like rotating records.
    /// Each ring spins on its local Z-axis.
    /// </summary>
    public void ApplyVinyl(MotifSnapshot snapshot, float alphaScale = 1f)
    {
        StopAllCoroutines();
        _lockedLook        = false;
        _recordFadingOut   = false;
        _gameplayFadingOut = false;
        DestroyList(_recordRings);
        ResetHighlightState();

        if (snapshot == null || config == null) return;

        var ringKeys = BuildRingKeys(snapshot);
        if (ringKeys.Count == 0) return;

        int segs = Mathf.Max(16, config.segments);
        var bandedRadii = ComputeRadii(ringKeys, out float ringThick);
        int maxNoteCount = ringKeys.Count > 0 ? ringKeys.Max(k => k.noteCount) : 0;

        for (int i = 0; i < ringKeys.Count; i++)
        {
            var (binIndex, role, color, matchRatio, noteCount) = ringKeys[i];
            float innerR = bandedRadii[i];
            float outerR = innerR + ringThick;

            var ringNotes = NotesForRing(snapshot, binIndex, color);

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
            StartCoroutine(SpinRingContinuous(i, entry.Root.transform, rotDeg));
        }

        if (alphaScale < 1f) SetAlphaScale(alphaScale);

        RefreshPlayAreaFit(_recordRings.Count, bandedRadii[bandedRadii.Length - 1] + ringThick);
    }

    /// <summary>
    /// Render gray/unlit placeholder rings for a motif that hasn't been played yet — no
    /// snapshot data exists, so ring count comes directly from <paramref name="ringCount"/>
    /// (the motif's nodesPerStar) rather than recorded bins. Used for the "next up" locked
    /// entry in the PhaseLibrary carousel: selectable, but nothing to preview-play.
    /// </summary>
    public void ApplyLockedPreview(int ringCount, float alphaScale = 1f, float lineWidth = 0f)
    {
        StopAllCoroutines();
        _lockedLook        = true;
        _recordFadingOut   = false;
        _gameplayFadingOut = false;
        DestroyList(_recordRings);
        ResetHighlightState();

        if (config == null) return;

        ringCount = Mathf.Max(0, ringCount);
        float ringThick = config.ringThickness;
        float[] fixedRadii = null;
        if (_fixedFit > 0f) fixedRadii = ComputeFixedRadii(ringCount, null, config, out ringThick);
        int segs  = Mathf.Max(16, config.segments);
        alphaScale = Mathf.Clamp01(alphaScale);

        for (int i = 0; i < ringCount; i++)
        {
            float innerR = fixedRadii != null ? fixedRadii[i] : RingInnerRadius(i);
            float outerR = innerR + ringThick;

            // A locked ring has no contour, so its filled band is the line: thin it to the contour
            // width a recorded ring would have, centred on the same outer edge.
            if (fixedRadii != null && lineWidth > 0f && lineWidth < ringThick)
            {
                float edge = outerR;
                innerR = edge - lineWidth * 0.5f;
                outerR = edge + lineWidth * 0.5f;
            }

            var entry = BuildRingEntry($"LockedRing_{i}", innerR, outerR, segs,
                config.remainingRingColor, default, -1,
                new List<MotifSnapshot.NoteEntry>(), 0,
                config.remainingRingAlpha * alphaScale, 0f,
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

        RefreshPlayAreaFit(_recordRings.Count, fixedRadii != null ? 1f : (float?)null);
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

            // The playing bin's contour draws above every other ring (just under the travel dots /
            // fireworks); otherwise it goes back to its slot in the stack.
            if (_recordRings[i].Contour != null)
                _recordRings[i].Contour.sortingOrder = shouldHighlight
                    ? TravelDotSortingOrder - 1
                    : RecordRingSortingOrder(i, _recordRings.Count).contour;
        }
    }

    /// <summary>Sets the contour line width of the already-built record rings, in place.</summary>
    public void SetContourWidth(float width)
    {
        foreach (var e in _recordRings)
            if (e.Contour != null) e.Contour.widthMultiplier = width;
    }

    /// <summary>
    /// Re-tints the already-built record rings to <paramref name="alphaScale"/> (0–1) without
    /// rebuilding them. Used by the carousel to fade a glyph between selected and unselected.
    /// </summary>
    public void SetAlphaScale(float alphaScale)
    {
        if (config == null || _recordRings.Count == 0) return;
        alphaScale = Mathf.Clamp01(alphaScale);
        float fillA    = _lockedLook ? config.remainingRingAlpha    : config.ringAlpha;
        float contourA = _lockedLook ? 0f : config.contourAlpha;
        var mpb = new MaterialPropertyBlock();
        foreach (var e in _recordRings)
        {
            Color c = e.BaseColor;
            if (e.Fill != null) SetFillColor(e.Fill, new Color(c.r, c.g, c.b, fillA * alphaScale), mpb);
            if (e.Contour != null)
            {
                var lc = new Color(c.r, c.g, c.b, contourA * alphaScale);
                e.Contour.startColor = lc;
                e.Contour.endColor   = lc;
            }
        }
    }

    private SpriteRenderer _selectionBackdrop;
    private static Sprite  _circleSprite;

    /// <summary>
    /// Carousel only: fades a black circle in behind this glyph (k = 1) so the lines of the glyphs
    /// behind it don't cross the selected glyph's translucent rings, or out (k = 0). The sprite is
    /// 1 unit across, so it is scaled to the stack's actual reach (see BackdropRadius). It sorts
    /// below every ring (order 0 vs FillSortingOrder).
    /// </summary>
    public void SetSelectedBackdrop(float k)
    {
        k = Mathf.Clamp01(k);

        if (_selectionBackdrop == null)
        {
            if (k <= 0f) return;
            var go = new GameObject("SelectionBackdrop");
            go.transform.SetParent(transform, worldPositionStays: false);
            _selectionBackdrop              = go.AddComponent<SpriteRenderer>();
            _selectionBackdrop.sprite       = CircleSprite();
            _selectionBackdrop.sortingOrder = FullBgDiscSortingOrder - 1;
        }

        _selectionBackdrop.enabled = k > 0f;
        _selectionBackdrop.color   = new Color(0f, 0f, 0f, k);
        _selectionBackdrop.transform.localScale = Vector3.one * (2f * BackdropRadius());
    }

    // Outermost reach of the record in local units: the largest ring's outer edge, grown by the
    // playing-bin highlight's scale pulse (which scales a ring's Root past its built radius) and
    // half the contour width. Falls back to the fixed-fit unit radius before any ring is built.
    private float BackdropRadius()
    {
        float outer = 0f;
        foreach (var e in _recordRings) outer = Mathf.Max(outer, e.OuterRadius);
        if (outer <= 0f) return 1f;
        return outer * Mathf.Max(1f, config != null ? config.highlightScaleMultiplier : 1f)
             + (config != null ? config.recordLineWidth * 0.5f : 0f);
    }

    private static Sprite CircleSprite()
    {
        if (_circleSprite != null) return _circleSprite;

        const int size = 256;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        var px  = new Color32[size * size];
        float r = size * 0.5f;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Mathf.Sqrt((x + 0.5f - r) * (x + 0.5f - r) + (y + 0.5f - r) * (y + 0.5f - r));
                px[y * size + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(r - d) * 255f)); // 1px antialiased edge
            }
        tex.SetPixels32(px);
        tex.Apply();
        _circleSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
        return _circleSprite;
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

    // The snapshot's collected notes that belong on the ring for this bin and track colour.
    private static List<MotifSnapshot.NoteEntry> NotesForRing(MotifSnapshot snapshot, int binIndex, Color color) =>
        snapshot.CollectedNotes
            .Where(n => n.BinIndex == binIndex
                     && Mathf.Approximately(n.SerializedTrackColor.r, color.r)
                     && Mathf.Approximately(n.SerializedTrackColor.g, color.g)
                     && Mathf.Approximately(n.SerializedTrackColor.b, color.b))
            .ToList();

    // Only the ring(s) of the bin currently playing (see SetPlayingBin) turn; the rest hold their angle.
    private IEnumerator SpinRingContinuous(int ringIndex, Transform t, float rotDegPerSec)
    {
        while (!_recordFadingOut)
        {
            if (t == null) yield break;
            if (_highlightedRingIndices.Contains(ringIndex))
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
