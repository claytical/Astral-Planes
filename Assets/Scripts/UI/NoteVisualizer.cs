using System;
using System.Collections;
using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using UnityEngine.UI;

public class NoteVisualizer : MonoBehaviour
{
    [Header("Ascension")]
    [Tooltip("World-space UI reference the notes should rise to (e.g., a RectTransform named 'Line of Ascent').")]
    public RectTransform lineOfAscent;
    [Tooltip("Ascend duration in drum loops (1 = one full loop).")]
    [Min(1)] public int ascendLoops = 8;
    [Tooltip("Extra seconds added to the loop-based duration to avoid snapping on the boundary.")]
    public float ascendPaddingSeconds = 0.15f;
    [Tooltip("Small world-space Y padding to keep notes from sitting exactly on the line.")]
    public float ascendLineWorldPadding = 0f;
    [Header("Playhead")]
    public RectTransform playheadLine;
    public ParticleSystem playheadParticles;
    [Range(0f, 1f)]
    [SerializeField] private float _playheadEnergy01 = 0f;       // what we actually render
    private float _playheadEnergyTarget01 = 0f;                  // what game logic asks for
    [SerializeField] private float _playheadEnergyLerpSpeed = 4f;
    // Each ascended note bumps this; decays over time. Drives extra particle activity.
    [Range(0f, 1f)]
    [SerializeField] private float _lineCharge01 = 0f;
    [SerializeField] private float _lineChargeDecaySpeed = 0.5f;
    [Header("Playhead Pulse (Color)")]
    [SerializeField] private float releasePulseSeconds = 0.18f;
    [SerializeField] private Color releasePulseColor = new Color(1f, 0.2f, 0.9f, 1f); // hot magenta-ish

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
    [Header("Marker & Tether Prefabs")]
    public GameObject notePrefab;
    public GameObject noteTetherPrefab;
    private int _forcedLeaderSteps = -1;
    private int _forcedLeaderBins = -1;
    [Header("Track Rows (one per InstrumentTrack in controller order)")]
    public List<RectTransform> trackRows;
    [Header("Bin Visualization")]
    [Tooltip("Parent RectTransform where bin indicators will be instantiated.")]
    public RectTransform binStripParent;

    [Tooltip("Prefab with an Image component used for each bin indicator.")]
    public GameObject binIndicatorPrefab;

    [Tooltip("Color for inactive bins.")]
    public Color binInactiveColor = new Color(1f, 1f, 1f, 0.2f);

    [Tooltip("Color for the currently active target bin.")]
    public Color binActiveColor = new Color(1f, 1f, 1f, 0.9f);
    private List<(InstrumentTrack, int)> deadKeys = new List<(InstrumentTrack, int)>();
    private Canvas _worldSpaceCanvas;
    private Transform _uiParent;
    private bool isInitialized;
    private readonly Dictionary<InstrumentTrack, HashSet<int>> _ghostNoteSteps = new();
    public Dictionary<(InstrumentTrack, int), Transform> noteMarkers = new();
    private readonly Dictionary<InstrumentTrack, Dictionary<int, Vector3>> _trackStepWorldPositions = new();
    private readonly List<Image> _binIndicators = new List<Image>();
    private int _activeBinCount = 0;      // How many bins are currently in use (post-contraction)
    private int _currentTargetBin = -1;  
    [Header("Playhead Trail (Particles)")]
    [SerializeField] private bool playheadTrailEnabled = true;

// Particles per world unit traveled (higher = denser trail).
    [SerializeField] private float playheadTrailEmitPerWorldUnit = 45f;

// Safety cap so a hitch doesn't emit thousands.
    [SerializeField] private int playheadTrailMaxEmitPerFrame = 160;

// Optional: keep particle Z stable (useful if your world-space canvas depth fights sorting).
    [SerializeField] private float playheadTrailWorldZOverride = float.NaN;

    private Vector3 _lastPlayheadParticleWorldPos;
    private bool _hasLastPlayheadParticleWorldPos;
    class AscendTask
    {
        public InstrumentTrack track;
        public List<MarkerState> markers = new();   // per-marker state
        public int delayLoopsRemaining;
        public int totalAscendLoops;
        public int stepsCompleted;
        public System.Action onArrive;
        public bool IsArrived => stepsCompleted >= totalAscendLoops || markers.TrueForAll(ms => ms.go == null);
    }
    private struct BlastTask
    {
        public GameObject go;
        public Vector3 startPos;
        public Vector3 dir;       // world-space direction
        public float startScale;
        public float endScale;
        public float dur;         // seconds
        public float t;           // normalized 0..1
        public System.Action onDone; // optional (SFX, pooling return, etc.)
    }
    private struct MarkerState
    {
        public GameObject go;
        public (InstrumentTrack track, int step) key;
        public float stepY;              // distance moved per loop tick
        public int delayLoopsRemaining;  // per-marker optional delay
        public int loopsRemaining;       // per-marker countdown to arrival
        public Action onDonePerMarker; // your old coroutine's onDone lambda
    }
    private struct RushTask
    {
        public GameObject go;        // marker being animated
        public Vector3 startPos;     // cached at enqueue
        public Transform target;     // vehicle (or any target)
        public float dur;            // seconds
        public float t;              // normalized 0..1
        public Action onArrive; // called when we reach target (chain effects/cleanup)
    }
    private readonly Dictionary<InstrumentTrack, AscendTask> _ascendTasks = new();
    private int _lastObservedCompletedLoops = -1;
    private readonly Dictionary<(InstrumentTrack track, int step), int> _stepBurst = new();
    private readonly HashSet<(InstrumentTrack track, int step)> _animatingSteps = new();
    private readonly List<BlastTask> _blastTasks = new();
    private readonly List<RushTask> _rushTasks = new();
    [Header("First-Play Confirm FX")]
    [SerializeField] private ParticleSystem firstPlayConfirmOrbPrefab;
    [SerializeField] private float firstPlayConfirmTravelSeconds = 2f;
    [SerializeField] private int firstPlayConfirmEmitCount = 24;

    private struct FirstPlayConfirmRequest
    {
        public Transform source;     // vehicle
        public InstrumentTrack track;
        public int step;
        public double dspTime;       // when this step first plays
        public Color color;
        public bool spawned;
    }

    private struct FirstPlayConfirmTask
    {
        public ParticleSystem ps;
        public Vector3 start;
        public Vector3 end;
        public double startDsp;
        public double endDsp;
        public Color color;
    }

    private readonly List<FirstPlayConfirmRequest> _firstPlayRequests = new();
    private readonly List<FirstPlayConfirmTask> _firstPlayTasks = new();
    private Color stepColor;

