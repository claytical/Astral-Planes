using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;

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
//
//  Split across partials: GameplayRings.cs (per-bin rings), RecordRings.cs
//  (full-motif snapshot rendering), SpinOff.cs (roll-off exit animation),
//  RingBuilder.cs (mesh/GameObject construction), Animation.cs (low-level
//  draw-in/travel-dot coroutines).
// =========================================================================
public partial class MotifRingGlyphApplicator : MonoBehaviour
{
    [Header("Config")]
    public RingGlyphConfig config;

    [Tooltip("Material for contour LineRenderers (Sprites/Default works well).")]
    public Material lineMaterial;

    [Tooltip("When true, rings rotate at speeds distributed evenly across [rotSpeedBase, rotSpeedMax] " +
             "by ring index rather than by fill duration. Use on library cards for a spherical look.")]
    public bool sphericalRotation = false;

    [Tooltip("When GameFlowManager is unavailable (carousel, solar system), the ring-stack outer radius " +
             "(world units) TARGETED when ring count is at or above maxRingCountForSizing — not a hard " +
             "ceiling. The stack is always rescaled to interpolate between minDisplayRadius (at " +
             "minRingCountForSizing) and this value, so ring count directly drives overall size instead " +
             "of a cap that only ever shrinks (or never engages at all). 0 = no fit (raw, unscaled size).")]
    public float maxDisplayRadius = 0f;

    [Tooltip("Ring-stack outer radius (world units) when ring count is at or below minRingCountForSizing.")]
    public float minDisplayRadius = 0.5f;

    [Tooltip("Ring count corresponding to minDisplayRadius.")]
    public int minRingCountForSizing = 2;

    [Tooltip("Ring count corresponding to maxDisplayRadius.")]
    public int maxRingCountForSizing = 16;

    [Tooltip("NoteVisualizer whose noteMarkers provide travel-dot start positions for gameplay rings.")]
    [SerializeField] private NoteVisualizer noteVisualizer;

    [Tooltip("Prefab instantiated for each note travel dot. Must have a LineRenderer or SpriteRenderer to receive the note color.")]
    [SerializeField] private GameObject noteTravelDotPrefab;

    [Tooltip("One-shot particle prefab instantiated at both ends of a broken tether (timeline marker and ring landing point). Must self-destroy (e.g. via Explosion.cs) and expose a ParticleSystem to receive the track color.")]
    [SerializeField] private GameObject tetherBreakEffectPrefab;

    [Tooltip("One-shot particle prefab spawned at a note's position on its ring while RingPreviewPlayer plays it back (PhaseLibrary carousel preview only). Must self-destroy (e.g. via Explosion.cs) and expose a ParticleSystem to receive the ring's role color.")]
    [SerializeField] private GameObject notePlaybackFireworkPrefab;

