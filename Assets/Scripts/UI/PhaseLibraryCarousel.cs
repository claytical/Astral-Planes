using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

/// <summary>
/// Selector for saved motif rings in the Main scene, laid out as stacked orbits (see MotifMazeGraph
/// for the traversal rules).
///
/// Phase 0 is the tutorial: a single glyph with no orbit that leaves the carousel once completed.
/// Every later phase is a circular orbit of any number of motifs. Left / Right glide the whole ring
/// to the next open motif (the selected glyph travels off along the orbit, the next one arrives at the
/// front, wrapping; motifs not yet unlocked are drawn as locked previews and stepped over); Up / Down shift to the next / previous phase's orbit, which glides into the centre with
/// the motif at the same position round its ring selected. The carousel opens on the most recently
/// recorded motif, or phase 1 motif 0 if none.
///
/// Display: the orbit is an ellipse seen from above. The selected motif sits flat at the centre of a
/// 3x3 screen grid and the ring curves up on both sides and round the back, partly hidden behind it.
/// Every motif of the phase is drawn on the orbit; a non-selected glyph differs from the selected one
/// only by alpha (unselectedAlpha) and an X-axis tip (neighborTiltDegrees). All glyphs share one size (SetFixedFit), whatever their ring count. The
/// next phase out waits one grid cell above as a dim ellipse with its front glyph; the phase below is
/// not drawn, so Up reads as ascending.
/// MotifOrbitLineRenderer draws the ellipses, leaving gaps behind each glyph.
///
/// A visible motif is either played (has a saved MotifSnapshot — audible, full ring detail) or
/// unplayed and rendered as a locked preview (gray/unlit rings, count = nodesPerStar) via
/// ApplyLockedPreview. Locked entries are selectable — confirming one jumps straight into that
/// motif — but never previewed with audio.
///
/// No entries at all (nothing played yet):
///   - All glyphs hidden; pressToStartCue shown.
///   - Any confirm input loads trackSelectionScene directly.
///
/// Input: direct polling of Gamepad.current / Keyboard.current.
/// No PlayerInput required — compatible with Main scene's any-device model.
/// </summary>
public class PhaseLibraryCarousel : MonoBehaviour
{
    [Tooltip("Pool of ring glyphs the carousel reuses, one per motif on screen. The first is cloned " +
             "when more are needed, and its depth (z) is the depth the orbit is drawn at.")]
    [SerializeField] private MotifRingGlyphApplicator[] slots;

    [Tooltip("Optional: an extra glyph added to the reusable pool.")]
    [SerializeField] private MotifRingGlyphApplicator upSlot;

    [Tooltip("Optional: an extra glyph added to the reusable pool.")]
    [SerializeField] private MotifRingGlyphApplicator downSlot;

    [Tooltip("X-axis tip applied to non-selected glyphs so the orbit reads as a disc seen from " +
             "above (squashes it vertically by cos). The selected glyph is never tipped.")]
    [SerializeField] private float neighborTiltDegrees = 55f;

    [Tooltip("Fraction of a grid cell's half-size (the smaller of width/height) that each glyph's " +
             "outer radius fills. The same size is used for every glyph.")]
    [Range(0.2f, 1f)]
    [SerializeField] private float cellFill = 0.9f;

    [Tooltip("Draws the orbit ellipses. Optional.")]
    [SerializeField] private MotifOrbitLineRenderer orbitLines;

    [Tooltip("Clear space left between the orbit line and a glyph's outer ring.")]
    [SerializeField] private float linePadding = 0.1f;

    [Tooltip("Alpha of the far half of an orbit, and of adjacent phases' orbits.")]
    [Range(0f, 1f)]
    [SerializeField] private float orbitDimAlpha = 0.35f;

    [Tooltip("Vertical radius of an orbit as a fraction of its horizontal radius. Higher = the ring " +
             "opens up more and the far side rises higher behind the selected glyph.")]
    [Range(0.02f, 0.5f)]
    [SerializeField] private float orbitFlatten = 0.12f;

    [Tooltip("Depth offset (z) so far-side glyphs draw behind near ones. Draw order only; it does not change how a glyph looks.")]
    [SerializeField] private float backDepthZ = 0.5f;
    [Tooltip("World-unit offset of the whole carousel from the camera centre (e.g. negative Y moves it down). Applies to the glyphs and the orbit lines together.")]
    [SerializeField] private Vector2 centerOffset = Vector2.zero;

    [Tooltip("World distance between stacked phase orbits. 0 = one grid cell.")]
    [SerializeField] private float phaseSpacing = 0f;

    [Tooltip("Points sampled around each orbit ellipse.")]
    [SerializeField] private int orbitSegments = 128;

    [SerializeField] private float slideDuration = 0.25f;
    [SerializeField] private string trackSelectionScene = "TrackSelection";
    [SerializeField] private float inputCooldown = 0.2f;