    public void RegisterCollectedMarker(InstrumentTrack track, int burstId, int step, GameObject markerGo)
    {
        if (!track || !markerGo) return;
        Debug.Log($"[RegisterCollected] {track.name} burstId={burstId} step={step}, markerGo y={markerGo.transform.position.y:F1}");
        if (noteMarkers.TryGetValue((track, step), out var existing) && existing && existing.gameObject != markerGo)
        {
            Debug.Log($"  → DESTROYING old marker at step {step}, was at y={existing.position.y:F1}");
            Destroy(existing.gameObject);
        }

        noteMarkers[(track, step)] = markerGo.transform;
        _stepBurst[(track, step)]  = burstId;
        Debug.Log($"[NV:STEP_BURST_SET] track={track.name} step={step} burstId={burstId} markerId={markerGo.GetInstanceID()}");

        var tag = markerGo.GetComponent<MarkerTag>();
        if (!tag) tag = markerGo.AddComponent<MarkerTag>();
        tag.track = track;
        tag.step = step;
        tag.burstId = burstId;       // ← mark ownership by this burst
        tag.ascendBurstId = burstId; // ← persist for ascension even if canonicalize neutralizes burstId later
        tag.isPlaceholder = false;   // ← lit now
        tag.isAscending = false;
        var ml = markerGo.GetComponent<MarkerLight>() ?? markerGo.AddComponent<MarkerLight>(); 
        ml.LightUp(track.trackColor);
    }
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
    public void ScheduleFirstPlayConfirm(Transform source, InstrumentTrack track, int step, double dspTime, Color color)
    {
        if (track == null || source == null) return;
        Debug.Log($"[CONFIRM_SCHED] track={track.name} step={step} dsp={dspTime:F6} now={AudioSettings.dspTime:F6} dt={(dspTime-AudioSettings.dspTime):F4}");
        _firstPlayRequests.Add(new FirstPlayConfirmRequest
        {
            source = source,
            track = track,
            step = step,
            dspTime = dspTime,
            color = color,
            spawned = false
        });
    }
    private void UpdatePlayheadParticleTrailWorld()
    {
//        if (!playheadTrailEnabled) return;
        if (playheadParticles == null || playheadLine == null) return;

        // Ensure "trail" behavior: particles remain in world when emitter moves.
        var main = playheadParticles.main;
        if (main.simulationSpace != ParticleSystemSimulationSpace.World)
            main.simulationSpace = ParticleSystemSimulationSpace.World;

        // Emitter should live on the playhead line (it is a child, but we sample world pos anyway).
        Vector3 now = playheadParticles.transform.position;

        if (!float.IsNaN(playheadTrailWorldZOverride))
            now.z = playheadTrailWorldZOverride;

        // First frame after enable/reset: just seed the position.
        if (!_hasLastPlayheadParticleWorldPos)
        {
            _hasLastPlayheadParticleWorldPos = true;
            _lastPlayheadParticleWorldPos = now;
            return;
        }

        Vector3 prev = _lastPlayheadParticleWorldPos;
        if (!float.IsNaN(playheadTrailWorldZOverride))
            prev.z = playheadTrailWorldZOverride;

        float dist = Vector3.Distance(prev, now);
        if (dist <= 0.00001f)
            return;

        // Emit a number of particles proportional to distance traveled.
        int emitCount = Mathf.Clamp(
            Mathf.CeilToInt(dist * playheadTrailEmitPerWorldUnit),
            1,
            playheadTrailMaxEmitPerFrame
        );

        // Emit evenly along the traveled segment so the trail is continuous.
        var emitParams = new ParticleSystem.EmitParams();
        for (int i = 0; i < emitCount; i++)
        {
            float u = (emitCount <= 1) ? 1f : (i / (emitCount - 1f));
            Vector3 p = Vector3.Lerp(prev, now, u);
            emitParams.position = p;

            // Optional: tiny jitter can soften "beads on a string" if you want it.
            // emitParams.position += UnityEngine.Random.insideUnitSphere * 0.01f;
// Make trail particles inherit the same pulse color immediately.
            if (_releasePulseT > 0f)
            {
                float pulse01 = Mathf.Clamp01(_releasePulseT / Mathf.Max(0.0001f, releasePulseSeconds));
                emitParams.startColor = Color.Lerp(Color.white, releasePulseColor, pulse01);
            }
            else
            {
                emitParams.startColor = Color.white; // or omit if you want the system default
            }

            playheadParticles.Emit(emitParams, 1);
        }

        _lastPlayheadParticleWorldPos = now;
    }
    /// <summary>
    /// Hard visual reset for motif boundaries. This should be invoked by the single
    /// motif authority after track data has been cleared, and before the next motif
    /// begins spawning notes.
    /// </summary>
    public void BeginNewMotif_ClearAll(bool destroyMarkerGameObjects = true)
    {
        // Stop any in-progress task state (we use Update-driven task lists; clearing is sufficient).
        _ascendTasks.Clear();
        _blastTasks.Clear();
        _rushTasks.Clear();
        _stepBurst.Clear();
        _animatingSteps.Clear();
        _ghostNoteSteps.Clear();
        _hasLastPlayheadParticleWorldPos = false;
        _lastPlayheadParticleWorldPos = Vector3.zero;
        //_trackStepWorldPositions.Clear();
        _lastObservedCompletedLoops = -1;
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

        // Bin strip visuals
        if (binStripParent != null)
        {
            for (int i = binStripParent.childCount - 1; i >= 0; i--)
                Destroy(binStripParent.GetChild(i).gameObject);
        }
        _binIndicators.Clear();
        _activeBinCount = 0;
        _currentTargetBin = -1;

        // Force any temporarily overridden leader sizes back to default.
        _forcedLeaderSteps = -1;
        _forcedLeaderBins = -1;

        // Particles: clear any lingering emission so nothing looks "stuck".
        if (playheadParticles != null)
            playheadParticles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        _pendingReleasePulse = false;
        _playheadEnergy01 = 0f;
        _playheadEnergyTarget01 = 0f;
        _lineCharge01 = 0f;
        // Cache playhead visuals (optional if you want to scale/alpha it with energy)
        if (playheadLine != null)
        {
            _playheadBaseHeight = playheadLine.sizeDelta.y;
            if (_playheadBaseHeight <= 0f)
                _playheadBaseHeight = 100f; // defensive default
        }        
    }
   public void ForceSyncMarkersToPersistentLoop(InstrumentTrack track)
{
    if (track == null) return;
    if (_ctrl == null || _ctrl.tracks == null) return;

    int trackIndex = Array.IndexOf(_ctrl.tracks, track);
    if (trackIndex < 0 || trackIndex >= trackRows.Count) return;

    int totalSteps = Mathf.Max(1, track.GetTotalSteps());

    // Build authoritative set of loop-owned steps (what should exist visually).
    var loopNotes = track.GetPersistentLoopNotes();
    var loopSteps = new HashSet<int>();
    if (loopNotes != null)
    {
        foreach (var (step, _, _, _, _) in loopNotes)
        {
            if (step >= 0 && step < totalSteps)
                loopSteps.Add(step);
        }
    }

    // 1) Remove stale dictionary markers (steps no longer in the persistent loop OR out of range).
    //    Do NOT destroy ascending markers mid-flight.
    if (noteMarkers != null)
    {
        var keys = noteMarkers.Keys.ToList();
        foreach (var key in keys)
        {
            if (key.Item1 != track) continue;

            int step = key.Item2;

            bool outOfRange = (step < 0 || step >= totalSteps);
            bool notInLoop  = !loopSteps.Contains(step);

            if (!outOfRange && !notInLoop) continue;

            if (noteMarkers.TryGetValue(key, out var tr) && tr != null)
            {
                var tag = tr.GetComponent<MarkerTag>();
                if (tag != null && tag.isAscending)
                    continue; // keep in-flight ascension markers intact

                // Safe to destroy (orphan / out-of-window)
#if UNITY_EDITOR
                if (!Application.isPlaying) DestroyImmediate(tr.gameObject);
                else Destroy(tr.gameObject);
#else
                Destroy(tr.gameObject);
#endif
            }

            noteMarkers.Remove(key);
        }
    }

    // 2) Remove stale row children that are loop-owned but not present in the dictionary anymore.
    //    This catches any UI stragglers not tracked in noteMarkers.
    var row = trackRows[trackIndex];
    for (int i = row.childCount - 1; i >= 0; i--)
    {
        var child = row.GetChild(i);
        if (!child) continue;

        var tag = child.GetComponent<MarkerTag>();
        if (tag == null) continue;
        if (tag.track != track) continue;
        if (tag.isAscending) continue;

        int step = tag.step;

        bool outOfRange = (step < 0 || step >= totalSteps);
        bool notInLoop  = !loopSteps.Contains(step);

        // IMPORTANT: only hard-remove "loop-owned" markers here.
        // Burst-owned markers (burstId >= 0) are governed by burst cleanup logic.
        bool loopOwned = tag.burstId < 0;

        if ((outOfRange || notInLoop) && loopOwned)
        {
#if UNITY_EDITOR
            if (!Application.isPlaying) DestroyImmediate(child.gameObject);
            else Destroy(child.gameObject);
#else
            Destroy(child.gameObject);
#endif
        }
    }

    // 3) Ensure every loop step has a marker (re-add missing ones).
    foreach (int step in loopSteps)
        PlacePersistentNoteMarker(track, step, lit: true, burstId: -1);

    // 4) Relayout after removals/additions.
    RecomputeTrackLayout(track);
}
 
    /// <summary>
    /// Set how "charged" the playhead is [0..1] based on how many notes in the
    /// current burst have been collected. This will be smoothed visually.
    /// </summary>
    public void SetPlayheadEnergy01(float value)
    {
        _playheadEnergyTarget01 = Mathf.Clamp01(value);
    }

