using System;
using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using Random = UnityEngine.Random;

public partial class Vehicle : MonoBehaviour
{

    private static int _nextId;
    private readonly int _id = System.Threading.Interlocked.Increment(ref _nextId);

    private bool  _isActivePlow;
    private float _plowVelocityDrain;
    private float _lastPressureFactor;
    private static readonly Collider2D[] _pressureHits = new Collider2D[8];
    private int _mineNodeLayerMask;
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
    [SerializeField] private LayerMask gravityVoidMask; // set in inspector OR leave 0 and use tag fallback in VehicleConfig

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

    public static bool AnyVehicleCarrying() => _s_vehiclesCarrying > 0;
    public static bool AnyVehicleCarryingTrack(InstrumentTrack track)
        => _s_vehiclesCarryingByTrack.TryGetValue(track, out var count) && count > 0;

    public bool CanAcceptCollectable(InstrumentTrack track) =>
        _lockedTrack == null || _lockedTrack == track;

    private void AcquireTrackLock(InstrumentTrack track)
    {
        if (_lockedTrack != null) return;
        _lockedTrack = track;
        _s_vehiclesCarrying++;
        if (track != null)
            _s_vehiclesCarryingByTrack[track] =
                (_s_vehiclesCarryingByTrack.TryGetValue(track, out int c) ? c : 0) + 1;
    }

    private void ReleaseTrackLock()
    {
        if (_lockedTrack == null) return;
        if (_lockedTrack != null && _s_vehiclesCarryingByTrack.TryGetValue(_lockedTrack, out int c))
            _s_vehiclesCarryingByTrack[_lockedTrack] = Mathf.Max(0, c - 1);
        _lockedTrack = null;
        _s_vehiclesCarrying = Mathf.Max(0, _s_vehiclesCarrying - 1);
    }

    public void ClearPendingNotesForBridge()
    {
        _pendingNotes.Clear();
        _armedReleases.Clear();
        _releaseButtonHeld = false;
        _lastArmWasFromHold = false;
        ReleaseTrackLock();
        DestroyVehicleTether();
    }

    private void DiscardCarriedCollectables()
    {
        foreach (var armed in _armedReleases)
            armed.note.collectable?.OnManualReleaseDiscarded();
        _armedReleases.Clear();

        foreach (var pending in _pendingNotes)
            pending.collectable?.OnManualReleaseDiscarded();
        _pendingNotes.Clear();

        _releaseButtonHeld = false;
        _lastArmWasFromHold = false;
        ReleaseTrackLock();
        DestroyVehicleTether();
    }

    private void DestroyVehicleTether()
    {
        if (_vehicleTether == null) return;
        Destroy(_vehicleTether.gameObject);
        _vehicleTether = null;
    }

    /// <summary>
    /// Called after a screen-wrap teleport. Resets the position history ring buffer and
    /// clears the TrailRenderer so no line is drawn across the screen between the old
    /// and new positions.
    /// </summary>
    public void ClearTrailForWrap()
    {
        // Reset ring buffer — RecordPositionHistory seeds itself from the new position next Update.
        _posHistoryCount = 0;
        _posHistoryHead  = 0;
        _posHistoryAccum = 0f;
        _posHistoryLast  = transform.position;

        // Clear the rendered trail geometry.
        if (_activeTrailRenderer != null)
            _activeTrailRenderer.Clear();
    }

