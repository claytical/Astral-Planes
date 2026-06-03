using System;
using System.Collections;
using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using UnityEngine.UI;

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
    [Tooltip("How many steps ahead (max) the cue starts moving toward the target. Beyond this distance it stays near the vehicle.")]
    [Min(1)] public int releaseCueLookaheadSteps = 8;
    [Tooltip("World-space arc height for the cue path (0 = straight line).")]
    public float releaseCueArcHeight = 0.8f;
    [Header("Track Rows (one per InstrumentTrack in controller order)")]
    public List<RectTransform> trackRows;
    [Header("Bin Visualization")]
    [Tooltip("Parent RectTransform where bin indicators will be instantiated.")]
    public RectTransform binStripParent;
    public Dictionary<(InstrumentTrack, int), Transform> noteMarkers = new();
    [Header("First-Play Confirm FX")]
    [SerializeField] private ParticleSystem firstPlayConfirmOrbPrefab;
    [SerializeField] private float firstPlayConfirmTravelSeconds = 2f;
    [SerializeField] private int firstPlayConfirmEmitCount = 24;
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
    private readonly List<Image> _binIndicators = new List<Image>();
    private int _activeBinCount = 0;      // How many bins are currently in use (post-contraction)
    private int _currentTargetBin = -1;  

    private Vector3 _lastPlayheadParticleWorldPos;
    private bool _hasLastPlayheadParticleWorldPos;
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
    private struct RushTask
    {
        public GameObject go;        // marker being animated
        public Vector3 startPos;     // cached at enqueue
        public Transform target;     // vehicle (or any target)
        public float dur;            // seconds
        public float t;              // normalized 0..1
        public Action onArrive; // called when we reach target (chain effects/cleanup)
    }
    private int _lastObservedCompletedLoops = -1;
    private readonly Dictionary<(InstrumentTrack track, int step), int> _stepBurst = new();
    private readonly HashSet<(InstrumentTrack track, int step)> _animatingSteps = new();
    private readonly List<BlastTask> _blastTasks = new();
    private readonly List<RushTask> _rushTasks = new();

    private readonly Dictionary<int, GameObject> _releaseCuesByVehicle = new();

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
        if (GameFlowManager.VerboseLogging) Debug.Log($"[RegisterCollected] {track.name} burstId={burstId} step={step}, markerGo y={markerGo.transform.position.y:F1}");
        if (noteMarkers.TryGetValue((track, step), out var existing) && existing && existing.gameObject != markerGo)
        {
            if (GameFlowManager.VerboseLogging) Debug.Log($"  → DESTROYING old marker at step {step}, was at y={existing.position.y:F1}");
            Destroy(existing.gameObject);
        }

        noteMarkers[(track, step)] = markerGo.transform;
        _stepBurst[(track, step)]  = burstId;
        if (GameFlowManager.VerboseLogging) Debug.Log($"[NV:STEP_BURST_SET] track={track.name} step={step} burstId={burstId} markerId={markerGo.GetInstanceID()}");

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
    

    public void ClearManualReleaseCue(Transform vehicle)
    {
        if (vehicle == null) return;
        int id = vehicle.GetInstanceID();
        if (_releaseCuesByVehicle.TryGetValue(id, out var cue) && cue != null)
            Destroy(cue);
        _releaseCuesByVehicle.Remove(id);
    }

    public void BlastManualReleaseCue(Transform vehicle)
    {
        ClearManualReleaseCue(vehicle);
    }

    public void BlastManualReleaseCueFailure(Transform vehicle, Vector3 blastPos, Color roleColor)
    {
        ClearManualReleaseCue(vehicle);

        if (failureExplosionPrefab != null)
        {
            var ps = Instantiate(failureExplosionPrefab, blastPos, Quaternion.identity);
            var main = ps.main;
            main.startColor = roleColor * 0.45f;
            ps.Play();
            Destroy(ps.gameObject, main.duration + main.startLifetime.constantMax);
        }
    }

    public void PulseMarkerSpecial(InstrumentTrack track, int stepAbs)
    {
        if (track == null) return;
        if (noteMarkers == null) return;
        if (!noteMarkers.TryGetValue((track, stepAbs), out var tr) || tr == null) return;
        var ml = tr.GetComponent<MarkerLight>();
        if (ml != null) ml.LightUp(track.trackColor);
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

    // Creates rows for any tracks beyond the inspector-configured set.
    // Redistributes the total Y anchor range evenly so all rows fit without overflowing.
    private void EnsureTrackRowsForAllTracks()
    {
        if (trackRows == null || trackRows.Count == 0 || _ctrl?.tracks == null) return;

        int needed = 0;
        for (int i = 0; i < _ctrl.tracks.Length; i++)
            if (_ctrl.tracks[i] != null) needed = i + 1;

        int existing = trackRows.Count;
        if (existing >= needed) return;

        // Measure total Y anchor range that existing rows collectively cover.
        float totalMin = float.MaxValue, totalMax = float.MinValue;
        foreach (var r in trackRows)
        {
            if (r == null) continue;
            if (r.anchorMin.y < totalMin) totalMin = r.anchorMin.y;
            if (r.anchorMax.y > totalMax) totalMax = r.anchorMax.y;
        }
        if (totalMin >= totalMax) { totalMin = 0f; totalMax = 1f; }

        // Find a non-null template to clone offsetMin/Max.y from.
        RectTransform template = null;
        for (int i = trackRows.Count - 1; i >= 0; i--)
            if (trackRows[i] != null) { template = trackRows[i]; break; }
        if (template == null || template.parent == null) return;

        // Create missing row GameObjects.
        for (int i = existing; i < needed; i++)
        {
            var go = new GameObject($"TrackRow_Auto_{i}", typeof(RectTransform));
            go.transform.SetParent(template.parent, worldPositionStays: false);
            var rt = go.GetComponent<RectTransform>();
            rt.offsetMin = new Vector2(0f, template.offsetMin.y);
            rt.offsetMax = new Vector2(0f, template.offsetMax.y);
            trackRows.Add(rt);
        }

        // Redistribute all rows equally across the total Y range.
        float rowHeight = (totalMax - totalMin) / needed;
        for (int i = 0; i < needed; i++)
        {
            var row = trackRows[i];
            if (row == null) continue;
            float yMin = totalMin + rowHeight * i;
            row.anchorMin = new Vector2(0f, yMin);
            row.anchorMax = new Vector2(1f, yMin + rowHeight);
            row.offsetMin = new Vector2(0f, row.offsetMin.y);
            row.offsetMax = new Vector2(0f, row.offsetMax.y);
        }
    }
    private void UpdatePlayheadParticleTrailWorld()
    {
        if (playheadParticles == null || playheadLine == null) return;

        var main = playheadParticles.main;
        if (main.simulationSpace != ParticleSystemSimulationSpace.World)
            main.simulationSpace = ParticleSystemSimulationSpace.World;

        Vector3 now = playheadParticles.transform.position;

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
                emitParams.startColor = Color.Lerp(Color.white, GetReleasePulseColor(_lastReleasePulseRole), .5f);
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
        ascensionDirector?.ClearAllTasks();
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
    public void TriggerPlayheadReleasePulse(MusicalRole role = MusicalRole.None)
    {
        _lastReleasePulseRole = role;
        _pendingReleasePulse = true;
        _releasePulseT = releasePulseSeconds; // start the color pulse immediately
    }

    private Color GetReleasePulseColor(MusicalRole role)
    {
        if (role != MusicalRole.None)
        {
            var profile = MusicalRoleProfileLibrary.GetProfile(role);
            if (profile != null) return profile.GetBaseColor();
        }
        return releasePulseColorFallback;
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
        UpdateFirstPlayConfirmTasks();
        UpdatePlayheadParticleTrailWorld();
        ComputeCurrentStepState(leaderStartDsp, out int currentStep, out bool shimmer, out float maxVelocity);
        stepColor = ComputeStepColor(currentStep);
        UpdateParticleEmission(shimmer, maxVelocity);
        UpdateNoteMarkerPositions();

        int loopsNow = _drum.completedLoops;
        if (loopsNow != _lastObservedCompletedLoops)
            _lastObservedCompletedLoops = loopsNow;

        TickAnimationTasks();
    }

    // Visual clock MUST match the audio clock: use leader loop length for both x-position and step sampling.
    // GetLeaderSteps() returns the expanded step count (e.g. 32 for a 2-bin loop).
    private void MovePlayheadLine(double leaderStartDsp)
    {
        int drumTotalSteps = Mathf.Max(1, _drum.GetLeaderSteps());
        float fullVisualLoopDuration = Mathf.Max(0.0001f, _drum.GetLoopLengthInSeconds());
        float globalElapsed = (float)(AudioSettings.dspTime - leaderStartDsp);
        float globalNormalized = (globalElapsed % fullVisualLoopDuration) / fullVisualLoopDuration;
        float xPos = Mathf.Lerp(0f, GetScreenWidth(), Mathf.Clamp01(globalNormalized));
        playheadLine.anchoredPosition = new Vector2(xPos, playheadLine.anchoredPosition.y);
    }

    private void ComputeCurrentStepState(double leaderStartDsp,
        out int currentStep, out bool shimmer, out float maxVelocity)
    {
        int drumTotalSteps = Mathf.Max(1, _drum.GetLeaderSteps());
        float fullVisualLoopDuration = Mathf.Max(0.0001f, _drum.GetLoopLengthInSeconds());
        float stepDuration = fullVisualLoopDuration / drumTotalSteps;
        float leaderT = (float)((AudioSettings.dspTime - leaderStartDsp) % fullVisualLoopDuration);
        if (leaderT < 0f) leaderT += fullVisualLoopDuration;

        currentStep = Mathf.FloorToInt(leaderT / stepDuration);
        currentStep = ((currentStep % drumTotalSteps) + drumTotalSteps) % drumTotalSteps;

        shimmer = false;
        maxVelocity = 0f;
        var controller = _gfm?.controller;
        if (controller?.tracks == null) return;
        foreach (var track in controller.tracks)
        {
            if (track == null) continue;
            maxVelocity = Mathf.Max(maxVelocity, track.GetVelocityAtStep(currentStep) / 127f);
            if (!shimmer && _ghostNoteSteps.TryGetValue(track, out var steps)
                && steps != null && steps.Contains(currentStep))
                shimmer = true;
        }
    }

    private void UpdateParticleEmission(bool shimmer, float maxVelocity)
    {
        if (playheadParticles == null) return;

        var main = playheadParticles.main;
        var emission = playheadParticles.emission;
        float velFactor = Mathf.Clamp01(maxVelocity);
        float energyFactor = Mathf.Lerp(0.3f, 1.0f, _playheadEnergy01);
        float chargeFactor = 1.0f + 1.5f * _lineCharge01;

        main.startSize = Mathf.Lerp(0.8f, 1.2f, velFactor) * energyFactor;
        emission.rateOverTime = Mathf.Lerp(10f, 50f, velFactor) * energyFactor * chargeFactor;
        emission.enabled = shimmer || _lineCharge01 > 0.05f || _playheadEnergy01 > 0.05f;

        if (_releasePulseT > 0f)
            _releasePulseT = Mathf.Max(0f, _releasePulseT - Time.deltaTime);

        var col = playheadParticles.colorOverLifetime;
        col.enabled = true;
        Gradient g = new Gradient();
        g.SetKeys(
            new[] { new GradientColorKey(stepColor, 0f), new GradientColorKey(stepColor, 1f) },
            new[] { new GradientAlphaKey(0.85f, 0f), new GradientAlphaKey(0.0f, 1f) }
        );
        col.color = g;

        if (_pendingReleasePulse)
        {
            _pendingReleasePulse = false;
            playheadParticles.Emit(30);
            _playheadEnergyTarget01 = 0f;
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
    public void MarkGhostPadding(InstrumentTrack track, int startStepInclusive, int count) {
        if (!_ghostNoteSteps.TryGetValue(track, out var set))
            _ghostNoteSteps[track] = set = new HashSet<int>();

        int leaderBins = (_ctrl != null) ? Mathf.Max(1, _ctrl.GetCommittedLeaderBins()) : 1;
        int binSize    = (_drum != null) ? Mathf.Max(1, _drum.totalSteps) : 16;
        int total      = Mathf.Max(1, leaderBins * binSize);

        for (int i = 0; i < count; i++)
            set.Add((startStepInclusive + i) % total);
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
        ascensionDirector?.Initialize(_drum);

        return (_drum != null && _ctrl != null && _ctrl.tracks != null);
    }

    // Returns false if no usable anchor is available (Update should return early).
    // Writes the resolved anchor DSP time into leaderStartDsp.
    private bool TickAnchorGuard(out double leaderStartDsp)
    {
        leaderStartDsp =
            (_drum.leaderStartDspTime > 0.0) ? _drum.leaderStartDspTime :
            (_drum.startDspTime > 0.0)       ? _drum.startDspTime :
                                               0.0;

        if (leaderStartDsp <= 0.0)
        {
            if (!_hasCachedDrumAnchor)
                return false;
            leaderStartDsp = _cachedLeaderStartDspTime;
        }
        else
        {
            _hasCachedDrumAnchor = true;
            _cachedLeaderStartDspTime = leaderStartDsp;
        }
        return true;
    }

    private void TickPlayheadEnergy()
    {
        _playheadEnergy01 = Mathf.MoveTowards(
            _playheadEnergy01,
            _playheadEnergyTarget01,
            _playheadEnergyLerpSpeed * Time.deltaTime
        );
    }

    private float GetAscendTargetWorldY()
    {
        return ascensionDirector != null
            ? ascensionDirector.GetAscendTargetWorldY()
            : GetTopWorldY();   // fallback — should not normally be reached
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

    public void CanonicalizeTrackMarkers(InstrumentTrack track, int currentBurstId)
    {
        if (track == null) return;

        int trackIndex = Array.IndexOf(_ctrl.tracks, track);
        if (GameFlowManager.VerboseLogging) Debug.Log($"[CANONICALIZE TRACK MARKERS] {track.name} for {currentBurstId}");
        if (trackIndex < 0 || trackIndex >= trackRows.Count) return;
        var row = trackRows[trackIndex];

        var loopSteps = new HashSet<int>(track.GetPersistentLoopNotes().Select(n => n.Item1));

        RemoveStaleMarkerEntries(track);
        NormalizeTagsOnRow(row, track, loopSteps, currentBurstId);
        PruneStaleMarkerDictEntries(track);

        RecomputeTrackLayout(track);
        int activeBurst = (currentBurstId >= 0) ? currentBurstId : track.currentBurstId;
        DestroyOrphanRowMarkers(track, activeBurst, dryRun: false);
    }

    private void RemoveStaleMarkerEntries(InstrumentTrack track)
    {
        var toRemove = new List<(InstrumentTrack, int)>();
        foreach (var kv in noteMarkers)
        {
            if (kv.Key.Item1 == track && (kv.Value == null || kv.Value.gameObject == null))
                toRemove.Add(kv.Key);
        }
        foreach (var k in toRemove)
        {
            if (GameFlowManager.VerboseLogging) Debug.Log($"[CANONICALIZE TRACK MARKERS] Remove {k.Item1}");
            noteMarkers.Remove(k);
        }
    }

    private void NormalizeTagsOnRow(RectTransform row, InstrumentTrack track, HashSet<int> loopSteps, int currentBurstId)
    {
        var tags = row.GetComponentsInChildren<MarkerTag>(includeInactive: true);
        if (GameFlowManager.VerboseLogging) Debug.Log($"[CANONICALIZE TRACK MARKERS] Tags: {tags.Length}");

        foreach (var tag in tags)
        {
            if (!tag || tag.track != track) continue;
            if (tag.isAscending) continue;

            bool isLoop = loopSteps.Contains(tag.step);
            bool inFilledBin = true;
            try { inFilledBin = track.IsStepInFilledBin(tag.step); } catch {}
            if (!isLoop && tag.isPlaceholder)
            {
                // Keep placeholders that belong to this canonicalization burst even if bin
                // isn’t filled yet — prevents just-placed expansion markers from being
                // destroyed immediately (which broke NoteTether end targets).
                if (tag.burstId == currentBurstId)
                {
                    var key = (track, tag.step);
                    noteMarkers[key] = tag.transform;
                    var ml = tag.GetComponent<MarkerLight>() ?? tag.gameObject.AddComponent<MarkerLight>();
                    ml.SetGrey(track.trackColor);
                    continue;
                }

                if (!inFilledBin)
                {
#if UNITY_EDITOR
                    if (!Application.isPlaying) UnityEngine.Object.DestroyImmediate(tag.gameObject);
                    else UnityEngine.Object.Destroy(tag.gameObject);
#else
                    UnityEngine.Object.Destroy(tag.gameObject);
#endif
                    continue;
                }
            }
            if (isLoop)
            {
                // Density-injection guard: placeholder for current burst whose step is
                // already committed by a previous burst — preserve placeholder so Vehicle
                // can still target it for release.
                if (tag.isPlaceholder && tag.burstId == currentBurstId)
                {
                    noteMarkers[(track, tag.step)] = tag.transform;
                    continue;
                }

                tag.isPlaceholder = false;
                var key = (track, tag.step);
                noteMarkers[key] = tag.transform;
                continue;
            }

            if (tag.isPlaceholder)
            {
                if (GameFlowManager.VerboseLogging) Debug.Log($"[CANONICALIZE TRACK MARKERS] Placeholder Tag: {tag.gameObject.name}");

                if (tag.burstId != currentBurstId)
                {
                    if (GameFlowManager.VerboseLogging) Debug.Log($"[CANONICALIZE TRACK MARKERS] Placeholder Tag: {tag.gameObject.name} BurstID is not Current BurstID");
#if UNITY_EDITOR
                    if (!Application.isPlaying) UnityEngine.Object.DestroyImmediate(tag.gameObject);
                    else UnityEngine.Object.Destroy(tag.gameObject);
#else
                    UnityEngine.Object.Destroy(tag.gameObject);
#endif
                    continue;
                }

                tag.burstId = currentBurstId;
                var key = (track, tag.step);
                noteMarkers[key] = tag.transform;
            }
            else
            {
                // Non-placeholder not in loop — neutralize to loop for safety, don’t destroy.
                tag.burstId = -1;
                tag.isPlaceholder = false;
                var key = (track, tag.step);
                noteMarkers[key] = tag.transform;
            }
        }
    }

    private void PruneStaleMarkerDictEntries(InstrumentTrack track)
    {
        var toRemove = new List<(InstrumentTrack, int)>();
        foreach (var kv in noteMarkers)
        {
            if (kv.Key.Item1 != track) continue;
            if (kv.Value == null || kv.Value.gameObject == null) toRemove.Add(kv.Key);
        }
        foreach (var k in toRemove) noteMarkers.Remove(k);
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
        if (GameFlowManager.VerboseLogging) Debug.Log($"[PLACE] Starting for {stepIndex} on {track.name}");
        var key = (track, stepIndex);

        EnsureTrackRowsForAllTracks();
        int trackIndex = Array.IndexOf(_ctrl.tracks, track);
        if (trackIndex < 0 || trackIndex >= trackRows.Count) return null;
        RectTransform row = trackRows[trackIndex];
        Rect rowRect = row.rect;

        bool shouldLight = lit;

        if (TryReuseExistingMarker(key, row, rowRect, track, stepIndex, shouldLight, burstId, out var reused))
            return reused;

        if (TryAdoptMarker(key, row, rowRect, track, stepIndex, shouldLight, lit, burstId, out var adopted))
            return adopted;

        return SpawnNewPersistentMarker(key, row, rowRect, track, stepIndex, shouldLight, burstId);
    }

    private bool TryReuseExistingMarker(
        (InstrumentTrack, int) key, RectTransform row, Rect rowRect,
        InstrumentTrack track, int stepIndex, bool shouldLight, int burstId,
        out GameObject result)
    {
        result = null;
        if (!noteMarkers.TryGetValue(key, out var existing) || !existing || !existing.gameObject.activeInHierarchy)
            return false;

        var existingTag0 = existing.GetComponent<MarkerTag>();
        if (existingTag0 != null && existingTag0.isAscending)
        {
            result = existing.gameObject;
            return true;
        }

        UpdateMarkerXPreserveYIfAscending(rowRect, row, track, stepIndex, existing);

        if (_animatingSteps.Contains(key))
        {
            if (GameFlowManager.VerboseLogging) Debug.Log($"[NoteViz] [Reuse-WhileAnimating] step {stepIndex} is animating → keep existing, no new spawn");
            result = existing.gameObject;
            return true;
        }

        if (shouldLight)
        {
            bool inLoop = track.GetPersistentLoopNotes().Any(n => n.Item1 == stepIndex);
            if (inLoop)
            {
                var existingTag = existing.GetComponent<MarkerTag>() ?? existing.gameObject.AddComponent<MarkerTag>();
                existingTag.isPlaceholder = false;
                if (burstId >= 0) existingTag.burstId = burstId;
            }
        }
        else
        {
            var tag = existing.GetComponent<MarkerTag>() ?? existing.gameObject.AddComponent<MarkerTag>();
            tag.isPlaceholder = true;
            if (burstId >= 0) tag.burstId = burstId;

            var ml = existing.GetComponent<MarkerLight>() ?? existing.gameObject.AddComponent<MarkerLight>();
            ml.SetGrey(track.trackColor);
            if (GameFlowManager.VerboseLogging) Debug.Log($"[NV:MARKER_PLACEHOLDER] track={track.name} step={stepIndex} burstIdParam={burstId} markerId={existing.gameObject.GetInstanceID()} placeholder=True");
        }

        result = existing.gameObject;
        return true;
    }

    private bool TryAdoptMarker(
        (InstrumentTrack, int) key, RectTransform row, Rect rowRect,
        InstrumentTrack track, int stepIndex, bool shouldLight, bool lit, int burstId,
        out GameObject result)
    {
        result = null;
        var adopt = TryAdoptExistingAt(track, stepIndex, row);
        if (!adopt) return false;

        if (GameFlowManager.VerboseLogging) Debug.Log($"[NoteViz] Found note to adopt. This shouldn’t happen.");
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

        if (GameFlowManager.VerboseLogging) Debug.Log($"[NoteViz] ADOPT marker track={track.name} step={stepIndex} lit={lit} burst={burstId} go={adopt.gameObject.GetInstanceID()}");
        result = adopt.gameObject;
        return true;
    }

    private GameObject SpawnNewPersistentMarker(
        (InstrumentTrack, int) key, RectTransform row, Rect rowRect,
        InstrumentTrack track, int stepIndex, bool shouldLight, int burstId)
    {
        int totalSteps = Mathf.Max(1, track.GetTotalSteps());
        int binSize = Mathf.Max(1, track.BinSize());
        int leaderBinsForPlacement = GetLeaderBinsForPlacement(track, totalSteps, binSize);
        float xLocal = ComputeXLocalForTrack(rowRect, track, stepIndex, binSize, leaderBinsForPlacement);
        if (GameFlowManager.VerboseLogging) Debug.Log($"xLocal : {xLocal} for track {track.name} stepIndex {stepIndex} lit={shouldLight}");

        float bottomWorldY = GetBottomWorldY();
        float bottomLocalY = row.InverseTransformPoint(new Vector3(0f, bottomWorldY, 0f)).y;

        // Idempotent guard — something may have raced us
        if (noteMarkers.TryGetValue(key, out var appeared) && appeared)
        {
            UpdateMarkerXPreserveYIfAscending(rowRect, row, track, stepIndex, appeared);
            return appeared.gameObject;
        }

        GameObject marker = Instantiate(notePrefab, row, worldPositionStays: false);
        marker.transform.localPosition = new Vector3(xLocal, bottomLocalY, 0f);

        var newTag = marker.GetComponent<MarkerTag>() ?? marker.AddComponent<MarkerTag>();
        newTag.track = track;
        newTag.step = stepIndex;
        newTag.isPlaceholder = !shouldLight;
        if (burstId >= 0) newTag.burstId = burstId;

        noteMarkers[key] = marker.transform;
        if (GameFlowManager.VerboseLogging) Debug.Log($"[NV:MARKER_REGISTER] track={track.name} step={stepIndex} burstIdParam={burstId} markerId={marker.gameObject.GetInstanceID()} lit={shouldLight}");

        ApplyMarkerVisuals(marker, track, shouldLight);
        return marker;
    }

    private void ApplyMarkerVisuals(GameObject marker, InstrumentTrack track, bool isLit)
    {
        var vnm = marker.GetComponent<VisualNoteMarker>();
        var ml = marker.GetComponent<MarkerLight>() ?? marker.AddComponent<MarkerLight>();
        if (isLit)
        {
            if (vnm != null) vnm.Initialize(track.trackColor);
            ml.LightUp(track.trackColor);
        }
        else
        {
            if (vnm != null) vnm.SetWaitingParticles(track.trackColor);
            ml.SetGrey(track.trackColor);
        }
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
    /// <summary>
    /// Reset all active ascension countdowns as a new phrasing (called on chord-progression swap).
    /// Keeps markers visually in place but resets their loop countdown from the current position.
    /// </summary>
    public void ResetAscensionPhrasing()
    {
        ascensionDirector?.ResetPhrasing();
    }

    public void TriggerBurstAscend(InstrumentTrack track, int burstId, float seconds)
    {
        if (ascensionDirector == null) return;

        ascensionDirector.TriggerBurstAscend(
            track,
            burstId,
            seconds,
            GetMarkersForTrackAndBurst,
            ResolveAscendLoopsForTrack(track),
            GetCommittedStepForMarker
        );
    }

    private int ResolveAscendLoopsForTrack(InstrumentTrack track)
    {
        if (track == null) return -1;
        if (_gfm == null) _gfm = GameFlowManager.Instance;
        var motif = _gfm?.phaseTransitionManager?.currentMotif;
        if (motif?.roleNoteConfigs == null) return -1;
        foreach (var cfg in motif.roleNoteConfigs)
            if (cfg != null && cfg.ascendLoops > 0 && cfg.role == track.assignedRole)
                return cfg.ascendLoops;
        return -1; // fall back to NoteAscensionDirector's inspector default
    }

    /// <summary>
    /// Defensive cleanup: removes the marker at stepAbs for the given track if it is not
    /// currently in the persistent loop and is not mid-ascension. Used after a discard to
    /// ensure the authored step's marker is gone even if isPlaceholder or burstId was altered.
    /// </summary>
    public void RemoveOrphanMarkerAtStep(InstrumentTrack track, int stepAbs)
    {
        var key = (track, stepAbs);
        if (!noteMarkers.TryGetValue(key, out var tr) || tr == null) return;

        var tag = tr.GetComponent<MarkerTag>();
        if (tag != null && tag.isAscending) return; // leave ascending markers alone

        // Only remove if the step is not in the persistent loop (it was discarded, not committed).
        if (track != null && track.IsPersistentStepOccupied(stepAbs)) return;

        noteMarkers.Remove(key);
        Destroy(tr.gameObject);
    }

    /// <summary>
    /// Removes and destroys all placeholder markers for the given track and burst.
    /// Called on burst completion so authored-step placeholders that were never
    /// committed (collectables picked up and released elsewhere, or discarded) don't linger.
    /// </summary>
    public void RemoveAllPlaceholdersForBurst(InstrumentTrack track, int burstId)
    {
        var toRemove = new List<(InstrumentTrack, int)>();
        foreach (var (step, _, tag) in IterateMarkersForTrack(track,
            t => t.isPlaceholder && !t.isAscending && t.burstId == burstId))
        {
            toRemove.Add((track, step));
        }
        foreach (var key in toRemove)
        {
            if (noteMarkers.TryGetValue(key, out var tr) && tr != null)
                Destroy(tr.gameObject);
            noteMarkers.Remove(key);
        }
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

        // NEW: if untagged and not dict-owned, it's unmanaged "mystery" content.
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

            int leaderBinsBase;
            if (_forcedLeaderSteps >= 1)
                leaderBinsBase = Mathf.Max(1, Mathf.CeilToInt(_forcedLeaderSteps / (float)binSize));
            else
                leaderBinsBase = Mathf.Max(1, _ctrl.GetCommittedLeaderBins());

            int trackBins = Mathf.Max(1, Mathf.CeilToInt(totalSteps / (float)binSize));
            int leaderBinsForPlacement = Mathf.Max(leaderBinsBase, trackBins);

            float bottomWorldY = GetBottomWorldY();
            float bottomLocalY = row.InverseTransformPoint(new Vector3(0f, bottomWorldY, 0f)).y;

            var chosenByStep = ReconcileDuplicateMarkersInRow(row, track);
            RepositionAndPruneMarkers(row, track, rowRect, binSize, leaderBinsForPlacement, bottomLocalY, chosenByStep);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[RECOMPUTE] EXCEPTION track={track?.name ?? "NULL"} ex={ex}");
        }
    }

    // Pass 1: scan row children, pick the canonical marker per step using priority rules,
    // then force noteMarkers to point at the canonical transform.
    private Dictionary<int, Transform> ReconcileDuplicateMarkersInRow(RectTransform row, InstrumentTrack track)
    {
        var chosenByStep = new Dictionary<int, Transform>(64);
        var chosenTagByStep = new Dictionary<int, MarkerTag>(64);

        for (int i = 0; i < row.childCount; i++)
        {
            var child = row.GetChild(i);
            if (!child) continue;

            var tag = child.GetComponent<MarkerTag>();
            if (tag == null || tag.track != track) continue;

            int step = tag.step;
            if (step < 0) continue;

            if (!chosenByStep.TryGetValue(step, out var existingTf) || !existingTf)
            {
                chosenByStep[step] = child;
                chosenTagByStep[step] = tag;
                continue;
            }

            var existingTag = chosenTagByStep[step];

            // Priority: ascending > non-placeholder > higher burstId
            bool aAsc = tag.isAscending;
            bool bAsc = existingTag != null && existingTag.isAscending;
            bool aPH = tag.isPlaceholder;
            bool bPH = existingTag != null && existingTag.isPlaceholder;
            int aBid = tag.burstId;
            int bBid = existingTag != null ? existingTag.burstId : -999999;

            bool takeA = false;
            if (aAsc != bAsc) takeA = aAsc;
            else if (aPH != bPH) takeA = !aPH;
            else if (aBid != bBid) takeA = aBid > bBid;

            if (takeA)
            {
                chosenByStep[step] = child;
                chosenTagByStep[step] = tag;
            }
        }

        foreach (var kv in chosenByStep)
        {
            int step = kv.Key;
            var tf = kv.Value;
            if (!tf) continue;

            var dictKey = (track, step);
            if (noteMarkers.TryGetValue(dictKey, out var oldTf) && oldTf && oldTf != tf)
                Debug.LogWarning($"[RECOMPUTE] DICT_SWAP track={track.name} step={step} old={oldTf.gameObject.GetInstanceID()} new={tf.gameObject.GetInstanceID()}");

            noteMarkers[dictKey] = tf;
        }

        return chosenByStep;
    }

    // Passes 2+3: reposition canonical markers, then destroy non-canonical duplicates.
    private void RepositionAndPruneMarkers(
        RectTransform row, InstrumentTrack track, Rect rowRect,
        int binSize, int leaderBinsForPlacement, float bottomLocalY,
        Dictionary<int, Transform> chosenByStep)
    {
        var kvs = noteMarkers.ToArray();
        foreach (var kv in kvs)
        {
            var key = kv.Key;
            var tf = kv.Value;
            if (key.Item1 != track || !tf) continue;

            float xLocal = ComputeXLocalForTrack(rowRect, track, key.Item2, binSize, leaderBinsForPlacement);
            var lp = tf.localPosition;
            float yLocal = IsAscending(tf) ? lp.y : bottomLocalY;
            tf.localPosition = new Vector3(xLocal, yLocal, lp.z);
        }

        for (int i = row.childCount - 1; i >= 0; i--)
        {
            var child = row.GetChild(i);
            if (!child) continue;

            var tag = child.GetComponent<MarkerTag>();
            if (tag == null || tag.track != track) continue;

            int step = tag.step;
            if (step < 0) continue;
            if (!chosenByStep.TryGetValue(step, out var canonical) || !canonical) continue;
            if (child == canonical) continue;

            bool isAnimatingNow = _animatingSteps != null && _animatingSteps.Contains((track, step));
            if (isAnimatingNow)
            {
                Debug.LogWarning($"[RECOMPUTE] KEEP_DUP_DURING_ANIM track={track.name} step={step} dup={child.gameObject.GetInstanceID()} canonical={canonical.gameObject.GetInstanceID()}");
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
    private static bool IsAscending(Transform tf)
    {
        if (tf == null) return false;
        var tag = tf.GetComponent<MarkerTag>();
        return tag != null && tag.isAscending;
    }

}
