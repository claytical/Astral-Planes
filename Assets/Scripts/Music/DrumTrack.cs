using System;
using UnityEngine;
using System.Collections.Generic;
using System.Collections;
using System.Linq;
// One completed playthrough of one authored chapter.
// Sealed at phase completion and added to GardenRecord.

public class DrumTrack : MonoBehaviour
{
    private int _boundarySerial = 0;
    private PlayArea _lastPlayAreaForTileCache;
    private bool _hasLastPlayAreaForTileCache = false;
    public GameObject phaseStarPrefab;
    public GameObject mineNodePrefab;
    private float _pendingBpm;
    private int _pendingTotalSteps;
    private bool _pendingTimingValid;
    [SerializeField] private bool logBeatSeqGates = false;
    private int _beatSeqGateSpamGuard = 0;
    private string _lastMotifSetBy = "never";
    private int _motifSetSerial = 0;
// --- Session-relative intensity (aggregate across all players) ---
    [SerializeField, Tooltip("EMA smoothing for the session baseline (aggregate energy burn per loop). Higher adapts faster.")]
    private float sessionBurnEmaAlpha = 0.25f;

    [SerializeField, Tooltip("How many multiples above baseline maps to full intensity (>=1). 2.5 means 2.5x baseline => intensity 1.")]
    private float burnMultipleAtFullIntensity = 2.5f;
    private float _lateBindMotifTimer = 0f;
    private const float kLateBindMotifInterval = 1.0f;
    private float _lastTotalSpentSample = -1f; // baseline sample of TOTAL spent tanks (cumulative)
    private float _burnBaselineEma = 0f;       // EMA of per-loop delta (aggregate)
    private double _lastApplyMotifDsp = -1.0;
    private string _lastApplyMotifId = "";
    [HideInInspector]
    public float drumLoopBPM = 120f;
    [HideInInspector]
    public int totalSteps = 16;
    public float gridPadding = 0f;
    public float timingWindowSteps = .25f; // Can shrink to 0.5 or less as game progresses
    public AudioSource drumAudioSource;
    [HideInInspector]
    public double startDspTime;
    private AudioSource _drumA; // primary deck
    private AudioSource _drumB; // secondary deck (created at runtime if missing)
    private AudioSource _activeDrum; // currently audible deck
    private AudioSource _inactiveDrum;
    public double leaderStartDspTime { get; private set; }
    [HideInInspector]
    public List<MotifSnapshot> SessionPhases = new();
    [HideInInspector]
    public List<MineNode> activeMineNodes = new List<MineNode>();
    [HideInInspector]
    public int currentStep;

    [Header("Grid Sizing (Pixel-driven)")]
    [Tooltip("Reference screen width used to derive a default cell pixel size (e.g., 1920).")]
    [SerializeField]
    private int referenceWidthPx = 1920;

    [Tooltip("Reference grid columns used with referenceWidthPx to derive cell pixel size (e.g., 36).")]
    [SerializeField]
    private int referenceColumns = 36;

    [Tooltip("Bottom UI padding in pixels to exclude from the grid area (e.g., 160).")] [SerializeField]
    private int uiBottomPaddingPx = 160;

    [Tooltip("If true, DrumTrack will resize SpawnGrid at runtime to fill the usable screen.")] [SerializeField]
    private bool autoSizeSpawnGridToScreen = true;

    public int completedLoops { get; private set; } = 0;
    private float _loopLengthInSeconds, _phaseStartTime;
    private float _gridCheckTimer;
    private readonly float _gridCheckInterval = 10f;
    private float _clipLengthSec;
    private const float kMinLen = 1e-4f; // guard for zero/denorm lengths
    private bool HasValidClipLen => _clipLengthSec > kMinLen;

    private bool _started;
    private int _tLoop, _phaseCount;
    private AudioClip _pendingDrumLoop;
    private GameFlowManager _gfm;
    private SpawnGrid _spawnGrid;
    private CosmicDustGenerator _dust;
    private InstrumentTrackController _trackController;
    [HideInInspector]
    public StarPool _starPool;
    private PhaseTransitionManager _phaseTransitionManager;

    private float _cachedTileDiameterWorld = -1f;

    private int _binIdx = -1;
    private int _binCount = 4; // default; PhaseStar can override per-spawn
    
    private MotifProfile _motif;
    private List<AudioClip> _entryLoops;
    private List<AudioClip> _intensityLoops;

    private int _entryLoopsRemaining;

    private AudioClip _currentDrumClip;

    public event System.Action OnLoopBoundary; // fire in LoopRoutines()
    public event System.Action<PhaseStarBehaviorProfile> OnPhaseStarSpawned;
    public event System.Action<int, int> OnStepChanged; // (stepIndex, leaderSteps)

    private int _lastStepIdx = -1;
    private bool _driveFromEnergy;

    [SerializeField, Tooltip("How many 'spent tanks' per loop counts as full intensity (E). Tune while testing.")]
    private float tanksPerLoopAtFullIntensity = 0.35f;

    [SerializeField, Tooltip("Optional: minimum change in intensity01 required before allowing a new target profile.")]
    private float intensityHysteresis = 0.08f;

    private float _lastSpentTanksSample = -1f; // baseline at last boundary
    private float _lastIntensity01 = 0f; // for hysteresis
    
    private float EffectiveLoopLengthSec => (_trackController != null) ? _trackController.GetEffectiveLoopLengthInSeconds() : _clipLengthSec;
    public float GetLoopLengthInSeconds() => EffectiveLoopLengthSec;
    public float GetClipLengthInSeconds() => _clipLengthSec; // new helper for audio-bound code
    public struct PlayArea
    {
        public float left;
        public float right;
        public float bottom;
        public float top;
        public float width  => right - left;
        public float height => top - bottom;
    }
    [Header("Play Area Mapping")]
    [SerializeField] private bool lockPlayAreaAfterInit = true;

    private PlayArea _lockedPlayArea;
    private bool _hasLockedPlayArea = false;
    private bool _pendingDrumLoopArmed;
    private double _pendingDrumLoopDspStart;

    /// <summary>
	/// Returns the world-space play area used to map SpawnGrid cells to world positions.
	/// The play area is the visible camera region, clipped so it does not overlap the NoteVisualizer.
	/// </summary>
    private PlayArea GetPlayAreaWorld()
	{
		TryGetPlayAreaWorld(out var area);
		return area;
	}
	private static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

