using System;
using UnityEngine;
using System.Collections.Generic;
using System.Collections;
using System.Linq;
using Steamworks;
using Random = UnityEngine.Random;

public class PhaseSnapshot
{
    public MazeArchetype Pattern;
    public Color Color;
    public List<NoteEntry> CollectedNotes = new();
    public Dictionary<MusicalRole, float> TrackScores = new();
    public float Timestamp;

    public class NoteEntry
    {
        public int Step;
        public int Note;
        public float Velocity;
        public Color TrackColor;

        public NoteEntry(int step, int note, float velocity, Color trackColor)
        {
            this.Step = step;
            this.Note = note;
            this.Velocity = velocity;
            this.TrackColor = trackColor;
        }
    }
}

public class DrumTrack : MonoBehaviour
{
    private int _boundarySerial = 0;
    private PlayArea _lastPlayAreaForTileCache;
    private bool _hasLastPlayAreaForTileCache = false;
    public GameObject phaseStarPrefab;
    public GameObject mineNodePrefab;
    public PhasePersonalityRegistry phasePersonalityRegistry;
    private MotifProfile _pendingMotif;
    private float _pendingBpm;
    private int _pendingTotalSteps;
    private bool _pendingTimingValid;

    [Header("Play Area Mapping")]
    [Tooltip(
        "If true, GetPlayAreaWorld() is clamped to Dust Band (min/max Y). If false, grid uses full screen minus UI reserve.")]
    [SerializeField]
    private bool clampPlayAreaToDustBand = false;

    [Header("UI Safe Area (Viewport)")] [Range(0f, 0.5f)] [SerializeField]
    private float uiReserveBottomViewport = 0.14f; // reserve bottom 14% for UI

    [Range(0f, 0.5f)] [SerializeField] private float uiReserveTopViewport = 0.00f; // optional top reserve

    [SerializeField] private float uiReserveBottomInsetWorld = 0f; // optional fine-tune in world units
    [SerializeField] private float uiReserveTopInsetWorld = 0f;

    public float drumLoopBPM = 120f;
    public float gridPadding = 0f;
    public int totalSteps = 16;
    public float timingWindowSteps = 1f; // Can shrink to 0.5 or less as game progresses
    public AudioSource drumAudioSource;
    public double startDspTime;
    private AudioSource _drumA; // primary deck
    private AudioSource _drumB; // secondary deck (created at runtime if missing)
    private AudioSource _activeDrum; // currently audible deck
    private AudioSource _inactiveDrum;
    public double leaderStartDspTime { get; private set; }
    public List<PhaseSnapshot> SessionPhases = new();
    public List<MineNode> activeMineNodes = new List<MineNode>();
    public bool isPhaseStarActive;
    public int currentStep;

    [Header("Dust Band Mapping (Viewport Y)")] [Range(0f, 1f)] [SerializeField]
    private float dustBandMinY = 0.00f; // bottom of screen

    [Range(0f, 1f)] [SerializeField] private float dustBandMaxY = 0.80f; // 80% up the screen
    [SerializeField] private float dustBandTopInsetWorld = 0f; // optional extra inset in world units

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
    public PhaseStar _star;
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
    public event System.Action<MazeArchetype, PhaseStarBehaviorProfile> OnPhaseStarSpawned;
    public event System.Action<int, int> OnBinChanged; // (idx, binCount)
    public event System.Action<int, int> OnStepChanged; // (stepIndex, leaderSteps)
    public event System.Action<int, int> OnStepPulseN; // (stepIndex, n)

    [SerializeField] private int stepPulseEveryN = 0; // 0 disables
    private int _lastStepIdx = -1;
    private bool _driveFromEnergy;

    [SerializeField, Tooltip("How many 'spent tanks' per loop counts as full intensity (E). Tune while testing.")]
    private float tanksPerLoopAtFullIntensity = 0.35f;

    [SerializeField, Tooltip("Optional: minimum change in intensity01 required before allowing a new target profile.")]
    private float intensityHysteresis = 0.08f;

