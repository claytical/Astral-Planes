using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// =========================================================================
//  MotifRingGlyphApplicator
//
//  MonoBehaviour that owns the LineRenderers for the ring glyph system.
//  Place on any persistent GO. Call AnimateApply(snapshot) to build rings
//  with animated draw-in + per-ring Z rotation, FadeOutAndClear(dur) to
//  fade them out, and Clear() to remove immediately.
//
//  Usage:
//    ringApplicator.AnimateApply(snapshot);
//    ringApplicator.FitToPlayArea(areaWidth, areaHeight, cx, cy);
//    StartCoroutine(ringApplicator.FadeOutAndClear(config.fadeOutDuration));
// =========================================================================
public class MotifRingGlyphApplicator : MonoBehaviour
{
    [Header("Config")]
    [Tooltip("ScriptableObject controlling ring layout, segment count, tug, and animation parameters.")]
    public RingGlyphConfig config;

    [Tooltip("Material applied to all ring LineRenderers. Sprites/Default works for unlit rings.")]
    public Material lineMaterial;

    private readonly List<LineRenderer> _rings = new();
    private bool _fadingOut;

    private struct NoteAnimInfo
    {
        public InstrumentTrack Track;
        public int             AbsStep;
        public float           NoteAngle;    // radians [0, 2π)
        public Vector3         TugLocalPos;  // in ring-local space
        public Color           DotColor;
    }

    // ── Public API ───────────────────────────────────────────────────────────

    void Start()
    {
        GameFlowManager.Instance.RegisterRingGlyphApplicator(this);
    }

    /// <summary>
    /// Build rings from the snapshot with animated draw-in and per-ring Z-axis rotation.
    /// Each ring traces itself in over <c>config.ringDrawInDuration</c> seconds (staggered),
    /// then rotates continuously at a speed derived from its bin's fill duration.
    /// Stops and replaces any previously running animation.
    /// </summary>
    public void AnimateApply(MotifSnapshot snapshot)
    {
        StopAllCoroutines();
        _fadingOut = false;
        Clear();

        if (snapshot == null || config == null) return;

        var polylines = MotifRingGlyphGenerator.Generate(snapshot, config);
        if (polylines.Count == 0) return;

        // Build lookup: (binIndex, r, g, b) → FillDurationSeconds
        var fillDurs = new Dictionary<(int, float, float, float), float>();
        foreach (var bin in snapshot.TrackBins)
        {
            Color c = bin.TrackColor;
            var key = (bin.BinIndex, c.r, c.g, c.b);
            if (!fillDurs.TryGetValue(key, out float existing) || bin.FillDurationSeconds > existing)
                fillDurs[key] = bin.FillDurationSeconds;
        }

        // Resolve NoteVisualizer and build color→track lookup for travel dots
        var noteViz = GameFlowManager.Instance?.controller?.noteVisualizer;
        var tracks  = GameFlowManager.Instance?.controller?.tracks;
        var trackByQColor = new Dictionary<Color, InstrumentTrack>();
        if (tracks != null)
            foreach (var tr in tracks)
                if (tr != null) { Color32 q = tr.trackColor; trackByQColor[(Color)q] = tr; }

        for (int i = 0; i < polylines.Count; i++)
        {
            var poly = polylines[i];

            var go = new GameObject(poly.LayerName);
            go.transform.SetParent(transform, worldPositionStays: false);

            var lr = go.AddComponent<LineRenderer>();
            if (lineMaterial != null) lr.material = lineMaterial;
            lr.useWorldSpace     = false;
            lr.loop              = false;
            lr.widthMultiplier   = poly.LineWidth;
            lr.startColor        = poly.LineColor;
            lr.endColor          = poly.LineColor;
            lr.positionCount     = 0;   // starts empty; draw-in coroutine populates it
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows    = false;
            _rings.Add(lr);

            // Rotation speed: scales with how long this bin took to fill
            Color lc = poly.LineColor;
            fillDurs.TryGetValue((poly.BinIndex, lc.r, lc.g, lc.b), out float fillDur);
            float rotDeg = Mathf.Clamp(
                config.rotSpeedBase * Mathf.Max(fillDur, 0.1f),
                0f, config.rotSpeedMax);
            // Alternate clockwise / counterclockwise per ring for visual depth
            if (i % 2 == 1) rotDeg = -rotDeg;

            // Build per-ring note infos for travel dot animation
            int   binSize = Mathf.Max(1, snapshot.TotalSteps);
            float tugR    = (config.innerRadius + i * (config.ringSpacing + config.lineWidth))
                            * (1f - config.tugDepthFraction);

            var noteInfos = new List<NoteAnimInfo>();
            foreach (var n in snapshot.CollectedNotes)
            {
                if (n.BinIndex != poly.BinIndex) continue;
                Color32 q = n.TrackColor;
                if (!trackByQColor.TryGetValue((Color)q, out var track)) continue;

                int   localStep = n.Step % binSize;
                float angle     = (localStep / (float)binSize) * Mathf.PI * 2f;
                float tx        = Mathf.Cos(angle) * tugR;
                float ty        = Mathf.Sin(angle) * tugR;

                noteInfos.Add(new NoteAnimInfo
                {
                    Track       = track,
                    AbsStep     = n.Step,
                    NoteAngle   = angle,
                    TugLocalPos = new Vector3(tx, ty, 0f),
                    DotColor    = poly.LineColor,
                });
            }

            // Sort by angle so dots fire in draw-in order
            noteInfos.Sort((a, b) => a.NoteAngle.CompareTo(b.NoteAngle));

            float delay = i * config.ringStaggerDelay;
            StartCoroutine(AnimateSingleRing(lr, poly.Points, delay,
                config.ringDrawInDuration, go.transform, rotDeg, noteInfos, noteViz));
        }
    }

