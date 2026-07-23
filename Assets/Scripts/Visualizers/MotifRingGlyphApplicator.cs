using System.Collections.Generic;
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
    private Coroutine _gameplayHideCoroutine;

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

    private static void DestroyList(List<RingEntry> list)
    {
        foreach (var e in list)
            if (e.Root != null) Destroy(e.Root);
        list.Clear();
    }
}
