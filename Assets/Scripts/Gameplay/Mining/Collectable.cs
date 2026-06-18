using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
public partial class Collectable : MonoBehaviour
{
    private static int _nextId;
    private readonly int _id = System.Threading.Interlocked.Increment(ref _nextId);

    [Header("Core References")]
    [Tooltip("Track that spawned this collectable and receives collection callbacks.")]
    public InstrumentTrack assignedInstrumentTrack; // 🎼 The track that spawned this collectable

    [Tooltip("Primary sprite used for glow, tint, and release feedback.")]
    public SpriteRenderer energySprite;

    [Tooltip("Burst identifier used by spawn/accounting systems.")]
    public int burstId;

    [Tooltip("Authoritative burst bin assigned at spawn; used to resolve absolute sequencer step on deposit.")]
    public int intendedBin = -1;

    [Header("Spawn Intent")]
    public bool isTrappedInDust = false;
    // ---- Dust collision tuning ----
    public float dustCollisionEnterImpulse = 0.85f;
    public float dustCollisionStayForce = 2.25f;
// ---- Spawn Arrival Intro ----
    [Header("Spawn Arrival")]
    [SerializeField] private float spawnArrivalSeconds = 5.0f;
    [SerializeField] private AnimationCurve spawnArrivalEase = null;
    [SerializeField] private float spawnArrivalConeAngleDeg = 25f;
    [SerializeField] private float spawnArrivalNoiseStrength = 3.2f;
    [SerializeField] private float spawnArrivalNoiseFrequency = 1.2f;
    private Coroutine _spawnArrivalRoutine;
// ---- Deposit timing knobs ----
    [Header("Deposit Timing")]
    [Tooltip("How long the tether travel should take under normal circumstances (seconds).")]
    [SerializeField] private float depositTravelSeconds = 1.6f;   // readable default

    [Tooltip("Hard minimum travel time, unless the deposit moment is sooner than this.")]
    [SerializeField] private float minDepositTravelSeconds = 1.0f;

    [Tooltip("Hard maximum travel time (prevents overly slow zips).")]
    [SerializeField] private float maxDepositTravelSeconds = 2.2f;

    [Tooltip("Maximum readable orbit time before launch (seconds).")]
    [SerializeField] private float maxOrbitSeconds = 1.10f;

    [Tooltip("If the deposit moment is this soon, skip orbit and just travel (seconds).")]
    [SerializeField] private float imminentNoOrbitThreshold = 0.28f;

    [Tooltip("Small safety margin in DSP scheduling (seconds).")]
    [SerializeField] private float dspEpsilon = 0.010f;

    [Tooltip("Minimum lead time so travel doesn't start 'late' due to frame jitter.")]
    [SerializeField] private float carryMinLeadSeconds = 0.02f;
    public int amount = 5;
    private int noteDurationTicks = 4; // 🎵 Default to 1/16th note duration
    private int assignedNote;          // 🎵 The MIDI note value
    public Transform ribbonMarker;           // assigned when spawned
    public int intendedStep = -1;       // set at spawn (authoritative target)

    [Header("Carry (Parented)")]
    [SerializeField] private CarrySettings carrySettings = new CarrySettings();

    [Serializable]
    public class CarrySettings
    {
        [Tooltip("Local hover offset while parented to the collector.")]
        public Vector3 localOffset = new Vector3(0f, 0.65f, 0f);

        [Tooltip("Small randomized wobble applied to the carry offset.")]
        [Range(0f, 0.2f)]
        public float localOffsetJitter = 0.05f;
    }
    private GameFlowManager _gfm;
    private Transform _carryParent;
    // Idempotency flags
    private bool _handled;            // prevents double processing on trigger
    private bool _destroyNotified;    // prevents double OnDestroyed

    [SerializeField] private float pulseSpeed = 1.6f;
    [SerializeField] private float minAlpha = 0.25f;
    [SerializeField] private float maxAlpha = 0.65f;
    [SerializeField] private float pulseScale = 1.08f;
    private Coroutine _pulseRoutine;
    // Collectable.cs (top-level in class)
    static readonly Dictionary<Vector2Int, Collectable> _occupantByCell = new();
    static readonly Dictionary<Vector2Int, Collectable> _reservedByCell = new();
    static readonly object _lock = new object(); // optional; Unity main thread makes this mostly unnecessary
    private static readonly Dictionary<InstrumentTrack, int> _s_liveByTrack = new();

    public static bool AnyLiveFromOtherTracks(InstrumentTrack excl)
    {
        foreach (var kv in _s_liveByTrack)
            if (kv.Key != excl && kv.Value > 0) return true;
        return false;
    }

    public static bool AnyLiveForTrack(InstrumentTrack track)
        => _s_liveByTrack.TryGetValue(track, out var count) && count > 0;