	public bool TryGetPlayAreaWorld(out PlayArea area)
	{
        if (lockPlayAreaAfterInit && _hasLockedPlayArea)
        {
            area = _lockedPlayArea;
            return true;
        }

		area = default;

		// We want DrumTrack to be the sole authority for grid→world mapping.
		// Do not depend on NoteVisualizer (it may not be initialized yet, and its layout can change).
		if (!HasSpawnGrid()) return false;

		var cam = Camera.main;
		if (cam == null) return false;

		// Prefer orthographic projection (your game is 2D). If not orthographic, fall back to viewport corners.
		float halfH = cam.orthographicSize;
		float halfW = halfH * cam.aspect;
		float left, right, bottom, top;
		float z = cam.orthographic ? 0f : Mathf.Abs(cam.transform.position.z);

		if (cam.orthographic)
		{
			// Anchor to world origin (0,0) so the play area aligns with Boundaries.cs,
			// which positions all four boundary colliders at ±halfW/±halfH from world
			// origin — independent of where the camera happens to sit.
			left   = -halfW;
			right  = +halfW;
			bottom = -halfH;
			top    = +halfH;
		}
		else
		{
			// Perspective camera safety path.
			Vector3 bl = cam.ViewportToWorldPoint(new Vector3(0f, 0f, z));
			Vector3 tr = cam.ViewportToWorldPoint(new Vector3(1f, 1f, z));
			left   = bl.x;
			right  = tr.x;
			bottom = bl.y;
			top    = tr.y;
		}

		// Optional padding to keep spawns away from the very edge of the screen.
		if (gridPadding > 0f)
		{
			left   += gridPadding;
			right  -= gridPadding;
			bottom += gridPadding;
			top    -= gridPadding;
		}

		// Reserve a bottom viewport band for UI derived from uiBottomPaddingPx.
		float uiBotV = Screen.height > 0 ? Mathf.Clamp01(uiBottomPaddingPx / (float)Screen.height) : 0f;
		if (uiBotV > 0f)
		{
			// For orthographic cameras map the viewport fraction from world origin,
			// consistent with the world-anchored bounds above.
			float uiBottomWorld = cam.orthographic
				? Mathf.Lerp(-halfH, halfH, uiBotV)
				: cam.ViewportToWorldPoint(new Vector3(0f, uiBotV, z)).y;
			bottom = Mathf.Max(bottom, uiBottomWorld);
		}
		// Validate.
		if (!IsFinite(left) || !IsFinite(right) || !IsFinite(bottom) || !IsFinite(top)) return false;
		if (right <= left || top <= bottom) return false;

		area.left = left;
		area.right = right;
		area.bottom = bottom;
		area.top = top;
        if (lockPlayAreaAfterInit && !_hasLockedPlayArea)
        {
            _lockedPlayArea = area;
            _hasLockedPlayArea = true;
        }
		return true;
	}
    public int GetCommittedBinCount() => Mathf.Max(1, _binCount);
    public int GetBoundarySerial() => _boundarySerial;
    public void SetBinCount(int bins)
    {
        // Bin count here is used for *visual/logic binning inside the leader loop* (OnBinChanged),
        // not for capacity. Do NOT override to a track's maxLoopMultiplier (capacity), because that
        // causes the system to behave as if bins 2..N exist even when they have not been committed.
        //
        // Authority:
        // - InstrumentTrackController (or callers) decide the current *committed* leader bin count.
        // - DrumTrack simply clamps and applies it.
        _binCount = Mathf.Max(1, bins);
    }
    private AudioClip ChooseEntryClip()
    {
        if (_entryLoops == null || _entryLoops.Count == 0) return null;
        int i = UnityEngine.Random.Range(0, _entryLoops.Count);
        return _entryLoops[i];
    }

    private AudioClip ResolveIntensityClip(float intensity01)
    {
        if (_intensityLoops == null || _intensityLoops.Count == 0) return null;

        int n = _intensityLoops.Count;
        if (n == 1) return _intensityLoops[0];

        intensity01 = Mathf.Clamp01(intensity01);

        // Even mapping: 0 -> 0, 1 -> n-1
        int idx = Mathf.RoundToInt(intensity01 * (n - 1));
        idx = Mathf.Clamp(idx, 0, n - 1);

        return _intensityLoops[idx];
    }

    private void ApplyMotif(MotifProfile motif, bool armAtNextBoundary, string who, bool restartTransport = false)
{
    // ------------------------------------------------------------
    // 0) Spam detection + provenance
    // ------------------------------------------------------------
    string incomingId = motif ? motif.motifId : "null";
    double dspNow = AudioSettings.dspTime;

    // Detect “motif spam”: same motif being applied again within ~1–2 loops.
    if (_lastApplyMotifId == incomingId && _lastApplyMotifDsp > 0 && (dspNow - _lastApplyMotifDsp) < 10.0)
    {
        Debug.LogWarning(
            $"[DRUM][MOTIF][SPAM] Reapplying motif={incomingId} within {(dspNow - _lastApplyMotifDsp):F3}s by {who}\n" +
            Environment.StackTrace
        );
    }

    _lastApplyMotifId = incomingId;
    _lastApplyMotifDsp = dspNow;

    _lastMotifSetBy = who;
    _motifSetSerial++;
    Debug.Log($"[DRUM][MOTIF][SET#{_motifSetSerial}] by {who}: incoming motif={incomingId}");

    // ------------------------------------------------------------
    // 1) Snapshot old state BEFORE overwrite
    // ------------------------------------------------------------
    MotifProfile oldMotif = _motif;

    int oldSteps = (oldMotif != null) ? oldMotif.stepsPerLoop : totalSteps;
    float oldBpm = (oldMotif != null) ? oldMotif.bpm : drumLoopBPM;

    bool motifChanged = (oldMotif != motif);

    bool stepsChanged = (oldMotif != null && motif != null && oldMotif.stepsPerLoop != motif.stepsPerLoop);
    bool bpmChanged   = (oldMotif != null && motif != null && Mathf.Abs(oldMotif.bpm - motif.bpm) > 0.001f);

    // ------------------------------------------------------------
    // 2) Apply motif refs (idempotent with respect to counters when same motif)
    // ------------------------------------------------------------
    _motif = motif;
    _entryLoops     = (_motif != null) ? _motif.entryDrumLoops : null;
    _intensityLoops = (_motif != null) ? _motif.intensityDrumLoops : null;

    _driveFromEnergy = (_motif != null) && _motif.driveBeatsFromEnergy;
    // Only (re)open the entry window and reset intensity sampling when motif actually changed,
    // OR when we explicitly restart transport (hard reset semantics).
    if (motifChanged || restartTransport)
    {
        _entryLoopsRemaining = (_motif != null) ? Mathf.Max(0, _motif.entryLoopCount) : 0;

        // Reset intensity sampling for new motif (prevents “stuck at 0” after first motif).
        _lastSpentTanksSample = -1f;
        _lastTotalSpentSample = -1f;
        _burnBaselineEma = 0f;
        // Preserve intensity when timing is identical — same BPM and step count means the drum
        // clips loop at the same rate, so there's no perceptual discontinuity to reset from.
        if (stepsChanged || bpmChanged)
            _lastIntensity01 = 0f;
    }
    // else: same motif being reapplied; do NOT reset entry/intensity counters.

    // Asset sanity warnings (always useful; does not mutate state)
    if (_motif != null)
    {
        if (!_motif.driveBeatsFromEnergy)
            Debug.LogWarning($"[DRUM][MOTIF] Motif {_motif.motifId} has driveBeatsFromEnergy=FALSE. Beat intensity will never run.");

        int entryCt = _motif.entryDrumLoops != null ? _motif.entryDrumLoops.Count : 0;
        int intCt   = _motif.intensityDrumLoops != null ? _motif.intensityDrumLoops.Count : 0;

        if (entryCt == 0)
            Debug.LogWarning($"[DRUM][MOTIF] Motif {_motif.motifId} has 0 entryDrumLoops.");
        if (intCt == 0)
            Debug.LogWarning($"[DRUM][MOTIF] Motif {_motif.motifId} has 0 intensityDrumLoops. Intensity changes cannot occur.");
    }

    // Choose clip for this motif (clip choice can be immediate even if timing is pending).
    AudioClip clip = ChooseEntryClip();

    // ------------------------------------------------------------
    // 3) Timing commit policy (preserve your existing transport-safety rules)
    // ------------------------------------------------------------
    if (_started && (stepsChanged || bpmChanged) && !restartTransport)
    {
        // If caller asked for an unarmed swap, force arming (safe boundary).
        if (!armAtNextBoundary)
        {
            Debug.LogWarning(
                $"[DRUM][MOTIF] Timing changed (steps {oldSteps}->{(motif ? motif.stepsPerLoop : oldSteps)}, " +
                $"bpm {oldBpm}->{(motif ? motif.bpm : oldBpm)}) but armAtNextBoundary==false and restartTransport==false. " +
                $"Forcing armAtNextBoundary=true to avoid transport mismatch."
            );
            armAtNextBoundary = true;
        }

        // Defer timing changes until the swap actually commits.
        _pendingBpm = (motif != null) ? motif.bpm : drumLoopBPM;
        _pendingTotalSteps = (motif != null) ? motif.stepsPerLoop : totalSteps;
        _pendingTimingValid = true;

        Debug.Log(
            $"[DRUM][MOTIF] ApplyMotif(DEFERRED TIMING) by {who}: motif={(motif ? motif.motifId : "null")} " +
            $"steps {oldSteps}->{_pendingTotalSteps} bpm {oldBpm}->{_pendingBpm} " +
            $"clip={(clip ? clip.name : "null")} armAtNextBoundary={armAtNextBoundary} restart={restartTransport} " +
            $"motifChanged={motifChanged}"
        );
    }
    else
    {
        // Safe to apply timing immediately when:
        // - we haven't started (ManualStart will schedule), OR
        // - restartTransport==true (immediate reschedule), OR
        // - no timing change.
        if (_motif != null)
        {
            drumLoopBPM = _motif.bpm;
            totalSteps  = _motif.stepsPerLoop;
        }

        _pendingTimingValid = false;

        Debug.Log(
            $"[DRUM][MOTIF] ApplyMotif by {who}: motif={(_motif ? _motif.motifId : "null")} " +
            $"entryLoops={(_entryLoops != null ? _entryLoops.Count : 0)} intensityLoops={(_intensityLoops != null ? _intensityLoops.Count : 0)} " +
            $"entryLoopRemaining={_entryLoopsRemaining} drive={_driveFromEnergy} bpm={drumLoopBPM} steps={totalSteps} " +
            $"clip={(clip ? clip.name : "null")} armAtNextBoundary={armAtNextBoundary} restart={restartTransport} " +
            $"motifChanged={motifChanged}"
        );
    }

    // ------------------------------------------------------------
    // 4) Clip guards + scheduling
    // ------------------------------------------------------------
    if (clip == null)
    {
        Debug.LogWarning($"[DRUM][MOTIF] No entry clip available for motif '{(_motif ? _motif.motifId : "null")}' (by {who}).");
        return;
    }

    // If we haven't started yet, let ManualStart handle scheduling.
    if (!_started)
        return;

    if (restartTransport)
    {
        // Hard reset both decks, then schedule fresh.
        try { if (_activeDrum != null) _activeDrum.Stop(); } catch { }
        try { if (_inactiveDrum != null) _inactiveDrum.Stop(); } catch { }
        _pendingDrumLoop = null;
        _pendingDrumLoopArmed = false;

        // Since we're restarting, timing must be committed immediately.
        if (_motif != null)
        {
            drumLoopBPM = _motif.bpm;
            totalSteps  = _motif.stepsPerLoop;
        }
        _pendingTimingValid = false;

        EnsureDualDrumSources();
        if (_activeDrum == null) return;

        _activeDrum.clip = clip;
        _activeDrum.loop = true;
        _clipLengthSec = Mathf.Max(clip.length, 0f);

        double dspStart = AudioSettings.dspTime + 0.05;
        _activeDrum.PlayScheduled(dspStart);
        drumAudioSource = _activeDrum;
        startDspTime = dspStart;
        leaderStartDspTime = dspStart;
        _currentDrumClip = clip;
        return;
    }

    // Normal path: arm a pending clip and let Update() schedule the swap at a safe boundary.
    _pendingDrumLoop = clip;
    _pendingDrumLoopArmed = armAtNextBoundary;
}
    public void SetMotifBeatSequence(MotifProfile motif, bool armAtNextBoundary, string who, bool restartTransport = false) {
        ApplyMotif(motif, armAtNextBoundary, who, restartTransport);
    }
    