    [Tooltip("Alpha applied to non-center (unselected) carousel slots.")]
    [Range(0f, 1f)]
    [SerializeField] private float unselectedAlpha = 0.25f;

    [Tooltip("Shown when no ring records exist yet. Hide when rings are present.")]
    [SerializeField] private GameObject pressToStartCue;

    [Tooltip("Shows the selected motif's name: \"Tutorial\" for phase 0, otherwise \"Phase, Motif\" " +
             "(motif reads \"Unexplored\" until it has a saved ring).")]
    [SerializeField] private TMPro.TMP_Text selectionLabel;

    [SerializeField] private string tutorialLabel = "Tutorial";
    [SerializeField] private string unexploredLabel = "Unexplored";

    [Tooltip("Plays audio preview of the centered ring.")]
    [SerializeField] private RingPreviewPlayer previewPlayer;

    [Tooltip("Glyph config for the selected (centre) motif. Applied to whichever pooled glyph is showing it.")]
    [SerializeField] private RingGlyphConfig selectedGlyphConfig;

    [Tooltip("Glyph config for every unselected motif.")]
    [SerializeField] private RingGlyphConfig unselectedGlyphConfig;

    [Tooltip("World scale of the selected (centre) glyph. Its rings use the selected config's innerRadius, " +
             "ringThickness, ringSpacing and interBinSpacing as literal units (no fixed fit), so it can be " +
             "much larger than its neighbours.")]
    [SerializeField, Min(0.01f)] private float selectedGlyphScale = 1f;

    [Tooltip("Authoritative motif list: phase 0 = tutorial (1 motif), later phases = orbits of any size.")]
    [SerializeField] private PhaseLibrary phaseLibrary;

#if UNITY_EDITOR
    [Header("Debug (Editor Only)")]
    [Tooltip("Design-time only: shows and allows navigating to every phase/motif in " +
             "PhaseLibrary regardless of completion state. Never available in player builds.")]
    [SerializeField] private bool showAllPhasesForDesign = false;
#endif

    /// <summary>Fired when the player confirms a motif, before the scene loads.</summary>
    public UnityEvent onMotifConfirmed;

    /// <summary>One carousel slot's worth of data: either a recorded snapshot or a not-yet-played motif offered as a locked preview.</summary>
    private struct CarouselEntry
    {
        public MotifSnapshot Snapshot;   // non-null for a played/recorded motif
        public MotifProfile  Profile;    // non-null only for a locked (unplayed) entry
        public string PhaseId;
        public int    PhaseIndex;
        public int    MotifIndex;
        public string MotifId;
        public bool   IsLocked => Snapshot == null;
    }

    private MotifMazeGraph _graph;
    private readonly Dictionary<(string phaseId, string motifId), List<MotifSnapshot>> _snapshots = new();
    private readonly Dictionary<(int phase, int motif), CarouselEntry> _entries = new();
    private bool  _hasEntries;
    private int   _phase;
    private int   _motif;
    private Vector3 _anchor;
    private readonly List<MotifRingGlyphApplicator> _pool = new();
    private readonly HashSet<MotifRingGlyphApplicator> _inUse = new();
    private List<OrbitGroup> _groups = new();
    private MotifRingGlyphApplicator _selectedGlyph;
    private Grid _grid;
    private readonly List<(Vector2 at, float halfW, float halfH)> _footprints = new();
    private readonly List<Vector3> _run = new();
    private bool  _sliding;
    private bool  _confirming;
    private float _nextInputTime;

    // ── Lifecycle ──────────────────────────────────────────────────────────────

    private void Start()
    {
        if (slots == null || slots.Length == 0)
        {
            Debug.LogWarning("[PhaseLibraryCarousel] No slots assigned; carousel disabled.", this);
            enabled = false;
            return;
        }

        var rings = RingSessionStore.LoadAllRingsFromDisk();
        foreach (var r in rings)
        {
            var key = (r.PhaseId, r.MotifId);
            if (!_snapshots.TryGetValue(key, out var list)) _snapshots[key] = list = new List<MotifSnapshot>();
            list.Add(r);
        }

        bool revealAll = false;
#if UNITY_EDITOR
        revealAll = showAllPhasesForDesign;
#endif
        _graph = new MotifMazeGraph(phaseLibrary, IsCompleted, revealAll);
        PickStartNode();
        _graph.Remember(_phase, _motif);
        _hasEntries = (revealAll || rings.Count > 0) && _graph.Exists(_phase, _motif);

        InitSlotLayout();

        if (pressToStartCue != null) pressToStartCue.SetActive(!_hasEntries);

        if (_hasEntries)
        {
            BuildInitialScene();
            UpdatePreviewForCenter();
        }
        else
        {
            if (selectionLabel != null) selectionLabel.gameObject.SetActive(false);
            if (orbitLines != null) orbitLines.Clear();
            foreach (var g in _pool) g.gameObject.SetActive(false);
        }

        if (previewPlayer != null)
        {
            previewPlayer.OnBinIndexChanged += HandlePreviewBinChanged;
            previewPlayer.OnNoteFired       += HandleNoteFired;
            previewPlayer.OnNotePulse       += HandleNotePulse;
            previewPlayer.OnNoteApproach    += HandleNoteApproach;
        }
    }