    // Guards against double-decrementing _s_liveByTrack when a collectable is removed from
    // play via a non-Destroy path (e.g. DarkTimeoutRoutine) and later actually destroyed
    // (e.g. ForceDestroyCollectablesInFlight / FreezeGameplayForBridge).
    private bool _countedAsLive;
    private void MarkNoLongerLive()
    {
        if (!_countedAsLive) return;
        _countedAsLive = false;
        if (assignedInstrumentTrack != null && _s_liveByTrack.TryGetValue(assignedInstrumentTrack, out int lc))
            _s_liveByTrack[assignedInstrumentTrack] = Mathf.Max(0, lc - 1);
    }
    private DustClaimManager _dustClaims;
    private DustClaimManager GetDustClaims() => _dustClaims != null ? _dustClaims : (_dustClaims = FindObjectOfType<DustClaimManager>());
    private static readonly Collider2D[] _dustProbeHits = new Collider2D[16];
    Vector2Int _currentCell;
    Vector2Int _reservedCell;
    // ---- Autonomy (Loop Boundary "Idea") ----
    [Header("Autonomy (Loop Boundary Idea)")]
    [SerializeField] private bool useLoopBoundaryIdea = true;

    [Tooltip("How many grid cells ahead we evaluate when choosing an idea direction.")]
    [SerializeField] private int ideaLookaheadCells = 5;

    [Tooltip("Idea direction bias strength as a fraction of base move speed.")]
    [Range(0f, 1.5f)]
    [SerializeField] private float ideaBiasStrength = 0.55f;

    [Tooltip("How quickly the note turns toward its new idea direction.")]
    [SerializeField] private float ideaTurnLerp = 6.0f;

    [Tooltip("Small turbulence layered on top of idea bias.")]
    [Range(0f, 1f)]
    [SerializeField] private float microTurbulenceStrength = 0.20f;

    private Vector2 _ideaDir = Vector2.zero;
    private Vector2 _ideaDirSmoothed = Vector2.zero;
    private DrumTrack _boundDrumTrack;

// ---- Carry Orbit ----
    [Header("Carry Orbit")]
    [SerializeField] private float carryOrbitRadius = 0.55f;
    [SerializeField] private float carryOrbitAngularSpeed = 3.0f; // radians/sec
    [SerializeField] private float carryOrbitFollowLerp = 18f;
    [SerializeField] private float carryOrbitVerticalBias = 0.05f; // small upward bias so it reads above vehicle

    private int _carryOrbitIndex = -1;
    private bool _registeredInCarryOrbit;

// Vehicle transform -> carried collectables (for spacing)
    private static readonly Dictionary<Transform, List<Collectable>> _carryOrbitByCollector = new();    
    private Transform _collector;
    private Coroutine _carryRoutine;
    private bool _inCarry;

// ---- Note Trail (Manual Release) ----
    [Header("Note Trail (Manual Release)")]
    [Tooltip("How quickly the note lerps toward its trail slot position (world space).")]
    [SerializeField] private float trailFollowLerp = 12f;

    [Tooltip("Scale pulse min when idle in trail.")]
    [SerializeField] private float trailIdleScaleMin = 0.85f;

    [Tooltip("Scale pulse max at full release-ready glow.")]
    [SerializeField] private float trailReadyScaleMax = 1.25f;

    [Tooltip("Glow pulse speed (radians/sec) when release window is near.")]
    [SerializeField] private float trailReadyPulseSpeed = 6f;

    [Header("Trail Drift (Autonomous Motion)")]
    [Tooltip("Max world-space radius of idle drift orbit around the slot position.")]
    [SerializeField] private float trailDriftRadius = 0.18f;

    [Tooltip("Speed of the drift orbit (radians/sec). Each collectable gets a random phase.")]
    [SerializeField] private float trailDriftSpeed = 1.1f;

    [Tooltip("How strongly the energy is pulled toward its tether's far-end (the note world). 0 = no pull.")]
    [SerializeField] private float trailTetherPull = 0.06f;

    [Tooltip("Drift radius shrinks to this fraction as release pulse approaches 1 (energy focuses).")]
    [SerializeField] private float trailDriftFocusMul = 0.15f;

    private Vector3 _trailWorldTarget;
    private bool _trailFollowActive;
    private float _trailReleasePulse01; // 0=idle, 1=release imminent
    private Coroutine _trailFollowRoutine;
    private Vector3 _trailBaseScale;
    private bool _trailBaseScaleCaptured;
    private float _trailDriftPhase;      // randomised per-instance so collectables don't orbit in sync
    bool _hasReservation;
    public bool ReportedCollected { get; private set; }

    /// <summary>
    /// Call before manually consuming a collectable so OnCollectableDestroyed does not
    /// double-decrement burst remaining. The commit path already handles burst accounting.
    /// </summary>
    public void MarkAsReportedCollected() => ReportedCollected = true;
    public delegate void OnCollectedHandler(int duration, float force);
    public event OnCollectedHandler OnCollected;   // informational; does not call the track
    public event Action OnDestroyed;               // for bookkeeping (track cleans lists, etc.)
    public void BindMarkerAtSpawn(Transform marker, int anchorStep)
    {
        ribbonMarker = marker;
    }