    /// <summary>
    /// Fade all rings from their current alpha to transparent over <paramref name="duration"/>
    /// seconds, then destroy them. Safe to fire-and-forget with StartCoroutine.
    /// </summary>
    public IEnumerator FadeOutAndClear(float duration)
    {
        _fadingOut = true;   // signals rotation loops in AnimateSingleRing to exit

        var startColors = _rings.ConvertAll(lr => lr != null ? lr.startColor : Color.clear);
        float elapsed = 0f;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float alpha = 1f - Mathf.Clamp01(elapsed / duration);
            for (int i = 0; i < _rings.Count; i++)
            {
                if (_rings[i] == null) continue;
                Color c = startColors[i];
                c.a = alpha;
                _rings[i].startColor = _rings[i].endColor = c;
            }
            yield return null;
        }

        Clear();
        _fadingOut = false;
    }

    /// <summary>
    /// Rebuild all ring LineRenderers from the supplied snapshot with no animation.
    /// Destroys any previously built rings first.
    /// </summary>
    public void Apply(MotifSnapshot snapshot)
    {
        StopAllCoroutines();
        _fadingOut = false;
        Clear();

        if (snapshot == null || config == null) return;

        var polylines = MotifRingGlyphGenerator.Generate(snapshot, config);

        foreach (var poly in polylines)
        {
            var go = new GameObject(poly.LayerName);
            go.transform.SetParent(transform, worldPositionStays: false);

            var lr = go.AddComponent<LineRenderer>();
            if (lineMaterial != null) lr.material = lineMaterial;
            lr.useWorldSpace     = false;
            lr.loop              = false;
            lr.widthMultiplier   = poly.LineWidth;
            lr.startColor        = poly.LineColor;
            lr.endColor          = poly.LineColor;
            lr.positionCount     = poly.Points.Count;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows    = false;

            for (int i = 0; i < poly.Points.Count; i++)
                lr.SetPosition(i, new Vector3(poly.Points[i].x, poly.Points[i].y, 0f));

            _rings.Add(lr);
        }
    }

    /// <summary>Destroy all ring child GameObjects and clear the renderer list.</summary>
    public void Clear()
    {
        foreach (Transform child in transform)
            Destroy(child.gameObject);
        _rings.Clear();
    }

    /// <summary>
    /// Center and scale the ring group to fit within a world-space rectangle.
    /// </summary>
    public void FitToPlayArea(float width, float height, float cx, float cy)
    {
        transform.position   = new Vector3(cx, cy, transform.position.z);
        float scale          = Mathf.Min(width, height);
        transform.localScale = new Vector3(scale, scale, 1f);
    }

    private void OnDestroy() => Clear();

    // ── Animation ────────────────────────────────────────────────────────────

    private IEnumerator AnimateSingleRing(
        LineRenderer lr,
        List<Vector2> pts,
        float delay,
        float drawDuration,
        Transform ringTransform,
        float rotDegPerSec,
        List<NoteAnimInfo> noteInfos,
        NoteVisualizer noteViz)
    {
        if (delay > 0f) yield return new WaitForSeconds(delay);

        // Phase 1: Draw-in — grow positionCount from 2 → total over drawDuration
        int   total    = pts.Count;
        int   nextNote = 0;
        float elapsed  = 0f;
        lr.positionCount = 2;

        while (elapsed < drawDuration)
        {
            elapsed += Time.deltaTime;
            float progress   = Mathf.Clamp01(elapsed / drawDuration);
            float drawnAngle = progress * Mathf.PI * 2f;

            // Fire a travel dot for each note whose angle the trace has passed
            while (nextNote < noteInfos.Count && drawnAngle >= noteInfos[nextNote].NoteAngle)
            {
                var info = noteInfos[nextNote++];
                if (noteViz != null && noteViz.noteMarkers != null &&
                    info.Track != null &&
                    noteViz.noteMarkers.TryGetValue((info.Track, info.AbsStep), out var markerTr) &&
                    markerTr != null)
                {
                    StartCoroutine(TravelNoteDot(
                        markerTr.position, ringTransform, info.TugLocalPos,
                        config.noteTravelDuration, info.DotColor));
                }
            }

            int count = Mathf.Clamp(
                Mathf.RoundToInt(progress * total),
                2, total);
            lr.positionCount = count;
            for (int i = 0; i < count; i++)
                lr.SetPosition(i, new Vector3(pts[i].x, pts[i].y, 0f));
            yield return null;
        }

        // Ensure all points are set at full draw completion
        lr.positionCount = total;
        for (int i = 0; i < total; i++)
            lr.SetPosition(i, new Vector3(pts[i].x, pts[i].y, 0f));

        // Phase 2: Rotate until FadeOutAndClear signals _fadingOut
        while (!_fadingOut)
        {
            ringTransform.Rotate(0f, 0f, rotDegPerSec * Time.deltaTime);
            yield return null;
        }
    }

    private IEnumerator TravelNoteDot(
        Vector3 startWorld, Transform ringTransform, Vector3 tugLocalPos,
        float duration, Color color)
    {
        var go = new GameObject("NoteTravelDot");
        go.transform.SetParent(transform, worldPositionStays: true);

        var lr = go.AddComponent<LineRenderer>();
        if (lineMaterial != null) lr.material = lineMaterial;
        lr.startColor        = lr.endColor = color;
        lr.widthMultiplier   = config.lineWidth * 2f;
        lr.useWorldSpace     = false;
        lr.loop              = false;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows    = false;

        // Small circle in dot's own local space
        const int segs = 8;
        float r = config.noteDotRadius;
        lr.positionCount = segs + 1;
        for (int i = 0; i <= segs; i++)
        {
            float a = i / (float)segs * Mathf.PI * 2f;
            lr.SetPosition(i, new Vector3(Mathf.Cos(a) * r, Mathf.Sin(a) * r, 0f));
        }

        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float   t   = Mathf.Clamp01(elapsed / duration);
            Vector3 end = ringTransform.TransformPoint(tugLocalPos); // live world pos, tracks ring rotation
            go.transform.position = Vector3.Lerp(startWorld, end, t);
            yield return null;
        }

        Destroy(go);
    }
}