    private void ArmPendingDrumLoopForNextLeaderBoundary(double nextBoundaryDsp, double effectiveLoopLen)
{
    if (!_pendingDrumLoopArmed || _pendingDrumLoop == null)
        return;

    EnsureDualDrumSources();
    if (_activeDrum == null || _inactiveDrum == null)
        return;

    double dspNow = AudioSettings.dspTime;

    // We *want* to change near the leader boundary, but we must not cut a looping drum clip mid-bar.
    // So: pick a swap time at/after nextBoundaryDsp that lands on a drum-bar boundary (clip boundary).
    double swapDsp = nextBoundaryDsp;

    if (_activeDrum.clip != null && _activeDrum.clip.length > 0.0001f)
    {
        double barLen = _activeDrum.clip.length;

        // Anchor bar counting off the current *leader* start so swaps stay musically consistent.
        // IMPORTANT: do NOT re-anchor leaderStartDspTime to a FUTURE swap time (that freezes transport).
        double t = swapDsp - leaderStartDspTime;
        if (t < 0) t = 0;

        double bars = System.Math.Ceiling(t / barLen);
        swapDsp = leaderStartDspTime + bars * barLen;
    }

    // Safety: must schedule in the future
    if (swapDsp <= dspNow + 0.01)
        swapDsp = dspNow + 0.05;

    var newClip = _pendingDrumLoop;
    if (newClip == null)
    {
        _pendingDrumLoopArmed = false;
        return;
    }


    _inactiveDrum.clip = newClip;
    _inactiveDrum.loop = true;
    _inactiveDrum.playOnAwake = false; 

    // Try to end active at the swap boundary (avoids overlaps)
    try
    {
        _activeDrum.SetScheduledEndTime(swapDsp);
    }
    catch (Exception e)
    {
        Debug.LogError($"[DRUM] SetScheduledEndTime FAILED on active clip={(_activeDrum.clip ? _activeDrum.clip.name : "null")} swapDsp={swapDsp:F3}\n{e}");
    }
    // Schedule the new loop, but DO NOT swap references yet.
    // The currently-audible deck must remain authoritative until swapDsp arrives.
    // Note: assigning .clip above already stopped any prior playback on _inactiveDrum,
    // so there is no need to check isPlaying before calling PlayScheduled.
    _inactiveDrum.PlayScheduled(swapDsp);

    _pendingDrumLoopDspStart = swapDsp;

    // Keep flags so Update() can finalize the deck swap when DSP reaches swapDsp.
    _pendingDrumLoopArmed = false;
    _pendingDrumLoop = null;

    Debug.Log($"[DRUM] Armed drum loop swap for dsp={swapDsp:F3} clip={newClip.name}");
}

    // 1) Late-bind motif is ONE-SHOT (only when _motif==null). It will NOT re-apply repeatedly.
    // 2) Transport boundary catch-up remains the same.
    // 3) Finalize A/B deck swap occurs BEFORE step/bin calculations (so events reflect the audible deck ASAP).
    // 4) Watchdog remains at end.
    // 5) Guards are consolidated so we don’t early-return before finalizing a swap.

