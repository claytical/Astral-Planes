using System;
using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using Random = UnityEngine.Random;

public partial class Vehicle : MonoBehaviour
{

    private static int _nextId;
    private readonly int _id = System.Threading.Interlocked.Increment(ref _nextId);

    private bool  _isDead;
    private bool  _isActivePlow;
    private float _plowVelocityDrain;
    private float _lastPressureFactor;
    private static readonly Collider2D[] _pressureHits = new Collider2D[8];
    private int _mineNodeLayerMask;
    private ContactFilter2D _pressureFilter;
    public ShipMusicalProfile profile;
    [SerializeField] private VehicleConfig vehicleConfig;
    [HideInInspector] public float capacity = 10f;
    private float _burnRateMultiplier = 1f; // Multiplier for the burn rate based on trigger pressure
    private bool _boostCostFree;           // When true, boost ignores energy requirement and skips burn
    [HideInInspector] public float arcadeMaxSpeed = 14f;
    private float _cumulativeEnergySpent = 0f;
    [HideInInspector] [SerializeField] private int vehicleKeepClearRadiusCells = 0;
    private float _nextVehicleKeepClearRefreshAt = 0f;

    [Tooltip("Child SpriteRenderer clone of the vehicle art. Normally hidden. Scales up as placement becomes available.")]
    [SerializeField] private SpriteRenderer soulSprite;

    [Header("Gravity Void Detection")]
    [SerializeField] private LayerMask gravityVoidMask; // set in inspector; 0 disables gravity-void detection

    [Header("Boost Collision")]
    [SerializeField] private LayerMask dustLayerMask; // assign to dust layer so boost ignores dust colliders

    private Vector3 _lastSafeWorld;
    private Vector2Int _lastSafeCell;
    private bool _hasSafeAnchor = false;

    private float _lastRecoverAt = -999f;
    private float _timeStuckInVoid = 0f;

    private GameFlowManager gfm;
    float   _lastMoveStamp;
    private Vector2 _moveInput;
    public float energyLevel;
    public bool boosting = false;
    private Vector2 _lastNonZeroInput; // remembers the last aim direction
    public GameObject trail; // Trail prefab
    public AudioClip thrustClip;
    public PlayerStats playerStatsUI; // Reference to the PlayerStats UI element
    public PlayerStatsTracking playerStats;

    private GameObject activeTrail; // Reference to the currently active trail instance
    private TrailRenderer _activeTrailRenderer;
    public Rigidbody2D rb;
    private AudioManager audioManager;
    private SpriteRenderer baseSprite;
    private Vector3 lastPosition;
    private DrumTrack drumTrack;
    private double loopStartDSPTime;
    private float _lastDamageTime = -1f;
    private Coroutine flickerPulseRoutine;
    private bool isFlickering = false;



    private Coroutine _spawnRestPocketCo;

    private Color _vehicleDefaultColor = Color.white;
    private struct ArmedRelease
    {
        public PendingCollectedNote note;
        public int targetAbsStep;
        public int totalAbsSteps;
        public double gapDurationDsp;   // wall-clock seconds from arm moment to target; stable across bin expansion
        public float releaseVelocity;   // timing-based velocity captured at the moment the player armed the release
    }

    // Armed releases commit automatically as the playhead reaches the target.
    private readonly Queue<ArmedRelease> _armedReleases = new Queue<ArmedRelease>(8);
    // Single Vehicle-owned tether — created on first collect, destroyed when all notes gone.
    private NoteTether _vehicleTether;
    private double _lastRawAbsStep = 0.0;
    private bool _hasLastRawAbsStep = false;
    private bool _releaseButtonHeld;
    private bool _lastArmWasFromHold;

    public void SetReleaseButtonHeld(bool held)
    {
        _releaseButtonHeld = held;
    }

    [Header("Release Cue")]
    [Tooltip("Optional VehicleReleaseCue component on this vehicle (or a child). Drives the ring fill and beat-dot countdown.")]
    [SerializeField] private VehicleReleaseCue releaseCue;