    /// <summary>
    /// Called when a burst completes & drums change to trigger a short visual pulse.
    /// </summary>
    public void TriggerPlayheadReleasePulse()
    {
        _pendingReleasePulse = true;
        _releasePulseT = releasePulseSeconds; // start the color pulse immediately
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
        // Keep Start, but do not “lock” the drum here.
        RefreshCoreRefs(force: true);
    }

void Update()
{
    if (!isInitialized || playheadLine == null)
        return;

    // Refresh once per frame; only reassigns when something actually changed.
    if (!RefreshCoreRefs(force: false))
        return;

    if (_drum == null)
        return;

    // ------------------------------------------------------------
    // Anchor guard (prevents playhead disappearing when drum transport
    // anchor is briefly unavailable during clip swaps / boot / wiring)
    // ------------------------------------------------------------
    double leaderStartDsp =
        (_drum.leaderStartDspTime > 0.0) ? _drum.leaderStartDspTime :
        (_drum.startDspTime > 0.0)       ? _drum.startDspTime :
                                           0.0;

    if (leaderStartDsp <= 0.0)
    {
        // No current anchor. If we've ever had one, keep rendering using the cached anchor.
        if (!_hasCachedDrumAnchor)
            return;

        leaderStartDsp = _cachedLeaderStartDspTime;
    }
    else
    {
        // Cache last-known-good anchor so transient gaps don't blank the UI for a full loop.
        _hasCachedDrumAnchor = true;
        _cachedLeaderStartDspTime = leaderStartDsp;
    }

    // Smooth playhead energy & line charge toward their targets
    _playheadEnergy01 = Mathf.MoveTowards(
        _playheadEnergy01,
        _playheadEnergyTarget01,
        _playheadEnergyLerpSpeed * Time.deltaTime
    );

    _lineCharge01 = Mathf.MoveTowards(
        _lineCharge01,
        0f,
        _lineChargeDecaySpeed * Time.deltaTime
    );

    // --- Playhead position across the "leader" loop (max loop multiplier) ---
    int audibleBin = (_gfm != null && _gfm.controller != null)
        ? _gfm.controller.GetTransportFrame().playheadBin
        : 0;
    _ = audibleBin; // (kept to preserve your original intent; safe no-op if unused)

// --- Visual clock MUST match playheadLine clock ---
// Use the leader loop length for both x-position AND step sampling.
    int drumTotalSteps = Mathf.Max(1, _drum.totalSteps);
    float fullVisualLoopDuration = Mathf.Max(0.0001f, _drum.GetLoopLengthInSeconds());

// Seconds per step in the VISUAL loop timeline
    float stepDuration = fullVisualLoopDuration / drumTotalSteps;

// Position playhead line using the same loop duration
    float globalElapsed = (float)(AudioSettings.dspTime - leaderStartDsp);
    float globalNormalized = (globalElapsed % fullVisualLoopDuration) / fullVisualLoopDuration;

    float canvasWidth = GetScreenWidth();
    float xPos = Mathf.Lerp(0f, canvasWidth, Mathf.Clamp01(globalNormalized));
    playheadLine.anchoredPosition = new Vector2(xPos, playheadLine.anchoredPosition.y);

    ProcessFirstPlayConfirmFx();
    UpdateFirstPlayConfirmTasks();
    DisableBuiltInEmissionForTrail();
    UpdatePlayheadParticleTrailWorld();

// Leader time in [0, fullVisualLoopDuration)
    float leaderT = (float)((AudioSettings.dspTime - leaderStartDsp) % fullVisualLoopDuration);
    if (leaderT < 0f) leaderT += fullVisualLoopDuration;

// Current step derived from the SAME timeline the playhead uses
    int currentStep = Mathf.FloorToInt(leaderT / stepDuration);
    currentStep = ((currentStep % drumTotalSteps) + drumTotalSteps) % drumTotalSteps;

    stepColor = ComputeStepColor(currentStep);

    bool shimmer = false;
    float maxVelocity = 0f;

    var controller = GameFlowManager.Instance != null ? GameFlowManager.Instance.controller : null;
    if (controller != null && controller.tracks != null)
    {
        foreach (var track in controller.tracks)
        {
            if (track == null) continue;

            float v = track.GetVelocityAtStep(currentStep);
            maxVelocity = Mathf.Max(maxVelocity, v / 127f);

            if (_ghostNoteSteps.TryGetValue(track, out var steps) && steps != null && steps.Contains(currentStep))
            {
                shimmer = true;
                break;
            }
        }
    }

    if (playheadParticles != null)
    {
        var main = playheadParticles.main;
        var emission = playheadParticles.emission;
        // Base factors from music (velocity) plus collection/ascension state
        float velFactor = Mathf.Clamp01(maxVelocity);
        float energyFactor = Mathf.Lerp(0.3f, 1.0f, _playheadEnergy01);   // fills as burst is collected
        float chargeFactor = 1.0f + 1.5f * _lineCharge01;                 // extra particles as notes hit the top

        main.startSize = Mathf.Lerp(0.8f, 1.2f, velFactor) * energyFactor;
        emission.rateOverTime = Mathf.Lerp(10f, 50f, velFactor) * energyFactor * chargeFactor;
        emission.enabled = shimmer || _lineCharge01 > 0.05f || _playheadEnergy01 > 0.05f;
// Decay pulse timer
        if (_releasePulseT > 0f)
            _releasePulseT = Mathf.Max(0f, _releasePulseT - Time.deltaTime);

        float pulse01 = (releasePulseSeconds <= 0f) ? 0f : Mathf.Clamp01(_releasePulseT / releasePulseSeconds);
        var col = playheadParticles.colorOverLifetime;

        col.enabled = true;

// Build a base gradient (your normal behavior)
        float baseAlpha = 0.4f + velFactor * 0.5f;
        float topAlpha = 0.1f;
        float alphaBoost = 0.3f * (_playheadEnergy01 + _lineCharge01);

        Color c0 = Color.white;
        Color c1 = Color.cyan;

// Pulse pushes both ends toward releasePulseColor
        if (pulse01 > 0f)
        {
            c0 = Color.Lerp(c0, releasePulseColor, pulse01);
            c1 = Color.Lerp(c1, releasePulseColor, pulse01);
            alphaBoost += 0.35f * pulse01; // optional: make it “flash” brighter
        }
        Gradient g = new Gradient();
        g.SetKeys(
            new[]
            {
                new GradientColorKey(stepColor, 0f),
                new GradientColorKey(stepColor, 1f),
            },
            new[]
            {
                new GradientAlphaKey(0.85f, 0f),
                new GradientAlphaKey(0.0f, 1f),
            }
        );
        col.color = g;
        // Fire a short extra burst when a burst completes / drums change
        if (_pendingReleasePulse)
        {
            _pendingReleasePulse = false;

            // Emit a short pop; you can tune this count
            playheadParticles.Emit(30);

            // Reset energy target so the bar "empties" after release
            _playheadEnergyTarget01 = 0f;
        }
    }

    // --- Build step → world position maps per row (no lines; direct math) ---
    // (kept as in your current code: map-building block remains commented out)
    int longestSteps = GetDeclaredLongestSteps();
    _ = longestSteps;

    // Move any live markers to their updated step positions
    UpdateNoteMarkerPositions();

    int loopsNow = _drum.completedLoops;
    if (loopsNow != _lastObservedCompletedLoops)
    {
        _lastObservedCompletedLoops = loopsNow;
        OnLoopBoundary();
    }

    for (int i = _blastTasks.Count - 1; i >= 0; i--)
    {
        var task = _blastTasks[i];
        if (!task.go) { _blastTasks.RemoveAt(i); continue; }

        task.t += Time.deltaTime / Mathf.Max(0.0001f, task.dur);
        float u = Mathf.Clamp01(task.t);

        // position lerp along a short ray
        var p = task.startPos + task.dir * u;
        task.go.transform.position = p;

        // scale down/up as desired
        float s = Mathf.Lerp(task.startScale, task.endScale, u);
        task.go.transform.localScale = Vector3.one * s;

        if (u >= 1f)
        {
            try { task.onDone?.Invoke(); } catch (System.Exception e) { Debug.LogException(e); }
            if (task.go) Destroy(task.go);
            _blastTasks.RemoveAt(i);
        }
        else
        {
            _blastTasks[i] = task;
        }
    }

    for (int i = _rushTasks.Count - 1; i >= 0; i--)
    {
        var task = _rushTasks[i];

        // If marker or target vanished, drop the task
        if (!task.go || !task.target)
        {
            _rushTasks.RemoveAt(i);
            continue;
        }

        task.t += Time.deltaTime / Mathf.Max(0.0001f, task.dur);
        float u = Mathf.Clamp01(task.t);

        // Lerp in world space
        var p = Vector3.Lerp(task.startPos, task.target.position, u);
        task.go.transform.position = p;

        if (u >= 1f)
        {
            // Arrived — fire callback, then clean up marker however you do it next
            try { task.onArrive?.Invoke(); } catch (System.Exception e) { Debug.LogException(e); }
            if (task.go) Destroy(task.go); // or return to pool if you pool markers
            _rushTasks.RemoveAt(i);
        }
        else
        {
            _rushTasks[i] = task; // write back mutated struct
        }
    }
}

private void ProcessFirstPlayConfirmFx()
{
    if (firstPlayConfirmOrbPrefab == null) return;
    if (_firstPlayRequests.Count == 0) return;

    double now = AudioSettings.dspTime;

    for (int i = 0; i < _firstPlayRequests.Count; i++)
    {
        var r = _firstPlayRequests[i];
        if (r.spawned) continue;

        // If the target moment already passed, do nothing here.
        // (You may optionally spawn an instant "late" pop instead.)
        if (r.dspTime <= now + 0.0001)
        {
            r.spawned = true;
            _firstPlayRequests[i] = r;
            continue;
        }

        // End position: marker if available, otherwise playhead
        Vector3 endWorld;
        if (noteMarkers != null &&
            noteMarkers.TryGetValue((r.track, r.step), out var markerTr) &&
            markerTr != null)
        {
            endWorld = markerTr.position;
        }
        else
        {
            endWorld = (playheadLine != null) ? playheadLine.position : transform.position;
        }

        // Start position: snapshot the source at spawn time
        Vector3 startWorld = r.source != null ? r.source.position : transform.position;

        // Spawn NOW (not "only when within travel window")
        var ps = Instantiate(
            firstPlayConfirmOrbPrefab,
            startWorld,
            Quaternion.identity,
            _uiParent ? _uiParent : transform
        );

        var main = ps.main;
        main.startColor = r.color;
        main.simulationSpace = ParticleSystemSimulationSpace.World;

        ps.Play(true);

        // IMPORTANT: duration = time remaining until first-play moment
        _firstPlayTasks.Add(new FirstPlayConfirmTask
        {
            ps = ps,
            start = startWorld,
            end = endWorld,
            startDsp = now,
            endDsp = r.dspTime,
            color = r.color
        });

        r.spawned = true;
        _firstPlayRequests[i] = r;
    }
}
private void UpdateFirstPlayConfirmTasks()
{
    if (_firstPlayTasks.Count == 0) return;

    double now = AudioSettings.dspTime;

    for (int i = _firstPlayTasks.Count - 1; i >= 0; i--)
    {
        var t = _firstPlayTasks[i];
        if (t.ps == null)
        {
            _firstPlayTasks.RemoveAt(i);
            continue;
        }

        double dur = System.Math.Max(0.0001, t.endDsp - t.startDsp);

        // Normalized progress in DSP time
        float u = (float)((now - t.startDsp) / dur);
        u = Mathf.Clamp01(u);

        // Smooth movement (prevents “linear snap” feel)
        float eased = u * u * (3f - 2f * u); // SmoothStep

        // If we spawned late for any reason, don’t force the orb to start at the old "start" point visually.
        // This makes late spawns appear *already in progress* instead of racing to catch up.
        Vector3 p = Vector3.Lerp(t.start, t.end, eased);
        t.ps.transform.position = p;

        if (now >= t.endDsp)
        {
            // ARRIVE: burst + cleanup
            var emitParams = new ParticleSystem.EmitParams
            {
                position = t.end,
                startColor = t.color
            };
            t.ps.Emit(emitParams, firstPlayConfirmEmitCount);

            t.ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            Destroy(t.ps.gameObject, 0.35f);

            _firstPlayTasks.RemoveAt(i);
        }
        else
        {
            _firstPlayTasks[i] = t;
        }
    }
}
private Color ComputeStepColor(int step)
{
    var controller = _gfm != null ? _gfm.controller : null;
    if (controller == null || controller.tracks == null) return Color.white;

    float totalW = 0f;
    Vector3 sum = Vector3.zero;

    foreach (var tr in controller.tracks)
    {
        if (tr == null) continue;

        float v = tr.GetVelocityAtStep(step);   // 0..127
        if (v <= 0f) continue;

        float w = v / 127f;
        totalW += w;

        Color c = tr.trackColor;
        sum += new Vector3(c.r, c.g, c.b) * w;
    }

    if (totalW <= 0.0001f) return Color.white;

    Vector3 rgb = sum / totalW;
    return new Color(rgb.x, rgb.y, rgb.z, 1f);
}
    private void OnLoopBoundary()
{
    if (_ascendTasks.Count == 0) return;

    // Take a snapshot of keys so we can safely modify the dictionary.
    var keys = new List<InstrumentTrack>(_ascendTasks.Keys);

    foreach (var trk in keys)
    {
        var task = _ascendTasks[trk];

        // Optional: keep batch-level delay if you still use it.
        if (task.delayLoopsRemaining > 0)
        {
            task.delayLoopsRemaining--;
            _ascendTasks[trk] = task;   // <-- WRITE BACK
            continue;
        }

        bool anyAlive = false;

        for (int i = 0; i < task.markers.Count; i++)
        {
            var ms = task.markers[i];

            // Already finished?
            if (ms.go == null) continue;

            // Per-marker delay (optional)
            if (ms.delayLoopsRemaining > 0)
            {
                ms.delayLoopsRemaining--;
                task.markers[i] = ms;
                anyAlive = true;
                continue;
            }

            // Move one "loop step" toward target
            var t = ms.go.transform;
            var p = t.position; // world space
            p.y += ms.stepY;    // step size was computed at enqueue time
            t.position = p;

            // Countdown loops; when zero, this marker has arrived
            ms.loopsRemaining = Mathf.Max(0, ms.loopsRemaining - 1);

            if (ms.loopsRemaining <= 0)
            {
                // Each arrival at the line gives the "line" a bit more charge.
                _lineCharge01 = Mathf.Clamp01(_lineCharge01 + 0.2f);

                // Per-marker finish (what the coroutine tail used to do)
                _animatingSteps.Remove(ms.key);
                if (ms.go) Destroy(ms.go);

                try { ms.onDonePerMarker?.Invoke(); } catch (System.Exception e) { Debug.LogException(e); }

                ms.go = null; // mark finished
                task.markers[i] = ms;
                // don’t set anyAlive here; marker is done
            }

            else
            {
                // Still moving next loop
                task.markers[i] = ms;
                anyAlive = true;
            }
        }

        task.stepsCompleted++; // keep if you use it elsewhere

        if (!anyAlive)
        {
            // All markers for this track are finished
            try { task.onArrive?.Invoke(); } catch (System.Exception e) { Debug.LogException(e); }
            _ascendTasks.Remove(trk);

            // IMPORTANT: don’t mass-destroy markers or call DestroyOrphanRowMarkers() here.
            // Burst-level cleanup (remove from noteMarkers, culls, audio) is handled by the
            // pending--/if(pending==0) lambda you pass per marker.
        }
        else
        {
            _ascendTasks[trk] = task;   // <-- WRITE BACK
        }
    }
}
    public void MarkGhostPadding(InstrumentTrack track, int startStepInclusive, int count) {
        if (!_ghostNoteSteps.TryGetValue(track, out var set))
            _ghostNoteSteps[track] = set = new HashSet<int>();

        int leaderBins = (_ctrl != null) ? Mathf.Max(1, _ctrl.GetCommittedLeaderBins()) : 1;
        int binSize    = (_drum != null) ? Mathf.Max(1, _drum.totalSteps) : 16;
        int total      = Mathf.Max(1, leaderBins * binSize);

        for (int i = 0; i < count; i++)
            set.Add((startStepInclusive + i) % total);
    }
    private void DisableBuiltInEmissionForTrail()
    {
        if (!playheadParticles) return;
        var emission = playheadParticles.emission;
        emission.enabled = false;

        // Defensive: if enabled elsewhere, force it off.
        emission.rateOverTime = 0f;
        emission.rateOverDistance = 0f;
    }
    private bool RefreshCoreRefs(bool force = false)
    {
        // Cache the singleton once (still cheap, but avoid repeating per-frame)
        if (_gfm == null) _gfm = GameFlowManager.Instance;
        if (_gfm == null) return false;

        // Pull current references from GFM
        var newDrum = _gfm.activeDrumTrack;
        var newCtrl = _gfm.controller;

        // If we have never bound, or something changed (scene swap / re-register), update caches.
        if (force || _drum != newDrum || _ctrl != newCtrl)
        {
            _drum = newDrum;
            _ctrl = newCtrl;
        }

        return (_drum != null && _ctrl != null && _ctrl.tracks != null);
    }