    private void OnDestroy()
    {
        if (previewPlayer != null)
        {
            previewPlayer.OnBinIndexChanged -= HandlePreviewBinChanged;
            previewPlayer.OnNoteFired       -= HandleNoteFired;
            previewPlayer.OnNotePulse       -= HandleNotePulse;
            previewPlayer.OnNoteApproach    -= HandleNoteApproach;
        }
    }

    // Highlights the centered slot's ring(s) matching the bin RingPreviewPlayer is currently
    // sounding; -1 (playback stopped) clears any highlight instead. Only the centered slot ever
    // plays audio, so this is the only slot that ever receives highlight calls.
    private void HandlePreviewBinChanged(int binIndex)
    {
        if (_selectedGlyph == null) return;

        if (binIndex < 0) _selectedGlyph.ClearPlayingBinHighlight();
        else               _selectedGlyph.SetPlayingBin(binIndex);
    }

    // Spawns the note-position firework on the centered slot only, matching HandlePreviewBinChanged.
    private void HandleNoteFired(MusicalRole role, int binIndex, float angleRad)
    {
        if (_selectedGlyph == null) return;
        _selectedGlyph.SpawnNoteFirework(role, binIndex, angleRad);
    }

    // Turns the matching ring into place ahead of its note (centered slot only).
    private void HandleNoteApproach(MusicalRole role, int binIndex, int localStep, double startDsp, double prevStartDsp)
    {
        if (_selectedGlyph == null) return;
        _selectedGlyph.ApproachNote(role, binIndex, localStep, startDsp, prevStartDsp);
    }

    // Swells the matching ring's dip for the note's sounding length — centered slot only.
    private void HandleNotePulse(MusicalRole role, int binIndex, int localStep, double startDsp, float durationSec)
    {
        if (_selectedGlyph == null) return;
        _selectedGlyph.PulseNote(role, binIndex, localStep, startDsp, durationSec);

        // The orbit line's needle ripples on the same clock as the dip pulse.
        if (orbitLines != null)
        {
            double at = startDsp + (selectedGlyphConfig != null ? selectedGlyphConfig.notePulseLatencySec : 0f);
            _selectedGlyph.TryGetRingColor(role, binIndex, out var sparkColor);
            orbitLines.PulseNeedle(at, Color.white);
        }
    }

    // ── Entries ────────────────────────────────────────────────────────────────

    // Snapshots are matched by stable PhaseId/MotifId rather than raw index — safe against
    // PhaseLibrary reordering, same convention as PhaseTransitionManager.JumpToMotif.
    private bool IsCompleted(int phaseIndex, int motifIndex)
    {
        var phases = phaseLibrary != null ? phaseLibrary.phases : null;
        if (phases == null || phaseIndex < 0 || phaseIndex >= phases.Count) return false;

        var phase = phases[phaseIndex];
        if (phase?.motifs == null || motifIndex < 0 || motifIndex >= phase.motifs.Count) return false;

        return phase.motifs[motifIndex] != null && FindSnapshot(phaseIndex, motifIndex) != null;
    }

    // The ring saved for this exact slot. Slots may share a motifId (duplicate / placeholder
    // entries), so when more than one candidate matches the id the snapshot's MotifIndex decides.
    private MotifSnapshot FindSnapshot(int phaseIndex, int motifIndex)
    {
        var phase = phaseLibrary.phases[phaseIndex];
        var motif = phase.motifs[motifIndex];
        if (!_snapshots.TryGetValue((phase.phaseId, motif.motifId), out var candidates)) return null;

        int sharing = 0;
        foreach (var m in phase.motifs)
            if (m != null && m.motifId == motif.motifId) sharing++;
        if (sharing <= 1) return candidates[0];

        foreach (var c in candidates)
            if (c.MotifIndex == motifIndex) return c;
        return null;
    }

    private CarouselEntry GetEntry(int phaseIndex, int motifIndex)
    {
        if (_entries.TryGetValue((phaseIndex, motifIndex), out var cached)) return cached;

        var phase = phaseLibrary.phases[phaseIndex];
        var motif = phase.motifs[motifIndex];
        var snap = FindSnapshot(phaseIndex, motifIndex);

        var entry = new CarouselEntry
        {
            Snapshot   = snap,
            Profile    = snap == null ? motif : null,
            PhaseId    = phase.phaseId,
            PhaseIndex = phaseIndex,
            MotifIndex = motifIndex,
            MotifId    = motif.motifId,
        };
        _entries[(phaseIndex, motifIndex)] = entry;
        return entry;
    }

