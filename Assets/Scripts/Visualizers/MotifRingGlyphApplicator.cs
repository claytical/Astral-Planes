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

    [Tooltip("When GameFlowManager is unavailable (carousel, solar system), cap the display so no ring " +
             "exceeds this world-space radius. 0 = no cap.")]
    public float maxDisplayRadius = 0f;

    [Tooltip("NoteVisualizer whose noteMarkers provide travel-dot start positions for gameplay rings.")]
    [SerializeField] private NoteVisualizer noteVisualizer;

    [Tooltip("Prefab instantiated for each note travel dot. Must have a LineRenderer or SpriteRenderer to receive the note color.")]
    [SerializeField] private GameObject noteTravelDotPrefab;

    private static readonly int BasePropId  = Shader.PropertyToID("_BaseColor");
    private static readonly int ColorPropId = Shader.PropertyToID("_Color");

    // Dot-launch lead when the drum track can't report a step duration.
    private const int FallbackLeadSteps = 2;

    private struct RingEntry
    {
        public GameObject    Root;
        public MeshRenderer  Fill;
        public LineRenderer  Contour;
        public Color         BaseColor;
        public int[]         FullTris;
        public List<Vector2> ContourPoints;
        public int           BinIndex;
        public MusicalRole   Role;
    }

    private readonly List<RingEntry> _gameplayRings  = new();
    private readonly List<RingEntry> _recordRings    = new();
    private readonly List<RingEntry> _remainingRings = new(); // gray placeholders for motif progress not yet completed

    private bool    _recordFadingOut;
    private bool    _gameplayFadingOut;    // stops rotation coroutines; set when spin animation begins
    private bool    _clearingGameplayRings; // stops deformation coroutines; set only in ClearGameplayRings
    private bool    _superNodeMode;
    private bool    _spinOffPending;       // spin is imminent; prevents per-deformation hide during wait
    private Vector3 _fitScale;
    private int     _pendingDeformationCount;

    private struct NoteAnimInfo
    {
        public InstrumentTrack         Track;
        public int                     AbsStep;
        public float                   NoteAngle;
        public Vector3                 RingLocalPos;
        public Vector3                 TugLocalPos;
        public Color                   DotColor;
        public MotifSnapshot.NoteEntry SourceNote; // for tween-in on the last ring
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────

    void Start() => GameFlowManager.Instance?.RegisterRingGlyphApplicator(this);
    void OnDestroy() => Clear();

    // ── Gameplay ring API ────────────────────────────────────────────────────

    /// <summary>
    /// Spawn one flat circle ring for a just-completed bin. Deformation fires
    /// separately via <see cref="BeginBinRingDeformation"/> when the playhead
    /// reaches the bin's start step.
    /// </summary>
    // Called by SuperNodeTrackNode.Collect() before InstantFillAllBins fires OnBinFilled.
    // Ring spawning for the new bins happens via the OnBinFilled event path — not via any
    // explicit SpawnBinRing loop here.
    public void SetSuperNodeMode(bool active) => _superNodeMode = active;

    public void SpawnBinRing(MusicalRole role, int binIndex, Color color,
                              List<MotifSnapshot.NoteEntry> notes, int totalSteps,
                              InstrumentTrack track = null)
    {
        if (config == null) return;

        int   idx    = _gameplayRings.Count;
        float innerR = RingInnerRadius(idx);
        float outerR = innerR + config.ringThickness;
        int   segs   = Mathf.Max(16, config.segments);

        var entry = BuildRingEntry($"GameplayRing_Bin{binIndex}_{role}",
            innerR, outerR, segs, color, role, binIndex,
            new List<MotifSnapshot.NoteEntry>(), totalSteps);
        _gameplayRings.Add(entry);

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

        // Draw in the flat circle contour, then rotate persistently.
        StartCoroutine(AnimateSingleRing(
            entry.Contour, entry.ContourPoints,
            0f, config.ringAppearDuration,
            entry.Root.transform, rotDeg,
            new List<NoteAnimInfo>(), null,
            shouldStop: () => _gameplayFadingOut));

        // Reset deformation count when the record was hidden — starts a fresh session.
        if (transform.localScale.sqrMagnitude < 0.0001f)
            _pendingDeformationCount = 0;

        RefreshPlayAreaFit(_gameplayRings.Count + _remainingRings.Count);
    }

    /// <summary>
    /// Fire the drum-step-synced deformation sequence for the ring that was
    /// spawned for <paramref name="binIndex"/>. Called by BinRingController
    /// when the playhead reaches the bin's start step.
    /// </summary>
    public void BeginBinRingDeformation(int binIndex, List<MotifSnapshot.NoteEntry> notes,
        int totalSteps, InstrumentTrack track, MusicalRole role, Color color)
    {
        // Match on both binIndex AND role — multiple roles can share the same binIndex.
        int ringIdx = _gameplayRings.FindIndex(r => r.BinIndex == binIndex && r.Role == role);
        if (ringIdx < 0 || config == null) return;
        float innerR = RingInnerRadius(ringIdx);
        float outerR = innerR + config.ringThickness;
        _pendingDeformationCount++;
        StartCoroutine(DeformBinRingCoroutine(
            _gameplayRings[ringIdx], notes, totalSteps, track, role, color, outerR));
    }

    private IEnumerator DeformBinRingCoroutine(
        RingEntry ring, List<MotifSnapshot.NoteEntry> notes,
        int totalSteps, InstrumentTrack track, MusicalRole role, Color color, float outerR)
    {
        if (ring.Root == null) { _pendingDeformationCount--; yield break; }

        int   binSteps    = Mathf.Max(1, totalSteps);
        var   drumRef     = GameFlowManager.Instance?.activeDrumTrack;
        int   leaderSteps = drumRef != null ? drumRef.GetLeaderSteps() : binSteps;

        float stepDur = 0f;
        drumRef?.TryGetNextBaseStepDsp(out _, out stepDur);
        int leadSteps = stepDur > 0.001f
            ? Mathf.Max(1, Mathf.RoundToInt(config.noteTravelDuration / stepDur))
            : FallbackLeadSteps;

        float tugR = outerR * (1f - config.tugDepthFraction);

        var sortedNotes = notes == null
            ? new List<MotifSnapshot.NoteEntry>()
            : notes.OrderBy(n => n.Step % leaderSteps).ToList();

        var      revealedNotes = new List<MotifSnapshot.NoteEntry>();
        Coroutine contourTween = null;
        bool      contourSettled = sortedNotes.Count == 0;

        // shouldStop: only hard-clear kills deformation; spin-off lets it finish naturally.
        System.Func<bool> shouldStop = () => _clearingGameplayRings || ring.Root == null;

        foreach (var note in sortedNotes)
        {
            if (shouldStop()) break;

            int   noteLeaderStep = note.Step % leaderSteps;
            int   triggerStep    = (noteLeaderStep - leadSteps + leaderSteps) % leaderSteps;
            int   localStep      = note.Step % binSteps;
            float angle          = localStep / (float)binSteps * Mathf.PI * 2f;
            var   tugLocal       = new Vector3(Mathf.Cos(angle) * tugR, Mathf.Sin(angle) * tugR, 0f);

            Transform markerTr = null;
            if (noteVisualizer?.noteMarkers != null && track != null)
                noteVisualizer.noteMarkers.TryGetValue((track, note.Step), out markerTr);

            var capturedNote = note;
            StartCoroutine(WaitAndLaunchDot(
                ring.Root.transform, tugLocal, note.TrackColor,
                markerTr, outerR, angle,
                drumRef, triggerStep, config.noteTravelDuration, shouldStop,
                onLaunch: () =>
                {
                    revealedNotes.Add(capturedNote);
                    var targetPoly = MotifRingGlyphGenerator.GenerateSingleRingAtRadius(
                        role, ring.BinIndex, color, revealedNotes, binSteps, outerR, config);
                    var targetPts = targetPoly?.Points;
                    if (targetPts != null)
                    {
                        contourSettled = false;
                        if (contourTween != null) StopCoroutine(contourTween);
                        contourTween = StartCoroutine(
                            TweenContour(ring.Contour, targetPts, config.noteTravelDuration,
                                onComplete: () => contourSettled = true));
                    }
                }));
        }

        while (!contourSettled || revealedNotes.Count < sortedNotes.Count)
        {
            if (shouldStop()) { _pendingDeformationCount--; yield break; }
            yield return null;
        }

        _pendingDeformationCount--;

        // Hide record after deformation — only when all concurrent deformations are done
        // and we are not in spin-off or SuperNode mode.
        if (!_superNodeMode && !_gameplayFadingOut && !_clearingGameplayRings && !_spinOffPending
            && _pendingDeformationCount == 0)
        {
            float dur        = config != null ? config.scaleDownDuration : 0.5f;
            Vector3 startScale = transform.localScale;
            float elapsed    = 0f;
            while (elapsed < dur && !_clearingGameplayRings && !_gameplayFadingOut)
            {
                elapsed += Time.deltaTime;
                transform.localScale = Vector3.Lerp(startScale, Vector3.zero, elapsed / dur);
                yield return null;
            }
            if (!_clearingGameplayRings && !_gameplayFadingOut)
                transform.localScale = Vector3.zero;
        }
    }

    /// <summary>
    /// Show gray/diminished placeholder rings for motif progress not yet completed.
    /// Rebuilds the placeholder stack each call (called once per Collectable set
    /// finishing its landing on the timeline), stacked just outside the current
    /// completed (_gameplayRings) count.
    /// </summary>
    public void SetRemainingProgressRings(int remainingCount)
    {
        if (config == null) return;

        DestroyList(_remainingRings);
        _remainingRings.Clear();

        remainingCount = Mathf.Max(0, remainingCount);
        int segs = Mathf.Max(16, config.segments);

        for (int i = 0; i < remainingCount; i++)
        {
            int   idx    = _gameplayRings.Count + i;
            float innerR = RingInnerRadius(idx);
            float outerR = innerR + config.ringThickness;

            var entry = BuildRingEntry($"RemainingRing_{i}", innerR, outerR, segs,
                config.remainingRingColor, default, -1,
                new List<MotifSnapshot.NoteEntry>(), 0,
                config.remainingRingAlpha, config.remainingContourAlpha);
            _remainingRings.Add(entry);

            StartCoroutine(AnimateMeshFill(
                entry.Fill.GetComponent<MeshFilter>().sharedMesh,
                entry.FullTris, segs, delay: i * config.ringStaggerDelay, config.ringAppearDuration));

            StartCoroutine(AnimateSingleRing(
                entry.Contour, entry.ContourPoints,
                i * config.ringStaggerDelay, config.ringAppearDuration,
                entry.Root.transform, 0f,
                new List<NoteAnimInfo>(), null,
                shouldStop: () => _gameplayFadingOut));
        }

        RefreshPlayAreaFit(_gameplayRings.Count + _remainingRings.Count);
    }

    /// <summary>Destroy all gameplay rings immediately and hide the record.</summary>
    public void ClearGameplayRings()
    {
        StopAllCoroutines(); // prevent ghost decrements from old deformation coroutines
        _clearingGameplayRings   = true;
        _gameplayFadingOut       = true;
        _superNodeMode           = false;
        _spinOffPending          = false;
        _pendingDeformationCount = 0;
        DestroyList(_gameplayRings);
        DestroyList(_remainingRings);
        _remainingRings.Clear();
        _gameplayFadingOut       = false;
        _clearingGameplayRings   = false;
        transform.localScale     = Vector3.zero;
    }

    // ── Record ring API ──────────────────────────────────────────────────────

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

    /// <summary>
    /// Spin the whole record 360° clockwise over <paramref name="spinDuration"/>, then
    /// slide it off the left edge over <paramref name="rollDuration"/>.
    /// </summary>
    public IEnumerator SpinAndRollOffRecordRings(float spinDuration, float rollDuration)
    {
        _recordFadingOut = true;  // stop per-ring rotation coroutines

        float tiltDeg     = config != null ? config.tiltXDegrees  : 75f;
        float scaleDur    = config != null ? config.scaleDownDuration : 0.5f;
        float speedBase   = config != null ? config.rotSpeedMax   : 300f;
        Vector3 originalScale = transform.localScale;

        // Capture each ring's current Z rotation and assign staggered exit speeds.
        // Alternating direction + index spread creates the "deformed but in-sync" wobble.
        var rings      = _recordRings.ToArray();
        var ringZRots  = new float[rings.Length];
        var ringSpeeds = new float[rings.Length];
        for (int i = 0; i < rings.Length; i++)
        {
            ringZRots[i]  = rings[i].Root != null ? rings[i].Root.transform.localEulerAngles.z : 0f;
            float spd     = speedBase * (1f + i * 0.2f);
            ringSpeeds[i] = i % 2 == 0 ? spd : -spd;
        }

        // Phase 1: spin parent 360° clockwise over spinDuration; rings spin independently
        float elapsed = 0f;
        while (elapsed < spinDuration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / spinDuration);
            transform.rotation = Quaternion.Euler(0f, 0f, -360f * t);
            AdvanceRingZRots(rings, ringZRots, ringSpeeds);
            yield return null;
        }
        transform.rotation = Quaternion.identity;

        // Phase 2: tilt X axis to tiltDeg over rollDuration; rings keep spinning
        elapsed = 0f;
        while (elapsed < rollDuration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / rollDuration);
            transform.localEulerAngles = new Vector3(Mathf.Lerp(0f, tiltDeg, t), 0f, 0f);
            AdvanceRingZRots(rings, ringZRots, ringSpeeds);
            yield return null;
        }

        // Phase 3: scale to zero; rings keep spinning
        elapsed = 0f;
        while (elapsed < scaleDur)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / scaleDur);
            transform.localScale = Vector3.Lerp(originalScale, Vector3.zero, t);
            AdvanceRingZRots(rings, ringZRots, ringSpeeds);
            yield return null;
        }

        DestroyList(_recordRings);
        _recordFadingOut = false;
        transform.localScale = originalScale;
        transform.rotation = Quaternion.identity;
    }

    /// <summary>
    /// Spin-and-roll-off the accumulated gameplay rings (used at bridge time).
    /// Restores visibility first if the record was hidden after the last deformation.
    /// </summary>
    public IEnumerator SpinAndRollOffActiveRings(float spinDuration, float rollDuration)
    {
        // Prevent per-deformation scale-to-zero while waiting, but keep rings rotating so
        // the record stays animated during the wait (rings look live, not frozen).
        _spinOffPending = true;

        // Wait for any in-flight deformations so the final ring fully deforms before spin-off.
        if (_pendingDeformationCount > 0)
        {
            float maxWait = spinDuration + rollDuration;
            float waited  = 0f;
            while (_pendingDeformationCount > 0 && waited < maxWait && !_clearingGameplayRings)
            {
                waited += Time.deltaTime;
                yield return null;
            }
        }

        // Deformations settled (or timed out): now stop rotation and begin spin animation.
        _gameplayFadingOut = true;
        _spinOffPending    = false;

        // Restore to full scale — handles both "hidden after deformation" and "mid-fade interrupted".
        transform.localScale = _fitScale.sqrMagnitude > 0.0001f ? _fitScale : Vector3.one;

        float   tiltDeg       = config != null ? config.tiltXDegrees     : 75f;
        float   scaleDur      = config != null ? config.scaleDownDuration : 0.5f;
        float   speedBase     = config != null ? config.rotSpeedMax       : 300f;
        Vector3 originalScale = transform.localScale;

        var rings      = _gameplayRings.Concat(_remainingRings).ToArray();
        var ringZRots  = new float[rings.Length];
        var ringSpeeds = new float[rings.Length];
        for (int i = 0; i < rings.Length; i++)
        {
            ringZRots[i]  = rings[i].Root != null ? rings[i].Root.transform.localEulerAngles.z : 0f;
            float spd     = speedBase * (1f + i * 0.2f);
            ringSpeeds[i] = i % 2 == 0 ? spd : -spd;
        }

        float elapsed = 0f;
        while (elapsed < spinDuration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / spinDuration);
            transform.rotation = Quaternion.Euler(0f, 0f, -360f * t);
            AdvanceRingZRots(rings, ringZRots, ringSpeeds);
            yield return null;
        }
        transform.rotation = Quaternion.identity;

        elapsed = 0f;
        while (elapsed < rollDuration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / rollDuration);
            transform.localEulerAngles = new Vector3(Mathf.Lerp(0f, tiltDeg, t), 0f, 0f);
            AdvanceRingZRots(rings, ringZRots, ringSpeeds);
            yield return null;
        }

        elapsed = 0f;
        while (elapsed < scaleDur)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / scaleDur);
            transform.localScale = Vector3.Lerp(originalScale, Vector3.zero, t);
            AdvanceRingZRots(rings, ringZRots, ringSpeeds);
            yield return null;
        }

        DestroyList(_gameplayRings);
        DestroyList(_remainingRings);
        _remainingRings.Clear();
        _gameplayFadingOut       = false;
        _spinOffPending          = false;
        _superNodeMode           = false;
        _pendingDeformationCount = 0;
        transform.localScale     = Vector3.zero;
        transform.rotation       = Quaternion.identity;
    }

    private void AdvanceRingZRots(RingEntry[] rings, float[] zRots, float[] speeds)
    {
        for (int i = 0; i < rings.Length; i++)
        {
            if (rings[i].Root == null) continue;
            zRots[i] += speeds[i] * Time.deltaTime;
            rings[i].Root.transform.localEulerAngles = new Vector3(0f, 0f, zRots[i]);
        }
    }

    /// <summary>Destroy all rings (both layers).</summary>
    public void Clear()
    {
        foreach (Transform child in transform) Destroy(child.gameObject);
        _recordRings.Clear();
        _gameplayRings.Clear();
        _remainingRings.Clear();
    }

    public void FitToPlayArea(float width, float height, float cx, float cy)
    {
        float s = Mathf.Min(width, height);
        transform.position   = new Vector3(cx, cy, transform.position.z);
        transform.localScale = new Vector3(s, s, 1f);
        _fitScale = transform.localScale;
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private float RingInnerRadius(int idx) =>
        config.innerRadius + idx * (config.ringThickness + config.ringSpacing);

    private void RefreshPlayAreaFit(int ringCount)
    {
        if (config == null || ringCount == 0) return;

        var gfm = GameFlowManager.Instance;
        if (gfm?.activeDrumTrack != null && gfm.activeDrumTrack.TryGetPlayAreaWorld(out var area))
        {
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
            _fitScale            = transform.localScale;
            return;
        }

        if (maxDisplayRadius > 0f)
        {
            float outerRadius = RingInnerRadius(ringCount - 1) + config.ringThickness;
            if (outerRadius <= 0f) return;
            float scale      = Mathf.Min(1f, maxDisplayRadius / outerRadius);
            transform.localScale = Vector3.one * scale;
            _fitScale            = transform.localScale;
        }
    }

    private RingEntry BuildRingEntry(
        string goName, float innerR, float outerR, int segs,
        Color color, MusicalRole role, int binIndex,
        List<MotifSnapshot.NoteEntry> notes, int totalSteps,
        float? fillAlphaOverride = null, float? contourAlphaOverride = null)
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
        SetFillColor(mr, new Color(color.r, color.g, color.b, fillAlphaOverride ?? config.ringAlpha), mpb);

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
        var contourColor     = new Color(color.r, color.g, color.b, contourAlphaOverride ?? config.contourAlpha);
        lr.startColor        = contourColor;
        lr.endColor          = contourColor;
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
            BinIndex      = binIndex,
            Role          = role,
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
                var lc = new Color(c.r, c.g, c.b, config.contourAlpha * normalizedAlpha);
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
        System.Func<bool> shouldStop,
        bool spherical = false)
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
                        markerTr.position, ringTransform,
                        info.RingLocalPos, info.TugLocalPos,
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
            if (spherical)
                ringTransform.Rotate(0f, rotDegPerSec * Time.deltaTime, 0f);
            else
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
        System.Func<bool> shouldStop,
        bool spherical = false)
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
            : FallbackLeadSteps;

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
            if (spherical)
                ringTransform.Rotate(0f, rotDegPerSec * Time.deltaTime, 0f);
            else
                ringTransform.Rotate(0f, 0f, rotDegPerSec * Time.deltaTime);
            yield return null;
        }
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

        // Ring surface position in local ring space — dot arrives here first, then pushes inward.
        var ringLocalPos = new Vector3(Mathf.Cos(angle) * outerR, Mathf.Sin(angle) * outerR, 0f);

        // onLaunch fires at impact (when the dot reaches the ring surface), not at departure.
        StartCoroutine(TravelNoteDot(dotWorld, ringTransform, ringLocalPos, tugLocal, travelDuration, dotColor, onLaunch));
    }

    private IEnumerator TweenContour(LineRenderer lr, List<Vector2> to, float duration,
        System.Action onComplete = null)
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
        onComplete?.Invoke();
    }

    private static void ApplyContourPoints(LineRenderer lr, List<Vector2> pts)
    {
        if (lr == null || pts == null) return;
        lr.positionCount = pts.Count;
        for (int i = 0; i < pts.Count; i++)
            lr.SetPosition(i, new Vector3(pts[i].x, pts[i].y, 0f));
    }

    private IEnumerator TravelNoteDot(
        Vector3 startWorld, Transform ringTransform,
        Vector3 ringLocalPos, Vector3 tugLocalPos,
        float duration, Color color,
        System.Action onImpact = null)
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

        // Phase 1 — approach: dot travels from the note marker to the ring surface.
        float elapsed = 0f;
        while (elapsed < duration)
        {
            if (ringTransform == null) { Destroy(go); yield break; }
            elapsed += Time.deltaTime;
            go.transform.position = Vector3.Lerp(
                startWorld,
                ringTransform.TransformPoint(ringLocalPos),
                Mathf.Clamp01(elapsed / duration));
            yield return null;
        }

        if (ringTransform == null) { Destroy(go); yield break; }

        // Impact: dot has reached the ring surface — start the note and contour deformation.
        if (config?.impactSfx != null)
        {
            var cam = Camera.main;
            AudioSource.PlayClipAtPoint(
                config.impactSfx,
                cam != null ? cam.transform.position : Vector3.zero,
                config.impactSfxVolume);
        }
        onImpact?.Invoke();

        // Phase 2 — push: dot moves inward from ring surface to tug point as the ring curves.
        elapsed = 0f;
        while (elapsed < duration)
        {
            if (ringTransform == null) { Destroy(go); yield break; }
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / duration);
            go.transform.position = ringTransform.TransformPoint(
                Vector3.Lerp(ringLocalPos, tugLocalPos, t));
            yield return null;
        }

        Destroy(go);
    }
}
