using System;
using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using Random = UnityEngine.Random;

public class Vehicle : MonoBehaviour
{

    // ---------------------------------------------------------------------
    // Manual Note Release (FIFO queue)
    // ---------------------------------------------------------------------
    [Header("Manual Note Release")]
    [SerializeField] private bool enableManualNoteRelease = false;
    [SerializeField] private int manualReleaseQueueCapacity = 9;

    [Tooltip("When releasing onto an already-occupied step, multiply committed velocity by this factor.")]
    [SerializeField] private float occupiedStepVelocityMultiplier = 1.25f;

    [Tooltip("On occupied-step releases, play a one-shot octave accent (+12) in addition to the committed note.")]
    [SerializeField] private bool occupiedStepOctaveAccent = true;


    public bool ManualNoteReleaseEnabled => enableManualNoteRelease;

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

    // ------------------------------------------------------------
    // Manual-release cue (visual guidance)
    // ------------------------------------------------------------
    private void Update()
    {
        RecordPositionHistory();
        TickManualReleaseCue();
        TickNoteTrail();
    }

 private void TickManualReleaseCue()
{
    if (!enableManualNoteRelease) return;

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

        if (crossed || fwd <= manualReleaseAutoCommitEpsSteps)
        {
            CommitManualReleaseAtStep(armed, a.targetAbsStep);
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

        int binSize = Mathf.Max(1, p.track.drumTrack.totalSteps);
        int targetLocal = ((targetAbsStep % binSize) + binSize) % binSize;
        bool matchesAuthored = (p.authoredAbsStep < 0) || (targetAbsStep == p.authoredAbsStep);
        bool localMatch = (targetLocal == p.authoredLocalStep);
        int chosenMidi = (matchesAuthored || localMatch) ? p.collectedMidi : p.authoredRootMidi;

        bool occupied = p.track.IsPersistentStepOccupied(targetAbsStep);
        float commitVel = p.velocity127;
        if (occupied)
            commitVel = Mathf.Clamp(commitVel * occupiedStepVelocityMultiplier, 1f, 127f);

        // Write the note to the loop (lights the marker via AddNoteToLoop reuse path).
        p.track.CommitManualReleasedNote(
            stepAbs: targetAbsStep,
            midiNote: chosenMidi,
            durationTicks: p.durationTicks,
            velocity127: commitVel,
            authoredRootMidi: p.authoredRootMidi,
            burstId: p.burstId,
            lightMarkerNow: true
        );

        // Play the note immediately — the playhead is at this step right now.
        // Without this, the note only sounds the next time the loop passes this step.
        p.track.PlayOneShotMidi(chosenMidi, commitVel, p.durationTicks);

        // Visual feedback: pulse the marker and emit the track-color particle burst.
        if (viz != null)
        {
            viz.PulseMarkerSpecial(p.track, targetAbsStep);
            viz.TriggerPlayheadReleasePulse();
        }

        // Occupied-step reward accent.
        if (occupied && occupiedStepOctaveAccent)
            p.track.PlayOneShotMidi(chosenMidi + 12, commitVel, p.durationTicks);

        // Mark as collected BEFORE destroying so OnCollectableDestroyed skips the
        // "lost note" branch and does not double-decrement _burstRemaining.
        if (p.collectable != null) p.collectable.MarkAsReportedCollected();
        if (p.collectable != null) p.collectable.OnManualReleaseConsumed();
    }
    // ---- Note Trail Management ----

    private void RecordPositionHistory()
    {
        if (!enableManualNoteRelease) return;

        int cap = Mathf.Max(8, trailHistoryCapacity);
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
        _posHistoryLast = cur;

        // Record at a density of ~4 samples per slot-spacing so we have smooth curve data
        float sampleDist = Mathf.Max(0.01f, trailSlotSpacing * 0.25f);
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
        if (_posHistory == null || _posHistoryCount < 2)
            return transform.position - (Vector3)(rb ? rb.linearVelocity.normalized * distance : Vector2.up * distance);

        float remaining = distance;
        // Start from the newest entry (head-1) and walk backwards
        int idx = (_posHistoryHead - 1 + _posHistory.Length) % _posHistory.Length;
        Vector3 prev = _posHistory[idx];

        for (int i = 1; i < _posHistoryCount; i++)
        {
            int nextIdx = (idx - 1 + _posHistory.Length) % _posHistory.Length;
            Vector3 next = _posHistory[nextIdx];

            float seg = Vector3.Distance(prev, next);
            if (seg <= 0f) { idx = nextIdx; prev = next; continue; }

            if (remaining <= seg)
                return Vector3.Lerp(prev, next, remaining / seg);

            remaining -= seg;
            idx = nextIdx;
            prev = next;
        }

        // Ran out of history — extrapolate from last known direction
        return prev;
    }

    private void TickNoteTrail()
    {
        if (!enableManualNoteRelease) return;

        var gfm = GameFlowManager.Instance;
        var viz = gfm != null ? gfm.noteViz : null;

        // When both queues are empty, explicitly zero the cue and exit.
        if (_pendingNotes.Count == 0 && _armedReleases.Count == 0)
        {
            releaseCue?.SetFill(0f);
            return;
        }

        // ------------------------------------------------------------------
        // Compute pulse01 — how close the playhead is to the next target step.
        // Armed note takes priority over pending; both use the same approach:
        // remaining DSP time / gap DSP time, so the ramp is gap-normalised and
        // stable across bin expansions.
        // ------------------------------------------------------------------
        float pulse01 = 0f;

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
                    double windowDsp = (manualReleaseArmAheadSteps + ringWindowLead) * pStepDur;
                    pulse01 = 1f - Mathf.Clamp01((float)(fwdDsp / Math.Max(0.001, windowDsp)));
                }
            }
        }

        // Drive the vehicle-local release cue ring.
        releaseCue?.SetFill(pulse01);

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
                float dist = trailFirstSlotOffset + armedSlot * trailSlotSpacing;
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

            float dist = trailFirstSlotOffset + slot * trailSlotSpacing;
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

    [Header("Impact Dig Tuning")]
    [Tooltip("Maximum trench length (cells) for the best ship at top speed when digging the softest dust.")]
    [SerializeField] private int maxDigCellsSoft = 10;
    [SerializeField] private float minDigCellsWhenBoosting = 1.0f; // allows digging out when boxed in
    [Tooltip("Minimum time between impact digs per vehicle (seconds). Prevents multiple dust colliders from triggering multiple chain reactions on the same strike).")]
    [SerializeField] private float impactDigCooldownSeconds = 0.12f;
    [Tooltip("Per-cell budget cost before hardness. 1.0 means budget is in 'soft cells'.")]
    [SerializeField] private float digBaseCellCost = 1.0f;
    [Tooltip("Additional per-cell cost added by dust hardness01. 1.0 -> default hardness (0.5) costs 1.5 budget per cell.")]
    [SerializeField] private float digHardnessCost = 1.0f;
    private float _lastImpactDigAt = -999f;
    public ShipMusicalProfile profile;
    public float capacity = 10f;
    private float _baseBurnAmount = 1f;
    private float _burnRateMultiplier = 1f; // Multiplier for the burn rate based on trigger pressure
    // === Arcade RB2D tuning ===
    [Header("Arcade Movement")]
    [SerializeField]
    public float arcadeMaxSpeed = 14f;
    [SerializeField] private float arcadeAccel = 40f;
    [SerializeField] private float arcadeBoostAccel = 80f;
    [SerializeField] private float arcadeLinearDamping = 2f;   // typical 0–5
    [SerializeField] private float arcadeAngularDamping = 0.5f; // optional: reduces spin after bumps
    [SerializeField] private bool  requireBoostForThrust = false; // set true if you want “no boost, no thrust”
    [Header("Coast/Stop (mass-dependent)")]
    [SerializeField] private float coastBrakeForce   = 6f;   // N per (m/s). F = -k*v (independent of mass)
    [SerializeField] private float stopSpeed         = 0.05f; // snap-to-rest threshold (m/s)
    [SerializeField] private float stopAngularSpeed  = 5f;   // deg/s
    private float _cumulativeEnergySpent = 0f;
    [Header("Input Filtering")]
    [SerializeField] float inputDeadzone = 0.20f;   // tune to your stick
    [SerializeField] float inputTimeout  = 0.15f;   // seconds before we auto-zero if Move() isn’t called
    [Header("Recovery / Out-of-Bounds")]
    [SerializeField] private bool enableRecovery = true;
    [SerializeField] private float viewportOobMargin = 0.15f; // allow some overshoot before recovery
    [SerializeField] private float minSecondsBetweenRecoveries = 0.75f;
    [SerializeField] private int respawnSearchRadiusCells = 8;
    [SerializeField] private float stuckSpeedThreshold = 0.35f;   // "not moving"
    [SerializeField] private float stuckSecondsInVoid = 0.60f;    // time inside void + not moving before eject

    [Header("Gravity Void Detection")]
    [SerializeField] private LayerMask gravityVoidMask; // set in inspector OR leave 0 and use tag fallback below
    [SerializeField] private string gravityVoidTag = "GravityVoid"; // optional fallback if you don’t want a layer
    [SerializeField] private float voidProbeRadiusWorld = 0.6f; // around vehicle center

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
    [SerializeField] private bool isLocked = false;
    private Vector3 lastPosition;
    private DrumTrack drumTrack;
    private bool incapacitated = false;
    private double loopStartDSPTime;
    private float _lastDamageTime = -1f;
    private Coroutine flickerPulseRoutine;
    private bool isFlickering = false;
    
    [Header("Dust Legibility Pocket")]
    [SerializeField] private bool keepDustClearAroundVehicle = true;
    [SerializeField] private int vehicleKeepClearRadiusCells = 1;
    [SerializeField] private float vehicleKeepClearRefreshSeconds = 0.10f;
    private float _nextVehicleKeepClearRefreshAt = 0f;

    private Coroutine _spawnRestPocketCo;

    [Header("Dust Spawn Rest Pocket")]
    [Tooltip("Carves a small pocket at spawn so the vehicle is not born intersecting dust colliders.")]
    [SerializeField] private bool carveSpawnRestPocket = true;
    [Tooltip("If true, compute the pocket radius from the vehicle collider bounds and the drum grid cell size.")]
    [SerializeField] private bool spawnRestPocketAutoRadius = true;
    [Tooltip("Used when Auto Radius is disabled.")]
    [SerializeField] private int spawnRestPocketRadiusCells = 1;
    [Tooltip("Fade time (seconds) for the initial pocket carve.")]
    [SerializeField] private float spawnRestPocketFadeSeconds = 0.05f;
    [Tooltip("Delay (seconds) before carving the pocket. Useful if spawn ordering is tight.")]
    [SerializeField] private float spawnRestPocketDelaySeconds = 0.0f;

    [Header("Scale Calibration (Debug)")] 
    [SerializeField] private bool logScaleCalibrationOnAssign = true;
    private bool _scaleCalibrationLogged = false;