    private CarouselEntry CurrentEntry => GetEntry(_phase, _motif);

    // ── Setup ──────────────────────────────────────────────────────────────────

    private void InitSlotLayout()
    {
        _anchor = slots[0].transform.position; // depth the orbit is drawn at

        foreach (var g in slots) if (g != null) _pool.Add(g);
        if (upSlot != null)   _pool.Add(upSlot);
        if (downSlot != null) _pool.Add(downSlot);
    }

    // Tutorial not done: the hub is the only entry. Otherwise open where the player last recorded
    // a ring (newest save among the orbit phases), falling back to the first motif of phase 1.
    private void PickStartNode()
    {
        _phase = 0;
        _motif = 0;
        if (!_graph.TutorialComplete) return;

        _phase = 1;
        long newest = long.MinValue;
        var phases = phaseLibrary != null ? phaseLibrary.phases : null;
        if (phases == null) return;

        for (int p = 1; p < phases.Count; p++)
        for (int m = 0; m < _graph.MotifCount(p); m++)
        {
            if (!_graph.Exists(p, m)) continue;
            var snap = FindSnapshot(p, m);
            if (snap == null) continue;
            if (snap.SavedAtUtcTicks <= newest) continue;

            newest = snap.SavedAtUtcTicks;
            _phase = p;
            _motif = m;
        }
    }

    // ── Display ────────────────────────────────────────────────────────────────

    private struct Grid
    {
        public Vector3 C;
        public float CellW, CellH, R, S;
    }

    private struct OrbitShape { public float A, Rx, Ry; }

    private sealed class OrbitNode
    {
        public MotifRingGlyphApplicator Glyph;
        public int Motif;
        public int D; // signed steps from the group's selected motif
        public bool Centered; // whether Glyph was last applied (and configured) as the selected look
    }

    // One orbit on screen: a phase's motifs (Full) or just its front motif (adjacent phases).
    private sealed class OrbitGroup
    {
        public int Phase, Motif, Count;
        public OrbitShape Shape;
        public float YOff;   // vertical offset from the grid centre
        public float Spin;   // fractional steps the ring has turned
        public float LineAlpha = 1f;
        public readonly List<OrbitNode> Nodes = new();
    }

    // Height above the selected glyph for a point `phi` round the orbit: the front dips lowest and
    // the ring curves up on both sides and round the back.
    private static float RiseAt(OrbitShape o, float phi) => o.Ry * (1f - Mathf.Cos(phi));

    private static float Wrap(float angle) => Mathf.Repeat(angle + Mathf.PI, Mathf.PI * 2f) - Mathf.PI;

    // Screen-grid geometry from the main camera: grid centre, cell size and glyph radius.
    private bool TryComputeGrid(out Grid g)
    {
        g = default;
        var cam = Camera.main;
        if (cam == null || !cam.orthographic) return false;

        float halfH = cam.orthographicSize;
        g.C     = new Vector3(cam.transform.position.x + centerOffset.x, cam.transform.position.y + centerOffset.y, _anchor.z);
        g.CellH = 2f * halfH / 3f;
        g.CellW = 2f * halfH * cam.aspect / 3f;
        g.R     = cellFill * Mathf.Min(g.CellW, g.CellH) * 0.5f;
        g.S     = phaseSpacing > 0f ? phaseSpacing : g.CellH;
        return true;
    }

    // Neighbours always land one cell to the side for small orbits; bigger orbits widen past the
    // screen so the visible front arc keeps the same glyph spacing.
    private OrbitShape ShapeFor(int count)
    {
        float a  = Mathf.PI * 2f / Mathf.Max(1, count);
        float rx = _grid.CellW / Mathf.Sin(Mathf.Min(a, Mathf.PI * 0.5f));
        return new OrbitShape { A = a, Rx = rx, Ry = rx * orbitFlatten };
    }

    // Where a glyph `d` steps round the orbit sits (ring turned by `spin` steps) and its tip.
    private void Evaluate(OrbitGroup g, float d, float spin, out Vector3 pos, out float tilt)
    {
        var o = g.Shape;
        float phi  = (d - spin) * o.A;
        float cosP = Mathf.Cos(phi);
        pos = _grid.C + new Vector3(o.Rx * Mathf.Sin(phi), g.YOff + RiseAt(o, phi), backDepthZ * (1f - cosP) * 0.5f);

        float stepF = Mathf.Min(1f, Mathf.Abs(Wrap(phi)) / o.A);
        float vert  = _grid.S > 0f ? Mathf.Min(1f, Mathf.Abs(g.YOff) / _grid.S) : 0f;
        tilt = neighborTiltDegrees * Mathf.Max(stepF, vert);
    }