    // ---- Note Trail Position History ----
    // Ring buffer of recent world positions (populated in Update)
    private Vector3[] _posHistory;
    private int _posHistoryHead;
    private int _posHistoryCount;
    private float _posHistoryAccum; // distance accumulator for spacing
    private Vector3 _posHistoryLast;
    private Vector3 _lastTravelDir = Vector3.down; // fallback tail direction when stationary

    public bool ManualNoteReleaseEnabled => true;

    public struct PendingCollectedNote
    {
        public InstrumentTrack track;
        public Collectable collectable;
        public int authoredAbsStep;      // authored/assigned absolute step at pickup (may be -1 if unknown)
        public int authoredLocalStep;    // authored step within bin (0..binSize-1)
        public int collectedMidi;        // collectable.GetNote()
        public int durationTicks;
        public float velocity127;
        public int authoredRootMidi;     // root in the same register as collectedMidi (used for mismatch substitution)
        public int burstId;              // used for decrement/completion logic at commit
    }

    private readonly Queue<PendingCollectedNote> _pendingNotes = new Queue<PendingCollectedNote>();
    private readonly HashSet<InstrumentTrack> _carriedTracksScratch = new HashSet<InstrumentTrack>();
    private readonly HashSet<int> _spokenForScratch = new HashSet<int>();

    private InstrumentTrack _lockedTrack = null;
    private static int _s_vehiclesCarrying = 0;
    private static readonly Dictionary<InstrumentTrack, int> _s_vehiclesCarryingByTrack = new();

    // ------------------------------------------------------------
    // Manual-release cue (visual guidance)
    // ------------------------------------------------------------
    private void Awake()
    {
        baseSprite = GetComponent<SpriteRenderer>();
    }

    void Start()
        {
            rb = GetComponent<Rigidbody2D>();
            gfm = GameFlowManager.Instance;
            _mineNodeLayerMask = LayerMask.GetMask("DiscoveryTrackNode");
            _pressureFilter.SetLayerMask(_mineNodeLayerMask);
            _pressureFilter.useTriggers = Physics2D.queriesHitTriggers;
            var col = GetComponent<Collider2D>();
            if (GameFlowManager.VerboseLogging) Debug.Log(
                $"[VEHICLE:INIT] '{name}' layer={gameObject.layer} " +
                $"col={(col ? col.GetType().Name : "NULL")} col.isTrigger={(col ? col.isTrigger : false)} " +
                $"rb={(rb ? rb.bodyType.ToString() : "NULL")}",
                this
            );

            if (baseSprite != null)
                _vehicleDefaultColor = baseSprite.color;

            if (soulSprite != null)
            {
                soulSprite.transform.localScale = Vector3.one * profile.soulMinScale;

                var c = soulSprite.color;
                c.a = 0f;
                soulSprite.color = c;
                soulSprite.enabled = false;
            }
            audioManager = GetComponent<AudioManager>();
            loopStartDSPTime = gfm.activeDrumTrack.startDspTime;
            ApplyShipProfile(profile);
            if (rb == null)
            {
                Debug.LogError("❌ Rigidbody2D component is missing.");
                enabled = false;
                return;
            }
            rb.gravityScale    = 0f;
            rb.linearVelocity  = Vector2.zero;
            rb.angularVelocity = 0f;

            if (audioManager == null)
            {
                Debug.LogError("❌ AudioManager component is missing.");
                enabled = false;
                return;
            }

            if (playerStatsUI == null)
            {
                Debug.LogError("❌ PlayerStats UI reference not assigned.");
                enabled = false;
                return;
            }

            if (playerStats == null)
            {
                Debug.LogError("❌ PlayerStatsTracking component is missing.");
                enabled = false;
                return;
            }

            energyLevel = capacity; // Start at full energy
            lastPosition = transform.position;
            SyncEnergyUI();

            // Hook step events for the release-cue beat countdown.
            var drum = gfm?.activeDrumTrack;
            if (drum != null) drum.OnStepChanged += OnStepTickForReleaseCue;

            // --- Spawn rest pocket ---
            // The vehicle often spawns inside a solid dust tile by design (teaches boosting),
            // but we still need a tiny free volume so we don't start interpenetrating colliders.
            if (vehicleConfig.carveSpawnRestPocket)
            {
                if (_spawnRestPocketCo != null) StopCoroutine(_spawnRestPocketCo);
                _spawnRestPocketCo = StartCoroutine(Co_CarveSpawnRestPocket());
            }
        }
    void FixedUpdate() {

        if (_isDead) return;

        float dt = Time.fixedDeltaTime;

        _isActivePlow = false;   // always reset; DoPlowTick sets true only when cells are carved

        DoPlowTick();
        RefreshVehicleKeepClearIfNeeded();

        if (!boosting) _plowVelocityDrain = 0f;
        // --- Loop boundary check (null-safe) ---
        if (drumTrack != null)
        {
            float  loopLen = drumTrack.GetLoopLengthInSeconds();
            double dspNow  = AudioSettings.dspTime;
            if (dspNow - loopStartDSPTime >= loopLen)
            {
                loopStartDSPTime = dspNow;
            }
        }

        // --- Input hygiene: if Move() hasn't been called recently, treat as zero ---
        if (Time.time - _lastMoveStamp > vehicleConfig.inputTimeout) _moveInput = Vector2.zero;

        UpdatePressureInstability();
        UpdateMovementAndFuel(dt);

// Stats + audio

    UpdateDistanceCovered();
    ClampAngularVelocity();
    audioManager.AdjustPitch(rb.linearVelocity.magnitude * 0.1f);
    if (vehicleConfig.enableRecovery)
    {
        UpdateSafeAnchor();
        RecoverIfNeeded();
    }

}