    [Header("Sequencer Link")]
    [Tooltip("Candidate step anchors this collectable may resolve to when attaching to the track ribbon.")]
    public List<int> sharedTargetSteps = new List<int>();
    // --- Movement (grid-aware, dust-adjacent) ---
    [Header("Movement")]
    [SerializeField] private float minSpeed = 0.8f;     // world units/sec for longest notes
    [SerializeField] private float maxSpeed = 3.5f;     // world units/sec for shortest notes
    [SerializeField] private float dustAdjacencyProbe = 0.42f; // radius used to detect nearby dust

    private Camera _cam;
    private Rigidbody2D _rb;
    private float _speed;
    private System.Random _rng;
    private MusicalRole _role = MusicalRole.None;
    private MusicalRoleProfile _roleProfile;
    private float _orbitalChirality = 1f;   // +1 CCW, -1 CW — locked per instance
    private float _leadRefreshTimer = 0f;
    private bool _grooveBurstActive = true;
    private float _groovePhaseTimer = 0f;
    private float _openSpaceRelocateTimer = 0f;
    private bool  _isSprinting = false;
    private float _sprintTimer = 0f;

    public int GetNote() => assignedNote;
    public bool IsDark { get; private set; } = false;

    public void ApplyTrackVisuals(InstrumentTrack track)
    {
        if (track == null) return;
        if (TryGetComponent(out CollectableParticles particleScript))
            particleScript.ConfigureByDuration(1, track);

        if (energySprite != null)
        {
            var c = track.DisplayColor;
            c.a = Mathf.Clamp01(maxAlpha);
            energySprite.color = c;

            if (_pulseRoutine != null) StopCoroutine(_pulseRoutine);
            _pulseRoutine = StartCoroutine(PulseEnergySprite());
        }

        var explode = GetComponent<Explode>();
        if (explode != null)
            explode.SetTint(track.DisplayColor, multiply: true);
    }

    private void Initialize(int note, int duration, InstrumentTrack track, NoteSet noteSet, List<int> steps)
    {
        assignedNote              = note;
        noteDurationTicks         = duration;
        assignedInstrumentTrack   = track;

        sharedTargetSteps = (steps != null && steps.Count > 0)
            ? new List<int>(steps)
            : new List<int>();

        if (sharedTargetSteps.Count == 0)
            Debug.LogWarning($"{gameObject.name} - No target steps provided.");

        _role = assignedInstrumentTrack != null ? assignedInstrumentTrack.assignedRole : MusicalRole.None;
        _roleProfile = MusicalRoleProfileLibrary.GetProfile(_role);
        _orbitalChirality = (_id & 1) == 0 ? 1f : -1f;
        if (assignedInstrumentTrack != null)
        {
            _s_liveByTrack[assignedInstrumentTrack] =
                (_s_liveByTrack.TryGetValue(assignedInstrumentTrack, out int lc) ? lc : 0) + 1;
            _countedAsLive = true;
        }

        ApplyTrackVisuals(track);
        if (assignedInstrumentTrack == null)
            Debug.LogError($"Collectable {gameObject.name} - assignedInstrumentTrack is NULL on initialization!");

        StartCoroutine(DarkTimeoutRoutine(track));
        if (_rb == null) TryGetComponent(out _rb);
        _rng ??= new System.Random(StableSeed());
        StartCoroutine(MovementRoutine());
        TryBindLoopBoundary();
        HandleLoopBoundaryIdea();

        ClearReservation();

        var dt = assignedInstrumentTrack ? assignedInstrumentTrack.drumTrack : null;
        if (dt != null)
        {
            _currentCell = dt.CellOf(transform.position);
            RegisterOccupant(_currentCell);
            GetDustClaims()?.ClaimCell($"Collectable#{_id}", _currentCell, DustClaimType.Occupancy, seconds: -1f);

            if (_gfm == null) _gfm = GameFlowManager.Instance;
            var dustGen = _gfm?.dustGenerator;
            if (dustGen != null)
                dustGen.CreateJailCenterForCollectable(_currentCell, holdSeconds: 0f, ownerId: _id);
        }
    }

    private void NotifyDestroyedOnce()
    {
        if (_destroyNotified) return;
        _destroyNotified = true;
        OnDestroyed?.Invoke();
    }

    private void OnEnable()
    {
        _handled = false;
        _destroyNotified = false;
    }

    private void OnDestroy()
    {
        MarkNoLongerLive();
        ClearReservation();
        UnbindLoopBoundary();
        UnregisterOccupant();
        UnregisterCarryOrbit();
        GetDustClaims()?.ReleaseOwner($"Collectable#{_id}");
        NotifyDestroyedOnce();
    }

    private void OnDisable()
    {
        ClearReservation();
        UnbindLoopBoundary();
        UnregisterOccupant();
        GetDustClaims()?.ReleaseOwner($"Collectable Released");
        NotifyDestroyedOnce();
    }
}