    // Signed steps in (-count/2, count/2] round a ring of `count`.
    private static int WrapSteps(int d, int count)
    {
        if (d > count / 2) d -= count;
        else if (d <= -count / 2 && count > 1) d += count;
        return d;
    }

    // An orbit with a glyph slot for every motif of the phase (unlocked or not). Slots are unassigned.
    private OrbitGroup BuildGroup(int phase, int motif, float yOff, bool full)
    {
        var g = new OrbitGroup { Phase = phase, Motif = motif, YOff = yOff, Count = Mathf.Max(1, _graph.MotifCount(phase)) };
        g.Shape = ShapeFor(g.Count);
        if (!full || phase == 0 || g.Count < 2)
        {
            g.Nodes.Add(new OrbitNode { Motif = motif, D = 0 });
            return g;
        }

        for (int k = 0; k < g.Count; k++)
            if (k == motif || _graph.Exists(phase, k))
                g.Nodes.Add(new OrbitNode { Motif = k, D = WrapSteps(k - motif, g.Count) });
        return g;
    }

    // Re-bases every glyph's offset on `motif` becoming the selected one.
    private void RebaseNodes(OrbitGroup g, int motif)
    {
        g.Motif = motif;
        if (g.Phase == 0 || g.Count < 2) return;
        foreach (var n in g.Nodes) n.D = WrapSteps(n.Motif - motif, g.Count);
    }

    private MotifRingGlyphApplicator TakeGlyph()
    {
        foreach (var g in _pool)
            if (!_inUse.Contains(g)) { _inUse.Add(g); g.gameObject.SetActive(true); return g; }

        var clone = Instantiate(_pool[0], _pool[0].transform.parent);
        clone.name = $"{_pool[0].name} ({_pool.Count})";
        // Instantiate copies the source's ring objects as children, but not the applicator's private
        // ring lists, so the clone could never clear them and they'd show through its own rings.
        foreach (Transform child in clone.transform) Destroy(child.gameObject);
        clone.SetFixedFit(_grid.R);
        clone.gameObject.SetActive(true); // ApplyEntry starts coroutines, which need an active object
        _pool.Add(clone);
        _inUse.Add(clone);
        return clone;
    }

    private void ReleaseNode(OrbitNode n)
    {
        if (n.Glyph == null) return;
        n.Glyph.gameObject.SetActive(false);
        _inUse.Remove(n.Glyph);
        n.Glyph = null;
    }

    private void ReleaseGroup(OrbitGroup g)
    {
        foreach (var n in g.Nodes) ReleaseNode(n);
    }

    private float NodeAlpha(OrbitGroup g, OrbitNode n, bool isSelectedGroup)
    {
        return isSelectedGroup && n.D == 0 ? 1f : unselectedAlpha;
    }

    private void AssignNode(OrbitGroup g, OrbitNode n, bool isSelectedGroup)
    {
        n.Glyph = TakeGlyph();
        bool isCenter = isSelectedGroup && n.D == 0;
        if (isCenter) _selectedGlyph = n.Glyph;
        n.Centered = isCenter;
        ApplyEntry(n.Glyph, GetEntry(g.Phase, n.Motif), isCenter, NodeAlpha(g, n, isSelectedGroup));
    }

    private void AssignGroup(OrbitGroup g, bool isSelectedGroup)
    {
        foreach (var n in g.Nodes) AssignNode(g, n, isSelectedGroup);
    }

    private void PlaceGroup(OrbitGroup g)
    {
        foreach (var n in g.Nodes)
        {
            if (n.Glyph == null) continue;
            Evaluate(g, n.D, g.Spin, out var pos, out float tilt);
            n.Glyph.gameObject.SetActive(true);
            n.Glyph.transform.position      = pos;
            n.Glyph.transform.localRotation = Quaternion.Euler(tilt, 0f, 0f);

            // Every glyph's renderers use the same explicit sortingOrders (rings stack at 3, 5, 7…),
            // and an explicit order always beats z, so without a group a far glyph's higher-numbered
            // rings would draw over the near glyph. The group makes each glyph sort as one unit, and
            // nearer (smaller z) glyphs get the higher order.
            if (!n.Glyph.TryGetComponent<SortingGroup>(out var group))
                group = n.Glyph.gameObject.AddComponent<SortingGroup>();
            group.sortingOrder = -Mathf.RoundToInt(pos.z * 1000f);
        }
    }

