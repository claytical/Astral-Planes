using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// =========================================================================
//  MotifRingGlyphApplicator
//
//  Each ring has two visual layers sharing one parent transform:
//
//  Fill    — semi-transparent filled annulus (MeshRenderer) tinted by the
//            musical role color; multiple rings stack like record tracks.
//
//  Contour — note-tug LineRenderer at the outer rim of the filled annulus,
//            sitting in the space just above it. Note travel-dot animations
//            fire from NoteVisualizer markers to the tug points during the
//            record draw-in.
//
//  The parent GO is rotated by the contour animation coroutine, so fill and
//  contour rotate together.
//
//  _gameplayRings — one ring per completed bin, spawned during play
//  _recordRings   — full-motif snapshot, shown at bridge time
// =========================================================================
public class MotifRingGlyphApplicator : MonoBehaviour
{
    [Header("Config")]
    public RingGlyphConfig config;

    [Tooltip("Material for contour LineRenderers (Sprites/Default works well).")]
    public Material lineMaterial;

    private static readonly int BasePropId  = Shader.PropertyToID("_BaseColor");
    private static readonly int ColorPropId = Shader.PropertyToID("_Color");

    private struct RingEntry
    {
        public GameObject    Root;
        public MeshRenderer  Fill;
        public LineRenderer  Contour;
        public Color         BaseColor;
        public int[]         FullTris;       // saved before mesh triangles are cleared
        public List<Vector2> ContourPoints;  // polyline passed to AnimateSingleRing
    }

    private readonly List<RingEntry> _gameplayRings = new();
    private readonly List<RingEntry> _recordRings   = new();

    private bool _recordFadingOut;
    private bool _gameplayFadingOut;

    private struct NoteAnimInfo
    {
        public InstrumentTrack Track;
        public int             AbsStep;
        public float           NoteAngle;
        public Vector3         TugLocalPos;
        public Color           DotColor;
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────

    void Start() => GameFlowManager.Instance.RegisterRingGlyphApplicator(this);
    void OnDestroy() => Clear();

    // ── Gameplay ring API ────────────────────────────────────────────────────

    /// <summary>
    /// Spawn one ring for a just-completed bin: a filled annulus with a
    /// note-tug contour at its outer rim. No travel dots for gameplay rings.
    /// </summary>
    public void SpawnBinRing(MusicalRole role, int binIndex, Color color,
                              List<MotifSnapshot.NoteEntry> notes, int totalSteps)
    {
        if (config == null) return;

        int   idx    = _gameplayRings.Count;
        float innerR = RingInnerRadius(idx);
        float outerR = innerR + config.ringThickness;
        int   segs   = Mathf.Max(16, config.segments);

        var entry = BuildRingEntry($"GameplayRing_Bin{binIndex}_{role}",
            innerR, outerR, segs, color, role, binIndex, notes, totalSteps);
        _gameplayRings.Add(entry);

        float rotDeg = Mathf.Clamp(config.rotSpeedBase, 0f, config.rotSpeedMax);
        if (idx % 2 == 1) rotDeg = -rotDeg;

        StartCoroutine(AnimateMeshFill(
            entry.Fill.GetComponent<MeshFilter>().sharedMesh,
            entry.FullTris, segs, delay: 0f, config.ringDrawInDuration));

        StartCoroutine(AnimateSingleRing(
            entry.Contour, entry.ContourPoints,
            delay: 0f, config.ringDrawInDuration,
            entry.Root.transform, rotDeg,
            new List<NoteAnimInfo>(), noteViz: null,
            shouldStop: () => _gameplayFadingOut));

        RefreshPlayAreaFit(_gameplayRings.Count);
    }

    /// <summary>Fade and destroy all gameplay rings.</summary>
    public IEnumerator FadeAndClearGameplayRings(float duration)
    {
        _gameplayFadingOut = true;
        var snapshot = _gameplayRings.ToArray();
        var mpbs     = MakeMpbs(snapshot.Length);

        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            ApplyAlpha(snapshot, 1f - Mathf.Clamp01(elapsed / duration), mpbs);
            yield return null;
        }