    private void EnqueueBlast(GameObject marker, Vector3 dir, float durationSeconds, float startScale = 1f, float endScale = 0.2f, System.Action onDone = null)
    {
        if (!marker) return;

        // Normalize dir (fallback to a tiny random nudge if zero)
        var d = dir;
        if (d.sqrMagnitude < 1e-6f) d = UnityEngine.Random.insideUnitSphere * 0.5f;
        d = d.normalized * 0.8f; // tune pop distance

        _blastTasks.Add(new BlastTask
        {
            go         = marker,
            startPos   = marker.transform.position,
            dir        = d,
            startScale = startScale,
            endScale   = endScale,
            dur        = Mathf.Max(0.01f, durationSeconds),
            t          = 0f,
            onDone     = onDone
        });
    }
    private float GetAscendTargetWorldY()
    {
        if (lineOfAscent != null)
            return lineOfAscent.position.y + ascendLineWorldPadding;

        // Fallback (old behavior) — but you generally should not rely on this.
        return GetTopWorldY() + ascendLineWorldPadding;
    }
    public float GetTopWorldY()
    {
        RectTransform rt = GetComponent<RectTransform>();
        Vector3[] worldCorners = new Vector3[4];
        rt.GetWorldCorners(worldCorners);
        return worldCorners[1].y;
    }
    private float GetBottomWorldY()
    {
        RectTransform rt = GetComponent<RectTransform>();
        Vector3[] worldCorners = new Vector3[4];
        rt.GetWorldCorners(worldCorners);
        return worldCorners[0].y; // bottom-left corner
    }
    public Transform GetUIParent() => _uiParent;
    int GetDeclaredLongestSteps()
    {
        if (!RefreshCoreRefs(false)) return 1;
        int leaderBins  = Mathf.Max(1, _ctrl.GetCommittedLeaderBins());
        int binSize     = Mathf.Max(1, _drum.totalSteps);
        return leaderBins * binSize;
    }
    public void CanonicalizeTrackMarkers(InstrumentTrack track, int currentBurstId)
{
    if (track == null) return;

    // Resolve row
    int trackIndex = Array.IndexOf(_ctrl.tracks, track);
    Debug.Log($"[CANONICALIZE TRACK MARKERS] {track.name} for {currentBurstId}");
    if (trackIndex < 0 || trackIndex >= trackRows.Count) return;
    var row = trackRows[trackIndex];

    // CURRENT loop steps (authoritative)
    var loopSteps = new HashSet<int>(track.GetPersistentLoopNotes().Select(n => n.Item1));
    Debug.Log($"[CANONICALIZE TRACK MARKERS] {track.name} for {currentBurstId} Loop Steps Count: {loopSteps.Count}");

    // Remove any stale entries for this track first
    var toRemove = new List<(InstrumentTrack,int)>();
    foreach (var kv in noteMarkers)
    {
        if (kv.Key.Item1 == track && (kv.Value == null || kv.Value.gameObject == null))
            toRemove.Add(kv.Key);
    }
    Debug.Log($"[CANONICALIZE TRACK MARKERS] {track.name} for {currentBurstId} Loop Steps Count: {loopSteps.Count} Removed {toRemove.Count}");

    foreach (var k in toRemove)
    {
        Debug.Log($"[CANONICALIZE TRACK MARKERS] Remove {k.Item1}");
        noteMarkers.Remove(k);
    }

    // Pass 1: normalize tags
    var tags = row.GetComponentsInChildren<MarkerTag>(includeInactive: true);
    Debug.Log($"[CANONICALIZE TRACK MARKERS] Tags: {tags.Length}");

    foreach (var tag in tags)
    {
        if (!tag || tag.track != track) continue;

        bool isLoop = loopSteps.Contains(tag.step); // loop is the source of truth
        bool inFilledBin = true;
        try { inFilledBin = track.IsStepInFilledBin(tag.step); } catch {} 
        if (!isLoop && tag.isPlaceholder) { 
            // NEW: keep placeholders that belong to *this* canonicalization burst,
            // // even if the bin isn't filled yet. This prevents just-placed markers
            // (e.g., expansion steps 16–32) from being destroyed immediately,
            // // which is what was breaking your NoteTethers (end became Missing).
            if (tag.burstId == currentBurstId) { 
                var key = (track, tag.step); 
                noteMarkers[key] = tag.transform; // ensure tether rebind can find it
                // ensure greyed look persists
                var ml = tag.GetComponent<MarkerLight>() ?? tag.gameObject.AddComponent<MarkerLight>(); 
                ml.SetGrey(track.trackColor); 
                continue;
            }
            
            // Placeholders from other bursts in unfilled bins can still be culled
            if (!inFilledBin) {
                #if UNITY_EDITOR
                if (!Application.isPlaying) UnityEngine.Object.DestroyImmediate(tag.gameObject);else UnityEngine.Object.Destroy(tag.gameObject);
                #else
                    UnityEngine.Object.Destroy(tag.gameObject);
                #endif
                continue;
            }
            // else: placeholder in a filled bin (legacy visual) → keep; it'll be
            // // // normalized by later lighting/loop writes if appropriate.
        }
        if (isLoop)
        {
            tag.burstId = -1;       // neutral for persistent loop
            tag.isPlaceholder = false;

            var key = (track, tag.step);
            noteMarkers[key] = tag.transform;
            continue;
        }

        // Not in loop: placeholders only
        if (tag.isPlaceholder)
        {
            Debug.Log($"[CANONICALIZE TRACK MARKERS] Placeholder Tag: {tag.gameObject.name}");

            if (tag.burstId != currentBurstId)
            {
                Debug.Log($"[CANONICALIZE TRACK MARKERS] Placeholder Tag: {tag.gameObject.name} BurstID is not Current BurstID");

#if UNITY_EDITOR
                if (!Application.isPlaying) UnityEngine.Object.DestroyImmediate(tag.gameObject);
                else UnityEngine.Object.Destroy(tag.gameObject);
#else
                UnityEngine.Object.Destroy(tag.gameObject);
#endif
                continue;
            }

            // current burst placeholder → keep
            tag.burstId = currentBurstId;
            var key = (track, tag.step);
            noteMarkers[key] = tag.transform;
        }
        else
        {
            // Unexpected non-placeholder not in loop — don’t destroy; neutralize to loop for safety
            tag.burstId = -1;
            tag.isPlaceholder = false;
            var key = (track, tag.step);
            noteMarkers[key] = tag.transform;
        }
    }

    // Pass 2: prune dictionary entries that no longer have a child object
    toRemove.Clear();
    foreach (var kv in noteMarkers)
    {
        if (kv.Key.Item1 != track) continue;
        if (kv.Value == null || kv.Value.gameObject == null) toRemove.Add(kv.Key);
    }
    foreach (var k in toRemove) noteMarkers.Remove(k);

    // Recompute X without stomping Y
    RecomputeTrackLayout(track);
    // NEW: remove orphans using the SAME burst id the caller used
    int activeBurst = (currentBurstId >= 0) ? currentBurstId : track.currentBurstId; 
    DestroyOrphanRowMarkers(track, activeBurst, dryRun: false);
}
    
