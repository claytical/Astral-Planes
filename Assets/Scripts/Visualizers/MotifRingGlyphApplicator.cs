using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// =========================================================================
//  MotifRingGlyphApplicator
//
//  Manages two ring layers:
//
//  Gameplay rings (_gameplayRings): spawned one-at-a-time as bins are
//  completed during play via SpawnBinRing(). Each ring fades at loop
//  boundary via FadeAndClearGameplayRings() or is cleared instantly by
//  ClearGameplayRings() before the bridge record is shown.
//
//  Record rings (_recordRings): built from the full motif snapshot at
//  bridge time via AnimateApply(). These represent the completed motif
//  and fade out at the end of the bridge via FadeOutAndClear().
// =========================================================================
public class MotifRingGlyphApplicator : MonoBehaviour
{
    [Header("Config")]
    [Tooltip("ScriptableObject controlling ring layout, segment count, tug, and animation parameters.")]
    public RingGlyphConfig config;

    [Tooltip("Material applied to all ring LineRenderers. Sprites/Default works for unlit rings.")]
    public Material lineMaterial;

    private readonly List<LineRenderer> _recordRings   = new();
    private readonly List<LineRenderer> _gameplayRings = new();

    private bool _recordFadingOut   = false;
    private bool _gameplayFadingOut = false;

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

    // ── Gameplay ring API ────────────────────────────────────────────────────

    /// <summary>
    /// Spawn a single ring for a just-completed bin. Rings stack outward by the
    /// order they are added. Animates with draw-in and continuous rotation.
    /// </summary>
    public void SpawnBinRing(MusicalRole role, int binIndex, Color color,
                              List<MotifSnapshot.NoteEntry> notes, int totalSteps)
    {
        if (config == null) return;

        int ringIndex = _gameplayRings.Count;
        var poly = MotifRingGlyphGenerator.GenerateSingleRing(
            role, binIndex, color, notes, totalSteps, ringIndex, config);
        if (poly == null) return;

        var go = new GameObject(poly.LayerName);
        go.transform.SetParent(transform, worldPositionStays: false);

        var lr = go.AddComponent<LineRenderer>();
        if (lineMaterial != null) lr.material = lineMaterial;
        lr.useWorldSpace     = false;
        lr.loop              = false;
        lr.widthMultiplier   = poly.LineWidth;
        lr.startColor        = poly.LineColor;
        lr.endColor          = poly.LineColor;
        lr.positionCount     = 0;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows    = false;
        _gameplayRings.Add(lr);

        float rotDeg = Mathf.Clamp(config.rotSpeedBase * 1f, 0f, config.rotSpeedMax);
        if (ringIndex % 2 == 1) rotDeg = -rotDeg;

        StartCoroutine(AnimateSingleRing(
            lr, poly.Points, delay: 0f,
            config.ringDrawInDuration, go.transform, rotDeg,
            noteInfos: new List<NoteAnimInfo>(), noteViz: null,
            shouldStop: () => _gameplayFadingOut));
    }

    /// <summary>Fade and destroy all gameplay rings over <paramref name="duration"/> seconds.</summary>
    public IEnumerator FadeAndClearGameplayRings(float duration)
    {
        _gameplayFadingOut = true;

        var startColors = _gameplayRings.ConvertAll(lr => lr != null ? lr.startColor : Color.clear);
        float elapsed = 0f;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float alpha = 1f - Mathf.Clamp01(elapsed / duration);
            for (int i = 0; i < _gameplayRings.Count; i++)
            {
                if (_gameplayRings[i] == null) continue;
                Color c = startColors[i];
                c.a = alpha;
                _gameplayRings[i].startColor = _gameplayRings[i].endColor = c;
            }
            yield return null;
        }