    private float _lastSpentTanksSample = -1f; // baseline at last boundary
    private float _lastIntensity01 = 0f; // for hysteresis

// armNow: when true, schedules a drum loop change to the motif's selected entry clip
// at the next leader boundary (if already running). This is the common case for motif changes.

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
        int idx = Mathf.Clamp(Mathf.FloorToInt(intensity01 * n), 0, n - 1);
        return _intensityLoops[idx];
    }

public void ApplyMotif(MotifProfile motif, bool armAtNextBoundary, string who, bool restartTransport = false)
{
    // Capture old timing BEFORE we overwrite _motif.
    int oldSteps = (_motif != null) ? _motif.stepsPerLoop : totalSteps;
    float oldBpm = (_motif != null) ? _motif.bpm : drumLoopBPM;

    bool stepsChanged = (_motif != null && motif != null && _motif.stepsPerLoop != motif.stepsPerLoop);
    bool bpmChanged   = (_motif != null && motif != null && Mathf.Abs(_motif.bpm - motif.bpm) > 0.001f);

    _motif = motif;
    _entryLoops = (_motif != null) ? _motif.entryDrumLoops : null;
    _intensityLoops = (_motif != null) ? _motif.intensityDrumLoops : null;

    _entryLoopsRemaining = (_motif != null) ? Mathf.Max(0, _motif.entryLoopCount) : 0;
    _driveFromEnergy = (_motif != null) && _motif.driveBeatsFromEnergy;

    // Reset intensity sampling for this motif (prevents “stuck at 0” after first motif).
    _lastSpentTanksSample = -1f;
    _lastIntensity01 = 0f;

    // Choose clip for this motif (clip choice can be immediate even if timing is pending).
    AudioClip clip = ChooseEntryClip();

    // If we're already running and steps changed, we must NOT change totalSteps immediately unless we restart now.
    if (_started && (stepsChanged || bpmChanged) && !restartTransport)
    {
        // If caller asked for an armed swap, enforce it (safe boundary).
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
        _pendingMotif = motif;
        _pendingBpm = (motif != null) ? motif.bpm : drumLoopBPM;
        _pendingTotalSteps = (motif != null) ? motif.stepsPerLoop : totalSteps;
        _pendingTimingValid = true;

        Debug.Log(
            $"[DRUM][MOTIF] ApplyMotif(DEFERRED TIMING) by {who}: motif={(motif ? motif.motifId : "null")} " +
            $"steps {oldSteps}->{_pendingTotalSteps} bpm {oldBpm}->{_pendingBpm} " +
            $"clip={(clip ? clip.name : "null")} armAtNextBoundary={armAtNextBoundary} restart={restartTransport}"
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

        _pendingMotif = null;
        _pendingTimingValid = false;

        Debug.Log(
            $"[DRUM][MOTIF] ApplyMotif by {who}: motif={(_motif ? _motif.motifId : "null")} " +
            $"entryLoops={(_entryLoops != null ? _entryLoops.Count : 0)} intensityLoops={(_intensityLoops != null ? _intensityLoops.Count : 0)} " +
            $"entryLoopCount={_entryLoopsRemaining} drive={_driveFromEnergy} bpm={drumLoopBPM} steps={totalSteps} " +
            $"clip={(clip ? clip.name : "null")} armAtNextBoundary={armAtNextBoundary} restart={restartTransport}"
        );
    }

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
        _pendingMotif = null;
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

    public void ResetMotifDrumSequencing()
    {
        _motif = null;
        _entryLoops = null;
        _intensityLoops = null;

        _entryLoopsRemaining = 0;
        _driveFromEnergy = false;

        _lastSpentTanksSample = -1f;
        _lastIntensity01 = 0f;

        _pendingDrumLoop = null;
        _pendingDrumLoopArmed = false;
    }
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
	public PlayArea GetPlayAreaWorld()
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
		Vector3 camPos = cam.transform.position;
		float left, right, bottom, top;
        float z = Mathf.Abs(camPos.z);
		if (cam.orthographic)
		{
			float halfH = cam.orthographicSize;
			float halfW = halfH * cam.aspect;

			left   = camPos.x - halfW;
			right  = camPos.x + halfW;
			bottom = camPos.y - halfH;
			top    = camPos.y + halfH;
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
        z = cam.orthographic ? 0f : Mathf.Abs(cam.transform.position.z);
		// Optional padding to keep spawns away from the very edge of the screen.
		// This is *not* UI-aware; it's a conservative safety margin.
		if (gridPadding > 0f)
		{
			left   += gridPadding;
			right  -= gridPadding;
			bottom += gridPadding;
			top    -= gridPadding;
		} 
// --- UI safe area clamp (viewport -> world) ---
// We reserve a bottom viewport band for UI so the grid never overlaps it.
// This is independent of dust visuals and does not depend on NoteVisualizer init.
        float uiBotV = Mathf.Clamp01(uiReserveBottomViewport);
        float uiTopV = Mathf.Clamp01(uiReserveTopViewport);

 

        if (uiBotV > 0f)
        {
            float uiBottomWorld = cam.ViewportToWorldPoint(new Vector3(0f, uiBotV, z)).y;
            bottom = Mathf.Max(bottom, uiBottomWorld + Mathf.Max(0f, uiReserveBottomInsetWorld));
        }

        if (uiTopV > 0f)
        {
            float uiTopWorld = cam.ViewportToWorldPoint(new Vector3(0f, 1f - uiTopV, z)).y;
            top = Mathf.Min(top, uiTopWorld - Mathf.Max(0f, uiReserveTopInsetWorld));
        }
        if (clampPlayAreaToDustBand) { 
            // Convert viewport band (minY..maxY) into world Y limits, using camera viewport conversion.
            float vMin = Mathf.Clamp01(dustBandMinY); 
            float vMax = Mathf.Clamp01(dustBandMaxY); 
            if (vMax < vMin) { float t = vMin; vMin = vMax; vMax = t; }
            // Use left edge x for conversion; only Y matters.
            Vector3 w0 = cam.ViewportToWorldPoint(new Vector3(0f, vMin, cam.nearClipPlane)); 
            Vector3 w1 = cam.ViewportToWorldPoint(new Vector3(0f, vMax, cam.nearClipPlane));
            bottom = Mathf.Max(bottom, Mathf.Min(w0.y, w1.y)); 
            top    = Mathf.Min(top,    Mathf.Max(w0.y, w1.y) - Mathf.Max(0f, dustBandTopInsetWorld));
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

    // Defensive: stop anything previously scheduled on inactive deck
    try { _inactiveDrum.Stop(); } catch { }

    _inactiveDrum.clip = newClip;
    _inactiveDrum.loop = true;
    _inactiveDrum.playOnAwake = false;

    // Try to end active at the swap boundary (avoids overlaps)
    try { _activeDrum.SetScheduledEndTime(swapDsp); } catch { }

    // Schedule the new loop, but DO NOT swap references yet.
    // The currently-audible deck must remain authoritative until swapDsp arrives.
    _inactiveDrum.PlayScheduled(swapDsp);

    _pendingDrumLoopDspStart = swapDsp;

    // Keep flags so Update() can finalize the deck swap when DSP reaches swapDsp.
    _pendingDrumLoopArmed = false;
    _pendingDrumLoop = null;

    Debug.Log($"[DRUM] Armed drum loop swap for dsp={swapDsp:F3} clip={newClip.name}");
}

    private void Update()
{
    // 0) Manager may exist but not be ready (or still wiring scenes)
    if (_gfm == null || !_gfm.ReadyToPlay())
        return;

    // 1) Watchdog timer for the spawn grid
    _gridCheckTimer += Time.deltaTime;
    if (_gridCheckTimer >= _gridCheckInterval)
    {
        ValidateSpawnGrid();
        _gridCheckTimer = 0f;
    }

    // 2) Transport/clip guards
    if (drumAudioSource == null || !HasValidClipLen || totalSteps <= 0)
        return;

    // ---- Leader-loop transport (single source of truth) ----
    if (leaderStartDspTime <= 0.0)
        leaderStartDspTime = startDspTime;

    // Clamp effective loop len defensively
    double effLen = Mathf.Max(0.0001f, EffectiveLoopLengthSec);

    const int kMaxBoundaryCatchup = 4;
    int catchup = 0;

    // Catch up loop boundaries (in case of hitches)
    while (AudioSettings.dspTime - leaderStartDspTime >= effLen && catchup < kMaxBoundaryCatchup)
    {
        // We crossed a boundary: advance the anchor to the start of the new loop.
        leaderStartDspTime += effLen;
        completedLoops++;

        // This is the boundary we just crossed (start of the *new* loop)
        double boundaryDsp = leaderStartDspTime;

        // Decide target clip based on what happened during the previous loop
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

    int leaderSteps = GetLeaderSteps();

    // Step indexing aligned to EFFECTIVE loop length
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

        int n = stepPulseEveryN;
        if (n > 0 && (currentStep % n) == 0)
            OnStepPulseN?.Invoke(currentStep, n);
    }

    // 3) Loop/bins driven by EFFECTIVE loop length
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
            OnBinChanged?.Invoke(_binIdx, bins);
        }
    }
    if (drumAudioSource == null || !HasValidClipLen || totalSteps <= 0)
        return;
// ------------------------------------------------------------
// Finalize any pending A/B deck swap when the scheduled DSP time arrives.
// IMPORTANT: the transport anchor (leaderStartDspTime) must never jump into the future.
// ------------------------------------------------------------
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

            // Clip length is now driven by the active deck.
            _clipLengthSec = (_activeDrum != null && _activeDrum.clip != null)
                ? Mathf.Max(_activeDrum.clip.length, 0f)
                : 0f;

            // startDspTime is the *bar* anchor for the currently-playing clip;
            // leaderStartDspTime remains leader-loop anchor.
            _currentDrumClip = (_activeDrum != null) ? _activeDrum.clip : null;

// ✅ COMMIT PENDING TIMING NOW THAT THE NEW DECK IS AUDIBLE
            if (_pendingTimingValid)
            {
                drumLoopBPM = _pendingBpm;
                totalSteps  = Mathf.Max(1, _pendingTotalSteps);

                // Clear pending timing so intensity-driven clip swaps don't reapply it.
                _pendingTimingValid = false;

                Debug.Log(
                    $"[DRUM][MOTIF] Committed pending timing at swap: bpm={drumLoopBPM} steps={totalSteps} " +
                    $"dsp={startDspTime:F3} clip={(_currentDrumClip ? _currentDrumClip.name : "null")}"
                );
            }

            Debug.Log($"[DRUM] Finalized drum loop swap at dsp={startDspTime:F3} clip={(_currentDrumClip ? _currentDrumClip.name : "null")}");

            _pendingDrumLoopDspStart = -1.0;

            
        }
    }