    private void UpdatePressureInstability()
    {
        if (profile.pressureInstabilityRadius <= 0f || profile.pressureInstabilityStrength01 <= 0f)
        {
            _lastPressureFactor = 0f;
            return;
        }
        int pCount = Physics2D.OverlapCircle(rb.position, profile.pressureInstabilityRadius,
                                             _pressureFilter, _pressureHits);
        if (pCount == 0) { _lastPressureFactor = 0f; return; }

        float minSqrDist = float.MaxValue;
        for (int i = 0; i < pCount; i++)
        {
            if (_pressureHits[i] == null) continue;
            float sqr = ((Vector2)_pressureHits[i].transform.position - rb.position).sqrMagnitude;
            if (sqr < minSqrDist) minSqrDist = sqr;
        }
        _lastPressureFactor = 1f - Mathf.InverseLerp(0f, profile.pressureInstabilityRadius, Mathf.Sqrt(minSqrDist));
    }

    private void UpdateMovementAndFuel(float dt)
    {
        bool hasInput = _moveInput.sqrMagnitude > 0.0001f;

        if (hasInput || boosting)
        {
            Vector2 steerDir = hasInput
                ? _moveInput.normalized
                : (_lastNonZeroInput.sqrMagnitude > 0f ? _lastNonZeroInput : (Vector2)transform.up);

            float authority = GetEffectiveAuthority();
            Vector2 effectiveDir = (authority >= 1f || rb.linearVelocity.sqrMagnitude < 0.01f)
                ? steerDir
                : Vector2.Lerp(rb.linearVelocity.normalized, steerDir, authority).normalized;

            Vector2 desiredVel = effectiveDir * arcadeMaxSpeed;
            float   accelUsed  = boosting ? profile.arcadeBoostAccel : profile.arcadeAccel;
            if (accelUsed > 0f)
            {
                Vector2 dv      = desiredVel - rb.linearVelocity;
                float   maxStep = accelUsed * dt;
                rb.linearVelocity += (dv.sqrMagnitude > maxStep * maxStep) ? dv.normalized * maxStep : dv;
            }
        }
        else
        {
            Vector2 v = rb.linearVelocity;
            if (v.sqrMagnitude > 0f && profile.coastBrakeForce > 0f)
                rb.AddForce(-v * profile.coastBrakeForce, ForceMode2D.Force);
            if (v.magnitude < profile.stopSpeed && Mathf.Abs(rb.angularVelocity) < profile.stopAngularSpeed)
            {
                rb.linearVelocity  = Vector2.zero;
                rb.angularVelocity = 0f;
            }
        }

        // Plow drag: applied after acceleration so high-resistance cells create real terminal velocity
        if (_plowVelocityDrain > 0f && rb.linearVelocity.sqrMagnitude > 0.01f)
            rb.linearVelocity *= Mathf.Max(0f, 1f - _plowVelocityDrain);

        if (boosting && energyLevel > 0f && !_boostCostFree)
            ConsumeEnergy(_burnRateMultiplier * profile.burnRate);

        if (rb.linearVelocity.sqrMagnitude > 0.0001f)
            rb.rotation = Mathf.Atan2(rb.linearVelocity.y, rb.linearVelocity.x) * Mathf.Rad2Deg - 90f;
    }

