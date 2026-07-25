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

    [Header("Tuning")]
    [Tooltip("Non-role-varying tuning knobs shared by every spawned Collectable. Per-role movement tuning stays on MusicalRoleProfile.")]
    [SerializeField] private CollectableTuning tuning;

    private void EnsureTuning()
    {
        if (tuning != null) return;
        Debug.LogError($"{gameObject.name}: CollectableTuning not assigned on the Note prefab — using ScriptableObject field defaults as a fallback. Assign a CollectableTuning asset in the Inspector.");
        tuning = ScriptableObject.CreateInstance<CollectableTuning>();
    }

    // ---- Dust collision tuning ----
    private float dustCollisionEnterImpulse => tuning.dustCollision.dustCollisionEnterImpulse;
    private float dustCollisionStayForce => tuning.dustCollision.dustCollisionStayForce;
// ---- Spawn Arrival Intro ----
    private float arrivalCarveFadeSeconds => tuning.dustCollision.arrivalCarveFadeSeconds;
    private Coroutine _spawnArrivalRoutine;
    private bool _inSpawnArrival;              // in protected flight: vehicles pass through, no collection
    private Collider2D[] _solidColliders;      // non-trigger colliders, disabled during flight

    /// <summary>
    /// The spawn flight is the note's protected first statement: it sounds at launch
    /// and streaks to its cell untouchable — vehicles pass through (solid colliders off)
    /// and OnTriggerEnter2D refuses collection until it lands.
    /// </summary>
    private void SetSpawnArrivalProtection(bool on)
    {
        _inSpawnArrival = on;

        if (_solidColliders == null)
        {
            var all = GetComponents<Collider2D>();
            int solid = 0;
            for (int i = 0; i < all.Length; i++) if (!all[i].isTrigger) solid++;
            _solidColliders = new Collider2D[solid];
            for (int i = 0, j = 0; i < all.Length; i++)
                if (!all[i].isTrigger) _solidColliders[j++] = all[i];
        }

        for (int i = 0; i < _solidColliders.Length; i++)
            if (_solidColliders[i] != null) _solidColliders[i].enabled = !on;
    }
// ---- Deposit timing knobs ----
    private float depositTravelSeconds => tuning.depositTiming.depositTravelSeconds;
    private float minDepositTravelSeconds => tuning.depositTiming.minDepositTravelSeconds;
    private float maxDepositTravelSeconds => tuning.depositTiming.maxDepositTravelSeconds;
    private float carryMinLeadSeconds => tuning.depositTiming.carryMinLeadSeconds;
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

    private float absorbGrowSeconds => tuning.absorbVfx.absorbGrowSeconds;
    private float absorbHoldSeconds => tuning.absorbVfx.absorbHoldSeconds;
    private float absorbShrinkSeconds => tuning.absorbVfx.absorbShrinkSeconds;
    private float absorbScalePeakMultiplier => tuning.absorbVfx.absorbScalePeakMultiplier;

    private GameFlowManager _gfm;
    private Transform _carryParent;
    // Idempotency flags
    private bool _handled;            // prevents double processing on trigger
    private bool _destroyNotified;    // prevents double OnDestroyed

    private float pulseSpeed => tuning.idlePulse.pulseSpeed;
    private float minAlpha => tuning.idlePulse.minAlpha;
    private float maxAlpha => tuning.idlePulse.maxAlpha;
    private float pulseScale => tuning.idlePulse.pulseScale;
    private Coroutine _pulseRoutine;
    // Collectable.cs (top-level in class)
    static readonly Dictionary<Vector2Int, Collectable> _occupantByCell = new();
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
    // play and later actually destroyed (e.g. ForceDestroyCollectablesInFlight /
    // FreezeGameplayForBridge).
    private bool _countedAsLive;
    private void MarkNoLongerLive()
    {
        if (!_countedAsLive) return;
        _countedAsLive = false;
        if (assignedInstrumentTrack != null && _s_liveByTrack.TryGetValue(assignedInstrumentTrack, out int lc))
            _s_liveByTrack[assignedInstrumentTrack] = Mathf.Max(0, lc - 1);
    }
    private DustClaimManager _dustClaims;
    private DustClaimManager GetDustClaims() => _dustClaims != null ? _dustClaims : (_dustClaims = FindAnyObjectByType<DustClaimManager>());
    Vector2Int _currentCell;

    private Transform _collector;
    private Coroutine _carryRoutine;
    private bool _inCarry;

