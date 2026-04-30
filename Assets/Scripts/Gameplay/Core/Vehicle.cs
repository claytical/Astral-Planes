using System;
using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using Random = UnityEngine.Random;

public class Vehicle : MonoBehaviour
{

    private float _nextPlowTickAt;
    public ShipMusicalProfile profile;
    [SerializeField] private VehicleConfig vehicleConfig;
    [HideInInspector] public float capacity = 10f;
    private float _burnRateMultiplier = 1f; // Multiplier for the burn rate based on trigger pressure
    private bool _boostCostFree;           // When true, boost ignores energy requirement and skips burn
    [HideInInspector] public float arcadeMaxSpeed = 14f;
    private float _cumulativeEnergySpent = 0f;

    [Tooltip("Child SpriteRenderer clone of the vehicle art. Normally hidden. Scales up as placement becomes available.")]
    [SerializeField] private SpriteRenderer soulSprite;

    [Header("Gravity Void Detection")]
    [SerializeField] private LayerMask gravityVoidMask; // set in inspector OR leave 0 and use tag fallback in VehicleConfig

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
    public Rigidbody2D rb;
    private AudioManager audioManager;
    private SpriteRenderer baseSprite;
    private Vector3 lastPosition;
    private DrumTrack drumTrack;
    private double loopStartDSPTime;
    private float _lastDamageTime = -1f;
    private Coroutine flickerPulseRoutine;
    private bool isFlickering = false;
    
    [HideInInspector] [SerializeField] private int vehicleKeepClearRadiusCells = 0;
    private float _nextVehicleKeepClearRefreshAt = 0f;

    private Coroutine _spawnRestPocketCo;

    private Color _vehicleDefaultColor = Color.white;
    private struct ArmedRelease
    {
        public PendingCollectedNote note;
        public int targetAbsStep;
        public int totalAbsSteps;
        public double gapDurationDsp;  // wall-clock seconds from arm moment to target; stable across bin expansion
    }

    // Armed releases commit automatically as the playhead reaches the target.
    private readonly Queue<ArmedRelease> _armedReleases = new Queue<ArmedRelease>(8);
    private double _lastRawAbsStep = 0.0;
    private bool _hasLastRawAbsStep = false;

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

    public void ClearPendingNotesForBridge()
    {
        _pendingNotes.Clear();
        _armedReleases.Clear();
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
        if (activeTrail != null)
        {
            var tr = activeTrail.GetComponent<TrailRenderer>();
            if (tr != null) tr.Clear();
        }
    }