    private void Update()
{
    // 0) Manager may exist but not be ready (or still wiring scenes)
    if (_gfm == null || !_gfm.ReadyToPlay())
        return;

    // ---------------------------------------------------------------------
    // 0.5) Motif late-bind (recovery) — ONE SHOT ONLY, only if we truly have no motif.
    // Do NOT late-bind just because intensityLoops is empty, drive is false, etc.
    // Those are authored motif settings; late-binding would fight intentional content.
    // ---------------------------------------------------------------------
    _lateBindMotifTimer += Time.deltaTime;
    if (_lateBindMotifTimer >= kLateBindMotifInterval)
    {
        _lateBindMotifTimer = 0f;

        if (_motif == null) // <-- critical: only if we have no motif at all
        {
            var ptm = _phaseTransitionManager != null ? _phaseTransitionManager : GameFlowManager.Instance?.phaseTransitionManager;
            var m = ptm != null ? ptm.currentMotif : null;

            if (m != null)
            {
                Debug.Log($"[DRUM][MOTIF] Late-bind applying PTM motif={m.motifId}");
                ApplyMotif(m, armAtNextBoundary: true, who: "DrumTrack/LateBind", restartTransport: false);
            }
        }
    }

    // 1) Watchdog timer for the spawn grid
    _gridCheckTimer += Time.deltaTime;
    if (_gridCheckTimer >= _gridCheckInterval)
    {
        ValidateSpawnGrid();
        _gridCheckTimer = 0f;
    }

    // ---------------------------------------------------------------------
    // 2) Transport guards (but do NOT return before finalizing pending swap)
    // ---------------------------------------------------------------------
    if (drumAudioSource == null || totalSteps <= 0)
        return;

    // Ensure we have a stable clip length (ManualStart sets _clipLengthSec)
    if (!HasValidClipLen)
        return;

    // ---- Leader-loop transport (single source of truth) ----
    if (leaderStartDspTime <= 0.0)
        leaderStartDspTime = startDspTime;

    // Clamp effective loop len defensively
    double effLen = Mathf.Max(0.0001f, EffectiveLoopLengthSec);

    const int kMaxBoundaryCatchup = 4;
    int catchup = 0;

    // ---------------------------------------------------------------------
    // 2.5) Catch up loop boundaries (in case of hitches)
    // ---------------------------------------------------------------------
    while (AudioSettings.dspTime - leaderStartDspTime >= effLen && catchup < kMaxBoundaryCatchup)
    {
        // We crossed a boundary: advance the anchor to the start of the new loop.
        leaderStartDspTime += effLen;
        completedLoops++;
        _boundarySerial++;

        // This is the boundary we just crossed (start of the *new* loop)
        double boundaryDsp = leaderStartDspTime;

        // Decide target clip based on what happened during the previous loop
        if (logBeatSeqGates) Debug.Log($"[DRUM] Loop boundary dsp={boundaryDsp:F3}");
        HandleBeatSequencingAtLoopBoundary((float)effLen);

        // Fire loop boundary event (expansion/commit handlers may modify EffectiveLoopLengthSec)
        OnLoopBoundary?.Invoke();

        // Re-snapshot effLen *after* handlers
        effLen = Mathf.Max(0.0001f, EffectiveLoopLengthSec);

        // Schedule any pending drum swap for the NEXT boundary (musically stable: listen → respond next loop)
        double nextBoundaryDsp = boundaryDsp + effLen;

        // Ensure scheduling is strictly in the future.
        double dspNow = AudioSettings.dspTime;
        const double kMinLead = 0.010; // 10ms
        if (nextBoundaryDsp <= dspNow + kMinLead)
            nextBoundaryDsp = dspNow + 0.050; // fallback lead if we’re already too close

        ArmPendingDrumLoopForNextLeaderBoundary(nextBoundaryDsp, effLen);

        catchup++;
    }

    // ---------------------------------------------------------------------
    // 3) Finalize any pending A/B deck swap as soon as it becomes audible.
    // Put this BEFORE step/bin so downstream listeners see the current deck ASAP.
    // ---------------------------------------------------------------------
    if (_pendingDrumLoopDspStart > 0.0)
    {
        double dspNow = AudioSettings.dspTime;
        const double kSwapEps = 0.002; // 2ms guard
        if (dspNow + kSwapEps >= _pendingDrumLoopDspStart)
        {
            // Swap deck references NOW that the new deck is actually audible.
            var prevActive = _activeDrum;
            _activeDrum = _inactiveDrum;
            _inactiveDrum = prevActive;
            drumAudioSource = _activeDrum;
            if (_inactiveDrum != null)
            {
                try { _inactiveDrum.Stop(); } catch { }
            }

            StopAllOtherDrumSources(keepPlaying: _activeDrum);
            // Clip length is now driven by the active deck.
            _clipLengthSec = (_activeDrum != null && _activeDrum.clip != null)
                ? Mathf.Max(_activeDrum.clip.length, 0f)
                : 0f;

            _currentDrumClip = (_activeDrum != null) ? _activeDrum.clip : null;

            // ✅ COMMIT PENDING TIMING NOW THAT THE NEW DECK IS AUDIBLE
            if (_pendingTimingValid)
            {
                drumLoopBPM = _pendingBpm;
                totalSteps  = Mathf.Max(1, _pendingTotalSteps);
                _pendingTimingValid = false;

                if (logBeatSeqGates) Debug.Log(
                    $"[DRUM][MOTIF] Committed pending timing at swap: bpm={drumLoopBPM} steps={totalSteps} " +
                    $"clip={(_currentDrumClip ? _currentDrumClip.name : "null")}"
                );
            }

            if (logBeatSeqGates) Debug.Log($"[DRUM] Finalized drum loop swap at dsp={_pendingDrumLoopDspStart:F3} clip={(_currentDrumClip ? _currentDrumClip.name : "null")}");

            _pendingDrumLoopDspStart = -1.0;
        }
    }

    // ---------------------------------------------------------------------
    // 4) Step indexing aligned to EFFECTIVE loop length
    // ---------------------------------------------------------------------
    int leaderSteps = GetLeaderSteps();

    float effectiveLen = EffectiveLoopLengthSec;
    if (effectiveLen <= 0f) return;

    float elapsedTime = (float)(AudioSettings.dspTime - leaderStartDspTime);
    float stepDuration = (leaderSteps > 0) ? (effectiveLen / leaderSteps) : 0f;
    if (stepDuration <= 0f || float.IsInfinity(stepDuration))
        return;

    float tInLoop = elapsedTime % effectiveLen;
    int absoluteStep = Mathf.FloorToInt(tInLoop / stepDuration);
    currentStep = absoluteStep % leaderSteps;

    if (currentStep != _lastStepIdx)
    {
        _lastStepIdx = currentStep;
        OnStepChanged?.Invoke(currentStep, leaderSteps);
    }

    // ---------------------------------------------------------------------
    // 5) Loop/bins driven by EFFECTIVE loop length
    // ---------------------------------------------------------------------
    if (effectiveLen > kMinLen)
    {
        int bins = Mathf.Max(1, _binCount);
        double dsp = AudioSettings.dspTime;
        double pos = (dsp - leaderStartDspTime) % effectiveLen;
        if (pos < 0) pos += effectiveLen;

        double binDur = effectiveLen / bins;
        const double Eps = 1e-5;

        int idx = (int)((pos + Eps) / binDur);
        if (idx >= bins) idx -= bins;

        if (idx != _binIdx)
        {
            _binIdx = idx;
            //OnBinChanged?.Invoke(_binIdx, bins);
        }
    }

    // ---------------------------------------------------------------------
    // 6) Transport watchdog: if scheduling ever fails, don't stay silent
    // ---------------------------------------------------------------------
    if (_activeDrum != null && _activeDrum.clip != null)
    {
        if (leaderStartDspTime > 0.0 && !_activeDrum.isPlaying)
        {
            double dspNow = AudioSettings.dspTime;
            double restart = dspNow + 0.05;

            try { _activeDrum.Stop(); } catch { }
            _activeDrum.loop = true;
            _activeDrum.PlayScheduled(restart);

            startDspTime = restart;
            leaderStartDspTime = restart;

            _clipLengthSec = Mathf.Max(_activeDrum.clip.length, 0f);

            Debug.LogWarning($"[DRUM] Watchdog restart: clip={_activeDrum.clip.name} dsp={restart:F3}");
        }
    }

    // 7) Housekeeping
    if (activeMineNodes != null && activeMineNodes.Count > 0)
        activeMineNodes.RemoveAll(n => n == null);
}
    public bool TryGetNextBaseStepDsp(out double nextStepDsp, out float stepDurationSec, int stepOffset = 1)
    {
        nextStepDsp = 0;
        stepDurationSec = 0f;

        if (leaderStartDspTime <= 0.0) return false;

        double effLen = Mathf.Max(0.0001f, EffectiveLoopLengthSec);
//        int steps = Mathf.Max(1, totalSteps);   // <- STABLE grid
//        stepDurationSec = (float)(effLen / steps);
        int steps = Mathf.Max(1, GetLeaderSteps());  // NOT totalSteps
        stepDurationSec = (float)(effLen / steps);
        if (stepDurationSec <= 0f || float.IsInfinity(stepDurationSec)) return false;

        double dspNow = AudioSettings.dspTime;
        double elapsed = dspNow - leaderStartDspTime;
        if (elapsed < 0) elapsed = 0;

        double tInLoop = elapsed % effLen;

        int curStep = Mathf.FloorToInt((float)(tInLoop / stepDurationSec));
        int targetStep = curStep + Mathf.Max(1, stepOffset);

        nextStepDsp = leaderStartDspTime + (targetStep * stepDurationSec);

        // ensure future
        const double kMinLead = 0.005;
        if (nextStepDsp <= dspNow + kMinLead)
            nextStepDsp = dspNow + 0.010;

        return true;
    }