    [Tooltip("Mixer group for launch/impact one-shot SFX (see PlayOneShotRouted in Animation.cs).")]
    [SerializeField] private AudioMixerGroup effectsMixerGroup;

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
        public float         OuterRadius;
        // This ring's position in its own stack (0 = innermost), NOT BinIndex — multiple rings
        // can share (or lack) a meaningful BinIndex (e.g. every locked-preview/remaining-progress
        // placeholder ring uses -1), so this is what BuildRingEntry uses to stagger each ring's
        // contour seam angle — see RingOrdinalAngleJitter in MotifRingGlyphGenerator.cs.
        public int           RingOrdinal;
    }

    private readonly List<RingEntry> _gameplayRings  = new();
    private readonly List<RingEntry> _recordRings    = new();
    private readonly List<RingEntry> _remainingRings = new(); // gray placeholders for motif progress not yet completed

    // Currently-playing-bin highlight (RecordRings.cs) — indices into _recordRings.
    private readonly HashSet<int> _highlightedRingIndices = new();
    private readonly Dictionary<int, Coroutine> _ringPulseCoroutines = new();

    private bool    _recordFadingOut;
    private bool    _gameplayFadingOut;    // stops rotation coroutines; set when spin animation begins
    private bool    _clearingGameplayRings; // stops deformation coroutines; set only in ClearGameplayRings
    private bool    _superNodeMode;
    private bool    _spinOffPending;       // spin is imminent; prevents per-deformation hide during wait
    private Vector3 _fitScale;
    private int     _pendingDeformationCount;
    private Coroutine _gameplayHideCoroutine;
    private Coroutine _nextUpColorCoroutine;
    private Coroutine _recordFadeOutCoroutine;
    private Color     _nextUpCurrentColor; // live lerped RGB of the next-up ring, for fade-out to read

    // Solid backdrop discs behind the whole ring stack — inner one sized to placed
    // (completed) rings, outer one sized to the entire record (placed + remaining).
    private MeshRenderer _placedBgDisc;
    private MeshRenderer _fullBgDisc;
    private MaterialPropertyBlock _placedBgMpb;
    private MaterialPropertyBlock _fullBgMpb;

    // Physical collision for gameplay bin rings only — never added for library-card
    // (RecordRings) instances, since only SpawnBinRing lazily creates it.
    private CircleCollider2D _ringCollider;

    // Exposed so Vehicle can grant itself brief collision immunity right after depositing a
    // note — depositing leaves the vehicle sitting at the ring, and OnCollisionEnter2D would
    // otherwise treat that near-guaranteed self-collision as a TryBreakNextPendingTether hit
    // on the note it just committed. Null until SpawnBinRing lazily creates the collider.
    public CircleCollider2D RingCollider => _ringCollider;

    // A note whose preview tether is still live (created, not yet departed toward the ring).
    // Populated/retired by DeformBinRingCoroutine; consumed by TryBreakNextPendingTether when
    // the vehicle collides with _ringCollider (see MotifRingGlyphApplicator.GameplayRings.cs).
    private class PendingRingNote
    {
        public NoteTether       Tether;
        public GameObject       Anchor;
        public InstrumentTrack  Track;
        public int              Step;
        public bool[]           CancelFlag; // shared with the WaitAndLaunchDot closure
        // Lets the owning DeformBinRingCoroutine know this note will never reach onLaunch, so its
        // completion wait doesn't hang forever waiting for a note that's been broken.
        public System.Action    OnCancelled;
    }
    private readonly List<PendingRingNote> _pendingRingNotes = new();

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

    /// <summary>Destroy all rings (both layers).</summary>
    public void Clear()
    {
        foreach (Transform child in transform) Destroy(child.gameObject);
        _recordRings.Clear();
        _gameplayRings.Clear();
        _remainingRings.Clear();
        _placedBgDisc = null;
        _fullBgDisc   = null;
        ResetHighlightState();
    }

    // Stops any in-flight playing-bin pulse coroutines and clears their bookkeeping. Must run
    // whenever _recordRings is rebuilt (indices into it would otherwise go stale) or destroyed.
    private void ResetHighlightState()
    {
        foreach (var co in _ringPulseCoroutines.Values)
            if (co != null) StopCoroutine(co);
        _ringPulseCoroutines.Clear();
        _highlightedRingIndices.Clear();
    }

    public void FitToPlayArea(float width, float height, float cx, float cy)
    {
        float s = Mathf.Min(width, height);
        transform.position   = new Vector3(cx, cy, transform.position.z);
        transform.localScale = new Vector3(s, s, 1f);
        _fitScale = transform.localScale;
    }

    private float RingInnerRadius(int idx) =>
        config.innerRadius + idx * (config.ringThickness + config.ringSpacing);

    // ── Gameplay collision ──────────────────────────────────────────────────
    // Solid while the ring stack is flat (spawn, deformation, idle hide); disabled
    // once the tilt-exit phase begins (RunSpinRollScale) so the vehicle can phase
    // through. Static, non-trigger — Unity's physics solver pushes the vehicle out
    // for free, same as CosmicDust.

    private void EnsureRingCollider()
    {
        if (_ringCollider != null) return;
        _ringCollider = GetComponent<CircleCollider2D>();
        if (_ringCollider == null) _ringCollider = gameObject.AddComponent<CircleCollider2D>();
        _ringCollider.isTrigger = false;
        _ringCollider.enabled   = false;
        int ringLayer = LayerMask.NameToLayer("RingGlyph");
        if (ringLayer >= 0) gameObject.layer = ringLayer;
    }

    private void UpdateRingColliderRadius(int ringCount)
    {
        if (_ringCollider == null || config == null || ringCount <= 0) return;
        _ringCollider.radius = RingInnerRadius(ringCount - 1) + config.ringThickness;
    }

    private void RefreshPlayAreaFit(int ringCount, float? actualOuterRadiusOverride = null)
    {
        if (config == null || ringCount == 0) return;

        var gfm = GameFlowManager.Instance;
        if (gfm?.activeDrumTrack != null && gfm.activeDrumTrack.TryGetPlayAreaWorld(out var area))
        {
            float outerRadius = actualOuterRadiusOverride ?? (RingInnerRadius(ringCount - 1) + config.ringThickness);
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
            float outerRadius = actualOuterRadiusOverride ?? (RingInnerRadius(ringCount - 1) + config.ringThickness);
            if (outerRadius <= 0f) return;

            // Interpolate between min/max targets by ring count so overall size is driven directly
            // by how many rings there are, rather than a cap that only ever shrinks an oversized
            // stack (or never engages at all when the config's natural size is already small).
            int minCount = Mathf.Max(1, minRingCountForSizing);
            int maxCount = Mathf.Max(minCount + 1, maxRingCountForSizing);
            float t            = Mathf.InverseLerp(minCount, maxCount, ringCount);
            float targetRadius = Mathf.Lerp(minDisplayRadius, maxDisplayRadius, t);

            float scale = targetRadius / outerRadius;
            transform.localScale = Vector3.one * scale;
            _fitScale            = transform.localScale;
        }
    }

    private static void DestroyList(List<RingEntry> list)
    {
        foreach (var e in list)
            if (e.Root != null) Destroy(e.Root);
        list.Clear();
    }
}