    private void BuildInitialScene()
    {
        if (!TryComputeGrid(out _grid))
        {
            Debug.LogWarning("[PhaseLibraryCarousel] Needs an orthographic Main camera; carousel disabled.", this);
            enabled = false;
            return;
        }
        foreach (var g in _pool) { g.SetFixedFit(_grid.R); g.gameObject.SetActive(false); }
        _inUse.Clear();
        _selectedGlyph = null;

        var cur = BuildGroup(_phase, _motif, 0f, true);
        _groups = new List<OrbitGroup> { cur };
        AssignGroup(cur, true);
        RebuildAdjacent();
        RefreshLabel();
        RefreshScene();
    }

    // The next phase out waits one phase spacing above, shown dimly as its front glyph and orbit.
    private void RebuildAdjacent()
    {
        for (int i = _groups.Count - 1; i >= 1; i--)
        {
            ReleaseGroup(_groups[i]);
            _groups.RemoveAt(i);
        }

        if (!_graph.TryGetNeighbor(_phase, _motif, MazeArm.Up, out int p, out int m) || p == _phase) return;
        var g = BuildGroup(p, m, _grid.S, false);
        g.LineAlpha = orbitDimAlpha;
        _groups.Add(g);
        AssignGroup(g, false);
    }

    // Positions every glyph and redraws the lines from the groups' current spin / offsets.
    private void RefreshScene()
    {
        foreach (var g in _groups) PlaceGroup(g);
        RefreshLines();
    }

    private void RefreshLines()
    {
        if (orbitLines == null) return;
        orbitLines.Clear();
        foreach (var g in _groups)
            if (g.Phase >= 1 && g.Count >= 2) DrawOrbit(g, allowShimmer: Mathf.Abs(g.YOff) < 0.001f);
    }

    // The orbit ellipse: near half at the group's alpha, far half dimmer, with gaps behind glyphs.
    // The last near-side run before the selected glyph's footprint (its left edge, by the playback
    // point) is drawn as the animated needle when allowShimmer.
    private void DrawOrbit(OrbitGroup g, bool allowShimmer)
    {
        var o = g.Shape;

        _footprints.Clear();
        foreach (var n in g.Nodes)
        {
            if (n.Glyph == null || !n.Glyph.gameObject.activeSelf) continue;
            Evaluate(g, n.D, g.Spin, out var pos, out float tilt);
            // The selected glyph lays out freely, so its footprint follows its real outermost ring.
            float r = n.Centered && n.Glyph.OuterWorldRadius > 0f ? n.Glyph.OuterWorldRadius : _grid.R;
            _footprints.Add((new Vector2(pos.x, pos.y), r + linePadding,
                             r * Mathf.Cos(tilt * Mathf.Deg2Rad) + linePadding));
        }

        int seg = Mathf.Max(16, orbitSegments);
        bool runIsFar = false;
        bool startsHidden = false, needleDrawn = false, startFaded = false;
        _run.Clear();

        Vector3 PointAt(int i)
        {
            float t = i / (float)seg * Mathf.PI * 2f;
            return _grid.C + new Vector3(o.Rx * Mathf.Sin(t), g.YOff + RiseAt(o, t), 0f);
        }
        bool IsHidden(Vector3 p)
        {
            foreach (var f in _footprints)
            {
                float dx = (p.x - f.at.x) / f.halfW;
                float dy = (p.y - f.at.y) / f.halfH;
                if (dx * dx + dy * dy < 1f) return true;
            }
            return false;
        }

        // The needle is the run that reaches the last visible point before the line wraps back to the start.
        int lastVisible = -1;
        for (int i = seg; i >= 0 && lastVisible < 0; i--)
            if (!IsHidden(PointAt(i))) lastVisible = i;

        void Flush(bool last)
        {
            if (_run.Count >= 2)
            {
                bool needle = allowShimmer && startsHidden && last && !needleDrawn && !runIsFar;
                if (needle) needleDrawn = true;
                // The first near run after the glyph (its right side) is the line's soft end.
                bool softStart = allowShimmer && startsHidden && !last && !startFaded && !runIsFar;
                if (softStart) startFaded = true;
                orbitLines.DrawPolyline(_run, g.LineAlpha * (runIsFar ? orbitDimAlpha : 1f), needle, softStart);
            }
            _run.Clear();
        }

        for (int i = 0; i <= seg; i++)
        {
            float t    = i / (float)seg * Mathf.PI * 2f;
            float cosT = Mathf.Cos(t);
            var p = PointAt(i);

            bool hidden = IsHidden(p);
            if (i == 0) startsHidden = hidden;
            if (hidden) { Flush(i > lastVisible); continue; }

            bool far = cosT < -0.0001f;
            if (_run.Count > 0 && far != runIsFar) Flush(false);
            runIsFar = far;
            _run.Add(p);
        }
        Flush(true);
    }