    private void HandleBeatSequencingAtLoopBoundary(float loopSeconds)
    {
        // --- hard gates (log on exit) ---
        if (!_driveFromEnergy)
        {
            if (logBeatSeqGates) Debug.Log($"[DRUM][BeatSeq] exit: driveFromEnergy=false motif={(_motif ? _motif.motifId : "null")}");
            return;
        }
        if (_gfm == null)
        {
            if (logBeatSeqGates) Debug.Log($"[DRUM][BeatSeq] exit: _gfm==null motif={(_motif ? _motif.motifId : "null")}");
            return;
        }
        if (_motif == null)
        {
            if (logBeatSeqGates) Debug.Log("[DRUM][BeatSeq] exit: _motif==null");
            return;
        }

        // 1) Respect entry window
        if (_entryLoopsRemaining > 0)
        {
            if (logBeatSeqGates) Debug.Log($"[DRUM][BeatSeq] exit: entry window active remain={_entryLoopsRemaining} motif={_motif.motifId}");
            _entryLoopsRemaining--;
            return;
        }

        // 2) Need an intensity ladder
        int loopsCt = (_intensityLoops != null) ? _intensityLoops.Count : 0;
        if (loopsCt == 0)
        {
            if (logBeatSeqGates) Debug.Log($"[DRUM][BeatSeq] exit: intensityLoops=0 motif={_motif.motifId}");
            return;
        }

        // 3) Aggregate spent (session-relative)
        float totalSpent = _gfm.GetTotalSpentEnergyTanks();

        if (_lastTotalSpentSample < 0f)
        {
            _lastTotalSpentSample = totalSpent;
            if (logBeatSeqGates) Debug.Log($"[DRUM][BeatSeq] baseline acquired totalSpent={totalSpent:F3} motif={_motif.motifId}");
            return;
        }

        float delta = Mathf.Max(0f, totalSpent - _lastTotalSpentSample);
        _lastTotalSpentSample = totalSpent;

        // Update EMA baseline
        float d = Mathf.Max(0.0001f, delta);
        if (_burnBaselineEma <= 0f) _burnBaselineEma = d;

    // Normalize burn to "tanks per second" so tempo/loop-length changes don’t blow up ratio.
        float burnRate = delta / Mathf.Max(0.0001f, loopSeconds);

        if (_burnBaselineEma <= 0f)
            _burnBaselineEma = burnRate; // baseline in tanks/sec

        float a = Mathf.Clamp01(sessionBurnEmaAlpha);
        _burnBaselineEma = Mathf.Lerp(_burnBaselineEma, burnRate, a);

        float ratio = burnRate / Mathf.Max(0.0001f, _burnBaselineEma);
        float full = Mathf.Max(1.0001f, burnMultipleAtFullIntensity);
        float x = Mathf.Max(0f, ratio - 1f);
        float intensity01 = Mathf.Clamp01(x / (full - 1f));
    // Smooth intensity changes (prevents persistent maxing due to spikes)
        const float kIntensitySmooth = 0.35f; // 0..1; higher = faster
        intensity01 = Mathf.Lerp(_lastIntensity01, intensity01, kIntensitySmooth);
        // Hysteresis
        if (Mathf.Abs(intensity01 - _lastIntensity01) < intensityHysteresis)
            intensity01 = _lastIntensity01;
        _lastIntensity01 = intensity01;

        // Empty-bin gate: if every track has no notes in the bin currently playing,
        // force intensity to 0. Intentionally does NOT update _lastIntensity01 so the
        // energy-burn history is preserved and intensity recovers smoothly when notes appear.
        var _ctrl = _gfm?.controller;
        if (_ctrl?.tracks != null)
        {
            bool allEmpty = true;
            foreach (var track in _ctrl.tracks)
            {
                if (track == null) continue;
                // On a boundary callback, completedLoops has already been incremented
                // to the *new* loop. For "what just played" checks, use the previous loop
                // index so we do not accidentally sample bin 0 at wrap (e.g., I-II -> I-I).
                int bin = (Mathf.Max(0, completedLoops - 1)) % Mathf.Max(1, track.loopMultiplier);
                if (track.HasAnyNoteInBin(bin)) { allEmpty = false; break; }
            }
            if (allEmpty) intensity01 = 0f;
        }

        if (logBeatSeqGates) Debug.Log(
            $"[DRUM][BeatSeq] motif={_motif.motifId} total={totalSpent:F3} delta={delta:F3} ema={_burnBaselineEma:F3} " +
            $"ratio={ratio:F3} intensity={intensity01:F3} loops={loopsCt}"
        );

        // 4) Select target clip
        var targetClip = ResolveIntensityClip(intensity01);
        if (targetClip == null)
        {
            Debug.LogWarning($"[DRUM][BeatSeq] exit: ResolveIntensityClip returned null intensity={intensity01:F3} motif={_motif.motifId}");
            return;
        }

        if (logBeatSeqGates) Debug.Log($"[DRUM][BeatSeq] chose clip={targetClip.name} intensity={intensity01:F3}");

        // 5) Avoid redundant scheduling
        if (_pendingDrumLoopArmed && _pendingDrumLoop == targetClip)
        {
            if (logBeatSeqGates) Debug.Log($"[DRUM][BeatSeq] exit: already armed clip={targetClip.name}");
            return;
        }
        if (!_pendingDrumLoopArmed && drumAudioSource != null && drumAudioSource.clip == targetClip)
        {
            if (logBeatSeqGates) Debug.Log($"[DRUM][BeatSeq] exit: already playing clip={targetClip.name}");
            return;
        }
        // If a swap is already scheduled, don't stack another one.
        if (_pendingDrumLoopDspStart > 0.0)
        {
            if (logBeatSeqGates) Debug.Log($"[DRUM][BeatSeq] exit: swap already scheduled dsp={_pendingDrumLoopDspStart:F3}");
            return;
        }
        // 6) Schedule at boundary
        ScheduleDrumLoopChange(targetClip);
        if (logBeatSeqGates) Debug.Log($"[DRUM][BeatSeq] scheduled clip={targetClip.name}");
    }    
    private void EnsureDualDrumSources()
    {
        if (drumAudioSource == null)
        {
            Debug.LogError("[DrumTrack] EnsureDualDrumSources: drumAudioSource is null.");
            return;
        }

        // Deck A is ALWAYS the inspector-assigned drumAudioSource.
        if (_drumA == null) _drumA = drumAudioSource;

        var go = _drumA.gameObject;
        var all = go.GetComponents<AudioSource>();

        // Find a different AudioSource to use as Deck B.
        AudioSource candidateB = null;
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i] != null && all[i] != _drumA)
            {
                candidateB = all[i];
                break;
            }
        }

        if (candidateB == null)
        {
            candidateB = go.AddComponent<AudioSource>();

            // Clone core settings
            candidateB.playOnAwake = false;
            candidateB.outputAudioMixerGroup = _drumA.outputAudioMixerGroup;
            candidateB.volume = _drumA.volume;
            candidateB.pitch = _drumA.pitch;
            candidateB.panStereo = _drumA.panStereo;
            candidateB.spatialBlend = _drumA.spatialBlend;
            candidateB.reverbZoneMix = _drumA.reverbZoneMix;
            candidateB.dopplerLevel = _drumA.dopplerLevel;
            candidateB.spread = _drumA.spread;
            candidateB.rolloffMode = _drumA.rolloffMode;
            candidateB.minDistance = _drumA.minDistance;
            candidateB.maxDistance = _drumA.maxDistance;
            candidateB.priority = _drumA.priority;
        }

        _drumB = candidateB;

        if (_activeDrum == null) _activeDrum = _drumA;
        if (_activeDrum != _drumA && _activeDrum != _drumB) _activeDrum = _drumA;

        _inactiveDrum = (_activeDrum == _drumA) ? _drumB : _drumA;

        drumAudioSource = _activeDrum;
    }
    private void StopAllOtherDrumSources(AudioSource keepPlaying)
    {
        var sources = gameObject.GetComponents<AudioSource>();
        for (int i = 0; i < sources.Length; i++)
        {
            var s = sources[i];
            if (s == null) continue;
            if (s == keepPlaying) continue;

            if (s.isPlaying)
            {
                Debug.LogWarning($"[DRUM] Stopping extra/old AudioSource id={s.GetInstanceID()} clip={(s.clip ? s.clip.name : "null")}");
                try { s.Stop(); } catch { }
            }
        }
    }
    public void ManualStart()
    {
        _gfm = GameFlowManager.Instance;
        if (_gfm != null)
        {
            _spawnGrid = _gfm.spawnGrid;
            _dust = _gfm.dustGenerator;
            _trackController = _gfm.controller;
            _phaseTransitionManager = _gfm.phaseTransitionManager;
        }

        AutoSizeSpawnGridIfEnabled();

        // Optional debug: grid to screen scale sanity
        if (_gfm != null && _spawnGrid != null && _dust != null)
        {
            float tile = GetTileDiameterWorld();
            int w = _spawnGrid.gridWidth;
            float worldW = tile * (w - 1);
            float scrW = GetScreenWorldWidth();
            Debug.Log($"[GridScale] tile={tile:F3}, worldWide(grid)={worldW:F3}, screenWide={scrW:F3}, ratio={worldW / Mathf.Max(0.0001f, scrW):F3}");
        }

        if (_started) return;

        if (drumAudioSource == null)
        {
            Debug.LogError("[BOOT] DrumTrack.ManualStart: No AudioSource assigned (drumAudioSource is null).");
            return;
        }

        // Ensure dual-deck scheduling is available (A/B decks).
        EnsureDualDrumSources();
        if (_activeDrum == null)
        {
            Debug.LogError("[BOOT] DrumTrack.ManualStart: dual-drum source init failed (_activeDrum is null).");
            return;
        }

        // -------------------------------
        // Motif boot: DRIVEN BY PTM
        // -------------------------------
        MotifProfile bootMotif = null;
        if (_phaseTransitionManager != null)
            bootMotif = _phaseTransitionManager.currentMotif;

        if (bootMotif == null)
        {
            Debug.LogWarning(
                "[BOOT] DrumTrack.ManualStart: PTM.currentMotif is null.\n" +
                "Expected GameFlowManager to call PTM.StartPhase(...) before ManualStart.\n" +
                "Falling back to AudioSource.clip timing (may play inspector/default loop)."
            );
        }

        AudioClip initialClip = null;

    // Ensure our internal motif state is applied consistently with later transitions
    // IMPORTANT: PTM.StartPhase already applied the motif to DrumTrack during TrackSetup.
    // ManualStart should NOT re-apply unless DrumTrack somehow missed it.
        if (_motif != null)
        {
            if (ReferenceEquals(_motif, bootMotif))
            {
                Debug.Log($"[BOOT] DrumTrack.ManualStart: motif already applied by PTM ({_motif.motifId}); skipping ApplyMotif.");
            }
            else
            {
                ApplyMotif(bootMotif, armAtNextBoundary: false, who: "DrumTrack/ManualStart", restartTransport: false);
            }
        }
        else if (bootMotif != null)
        {
            ApplyMotif(bootMotif, armAtNextBoundary: false, who: "DrumTrack/ManualStart", restartTransport: false);
        }
        // Fallback: use whatever is on the inspector source
        if (initialClip == null)
            initialClip = drumAudioSource.clip;

        if (initialClip == null)
        {
            Debug.LogError("[BOOT] DrumTrack.ManualStart: no initial drum clip available (ChooseEntryClip + drumAudioSource.clip are null).");
            return;
        }

        // Prevent hearing the inspector/default loop: stop both decks before scheduling.
        try { _activeDrum.Stop(); } catch { }
        try { if (_inactiveDrum != null) _inactiveDrum.Stop(); } catch { }
        StopAllOtherDrumSources(keepPlaying: null);
        _pendingDrumLoop = null;
        _pendingDrumLoopArmed = false;

        // Configure active deck
        _activeDrum.clip = initialClip;
        _activeDrum.loop = true;
        _activeDrum.playOnAwake = false;

        _clipLengthSec = Mathf.Max(initialClip.length, 0f);

        // Start scheduled
        double dspStart = AudioSettings.dspTime + 0.05;
        _activeDrum.PlayScheduled(dspStart);

        // Make the active deck the canonical "drumAudioSource" used elsewhere
        drumAudioSource = _activeDrum;

        startDspTime = dspStart;
        leaderStartDspTime = dspStart;

        _currentDrumClip = initialClip;
        _started = true;

        if (_motif != null)
        {
            Debug.Log($"[BOOT] Drum transport started (motif): clip={initialClip.name} dspStart={dspStart:F3} bpm={drumLoopBPM} steps={totalSteps}");
        }
        else
        {
            // We don't know bpm/steps in fallback mode; keep whatever inspector/default values were set.
            Debug.Log($"[BOOT] Drum transport started (fallback): clip={initialClip.name} dspStart={dspStart:F3} (PTM motif missing)");
        }
    }
    public void ResetBeatSequencingState(string who)
    {
        // Do NOT clear _motif or loop lists. This is a state reset, not a motif reset.
        _lastSpentTanksSample = -1f;
        _lastTotalSpentSample = -1f;
        _burnBaselineEma = 0f;
        _lastIntensity01 = 0f;

        Debug.Log($"[DRUM][BeatSeq] Soft reset by {who} motif={(_motif ? _motif.motifId : "null")}");
    }
    private void AutoSizeSpawnGridIfEnabled() { 
        if (!autoSizeSpawnGridToScreen) return; 
        if (_spawnGrid == null) return;

        // Grid dimensions are fixed to the reference resolution so that dust tile world-size
        // stays uniform across all devices.  What changes per-device is only the camera's
        // orthographic size / world-space extents — not the number of grid cells.
        //
        // Previously cols/rows were derived from actual screen pixels, which caused dust to
        // appear spaced out on high-res laptops (more cells → larger tile world size) and
        // packed on the Steam Deck (fewer cells → smaller tile world size).
        if (referenceWidthPx <= 0 || referenceColumns <= 0)
        {
            Debug.LogWarning("[GridAutoSize] referenceWidthPx or referenceColumns not set; skipping auto-size.");
            return;
        }

        float cellPx  = referenceWidthPx / (float)referenceColumns; // e.g. 1920/36 ≈ 53.33
        int   refCols = referenceColumns;
        // Derive reference row count from the reference height (1080) minus the reference UI padding.
        int   refUsableH = 1080 - Mathf.Max(0, uiBottomPaddingPx);
        int   refRows    = Mathf.Max(1, Mathf.RoundToInt(refUsableH / cellPx));

        int sh = Mathf.Max(1, Screen.height);

        // Apply fixed reference grid — same on every device.
        _spawnGrid.ResizeGrid(refCols, refRows);
        _hasLockedPlayArea = false;

        // Any cached world mapping based on old grid dims must be invalidated.
        InvalidateGridWorldCache();

        Debug.Log($"[GridAutoSize] screen={Screen.width}x{sh} cellPx={cellPx:F2} -> grid={refCols}x{refRows} (reference-locked)");
    }   

    public void RequestPhaseStar(Vector2Int? cellHint = null)
    {
        if (_starPool != null)
        {
            Debug.Log("[SpawnGuard] StarPool already active; abort.");
            return;
        }

        if (!phaseStarPrefab)
        {
            Debug.LogError("[Spawn] PhaseStar prefab is NULL.");
            return;
        }

        if (!_trackController || _trackController.tracks == null || _trackController.tracks.Length == 0)
        {
            Debug.LogError("[Spawn] No instrument tracks available.");
            return;
        }

        var profileAsset = _phaseTransitionManager?.currentMotif?.starBehavior;
        if (_dust && profileAsset) _dust.ApplyProfile(profileAsset);
        if (_gfm && _dust)        _dust.RetintExisting(0.4f);

        IEnumerable<InstrumentTrack> targets = _trackController.tracks.Where(t => t != null);

        MotifProfile motif = _phaseTransitionManager?.currentMotif;
        if (motif == null)
            Debug.LogWarning("[Spawn] No current motif found on PhaseTransitionManager.");

        var poolGo = new GameObject("StarPool");
        _starPool = poolGo.AddComponent<StarPool>();
        _starPool.Initialize(this, motif, profileAsset, targets);

        OnPhaseStarSpawned?.Invoke(profileAsset);
        Debug.Log("[DrumTrack] StarPool created and initialized.");
    }

    public bool TryGetDustAt(Vector2Int cell, out CosmicDust dust)
    {
        dust = null;
        return _dust != null && _dust.TryGetDustAt(cell, out dust);
    }

    public float GetCellWorldSize()
    {
        return GetTileDiameterWorld();
    }

    public Vector2Int CellOf(Vector3 world) => WorldToGridPosition(world);
    public void RegisterMineNode(MineNode obj)
    {
        if (!activeMineNodes.Contains(obj))
        {
            activeMineNodes.Add(obj);
        }
    }

    public void UnregisterMineNode(MineNode obj)
    {
        activeMineNodes.Remove(obj);
    }
    
    private void ValidateSpawnGrid()
    {
        
        if (_gfm == null || _spawnGrid == null)
            return;

        

        for (int x = 0; x < _spawnGrid.gridWidth; x++)
        {
            for (int y = 0; y < _spawnGrid.gridHeight; y++)
            {
                // Skip empty cells outright
                if (_spawnGrid.IsCellAvailable(x, y))
                    continue;

                // 🔐 Only validate cells that *should* belong to Collectables / notes.
                // Do NOT touch Dust or MineNode occupancy here.
                var cell = _spawnGrid.GridCells[x, y];
                if (cell.ObjectType != GridObjectType.Note)
                    continue;

                Vector3 worldPos = GridToWorldPosition(new Vector2Int(x, y));
                Collider2D[] hits = Physics2D.OverlapCircleAll(worldPos, 0.25f);

                bool objectPresent = false;
                foreach (var hit in hits)
                {
                    if (hit.GetComponent<Collectable>())
                    {
                        objectPresent = true;
                        break;
                    }
                }

                if (!objectPresent)
                {
                    _spawnGrid.FreeCell(x, y);
                }
            }
        }
    }
    public void OccupySpawnCell(int x, int y, GridObjectType type)
    {
        if (_spawnGrid == null)
        {
            Debug.LogError("[DrumTrack] OccupySpawnCell: spawnGrid is null");
            return;
        }
        _spawnGrid.OccupyCell(x, y, type);
    }

    private float GetScreenWorldWidth()
    {
        var cam = Camera.main;
        if (!cam)
        {
            Debug.LogWarning("[DrumTrack] GetScreenWorldWidth: Camera.main is null.");
            return 0f;
        }

        float z          = -cam.transform.position.z;
        Vector3 bottomLeft = cam.ViewportToWorldPoint(new Vector3(0f, 0f, z));
        Vector3 topRight   = cam.ViewportToWorldPoint(new Vector3(1f, 1f, z));
        return (topRight.x - gridPadding) - (bottomLeft.x + gridPadding);
    }
    public int GetSpawnGridWidth()
    {
        int gridWidth = _spawnGrid.gridWidth;
        return gridWidth;
    }
    public bool HasDustAt(Vector2Int cell) {
        if (_dust == null) return false; 
        return _dust.HasDustAt(cell);
    }
    public bool IsSpawnCellAvailable(int x, int y)
    {
        
         if (_gfm == null || _spawnGrid == null) return false;
         // 1) Spawn-grid occupancy (nodes, notes, etc.)
         if (!_spawnGrid.IsCellAvailable(x, y))
             return false;
         // 2) Dust blocks spawning (your decision 8A: no spawning inside dust)
          
         if (_dust != null && _dust.HasDustAt(new Vector2Int(x, y))) 
             return false;
         return true;        
    }
    /// <summary>
    /// Navigation predicate distinct from spawn availability.
    /// Returns true if this cell is not occupied by dust. (Other objects may still exist here.)
    /// </summary>

    public bool HasSpawnGrid()
    {
        return _spawnGrid != null;
    }
    public void ResetSpawnCellBehavior(int x, int y)
    {
        _spawnGrid.ResetCellBehavior(x, y);
    }
    public void FreeSpawnCell(int x, int y)
    {
        _spawnGrid.FreeCell(x, y);
    }
    public Vector2Int GetRandomAvailableCell()
    {
        return _spawnGrid.GetRandomAvailableCell();
    }
    public Vector2 GridToWorldPosition(Vector2Int cell)
    {
        if (!TryGetPlayAreaWorld(out var area))
        {
            if (lockPlayAreaAfterInit && _hasLockedPlayArea)
                area = _lockedPlayArea;
            else
                return Vector2.zero;
        }

        GetTileSizeWorld(out float tileX, out float tileY);

        float x = area.left   + cell.x * tileX;
        float y = area.bottom + cell.y * tileY;

        return new Vector2(x, y);
    }
    private void GetTileSizeWorld(out float tileX, out float tileY)
    {
        tileX = 1f;
        tileY = 1f;

        if (!TryGetPlayAreaWorld(out var area))
        {
            if (lockPlayAreaAfterInit && _hasLockedPlayArea)
                area = _lockedPlayArea;
            else
                return;
        }

        int w = Mathf.Max(1, GetSpawnGridWidth());
        int h = Mathf.Max(1, GetSpawnGridHeight());

        // Divide by (count - 1) so cell 0 lands on area.left and cell w-1 lands on area.right,
        // aligning outermost dust cells with the physical boundary colliders.
        tileX = area.width  / Mathf.Max(1, w - 1);
        tileY = area.height / Mathf.Max(1, h - 1);

        if (tileX <= 0.00001f) tileX = 1f;
        if (tileY <= 0.00001f) tileY = 1f;
    }
    public Vector2Int WorldToGridPosition(Vector3 worldPos)
    {
        if (!TryGetPlayAreaWorld(out var area))
        {
            if (lockPlayAreaAfterInit && _hasLockedPlayArea)
                area = _lockedPlayArea;
            else
                return Vector2Int.zero;
        }

        int w = Mathf.Max(1, GetSpawnGridWidth());
        int h = Mathf.Max(1, GetSpawnGridHeight());

        GetTileSizeWorld(out float tileX, out float tileY);

        float gx = (worldPos.x - area.left) / tileX;
        float gy = (worldPos.y - area.bottom) / tileY;

        int ix = Mathf.FloorToInt(gx);
        int iy = Mathf.FloorToInt(gy);

        if (_dust != null && _dust.toroidal)
        {
            ix = ((ix % w) + w) % w;
            iy = ((iy % h) + h) % h;
        }
        else
        {
            ix = Mathf.Clamp(ix, 0, w - 1);
            iy = Mathf.Clamp(iy, 0, h - 1);
        }

        return new Vector2Int(ix, iy);
    }

    /// <summary>
    /// Wraps a grid coordinate toroidally when the dust generator is in toroidal mode.
    /// Returns the input clamped to grid bounds otherwise.
    /// </summary>
    public Vector2Int WrapGridCell(Vector2Int gp)
    {
        if (_dust != null && _dust.toroidal)
            return _dust.WrapCell(gp);
        int w = Mathf.Max(1, GetSpawnGridWidth());
        int h = Mathf.Max(1, GetSpawnGridHeight());
        return new Vector2Int(Mathf.Clamp(gp.x, 0, w - 1), Mathf.Clamp(gp.y, 0, h - 1));
    }
    public float GetTimeToLoopEnd(bool effective = true) { 
        float L = effective ? EffectiveLoopLengthSec : _clipLengthSec; 
        if (L <= 0f) return 0f; 
        float elapsed = (float)((AudioSettings.dspTime - startDspTime) % L); 
        return Mathf.Max(0f, L - elapsed);
    }
    public int GetSpawnGridHeight()
    {
        return _spawnGrid.gridHeight;
    }
    private void ScheduleDrumLoopChange(AudioClip newLoop)
    {
        if (newLoop == null)
        {
            Debug.LogWarning("[DrumTrack] ScheduleDrumLoopChange called with null clip.");
            return;
        }

        // 🔒 If a swap is already scheduled (inactive already has PlayScheduled),
        // DO NOT clear _pendingDrumLoopDspStart, or we will never finalize the swap.
        if (_pendingDrumLoopDspStart > 0.0)
        {
            // If you want, you can remember a "next-next" clip here. For now: ignore.
            Debug.Log($"[DRUM] ScheduleDrumLoopChange ignored; swap already scheduled for dsp={_pendingDrumLoopDspStart:F3} new={newLoop.name}");
            return;
        }

        _pendingDrumLoop = newLoop;
        _pendingDrumLoopArmed = true;

        // Only clear this when we are truly arming a not-yet-scheduled swap.
        _pendingDrumLoopDspStart = -1.0;
    }
    private float GetTileDiameterWorld()
    {
        // Prefer to recompute from current play area + grid dimensions.
        // This must match SyncTileWithScreen() and the GridToWorld/WorldToGrid formulas.
        if (TryGetPlayAreaWorld(out var area))
        {
            int w = Mathf.Max(1, GetSpawnGridWidth());
            int h = Mathf.Max(1, GetSpawnGridHeight());

            // If play area changed, invalidate cache.
            if (!_hasLastPlayAreaForTileCache || !ApproximatelyEqual(area, _lastPlayAreaForTileCache))
            {
                _cachedTileDiameterWorld = 0f;
                _lastPlayAreaForTileCache = area;
                _hasLastPlayAreaForTileCache = true;
            }

            if (_cachedTileDiameterWorld <= 0f)
            {
                float tileX = area.width  / Mathf.Max(1, w - 1);
                float tileY = area.height / Mathf.Max(1, h - 1);
                float tile  = Mathf.Min(tileX, tileY);
                _cachedTileDiameterWorld = (tile > 0f) ? tile : 1f;
            }

            return _cachedTileDiameterWorld;
        }

        // If we can't compute play area, fall back to prior cache if available.
        if (_cachedTileDiameterWorld > 0f)
            return _cachedTileDiameterWorld;

        return 1f;
    }
    private void InvalidateGridWorldCache()
    {
        _cachedTileDiameterWorld = 0f;
        _hasLastPlayAreaForTileCache = false;
        _hasLockedPlayArea = false;
    }
    private bool ApproximatelyEqual(PlayArea a, PlayArea b)
    {
        // Loose epsilon; avoids thrashing cache due to float jitter.
        const float eps = 0.0005f;
        return Mathf.Abs(a.left - b.left) < eps &&
               Mathf.Abs(a.right - b.right) < eps &&
               Mathf.Abs(a.bottom - b.bottom) < eps &&
               Mathf.Abs(a.top - b.top) < eps;
    }
    public void SyncTileWithScreen()
    {
        if (_gfm == null || _spawnGrid == null || _dust == null)
            return;

        int gridW = _spawnGrid.gridWidth;
        int gridH = _spawnGrid.gridHeight;
        if (gridW <= 0 || gridH <= 0) return;

        // Invalidate the cached play area so TryGetPlayAreaWorld recomputes from current values.
        _hasLockedPlayArea = false;
        InvalidateGridWorldCache();

        PlayArea area = GetPlayAreaWorld();
        if (area.width <= 0f || area.height <= 0f) return;

        // NOTE: Dividing by gridW / gridH, NOT (gridW - 1)/(gridH - 1)
        float tileX = area.width  / gridW;
        float tileY = area.height / gridH;

        float tile = Mathf.Min(tileX, tileY);   // square cells; in your case tileX == tileY

        _dust.TileDiameterWorld = tile;
        _cachedTileDiameterWorld = tile;
    }
    public int GetLeaderSteps()
    {
        int baseSteps = Mathf.Max(1, totalSteps);
        
        if (_trackController == null || _trackController.tracks == null || _trackController.tracks.Length == 0)
            return baseSteps;

        int maxMul = 1;
        foreach (var t in _trackController.tracks)
        {
            if (t == null) continue;

            // Prefer deriving the multiplier from the track's declared total steps,
            // since loopMultiplier may lag behind during expand/commit transitions.
            int trackSteps = Mathf.Max(1, t.GetTotalSteps());
            int mulFromSteps = Mathf.Max(1, Mathf.RoundToInt(trackSteps / (float)baseSteps));

            // Still consider loopMultiplier as a fallback (and for non-step-based cases).
            int mul = Mathf.Max(mulFromSteps, Mathf.Max(1, t.loopMultiplier));
            maxMul = Mathf.Max(maxMul, mul);
        }
        return baseSteps * maxMul;
    }

}