    private static bool SafeIsStepInFilledBin(InstrumentTrack track, int stepIndex)
    {
        try
        {
            // New API name
            if (track != null && track.IsStepInFilledBin(stepIndex)) return true;
            return false;
        }
        catch
        {
            // Older builds without IsStepInFilledBin → assume filled so UI doesn’t disappear
            return true;
        }
    }
    public GameObject PlacePersistentNoteMarker(InstrumentTrack track, int stepIndex, bool lit = true, int burstId = -1)
{
    Debug.Log($"[PLACE] Starting for {stepIndex} on {track.name}");
    Debug.Log($"[NoteViz] Placing Persistent Note marker for {track} at {stepIndex}, lit {lit} burst id {burstId}");
    var key = (track, stepIndex);

    int trackIndex = Array.IndexOf(_ctrl.tracks, track);
    Debug.Log($"[PLACE] Starting for {stepIndex} on {track.name} index: {trackIndex}");

    if (trackIndex < 0 || trackIndex >= trackRows.Count) return null;
    RectTransform row = trackRows[trackIndex];
    Rect rowRect = row.rect;
    Debug.Log($"[PLACE] Using {row.name} on {track.name} index: {trackIndex}");

    bool inFilledBin = SafeIsStepInFilledBin(track, stepIndex);
    bool isLoopOwned = (burstId < 0);
    bool shouldLight = isLoopOwned ? lit : (lit && inFilledBin);

    Debug.Log($"[PLACE] {stepIndex} on {track.name} index: {trackIndex} Filled bin: {inFilledBin} Loop Owned: {isLoopOwned} Lit: {shouldLight}");

    // REUSE
    if (noteMarkers.TryGetValue(key, out var existing) && existing && existing.gameObject.activeInHierarchy)
    {
        var existingTag0 = existing.GetComponent<MarkerTag>();
        if (existingTag0 != null && existingTag0.isAscending)
            return existing.gameObject;

        UpdateMarkerXPreserveYIfAscending(rowRect, row, track, stepIndex, existing);

        if (_animatingSteps.Contains(key))
        {
            Debug.Log($"[PLACE] Animating Steps Contains {stepIndex} on {track.name} index: {trackIndex} Filled bin: {inFilledBin} Loop Owned: {isLoopOwned} Lit: {shouldLight}");
            UpdateMarkerXPreserveYIfAscending(rowRect, row, track, stepIndex, existing);
            Debug.Log($"[NoteViz] [Reuse-WhileAnimating] step {stepIndex} is animating → keep existing, no new spawn");
            return existing.gameObject;
        }

        if (shouldLight)
        {
            bool inLoop = track.GetPersistentLoopNotes().Any(n => n.Item1 == stepIndex);
            if (inLoop)
            {
                var existingTag = existing.GetComponent<MarkerTag>() ?? existing.gameObject.AddComponent<MarkerTag>();
                existingTag.isPlaceholder = false;
                if (burstId >= 0) existingTag.burstId = burstId; // fix: don’t gate on existingTag.burstId
            }
        }
        else
        {
            var tag = existing.GetComponent<MarkerTag>() ?? existing.gameObject.AddComponent<MarkerTag>();
            tag.isPlaceholder = true;
            if (burstId >= 0) tag.burstId = burstId;

            var ml = existing.GetComponent<MarkerLight>() ?? existing.gameObject.AddComponent<MarkerLight>();
            ml.SetGrey(track.trackColor);

            Debug.Log($"[NV:MARKER_PLACEHOLDER] track={track.name} step={stepIndex} burstIdParam={burstId} markerId={existing.gameObject.GetInstanceID()} placeholder=True");
        }

        Debug.Log($"[PLACE] Returning existing object {stepIndex} on {track.name} index: {trackIndex} Filled bin: {inFilledBin} Loop Owned: {isLoopOwned} Lit: {shouldLight}");
        return existing.gameObject;
    }

    // ADOPT
    var adopt = TryAdoptExistingAt(track, stepIndex, row);
    if (adopt)
    {
        Debug.Log($"[PLACE] Trying to adopt {stepIndex} on {track.name} index: {trackIndex} Filled bin: {inFilledBin} Loop Owned: {isLoopOwned} Lit: {shouldLight}");
        Debug.Log($"[NoteViz] Found note to adopt. This shouldn't happen.");

        noteMarkers[key] = adopt;

        UpdateMarkerXPreserveYIfAscending(rowRect, row, track, stepIndex, adopt);

        var tag = adopt.GetComponent<MarkerTag>() ?? adopt.gameObject.AddComponent<MarkerTag>();
        tag.track = track;
        tag.step = stepIndex;

        if (shouldLight)
        {
            tag.isPlaceholder = false;
            if (burstId >= 0) tag.burstId = burstId;
        }
        else
        {
            tag.isPlaceholder = true;
            if (burstId >= 0) tag.burstId = burstId;

            var ml = adopt.GetComponent<MarkerLight>() ?? adopt.gameObject.AddComponent<MarkerLight>();
            ml.SetGrey(track.trackColor);
        }

        Debug.Log($"[NoteViz] ADOPT marker track={track.name} step={stepIndex} lit={lit} burst={burstId} go={adopt.gameObject.GetInstanceID()}");
        Debug.Log($"[PLACE] Adopting {stepIndex} on {track.name} index: {trackIndex} Filled bin: {inFilledBin} Loop Owned: {isLoopOwned} Lit: {shouldLight}");
        return adopt.gameObject;
    }

    // Positioning for creation
    int totalSteps = Mathf.Max(1, track.GetTotalSteps());
    int binSize = Mathf.Max(1, track.BinSize());
    int leaderBinsForPlacement = GetLeaderBinsForPlacement(track, totalSteps, binSize);
    float xLocal = ComputeXLocalForTrack(rowRect, track, stepIndex, binSize, leaderBinsForPlacement);

    Debug.Log($"xLocal : {xLocal} for track {track.name} stepIndex {stepIndex} lit={lit}");

    float bottomWorldY = GetBottomWorldY();
    float bottomLocalY = row.InverseTransformPoint(new Vector3(0f, bottomWorldY, 0f)).y;

    // Idempotent guard
    if (noteMarkers.TryGetValue(key, out var appeared) && appeared)
    {
        Debug.Log($"[PLACE] Returning Fallback {stepIndex} on {track.name} index: {trackIndex} Filled bin: {inFilledBin} Loop Owned: {isLoopOwned} Lit: {shouldLight}");
        UpdateMarkerXPreserveYIfAscending(rowRect, row, track, stepIndex, appeared);
        return appeared.gameObject;
    }

    // ------------------------------------------------------------
    // CREATE: instantiate as child with worldPositionStays=false,
    // then set LOCAL (row-space) coordinates.
    // ------------------------------------------------------------
    GameObject marker = Instantiate(notePrefab, row, worldPositionStays: false);
    marker.transform.localPosition = new Vector3(xLocal, bottomLocalY, 0f);

    var newTag = marker.GetComponent<MarkerTag>() ?? marker.AddComponent<MarkerTag>();
    newTag.track = track;
    newTag.step = stepIndex;
    newTag.isPlaceholder = !shouldLight;

    // If a burst is provided, stamp it. Otherwise leave as-is (loop-owned).
    if (burstId >= 0) newTag.burstId = burstId;

    noteMarkers[key] = marker.transform;
    Debug.Log($"[NV:MARKER_REGISTER] track={track.name} step={stepIndex} burstIdParam={burstId} markerId={marker.gameObject.GetInstanceID()} lit={shouldLight}");
    Debug.Log($"[NOTEMARKER] Size: {noteMarkers.Count} Key: {key}");

    if (shouldLight)
    {
        var vnm = marker.GetComponent<VisualNoteMarker>();
        if (vnm != null) vnm.Initialize(track.trackColor);

        var light = marker.GetComponent<MarkerLight>() ?? marker.AddComponent<MarkerLight>();
        light.LightUp(track.trackColor);
    }
    else
    {
        var vnm = marker.GetComponent<VisualNoteMarker>();
        if (vnm != null) vnm.SetWaitingParticles(track.trackColor);

        var ml = marker.GetComponent<MarkerLight>() ?? marker.AddComponent<MarkerLight>();
        ml.SetGrey(track.trackColor);
        Debug.Log($"[NOTEMARKER] Setting Marker Grey... position: {ml.transform.position}");
    }

    Debug.Log($"Returning final marker [PLACE] {stepIndex} on {track.name} index: {trackIndex} Filled bin: {inFilledBin} Loop Owned: {isLoopOwned} Lit: {shouldLight}");
    return marker;
}
    private void UpdateNoteMarkerPositions(bool forceXReflow = false)
    {
        // Snapshot to avoid "collection modified" issues if other code mutates noteMarkers this frame.
        var kvs = noteMarkers.ToArray();

        // Reuse a list if you can; shown inline for clarity.
       deadKeys.Clear();

        foreach (var kvp in kvs)
        {
            var track  = kvp.Key.Item1;
            var step   = kvp.Key.Item2;
            var marker = kvp.Value;

            if (!forceXReflow && _animatingSteps != null && _animatingSteps.Contains((track, step)))
                continue;

            if (marker == null) { deadKeys.Add(kvp.Key); continue; }

            if (track == null) { deadKeys.Add(kvp.Key); continue; }

            if (_trackStepWorldPositions == null || !_trackStepWorldPositions.TryGetValue(track, out var map) || map == null)
                continue;
            int trackIndex = (_ctrl != null && _ctrl.tracks != null) ? Array.IndexOf(_ctrl.tracks, track) : -1;
            if (trackIndex < 0 || trackRows == null || trackIndex >= trackRows.Count) continue;

            RectTransform row = trackRows[trackIndex];
            if (row == null) continue;

            if (map.TryGetValue(step, out var worldPos))
            {
                var lp = marker.localPosition;
                float newX = row.InverseTransformPoint(worldPos).x;
                marker.localPosition = new Vector3(newX, lp.y, lp.z);
            }
        }

        for (int i = 0; i < deadKeys.Count; i++)
            noteMarkers.Remove(deadKeys[i]);
    }