    private void RefreshLabel()
    {
        if (selectionLabel == null) return;

        var entry = CurrentEntry;
        if (entry.PhaseIndex == 0) { selectionLabel.text = tutorialLabel; return; }

        var phase = phaseLibrary.phases[entry.PhaseIndex];
        var motif = phase.motifs[entry.MotifIndex];
        string motifName = entry.IsLocked
            ? unexploredLabel
            : (string.IsNullOrEmpty(motif.displayName) ? motif.motifId : motif.displayName);
        selectionLabel.text = $"{phase.phaseName}, {motifName}";
    }

    private void ApplyEntry(MotifRingGlyphApplicator slot, CarouselEntry entry, bool isCenter, float alpha)
    {
        // Pooled glyphs aren't tied to a role, so the config follows the motif's selected state.
        var cfg = isCenter ? selectedGlyphConfig : unselectedGlyphConfig;
        if (cfg != null) slot.config = cfg;

        // Unselected glyphs share one radius (SetFixedFit); the selected one lays out from its config.
        if (isCenter) slot.SetFreeLayout(selectedGlyphScale);
        else          slot.SetFixedFit(_grid.R);
        if (entry.IsLocked)
            slot.ApplyLockedPreview(entry.Profile.nodesPerStar, alpha);
        else if (isCenter)
            slot.AnimateApply(entry.Snapshot, replayLastRing: false, notePulse: true);
        else
            slot.ApplyVinyl(entry.Snapshot, alpha);

        slot.SetSelectedBackdrop(isCenter ? 1f : 0f);
    }

    private void UpdatePreviewForCenter()
    {
        if (previewPlayer == null) return;
        var entry = CurrentEntry;
        if (entry.IsLocked) previewPlayer.Stop();
        else previewPlayer.Play(entry.Snapshot);
    }

    // ── Input ──────────────────────────────────────────────────────────────────

    private void Update()
    {
        // This polls raw device state directly (no Input Action / EventSystem involved), so it
        // has no built-in awareness of modal UI on top of it. The settings panel's own Confirm
        // press must not also be read here as "confirm the carousel and load TrackSelection" —
        // same guard role as the one in IntroInputHandler.OnAnyInput / LocalPlayer.HandleConfirm.
        // ShouldSuppressInput (not just IsOpen) also covers the same frame the panel just closed
        // on this exact press — Close() runs synchronously inside the Confirm callback, earlier
        // in this same frame, which would otherwise clear IsOpen before this Update() runs.
        if (SettingsPanelController.ShouldSuppressInput) return;

        bool right = false, left = false, up = false, down = false, confirm = false;

        var gp = Gamepad.current;
        if (gp != null)
        {
            right   |= gp.dpad.right.wasPressedThisFrame;
            left    |= gp.dpad.left.wasPressedThisFrame;
            up      |= gp.dpad.up.wasPressedThisFrame;
            down    |= gp.dpad.down.wasPressedThisFrame;
            confirm |= gp.buttonSouth.wasPressedThisFrame;
        }

        var kb = Keyboard.current;
        if (kb != null)
        {
            right   |= kb.rightArrowKey.wasPressedThisFrame;
            left    |= kb.leftArrowKey.wasPressedThisFrame;
            up      |= kb.upArrowKey.wasPressedThisFrame;
            down    |= kb.downArrowKey.wasPressedThisFrame;
            confirm |= kb.enterKey.wasPressedThisFrame || kb.spaceKey.wasPressedThisFrame;
        }

        if (!_hasEntries)
        {
            if (confirm) SceneManager.LoadScene(trackSelectionScene);
            return;
        }

        if (_sliding || _confirming) return; // ignore all input mid-slide/confirm so Confirm() never reads a stale node

        if (confirm) { Confirm(); return; }

        if (Time.unscaledTime >= _nextInputTime)
        {
            MazeArm? dir = right ? MazeArm.Right : left ? MazeArm.Left : down ? MazeArm.Down : up ? MazeArm.Up : (MazeArm?)null;
            if (dir.HasValue)
            {
                _nextInputTime = Time.unscaledTime + inputCooldown;
                Navigate(dir.Value);
            }
        }
    }

    // ── Navigation ─────────────────────────────────────────────────────────────

    // No-op when nothing lies that way (locked, or the orbit has nowhere to go). Left / Right turn
    // the ring one step; Up / Down shift the scene one phase.
    private void Navigate(MazeArm dir)
    {
        if (!_graph.TryGetNeighbor(_phase, _motif, dir, out int p, out int m)) return;
        StartCoroutine(dir == MazeArm.Left || dir == MazeArm.Right
            ? TurnRing(p, m)
            : ShiftPhase(dir == MazeArm.Up ? 1 : -1, p, m));
    }

    private IEnumerator TurnRing(int targetPhase, int targetMotif)
    {
        _sliding = true;
        var cur = _groups[0];
        int steps = WrapSteps(targetMotif - cur.Motif, cur.Count); // more than one when locked motifs are skipped
        yield return AnimateSlide(t => cur.Spin = steps * t,
            cur.Nodes.Find(n => n.D == 0), cur.Nodes.Find(n => n.Motif == targetMotif));

        Settle(cur, targetPhase, targetMotif);
        _sliding = false;
        UpdatePreviewForCenter();
    }

