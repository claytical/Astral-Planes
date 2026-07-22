using System;
using UnityEngine;
using System.Collections.Generic;

public partial class NoteVisualizer : MonoBehaviour
{
    [Header("Ascension Director")]
    [Tooltip("Handles all time-sequenced ascension animation. Must be assigned.")]
    [SerializeField] private NoteAscensionDirector ascensionDirector;

    [Header("Playhead")]
    public RectTransform playheadLine;
    public ParticleSystem playheadParticles;
    [Range(0f, 1f)]
    [SerializeField] private float _playheadEnergy01 = 0f;       // what we actually render
    private float _playheadEnergyTarget01 = 0f;                  // what game logic asks for
    [SerializeField] private float _playheadEnergyLerpSpeed = 4f;
    [SerializeField] private float releasePulseSeconds = 0.18f;
    [Tooltip("Fallback color used when no MusicalRole profile can be resolved for the releasing note.")]
    [SerializeField] private Color releasePulseColorFallback = new Color(1f, 0.2f, 0.9f, 1f); // hot magenta-ish
    private MusicalRole _lastReleasePulseRole = MusicalRole.None;
    [Header("Marker & Tether Prefabs")]
    public GameObject notePrefab;
    public GameObject noteTetherPrefab;

    [Header("Manual Release Cue")]
    [Tooltip("Prefab for the ghost/cue that travels between the vehicle and the next unlit placeholder marker.")]
    public GameObject releaseCuePrefab;
    [Tooltip("Particle prefab instantiated at the release cue position when a manual release fails.")]
    [SerializeField] private ParticleSystem failureExplosionPrefab;
    [SerializeField] private Color failureColor = new Color(1f, 0.12f, 0.05f, 1f);
    [Header("Track Rows (one per InstrumentTrack in controller order)")]
    public List<RectTransform> trackRows;
    public Dictionary<(InstrumentTrack, int), Transform> noteMarkers = new();
    [Header("First-Play Confirm FX")]
    [SerializeField] private ParticleSystem firstPlayConfirmOrbPrefab;
    [Header("Playhead Trail (Particles)")]
    [SerializeField] private bool playheadTrailEnabled = true;

// Particles per world unit traveled (higher = denser trail).
    [SerializeField] private float playheadTrailEmitPerWorldUnit = 45f;

// Safety cap so a hitch doesn't emit thousands.
    [SerializeField] private int playheadTrailMaxEmitPerFrame = 160;

// Optional: keep particle Z stable (useful if your world-space canvas depth fights sorting).
    [Tooltip("Override Z-depth for playhead trail particles. Leave as NaN to use actual world Z.")]
    [SerializeField] private float playheadTrailWorldZOverride = float.NaN;

    // Each ascended note bumps this; decays over time. Drives extra particle activity.
    private float _lineCharge01 => ascensionDirector != null ? ascensionDirector.LineCharge01 : 0f;    [Header("Playhead Pulse (Color)")]

    private float _releasePulseT = 0f; // seconds remaining
    // Flag to fire a short "release" burst when drums change / burst completes.
    private bool _pendingReleasePulse = false;
// Cached core refs (do not assume stable across scenes)
    private GameFlowManager _gfm;
    private InstrumentTrackController _ctrl;
    private DrumTrack _drum;
    private bool _hasCachedDrumAnchor;
    private double _cachedLeaderStartDspTime;
    private float _playheadBaseHeight;
    private int _forcedLeaderSteps = -1;
    private List<(InstrumentTrack, int)> deadKeys = new List<(InstrumentTrack, int)>();
    private Canvas _worldSpaceCanvas;
    private Transform _uiParent;
    private bool isInitialized;
    private readonly Dictionary<InstrumentTrack, HashSet<int>> _ghostNoteSteps = new();
    private readonly Dictionary<InstrumentTrack, Dictionary<int, Vector3>> _trackStepWorldPositions = new();

    private Vector3 _lastPlayheadParticleWorldPos;
    private bool _hasLastPlayheadParticleWorldPos;
    private readonly Dictionary<(InstrumentTrack track, int step), int> _stepBurst = new();

    private readonly Dictionary<Vehicle, GameObject> _releaseCuesByVehicle = new();

    private struct FirstPlayConfirmRequest
    {
        public Transform source;     // vehicle
        public InstrumentTrack track;
        public int step;
        public double dspTime;       // when this step first plays
        public Color color;
        public bool spawned;
        public float duration;
    }

    private readonly List<FirstPlayConfirmRequest> _firstPlayRequests = new();
    private Color stepColor;