    // ------------------------------------------------------------
    // Manual-release cue (visual guidance)
    // ------------------------------------------------------------
    void Start()
        {
            rb = GetComponent<Rigidbody2D>();
            gfm = GameFlowManager.Instance;
            var col = GetComponent<Collider2D>();
            Debug.Log(
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
            loopStartDSPTime = GameFlowManager.Instance.activeDrumTrack.startDspTime;
            ApplyShipProfile(profile);
            if (rb == null)
            {
                Debug.LogError("❌ Rigidbody2D component is missing.");
                enabled = false;
                return;
            }
            if (rb != null)
            {
                rb.gravityScale    = 0f;
                // Safety: ensure we start truly at rest.
                rb.linearVelocity = Vector2.zero;
                rb.angularVelocity = 0f;
            }

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
            var drum = GameFlowManager.Instance?.activeDrumTrack;
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

        // --- PhaseStar Safety Bubble: refuge zone during gravity void expansion ---
        // While inside the bubble the vehicle is a guest, not an agent:
        //   - Skip keep-clear carving so refuge dust is preserved.
        //   - Skip the rest of FixedUpdate physics (movement still runs via rb).
        // Energy drain from dust contact is suppressed in CosmicDust.OnCollisionStay2D
        // because that method checks Vehicle.boosting; inside the bubble the player
        // still pilots normally, so no change is needed there.
        if (StarPool.IsPointInsideAnySafetyBubble(transform.position))
        {
            // Release any existing keep-clear claim so refuge cells can regrow.
            if (gfm != null && gfm.dustGenerator != null && gfm.activeDrumTrack != null)
            {
                gfm.dustGenerator.ReleaseVehicleKeepClear(GetInstanceID());
            }

            // Still run the loop-boundary timer and input hygiene below,
            // but skip keep-clear refresh entirely.
        }
// After the safety bubble block, before RefreshVehicleKeepClearIfNeeded:
        bool insideBubble = StarPool.IsPointInsideAnySafetyBubble(transform.position);

        if (insideBubble)
        {
            // Release keep-clear claim so refuge dust regrows.
            if (gfm != null && gfm.dustGenerator != null && gfm.activeDrumTrack != null)
            {
                gfm.dustGenerator.ReleaseVehicleKeepClear(GetInstanceID());
            }
            // Skip keep-clear refresh — we're a guest inside the bubble.
        }
        else
        {
            RefreshVehicleKeepClearIfNeeded();
            DoPlowTick();
        }
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

        bool hasInput  = _moveInput.sqrMagnitude > 0.0001f;

        // ---- movement ----
        if (hasInput || boosting) {
            Vector2 steerDir   = hasInput ? _moveInput.normalized : (_lastNonZeroInput.sqrMagnitude > 0f ? _lastNonZeroInput : (Vector2)transform.up);
            Vector2 desiredVel = steerDir * arcadeMaxSpeed;
            float accelUsed    = boosting ? profile.arcadeBoostAccel : profile.arcadeAccel;

            if (accelUsed > 0f)
            {
                Vector2 dv      = desiredVel - rb.linearVelocity;
                float   maxStep = accelUsed * dt;
                Vector2 step    = (dv.sqrMagnitude > maxStep * maxStep) ? dv.normalized * maxStep : dv;
                rb.linearVelocity += step;
            }
            
        }
        else
        {
            // MASS-DEPENDENT COAST/BRAKE when no input OR cannot thrust
            Vector2 v = rb.linearVelocity;

            // Viscous brake: F = -k * v  → a = -(k/m) v (heavier ships coast longer)
            if (v.sqrMagnitude > 0f && profile.coastBrakeForce > 0f)
                rb.AddForce(-v * profile.coastBrakeForce, ForceMode2D.Force);

            // Snap to full rest near zero to kill jitter tails
            if (v.magnitude < profile.stopSpeed && Mathf.Abs(rb.angularVelocity) < profile.stopAngularSpeed)
            {
                rb.linearVelocity        = Vector2.zero;
                rb.angularVelocity = 0f;
            }
        }

        // Fuel burn only while boosting
        if (boosting && energyLevel > 0f && !_boostCostFree)
        {
            float burn = _burnRateMultiplier * profile.burnRate * dt;
            ConsumeEnergy(burn);
        }

        // Face travel direction
        if (rb.linearVelocity.sqrMagnitude > 0.0001f)
        {
            float angleDeg = Mathf.Atan2(rb.linearVelocity.y, rb.linearVelocity.x) * Mathf.Rad2Deg - 90f;
            rb.rotation = angleDeg;
        }

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

    private void Update()
    {
        RecordPositionHistory();
        TickManualReleaseCue();
        TickNoteTrail();
    }

    private void TickManualReleaseCue()
{
    var gfm = GameFlowManager.Instance;
    var viz = (gfm != null) ? gfm.noteViz : null;
    if (viz == null) return;

    // Build spoken-for set from all currently armed releases.
    // This is cheap (queue is tiny) and must happen before any cue update.
    var spokenFor = new HashSet<int>();
    foreach (var ar in _armedReleases)
        spokenFor.Add(ar.targetAbsStep);

    // -----------------------------------------------------------------
    // ARMED: check for playhead crossing and auto-commit the front note.
    // The cue shows where the NEXT press will land, not the armed target.
    // -----------------------------------------------------------------
    if (_armedReleases.Count > 0)
    {
        var a = _armedReleases.Peek();
        var armed = a.note;

        if (armed.track == null || armed.track.controller == null)
        {
            _armedReleases.Dequeue();
            return;
        }

        if (!armed.track.controller.TryGetRawPlayheadAbsStep(
                out double rawAbs, out int floorAbs, out int totalStepsLive))
            return;

        // Crossing check uses live totalSteps (Bug 2 fix).
        int totalSteps = Mathf.Max(1, totalStepsLive);
        double fwd = (a.targetAbsStep - rawAbs) % totalSteps;
        if (fwd < 0) fwd += totalSteps;

        bool crossed = false;
        if (_hasLastRawAbsStep)
        {
            double last = (_lastRawAbsStep % totalSteps + totalSteps) % totalSteps;
            double now  = (rawAbs          % totalSteps + totalSteps) % totalSteps;
            if (last <= now)
                crossed = (last <= a.targetAbsStep && a.targetAbsStep <= now);
            else
                crossed = (a.targetAbsStep >= last) || (a.targetAbsStep <= now);
        }

        if (crossed || fwd <= vehicleConfig.manualReleaseAutoCommitEpsSteps)
        {
            CommitManualReleaseAtStep(armed, a.targetAbsStep);
            // CommitManualReleaseAtStep may fire the bridge, which calls
            // ClearPendingNotesForBridge() and empties _armedReleases. Return
            // immediately in that case rather than dequeuing a cleared queue.
            if (_armedReleases.Count == 0) return;
            _armedReleases.Dequeue();
            spokenFor.Remove(a.targetAbsStep); // it's committed now, free it for cue
        }

        _lastRawAbsStep = rawAbs;
        _hasLastRawAbsStep = true;

        // Show the cue pointing at the next AVAILABLE step (skipping all armed ones).
        // If nothing is pending, clear the cue.
        if (_pendingNotes.Count > 0)
            viz.UpdateManualReleaseCueExcluding(transform, armed.track, rawAbs, floorAbs, totalStepsLive, spokenFor);
        else
            viz.ClearManualReleaseCue(transform);

        return;
    }

    // -----------------------------------------------------------------
    // PENDING only: show cue toward next unlit step (none spoken for).
    // -----------------------------------------------------------------
    if (_pendingNotes.Count <= 0)
    {
        viz.ClearManualReleaseCue(transform);
        return;
    }

    var queued = _pendingNotes.Peek();
    if (queued.track == null || queued.track.controller == null)
    {
        viz.ClearManualReleaseCue(transform);
        return;
    }

    if (!queued.track.controller.TryGetRawPlayheadAbsStep(
            out double rawAbsQ, out int floorAbsQ, out int totalStepsQ))
    {
        viz.ClearManualReleaseCue(transform);
        return;
    }

    // spokenFor is empty here (no armed releases), so this is equivalent to
    // the old UpdateManualReleaseCue call but routed through the unified method.
    viz.UpdateManualReleaseCueExcluding(transform, queued.track, rawAbsQ, floorAbsQ, totalStepsQ, spokenFor);

    _lastRawAbsStep = rawAbsQ;
    _hasLastRawAbsStep = true;
}
    private void CommitManualReleaseAtStep(PendingCollectedNote p, int targetAbsStep)
    {
        var gfm = GameFlowManager.Instance;
        var viz = (gfm != null) ? gfm.noteViz : null;

        if (p.track == null || p.track.controller == null || p.track.drumTrack == null)
        {
            if (p.collectable != null) p.collectable.OnManualReleaseConsumed();
            return;
        }

        bool compositionMode = p.track.controller != null &&
                               p.track.controller.noteCommitMode == NoteCommitMode.Composition;
        // Composition mode: use the note the player physically collected.
        // Performance mode: look up the authored note for whatever step the player released on.
        int chosenMidi = compositionMode
            ? p.collectedMidi
            : p.track.GetAuthoredNoteAtAbsStep(targetAbsStep);

        bool occupied = p.track.IsPersistentStepOccupied(targetAbsStep);
        float commitVel = p.velocity127;
        if (occupied)
            commitVel = Mathf.Clamp(commitVel * vehicleConfig.occupiedStepVelocityMultiplier, 1f, 127f);

        // Mark as collected and remove from spawnedCollectables BEFORE committing the note.
        // This ensures that when CommitManualReleasedNote fires OnCollectableBurstCleared,
        // AnyCollectablesInFlightGlobal() returns false and PhaseStar re-arms immediately
        // rather than deferring to the next loop boundary.
        // MarkAsReportedCollected must precede OnManualReleaseConsumed so that if the
        // deferred Destroy fires OnCollectableDestroyed, it skips the "lost note" branch
        // and does not double-decrement _burstRemaining.
        if (p.collectable != null) p.collectable.MarkAsReportedCollected();
        if (p.collectable != null) p.collectable.OnManualReleaseConsumed();

        // Write the note to the loop (lights the marker via AddNoteToLoop reuse path).
        p.track.CommitManualReleasedNote(
            stepAbs: targetAbsStep,
            midiNote: chosenMidi,
            durationTicks: p.durationTicks,
            velocity127: commitVel,
            authoredRootMidi: p.authoredRootMidi,
            burstId: p.burstId,
            lightMarkerNow: true,
            skipChordQuantize: compositionMode
        );

        // Play the note immediately — the playhead is at this step right now.
        // Without this, the note only sounds the next time the loop passes this step.
        p.track.PlayOneShotMidi(chosenMidi, commitVel, p.durationTicks);

        // Visual feedback: pulse the marker and emit the track-color particle burst.
        if (viz != null)
        {
            viz.PulseMarkerSpecial(p.track, targetAbsStep);
            viz.TriggerPlayheadReleasePulse(p.track.assignedRole);
        }

        // Occupied-step reward accent.
        if (occupied && vehicleConfig.occupiedStepOctaveAccent)
            p.track.PlayOneShotMidi(chosenMidi + 12, commitVel, p.durationTicks);
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
    private void TickNoteTrail()
    {
        var gfm = GameFlowManager.Instance;
        var viz = gfm != null ? gfm.noteViz : null;

        if (_pendingNotes.Count == 0 && _armedReleases.Count == 0)
        {
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

        if (_armedReleases.Count > 0)
        {
            var a = _armedReleases.Peek();
            if (a.note.track != null && a.note.track.controller != null &&
                a.note.track.controller.TryGetRawPlayheadAbsStep(out double rawAbsA, out _, out int totalA))
            {
                int total = Mathf.Max(1, totalA);
                double fwdSteps = (a.targetAbsStep - rawAbsA) % total;
                if (fwdSteps < 0) fwdSteps += total;

                var aDrum         = a.note.track.drumTrack;
                int aBinSize      = Mathf.Max(1, aDrum != null ? aDrum.totalSteps : total);
                int aLeaderBins   = Mathf.Max(1, Mathf.CeilToInt(total / (float)aBinSize));
                double aLoopLen   = (aDrum != null ? aDrum.GetLoopLengthInSeconds() : 1f) * aLeaderBins;
                double stepDur    = aLoopLen / total;
                double fwdDsp     = fwdSteps * stepDur;

                pulse01 = 1f - Mathf.Clamp01((float)(fwdDsp / Math.Max(0.001, a.gapDurationDsp)));

                if (a.note.track.IsExpansionPending)
                    pulse01 = Mathf.Min(pulse01, 0.3f);

                int aTargetLocal = ((a.targetAbsStep % aBinSize) + aBinSize) % aBinSize;
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
                var spokenFor = new HashSet<int>();
                foreach (var ar in _armedReleases)
                    spokenFor.Add(ar.targetAbsStep);

                if (viz != null && viz.TryGetNextUnlitStepExcluding(
                        p.track, rawAbsP, total, spokenFor, out int nextStep))
                {
                    double fwdSteps = (nextStep - rawAbsP + total) % total;

                    var pDrum       = p.track.drumTrack;
                    int pBinSize    = Mathf.Max(1, pDrum.totalSteps);
                    int pLeaderBins = Mathf.Max(1, Mathf.CeilToInt(total / (float)pBinSize));
                    double pLoopLen = pDrum.GetLoopLengthInSeconds() * pLeaderBins;
                    double pStepDur = pLoopLen / total;
                    double fwdDsp   = fwdSteps * pStepDur;

                    // Ring window is always manualReleaseArmAheadSteps wide regardless of
                    // whether the target is in an expansion bin. The +lead gives a visible
                    // heads-up before the commit gate opens.
                    const float ringWindowLead = 1.5f;
                    double windowDsp = (vehicleConfig.manualReleaseArmAheadSteps + ringWindowLead) * pStepDur;
                    pulse01 = 1f - Mathf.Clamp01((float)(fwdDsp / Math.Max(0.001, windowDsp)));

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
                float dist = vehicleConfig.trailFirstSlotOffset + armedSlot * vehicleConfig.trailSlotSpacing;
                ar.note.collectable.SetTrailTarget(SampleTrailPosition(dist));
            }

            ar.note.collectable.SetReleasePulse(armedSlot == 0 ? pulse01 : 0f);
            // Drive tether thickness: t01=0 = open window, t01=1 = release now.
            if (armedSlot == 0)
                ar.note.collectable.tether?.SetReleaseProgress(pulse01);
            armedSlot++;
        }

        // Pending notes: trail behind vehicle.
        int slot = armedSlot;
        foreach (var p in _pendingNotes)
        {
            if (p.collectable == null) { slot++; continue; }

            float dist = vehicleConfig.trailFirstSlotOffset + slot * vehicleConfig.trailSlotSpacing;
            p.collectable.SetTrailTarget(SampleTrailPosition(dist));
            p.collectable.SetReleasePulse(slot == 0 && _armedReleases.Count == 0 ? pulse01 : 0f);
            slot++;
        }
    }
    /// <summary>
    /// Called on every musical step tick. Drives the beat-dot countdown.
    /// Works for both armed and pending states.
    /// </summary>
    private void OnStepTickForReleaseCue(int stepIndex, int leaderSteps)
    {
        if (releaseCue == null) return;

        // Determine target step — armed takes priority, otherwise use next unlit placeholder.
        int targetStep = -1;
        double gapDsp  = 0;
        DrumTrack drum = null;

        if (_armedReleases.Count > 0)
        {
            var a  = _armedReleases.Peek();
            targetStep = a.targetAbsStep;
            gapDsp     = a.gapDurationDsp;
            drum       = a.note.track?.drumTrack;
        }
        else if (_pendingNotes.Count > 0)
        {
            var p = _pendingNotes.Peek();
            drum = p.track?.drumTrack;
            if (p.track?.controller != null &&
                p.track.controller.TryGetRawPlayheadAbsStep(out double rawAbsP, out _, out int totalP))
            {
                var spokenFor = new HashSet<int>();
                var gfm = GameFlowManager.Instance;
                var viz = gfm?.noteViz;
                if (viz != null && viz.TryGetNextUnlitStepExcluding(
                        p.track, rawAbsP, totalP, spokenFor, out int nextStep))
                {
                    targetStep = nextStep;
                    // Gap for pending: distance from now to target, in DSP seconds.
                    int pBinSize    = Mathf.Max(1, drum != null ? drum.totalSteps : leaderSteps);
                    int pLeaderBins = Mathf.Max(1, Mathf.CeilToInt(leaderSteps / (float)pBinSize));
                    double pLoopLen = (drum != null ? drum.GetLoopLengthInSeconds() : 1f) * pLeaderBins;
                    double pStepDur = pLoopLen / Mathf.Max(1, leaderSteps);
                    double fwdSteps = (nextStep - rawAbsP + totalP) % totalP;
                    gapDsp = Math.Max(0.001, fwdSteps * pStepDur);
                }
            }
        }

        if (targetStep < 0 || drum == null) return;

        int binSize    = Mathf.Max(1, drum.totalSteps);
        int leaderBins = Mathf.Max(1, Mathf.CeilToInt(leaderSteps / (float)binSize));
        double loopLen = drum.GetLoopLengthInSeconds() * leaderBins;
        double stepDur = loopLen / Mathf.Max(1, leaderSteps);

        int gapStepsNow = Mathf.Max(1, Mathf.RoundToInt((float)(gapDsp / stepDur)));

        double fwd      = (targetStep - stepIndex + (double)leaderSteps) % leaderSteps;
        int stepsLeft   = Mathf.Max(0, Mathf.RoundToInt((float)fwd));

        releaseCue.SetBeatsRemaining(stepsLeft, gapStepsNow);
    }
    private void UpdateVehiclePlacementResonance(float pulse01, InstrumentTrack cueTrack, bool isAuthoritative = true)
    {
        if (!vehicleConfig.useVehiclePlacementResonance)
            return;

        Color roleColor = _vehicleDefaultColor;
        if (cueTrack != null)
            roleColor = cueTrack.trackColor;

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

    public void CollectEnergy(int amount)
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
            float impactCapVelocity = 32f;
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
            }

            if (activeTrail != null)
            {
                activeTrail.GetComponent<TrailRenderer>().emitting = true; // Enable the trail's emission
            }
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
        _burnRateMultiplier = 0f; // Reset the multiplier when not boosting

        // Disable the trail's emission when boosting stops
        if (activeTrail != null)
        {
            activeTrail.GetComponent<TrailRenderer>().emitting = false;
        }
    }
    public float GetCumulativeSpentTanks() {
        if (capacity <= 0f) return 0f;
        return _cumulativeEnergySpent / capacity;
    }
    private void RefreshVehicleKeepClearIfNeeded() {
        if (gfm.BridgePending || gfm.GhostCycleInProgress) return;
        if (!vehicleConfig.keepDustClearAroundVehicle) return;

        // Throttle refresh
        if (Time.time < _nextVehicleKeepClearRefreshAt) return;
        _nextVehicleKeepClearRefreshAt = Time.time + Mathf.Max(0.02f, vehicleConfig.vehicleKeepClearRefreshSeconds);
        if (gfm == null) return;

        var gen = gfm.dustGenerator;
        var drum = gfm.activeDrumTrack;
        if (gen == null || drum == null) return;

        int ownerId = GetInstanceID();

        Vector2Int centerCell = drum.WorldToGridPosition(rb.position);

        if (!boosting)
        {
            // Claim just the center cell (radius=0, no force-remove) so dust can never
            // regrow directly under the vehicle, without actively clearing any dust.
            gen.SetVehicleKeepClear(ownerId, centerCell, 0, forceRemoveExisting: false);
            return;
        }

        // While boosting, maintain the full pocket and actively clear any dust inside it.
        gen.SetVehicleKeepClear(
            ownerId,
            centerCell,
            Mathf.Max(0, vehicleKeepClearRadiusCells),
            forceRemoveExisting: true,
            forceRemoveFadeSeconds: 0.20f
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
        int ownerId = GetInstanceID();
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

        gfm.dustGenerator.ReleaseVehicleKeepClear(GetInstanceID());
    }
    private void OnDestroy()
    {
        // Unhook step-tick before the object is gone.
        var drum = GameFlowManager.Instance?.activeDrumTrack;
        if (drum != null) drum.OnStepChanged -= OnStepTickForReleaseCue;

        var gfm = GameFlowManager.Instance;
        var gen = (gfm != null) ? gfm.dustGenerator : null;
        if (gen == null) return;

        gen.ReleaseVehicleKeepClear(GetInstanceID());
    }
    private void ConsumeEnergy(float amount)
        {
            energyLevel -= amount;
            energyLevel = Mathf.Max(0, energyLevel); // Clamp to 0
            _cumulativeEnergySpent += Mathf.Max(0f, amount);
            if (energyLevel <= 0)
            {
                rb.linearVelocity = Vector2.zero;
                rb.angularVelocity = 0f;
                    Explode explode = GetComponent<Explode>();
                    if (explode != null)
                    {
                        explode.Permanent();
                    }
                    GameFlowManager.Instance.CheckAllPlayersOutOfEnergy();
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
        if (gfm == null || gfm.dustGenerator == null || drumTrack == null) return;
        if (gfm.BridgePending || gfm.GhostCycleInProgress) return;
        if (!boosting) return;

        Vector2 vel = rb.linearVelocity;
        if (vel.magnitude < profile.plowMinSpeed) return;
        if (Time.time < _nextPlowTickAt) return;
        _nextPlowTickAt = Time.time + Mathf.Max(0.01f, vehicleConfig.plowTickSeconds);

        var gen = gfm.dustGenerator;
        float cellSize = Mathf.Max(0.001f, drumTrack.GetCellWorldSize());
        Vector2 forward = vel.normalized;
        Vector2 perp    = new Vector2(-forward.y, forward.x);
        var motif = GameFlowManager.Instance?.phaseTransitionManager?.currentMotif;
        float fade = Mathf.Max(0.01f, vehicleConfig.plowFadeSeconds);
        int halfW = Mathf.Max(0, profile.plowHalfWidthCells);
        int depth = Mathf.Max(0, profile.plowDepthCells);

        int chipAmount = Mathf.Max(1, profile.plowChipAmount);
        for (int d = 0; d <= depth; d++)
        {
            for (int s = -halfW; s <= halfW; s++)
            {
                Vector2    sampleWorld = rb.position
                    + forward * (d * cellSize)
                    + perp    * (s * cellSize);
                Vector2Int cell = drumTrack.WorldToGridPosition(sampleWorld);
                if (gen.HasDustAt(cell))
                    gen.ChipDustByVehicle(cell, chipAmount, fade);
            }
        }
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

        _pendingNotes.Enqueue(p);
        return true;
    }

    // Back-compat with earlier patches / external callers.
    public bool EnqueuePendingNote(PendingCollectedNote p) => EnqueuePendingCollectedNote(p);
    
    public bool TryReleaseQueuedNote()
{
    if (_pendingNotes.Count <= 0) return false;

    var gfm = GameFlowManager.Instance;
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
        viz?.BlastManualReleaseCueFailure(transform, p.track, p.authoredAbsStep);
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
        _pendingNotes.Dequeue();
        if (p.collectable != null) p.collectable.OnManualReleaseDiscarded();
        p.track.NotifyNoteDiscarded(p.burstId, p.authoredAbsStep);
        viz?.BlastManualReleaseCueFailure(transform, p.track, p.authoredAbsStep);
        CollectionSoundManager.Instance?.PlayReleaseFailure();
        return false;
    }

    int binSize = Mathf.Max(1, p.track.drumTrack.totalSteps);
    double fwdToTarget = (targetAbsStep - rawAbs + effectiveTotal) % effectiveTotal;

    bool inAheadWindow = fwdToTarget <= vehicleConfig.manualReleaseArmAheadSteps;
    double backFromTarget = effectiveTotal - fwdToTarget;
    bool inGraceWindow = vehicleConfig.manualReleaseGracePeriodSteps > 0f &&
                         backFromTarget <= vehicleConfig.manualReleaseGracePeriodSteps;
    bool pass = inAheadWindow || inGraceWindow;
    Debug.Log($"[RELEASE_GATE] target={targetAbsStep} rawAbs={rawAbs:F2} fwd={fwdToTarget:F2} back={backFromTarget:F2} window={vehicleConfig.manualReleaseArmAheadSteps:F1} grace={vehicleConfig.manualReleaseGracePeriodSteps:F1} effectiveTotal={effectiveTotal} PASS={pass}");

    if (!pass)
    {
        Debug.Log($"[RELEASE_BLOCKED] target={targetAbsStep} rawAbs={rawAbs:F2} fwd={fwdToTarget:F2} window={vehicleConfig.manualReleaseArmAheadSteps:F1} PASS=False commitSkipped=True");

        // Keep the note queued so the same pending note can still be released on a
        // later valid input action. This preserves cadence and avoids accidentally
        // dropping notes during bin transitions.
        viz?.BlastManualReleaseCueFailure(transform, p.track, p.authoredAbsStep);
        return false;
    }

    // Window passed — now consume the note.
    _pendingNotes.Dequeue();

    if (pass && vehicleConfig.manualReleaseUseArmLock)
    {
        // stepDur must use the full leader loop length (all bins × clip length), because
        // totalSteps from TryGetRawPlayheadAbsStep is also leader-scoped.
        // Using GetLoopLengthInSeconds() (single bin) here produces a stepDur that is
        // leaderBins× too small, making gapDurationDsp leaderBins× too small.
        var drum = p.track.drumTrack;
        int leaderBins = Mathf.Max(1, Mathf.CeilToInt(effectiveTotal / (float)binSize));
        double leaderLoopLen = drum.GetLoopLengthInSeconds() * leaderBins;
        double stepDur = leaderLoopLen / Mathf.Max(1.0f, effectiveTotal);
        double gapDsp  = fwdToTarget * stepDur;

        _armedReleases.Enqueue(new ArmedRelease
        {
            note           = p,
            targetAbsStep  = targetAbsStep,
            totalAbsSteps  = effectiveTotal,
            gapDurationDsp = gapDsp
        });
        return true;
    }

    if (pass)
    {
        CommitManualReleaseAtStep(p, targetAbsStep);
        viz?.BlastManualReleaseCue(transform);
        return true;
    }

    // Defensive guard: all non-pass paths should have returned above.
    Debug.Log($"[RELEASE_BLOCKED] target={targetAbsStep} rawAbs={rawAbs:F2} fwd={fwdToTarget:F2} window={vehicleConfig.manualReleaseArmAheadSteps:F1} PASS=False commitSkipped=True");
    return false;
}
}