// Replace the frac field with steps
    [Header("Manual Release Timing")]
    [Range(0f, 2f)]
    public float manualReleaseWindowSteps = 1f; // legacy: direct-release hit window (steps)

    [Tooltip("If true, button press ARMS the next unlit placeholder if it is within Arm Ahead Steps, then the note auto-commits when the playhead reaches that step. This is less twitchy and supports mashy sequences.")]
    [SerializeField] private bool manualReleaseUseArmLock = true;

    [Tooltip("How far ahead (in steps, fractional) the next placeholder can be for the press to count as an ARM. If the next placeholder is farther, the press discards.")]
    [Range(0.5f, 16f)]
    [SerializeField] private float manualReleaseArmAheadSteps = 6f;

    [Tooltip("How close (in steps) the playhead must be to the armed target for auto-commit. Keep small; this is NOT the player timing window.")]
    [Range(0.05f, 1.0f)]
    [SerializeField] private float manualReleaseAutoCommitEpsSteps = 0.35f;

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
    [Header("Note Trail")]
    [Tooltip("World-space spacing between queued notes trailing behind the vehicle.")]
    [SerializeField] private float trailSlotSpacing = 0.55f;

    [Tooltip("How far behind the vehicle the first note trails (world units).")]
    [SerializeField] private float trailFirstSlotOffset = 0.7f;

    [Tooltip("Number of historical positions stored for trail direction sampling.")]
    [SerializeField] private int trailHistoryCapacity = 48;

    [Tooltip("Steps ahead within which the release pulse starts building (0 = no pulse until release).")]
    [SerializeField] private float trailReleasePulseSteps = 4f;

    // Ring buffer of recent world positions (populated in Update)
    private Vector3[] _posHistory;
    private int _posHistoryHead;
    private int _posHistoryCount;
    private float _posHistoryAccum; // distance accumulator for spacing
    private Vector3 _posHistoryLast;
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

                rb.linearDamping  = arcadeLinearDamping;
                rb.angularDamping = arcadeAngularDamping;
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
            if (enableManualNoteRelease)
            {
                var drum = GameFlowManager.Instance?.activeDrumTrack;
                if (drum != null) drum.OnStepChanged += OnStepTickForReleaseCue;
            }

            // --- Spawn rest pocket ---
            // The vehicle often spawns inside a solid dust tile by design (teaches boosting),
            // but we still need a tiny free volume so we don't start interpenetrating colliders.
            if (carveSpawnRestPocket)
            {
                if (_spawnRestPocketCo != null) StopCoroutine(_spawnRestPocketCo);
                _spawnRestPocketCo = StartCoroutine(Co_CarveSpawnRestPocket());
            }
        }
    void FixedUpdate() {

        if (incapacitated) return;
    // --- PhaseStar Safety Bubble: dust has no effect inside the bubble ---
        if (PhaseStar.IsPointInsideSafetyBubble(transform.position))
        {
        }

        float dt = Time.fixedDeltaTime;
        RefreshVehicleKeepClearIfNeeded();
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
        if (Time.time - _lastMoveStamp > inputTimeout) _moveInput = Vector2.zero;

        bool hasInput  = _moveInput.sqrMagnitude > 0.0001f;
        bool canThrust = !requireBoostForThrust || boosting;

        // ---- movement ----
        if(canThrust && (hasInput || boosting)) {
            // Target velocity from input (single env scalar)
            Vector2 steerDir = hasInput ? _moveInput.normalized : (_lastNonZeroInput.sqrMagnitude > 0f ? _lastNonZeroInput : (Vector2)transform.up);
            // Target velocity from steer direction (sing env scalar)
            Vector2 desiredVel = steerDir * arcadeMaxSpeed;
            // Acceleration pick (handles boost-only ships with accel=0)
            float accelUsed;
            if (requireBoostForThrust)
                accelUsed = boosting ? arcadeBoostAccel : 0f;
            else
                accelUsed = (boosting ? arcadeBoostAccel : arcadeAccel);

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
            if (v.sqrMagnitude > 0f && coastBrakeForce > 0f)
                rb.AddForce(-v * coastBrakeForce, ForceMode2D.Force);

            // Snap to full rest near zero to kill jitter tails
            if (v.magnitude < stopSpeed && Mathf.Abs(rb.angularVelocity) < stopAngularSpeed)
            {
                rb.linearVelocity        = Vector2.zero;
                rb.angularVelocity = 0f;
            }
        }

        // Fuel burn only while boosting (keeps your baseBurnAmount/burnRateMultiplier economy)
        if (boosting && energyLevel > 0f)
        {
            float burn = _burnRateMultiplier * _baseBurnAmount * dt;
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
    if (enableRecovery)
    {
        UpdateSafeAnchor();
        RecoverIfNeeded();
    }
    
}
    private void UpdateSafeAnchor()
{
    if (rb == null || drumTrack == null) return;

    // Only record anchor when we are reasonably “in play”.
    // (Avoid recording while already offscreen or during a trap.)
    if (IsFarOutsideViewport(viewportOobMargin * 0.5f)) return;

    // If you want, you can also avoid anchoring while inside a void:
    // if (IsInsideGravityVoid(out _, out _)) return;

    _lastSafeWorld = rb.position;
    _lastSafeCell = drumTrack.WorldToGridPosition(rb.position);
    _hasSafeAnchor = true;
}

private void RecoverIfNeeded()
{
    if (Time.time - _lastRecoverAt < minSecondsBetweenRecoveries) return;
    if (rb == null || drumTrack == null || gfm == null || gfm.dustGenerator == null) return;

    // 1) Hard OOB: if we’re well outside camera viewport, snap back.
    if (IsFarOutsideViewport(viewportOobMargin))
    {
        DoSnapRespawn("viewport_oob");
        return;
    }

    // 2) Void trap: if we’re inside a void collider AND not moving for a while, do localized eject or snap.
    if (IsInsideGravityVoid(out var voidCenter, out var voidRadius))
    {
        float speed = rb.linearVelocity.magnitude;

        if (speed <= stuckSpeedThreshold)
            _timeStuckInVoid += Time.fixedDeltaTime;
        else
            _timeStuckInVoid = 0f;

        if (_timeStuckInVoid >= stuckSecondsInVoid)
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
        voidProbeRadiusWorld,
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
    float margin = Mathf.Max(0.15f, voidProbeRadiusWorld * 0.5f);
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
    if (!TryFindNearbyEmptyCell(_lastSafeCell, respawnSearchRadiusCells, out var respawnCell))
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

private bool TryFindNearbyEmptyCell(Vector2Int around, int maxRadius, out Vector2Int found)
{
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

    ConsumeEnergy(amount);
}
    
    public void ApplyShipProfile(ShipMusicalProfile p, bool refillEnergy = true)
    {
        profile = p;
        // Movement
        arcadeMaxSpeed       = p.arcadeMaxSpeed;
        arcadeAccel          = p.arcadeAccel;
        arcadeBoostAccel     = p.arcadeBoostAccel;
        arcadeLinearDamping  = p.arcadeLinearDamping;
        arcadeAngularDamping = p.arcadeAngularDamping;
        requireBoostForThrust= p.requireBoostForThrust;

        // Coast / Stop / Input
        coastBrakeForce      = p.coastBrakeForce;
        stopSpeed            = p.stopSpeed;
        stopAngularSpeed     = p.stopAngularSpeed;
        inputDeadzone        = p.inputDeadzone;

        // Physics
        rb.mass = p.mass;

        // Fuel tradeoffs (keep your semantics)
        capacity = p.capacity;                    // tank size (you already track capacity/energyLevel)
        _baseBurnAmount *= p.burnEfficiency;       // ship-specific efficiency multiplier
        if (refillEnergy) energyLevel = capacity; // start full on selection

        // Apply damping now
        rb.linearDamping  = arcadeLinearDamping;
        rb.angularDamping = arcadeAngularDamping;
    }
    public void SetColor(Color newColor)
    {
        if (baseSprite != null)
            baseSprite.color = newColor;

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
            LogScaleCalibrationOnce();
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
        if (isLocked) return;

        if (direction.magnitude < inputDeadzone) direction = Vector2.zero;

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
    public void TurnOnBoost(float triggerValue)
    {
        
        if (energyLevel > 0 && !boosting)        {
            boosting = true;

            if (audioManager != null && thrustClip != null)
                audioManager.PlayLoopingSound(thrustClip, .5f);
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
//        _remixController.ResetRemixVisuals();
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
    private void LogScaleCalibrationOnce() { 
        if (_scaleCalibrationLogged || !logScaleCalibrationOnAssign) return; 
        _scaleCalibrationLogged = true;
        gfm = GameFlowManager.Instance; 
        var drums = drumTrack != null ? drumTrack : (gfm != null ? gfm.activeDrumTrack : null); 
        if (drums == null) { 
            Debug.LogWarning($"[Scale] {name}: DrumTrack not assigned yet; cannot report vehicle↔cell scale.", this); 
            return;
        }
        float cell = Mathf.Max(0.0001f, drums.GetCellWorldSize());
        
        // Vehicle size proxy: prefer a CircleCollider2D radius; otherwise use bounds extents.
        float r = 0.5f; 
        var cc = GetComponent<CircleCollider2D>();
        if (cc != null)
            r = cc.radius * Mathf.Abs(transform.lossyScale.x);
        else { 
            var col = GetComponent<Collider2D>(); 
            if (col != null) r = Mathf.Max(col.bounds.extents.x, col.bounds.extents.y);
        } 
        float diameter = r * 2f; 
        float ratio = diameter / cell; // 1.0 means vehicle ~ 1 cell wide
    }
    private void RefreshVehicleKeepClearIfNeeded() {
        if (gfm.BridgePending || gfm.GhostCycleInProgress) return;
        if (!keepDustClearAroundVehicle) return;

        // Throttle refresh
        if (Time.time < _nextVehicleKeepClearRefreshAt) return;
        _nextVehicleKeepClearRefreshAt = Time.time + Mathf.Max(0.02f, vehicleKeepClearRefreshSeconds);
        if (gfm == null) return;

        var gen = gfm.dustGenerator;
        var drum = gfm.activeDrumTrack;
        if (gen == null || drum == null) return;

        var phaseNow = (gfm.phaseTransitionManager != null)
            ? gfm.phaseTransitionManager.currentPhase
            : MazeArchetype.Establish;

        int ownerId = GetInstanceID();

        // If not boosting, ensure we RELEASE any previous footprint continuously.
        // (This prevents stale keep-clear claims that permanently veto regrowth.)
        if (!boosting)
        {
            gen.ReleaseVehicleKeepClear(ownerId, phaseNow);
            return;
        }

        // While boosting, maintain the pocket and optionally remove dust.
        Vector2Int centerCell = drum.WorldToGridPosition(rb.position);

        gen.SetVehicleKeepClear(
            ownerId,
            centerCell,
            Mathf.Max(0, vehicleKeepClearRadiusCells),
            phaseNow,
            forceRemoveExisting: true,
            forceRemoveFadeSeconds: 0.20f
        );
    }
    private IEnumerator Co_CarveSpawnRestPocket()
    {
        // Optional delay to allow spawn ordering (dust grid, drumTrack, etc.) to settle.
        if (spawnRestPocketDelaySeconds > 0f)
            yield return new WaitForSeconds(spawnRestPocketDelaySeconds);
        else
            yield return null; // at least one frame so the dust grid exists

        if (gfm == null) yield break;

        var gen = gfm.dustGenerator;

        if (gen == null || drumTrack == null) yield break;

        var phaseNow = (gfm.phaseTransitionManager != null)
            ? gfm.phaseTransitionManager.currentPhase
            : MazeArchetype.Establish;

        // Compute which cell we're currently in.
        Vector2 pos = (rb != null) ? rb.position : (Vector2)transform.position;
        Vector2Int centerCell = drumTrack.WorldToGridPosition(pos);

        // Choose a radius that guarantees we are not born overlapping walls.
        int radiusCells = Mathf.Max(0, spawnRestPocketRadiusCells);
        if (spawnRestPocketAutoRadius)
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
            phaseNow,
            forceRemoveExisting: true,
            forceRemoveFadeSeconds: Mathf.Max(0.01f, spawnRestPocketFadeSeconds)
        );
        gen.ReleaseVehicleKeepClear(ownerId, phaseNow);
    }
    void OnDisable()
    {
        if (gfm == null || gfm.dustGenerator == null) return;

        var phaseNow = (gfm.phaseTransitionManager != null)
            ? gfm.phaseTransitionManager.currentPhase
            : MazeArchetype.Establish;

        gfm.dustGenerator.ReleaseVehicleKeepClear(GetInstanceID(), phaseNow);
    }
    private void OnDestroy()
    {
        // Unhook step-tick before the object is gone.
        if (enableManualNoteRelease)
        {
            var drum = GameFlowManager.Instance?.activeDrumTrack;
            if (drum != null) drum.OnStepChanged -= OnStepTickForReleaseCue;
        }

        var gfm = GameFlowManager.Instance;
        var gen = (gfm != null) ? gfm.dustGenerator : null;
        if (gen == null) return;

        var phaseNow = (gfm.phaseTransitionManager != null)
            ? gfm.phaseTransitionManager.currentPhase
            : MazeArchetype.Establish;

        gen.ReleaseVehicleKeepClear(GetInstanceID(), phaseNow);
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
        Debug.Log($"[VEHICLE:COLLISION] hit '{coll.collider.name}' layer={coll.collider.gameObject.layer}", this);
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

        // ---- Impact dig (dust maze) ----
        // Design intent: a single strike on collision entry triggers a chain reaction trench down a grid line.
        if (!boosting) return;
        if (gfm == null || gfm.dustGenerator == null || drumTrack == null) return;

        // Cooldown prevents multiple dust colliders from generating multiple chain reactions on one strike.
        if (Time.time - _lastImpactDigAt < Mathf.Max(0.01f, impactDigCooldownSeconds))
            return;

        _lastImpactDigAt = Time.time;

        DoImpactDig(coll);
}

    // ------------------------------------------------------------------------
    // Impact Dig (chain reaction trench)
    // ------------------------------------------------------------------------
private void DoImpactDig(Collision2D coll)
{
    if (coll == null) return;
    if (coll.contactCount <= 0) return;
    if (!boosting) return;
    if (gfm == null || gfm.dustGenerator == null || drumTrack == null) return;

    var gen = gfm.dustGenerator;

    // --- Choose the contact that best represents "into-surface" impact ---
    Vector2 relV = coll.relativeVelocity;
    if (rb != null && relV.sqrMagnitude < 0.0001f)
        relV = rb.linearVelocity;

    var best = coll.GetContact(0);
    float bestInto = float.NegativeInfinity;

    for (int i = 0; i < coll.contactCount; i++)
    {
        var c = coll.GetContact(i);
        float into = Vector2.Dot(relV, -c.normal); // >0 means we're moving into the surface
        if (into > bestInto)
        {
            bestInto = into;
            best = c;
        }
    }

    Vector2 contactWorld = best.point;

    // --- Impact measurement (only into-normal speed scales trench) ---
    Vector2 v = (rb != null) ? rb.linearVelocity : relV;
    float intoSpeed = Vector2.Dot(v, -best.normal);
    if (intoSpeed < 0f) intoSpeed = 0f;

    // --- Direction ---
    // If you actually hit into the surface, dig straight into it.
    // Otherwise (boxed-in / grazing), dig in the player's intended direction, but only 1 cell.
    const float kMinIntoForMulti = 0.25f; // not a "tuning number" so much as noise floor; adjust if needed
    bool hasMeaningfulImpact = intoSpeed >= kMinIntoForMulti;

    Vector2 digDirWorld;
    if (hasMeaningfulImpact)
    {
        digDirWorld = -best.normal;
    }
    else if (_moveInput.sqrMagnitude > 0.0001f)
    {
        digDirWorld = _moveInput.normalized;
    }
    else if (_lastNonZeroInput.sqrMagnitude > 0.0001f)
    {
        digDirWorld = _lastNonZeroInput.normalized;
    }
    else
    {
        digDirWorld = -best.normal;
    }

    if (digDirWorld.sqrMagnitude < 0.0001f) return;
    digDirWorld.Normalize();

    // --- Resolve entry cell ---
    if (!TryFindEntryDustCell(contactWorld, resolveRadiusCells: 1, out var gp))
        return;

    // Quantize direction to 8-way grid step.
    Vector2Int step = QuantizeDir8_ToGridStep(digDirWorld);
    if (step == Vector2Int.zero) return;

    // --- Determine intended trench length ---
    // Rule: touching + boost always removes exactly 1 cell.
    int cellsToCarve = 1;

    if (hasMeaningfulImpact)
    {
        // Map intoSpeed -> extra cells.
        // Shape: starts at 1, climbs with impact, clamped.
        // You can change these later without changing the model.
        const float kIntoForMax = 6.0f;     // "full-speed hit" reference
        const int   kMaxCells   = 12;       // absolute cap per strike

        float t = Mathf.InverseLerp(kMinIntoForMulti, kIntoForMax, intoSpeed);
        t = Mathf.Clamp01(t);

        // Slightly convex curve so small impacts don't explode into long trenches.
        t = t * t;

        int extra = Mathf.FloorToInt(t * (kMaxCells - 1));
        cellsToCarve = 1 + Mathf.Clamp(extra, 0, kMaxCells - 1);
    }

    // --- Hardness shortens trenches ---
    // Always carve the first cell. For subsequent cells, hardness may terminate.
    // Impact helps push slightly through hardness (optional but feels good).
    float impactT01 = 0f;
    if (hasMeaningfulImpact)
    {
        const float kIntoForMax = 6.0f;
        impactT01 = Mathf.InverseLerp(kMinIntoForMulti, kIntoForMax, intoSpeed);
        impactT01 = Mathf.Clamp01(impactT01);
    }

    float ContinueChance(float hardness01)
    {
        float h = Mathf.Clamp01(hardness01);
        float baseContinue = 1f - h;          // hard => low continue chance
        float impactBonus  = 0.65f * impactT01 * h; // impact slightly counters hardness only when hard
        return Mathf.Clamp01(baseContinue + impactBonus);
    }

    Vector2Int last = new Vector2Int(int.MinValue, int.MinValue);

    for (int i = 0; i < cellsToCarve; i++)
    {
        if (gp == last) { gp += step; continue; }
        last = gp;

        if (!gen.TryGetDustAt(gp, out var dust) || dust == null)
            break;

        // First cell guaranteed.
        if (i > 0 && hasMeaningfulImpact)
        {
            float p = ContinueChance(dust.clearing.hardness01);
            if (UnityEngine.Random.value > p)
                break;
        }

        gen.CarveDustAt(gp, fadeSeconds: 0.20f);
        gp += step;
    }

    Debug.Log($"[VEHICLE:DIG] intoSpeed={intoSpeed:F2} meaningful={hasMeaningfulImpact} cells={cellsToCarve} step={step}");
}
    private bool TryFindEntryDustCell(Vector2 world, int resolveRadiusCells, out Vector2Int gp)
{
    gp = default;
    if (gfm == null || gfm.dustGenerator == null || drumTrack == null) return false;

    var gen = gfm.dustGenerator;
    var dt = drumTrack;

    Vector2Int baseCell = dt.WorldToGridPosition(world);

    // Fast path: direct hit.
    if (gen.TryGetDustAt(baseCell, out var dust) && dust != null)
    {
        gp = baseCell;
        return true;
    }

    int r = Mathf.Clamp(resolveRadiusCells, 0, 4);
    if (r == 0) return false;

    // Search ring (closest-first-ish). This avoids "neighbor resolution" for the whole line;
    // it's only for finding an occupied entry cell when the contact lands between colliders.
    for (int dy = -r; dy <= r; dy++)
    {
        for (int dx = -r; dx <= r; dx++)
        {
            if (dx == 0 && dy == 0) continue;
            Vector2Int c = new Vector2Int(baseCell.x + dx, baseCell.y + dy);
            if (gen.TryGetDustAt(c, out dust) && dust != null)
            {
                gp = c;
                return true;
            }
        }
    }

    return false;
}
    private static readonly Vector2Int[] _dir8 =
    {
    new Vector2Int( 1, 0),
    new Vector2Int( 1, 1),
    new Vector2Int( 0, 1),
    new Vector2Int(-1, 1),
    new Vector2Int(-1, 0),
    new Vector2Int(-1,-1),
    new Vector2Int( 0,-1),
    new Vector2Int( 1,-1),
};
    private Vector2Int QuantizeDir8_ToGridStep(Vector2 worldDir)
{
    // Assumes your dust grid axes align with world X/Y.
    // If your grid is rotated, convert worldDir to grid-local here.
    if (worldDir.sqrMagnitude < 0.0001f) return Vector2Int.zero;
    worldDir.Normalize();

    float best = -999f;
    Vector2Int bestStep = Vector2Int.zero;

    for (int i = 0; i < _dir8.Length; i++)
    {
        Vector2 cand = ((Vector2)_dir8[i]).normalized;
        float d = Vector2.Dot(worldDir, cand);
        if (d > best)
        {
            best = d;
            bestStep = _dir8[i];
        }
    }

    return bestStep;
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
    public bool EnqueuePendingCollectedNote(PendingCollectedNote p)
    {
        if (!enableManualNoteRelease) return false;

        int cap = Mathf.Max(1, manualReleaseQueueCapacity);

        // If full, drop oldest (and clean up its visual carrier if still around)
        while (_pendingNotes.Count >= cap)
        {
            var dropped = _pendingNotes.Dequeue();
            if (dropped.collectable != null)
                dropped.collectable.OnManualReleaseDiscarded();
        }

        _pendingNotes.Enqueue(p);
        return true;
    }

    // Back-compat with earlier patches / external callers.
    public bool EnqueuePendingNote(PendingCollectedNote p) => EnqueuePendingCollectedNote(p);

    // Convenience overload (common call-site shape).
    public bool EnqueuePendingNote(
        InstrumentTrack track,
        Collectable collectable,
        int collectedMidi,
        int authoredRootMidi,
        int authoredLocalStep,
        int durationTicks,
        float velocity127,
        int burstId)
    {
        var pending = new PendingCollectedNote
        {
            track = track,
            collectable = collectable,
            collectedMidi = collectedMidi,
            authoredRootMidi = authoredRootMidi,
            authoredLocalStep = authoredLocalStep,
            durationTicks = durationTicks,
            velocity127 = velocity127,
            burstId = burstId
        };
        return EnqueuePendingCollectedNote(pending);
    }


    public int GetPendingCollectedNoteCount() => _pendingNotes.Count;

public bool TryReleaseQueuedNote()
{
    if (!enableManualNoteRelease) return false;
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
        viz?.BlastManualReleaseCue(transform);
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
        viz?.BlastManualReleaseCue(transform);
        return false;
    }

    int binSize = Mathf.Max(1, p.track.drumTrack.totalSteps);
    double fwdToTarget = (targetAbsStep - rawAbs + effectiveTotal) % effectiveTotal;

    Debug.Log($"[RELEASE_GATE] target={targetAbsStep} rawAbs={rawAbs:F2} fwd={fwdToTarget:F2} window={manualReleaseArmAheadSteps:F1} effectiveTotal={effectiveTotal} PASS={fwdToTarget <= manualReleaseArmAheadSteps}");

    if (fwdToTarget > manualReleaseArmAheadSteps)
    {
        // Outside window — discard the note. Counts toward burst completion silently.
        _pendingNotes.Dequeue();
        p.track.DiscardManualReleasedNote(p.burstId);
        if (p.collectable != null) p.collectable.OnManualReleaseDiscarded();
        viz?.BlastManualReleaseCue(transform);
        return false;
    }

    // Window passed — now consume the note.
    _pendingNotes.Dequeue();

    if (manualReleaseUseArmLock)
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

    CommitManualReleaseAtStep(p, targetAbsStep);
    viz?.BlastManualReleaseCue(transform);
    return true;
}
}