    public void Initialize()
        {
            isInitialized = true;
            _uiParent = transform.parent;
            _worldSpaceCanvas = _uiParent.GetComponentInParent<Canvas>();

            // Ensure rows stretch horizontally so our mapping stays sane across resolutions
            foreach (var row in trackRows)
            {
                row.anchorMin = new Vector2(0f, row.anchorMin.y);
                row.anchorMax = new Vector2(1f, row.anchorMax.y);
                row.offsetMin = new Vector2(0f, row.offsetMin.y);
                row.offsetMax = new Vector2(0f, row.offsetMax.y);
            }
        }

    /// <summary>
    /// Hard visual reset for motif boundaries. This should be invoked by the single
    /// motif authority after track data has been cleared, and before the next motif
    /// begins spawning notes.
    /// </summary>
    public void BeginNewMotif_ClearAll(bool destroyMarkerGameObjects = true)
    {
        // Stop any in-progress task state (we use Update-driven task lists; clearing is sufficient).
        ascensionDirector?.ClearAllTasks();
        _stepBurst.Clear();
        _ghostNoteSteps.Clear();
        _hasLastPlayheadParticleWorldPos = false;
        _lastPlayheadParticleWorldPos = Vector3.zero;
        //_trackStepWorldPositions.Clear();
        if (destroyMarkerGameObjects) {
            // 1) Destroy any markers we know about.
            if (noteMarkers != null) {
                foreach (var kv in noteMarkers) {
                    var tr = kv.Value;
                    if (tr != null)
                        Destroy(tr.gameObject);
                }
            }

            // 2) Destroy any remaining marker-tagged objects under the track rows.
            //    (This catches stragglers that are not present in noteMarkers.)
            if (trackRows != null) {
                for (int r = 0; r < trackRows.Count; r++) {
                    var row = trackRows[r];
                    if (!row) continue;
                    // Snapshot to avoid issues if Destroy affects hierarchy during iteration.
                    var tags = row.GetComponentsInChildren<MarkerTag>(includeInactive: true);
                    for (int i = 0; i < tags.Length; i++) {
                        if (tags[i] != null && tags[i].gameObject != null)
                            Destroy(tags[i].gameObject);
                    }
                }
            }
        }
        noteMarkers?.Clear();

        // Force any temporarily overridden leader sizes back to default.
        _forcedLeaderSteps = -1;

        // Particles: clear any lingering emission so nothing looks "stuck".
        if (playheadParticles != null)
            playheadParticles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        _pendingReleasePulse = false;
        _playheadEnergy01 = 0f;
        _playheadEnergyTarget01 = 0f;
        // Cache playhead visuals (optional if you want to scale/alpha it with energy)
        if (playheadLine != null)
        {
            _playheadBaseHeight = playheadLine.sizeDelta.y;
            if (_playheadBaseHeight <= 0f)
                _playheadBaseHeight = 100f; // defensive default
        }
    }

    // Editor-safe destroy: uses DestroyImmediate in edit mode, Destroy during play.
    private void SafeDestroy(GameObject go)
    {
        if (!go) return;
#if UNITY_EDITOR
        if (!Application.isPlaying) DestroyImmediate(go);
        else Destroy(go);
#else
        Destroy(go);
#endif
    }

    private void Awake()
    {
        // If Initialize() is called later by GFM, this is still safe.
        RefreshCoreRefs(force: true);
    }

    private void OnEnable()
    {
        RefreshCoreRefs(force: true);
    }

    void Start()
    {
        // Keep Start, but do not "lock" the drum here.
        RefreshCoreRefs(force: true);
    }

    void Update()
    {
        if (!isInitialized || playheadLine == null) return;
        if (!RefreshCoreRefs(force: false)) return;
        if (_drum == null) return;
        if (!TickAnchorGuard(out double leaderStartDsp)) return;

        TickPlayheadEnergy();
        MovePlayheadLine(leaderStartDsp);
        ProcessFirstPlayConfirmFx();
        UpdatePlayheadParticleTrailWorld();
        ComputeCurrentStepState(leaderStartDsp, out int currentStep, out bool shimmer, out float maxVelocity);
        stepColor = ComputeStepColor(currentStep);
        UpdateParticleEmission(shimmer, maxVelocity);
        UpdateNoteMarkerPositions();
    }

    private float GetAscendTargetWorldY()
    {
        return ascensionDirector != null
            ? ascensionDirector.GetAscendTargetWorldY()
            : GetTopWorldY();   // fallback — should not normally be reached
    }
    public float GetTopWorldY() => GetWorldCornerY(1);
    private float GetBottomWorldY() => GetWorldCornerY(0); // bottom-left corner

    private float GetWorldCornerY(int cornerIndex)
    {
        RectTransform rt = GetComponent<RectTransform>();
        Vector3[] worldCorners = new Vector3[4];
        rt.GetWorldCorners(worldCorners);
        return worldCorners[cornerIndex].y;
    }
    public Transform GetUIParent() => _uiParent;
}