// --- Transport watchdog: if scheduling ever fails, don't stay silent ---
    if (_activeDrum != null && _activeDrum.clip != null)
    {
        // If we have started transport and the active deck is not playing, restart safely.
        // This prevents "SetScheduledEndTime() ended the active deck but inactive never began" silence.
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

    // 4) Housekeeping
    if (activeMineNodes != null)
        activeMineNodes.RemoveAll(n => n == null);
}
    public bool TryGetNextBaseStepDsp(out double nextStepDsp, out float stepDurationSec, int stepOffset = 1)
    {
        nextStepDsp = 0;
        stepDurationSec = 0f;

        if (leaderStartDspTime <= 0.0) return false;

        double effLen = Mathf.Max(0.0001f, EffectiveLoopLengthSec);
        int steps = Mathf.Max(1, totalSteps);   // <- STABLE grid
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
        if (!_driveFromEnergy) return;
        if (_gfm == null) return;
        if (_motif == null) return;

        // 1) Respect entry window
        if (_entryLoopsRemaining > 0)
        {
            _entryLoopsRemaining--;
            return;
        }

        // 2) Need an intensity ladder
        if (_intensityLoops == null || _intensityLoops.Count == 0)
            return;

        // 3) Compute per-loop intensity from energy spent
        float spent = _gfm.GetMostSpentEnergyTanks();
        if (_lastSpentTanksSample < 0f)
        {
            // Baseline acquisition: first boundary after arming
            _lastSpentTanksSample = spent;
            return;
        }

        float delta = Mathf.Max(0f, spent - _lastSpentTanksSample);
        _lastSpentTanksSample = spent;

        float denom = Mathf.Max(0.0001f, tanksPerLoopAtFullIntensity);
        float intensity01 = Mathf.Clamp01(delta / denom);

        // Optional hysteresis
        if (Mathf.Abs(intensity01 - _lastIntensity01) < intensityHysteresis)
            intensity01 = _lastIntensity01;
        _lastIntensity01 = intensity01;

        // 4) Select target clip by index
        var targetClip = ResolveIntensityClip(intensity01);
        if (targetClip == null) return;

        // 5) Avoid redundant scheduling
        if (_pendingDrumLoopArmed && _pendingDrumLoop == targetClip) return;
        if (!_pendingDrumLoopArmed && drumAudioSource != null && drumAudioSource.clip == targetClip) return;

        // 6) Schedule at boundary (clip swap handled by ArmPendingDrumLoopForNextLeaderBoundary)
        ScheduleDrumLoopChange(targetClip);
    }
    private void EnsureDualDrumSources()
    {
        if (_drumA == null)
            _drumA = drumAudioSource;

        if (_drumA == null)
        {
            Debug.LogError("[DrumTrack] EnsureDualDrumSources: drumAudioSource is null.");
            return;
        }

        // Create secondary source if missing
        if (_drumB == null)
        {
            var go = _drumA.gameObject;
            _drumB = go.GetComponent<AudioSource>(); // will just be A again if only one exists

            // If we only have one AudioSource component, add another
            var all = go.GetComponents<AudioSource>();
            if (all.Length >= 2)
            {
                _drumA = all[0];
                _drumB = all[1];
            }
            else
            {
                _drumB = go.AddComponent<AudioSource>();

                // Clone core settings so the two decks behave identically
                _drumB.playOnAwake = false;
                _drumB.outputAudioMixerGroup = _drumA.outputAudioMixerGroup;
                _drumB.volume = _drumA.volume;
                _drumB.pitch = _drumA.pitch;
                _drumB.panStereo = _drumA.panStereo;
                _drumB.spatialBlend = _drumA.spatialBlend;
                _drumB.reverbZoneMix = _drumA.reverbZoneMix;
                _drumB.dopplerLevel = _drumA.dopplerLevel;
                _drumB.spread = _drumA.spread;
                _drumB.rolloffMode = _drumA.rolloffMode;
                _drumB.minDistance = _drumA.minDistance;
                _drumB.maxDistance = _drumA.maxDistance;
                _drumB.priority = _drumA.priority;
            }
        }

        // Establish default deck roles
        if (_activeDrum == null) _activeDrum = _drumA;
        if (_inactiveDrum == null) _inactiveDrum = (_activeDrum == _drumA) ? _drumB : _drumA;

        // Keep public field pointing to currently active deck (so other code reads the audible clip)
        drumAudioSource = _activeDrum;
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

    // Optional debug: grid to screen scale sanity
    if (_gfm != null && _spawnGrid != null && _dust != null)
    {
        float tile = GetTileDiameterWorld();
        int w = _spawnGrid.gridWidth;
        float worldW = tile * (w - 1);
        float scrW = GetScreenWorldWidth();
        Debug.Log($"[GridScale] tile={tile:F3}, worldWide(grid)={worldW:F3}, screenWide={scrW:F3}, ratio={worldW / Mathf.Max(0.0001f, scrW):F3}");
    }

    isPhaseStarActive = false;
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
            "Expected GameFlowManager to call PTM.StartChapter(...) before ManualStart.\n" +
            "Falling back to AudioSource.clip timing (may play inspector/default loop)."
        );
    }

    AudioClip initialClip = null;

    if (bootMotif != null)
    {
        _motif = bootMotif;

        // Timing from motif is authoritative
        drumLoopBPM = _motif.bpm;
        totalSteps = _motif.stepsPerLoop;

        // Ensure our internal motif state is applied consistently with later transitions
        // (ApplyMotif may also choose/setup clip candidates, beat seq, etc.)
        if (_motif != null)
        {
            ApplyMotif(_motif, armAtNextBoundary: false, who: "DrumTrack/ManualStart", restartTransport: false);
        }

        // ApplyMotif chooses the clip in your system, but ManualStart still needs an explicit initial clip
        initialClip = ChooseEntryClip();
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

    private void AutoSizeSpawnGridIfEnabled() { 
        if (!autoSizeSpawnGridToScreen) return; 
        if (_spawnGrid == null) return;
        int sw = Mathf.Max(1, Screen.width); 
        int sh = Mathf.Max(1, Screen.height);
        // Usable height excludes the bottom UI padding.
        int usableH = Mathf.Max(1, sh - Mathf.Max(0, uiBottomPaddingPx));
        // Derive a stable cell pixel size from your historical assumption.
        float cellPx = 0f;
        if (referenceWidthPx > 0 && referenceColumns > 0) 
            cellPx = referenceWidthPx / (float)referenceColumns; // ~53.33 at 1920/36
        
        if (cellPx <= 1f) cellPx = 50f; // safe fallback
        int cols = Mathf.Max(1, Mathf.RoundToInt(sw / cellPx));
        int rows = Mathf.Max(1, Mathf.RoundToInt(usableH / cellPx));
        
        // Keep UI reserve consistent with the pixel padding.
        uiReserveBottomViewport = Mathf.Clamp01(uiBottomPaddingPx / (float)sh);
        
        // Apply
        _spawnGrid.ResizeGrid(cols, rows);
        _hasLockedPlayArea = false;
        
        // Any cached world mapping based on old grid dims must be invalidated.
        InvalidateGridWorldCache();
        
        Debug.Log($"[GridAutoSize] screen={sw}x{sh} usableH={usableH} cellPx={cellPx:F2} -> grid={cols}x{rows} uiBottomV={uiReserveBottomViewport:F3}");
    }   
    public void CarveTemporaryCellFromVehicle(
        Vector3 worldPos,
        float healDelaySeconds,
        int resolveRadiusCells = 0)
    {
        if (_dust == null) return;

        MazeArchetype phase = GetCurrentPhaseSafe();

        _dust.CarveTemporaryCellFromVehicle(
            worldPos,
            phase,
            healDelaySeconds,
            resolveRadiusCells
        );
    }
    

    public void RequestPhaseStar(MazeArchetype phase, Vector2Int? cellHint = null)
    {
        if (isPhaseStarActive)
        {
            Debug.Log("[SpawnGuard] PhaseStar already active; abort.");
            return;
        }

        if (!phaseStarPrefab)
        {
            Debug.LogError("[Spawn] PhaseStar prefab is NULL.");
            return;
        }

        // Resolve dependencies up-front so we can error loudly instead of NRE
        if (!_trackController || _trackController.tracks == null || _trackController.tracks.Length == 0)
        {
            Debug.LogError("[Spawn] No instrument tracks available.");
            return;
        }

        // Pick a cell (prefer hint)
        Vector2Int cell = cellHint ?? (_spawnGrid != null ? _spawnGrid.GetRandomAvailableCell() : GetRandomAvailableCell());
        if (cell.x < 0)
        {
            Debug.LogWarning("[Spawn] 🚫 No available cell for PhaseStar.");
            return;
        }

        var pos = GridToWorldPosition(cell);
        Debug.Log($"[Spawn] 🌠 Spawning PhaseStar at {cell} (world {pos}) for phase {phase}");

        // Instantiate
        var go = Instantiate(phaseStarPrefab, pos, Quaternion.identity);
        _star = go.GetComponent<PhaseStar>();
        if (!_star)
        {
            Debug.LogError("[Spawn] Prefab missing PhaseStar");
            Destroy(go);
            return;
        }

        isPhaseStarActive = true;

        // Simple hook – PhaseStar exposes OnDestroyed? If not, use a helper component:
        var killer = go.AddComponent<OnDestroyRelay>();
        killer.onDestroyed += () =>
        {
            isPhaseStarActive = false;
            if (_star != null) _star = null; // important: clear stale reference
            if (_spawnGrid != null) _spawnGrid.FreeCell(cell.x, cell.y);
        };

        // Behavior profile + dust
        var profileAsset = phasePersonalityRegistry ? phasePersonalityRegistry.Get(phase) : null;
        if (_dust && profileAsset) _dust.ApplyProfile(profileAsset);
        if (_gfm && _dust) _dust.RetintExisting(0.4f);

        // Targets
        IEnumerable<InstrumentTrack> targets = _trackController.tracks.Where(t => t != null);


        // 🔹 Look up the motif for this spawn from the PhaseTransitionManager
        MotifProfile motif = null;
        
        if (_phaseTransitionManager != null && _phaseTransitionManager.currentMotif != null)
        {
            motif = _phaseTransitionManager.currentMotif;

            // Optional sanity check: warn if phase/motif phase don't line up
            Debug.Log($"[Spawn] Using motif '{motif.motifId}' for PhaseStar (phase {phase}).");
        }
        else
        {
            Debug.Log("[Spawn] No current motif available; PhaseStar will use phase-based NoteSets.");
        }

        // Wire star (now motif-aware)
        _star.Initialize(this, targets, profileAsset, phase, motif);
        OnPhaseStarSpawned?.Invoke(phase, profileAsset);
    }

    public bool TryGetDustAt(Vector2Int cell, out CosmicDust dust)
    {
        dust = null;
        return _dust != null && _dust.TryGetDustAt(cell, out dust);
    }

    public int CarveTemporaryCellFromMineNode(
        Vector3 worldPos,
        MazeArchetype phase,
        float healDelaySeconds,
        Color imprintColor,
        Color imprintShadowColor,
        float imprintHardness01,
        int resolveRadiusCells = 0,
        float appetiteMul = 1f)
    {
        if (_dust == null) return 0;

        return _dust.CarveTemporaryCellFromMineNode(
            worldPos,
            phase,
            healDelaySeconds,
            imprintColor,
            imprintShadowColor,
            imprintHardness01,
            resolveRadiusCells,
            appetiteMul
        );
    }

    private sealed class OnDestroyRelay : MonoBehaviour  {
        public System.Action onDestroyed;
        private void OnDestroy() { try { onDestroyed?.Invoke(); } catch {} }
    }
    private bool EnsureCachedRefs()
    {
        if (_gfm == null) _gfm = GameFlowManager.Instance;
        if (_gfm == null) return false;

        if (_spawnGrid == null) _spawnGrid = _gfm.spawnGrid;
        if (_dust == null)      _dust      = _gfm.dustGenerator;
        if (_trackController == null)      _trackController = _gfm.controller;

        return _spawnGrid != null;
    }
    
    public void SetBridgeAccent(bool on)
    {
        // Simple example: LPF + lower hats when on
        // Wire into your mixer/filters as appropriate.
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

    public MazeArchetype GetCurrentPhaseSafe()
    {
        // DrumTrack is level authority; phaseTransitionManager is already cached here.
        if (_phaseTransitionManager != null) return _phaseTransitionManager.currentPhase;
        return MazeArchetype.Establish;
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

        float x = area.left   + (cell.x + 0.5f) * tileX;
        float y = area.bottom + (cell.y + 0.5f) * tileY;

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

        tileX = area.width / w;
        tileY = area.height / h;

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

        ix = Mathf.Clamp(ix, 0, w - 1);
        iy = Mathf.Clamp(iy, 0, h - 1);

        return new Vector2Int(ix, iy);
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
    private IEnumerator InitializeDrumLoop()
    {
        // ✅ Wait until the AudioSource has a valid clip
        while (drumAudioSource.clip == null)
        {
            yield return null; // Wait until the next frame
        }
        _clipLengthSec = Mathf.Max(drumAudioSource.clip.length, 0f); 
        if (!HasValidClipLen)
        { 
            Debug.LogError("DrumTrack: Clip length is zero/invalid; aborting loop init."); 
            yield break;
        }
        drumAudioSource.loop = true; // ✅ Ensure the loop setting is applied
    }
    private void ScheduleDrumLoopChange(AudioClip newLoop)
    {
        if (newLoop == null)
        {
            Debug.LogWarning("[DrumTrack] ScheduleDrumLoopChange called with null clip.");
            return;
        }

        _pendingDrumLoop = newLoop;
        _pendingDrumLoopArmed = true;
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
                float tileX = area.width  / w;
                float tileY = area.height / h;
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

    public void InvalidateGridWorldCache()
    {
        _cachedTileDiameterWorld = 0f;
        _hasLastPlayAreaForTileCache = false;
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