        DestroyGameplayRings();
        _gameplayFadingOut = false;
    }

    /// <summary>Destroy all gameplay rings immediately with no fade.</summary>
    public void ClearGameplayRings()
    {
        _gameplayFadingOut = true;
        DestroyGameplayRings();
        _gameplayFadingOut = false;
    }

    // ── Record ring API ──────────────────────────────────────────────────────

    /// <summary>
    /// Build record rings from the full motif snapshot with animated draw-in
    /// and per-ring Z-axis rotation. Replaces any previously running record animation.
    /// </summary>
    public void AnimateApply(MotifSnapshot snapshot)
    {
        StopAllCoroutines();
        _recordFadingOut   = false;
        _gameplayFadingOut = false;
        ClearRecordRings();

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
            lr.positionCount     = 0;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows    = false;
            _recordRings.Add(lr);

            Color lc = poly.LineColor;
            fillDurs.TryGetValue((poly.BinIndex, lc.r, lc.g, lc.b), out float fillDur);
            float rotDeg = Mathf.Clamp(
                config.rotSpeedBase * Mathf.Max(fillDur, 0.1f),
                0f, config.rotSpeedMax);
            if (i % 2 == 1) rotDeg = -rotDeg;

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

            noteInfos.Sort((a, b) => a.NoteAngle.CompareTo(b.NoteAngle));

            float delay = i * config.ringStaggerDelay;
            StartCoroutine(AnimateSingleRing(
                lr, poly.Points, delay,
                config.ringDrawInDuration, go.transform, rotDeg, noteInfos, noteViz,
                shouldStop: () => _recordFadingOut));
        }
    }

    /// <summary>
    /// Fade all record rings to transparent over <paramref name="duration"/> seconds, then destroy.
    /// </summary>
    public IEnumerator FadeOutAndClear(float duration)
    {
        _recordFadingOut = true;

        var startColors = _recordRings.ConvertAll(lr => lr != null ? lr.startColor : Color.clear);
        float elapsed = 0f;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float alpha = 1f - Mathf.Clamp01(elapsed / duration);
            for (int i = 0; i < _recordRings.Count; i++)
            {
                if (_recordRings[i] == null) continue;
                Color c = startColors[i];
                c.a = alpha;
                _recordRings[i].startColor = _recordRings[i].endColor = c;
            }
            yield return null;
        }

        ClearRecordRings();
        _recordFadingOut = false;
    }

    /// <summary>Rebuild all record rings instantly with no animation.</summary>
    public void Apply(MotifSnapshot snapshot)
    {
        StopAllCoroutines();
        _recordFadingOut = false;
        ClearRecordRings();

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

            _recordRings.Add(lr);
        }
    }

    /// <summary>Destroy all rings (both layers) and clear all lists.</summary>
    public void Clear()
    {
        foreach (Transform child in transform)
            Destroy(child.gameObject);
        _recordRings.Clear();
        _gameplayRings.Clear();
    }

    /// <summary>Center and scale the ring group to fit within a world-space rectangle.</summary>
    public void FitToPlayArea(float width, float height, float cx, float cy)
    {
        transform.position   = new Vector3(cx, cy, transform.position.z);
        float scale          = Mathf.Min(width, height);
        transform.localScale = new Vector3(scale, scale, 1f);
    }

    private void OnDestroy() => Clear();

    // ── Private helpers ──────────────────────────────────────────────────────

    private void ClearRecordRings()
    {
        foreach (var lr in _recordRings)
            if (lr != null) Destroy(lr.gameObject);
        _recordRings.Clear();
    }

    private void DestroyGameplayRings()
    {
        foreach (var lr in _gameplayRings)
            if (lr != null) Destroy(lr.gameObject);
        _gameplayRings.Clear();
    }

    // ── Animation ────────────────────────────────────────────────────────────

    private IEnumerator AnimateSingleRing(
        LineRenderer lr,
        List<Vector2> pts,
        float delay,
        float drawDuration,
        Transform ringTransform,
        float rotDegPerSec,
        List<NoteAnimInfo> noteInfos,
        NoteVisualizer noteViz,
        System.Func<bool> shouldStop)
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

        // Phase 2: Rotate until shouldStop() returns true
        while (!shouldStop())
        {
            if (lr == null) yield break;
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
            Vector3 end = ringTransform.TransformPoint(tugLocalPos);
            go.transform.position = Vector3.Lerp(startWorld, end, t);
            yield return null;
        }

        Destroy(go);
    }
}