    private void Update()
    {
        RecordPositionHistory();
        TickManualReleaseCue();
        TickNoteTrail();
    }

    public void SetDrumTrack(DrumTrack drums)
        {
            drumTrack = drums;
        }

    private float GetEffectiveAuthority()
    {
        float auth = profile.directionalAuthority01;

        if (profile.plowSteeringPenalty01 > 0f && _isActivePlow)
            auth *= 1f - profile.plowSteeringPenalty01;

        if (profile.pressureInstabilityStrength01 > 0f && _lastPressureFactor > 0f)
            auth *= 1f - _lastPressureFactor * profile.pressureInstabilityStrength01;

        return Mathf.Clamp01(auth);
    }

    public void Move(Vector2 direction)
    {
        if (direction.magnitude < profile.inputDeadzone) direction = Vector2.zero;

        _moveInput     = direction;
        if (direction.sqrMagnitude > 0f) _lastNonZeroInput = direction.normalized;
        _lastMoveStamp = Time.time;

        // Optional: face input immediately if we’re currently stopped
        if (rb && direction.sqrMagnitude > 0.0001f && rb.linearVelocity.sqrMagnitude < 0.0001f)
        {
            float angle = Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg - 90f;
            rb.rotation = angle;
        }
    }
    private void Fly()
        {
            if (trail != null && activeTrail == null)
            {
                activeTrail = Instantiate(trail, transform);
                _activeTrailRenderer = activeTrail.GetComponent<TrailRenderer>();
            }

            if (_activeTrailRenderer != null)
                _activeTrailRenderer.emitting = true;
        }
    public void SetBoostFree(bool free)
    {
        _boostCostFree = free;
    }

    public void TurnOnBoost(float triggerValue)
    {

        if ((energyLevel > 0 || _boostCostFree) && !boosting)        {
            boosting = true;

            if (audioManager != null && thrustClip != null)
                audioManager.PlayLoopingSound(thrustClip, .1f);
            Fly();
        }
        _burnRateMultiplier = Mathf.Max(0.2f, triggerValue);
    }
    public void TurnOffBoost()
    {
        if (audioManager != null)
        {
            audioManager.StopSound();
        }
        boosting = false;
        _isActivePlow = false;
        _burnRateMultiplier = 0f; // Reset the multiplier when not boosting

        if (_activeTrailRenderer != null)
            _activeTrailRenderer.emitting = false;
    }
    void OnDisable()
    {
        if (gfm == null || gfm.dustGenerator == null) return;

        gfm.dustGenerator.ReleaseVehicleKeepClear(_id);
    }
    private void OnDestroy()
    {
        ReleaseTrackLock();

        // Unhook step-tick before the object is gone.
        if (gfm == null) gfm = GameFlowManager.Instance;
        var drum = gfm?.activeDrumTrack;
        if (drum != null) drum.OnStepChanged -= OnStepTickForReleaseCue;

        var gen = (gfm != null) ? gfm.dustGenerator : null;
        if (gen == null) return;

        gen.ReleaseVehicleKeepClear(_id);
    }
    private void UpdateDistanceCovered()
    {
        // Calculate the distance covered since the last frame
        float distance = Vector3.Distance(transform.position, lastPosition);
        playerStats.distanceCovered += (int)distance;
        playerStats.AddScore((int)distance); // Award points for distance

        lastPosition = transform.position;
    }

    private void ClampAngularVelocity()
        {
            float maxAngularVelocity = 540f;
            if (rb != null)
            {
                rb.angularVelocity = Mathf.Clamp(rb.angularVelocity, -maxAngularVelocity, maxAngularVelocity);
            }
        }

    public Sprite GetBaseSprite()
    {
        return baseSprite.sprite;
    }
}