    // The whole scene glides by one phase spacing: the destination orbit (built in full and carried in
    // with the scene) arrives at the centre while the current one leaves the opposite way.
    private IEnumerator ShiftPhase(int dir, int targetPhase, int targetMotif)
    {
        _sliding = true;
        var cur = _groups[0];
        for (int i = 1; i < _groups.Count; i++) ReleaseGroup(_groups[i]);

        var dest = BuildGroup(targetPhase, targetMotif, dir * _grid.S, true);
        AssignGroup(dest, false);
        _groups = new List<OrbitGroup> { cur, dest };

        yield return AnimateSlide(t =>
            {
                cur.YOff  = -dir * _grid.S * t;
                dest.YOff = dir * _grid.S * (1f - t);
            },
            cur.Nodes.Find(n => n.D == 0), dest.Nodes.Find(n => n.D == 0));

        ReleaseGroup(cur);
        _groups = new List<OrbitGroup> { dest };
        Settle(dest, targetPhase, targetMotif);
        _sliding = false;
        UpdatePreviewForCenter();
    }

    // Runs `apply(t)` for t eased 0 -> 1 over slideDuration, fading the outgoing / incoming selected
    // glyphs and redrawing the scene each frame.
    private IEnumerator AnimateSlide(System.Action<float> apply, OrbitNode oldSel, OrbitNode newSel)
    {
        float elapsed = 0f;
        while (elapsed < slideDuration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(elapsed / slideDuration));
            apply(t);
            FadeSelection(oldSel, newSel, t);
            RefreshScene();
            yield return null;
        }
    }

    // The outgoing selected glyph dims to the unselected look while the incoming one brightens.
    private void FadeSelection(OrbitNode oldSel, OrbitNode newSel, float t)
    {
        if (oldSel?.Glyph != null) oldSel.Glyph.SetAlphaScale(Mathf.Lerp(1f, unselectedAlpha, t));
        if (oldSel?.Glyph != null) oldSel.Glyph.SetSelectedBackdrop(1f - t);
        if (newSel?.Glyph != null) newSel.Glyph.SetAlphaScale(Mathf.Lerp(unselectedAlpha, 1f, t));
        if (newSel?.Glyph != null) newSel.Glyph.SetSelectedBackdrop(t);
    }

    // Lands a slide without rebuilding any ring: glyphs stay as drawn, offsets are re-based on the new
    // selected motif, glyphs that drifted out of range are released, newly needed ones are drawn,
    // and alphas are re-tinted in place.
    private void Settle(OrbitGroup cur, int phase, int motif)
    {
        _phase = phase;
        _motif = motif;
        _graph.Remember(phase, motif);
        cur.Phase = phase;
        cur.Spin  = 0f;
        cur.YOff  = 0f;
        cur.LineAlpha = 1f;

        RebaseNodes(cur, motif);
        RebuildAdjacent();

        foreach (var n in cur.Nodes)
        {
            if (n.Glyph == null) continue;

            // Contour shape (tug depth/width) is baked from the glyph's config when its rings are
            // built, so a node whose selected state flipped must be rebuilt with the new state's
            // config — swapping the config alone would leave it wearing the old state's shape.
            bool center = n.D == 0;
            if (n.Centered != center)
            {
                n.Centered = center;
                ApplyEntry(n.Glyph, GetEntry(cur.Phase, n.Motif), center, NodeAlpha(cur, n, true));
            }

            n.Glyph.SetAlphaScale(NodeAlpha(cur, n, true));
            n.Glyph.SetSelectedBackdrop(center ? 1f : 0f);
            if (center) _selectedGlyph = n.Glyph;
        }

        RefreshLabel();
        RefreshScene();
    }

    // ── Confirm ────────────────────────────────────────────────────────────────

    private void Confirm()
    {
        if (_confirming) return;
        _confirming = true;
        StartCoroutine(ConfirmRoutine());
    }

    private IEnumerator ConfirmRoutine()
    {
        // Fades the preview's own AudioSource to silence (see RingPreviewPlayer.StopAndFadeRoutine)
        // rather than guessing how long the synth's release tail needs — guarantees no residual
        // amplitude by the time the scene unload below destroys its MidiStreamPlayer mid-buffer.
        if (previewPlayer != null)
            yield return StartCoroutine(previewPlayer.StopAndFadeRoutine());

        var entry = CurrentEntry;
        PhaseLibraryStartConfig.RequestStart(entry.PhaseId, entry.PhaseIndex, entry.MotifIndex, entry.MotifId);
        onMotifConfirmed.Invoke();
        SceneManager.LoadScene(trackSelectionScene);
    }
}