    // ------------------------------------------------------------
    // Manual-release cue (visual guidance)
    // ------------------------------------------------------------
    void Start()
        {
            rb = GetComponent<Rigidbody2D>();
            gfm = GameFlowManager.Instance;
            _mineNodeLayerMask = LayerMask.GetMask("MineNode");
            var col = GetComponent<Collider2D>();
            if (GameFlowManager.VerboseLogging) Debug.Log(
                $"[VEHICLE:INIT] '{name}' layer={gameObject.layer} " +
                $"col={(col ? col.GetType().Name : "NULL")} col.isTrigger={(col ? col.isTrigger : false)} " +
                $"rb={(rb ? rb.bodyType.ToString() : "NULL")}",
                this
            );
       
            baseSprite = GetComponent<SpriteRenderer>();
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

        float dt = Time.fixedDeltaTime;

        bool insideBubble = StarPool.IsPointInsideAnySafetyBubble(transform.position);
        _isActivePlow = false;   // always reset; DoPlowTick sets true only when cells are carved

        if (insideBubble)
        {
            // Release keep-clear claim so refuge dust regrows.
            if (gfm != null && gfm.dustGenerator != null && gfm.activeDrumTrack != null)
                gfm.dustGenerator.ReleaseVehicleKeepClear(_id);
        }
        else
        {
            DoPlowTick();
            RefreshVehicleKeepClearIfNeeded();
        }

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
        int pCount = Physics2D.OverlapCircleNonAlloc(rb.position, profile.pressureInstabilityRadius,
                                                     _pressureHits, _mineNodeLayerMask);
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

    
    // ---- Note Trail Management ----
    private void RecordPositionHistory()
    {
        int cap = Mathf.Max(8, vehicleConfig.trailHistoryCapacity);
        if (_posHistory == null || _posHistory.Length != cap)
        {
            _posHistory = new Vector3[cap];
            _posHistoryHead = 0;
            _posHistoryCount = 0;
            _posHistoryLast = transform.position;
        }

        Vector3 cur = transform.position;
        float moved = Vector3.Distance(cur, _posHistoryLast);
        _posHistoryAccum += moved;
        if (moved > 0.001f)
            _lastTravelDir = (cur - _posHistoryLast).normalized;
        _posHistoryLast = cur;

        // Record at a density of ~4 samples per slot-spacing so we have smooth curve data
        float sampleDist = Mathf.Max(0.01f, vehicleConfig.trailSlotSpacing * 0.25f);
        if (_posHistoryAccum >= sampleDist)
        {
            _posHistoryAccum -= sampleDist;
            _posHistory[_posHistoryHead] = cur;
            _posHistoryHead = (_posHistoryHead + 1) % cap;
            if (_posHistoryCount < cap) _posHistoryCount++;
        }
    }
    /// <summary>
    /// Walks back through the position history to find a world point at arc-length <paramref name="distance"/>
    /// behind the vehicle head. Returns the vehicle position if history is too short.
    /// </summary>
    private Vector3 SampleTrailPosition(float distance)
    {
        Vector3 vehiclePos = transform.position;

        if (_posHistory == null || _posHistoryCount < 2)
            return vehiclePos - _lastTravelDir * distance;

        float remaining = distance;
        // Start from the newest entry (head-1) and walk backwards.
        // The newest entry may lag behind the actual vehicle position if it hasn't
        // moved far enough to trigger a new sample, so we prepend a virtual segment
        // from vehiclePos to the newest history point.
        int idx = (_posHistoryHead - 1 + _posHistory.Length) % _posHistory.Length;
        Vector3 prev = vehiclePos; // always start from the live vehicle position
        Vector3 newest = _posHistory[idx];

        // Walk: vehiclePos → newest → older samples
        for (int i = 0; i <= _posHistoryCount; i++)
        {
            Vector3 next = (i == 0) ? newest : default;
            if (i > 0)
            {
                int nextIdx = (idx - 1 + _posHistory.Length) % _posHistory.Length;
                next = _posHistory[nextIdx];
                idx = nextIdx;
            }

            float seg = Vector3.Distance(prev, next);
            if (seg <= 0f) { prev = next; continue; }

            if (remaining <= seg)
                return Vector3.Lerp(prev, next, remaining / seg);

            remaining -= seg;
            prev = next;

            if (i == _posHistoryCount - 1)
                break;
        }

        // Ran out of history — extrapolate straight back from the last known point
        // along the last travel direction so the tail keeps hanging naturally.
        return prev - _lastTravelDir * remaining;
    }
    // ── Shared timing helpers ──────────────────────────────────────────────

    private static double ComputeStepDuration(DrumTrack drum, int totalAbsSteps)
    {
        int total      = Mathf.Max(1, totalAbsSteps);
        int binSize    = drum != null ? Mathf.Max(1, drum.totalSteps) : total;
        int leaderBins = Mathf.Max(1, Mathf.CeilToInt(total / (float)binSize));
        double loopLen = (drum != null ? drum.GetLoopLengthInSeconds() : 1.0) * leaderBins;
        return loopLen / total;
    }

    private bool TryComputeArmedPulse(ArmedRelease a,
        out float pulse01, out bool inWindow, out bool atExact)
    {
        pulse01 = 0f; inWindow = false; atExact = false;
        if (a.note.track?.controller == null) return false;
        if (!a.note.track.controller.TryGetRawPlayheadAbsStep(out double rawAbs, out _, out int totalRaw))
            return false;
        int    total    = Mathf.Max(1, totalRaw);
        double fwdSteps = (a.targetAbsStep - rawAbs + total) % total;
        double stepDur  = ComputeStepDuration(a.note.track.drumTrack, total);
        double fwdDsp   = fwdSteps * stepDur;
        double gapDsp   = Math.Max(0.001, a.gapDurationDsp);
        pulse01  = 1f - Mathf.Clamp01((float)(fwdDsp / gapDsp));
        inWindow = fwdDsp <= gapDsp;
        atExact  = fwdSteps <= 0.025;
        return true;
    }

    private void ResolvePlaybackNote(PendingCollectedNote p, int atStep, out int midi, out int dur)
    {
        bool compositionMode = p.track.controller != null &&
                               p.track.controller.noteCommitMode == NoteCommitMode.Composition;
        midi = compositionMode ? p.collectedMidi : p.track.GetAuthoredNoteAtAbsStep(atStep);
        dur  = p.durationTicks;
        int binSize   = Mathf.Max(1, p.track.BinSize());
        int localStep = ((atStep % binSize) + binSize) % binSize;
        var noteSet   = p.track.GetNoteSetForBin(p.track.BinIndexForStep(atStep));
        if (noteSet != null && noteSet.TryGetTemplateTimingAtStep(localStep, out int authoredDur, out _))
            dur = authoredDur;
    }

    private void DiscardPendingNote(PendingCollectedNote p)
    {
        _pendingNotes.Dequeue();
        if (p.collectable != null) p.collectable.OnManualReleaseDiscarded();
        p.track.NotifyNoteDiscarded(p.burstId, p.authoredAbsStep);
    }

    // ──────────────────────────────────────────────────────────────────────

    private void TickNoteTrail()
    {
        if (gfm == null) gfm = GameFlowManager.Instance;
        var viz = gfm != null ? gfm.noteViz : null;

        _carriedTracksScratch.Clear();

        if (_pendingNotes.Count == 0 && _armedReleases.Count == 0)
        {
            ReleaseTrackLock();
            DestroyVehicleTether();
            if (viz != null) viz.UpdateCarryHighlights(_carriedTracksScratch);
            UpdateVehiclePlacementResonance(0f, null);
            return;
        }

        // ------------------------------------------------------------------
        // Compute pulse01 — how close the playhead is to the next target step.
        // Armed note takes priority over pending; both use the same approach:
        // remaining DSP time / gap DSP time, so the ramp is gap-normalised and
        // stable across bin expansions.
        // ------------------------------------------------------------------
        float pulse01 = 0f;
        bool isAuthoritative = true;
        bool inTimingWindow = false;
        bool atExactStep = false;

        if (_armedReleases.Count > 0)
        {
            var a = _armedReleases.Peek();
            if (TryComputeArmedPulse(a, out pulse01, out inTimingWindow, out atExactStep))
            {
                if (a.note.track.IsExpansionPending)
                    pulse01 = Mathf.Min(pulse01, 0.3f);

                var  aDrum        = a.note.track.drumTrack;
                int  aBinSize     = aDrum != null ? Mathf.Max(1, aDrum.totalSteps) : 1;
                int  aTargetLocal = ((a.targetAbsStep % aBinSize) + aBinSize) % aBinSize;
                bool aMatchesAuthored = (a.note.authoredAbsStep < 0) || (a.targetAbsStep == a.note.authoredAbsStep);
                isAuthoritative = aMatchesAuthored || (aTargetLocal == a.note.authoredLocalStep);
            }
        }
        else if (_pendingNotes.Count > 0)
        {
            // Use the same next-unlit-step the ghost cue is pointing at, so the
            // ring tracks the real release window rather than the authored step.
            var p = _pendingNotes.Peek();
            if (p.track != null && p.track.controller != null && p.track.drumTrack != null &&
                p.track.controller.TryGetRawPlayheadAbsStep(out double rawAbsP, out _, out int totalP))
            {
                int total = Mathf.Max(1, totalP);

                // Reuse the spoken-for set so we agree with the ghost cue.
                _spokenForScratch.Clear();
                foreach (var ar in _armedReleases)
                    _spokenForScratch.Add(ar.targetAbsStep);

                if (viz != null && viz.TryGetNextUnlitStepExcluding(
                        p.track, rawAbsP, total, _spokenForScratch, out int nextStep))
                {
                    double fwdSteps = (nextStep - rawAbsP + total) % total;

                    var    pDrum    = p.track.drumTrack;
                    int    pBinSize = pDrum != null ? Mathf.Max(1, pDrum.totalSteps) : 1;
                    double pStepDur = ComputeStepDuration(pDrum, total);
                    double fwdDsp   = fwdSteps * pStepDur;

                    // Ring window is always effectiveArmSteps wide regardless of
                    // whether the target is in an expansion bin. The +lead gives a visible
                    // heads-up before the commit gate opens.
                    const float ringWindowLead = 1.5f;
                    double windowDsp = (vehicleConfig.EffectiveArmAheadSteps(pStepDur) + ringWindowLead) * pStepDur;
                    pulse01 = 1f - Mathf.Clamp01((float)(fwdDsp / Math.Max(0.001, windowDsp)));
                    inTimingWindow = fwdDsp <= windowDsp;
                    atExactStep = fwdSteps <= 0.025;

                    int nextLocal = ((nextStep % pBinSize) + pBinSize) % pBinSize;
                    bool pMatchesAuthored = (p.authoredAbsStep >= 0) && (nextStep == p.authoredAbsStep);
                    isAuthoritative = pMatchesAuthored || (nextLocal == p.authoredLocalStep);
                }
            }
        }

        InstrumentTrack cueTrack = null;
        if (_armedReleases.Count > 0)
            cueTrack = _armedReleases.Peek().note.track;
        else if (_pendingNotes.Count > 0)
            cueTrack = _pendingNotes.Peek().track;

        UpdateVehiclePlacementResonance(pulse01, cueTrack, isAuthoritative);

        // Armed notes: fly orb toward its target marker.
        int armedSlot = 0;
        float bunchDist = vehicleConfig.trailFirstSlotOffset;
        foreach (var ar in _armedReleases)
        {
            if (ar.note.collectable == null) { armedSlot++; continue; }

            Vector3 markerWorld = Vector3.zero;
            bool hasMarkerPos = false;
            if (viz != null && ar.note.track != null &&
                viz.noteMarkers != null &&
                viz.noteMarkers.TryGetValue((ar.note.track, ar.targetAbsStep), out var markerTr) && markerTr != null)
            {
                markerWorld = markerTr.position;
                hasMarkerPos = true;
            }

            if (hasMarkerPos)
                ar.note.collectable.SetTrailTarget(markerWorld);
            else
            {
                float dist = bunchDist;
                ar.note.collectable.SetTrailTarget(SampleTrailPosition(dist));
            }

            ar.note.collectable.SetReleasePulse(armedSlot == 0 ? pulse01 : 0f);
            armedSlot++;
        }

        // Pending notes: trail behind vehicle.
        int slot = armedSlot;
        foreach (var p in _pendingNotes)
        {
            if (p.collectable == null) { slot++; continue; }

            p.collectable.SetTrailTarget(SampleTrailPosition(bunchDist));
            p.collectable.SetReleasePulse(slot == 0 && _armedReleases.Count == 0 ? pulse01 : 0f);
            slot++;
        }

        // Single Vehicle-owned tether: create, update, or destroy.
        bool hasNotes = _pendingNotes.Count > 0 || _armedReleases.Count > 0;
        if (!hasNotes)
        {
            DestroyVehicleTether();
        }
        else
        {
            if (_vehicleTether == null && viz != null && viz.noteTetherPrefab != null)
            {
                var go = Instantiate(viz.noteTetherPrefab);
                _vehicleTether = go.GetComponent<NoteTether>() ?? go.AddComponent<NoteTether>();
                Color col = Color.white;
                if (_pendingNotes.Count > 0 && _pendingNotes.Peek().track != null)
                    col = _pendingNotes.Peek().track.DisplayColor;
                else if (_armedReleases.Count > 0 && _armedReleases.Peek().note.track != null)
                    col = _armedReleases.Peek().note.track.DisplayColor;
                _vehicleTether.SetEndpoints(null, null, col);
            }

            if (_vehicleTether != null)
            {
                _vehicleTether.SetStartWorldPos(SampleTrailPosition(vehicleConfig.trailFirstSlotOffset));
                UpdateVehicleTether(viz);
            }
        }

        foreach (var ar in _armedReleases) if (ar.note.track != null) _carriedTracksScratch.Add(ar.note.track);
        foreach (var p in _pendingNotes)   if (p.track != null)       _carriedTracksScratch.Add(p.track);
        if (viz != null) viz.UpdateCarryHighlights(_carriedTracksScratch);
    }
    
    private void UpdateVehicleTether(NoteVisualizer viz)
    {
        if (_vehicleTether == null) return;

        // ── Armed-release state ─────────────────────────────────────────────────
        if (_armedReleases.Count > 0)
        {
            var a = _armedReleases.Peek();
            if (a.note.track?.controller == null) return;
            if (!TryComputeArmedPulse(a, out float pulse, out bool inWin, out bool atExact)) return;
            _vehicleTether.BindByStep(a.note.track, a.targetAbsStep, viz);
            _vehicleTether.SetReleaseProgress(pulse);
            _vehicleTether.SetTimingState(pulse, inWin, atExact);
            return;
        }

        // ── Pending-note state ──────────────────────────────────────────────────
        if (_pendingNotes.Count == 0) return;
        var p = _pendingNotes.Peek();
        if (p.track?.controller == null || p.track.drumTrack == null) return;
        if (!p.track.controller.TryGetRawPlayheadAbsStep(out double rawAbsP, out _, out int totalP)) return;

        int    tot      = Mathf.Max(1, totalP);
        double pStepDur = ComputeStepDuration(p.track.drumTrack, tot);
        double tetherWin   = vehicleConfig.EffectiveArmAheadSteps(pStepDur) * pStepDur;
        double graceDsp    = vehicleConfig.manualReleaseGracePeriodSteps * pStepDur;
        double playheadInLoop = rawAbsP % tot;

        // Find the nearest forward unlit step. No FIFO exclusions — one tether shows one step at a time.
        int  nextTarget = -1;
        bool resolved   = viz != null &&
            viz.TryGetNextUnlitStepExcluding(p.track, rawAbsP, tot, null, out nextTarget);

        int   currentBound  = _vehicleTether.boundStep;
        float notePulse     = 0f;
        bool  noteInWindow  = false;

        // Grace hold: stay on the just-passed step until its grace window expires.
        bool inGraceForBound = false;
        if (graceDsp > 0 && currentBound >= 0 && resolved && nextTarget != currentBound)
        {
            double fwdToBound    = (currentBound - rawAbsP + tot) % tot;
            double backFromBound = tot - fwdToBound;
            double backDspBound  = backFromBound * pStepDur;
            // Suppress grace that crosses the loop boundary (from a previous iteration).
            if (backDspBound <= graceDsp && backFromBound <= playheadInLoop + 0.001)
            {
                inGraceForBound = true;
                noteInWindow    = true;
                notePulse       = 1f - Mathf.Clamp01((float)(backDspBound / graceDsp));
            }
        }

        if (!inGraceForBound && resolved)
            _vehicleTether.BindByStep(p.track, nextTarget, viz);

        if (!inGraceForBound)
        {
            int visualStep = _vehicleTether.boundStep >= 0 ? _vehicleTether.boundStep : p.authoredAbsStep;
            if (visualStep >= 0)
            {
                double fwdSteps2 = (visualStep - rawAbsP + tot) % tot;
                double backSteps = tot - fwdSteps2;
                double fwdDsp2   = fwdSteps2 * pStepDur;
                double backDsp   = backSteps  * pStepDur;
                bool   inArmWin  = fwdDsp2 <= tetherWin;
                // Suppress cross-boundary grace (step passed in a previous iteration).
                bool   inGrace   = graceDsp > 0 && backDsp <= graceDsp && backSteps <= playheadInLoop + 0.001;
                noteInWindow = inArmWin || inGrace;
                bool atExact = fwdSteps2 <= 0.025;
                notePulse = inArmWin
                    ? 1f - Mathf.Clamp01((float)(fwdDsp2 / System.Math.Max(0.001, tetherWin)))
                    : inGrace
                        ? 1f - Mathf.Clamp01((float)(backDsp / System.Math.Max(0.001, graceDsp)))
                        : 0f;
                // Suppress the arm window for a step only reachable by crossing the loop boundary.
                if (inArmWin && playheadInLoop + fwdSteps2 >= tot)
                {
                    noteInWindow = false;
                    notePulse    = 0f;
                }
                _vehicleTether.SetReleaseProgress(notePulse);
                _vehicleTether.SetTimingState(notePulse, noteInWindow, atExact);
                return;
            }
        }

        _vehicleTether.SetReleaseProgress(notePulse);
        _vehicleTether.SetTimingState(notePulse, noteInWindow, false);
    }

    private void UpdateVehiclePlacementResonance(float pulse01, InstrumentTrack cueTrack, bool isAuthoritative = true)
    {
        if (!vehicleConfig.useVehiclePlacementResonance)
            return;

        Color roleColor = _vehicleDefaultColor;
        if (cueTrack != null)
            roleColor = cueTrack.DisplayColor;

        float tint01 = 0f;
        if (cueTrack != null && pulse01 > 0f)
            tint01 = Mathf.Clamp01(Mathf.Max(vehicleConfig.vehiclePlacementMinTint, pulse01));

        // -----------------------------------------------------------------
        // Root vehicle sprite: color resonance only
        // -----------------------------------------------------------------
        if (baseSprite != null)
        {
            Color targetVehicleColor = Color.Lerp(_vehicleDefaultColor, roleColor, tint01);
            baseSprite.color = Color.Lerp(
                baseSprite.color,
                targetVehicleColor,
                vehicleConfig.vehiclePlacementColorLerpSpeed * Time.deltaTime
            );
        }

        // -----------------------------------------------------------------
        // Soul clone: hidden by default, scales outward as readiness grows
        // -----------------------------------------------------------------
        if (soulSprite != null)
        {
            bool active = cueTrack != null;

            if (!active)
            {
                soulSprite.transform.localScale = Vector3.Lerp(
                    soulSprite.transform.localScale,
                    Vector3.one * profile.soulMaxScale,
                    profile.soulScaleLerpSpeed * Time.deltaTime
                );

                Color soulFade = soulSprite.color;
                soulFade.r = roleColor.r;
                soulFade.g = roleColor.g;
                soulFade.b = roleColor.b;
                soulFade.a = Mathf.Lerp(soulFade.a, 0f, profile.soulScaleLerpSpeed * Time.deltaTime);
                soulSprite.color = soulFade;

                if (soulFade.a <= 0.01f)
                    soulSprite.enabled = false;

                return;
            }

            soulSprite.enabled = true;

            float soulScale = Mathf.Lerp(profile.soulMinScale, profile.soulMaxScale, pulse01);
            soulSprite.transform.localScale = Vector3.Lerp(
                soulSprite.transform.localScale,
                Vector3.one * soulScale,
                profile.soulScaleLerpSpeed * Time.deltaTime
            );

            float soulAlpha = Mathf.Lerp(profile.soulAlphaMin, profile.soulAlphaMax, pulse01);

            Color targetSoulColor = isAuthoritative ? Color.white : roleColor;
            targetSoulColor.a = soulAlpha;

            soulSprite.color = Color.Lerp(
                soulSprite.color,
                targetSoulColor,
                profile.soulScaleLerpSpeed * Time.deltaTime
            );
        }
    }
      private void UpdateSafeAnchor()
{
    if (rb == null || drumTrack == null) return;

    // Only record anchor when we are reasonably “in play”.
    // (Avoid recording while already offscreen or during a trap.)
    if (IsFarOutsideViewport(vehicleConfig.viewportOobMargin * 0.5f)) return;

    // If you want, you can also avoid anchoring while inside a void:
    // if (IsInsideGravityVoid(out _, out _)) return;

    _lastSafeWorld = rb.position;
    _lastSafeCell = drumTrack.WorldToGridPosition(rb.position);
    _hasSafeAnchor = true;
}

    private void RecoverIfNeeded()
{
    if (Time.time - _lastRecoverAt < vehicleConfig.minSecondsBetweenRecoveries) return;
    if (rb == null || drumTrack == null || gfm == null || gfm.dustGenerator == null) return;

    // 1) Hard OOB: if we’re well outside camera viewport, snap back.
    if (IsFarOutsideViewport(vehicleConfig.viewportOobMargin))
    {
        DoSnapRespawn("viewport_oob");
        return;
    }

    // 2) Void trap: if we’re inside a void collider AND not moving for a while, do localized eject or snap.
    if (IsInsideGravityVoid(out var voidCenter, out var voidRadius))
    {
        float speed = rb.linearVelocity.magnitude;

        if (speed <= vehicleConfig.stuckSpeedThreshold)
            _timeStuckInVoid += Time.fixedDeltaTime;
        else
            _timeStuckInVoid = 0f;

        if (_timeStuckInVoid >= vehicleConfig.stuckSecondsInVoid)
        {
            // Prefer local eject (feels physical), but fall back to snap if we can’t find a clean spot.
            if (!TryEjectFromVoid(voidCenter, voidRadius))
            {
                DoSnapRespawn("void_trap_snap");
            }
            else
            {
                _lastRecoverAt = Time.time;
                _timeStuckInVoid = 0f;
            }
        }
    }
    else
    {
        _timeStuckInVoid = 0f;
    }
}

    private bool IsFarOutsideViewport(float margin)
{
    var cam = Camera.main;
    if (cam == null) return false;

    Vector3 vp = cam.WorldToViewportPoint(transform.position);

    // If behind camera (z < 0), treat as OOB.
    if (vp.z < 0f) return true;

    return (vp.x < -margin || vp.x > 1f + margin || vp.y < -margin || vp.y > 1f + margin);
}

    private bool IsInsideGravityVoid(out Vector2 center, out float radius)
{
    center = default;
    radius = 0f;

    if (gravityVoidMask.value == 0)
        return false; // explicitly disabled

    Vector2 pos = rb != null ? rb.position : (Vector2)transform.position;

    Collider2D hit = Physics2D.OverlapCircle(
        pos,
        vehicleConfig.voidProbeRadiusWorld,
        gravityVoidMask
    );

    if (hit == null)
        return false;

    var cc = hit as CircleCollider2D;
    if (cc != null)
    {
        center = cc.bounds.center;
        radius = Mathf.Max(0.05f, cc.bounds.extents.x);
        return true;
    }

    center = hit.bounds.center;
    radius = Mathf.Max(
        0.05f,
        Mathf.Max(hit.bounds.extents.x, hit.bounds.extents.y)
    );
    return true;
}

    private bool TryEjectFromVoid(Vector2 voidCenter, float voidRadius)
{
    if (!_hasSafeAnchor) return false;

    // Aim to put the vehicle just outside the void boundary, away from center.
    Vector2 pos = rb.position;
    Vector2 away = (pos - voidCenter);
    if (away.sqrMagnitude < 0.0001f) away = Random.insideUnitCircle.normalized;
    else away.Normalize();

    // Candidate target position on rim + small margin
    float margin = Mathf.Max(0.15f, vehicleConfig.voidProbeRadiusWorld * 0.5f);
    Vector2 targetWorld = voidCenter + away * (voidRadius + margin);

    // Convert to a grid cell and look for a nearby empty cell (localized)
    Vector2Int targetCell = drumTrack.WorldToGridPosition(targetWorld);
    if (TryFindNearbyEmptyCell(targetCell, maxRadius: 4, out var emptyCell))
    {
        Vector2 world = (Vector2)drumTrack.GridToWorldPosition(emptyCell);

        // Teleport + impulse outward so we don’t immediately re-stick
        rb.position = world;
        rb.linearVelocity = away * Mathf.Max(2.5f, arcadeMaxSpeed * 0.35f);
        rb.angularVelocity = 0f;

        _lastRecoverAt = Time.time;
        _timeStuckInVoid = 0f;
        return true;
    }

    return false;
}

    private void DoSnapRespawn(string reason)
{
    if (!_hasSafeAnchor) return;

    // Try to find an empty cell near the last safe anchor.
    if (!TryFindNearbyEmptyCell(_lastSafeCell, vehicleConfig.respawnSearchRadiusCells, out var respawnCell))
    {
        // Absolute fallback: last safe world position.
        rb.position = _lastSafeWorld;
        rb.linearVelocity = Vector2.zero;
        rb.angularVelocity = 0f;

        _lastRecoverAt = Time.time;
        _timeStuckInVoid = 0f;
        Debug.LogWarning($"[VEHICLE:RECOVER] {name} fallback to lastSafeWorld (reason={reason})", this);
        return;
    }

    Vector2 respawnWorld = (Vector2)drumTrack.GridToWorldPosition(respawnCell);

    rb.position = respawnWorld;
    rb.linearVelocity = Vector2.zero;
    rb.angularVelocity = 0f;

    _lastRecoverAt = Time.time;
    _timeStuckInVoid = 0f;

    Debug.LogWarning($"[VEHICLE:RECOVER] {name} respawn to cell {respawnCell} (reason={reason})", this);
}

    private bool TryFindNearbyEmptyCell(Vector2Int around, int maxRadius, out Vector2Int found) {
        found = around;

    var gen = gfm.dustGenerator;
    if (gen == null) return false;

    // radius 0 first
    if (IsCellEmpty(around)) { found = around; return true; }

    int rMax = Mathf.Clamp(maxRadius, 1, 64);

    // ring scan
    for (int r = 1; r <= rMax; r++)
    {
        // top/bottom rows
        for (int dx = -r; dx <= r; dx++)
        {
            var a = new Vector2Int(around.x + dx, around.y + r);
            var b = new Vector2Int(around.x + dx, around.y - r);
            if (IsCellEmpty(a)) { found = a; return true; }
            if (IsCellEmpty(b)) { found = b; return true; }
        }

        // left/right cols (skip corners already checked)
        for (int dy = -r + 1; dy <= r - 1; dy++)
        {
            var a = new Vector2Int(around.x + r, around.y + dy);
            var b = new Vector2Int(around.x - r, around.y + dy);
            if (IsCellEmpty(a)) { found = a; return true; }
            if (IsCellEmpty(b)) { found = b; return true; }
        }
    }

    return false;
}

    private bool IsCellEmpty(Vector2Int gp)
{
    var gen = gfm.dustGenerator;

    // IMPORTANT:
    // In your current usage, TryGetDustAt() seems to return false OR dust==null
    // when there is no dust cell at that position (or out of bounds).
    // We’re using it as “empty enough to respawn”.
    if (!gen.TryGetDustAt(gp, out var dust)) return true;
    return (dust == null);
}

    public void CollectEnergy(float amount)
    {
        energyLevel += amount;
        if (energyLevel > capacity)
        {
            energyLevel = capacity;
        }

        if (playerStatsUI != null)
        {
            playerStatsUI.UpdateFuel(energyLevel, capacity);
        }

        playerStats.RecordItemCollected();
            
    }
    public void DrainEnergy(float amount, string source = "Unknown")
{
    if (amount <= 0f) return;
    if (_boostCostFree) return; // free boost phase: skip all external energy drains
    ConsumeEnergy(amount);
}

    private void ApplyShipProfile(ShipMusicalProfile p, bool refillEnergy = true)
    {
        profile = p;

        arcadeMaxSpeed = p.arcadeMaxSpeed;

        rb.mass             = p.mass;
        rb.linearDamping    = p.arcadeLinearDamping;
        rb.angularDamping   = p.arcadeAngularDamping;

        capacity = p.capacity;
        if (refillEnergy) energyLevel = capacity;

        if (p.vehicleKeepClearRadiusCells > 0)
            vehicleKeepClearRadiusCells = p.vehicleKeepClearRadiusCells;
    }

    public void SyncEnergyUI()
        {
            if (playerStatsUI != null)
            {
                playerStatsUI.UpdateFuel(energyLevel, capacity);
            }
            
        }
    public void SetDrumTrack(DrumTrack drums)
        {
            drumTrack = drums;
        }
    public int GetForceAsDamage()
        {
            float speed = rb.linearVelocity.magnitude;
            float impactCapVelocity = profile != null ? profile.impactSpeedCap : 32f;
            float normalizedSpeed = Mathf.InverseLerp(0f, impactCapVelocity, speed);
            float curvedSpeed = Mathf.Pow(normalizedSpeed, 1.75f);
            float baseDamage = Mathf.Lerp(25f, 100f, curvedSpeed); 

            float massMultiplier = Mathf.Clamp(rb.mass, 0.75f, 2f);
            float damage = baseDamage * massMultiplier;

            // If we hit something within the last 0.5s, pad the damage slightly
            if (Time.time - _lastDamageTime < 0.5f)
            {
                damage = Mathf.Max(damage, 10f); // Floor for quick follow-ups
            }

            _lastDamageTime = Time.time;

            return Mathf.RoundToInt(Mathf.Clamp(damage, 0f, 120f));
        }
    public float HitVelocityMultiplier => profile != null ? profile.hitVelocityMultiplier : 1.0f;

    public float GetForceAsMidiVelocity()
    {
        float speed = rb.linearVelocity.magnitude;

        // Make sure this reflects *true* achievable speed (including boost),
        // otherwise you will peg at 127 constantly.
        float max = Mathf.Max(0.01f, arcadeMaxSpeed);

        float x = Mathf.Clamp01(speed / max);

        // Optional: curve to give more resolution in the midrange
        // x = Mathf.Pow(x, 0.7f);

        return Mathf.Lerp(40f, 127f, x);
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
    public float GetCumulativeSpentTanks() {
        if (capacity <= 0f) return 0f;
        return _cumulativeEnergySpent / capacity;
    }
    private void RefreshVehicleKeepClearIfNeeded() {
        if (gfm.BridgePending || gfm.GhostCycleInProgress) return;

        // Throttle refresh — collision safety clearance, not a dust design feature
        if (Time.time < _nextVehicleKeepClearRefreshAt) return;
        _nextVehicleKeepClearRefreshAt = Time.time + Mathf.Max(0.02f, vehicleConfig.vehicleKeepClearRefreshSeconds);
        if (gfm == null) return;

        var gen = gfm.dustGenerator;
        var drum = gfm.activeDrumTrack;
        if (gen == null || drum == null) return;

        int ownerId = _id;

        Vector2Int centerCell = drum.WorldToGridPosition(rb.position);

        if (!boosting)
        {
            // Claim just the center cell (radius=0, no force-remove) so dust can never
            // regrow directly under the vehicle, without actively clearing any dust.
            gen.SetVehicleKeepClear(ownerId, centerCell, 0, forceRemoveExisting: false);
            return;
        }

        // While boosting, claim the pocket (prevents regrowth) but don't force-remove existing
        // dust — the plow owns carving with resistance applied. forceRemoveExisting is only
        // used for the one-time spawn rest pocket, not for live carving.
        gen.SetVehicleKeepClear(
            ownerId,
            centerCell,
            Mathf.Max(0, vehicleKeepClearRadiusCells),
            forceRemoveExisting: false
        );
    }
    private IEnumerator Co_CarveSpawnRestPocket()
    {
        // Optional delay to allow spawn ordering (dust grid, drumTrack, etc.) to settle.
        if (vehicleConfig.spawnRestPocketDelaySeconds > 0f)
            yield return new WaitForSeconds(vehicleConfig.spawnRestPocketDelaySeconds);
        else
            yield return null; // at least one frame so the dust grid exists

        if (gfm == null) yield break;

        var gen = gfm.dustGenerator;

        if (gen == null || drumTrack == null) yield break;

        // Compute which cell we're currently in.
        Vector2 pos = (rb != null) ? rb.position : (Vector2)transform.position;
        Vector2Int centerCell = drumTrack.WorldToGridPosition(pos);

        // Choose a radius that guarantees we are not born overlapping walls.
        int radiusCells = Mathf.Max(0, vehicleConfig.spawnRestPocketRadiusCells);
        if (vehicleConfig.spawnRestPocketAutoRadius)
        {
            float cellWorld = Mathf.Max(0.01f, drumTrack.GetCellWorldSize());
            float rWorld = 0.0f;
            var col = GetComponent<Collider2D>();
            if (col != null)
                rWorld = Mathf.Max(col.bounds.extents.x, col.bounds.extents.y);
            else
                rWorld = 0.35f; // conservative fallback

            // Expand by a small margin so resting contacts don't continuously resolve.
            float rWithMargin = rWorld + (cellWorld * 0.15f);
            radiusCells = Mathf.Max(0, Mathf.CeilToInt(rWithMargin / cellWorld));
        }

        // Carve a small pocket *once*, then release keep-clear so regrowth behaves normally.
        // This creates a "rest" volume without creating a tunnel or permanently preventing regrowth.
        int ownerId = _id;
        gen.SetVehicleKeepClear(
            ownerId,
            centerCell,
            radiusCells,
            forceRemoveExisting: true,
            forceRemoveFadeSeconds: Mathf.Max(0.01f, vehicleConfig.spawnRestPocketFadeSeconds)
        );
        gen.ReleaseVehicleKeepClear(ownerId);
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
    private void ConsumeEnergy(float amount)
        {
            energyLevel -= amount;
            energyLevel = Mathf.Max(0, energyLevel);
            _cumulativeEnergySpent += Mathf.Max(0f, amount);
            if (energyLevel <= 0)
            {
                rb.linearVelocity = Vector2.zero;
                rb.angularVelocity = 0f;
                DiscardCarriedCollectables();
                    Explode explode = GetComponent<Explode>();
                    if (explode != null)
                    {
                        explode.Permanent();
                    }
                    gfm?.CheckAllPlayersOutOfEnergy();
//                }

            }

            // Update UI
            if (playerStatsUI != null)
            {
                playerStatsUI.UpdateFuel(energyLevel, capacity);
            }
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
    
    void OnCollisionEnter2D(Collision2D coll)
    {
        var node = coll.gameObject.GetComponent<MineNode>();

        // 🎯 Apply impact damage
        int damage = GetForceAsDamage();
        if (node != null)
        {
            TriggerFlickerAndPulse(1.2f, node.coreSprite.color, false);
            // 💥 Apply knockback
            Rigidbody2D nodeRb = node.GetComponent<Rigidbody2D>();
            if (nodeRb != null)
            {
                Vector2 forceDirection = rb.linearVelocity.normalized;
                float knockbackForce = rb.mass * rb.linearVelocity.magnitude * 0.5f; // Tunable
                nodeRb.AddForce(forceDirection * knockbackForce, ForceMode2D.Impulse);
            }
        }

        if (coll.gameObject.tag == "Bump")
        {
            TriggerThud(coll.contacts[0].point);
        }
}

    private void DoPlowTick()
    {
        _plowVelocityDrain = 0f;
        if (gfm == null || gfm.dustGenerator == null || drumTrack == null) return;
        if (gfm.BridgePending || gfm.GhostCycleInProgress) return;
        if (!boosting) return;

        Vector2 vel = rb.linearVelocity;
        var gen = gfm.dustGenerator;
        float cellSize  = Mathf.Max(0.001f, drumTrack.GetCellWorldSize());
        Vector2 forward = vel.normalized;
        Vector2 perp    = new Vector2(-forward.y, forward.x);
        float fade      = Mathf.Max(0.01f, vehicleConfig.plowFadeSeconds);
        int halfW       = Mathf.Max(0, profile.plowHalfWidthCells);
        int depth       = Mathf.Max(0, profile.plowDepthCells);
        int chipAmount  = Mathf.Max(1, profile.plowChipAmount);

        // Drain is felt regardless of speed; chipping requires min speed to carve.
        bool canCarve = vel.magnitude >= profile.plowMinSpeed;
        float totalVelocityDrain = 0f;

        for (int d = 0; d <= depth; d++)
        {
            for (int s = -halfW; s <= halfW; s++)
            {
                Vector2    sampleWorld = rb.position
                    + forward * (d * cellSize)
                    + perp    * (s * cellSize);
                Vector2Int cell = drumTrack.WorldToGridPosition(sampleWorld);
                if (!gen.TryGetCellState(cell, out var cellState)) continue;
                bool isSolid    = cellState == DustCellState.Solid;
                bool isClearing = cellState == DustCellState.Clearing;
                if (!isSolid && !isClearing) continue;

                float liveRes      = gen.GetLiveCarveResistance01(cell);
                float effectiveRes = liveRes * (1f - Mathf.Clamp01(profile.carveResistanceBypass01));

                if (profile.carveVelocityDrainPerCell > 0f)
                    totalVelocityDrain = Mathf.Max(totalVelocityDrain, liveRes * profile.carveVelocityDrainPerCell);

                if (!isSolid) continue;
                if (effectiveRes >= 1f) continue;
                if (!canCarve) continue;

                gen.DisableCellCollider(cell);
                gen.ChipDustByVehicle(cell, chipAmount, fade, profile.carveResistanceBypass01);
                _isActivePlow = true;
            }
        }

        _plowVelocityDrain = totalVelocityDrain;
    }

    private void TriggerThud(Vector2 collisionPoint)
        {
            if (baseSprite == null || isFlickering) return;

            if (flickerPulseRoutine != null)
            {
                StopCoroutine(flickerPulseRoutine); // Prevent stacking
            }
            flickerPulseRoutine = StartCoroutine(ThudRoutine(collisionPoint));
        }
    private void TriggerFlickerAndPulse(float scaleMultiplier, Color? baseColor = null, bool cycleHue = false)
        {
            if (baseSprite == null || isFlickering) return;

            if (flickerPulseRoutine != null)
            {
                StopCoroutine(flickerPulseRoutine); // Prevent stacking
            }

            flickerPulseRoutine = StartCoroutine(FlickerAndPulseRoutine(scaleMultiplier, baseColor, cycleHue));
        }
    private IEnumerator ThudRoutine(Vector2 coll)
        {
            isFlickering = true;
            yield return VisualFeedbackUtility.BoundaryThudFeedback(baseSprite, transform, coll);
            isFlickering = false;
            flickerPulseRoutine = null;
        }
    private IEnumerator FlickerAndPulseRoutine(float scaleMultiplier, Color? baseColor, bool cycleHue)
        {
            isFlickering = true;

            yield return VisualFeedbackUtility.SpectrumFlickerWithPulse(
                baseSprite,
                transform,
                0.2f,
                scaleMultiplier,
                cycleHue ? null : baseColor,
                cycleHue
            );

            isFlickering = false;
            flickerPulseRoutine = null;
        }
    // Enqueue a collected note for manual release. Returns true if queued.
    private bool EnqueuePendingCollectedNote(PendingCollectedNote p)
    {
        int cap = Mathf.Max(1, vehicleConfig.manualReleaseQueueCapacity);

        // If full, drop oldest (and clean up its visual carrier if still around)
        while (_pendingNotes.Count >= cap)
        {
            var dropped = _pendingNotes.Dequeue();
            if (dropped.collectable != null)
                dropped.collectable.OnManualReleaseDiscarded();
            dropped.track?.NotifyNoteDiscarded(dropped.burstId, dropped.authoredAbsStep);
        }

        AcquireTrackLock(p.track);
        _pendingNotes.Enqueue(p);
        return true;
    }


    public bool HasCapturedCollectablesPendingRelease()
    {
        return _pendingNotes.Count > 0 || _armedReleases.Count > 0;
    }

    // Back-compat with earlier patches / external callers.
    public bool EnqueuePendingNote(PendingCollectedNote p) => EnqueuePendingCollectedNote(p);
    
    public bool TryReleaseQueuedNote(bool allowSacrifice = true)
{
    if (_pendingNotes.Count <= 0) return false;

    if (gfm == null) gfm = GameFlowManager.Instance;
    var viz = (gfm != null) ? gfm.noteViz : null;

    // Peek first — only dequeue once we know the window passes.
    // An early press must leave the note in the queue so the cue remains visible.
    var p = _pendingNotes.Peek();
    if (p.track == null || p.track.controller == null || p.track.drumTrack == null)
    {
        _pendingNotes.Dequeue();
        if (p.collectable != null) p.collectable.OnManualReleaseConsumed();
        viz?.BlastManualReleaseCue(transform);
        return false;
    }

    if (!p.track.controller.TryGetRawPlayheadAbsStep(out double rawAbs, out int floorAbs, out int totalSteps))
    {
        _pendingNotes.Dequeue();
        if (p.collectable != null) p.collectable.OnManualReleaseDiscarded();
        p.track.NotifyNoteDiscarded(p.burstId, p.authoredAbsStep);
        CollectEnergy(p.collectable.amount * .25f);
//sacrifice note to gain small amount of energy instead of specific failure
        //        viz?.BlastManualReleaseCueFailure(transform, p.track, p.authoredAbsStep);
        return false;
    }

    // Build the set of steps already spoken for by in-flight armed releases.
    // These steps have not committed yet so their markers still say isPlaceholder=true,
    // but they are no longer available targets.
    var spokenFor = new HashSet<int>();
    foreach (var ar in _armedReleases)
        spokenFor.Add(ar.targetAbsStep);

    int effectiveTotal = Mathf.Max(totalSteps, p.track.GetTotalSteps());

    // Find nearest forward unlit placeholder that isn't already armed.
    if (viz == null || !viz.TryGetNextUnlitStepExcluding(p.track, rawAbs, effectiveTotal, spokenFor, out int targetAbsStep))
    {
        ResolvePlaybackNote(p, floorAbs, out int midiNoStep, out int durNoStep);
        p.track.PlayOneShotMidi(midiNoStep, p.velocity127, durNoStep);
        DiscardPendingNote(p);
        if (p.collectable != null) CollectEnergy(p.collectable.amount * .25f);

        //        viz?.BlastManualReleaseCueFailure(transform, p.track, p.authoredAbsStep);
        CollectionSoundManager.Instance?.PlayReleaseFailure();
        return false;
    }

    double fwdToTarget = (targetAbsStep - rawAbs + effectiveTotal) % effectiveTotal;

    // Hoist stepDur here so effectiveArmSteps can use it for the minimum-seconds floor,
    // and so the arm-lock path below can reuse it without a second GetLoopLengthInSeconds call.
    double stepDur = ComputeStepDuration(p.track.drumTrack, effectiveTotal);
    float effectiveArmSteps = vehicleConfig.EffectiveArmAheadSteps(stepDur);

    bool inAheadWindow = fwdToTarget <= effectiveArmSteps;
    double backFromTarget = effectiveTotal - fwdToTarget;
    bool inGraceWindow = vehicleConfig.manualReleaseGracePeriodSteps > 0f &&
                         backFromTarget <= vehicleConfig.manualReleaseGracePeriodSteps;
    bool pass = inAheadWindow || inGraceWindow;

    if (!pass && viz != null &&
        viz.TryGetNearestUnlitStepExcluding(p.track, rawAbs, effectiveTotal, spokenFor, out int nearestAbsStep, out double nearestFwd))
    {
        double nearestBack = effectiveTotal - nearestFwd;
        bool nearestPass = nearestFwd <= effectiveArmSteps ||
                           (vehicleConfig.manualReleaseGracePeriodSteps > 0f && nearestBack <= vehicleConfig.manualReleaseGracePeriodSteps);
        if (nearestPass)
        {
            targetAbsStep = nearestAbsStep;
            fwdToTarget = nearestFwd;
            backFromTarget = nearestBack;
            inAheadWindow = fwdToTarget <= effectiveArmSteps;
            inGraceWindow = vehicleConfig.manualReleaseGracePeriodSteps > 0f &&
                            backFromTarget <= vehicleConfig.manualReleaseGracePeriodSteps;
            pass = true;
            if (GameFlowManager.VerboseLogging) Debug.Log($"[RELEASE_RETARGET] oldTarget rejected, newTarget={targetAbsStep} rawAbs={rawAbs:F2} fwd={fwdToTarget:F2} back={backFromTarget:F2} PASS=True");
        }
    }
    if (GameFlowManager.VerboseLogging) Debug.Log($"[RELEASE_GATE] target={targetAbsStep} rawAbs={rawAbs:F2} fwd={fwdToTarget:F2} back={backFromTarget:F2} window={effectiveArmSteps:F1} grace={vehicleConfig.manualReleaseGracePeriodSteps:F1} effectiveTotal={effectiveTotal} PASS={pass}");

    if (!pass)
    {
        // Hold-cascade callers pass allowSacrifice=false: leave the note in queue so it
        // can be armed on a later tick when its step enters the window.
        if (!allowSacrifice) return false;

        if (GameFlowManager.VerboseLogging) Debug.Log($"[SACRIFICE] target={targetAbsStep} rawAbs={rawAbs:F2} fwd={fwdToTarget:F2} — note sacrificed outside timing window");
        ResolvePlaybackNote(p, targetAbsStep, out int midiToPlay, out int durToPlay);
        p.track.PlayOneShotMidi(midiToPlay, p.velocity127, durToPlay);
        DiscardPendingNote(p);
        Vector3 blastPos = p.collectable != null ? p.collectable.transform.position : transform.position;
        viz?.BlastManualReleaseCueFailure(transform, blastPos, p.track.DisplayColor);
        if (p.collectable != null) CollectEnergy(p.collectable.amount * .25f);
        return false;
    }

    // Window passed — now consume the note.
    _pendingNotes.Dequeue();

    bool lateGracePass = inGraceWindow && !inAheadWindow;

    // Timing-based velocity: 0 = earliest window open (~vel 40), 1 = exact step (vel 127).
    float releaseWindowLerp = inAheadWindow
        ? 1f - Mathf.Clamp01((float)(fwdToTarget / Mathf.Max(0.001f, effectiveArmSteps)))
        : inGraceWindow
            ? 1f - Mathf.Clamp01((float)(backFromTarget / Mathf.Max(0.001f, vehicleConfig.manualReleaseGracePeriodSteps)))
            : 0f;
    float releaseVelocity = Mathf.Lerp(40f, 127f, releaseWindowLerp);

    if (pass && vehicleConfig.manualReleaseUseArmLock && !lateGracePass)
    {
        // stepDur and leaderBins already computed above for the gate checks.
        double gapDsp  = fwdToTarget * stepDur;

        _armedReleases.Enqueue(new ArmedRelease
        {
            note            = p,
            targetAbsStep   = targetAbsStep,
            totalAbsSteps   = effectiveTotal,
            gapDurationDsp  = gapDsp,
            releaseVelocity = releaseVelocity
        });
        return true;
    }

    if (pass)
    {
        CommitManualReleaseAtStep(p, targetAbsStep, releaseVelocity);
        viz?.BlastManualReleaseCue(transform);
        return true;
    }

    // Defensive guard: all non-pass paths should have returned above.
    if (GameFlowManager.VerboseLogging) Debug.Log($"[RELEASE_BLOCKED] target={targetAbsStep} rawAbs={rawAbs:F2} fwd={fwdToTarget:F2} window={effectiveArmSteps:F1} PASS=False commitSkipped=True");
    return false;
}
}