    public void TriggerBurstAscend(InstrumentTrack track, int burstId, float durationSeconds) {
    if (track == null || burstId < 0) return; 
    if (!isActiveAndEnabled) return;
    
    var uiParent = GetUIParent(); 
    if (uiParent != null && !uiParent.gameObject.activeInHierarchy) return;

    // Ascend BOTH lit and placeholder markers for this burst.
    // If you truly only want lit loop-owned markers to rise, you can reintroduce filtering,
    // but for burst-cohort visuals this should be inclusive.
    bool includePlaceholders = true;

    var cohort = new List<(int step, Transform rt, MarkerTag tag)>();

    // ---- 1) Primary: dictionary-owned markers (fast path, consistent) ----
    foreach (var kvp in noteMarkers)
    {
        var key = kvp.Key; // (InstrumentTrack, step)
        if (key.Item1 != track) continue;

        var tr = kvp.Value;
        if (!tr) continue;

        var tag = tr.GetComponent<MarkerTag>();
        if (tag == null) continue;

        // We key cohort membership ONLY by burstId
        // Cohort membership: tag burstId OR persisted step->burst registry.
        bool matchesBurst = (tag.burstId == burstId) || (tag.ascendBurstId == burstId) || (_stepBurst.TryGetValue((track, key.Item2), out var sb) && sb == burstId);
        if (!matchesBurst) continue;
        if (!tr.TryGetComponent(out RectTransform rt)) continue;

        cohort.Add((key.Item2, rt, tag));
    }

    // ---- 2) Optional: pick up not-owned markers in the row (debug / resilience) ----
    // If you are still seeing reappearing/disappearing, this helps ensure we animate the actual on-screen cohort.
    if (cohort.Count == 0)
    {
        int trackIndex = Array.IndexOf(_ctrl.tracks, track);
        if (trackIndex >= 0 && trackIndex < trackRows.Count)
        {
            var row = trackRows[trackIndex];
            for (int i = 0; i < row.childCount; i++)
            {
                var child = row.GetChild(i);
                if (!child) continue;

                var tag = child.GetComponent<MarkerTag>();
                if (tag == null) continue;

                if (tag.track != track) continue;

                bool matchesBurst = (tag.burstId == burstId) || (tag.ascendBurstId == burstId) || (_stepBurst.TryGetValue((track, tag.step), out var sb) && sb == burstId);
                if (!matchesBurst) continue;
                if (!child.TryGetComponent(out RectTransform rt)) continue;

                cohort.Add((tag.step, rt, tag));
            }
        }
    }
    
    if (cohort.Count == 0) {
        if (track != null && track.TryGetBurstSteps(burstId, out var burstSteps)) { 
            foreach (var step in burstSteps) { 
                if (!noteMarkers.TryGetValue((track, step), out var tr) || tr == null) continue; 
                var tag = tr.GetComponent<MarkerTag>(); 
                if (tag == null) continue; 
                if (!includePlaceholders && tag.isPlaceholder) continue; 
                cohort.Add((step, tr, tag));
            }
        }
    }
    if (cohort.Count == 0)
    {
        int trackOwned = 0, stepBurstMatch = 0;
        foreach (var kvp in noteMarkers)
        {
            if (kvp.Key.Item1 != track) continue;
            trackOwned++;

            int step = kvp.Key.Item2;
            var tr = kvp.Value;
            var tag = tr ? tr.GetComponent<MarkerTag>() : null;

            bool hasSB = _stepBurst.TryGetValue((track, step), out var sb);
            if (hasSB && sb == burstId) stepBurstMatch++;

            Debug.Log(
                $"[ASCEND:DBG] track={track.name} burstId={burstId} step={step} " +
                $"tagBurst={(tag?tag.burstId:-999)} ascendBurst={(tag?tag.ascendBurstId:-999)} " +
                $"placeholder={(tag?tag.isPlaceholder:false)} hasSB={hasSB} sb={(hasSB?sb:-999)} " +
                $"tr={(tr?tr.GetInstanceID():-1)}");
        }

        Debug.LogWarning(
            $"[ASCEND] track={track.name} burstId={burstId} -> no markers found. " +
            $"noteMarkers(trackOnly)={trackOwned} stepBurstMatches={stepBurstMatch} stepBurstTotal={_stepBurst.Count}");
        return;
    }
// Mark ascending so orphan cleanup never kills them mid-flight
    foreach (var c in cohort)
    {
        c.tag.isAscending = true;
        c.tag.ascendBurstId = burstId;
        _animatingSteps.Add((track, c.step));
    }

    Debug.Log($"[ASCEND] track={track.name} burstId={burstId} cohort={cohort.Count} dur={durationSeconds:F2}s");

    StartCoroutine(AscendCohortCoroutine(track, burstId, cohort, durationSeconds));
}
    private IEnumerator AscendCohortCoroutine(InstrumentTrack track, int burstId, List<(int step, Transform rt, MarkerTag tag)> cohort, float durationSeconds)
{
    // Spawn stationary grey placeholders so the lit marker can rise away.
    foreach (var c in cohort)
    {
        if (!c.rt) continue;
        var row = c.rt.parent as RectTransform;
        if (!row) continue;

        var phGO = Instantiate(c.rt.gameObject, row);
        var phRT = phGO.GetComponent<Transform>();
        phRT.localPosition = c.rt.localPosition;
        phRT.localRotation = c.rt.localRotation;
        phRT.localScale    = c.rt.localScale;

        var phTag = phGO.GetComponent<MarkerTag>() ?? phGO.AddComponent<MarkerTag>();
        phTag.track         = track;
        phTag.step          = c.step;
        phTag.burstId       = burstId;
        phTag.isPlaceholder = true;
        phTag.isAscending   = false;

        var phML = phGO.GetComponent<MarkerLight>() ?? phGO.AddComponent<MarkerLight>();
        phML.SetGrey(track.trackColor);

        var phVNM = phGO.GetComponent<VisualNoteMarker>();
        if (phVNM != null)
        {
            phVNM.IsLit = false;
            if (phVNM.preCaptureParticles != null)
                phVNM.preCaptureParticles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            if (phVNM.capturedParticles != null)
                phVNM.capturedParticles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        }

        // The visual base for this step is now the placeholder.
        noteMarkers[(track, c.step)] = phRT;
    }

    float ascendWorldY = GetAscendTargetWorldY();

    var starts = new Dictionary<int, float>(cohort.Count);
    var ends   = new Dictionary<int, float>(cohort.Count);

    foreach (var c in cohort)
    {
        if (!c.rt) continue;
        var row = c.rt.parent as RectTransform;
        if (!row) continue;

        float startY = c.rt.localPosition.y;

        Vector3 markerWorld = c.rt.position;
        float endY = row.InverseTransformPoint(new Vector3(markerWorld.x, ascendWorldY, markerWorld.z)).y;

        starts[c.step] = startY;
        ends[c.step]   = endY;
    }

    // Enforce a minimum duration that is loop-aware.
    float loopLen = 1.0f;
    if (_drum != null)
    {
        // Prefer the *bar* clock if available; fall back to loop length.
        float clipLen = 0f;
        try { clipLen = _drum.GetClipLengthInSeconds(); } catch {}
        loopLen = (clipLen > 0f) ? clipLen : Mathf.Max(0.05f, _drum.GetLoopLengthInSeconds());
    }
    
    float t = 0f;
    while (t < durationSeconds)
    {
        float u = Mathf.Clamp01(t / durationSeconds);

        foreach (var c in cohort)
        {
            if (!c.rt) continue;
            if (!starts.TryGetValue(c.step, out var y0)) continue;
            if (!ends.TryGetValue(c.step, out var y1)) continue;

            var p = c.rt.localPosition;
            p.y = Mathf.Lerp(y0, y1, u);
            c.rt.localPosition = p;
        }

        t += Time.deltaTime;
        yield return null;
    }

    // Snap to final position
    foreach (var c in cohort)
    {
        if (!c.rt) continue;
        if (!ends.TryGetValue(c.step, out var y1)) continue;

        var p = c.rt.localPosition;
        p.y = y1;
        c.rt.localPosition = p;
    }

    // Remove ascending lit markers now that they reached the line
    foreach (var c in cohort)
    {
        if (!c.rt) continue;

        _animatingSteps.Remove((track, c.step));

#if UNITY_EDITOR
        if (!Application.isPlaying) DestroyImmediate(c.rt.gameObject);
        else Destroy(c.rt.gameObject);
#else
        Destroy(c.rt.gameObject);
#endif
    }

    // IMPORTANT CHANGE:
    // Do NOT clear the track bin immediately (mid-bar can mute the just-collected note).
    // Defer the clear to the next drum-bar boundary so the current bar finishes coherently.
    int binSize = Mathf.Max(1, track.BinSize());
    int anyStep = cohort[0].step;
    int binIdx  = anyStep / binSize;

    StartCoroutine(ClearBinOnNextBarBoundary(track, binIdx, burstId));
}
    private IEnumerator ClearBinOnNextBarBoundary(InstrumentTrack track, int binIdx, int burstId) {
        if (track == null)
            yield break;

        // If we can’t compute a reliable boundary, fall back to immediate clear (still safe).
        if (_drum == null || _drum.startDspTime == 0)
        {
            track.ClearBinNotesKeepAllocated(binIdx);
            CanonicalizeTrackMarkers(track, burstId);
            yield break;
        }

        float loopLen = 0f;
        try { loopLen = _drum.GetClipLengthInSeconds(); } catch {}
        if (loopLen <= 0f)
            loopLen = Mathf.Max(0.05f, _drum.GetLoopLengthInSeconds());

        if (loopLen <= 0.0001f)
        {
            track.ClearBinNotesKeepAllocated(binIdx);
            CanonicalizeTrackMarkers(track, burstId);
            yield break;
        }

        double dspNow   = AudioSettings.dspTime;
        double elapsed  = dspNow - _drum.startDspTime;
        double inBar    = elapsed % loopLen;
        if (inBar < 0) inBar += loopLen;

        float waitSec = (float)(loopLen - inBar);
        if (waitSec < 0.001f) waitSec = 0f;

        if (waitSec > 0f)
            yield return new WaitForSeconds(waitSec);

        track.ClearBinNotesKeepAllocated(binIdx);
        CanonicalizeTrackMarkers(track, burstId);
    }
    public void ConfigureBinStrip(int activeBinCount)
{
    if (binStripParent == null || binIndicatorPrefab == null)
    {
        Debug.LogWarning("[NoteVisualizer] ConfigureBinStrip called but binStripParent or binIndicatorPrefab is not assigned.");
        return;
    }

    activeBinCount = Mathf.Max(0, activeBinCount);

    // If the count is unchanged, do nothing.
    if (activeBinCount == _activeBinCount && _binIndicators.Count == activeBinCount)
        return;

    _activeBinCount = activeBinCount;

    // Clear existing indicators
    foreach (var img in _binIndicators)
    {
        if (img != null)
            Destroy(img.gameObject);
    }
    _binIndicators.Clear();

    if (_activeBinCount == 0)
        return;

    // Instantiate new indicators and lay them out horizontally
    for (int i = 0; i < _activeBinCount; i++)
    {
        var go = Instantiate(binIndicatorPrefab, binStripParent);
        go.name = $"BinIndicator_{i}";
        var img = go.GetComponent<Image>();
        if (img == null)
        {
            img = go.AddComponent<Image>();
            img.color = binInactiveColor;
        }

        _binIndicators.Add(img);
    }

    // Simple layout: distribute evenly across the parent width
    LayoutBinStrip();

    // Default target bin to the first one if out of range
    if (_currentTargetBin < 0 || _currentTargetBin >= _activeBinCount)
    {
        _currentTargetBin = 0;
    }

    RefreshBinHighlight();
}
    /// <summary>
    /// Positions bin indicators evenly within the binStripParent.
    /// </summary>
    private void LayoutBinStrip()
    {
        if (binStripParent == null || _binIndicators.Count == 0)
            return;

        float width = binStripParent.rect.width;
        float height = binStripParent.rect.height;

        int count = _binIndicators.Count;
        if (count == 0) return;

        float slotWidth = width / count;

        for (int i = 0; i < count; i++)
        {
            var img = _binIndicators[i];
            if (img == null) continue;

            var rt = img.rectTransform;
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(0f, 0f);
            rt.pivot     = new Vector2(0.5f, 0.5f);

            float centerX = (i + 0.5f) * slotWidth;
            float centerY = height * 0.5f;

            rt.anchoredPosition = new Vector2(centerX, centerY);
            rt.sizeDelta        = new Vector2(slotWidth * 0.8f, height * 0.8f);
        }
    }
    /// <summary>
    /// Applies colors to bin indicators according to _currentTargetBin.
    /// </summary>
    private void RefreshBinHighlight()
    {
        for (int i = 0; i < _binIndicators.Count; i++)
        {
            var img = _binIndicators[i];
            if (img == null) continue;

            img.color = (i == _currentTargetBin) ? binActiveColor : binInactiveColor;
        }
    }
     public void TriggerNoteBlastOff(InstrumentTrack track)
    {
        var gos = GetNoteMarkers(track);
        var keys = new List<(InstrumentTrack,int)>();
        foreach (var kv in noteMarkers)
            if (kv.Key.Item1 == track) keys.Add(kv.Key);
        foreach (var k in keys) noteMarkers.Remove(k);

        foreach (var go in gos)
            if (go) EnqueueBlast(go, UnityEngine.Random.insideUnitSphere, 0.25f);

        Debug.Log($"[DESTROY] Note Blast off {track.name}");
        DestroyOrphanRowMarkers(track,-1);
    }
    private List<GameObject> GetNoteMarkers(InstrumentTrack track)
    {
        var result = new List<GameObject>();
        foreach (var kvp in noteMarkers)
        {
            if (kvp.Key.Item1 != track) continue;
            var transform = kvp.Value;
            if (transform == null) continue;
            var go = transform.gameObject;
            if (go != null) result.Add(go);
        }
        return result;
    }
    private void UpdateMarkerXPreserveYIfAscending(Rect rowRect, RectTransform row, InstrumentTrack track, int stepIndex, Transform marker)
    {
        if (!marker) return;

        int totalSteps = Mathf.Max(1, track.GetTotalSteps());
        int binSize    = Mathf.Max(1, track.BinSize());
        int leaderBinsForPlacement = GetLeaderBinsForPlacement(track, totalSteps, binSize);
        float xLocal   = ComputeXLocalForTrack(rowRect, track, stepIndex, binSize, leaderBinsForPlacement);

        var tag = marker.GetComponent<MarkerTag>();
        bool ascending = (tag != null && tag.isAscending);

        var lp = marker.localPosition;
        marker.localPosition = ascending
            ? new Vector3(xLocal, lp.y, lp.z)      // preserve Y during ascent
            : new Vector3(xLocal, lp.y, 0f);       // preserve Y generally; row handles baseline
    }
    private void DestroyOrphanRowMarkers(InstrumentTrack track, int activeBurstId, bool dryRun = true)
{
    int trackIndex = Array.IndexOf(_ctrl.tracks, track);
    if (trackIndex < 0 || trackIndex >= trackRows.Count) return;

    var row = trackRows[trackIndex];

    // Build dict-owned set for this track once (prevents O(n^2) checks)
    var owned = new HashSet<Transform>();
    foreach (var kv in noteMarkers)
    {
        if (kv.Key.Item1 != track) continue;
        if (kv.Value) owned.Add(kv.Value);
    }

    var toDestroy = new List<GameObject>();
    var debugNotOwned = new List<GameObject>();
    var debugUntaggedUnowned = new List<GameObject>();

    for (int i = 0; i < row.childCount; i++)
    {
        var child = row.GetChild(i);
        if (!child) continue;

        var tag = child.GetComponent<MarkerTag>();

        // NEW: if untagged and not dict-owned, it's unmanaged “mystery” content.
        if (tag == null)
        {
            bool isOwned = owned.Contains(child);
            if (!isOwned)
            {
                debugUntaggedUnowned.Add(child.gameObject);
                // Treat as orphan candidate (safe because we only act within the row)
                toDestroy.Add(child.gameObject);
            }
            continue;
        }

        // Never treat an in-flight ascent marker as an orphan.
        if (tag.isAscending)
            continue;

        var key = (track, tag.step);

        bool hasKey = noteMarkers.TryGetValue(key, out var tr) && tr;
        bool sameObject = hasKey && tr.gameObject == child.gameObject;

        bool inFilledBin = true;
        try { inFilledBin = track.IsStepInFilledBin(tag.step); } catch { }

        // Only consider destroying placeholders if their bin is filled.
        bool stalePlaceholder = tag.isPlaceholder && inFilledBin && (tag.burstId >= 0) && (tag.burstId != activeBurstId);

        // Duplicate object (dict has key, but points to a different GO)
        bool duplicateForKey = hasKey && !sameObject;

        if (!sameObject) debugNotOwned.Add(child.gameObject);

        // Extra safety: if the dict-owned marker is ascending, do not destroy anything for this key.
        if (duplicateForKey)
        {
            var dictTag = tr.GetComponent<MarkerTag>();
            if (dictTag != null && dictTag.isAscending)
                duplicateForKey = false;
        }

        if (stalePlaceholder || duplicateForKey)
            toDestroy.Add(child.gameObject);

        Debug.Log($"[ORPHAN] considering {tag.step} ph={tag.isPlaceholder} bid={tag.burstId} " +
                  $"hasKey={hasKey} same={sameObject} stalePH={stalePlaceholder} dup={duplicateForKey} (active={activeBurstId})");
    }

    if (debugNotOwned.Count > 0)
    {
        Debug.LogWarning($"[NoteViz] {(dryRun ? "FOUND" : "CLEANING")} not-owned markers track={track.name} :: " +
                         string.Join(", ", debugNotOwned.Select(o => o ? o.GetInstanceID().ToString() : "null")));
    }

    if (debugUntaggedUnowned.Count > 0)
    {
        Debug.LogWarning($"[NoteViz] {(dryRun ? "FOUND" : "CLEANING")} UNTAGGED+UNOWNED markers track={track.name} :: " +
                         string.Join(", ", debugUntaggedUnowned.Select(o => o ? o.GetInstanceID().ToString() : "null")));
    }

    if (!dryRun)
    {
        foreach (var go in toDestroy)
        {
#if UNITY_EDITOR
            if (!Application.isPlaying) DestroyImmediate(go);
            else Destroy(go);
#else
            Destroy(go);
#endif
        }
    }
}
    private float GetScreenWidth() {
        RectTransform rt = _worldSpaceCanvas.GetComponent<RectTransform>();
        return rt.rect.width;
    }
    public void RecomputeTrackLayout(InstrumentTrack track)
{
    try
    {
        if (track == null) return;

        int trackIndex = Array.IndexOf(_ctrl.tracks, track);
        if (trackIndex < 0 || trackIndex >= trackRows.Count) return;

        RectTransform row = trackRows[trackIndex];
        Rect rowRect = row.rect;

        int totalSteps = Mathf.Max(1, track.GetTotalSteps());
        int binSize = Mathf.Max(1, track.BinSize());

        int leaderBinsForPlacement = GetLeaderBinsForPlacement(track, totalSteps, binSize);

        int leaderBinsBase;
        if (_forcedLeaderSteps >= 1)
            leaderBinsBase = Mathf.Max(1, Mathf.CeilToInt(_forcedLeaderSteps / (float)binSize));
        else
            leaderBinsBase = Mathf.Max(1, _ctrl.GetCommittedLeaderBins());

        int trackBins = Mathf.Max(1, Mathf.CeilToInt(totalSteps / (float)binSize));

        leaderBinsForPlacement = Mathf.Max(leaderBinsBase, trackBins);

        float bottomWorldY = GetBottomWorldY();
        float bottomLocalY = row.InverseTransformPoint(new Vector3(0f, bottomWorldY, 0f)).y;

        // -----------------------------------------------------------------
        // 1) Reconcile duplicates: choose a canonical marker per step
        //    based on tags on row children, then force noteMarkers to match.
        // -----------------------------------------------------------------
        var chosenByStep = new Dictionary<int, Transform>(64);
        var chosenTagByStep = new Dictionary<int, MarkerTag>(64);

        for (int i = 0; i < row.childCount; i++)
        {
            var child = row.GetChild(i);
            if (!child) continue;

            var tag = child.GetComponent<MarkerTag>();
            if (tag == null) continue;

            if (tag.track != track) continue;

            int step = tag.step;
            if (step < 0) continue;

            // If multiple markers exist for this step, pick a canonical one.
            if (!chosenByStep.TryGetValue(step, out var existingTf) || !existingTf)
            {
                chosenByStep[step] = child;
                chosenTagByStep[step] = tag;
                continue;
            }

            var existingTag = chosenTagByStep[step];

            // Priority rules:
            // 1) Prefer ascending over non-ascending (ascending is the one that should align with audio during ascent)
            // 2) Prefer non-placeholder over placeholder
            // 3) Prefer higher burstId (newer/explicit) when all else equal
            bool aAsc = tag.isAscending;
            bool bAsc = existingTag != null && existingTag.isAscending;

            bool aPH = tag.isPlaceholder;
            bool bPH = existingTag != null && existingTag.isPlaceholder;

            int aBid = tag.burstId;
            int bBid = existingTag != null ? existingTag.burstId : -999999;

            bool takeA = false;

            if (aAsc != bAsc) takeA = aAsc; // ascending wins
            else if (aPH != bPH) takeA = !aPH; // non-placeholder wins
            else if (aBid != bBid) takeA = aBid > bBid; // higher burst id wins (heuristic)
            else takeA = false; // keep existing deterministically

            if (takeA)
            {
                chosenByStep[step] = child;
                chosenTagByStep[step] = tag;
            }
        }

        // Force dictionary to point at the canonical marker for each discovered step.
        foreach (var kv in chosenByStep)
        {
            int step = kv.Key;
            var tf = kv.Value;
            if (!tf) continue;

            var dictKey = (track, step);
            if (noteMarkers.TryGetValue(dictKey, out var oldTf) && oldTf && oldTf != tf)
            {
                Debug.LogWarning(
                    $"[RECOMPUTE] DICT_SWAP track={track.name} step={step} old={oldTf.gameObject.GetInstanceID()} new={tf.gameObject.GetInstanceID()}");
            }

            noteMarkers[dictKey] = tf;
        }

        // -----------------------------------------------------------------
        // 2) Reposition the canonical instances (dict-owned after reconciliation).
        // -----------------------------------------------------------------
        var kvs = noteMarkers.ToArray();
        foreach (var kv in kvs)
        {
            var key = kv.Key;
            var tf = kv.Value;

            if (key.Item1 != track || !tf) continue;

            int stepIndex = key.Item2;

            float xLocal = ComputeXLocalForTrack(rowRect, track, stepIndex, binSize, leaderBinsForPlacement);

            var lp = tf.localPosition;

            // Preserve Y if ascending, otherwise pin to bottom.
            float yLocal = IsAscending(tf) ? lp.y : bottomLocalY;

            tf.localPosition = new Vector3(xLocal, yLocal, lp.z);
        }

        // -----------------------------------------------------------------
        // 3) Optional cleanup: destroy non-canonical duplicates in this row.
        //    This is what removes the “mystery” markers during commit expand.
        // -----------------------------------------------------------------
        for (int i = row.childCount - 1; i >= 0; i--)
        {
            var child = row.GetChild(i);
            if (!child) continue;

            var tag = child.GetComponent<MarkerTag>();
            if (tag == null) continue;
            if (tag.track != track) continue;

            int step = tag.step;
            if (step < 0) continue;

            if (!chosenByStep.TryGetValue(step, out var canonical) || !canonical) continue;

            if (child == canonical) continue;

            // Don’t kill things mid-animation churn.
            bool isAnimatingNow = false;
            if (_animatingSteps != null)
            {
                var animKey = (track, step);
                isAnimatingNow = _animatingSteps.Contains(animKey);
            }

            if (isAnimatingNow)
            {
                Debug.LogWarning(
                    $"[RECOMPUTE] KEEP_DUP_DURING_ANIM track={track.name} step={step} dup={child.gameObject.GetInstanceID()} canonical={canonical.gameObject.GetInstanceID()}");
                continue;
            }


#if UNITY_EDITOR
            if (!Application.isPlaying) DestroyImmediate(child.gameObject);
            else Destroy(child.gameObject);
#else
        Destroy(child.gameObject);
#endif
        }
    }
    catch (Exception ex)
    {
        Debug.LogError($"[RECOMPUTE] EXCEPTION track={track?.name ?? "NULL"} ex={ex}");
    }
}
    private int GetLeaderBinsForPlacement(InstrumentTrack track, int totalSteps, int binSize) {
        int leaderBinsBase; 
        if (_forcedLeaderSteps >= 1) { 
            leaderBinsBase = Mathf.Max(1, Mathf.CeilToInt(_forcedLeaderSteps / (float)binSize));
        }
        else { 
            leaderBinsBase = Mathf.Max(1, _ctrl.GetCommittedLeaderBins());
        }
        // Ensure placement width can represent this track's current bins.
        int trackBins = Mathf.Max(1, Mathf.CeilToInt(totalSteps / (float)binSize)); 
        return Mathf.Max(leaderBinsBase, trackBins);
    }
    public void RequestLeaderGridChange(int newLeaderSteps) { 
        // Apply immediately to prevent left-half folding during growth.
        // NOTE: This method previously ignored its parameter; it now becomes the single
        // source of truth for "snap the grid to this leader width" moments.
        _forcedLeaderSteps = (newLeaderSteps > 0) ? Mathf.Max(1, newLeaderSteps) : -1;
        
         if (_ctrl?.tracks == null) return;
         foreach (var t in _ctrl.tracks) 
             if (t) RecomputeTrackLayout(t);
         //UpdateNoteMarkerPositions(true);
    }
    private float ComputeXLocalForTrack(Rect rowRect, InstrumentTrack track, int stepIndex) { 
        int totalSteps = Mathf.Max(1, track.GetTotalSteps()); 
        int binSize    = Mathf.Max(1, track.BinSize()); 
        int leaderBins = GetLeaderBinsForPlacement(track, totalSteps, binSize); 
        return ComputeXLocalForTrack(rowRect, track, stepIndex, binSize, leaderBins);
    }
    float ComputeXLocalForTrack(Rect rowRect, InstrumentTrack track, int stepIndex, int binSize, int leaderBinsForPlacement)
    {
        if (track == null) return rowRect.xMin;

        if (stepIndex < 0) return rowRect.xMin;

        binSize = Mathf.Max(1, binSize);
        leaderBinsForPlacement = Mathf.Max(1, leaderBinsForPlacement);

        int binIndex   = stepIndex / binSize;
        int localInBin = stepIndex % binSize;

        float uMin = (float)binIndex / leaderBinsForPlacement;
        float uMax = (float)(binIndex + 1) / leaderBinsForPlacement;

        float uLocal = (localInBin + 0.5f) / binSize;

        float u = Mathf.Lerp(uMin, uMax, uLocal);
        u = Mathf.Clamp01(u);

        return Mathf.Lerp(rowRect.xMin, rowRect.xMax, u);
    }
    private Transform TryAdoptExistingAt(InstrumentTrack track, int stepIndex, RectTransform row)
    {
        // Look in the row for any marker with the same (track,step)
        var tag = row.GetComponentsInChildren<MarkerTag>(includeInactive: true)
            .FirstOrDefault(t => t && t.track == track && t.step == stepIndex);
        return tag ? tag.transform : null;
    }
    bool IsAscending(Transform tf) {
        if (tf == null) return false;
        foreach (var kv in _ascendTasks) // _ascendTasks: track -> task
        {
            var task = kv.Value;
            if (task == null || task.markers == null) continue;
            for (int i = 0; i < task.markers.Count; i++)
            {
                var ms = task.markers[i];
                if (ms.go != null && ms.go.transform == tf)
                    return true;
            }
        }
        return false;
    }
    
}