// ---- Note Trail (Manual Release) ----
    private float trailFollowLerp => tuning.noteTrail.trailFollowLerp;
    private float trailIdleScaleMin => tuning.noteTrail.trailIdleScaleMin;
    private float trailReadyScaleMax => tuning.noteTrail.trailReadyScaleMax;
    private float trailReadyPulseSpeed => tuning.noteTrail.trailReadyPulseSpeed;
    private float trailDriftRadius => tuning.noteTrail.trailDriftRadius;
    private float trailDriftSpeed => tuning.noteTrail.trailDriftSpeed;
    private float trailDriftFocusMul => tuning.noteTrail.trailDriftFocusMul;

    private Vector3 _trailWorldTarget;
    private bool _trailFollowActive;
    private float _trailReleasePulse01; // 0=idle, 1=release imminent
    private Coroutine _trailFollowRoutine;
    private Vector3 _trailBaseScale;
    private bool _trailBaseScaleCaptured;
    private float _trailDriftPhase;      // randomised per-instance so collectables don't orbit in sync
    public bool ReportedCollected { get; private set; }

    /// <summary>
    /// Call before manually consuming a collectable so OnCollectableDestroyed does not
    /// double-decrement burst remaining. The commit path already handles burst accounting.
    /// </summary>
    public void MarkAsReportedCollected() => ReportedCollected = true;
    public event Action OnDestroyed;               // for bookkeeping (track cleans lists, etc.)
    public void BindMarkerAtSpawn(Transform marker, int anchorStep)
    {
        ribbonMarker = marker;
    }

    [Header("Sequencer Link")]
    [Tooltip("Candidate step anchors this collectable may resolve to when attaching to the track ribbon.")]
    public List<int> sharedTargetSteps = new List<int>();
    // --- Movement (intent-driven velocity steering) ---
    [NonSerialized] public float spawnVelocity127 = 100f; // authored MIDI velocity from the riff template; modulates the role's speed ±25%

    private Camera _cam;
    private Rigidbody2D _rb;
    private float _profileSpeedMul = 1f;    // profile collectableSpeed — global multiplier on all motion
    private float _speed;                   // tuning.baseMotion.baseLinearSpeed × multiplier × velocity modulation, resolved at Initialize
    private float _steerAccel = 20f;        // _speed / collectableAccelSeconds — full speed on every note length
    private System.Random _rng;
    private MusicalRole _role = MusicalRole.None;
    private MusicalRoleProfile _roleProfile;
    private float _orbitalChirality = 1f;   // +1 CCW, -1 CW — locked per instance
    private bool _grooveBurstActive = true;
    private float _groovePhaseTimer = 0f;
    private Vector2 _intentDir = Vector2.zero; // current directional intent (unit)
    private float _intentTimer;                // counts down to the next intent pick
    private float _intentInterval = 1f;        // seconds; the note's duration in musical time
    private float _bounceRecoverTimer;         // > 0 right after a dust hit — steering weakened
    private int _bassChargeSign = 1;           // +1 up / -1 down, alternates per intent
    private float _bassChargeSpeed;            // per-pulse: spans to the opposite cage edge in one note duration
    private float _accelSeconds = 0.12f;       // time-to-speed; steering accel scales with the desired speed
    private float _leadHeadingDeg;             // Lead: drifting base heading; swerve oscillates across it
    private float _leadSwervePhase;            // Lead: radians into the sine weave
    private Vector2 _homeWorld;                // arrival position — the note's gravitational anchor
    private float _homeFreeRadius;             // world units of pull-free movement around home
    private float _fleeRadiusWorld;            // vehicle distance that triggers fleeing (0 = fearless)
    private bool _isFleeing;                   // hysteresis: fleeing until 1.5× the trigger radius
    private DrumTrack _boundStepDrums;         // OnStepChanged subscription for the timeline ghost pulse
    private Vector2 _moveBoundsMin;            // play-area clamp (bottom sits above the ascension-line UI band)
    private Vector2 _moveBoundsMax;
    private bool _hasMoveBounds;

    public int GetNote() => assignedNote;

    public void ApplyTrackVisuals(InstrumentTrack track)
    {
        if (track == null) return;
        if (TryGetComponent(out CollectableParticles particleScript))
            particleScript.ConfigureByDuration(noteDurationTicks, track);

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
        EnsureTuning();
        assignedNote              = note;
        noteDurationTicks         = duration;
        assignedInstrumentTrack   = track;

        sharedTargetSteps = (steps != null && steps.Count > 0)
            ? new List<int>(steps)
            : new List<int>();

        if (sharedTargetSteps.Count == 0)
            Debug.LogWarning($"{gameObject.name} - No target steps provided.");

        _role = assignedInstrumentTrack != null ? assignedInstrumentTrack.assignedRole : MusicalRole.None;
        // Prefer the motif-selected profile installed on the track; the library keeps only
        // the first-loaded asset per role, so it can't see per-motif tuning.
        _roleProfile = (assignedInstrumentTrack != null && assignedInstrumentTrack.ActiveProfile != null)
            ? assignedInstrumentTrack.ActiveProfile
            : MusicalRoleProfileLibrary.GetProfile(_role);
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

        if (_rb == null) TryGetComponent(out _rb);
        _rng ??= new System.Random(StableSeed());
        _leadHeadingDeg = (float)(_rng.NextDouble() * 360.0);

        // Speed: global role multiplier × base linear speed × MIDI-velocity modulation (±25%).
        _profileSpeedMul = _roleProfile != null ? Mathf.Max(0f, _roleProfile.collectableSpeed) : 1f;
        float velMod = Mathf.Lerp(0.75f, 1.25f, Mathf.Clamp01(spawnVelocity127 / 127f));
        _speed = tuning.baseMotion.baseLinearSpeed * _profileSpeedMul * velMod;

        // Steering accel: reach full speed in collectableAccelSeconds, on every note length.
        // Duration-derived pulse speeds (bass slam, harmony surge) can exceed _speed, so
        // MovementRoutine scales the accel with the desired speed using _accelSeconds.
        _accelSeconds = Mathf.Max(0.02f, _roleProfile != null ? _roleProfile.collectableAccelSeconds : 0.12f);
        _steerAccel = _speed / _accelSeconds;

        // Intent interval: the note's duration in musical time (ticks @ 480/quarter).
        var drums = assignedInstrumentTrack != null ? assignedInstrumentTrack.drumTrack : null;
        float bpm = (drums != null && drums.drumLoopBPM > 1f) ? drums.drumLoopBPM : 120f;
        _intentInterval = Mathf.Clamp(noteDurationTicks * 60f / (bpm * 480f), 0.25f, 6f);
        _intentTimer = 0f; // rest until the playhead first crosses this note's step

        // Timeline ghost pulse: when the playhead crosses this note's step, it sounds
        // softly and dances for its duration. Unbound at pickup and on destroy/disable.
        if (drums != null)
        {
            _boundStepDrums = drums;
            drums.OnStepChanged += HandleTimelineStep;
        }

        // Home tether: the arrival position (the deterministic step/pitch cell) anchors the note.
        _homeWorld = _rb != null ? _rb.position : (Vector2)transform.position;
        float cellSize = drums != null ? Mathf.Max(0.001f, drums.GetCellWorldSize()) : 1f;
        float homeRadiusCells = _roleProfile != null ? _roleProfile.collectableHomeRadiusCells : 1.5f;
        _homeFreeRadius = homeRadiusCells * cellSize;
        float fleeRadiusCells = _roleProfile != null ? _roleProfile.collectableFleeRadiusCells : 2f;
        _fleeRadiusWorld = fleeRadiusCells * cellSize;

        // Movement stays inside the play area (its bottom is above the ascension-line band),
        // cached once — TryGetPlayAreaWorld can allocate, and the area is stable in-session.
        if (drums != null && drums.TryGetPlayAreaWorld(out var playArea))
        {
            const float boundsPad = 0.4f;
            _moveBoundsMin = new Vector2(playArea.left + boundsPad, playArea.bottom + boundsPad);
            _moveBoundsMax = new Vector2(playArea.right - boundsPad, playArea.top - boundsPad);
            _hasMoveBounds = _moveBoundsMax.x > _moveBoundsMin.x && _moveBoundsMax.y > _moveBoundsMin.y;
        }

        StartCoroutine(MovementRoutine());

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
        UnbindTimelineStep();
        UnregisterOccupant();
        GetDustClaims()?.ReleaseOwner($"Collectable#{_id}");
        NotifyDestroyedOnce();
    }

    private void OnDisable()
    {
        UnbindTimelineStep();
        UnregisterOccupant();
        GetDustClaims()?.ReleaseOwner($"Collectable Released");
        NotifyDestroyedOnce();
    }
}
