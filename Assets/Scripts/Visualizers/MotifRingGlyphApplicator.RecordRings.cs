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

        for (int i = 0; i < ringKeys.Count; i++)
        {
            var (binIndex, role, color, fillDur) = ringKeys[i];
            float innerR     = RingInnerRadius(i);
            float outerR     = innerR + config.ringThickness;
            bool  isLastRing = (i == ringKeys.Count - 1);

            var ringNotes = snapshot.CollectedNotes
                .Where(n => n.BinIndex == binIndex
                         && Mathf.Approximately(n.SerializedTrackColor.r, color.r)
                         && Mathf.Approximately(n.SerializedTrackColor.g, color.g)
                         && Mathf.Approximately(n.SerializedTrackColor.b, color.b))
                .ToList();

            // Last ring starts flat — its dips animate in as dots travel.
            // All other rings pre-bake their dips (no travel dot replay).
            var entry = BuildRingEntry($"RecordRing_Bin{binIndex}_{role}",
                innerR, outerR, segs, color, role, binIndex,
                isLastRing ? new List<MotifSnapshot.NoteEntry>() : ringNotes,
                snapshot.TotalSteps);
            _recordRings.Add(entry);

            float rotDeg;
            if (sphericalRotation)
            {
                float t = ringKeys.Count > 1 ? (float)i / (ringKeys.Count - 1) : 0f;
                rotDeg = Mathf.Lerp(config.rotSpeedBase, config.rotSpeedMax, t);
                // Distribute rings at evenly-spaced Y angles so they form tilted orbits.
                entry.Root.transform.localEulerAngles =
                    new Vector3(0f, (float)i / ringKeys.Count * 180f, 0f);
            }
            else
            {
                rotDeg = Mathf.Clamp(config.rotSpeedBase * Mathf.Max(fillDur, 0.1f), 0f, config.rotSpeedMax);
            }
            if (i % 2 == 1) rotDeg = -rotDeg;

            float tugR    = outerR * (1f - config.tugDepthFraction);
            var noteInfos = new List<NoteAnimInfo>();
            foreach (var n in ringNotes)
            {
                Color32 q = n.TrackColor;
                if (!trackByQColor.TryGetValue(q, out var track)) continue;
                int   localStep = n.Step % binSize;
                float angle     = localStep / (float)binSize * Mathf.PI * 2f;
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
                    spherical: sphericalRotation));
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
                    spherical: sphericalRotation));
            }
        }

        RefreshPlayAreaFit(_recordRings.Count);
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

        if (snapshot == null || config == null) return;

        var ringKeys = BuildRingKeys(snapshot);
        if (ringKeys.Count == 0) return;

        int segs = Mathf.Max(16, config.segments);

        for (int i = 0; i < ringKeys.Count; i++)
        {
            var (binIndex, role, color, _) = ringKeys[i];
            float innerR = RingInnerRadius(i);
            float outerR = innerR + config.ringThickness;

            var ringNotes = snapshot.CollectedNotes
                .Where(n => n.BinIndex == binIndex
                         && Mathf.Approximately(n.SerializedTrackColor.r, color.r)
                         && Mathf.Approximately(n.SerializedTrackColor.g, color.g)
                         && Mathf.Approximately(n.SerializedTrackColor.b, color.b))
                .ToList();

            var entry = BuildRingEntry($"StaticRing_Bin{binIndex}_{role}",
                innerR, outerR, segs, color, role, binIndex, ringNotes, snapshot.TotalSteps);
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

        RefreshPlayAreaFit(_recordRings.Count);
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

        if (snapshot == null || config == null) return;

        var ringKeys = BuildRingKeys(snapshot);
        if (ringKeys.Count == 0) return;

        int segs = Mathf.Max(16, config.segments);

        for (int i = 0; i < ringKeys.Count; i++)
        {
            var (binIndex, role, color, fillDur) = ringKeys[i];
            float innerR = RingInnerRadius(i);
            float outerR = innerR + config.ringThickness;

            var ringNotes = snapshot.CollectedNotes
                .Where(n => n.BinIndex == binIndex
                         && Mathf.Approximately(n.SerializedTrackColor.r, color.r)
                         && Mathf.Approximately(n.SerializedTrackColor.g, color.g)
                         && Mathf.Approximately(n.SerializedTrackColor.b, color.b))
                .ToList();

            var entry = BuildRingEntry($"VinylRing_Bin{binIndex}_{role}",
                innerR, outerR, segs, color, role, binIndex, ringNotes, snapshot.TotalSteps);
            _recordRings.Add(entry);

            var mesh = entry.Fill.GetComponent<MeshFilter>().sharedMesh;
            mesh.SetTriangles(entry.FullTris, 0);
            mesh.RecalculateBounds();

            entry.Contour.positionCount = entry.ContourPoints.Count;
            for (int j = 0; j < entry.ContourPoints.Count; j++)
                entry.Contour.SetPosition(j, new Vector3(
                    entry.ContourPoints[j].x, entry.ContourPoints[j].y, 0f));

            float rotDeg = Mathf.Clamp(config.rotSpeedBase * Mathf.Max(fillDur, 0.1f), 0f, config.rotSpeedMax);
            if (i % 2 == 1) rotDeg = -rotDeg;
            StartCoroutine(SpinRingContinuous(entry.Root.transform, rotDeg));
        }

        if (alphaScale < 1f)
        {
            var mpbs = MakeMpbs(_recordRings.Count);
            ApplyAlpha(_recordRings.ToArray(), alphaScale, mpbs);
        }

        RefreshPlayAreaFit(_recordRings.Count);
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

    // Builds ordered ring keys (ascending BinIndex, then MusicalRole) from a motif snapshot,
    // with a fallback for legacy snapshots saved before TrackBins serialization was added
    // (derives keys directly from CollectedNotes grouped by (binIndex, trackColor)).
    private static List<(int binIndex, MusicalRole role, Color color, float fillDur)> BuildRingKeys(
        MotifSnapshot snapshot)
    {
        var seen     = new HashSet<(int, MusicalRole)>();
        var ringKeys = new List<(int binIndex, MusicalRole role, Color color, float fillDur)>();

        var fillDurs = new Dictionary<(int, float, float, float), float>();
        foreach (var bin in snapshot.TrackBins)
        {
            Color c = bin.TrackColor;
            var   k = (bin.BinIndex, c.r, c.g, c.b);
            if (!fillDurs.TryGetValue(k, out float ex) || bin.FillDurationSeconds > ex)
                fillDurs[k] = bin.FillDurationSeconds;
        }

        foreach (var bin in snapshot.TrackBins
                     .Where(b => b.IsFilled || b.CollectedSteps.Count > 0)
                     .OrderBy(b => b.BinIndex).ThenBy(b => (int)b.Role))
        {
            var key = (bin.BinIndex, bin.Role);
            if (!seen.Add(key)) continue;
            Color c2 = bin.TrackColor;
            fillDurs.TryGetValue((bin.BinIndex, c2.r, c2.g, c2.b), out float fd);
            ringKeys.Add((bin.BinIndex, bin.Role, c2, fd));
        }

        if (ringKeys.Count == 0)
        {
            var seenLegacy = new HashSet<(int, float, float, float)>();
            foreach (var n in snapshot.CollectedNotes.OrderBy(n => n.BinIndex))
            {
                Color c = n.TrackColor;
                var   ck = (n.BinIndex, c.r, c.g, c.b);
                if (!seenLegacy.Add(ck)) continue;
                ringKeys.Add((n.BinIndex, MusicalRole.None, c, 0f));
            }
        }

        return ringKeys;
    }
}
