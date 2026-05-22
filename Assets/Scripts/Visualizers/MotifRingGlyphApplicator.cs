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

    [Tooltip("When true, rings rotate at speeds distributed evenly across [rotSpeedBase, rotSpeedMax] " +
             "by ring index rather than by fill duration. Use on library cards for a spherical look.")]
    public bool sphericalRotation = false;

    [Tooltip("NoteVisualizer whose noteMarkers provide travel-dot start positions for gameplay rings.")]
    [SerializeField] private NoteVisualizer noteVisualizer;

    [Tooltip("Prefab instantiated for each note travel dot. Must have a LineRenderer or SpriteRenderer to receive the note color.")]
    [SerializeField] private GameObject noteTravelDotPrefab;

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
    private int  _revealPendingCount;

    private struct NoteAnimInfo
    {
        public InstrumentTrack         Track;
        public int                     AbsStep;
        public float                   NoteAngle;
        public Vector3                 TugLocalPos;
        public Color                   DotColor;
        public MotifSnapshot.NoteEntry SourceNote; // for tween-in on the last ring
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────

    void Start() => GameFlowManager.Instance?.RegisterRingGlyphApplicator(this);
    void OnDestroy() => Clear();

    // ── Gameplay ring API ────────────────────────────────────────────────────

    /// <summary>
    /// Spawn one ring for a just-completed bin: a filled annulus with a
    /// note-tug contour at its outer rim. No travel dots for gameplay rings.
    /// </summary>
    public void SpawnBinRing(MusicalRole role, int binIndex, Color color,
                              List<MotifSnapshot.NoteEntry> notes, int totalSteps,
                              InstrumentTrack track = null)
    {
        if (config == null) return;

        int   idx    = _gameplayRings.Count;
        float innerR = RingInnerRadius(idx);
        float outerR = innerR + config.ringThickness;
        int   segs   = Mathf.Max(16, config.segments);

        // Start flat — dips tween in as each note's dot arrives.
        var entry = BuildRingEntry($"GameplayRing_Bin{binIndex}_{role}",
            innerR, outerR, segs, color, role, binIndex,
            new List<MotifSnapshot.NoteEntry>(), totalSteps);
        _gameplayRings.Add(entry);

        // Rotation speed = 360° per bin duration so one revolution = one loop pass.
        var   drum            = GameFlowManager.Instance?.activeDrumTrack;
        float stepDurationSec = 0f;
        drum?.TryGetNextBaseStepDsp(out _, out stepDurationSec);
        int   drumTotalSteps  = drum != null ? drum.totalSteps : totalSteps;
        float binDurationSec  = drumTotalSteps * stepDurationSec;
        float rotDeg          = binDurationSec > 0.01f ? 360f / binDurationSec : 120f;
        if (idx % 2 == 1) rotDeg = -rotDeg;

        StartCoroutine(AnimateMeshFill(
            entry.Fill.GetComponent<MeshFilter>().sharedMesh,
            entry.FullTris, segs, delay: 0f, config.ringAppearDuration));

        _revealPendingCount++;
        StartCoroutine(AnimateNoteReveal(
            entry, notes, totalSteps, outerR,
            role, binIndex, color, rotDeg, binDurationSec, track,
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

    /// <summary>
    /// Spin a single gameplay ring off the left edge of the play area with
    /// accelerating rotation. Called at the end of each ring's lifecycle.
    /// </summary>
    private IEnumerator SpinOffRing(RingEntry ring, float baseRotDegPerSec)
    {
        if (ring.Root == null) yield break;

        Vector3 startPos  = ring.Root.transform.position;
        float   holdScale = config != null ? config.ringHoldScale          : 0.25f;
        float   duration  = config != null ? config.spinOffDuration        : 0.45f;
        float   extraDeg  = config != null ? config.spinOffExtraDegPerSec  : 720f;

        float targetX = startPos.x - 20f;
        var   gfm     = GameFlowManager.Instance;
        if (gfm?.activeDrumTrack != null && gfm.activeDrumTrack.TryGetPlayAreaWorld(out var area))
        {
            float worldScale  = transform.lossyScale.x;
            float outerRWorld = worldScale > 0f
                ? worldScale * (RingInnerRadius(Mathf.Max(0, _gameplayRings.Count - 1)) + config.ringThickness)
                : config.ringThickness;
            targetX = area.left - outerRWorld * holdScale;
        }

        float spinDir   = baseRotDegPerSec >= 0f ? 1f : -1f;
        float spinSpeed = spinDir * (Mathf.Abs(baseRotDegPerSec) + extraDeg);

        float elapsed = 0f;
        while (elapsed < duration)
        {
            if (ring.Root == null) break;
            elapsed += Time.deltaTime;
            float t   = Mathf.Clamp01(elapsed / duration);
            var   pos = ring.Root.transform.position;
            pos.x = Mathf.Lerp(startPos.x, targetX, t);
            ring.Root.transform.position = pos;
            ring.Root.transform.Rotate(0f, 0f, spinSpeed * Time.deltaTime);
            yield return null;
        }

        _gameplayRings.Remove(ring);
        if (ring.Root != null) Destroy(ring.Root);
    }

    /// <summary>
    /// Translate rings from center to off the left edge of the play area over
    /// <c>config.rollOffDuration</c> seconds. Used by record rings if needed.
    /// </summary>
    public IEnumerator RollOffGameplayRings()
    {
        var snapshot = _gameplayRings.ToArray();
        if (snapshot.Length == 0) yield break;

        var startPositions = new Vector3[snapshot.Length];
        for (int i = 0; i < snapshot.Length; i++)
            startPositions[i] = snapshot[i].Root != null
                ? snapshot[i].Root.transform.position : transform.position;

        float holdScale = config != null ? config.ringHoldScale  : 0.25f;
        float duration  = config != null ? config.rollOffDuration : 1.5f;
        float targetX   = startPositions[0].x - 20f;
        var   gfm       = GameFlowManager.Instance;
        if (gfm?.activeDrumTrack != null && gfm.activeDrumTrack.TryGetPlayAreaWorld(out var area))
        {
            float worldScale  = transform.lossyScale.x;
            float outerRWorld = worldScale > 0f
                ? worldScale * (RingInnerRadius(snapshot.Length - 1) + config.ringThickness)
                : config.ringThickness;
            targetX = area.left - outerRWorld * holdScale;
        }

        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / duration);
            for (int i = 0; i < snapshot.Length; i++)
            {
                if (snapshot[i].Root == null) continue;
                var pos = snapshot[i].Root.transform.position;
                pos.x = Mathf.Lerp(startPositions[i].x, targetX, t);
                snapshot[i].Root.transform.position = pos;
            }
            yield return null;
        }

        DestroyList(_gameplayRings);
    }

    /// <summary>
    /// After <paramref name="delay"/> seconds, press-and-bounce the ring to ringHoldScale:
    /// fast compress to bouncePressScale, spring past ringHoldScale, settle.
    /// </summary>
    private IEnumerator BounceToHoldScale(Transform ringTransform, float delay)
    {
        if (delay > 0f) yield return new WaitForSeconds(delay);
        if (ringTransform == null || config == null) yield break;

        float holdScale   = config.ringHoldScale;
        float pressScale  = config.bouncePressScale;
        float pressDur    = config.bouncePressDuration;
        float settleDur   = config.bounceSettleDuration;
        // Overshoot slightly past holdScale on the spring-back.
        float overshoot   = holdScale + (holdScale - pressScale) * 0.4f;

        // Phase 1: press down.
        float elapsed = 0f;
        while (elapsed < pressDur)
        {
            if (ringTransform == null) yield break;
            elapsed += Time.deltaTime;
            float s = Mathf.Lerp(1f, pressScale, Mathf.Clamp01(elapsed / pressDur));
            ringTransform.localScale = new Vector3(s, s, 1f);
            yield return null;
        }

        // Phase 2: spring back past holdScale (60% of settle time).
        float springDur = settleDur * 0.6f;
        elapsed = 0f;
        while (elapsed < springDur)
        {
            if (ringTransform == null) yield break;
            elapsed += Time.deltaTime;
            float s = Mathf.Lerp(pressScale, overshoot, Mathf.Clamp01(elapsed / springDur));
            ringTransform.localScale = new Vector3(s, s, 1f);
            yield return null;
        }

        // Phase 3: dampen to holdScale (remaining 40%).
        float dampDur  = settleDur * 0.4f;
        float dampStart = ringTransform != null ? ringTransform.localScale.x : overshoot;
        elapsed = 0f;
        while (elapsed < dampDur)
        {
            if (ringTransform == null) yield break;
            elapsed += Time.deltaTime;
            float s = Mathf.Lerp(dampStart, holdScale, Mathf.Clamp01(elapsed / dampDur));
            ringTransform.localScale = new Vector3(s, s, 1f);
            yield return null;
        }

        if (ringTransform != null)
            ringTransform.localScale = new Vector3(holdScale, holdScale, 1f);
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

        // Fallback for legacy snapshots saved before TrackBins serialization was added.
        // Derive ring keys directly from CollectedNotes grouped by (binIndex, trackColor).
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
                    Track       = track,
                    AbsStep     = n.Step,
                    NoteAngle   = angle,
                    TugLocalPos = new Vector3(Mathf.Cos(angle) * tugR, Mathf.Sin(angle) * tugR, 0f),
                    DotColor    = color,
                    SourceNote  = n,
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
                    shouldStop: () => _recordFadingOut));
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
                    shouldStop: () => _recordFadingOut));
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
        _revealPendingCount = 0;
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

    // Plays the last record ring the same way a gameplay ring animates:
    // starts flat, fires step-synced travel dots before each note's beat,
    // and simultaneously tweens the matching dip into the contour.
    private IEnumerator AnimateLastRecordRing(
        LineRenderer lr, List<Vector2> flatPts,
        float delay,
        Transform ringTransform, float rotDegPerSec,
        List<NoteAnimInfo> noteInfos, NoteVisualizer noteViz,
        MusicalRole role, int binIndex, Color color, int binSteps, float outerR,
        System.Func<bool> shouldStop)
    {
        if (delay > 0f) yield return new WaitForSeconds(delay);
        if (shouldStop()) yield break;

        // Show the full flat circle immediately — dips tween in as each dot travels.
        ApplyContourPoints(lr, flatPts);

        var drumRef     = GameFlowManager.Instance?.activeDrumTrack;
        int leaderSteps = drumRef != null ? drumRef.GetLeaderSteps() : binSteps;

        float stepDurRec = 0f;
        drumRef?.TryGetNextBaseStepDsp(out _, out stepDurRec);
        int leadSteps = stepDurRec > 0.001f
            ? Mathf.Max(1, Mathf.RoundToInt(config.noteTravelDuration / stepDurRec))
            : (config != null ? config.noteLaunchLeadSteps : 2);

        var      revealedNotes = new List<MotifSnapshot.NoteEntry>();
        Coroutine contourTween = null;

        foreach (var info in noteInfos)
        {
            if (shouldStop()) break;

            int noteLeaderStep = info.AbsStep % leaderSteps;
            int triggerStep    = (noteLeaderStep - leadSteps + leaderSteps) % leaderSteps;

            Transform markerTr = null;
            if (noteViz?.noteMarkers != null && info.Track != null)
                noteViz.noteMarkers.TryGetValue((info.Track, info.AbsStep), out markerTr);

            var capturedInfo = info;
            StartCoroutine(WaitAndLaunchDot(
                ringTransform, info.TugLocalPos, info.DotColor,
                markerTr, outerR, info.NoteAngle,
                drumRef, triggerStep, config.noteTravelDuration, shouldStop,
                onLaunch: () =>
                {
                    if (capturedInfo.SourceNote == null) return;
                    revealedNotes.Add(capturedInfo.SourceNote);
                    var targetPoly = MotifRingGlyphGenerator.GenerateSingleRingAtRadius(
                        role, binIndex, color, revealedNotes, binSteps, outerR, config);
                    var targetPts = targetPoly?.Points;
                    if (targetPts != null)
                    {
                        if (contourTween != null) StopCoroutine(contourTween);
                        contourTween = StartCoroutine(
                            TweenContour(lr, targetPts, config.noteTravelDuration));
                    }
                }));
        }

        while (!shouldStop())
        {
            if (ringTransform == null) yield break;
            ringTransform.Rotate(0f, 0f, rotDegPerSec * Time.deltaTime);
            yield return null;
        }
    }

    private IEnumerator AnimateNoteReveal(
        RingEntry ring,
        List<MotifSnapshot.NoteEntry> notes,
        int totalSteps, float outerR,
        MusicalRole role, int binIndex, Color color,
        float rotDegPerSec, float binDurationSec, InstrumentTrack track,
        System.Func<bool> shouldStop)
    {
        int binSteps = Mathf.Max(1, totalSteps);

        // ── Phase 1: Quick appear — dips already baked into ContourPoints ────────
        var currentPts = ring.ContourPoints;
        int totalPts   = currentPts?.Count ?? 0;
        float appearDur = config != null ? config.ringAppearDuration : 0.1f;

        ring.Contour.positionCount = 2;
        float elapsed = 0f;
        while (elapsed < appearDur && totalPts > 0)
        {
            elapsed += Time.deltaTime;
            float progress = Mathf.Clamp01(elapsed / appearDur);
            int   count    = Mathf.Clamp(Mathf.RoundToInt(progress * totalPts), 2, totalPts);
            ring.Contour.positionCount = count;
            for (int i = 0; i < count; i++)
                ring.Contour.SetPosition(i, new Vector3(currentPts[i].x, currentPts[i].y, 0f));
            yield return null;
        }
        if (currentPts != null && totalPts > 0)
            ApplyContourPoints(ring.Contour, currentPts);

        if (shouldStop() || ring.Root == null)
        {
            _revealPendingCount = Mathf.Max(0, _revealPendingCount - 1);
            yield break;
        }

        // ── Phase 2: Rotate at bin tempo; launch dots before each note's beat ────
        var drumRef     = GameFlowManager.Instance?.activeDrumTrack;
        int leaderSteps = drumRef != null ? drumRef.GetLeaderSteps() : binSteps;

        // Derive lead steps from travel duration so the dot arrives exactly on the beat.
        float stepDur = 0f;
        drumRef?.TryGetNextBaseStepDsp(out _, out stepDur);
        int leadSteps = stepDur > 0.001f
            ? Mathf.Max(1, Mathf.RoundToInt(config.noteTravelDuration / stepDur))
            : (config != null ? config.noteLaunchLeadSteps : 2);

        float tugR = outerR * (1f - config.tugDepthFraction);

        var sortedNotes = notes == null
            ? new List<MotifSnapshot.NoteEntry>()
            : notes.OrderBy(n => n.Step % leaderSteps).ToList();

        // Shared tween state — updated by each note's onLaunch callback.
        var      revealedNotes = new List<MotifSnapshot.NoteEntry>();
        Coroutine contourTween = null;

        foreach (var note in sortedNotes)
        {
            if (shouldStop() || ring.Root == null) break;

            int   noteLeaderStep = note.Step % leaderSteps;
            int   triggerStep    = (noteLeaderStep - leadSteps + leaderSteps) % leaderSteps;
            int   localStep      = note.Step % binSteps;
            float angle          = localStep / (float)binSteps * Mathf.PI * 2f;
            var   tugLocal       = new Vector3(Mathf.Cos(angle) * tugR, Mathf.Sin(angle) * tugR, 0f);

            Transform markerTr = null;
            if (noteVisualizer?.noteMarkers != null && track != null)
                noteVisualizer.noteMarkers.TryGetValue((track, note.Step), out markerTr);

            // Capture loop variables for the closure.
            var capturedNote = note;
            StartCoroutine(WaitAndLaunchDot(
                ring.Root.transform, tugLocal, note.TrackColor,
                markerTr, outerR, angle,
                drumRef, triggerStep, config.noteTravelDuration, shouldStop,
                onLaunch: () =>
                {
                    // Add this note's dip and tween the ring contour over the same
                    // duration as the dot travel — they deform and arrive together.
                    revealedNotes.Add(capturedNote);
                    var targetPoly = MotifRingGlyphGenerator.GenerateSingleRingAtRadius(
                        role, binIndex, color, revealedNotes, binSteps, outerR, config);
                    var targetPts = targetPoly?.Points;
                    if (targetPts != null)
                    {
                        if (contourTween != null) StopCoroutine(contourTween);
                        contourTween = StartCoroutine(
                            TweenContour(ring.Contour, targetPts, config.noteTravelDuration));
                    }
                }));
        }

        // Rotate for one bin duration + one dot-travel window.
        // The extra window ensures dots whose trigger step fires near the end of
        // binDurationSec still complete their travel before the ring presses.
        float binElapsed      = 0f;
        float travelBuffer    = config != null ? config.noteTravelDuration : 0.35f;
        float effectiveBinDur = (binDurationSec > 0.01f ? binDurationSec : 2f) + travelBuffer;
        while (binElapsed < effectiveBinDur && !shouldStop())
        {
            if (ring.Root == null)
            {
                _revealPendingCount = Mathf.Max(0, _revealPendingCount - 1);
                yield break;
            }
            ring.Root.transform.Rotate(0f, 0f, rotDegPerSec * Time.deltaTime);
            binElapsed += Time.deltaTime;
            yield return null;
        }

        // ── Phase 3: Press ────────────────────────────────────────────────────────
        if (!shouldStop() && ring.Root != null)
            yield return BounceToHoldScale(ring.Root.transform, delay: 0f);

        _revealPendingCount = Mathf.Max(0, _revealPendingCount - 1);

        // ── Phase 4: Spin off ─────────────────────────────────────────────────────
        if (!shouldStop())
            yield return SpinOffRing(ring, rotDegPerSec);
    }

    private IEnumerator WaitAndLaunchDot(
        Transform ringTransform, Vector3 tugLocal, Color dotColor,
        Transform markerTr, float outerR, float angle,
        DrumTrack drumRef, int triggerStep,
        float travelDuration, System.Func<bool> shouldStop,
        System.Action onLaunch = null)
    {
        if (drumRef != null && drumRef.currentStep != triggerStep)
        {
            bool stepFired = false;
            System.Action<int, int> onStep = (s, _) => { if (s == triggerStep) stepFired = true; };
            drumRef.OnStepChanged += onStep;
            yield return new WaitUntil(() => stepFired || shouldStop());
            drumRef.OnStepChanged -= onStep;
        }

        if (shouldStop() || ringTransform == null) yield break;

        if (config?.launchSfx != null)
        {
            var cam = Camera.main;
            AudioSource.PlayClipAtPoint(
                config.launchSfx,
                cam != null ? cam.transform.position : Vector3.zero,
                config.launchSfxVolume);
        }

        Vector3 dotWorld = markerTr != null
            ? markerTr.position
            : ringTransform.TransformPoint(
                  new Vector3(Mathf.Cos(angle) * outerR * 1.5f, Mathf.Sin(angle) * outerR * 1.5f, 0f));

        // Tween the ring dip in over the same duration as the dot travel.
        onLaunch?.Invoke();

        StartCoroutine(TravelNoteDot(dotWorld, ringTransform, tugLocal, travelDuration, dotColor));
    }

    private IEnumerator TweenContour(LineRenderer lr, List<Vector2> to, float duration)
    {
        if (lr == null || to == null || lr.positionCount != to.Count) yield break;
        var from = new List<Vector2>(lr.positionCount);
        for (int i = 0; i < lr.positionCount; i++)
        {
            var p = lr.GetPosition(i);
            from.Add(new Vector2(p.x, p.y));
        }
        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / duration);
            for (int i = 0; i < from.Count; i++)
            {
                var pt = Vector2.Lerp(from[i], to[i], t);
                lr.SetPosition(i, new Vector3(pt.x, pt.y, 0f));
            }
            yield return null;
        }
        ApplyContourPoints(lr, to);
    }

    private static void ApplyContourPoints(LineRenderer lr, List<Vector2> pts)
    {
        if (lr == null || pts == null) return;
        lr.positionCount = pts.Count;
        for (int i = 0; i < pts.Count; i++)
            lr.SetPosition(i, new Vector3(pts[i].x, pts[i].y, 0f));
    }

    private IEnumerator TravelNoteDot(
        Vector3 startWorld, Transform ringTransform, Vector3 tugLocalPos,
        float duration, Color color)
    {
        GameObject go;
        if (noteTravelDotPrefab != null)
        {
            go = Instantiate(noteTravelDotPrefab, startWorld, Quaternion.identity, transform);
            go.transform.localScale = Vector3.one * (config.noteDotRadius * 2f);
            var lr2 = go.GetComponentInChildren<LineRenderer>();
            if (lr2 != null) { lr2.startColor = lr2.endColor = color; }
            else
            {
                var sr = go.GetComponentInChildren<SpriteRenderer>();
                if (sr != null) sr.color = color;
            }
        }
        else
        {
            go = new GameObject("NoteTravelDot");
            go.transform.SetParent(transform, worldPositionStays: true);
            go.transform.position = startWorld;

            var lr = go.AddComponent<LineRenderer>();
            if (lineMaterial != null) lr.material = lineMaterial;
            lr.startColor        = lr.endColor = color;
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