        DestroyList(_gameplayRings);
        _gameplayFadingOut = false;
    }

    /// <summary>Destroy all gameplay rings immediately.</summary>
    public void ClearGameplayRings()
    {
        _gameplayFadingOut = true;
        DestroyList(_gameplayRings);
        _gameplayFadingOut = false;
    }

    // ── Record ring API ──────────────────────────────────────────────────────

    /// <summary>
    /// Build filled + contour record rings from the full motif snapshot with
    /// staggered draw-in, note travel dots, and continuous rotation.
    /// </summary>
    public void AnimateApply(MotifSnapshot snapshot)
    {
        StopAllCoroutines();
        _recordFadingOut   = false;
        _gameplayFadingOut = false;
        DestroyList(_recordRings);

        if (snapshot == null || config == null) return;

        // Build ordered ring keys (ascending BinIndex, then MusicalRole)
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

        if (ringKeys.Count == 0) return;

        var noteViz       = GameFlowManager.Instance?.controller?.noteVisualizer;
        var tracks        = GameFlowManager.Instance?.controller?.tracks;
        var trackByQColor = new Dictionary<Color, InstrumentTrack>();
        if (tracks != null)
            foreach (var tr in tracks)
                if (tr != null) { Color32 q = tr.trackColor; trackByQColor[(Color)q] = tr; }

        int segs    = Mathf.Max(16, config.segments);
        int binSize = Mathf.Max(1, snapshot.TotalSteps);

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

            var entry = BuildRingEntry($"RecordRing_Bin{binIndex}_{role}",
                innerR, outerR, segs, color, role, binIndex, ringNotes, snapshot.TotalSteps);
            _recordRings.Add(entry);

            float rotDeg = Mathf.Clamp(
                config.rotSpeedBase * Mathf.Max(fillDur, 0.1f), 0f, config.rotSpeedMax);
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
                    Track       = track,
                    AbsStep     = n.Step,
                    NoteAngle   = angle,
                    TugLocalPos = new Vector3(Mathf.Cos(angle) * tugR, Mathf.Sin(angle) * tugR, 0f),
                    DotColor    = color,
                });
            }
            noteInfos.Sort((a, b) => a.NoteAngle.CompareTo(b.NoteAngle));

            float delay = i * config.ringStaggerDelay;
            StartCoroutine(AnimateMeshFill(
                entry.Fill.GetComponent<MeshFilter>().sharedMesh,
                entry.FullTris, segs, delay, config.ringDrawInDuration));

            StartCoroutine(AnimateSingleRing(
                entry.Contour, entry.ContourPoints,
                delay, config.ringDrawInDuration,
                entry.Root.transform, rotDeg,
                noteInfos, noteViz,
                shouldStop: () => _recordFadingOut));
        }

        RefreshPlayAreaFit(_recordRings.Count);
    }

    /// <summary>Fade all record rings to transparent, then destroy.</summary>
    public IEnumerator FadeOutAndClear(float duration)
    {
        _recordFadingOut = true;
        var snapshot = _recordRings.ToArray();
        var mpbs     = MakeMpbs(snapshot.Length);

        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            ApplyAlpha(snapshot, 1f - Mathf.Clamp01(elapsed / duration), mpbs);
            yield return null;
        }

        DestroyList(_recordRings);
        _recordFadingOut = false;
    }

    /// <summary>Destroy all rings (both layers).</summary>
    public void Clear()
    {
        foreach (Transform child in transform) Destroy(child.gameObject);
        _recordRings.Clear();
        _gameplayRings.Clear();
    }

    public void FitToPlayArea(float width, float height, float cx, float cy)
    {
        float s = Mathf.Min(width, height);
        transform.position   = new Vector3(cx, cy, transform.position.z);
        transform.localScale = new Vector3(s, s, 1f);
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private float RingInnerRadius(int idx) =>
        config.innerRadius + idx * (config.ringThickness + config.ringSpacing);

    private void RefreshPlayAreaFit(int ringCount)
    {
        if (config == null || ringCount == 0) return;
        var gfm = GameFlowManager.Instance;
        if (gfm?.activeDrumTrack == null) return;
        if (!gfm.activeDrumTrack.TryGetPlayAreaWorld(out var area)) return;

        float outerRadius = RingInnerRadius(ringCount - 1) + config.ringThickness;
        if (outerRadius <= 0f) return;

        float targetRadius = area.height * (0.5f - config.fitPaddingFraction);
        if (targetRadius <= 0f) return;

        float scale = targetRadius / outerRadius;
        transform.position   = new Vector3(
            (area.left + area.right) * 0.5f,
            (area.bottom + area.top) * 0.5f,
            transform.position.z);
        transform.localScale = new Vector3(scale, scale, 1f);
    }

    private RingEntry BuildRingEntry(
        string goName, float innerR, float outerR, int segs,
        Color color, MusicalRole role, int binIndex,
        List<MotifSnapshot.NoteEntry> notes, int totalSteps)
    {
        var root = new GameObject(goName);
        root.transform.SetParent(transform, worldPositionStays: false);

        // ── Fill ─────────────────────────────────────────────────────────────
        var fillGo = new GameObject("Fill");
        fillGo.transform.SetParent(root.transform, worldPositionStays: false);

        var mesh     = BuildAnnulusMesh(innerR, outerR, segs);
        var fullTris = mesh.triangles;          // save before clearing
        mesh.SetTriangles(System.Array.Empty<int>(), 0);

        var mf        = fillGo.AddComponent<MeshFilter>();
        mf.sharedMesh = mesh;

        var mr               = fillGo.AddComponent<MeshRenderer>();
        mr.sharedMaterial    = config.ringMeshMaterial;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows    = false;

        var mpb = new MaterialPropertyBlock();
        SetFillColor(mr, new Color(color.r, color.g, color.b, config.ringAlpha), mpb);

        // ── Contour ───────────────────────────────────────────────────────────
        var contourGo = new GameObject("Contour");
        contourGo.transform.SetParent(root.transform, worldPositionStays: false);

        var poly = MotifRingGlyphGenerator.GenerateSingleRingAtRadius(
            role, binIndex, color, notes, totalSteps, outerR, config);
        var pts = poly?.Points ?? new List<Vector2>();

        var lr = contourGo.AddComponent<LineRenderer>();
        if (lineMaterial != null) lr.material = lineMaterial;
        lr.useWorldSpace     = false;
        lr.loop              = false;
        lr.widthMultiplier   = config.lineWidth;
        lr.startColor        = color;
        lr.endColor          = color;
        lr.positionCount     = 0;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows    = false;

        return new RingEntry
        {
            Root          = root,
            Fill          = mr,
            Contour       = lr,
            BaseColor     = color,
            FullTris      = fullTris,
            ContourPoints = pts,
        };
    }

    private static MaterialPropertyBlock[] MakeMpbs(int count)
    {
        var arr = new MaterialPropertyBlock[count];
        for (int i = 0; i < count; i++) arr[i] = new MaterialPropertyBlock();
        return arr;
    }

    private void ApplyAlpha(RingEntry[] entries, float normalizedAlpha,
                            MaterialPropertyBlock[] mpbs)
    {
        for (int i = 0; i < entries.Length; i++)
        {
            Color c = entries[i].BaseColor;
            if (entries[i].Fill != null)
                SetFillColor(entries[i].Fill,
                    new Color(c.r, c.g, c.b, config.ringAlpha * normalizedAlpha), mpbs[i]);

            if (entries[i].Contour != null)
            {
                var lc = new Color(c.r, c.g, c.b, normalizedAlpha);
                entries[i].Contour.startColor = lc;
                entries[i].Contour.endColor   = lc;
            }
        }
    }

    private static void SetFillColor(MeshRenderer mr, Color c, MaterialPropertyBlock mpb)
    {
        mpb.SetColor(BasePropId,  c);
        mpb.SetColor(ColorPropId, c);
        mr.SetPropertyBlock(mpb);
    }

    private static void DestroyList(List<RingEntry> list)
    {
        foreach (var e in list)
            if (e.Root != null) Destroy(e.Root);
        list.Clear();
    }

    // ── Mesh generation ──────────────────────────────────────────────────────

    private static Mesh BuildAnnulusMesh(float innerR, float outerR, int segments)
    {
        int n     = segments;
        var verts = new Vector3[n * 2];
        var uvs   = new Vector2[n * 2];

        for (int i = 0; i < n; i++)
        {
            float angle = i / (float)n * Mathf.PI * 2f;
            float cos = Mathf.Cos(angle), sin = Mathf.Sin(angle);
            verts[i]     = new Vector3(cos * outerR, sin * outerR, 0f);
            verts[n + i] = new Vector3(cos * innerR, sin * innerR, 0f);
            uvs[i]       = new Vector2(i / (float)n, 1f);
            uvs[n + i]   = new Vector2(i / (float)n, 0f);
        }

        var tris = new int[n * 6];
        for (int i = 0; i < n; i++)
        {
            int next = (i + 1) % n;
            int t    = i * 6;
            tris[t]     = i;        tris[t + 1] = next;     tris[t + 2] = n + i;
            tris[t + 3] = next;     tris[t + 4] = n + next; tris[t + 5] = n + i;
        }

        var mesh = new Mesh();
        mesh.vertices  = verts;
        mesh.uv        = uvs;
        mesh.triangles = tris;
        mesh.RecalculateBounds();
        return mesh;
    }

    // ── Animation ────────────────────────────────────────────────────────────

    private static IEnumerator AnimateMeshFill(
        Mesh mesh, int[] fullTris, int segments, float delay, float drawDuration)
    {
        if (delay > 0f) yield return new WaitForSeconds(delay);

        float elapsed = 0f;
        while (elapsed < drawDuration)
        {
            elapsed += Time.deltaTime;
            int visible = Mathf.Clamp(
                Mathf.RoundToInt(Mathf.Clamp01(elapsed / drawDuration) * segments) * 6,
                0, fullTris.Length);
            mesh.SetTriangles(fullTris, 0, visible, 0);
            mesh.RecalculateBounds();
            yield return null;
        }

        mesh.SetTriangles(fullTris, 0);
        mesh.RecalculateBounds();
    }

    private IEnumerator AnimateSingleRing(
        LineRenderer lr, List<Vector2> pts,
        float delay, float drawDuration,
        Transform ringTransform, float rotDegPerSec,
        List<NoteAnimInfo> noteInfos, NoteVisualizer noteViz,
        System.Func<bool> shouldStop)
    {
        if (delay > 0f) yield return new WaitForSeconds(delay);

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
                if (noteViz?.noteMarkers != null && info.Track != null &&
                    noteViz.noteMarkers.TryGetValue((info.Track, info.AbsStep), out var markerTr) &&
                    markerTr != null)
                {
                    StartCoroutine(TravelNoteDot(
                        markerTr.position, ringTransform, info.TugLocalPos,
                        config.noteTravelDuration, info.DotColor));
                }
            }

            int count = Mathf.Clamp(Mathf.RoundToInt(progress * total), 2, total);
            lr.positionCount = count;
            for (int i = 0; i < count; i++)
                lr.SetPosition(i, new Vector3(pts[i].x, pts[i].y, 0f));
            yield return null;
        }

        lr.positionCount = total;
        for (int i = 0; i < total; i++)
            lr.SetPosition(i, new Vector3(pts[i].x, pts[i].y, 0f));

        while (!shouldStop())
        {
            if (ringTransform == null) yield break;
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
        lr.startColor = lr.endColor = color;
        lr.widthMultiplier   = config.lineWidth * 2f;
        lr.useWorldSpace     = false;
        lr.loop              = false;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows    = false;

        const int segs = 8;
        float dotR = config.noteDotRadius;
        lr.positionCount = segs + 1;
        for (int i = 0; i <= segs; i++)
        {
            float a = i / (float)segs * Mathf.PI * 2f;
            lr.SetPosition(i, new Vector3(Mathf.Cos(a) * dotR, Mathf.Sin(a) * dotR, 0f));
        }

        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            go.transform.position = Vector3.Lerp(
                startWorld, ringTransform.TransformPoint(tugLocalPos),
                Mathf.Clamp01(elapsed / duration));
            yield return null;
        }

        Destroy(go);
    }
}
