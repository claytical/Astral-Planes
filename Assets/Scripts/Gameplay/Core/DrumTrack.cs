using UnityEngine;
using System.Collections.Generic;
using System.Collections;
using System.Linq;
using Gameplay.Mining;
using Random = UnityEngine.Random;

public class PhaseSnapshot
{
    public MusicalPhase Pattern;
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
    public MineNodePrefabRegistry nodePrefabRegistry;
    public MinedObjectPrefabRegistry minedObjectPrefabRegistry;
    public PhasePersonalityRegistry phasePersonalityRegistry; 
    public MusicalPhase? QueuedPhase;
    [Header("UI Safe Area (Viewport)")]
    [Range(0f, 0.5f)]
    [SerializeField] private float uiReserveBottomViewport = 0.14f; // reserve bottom 14% for UI

    [Range(0f, 0.5f)]
    [SerializeField] private float uiReserveTopViewport = 0.00f;    // optional top reserve

    [SerializeField] private float uiReserveBottomInsetWorld = 0f;  // optional fine-tune in world units
    [SerializeField] private float uiReserveTopInsetWorld = 0f;

    public float drumLoopBPM = 120f;
    public float gridPadding = 0f;
    public int totalSteps = 16;
    public float timingWindowSteps = 1f; // Can shrink to 0.5 or less as game progresses
    public AudioSource drumAudioSource;
    public double startDspTime;
    public List<PhaseSnapshot> SessionPhases = new();
    public List<MinedObject> activeMinedObjects = new List<MinedObject>();
    public List <MineNode> activeMineNodes = new List<MineNode>();
    public bool isPhaseStarActive;
    public int currentStep;
    [Header("Dust Band Mapping (Viewport Y)")]
    [Range(0f, 1f)] [SerializeField] private float dustBandMinY = 0.00f; // bottom of screen
    [Range(0f, 1f)] [SerializeField] private float dustBandMaxY = 0.80f; // 80% up the screen
    [SerializeField] private float dustBandTopInsetWorld = 0f;           // optional extra inset in world units

    public int completedLoops { get; private set; } = 0;
    private float _loopLengthInSeconds, _phaseStartTime;
    private float _gridCheckTimer;
    private readonly float _gridCheckInterval = 10f;
    private float _clipLengthSec;
    private const float kMinLen = 1e-4f; // guard for zero/denorm lengths
    private bool HasValidClipLen => _clipLengthSec > kMinLen;

    private bool _started;
    private int _lastLoopCount, _tLoop, _phaseCount;
    private AudioClip _pendingDrumLoop;
    private GameFlowManager _gfm;
    private SpawnGrid _spawnGrid;
    private CosmicDustGenerator _dust;
    private InstrumentTrackController _trackController;
    public PhaseStar _star;
    private PhaseTransitionManager _phaseTransitionManager;
    
    private float _cachedTileDiameterWorld = -1f;

    private int _binIdx = -1;
    private int _binCount = 4;                   // default; PhaseStar can override per-spawn
    
    public BeatMoodLibrary beatMoodLibrary;
    public BeatMood? QueuedBeatMood;
    private BeatMoodProfile _activeBeatProfile;
    private float _lastEffectiveLen = -1f;
    private int _lastEffectiveLoopCount = -1;
        
    public BeatMoodProfile ActiveBeatMoodProfile => _activeBeatProfile;
    [Header("Beat Mood Intensity Mapping")]
    [Tooltip("Intensity < lowThreshold uses lowIntensityMood.")]
    [Range(0f, 1f)] public float lowIntensityThreshold    = 0.25f;

    [Tooltip("Intensity < mediumThreshold uses mediumIntensityMood.")]
    [Range(0f, 1f)] public float mediumIntensityThreshold = 0.5f;

    [Tooltip("Intensity < highThreshold uses highIntensityMood.")]
    [Range(0f, 1f)] public float highIntensityThreshold   = 0.75f;

    [Tooltip("Beat mood used for very low intensity.")]
    public BeatMood lowIntensityMood;

    [Tooltip("Beat mood used for low–medium intensity.")]
    public BeatMood mediumIntensityMood;

    [Tooltip("Beat mood used for medium–high intensity.")]
    public BeatMood highIntensityMood;

    [Tooltip("Beat mood used for very high intensity.")]
    public BeatMood extremeIntensityMood;
    public event System.Action OnLoopBoundary; // fire in LoopRoutines()
    public event System.Action<MusicalPhase, PhaseStarBehaviorProfile> OnPhaseStarSpawned;
    public event System.Action<int,int> OnBinChanged; // (idx, binCount)
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

	/// <summary>
	/// Returns the world-space play area used to map SpawnGrid cells to world positions.
	/// The play area is the visible camera region, clipped so it does not overlap the NoteVisualizer.
	/// </summary>
	public PlayArea GetPlayAreaWorld()
	{
		TryGetPlayAreaWorld(out var area);
		return area;
	}

	/// <summary>
	/// True when we can reliably map spawn-grid coordinates to world space.
	/// Dust generation and any terrain work should wait for this to be true.
	/// </summary>
	public bool IsWorldMappingReady() => TryGetPlayAreaWorld(out _);

	private static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

	public bool TryGetPlayAreaWorld(out PlayArea area)
	{
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
        
// Convert viewport band (minY..maxY) into world Y limits, using camera viewport conversion.
        float vMin = Mathf.Clamp01(dustBandMinY);
        float vMax = Mathf.Clamp01(dustBandMaxY);
        if (vMax < vMin) { float t = vMin; vMin = vMax; vMax = t; }

// Use left edge x for conversion; only Y matters.
        Vector3 w0 = cam.ViewportToWorldPoint(new Vector3(0f, vMin, cam.nearClipPlane));
        Vector3 w1 = cam.ViewportToWorldPoint(new Vector3(0f, vMax, cam.nearClipPlane));

        bottom = Mathf.Max(bottom, Mathf.Min(w0.y, w1.y));
        top    = Mathf.Min(top,    Mathf.Max(w0.y, w1.y) - Mathf.Max(0f, dustBandTopInsetWorld));

		// Validate.
		if (!IsFinite(left) || !IsFinite(right) || !IsFinite(bottom) || !IsFinite(top)) return false;
		if (right <= left || top <= bottom) return false;

		area.left = left;
		area.right = right;
		area.bottom = bottom;
		area.top = top;
		return true;
	}
    public void SetBinCount(int bins)
    {
        // Always clamp the requested value to something sane.
        int requested = Mathf.Max(1, bins);

        int final = requested;

        // If we have a track controller, treat it as authority for bin count:
        // transport bins == placement bins == leader loopMultiplier.
        if (_trackController != null)
        {
            int leaderBins = Mathf.Max(1, _trackController.GetMaxLoopMultiplier());

            if (leaderBins != requested)
            {
                Debug.LogWarning(
                    $"[DrumTrack] SetBinCount request={requested}, " +
                    $"but leader loopMultiplier={leaderBins}. " +
                    $"Using leaderBins as transport bin count.");
            }

            final = leaderBins;
        }

        _binCount = final;
    }
    private void Update()
    {
        // 0) Manager may exist but not be ready (or still wiring scenes)
        
        if (_gfm == null || !_gfm.ReadyToPlay())
        {
            return;
        }

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
        double elapsed = AudioSettings.dspTime - startDspTime;

        double effLen = Mathf.Max(0.0001f, EffectiveLoopLengthSec); // leader loop length (e.g., 6s)
        int effectiveLoopCount = (int)System.Math.Floor(elapsed / effLen);

        if (effectiveLoopCount != _lastEffectiveLoopCount)
        {
            _lastEffectiveLoopCount = effectiveLoopCount;
            completedLoops = effectiveLoopCount;

            // This is now a true "leader loop boundary"
            OnLoopBoundary?.Invoke();
        }

        int leaderSteps = GetLeaderSteps();
        // Use the EFFECTIVE loop length so step indexing stays aligned with expanded bins.
        float elapsedTime  = (float)(AudioSettings.dspTime - startDspTime);
        float effectiveLen = EffectiveLoopLengthSec;

        float stepDuration = (leaderSteps > 0) ? (effectiveLen / leaderSteps) : 0f;
        if (stepDuration <= 0f || float.IsInfinity(stepDuration) || effectiveLen <= 0f)
            return;

        float tInLoop = elapsedTime % effectiveLen;
        int absoluteStep = Mathf.FloorToInt(tInLoop / stepDuration);
        currentStep = absoluteStep % leaderSteps;
        // 3) Loop/bins driven by EFFECTIVE loop length
        if (effectiveLen > kMinLen)
        {
            int   bins = Mathf.Max(1, _binCount);
            double dsp = AudioSettings.dspTime;
            double pos = (dsp - startDspTime) % effectiveLen;
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
            int extendedLoop = Mathf.FloorToInt(elapsedTime / effectiveLen);
            
            if (extendedLoop > _lastLoopCount)
            {
                _lastLoopCount = extendedLoop;
                int prev = _lastLoopCount;
                _boundarySerial++;

                bool effChanged = (_lastEffectiveLen > 0f && Mathf.Abs(effectiveLen - _lastEffectiveLen) > 0.001f);

                Debug.Log(
                    $"[BOUNDARY#{_boundarySerial}] extLoop={extendedLoop} last={_lastLoopCount} " +
                    $"elapsed={elapsedTime:F3} effLen={effectiveLen:F6} prevEff={_lastEffectiveLen:F6} effChanged={effChanged} " +
                    $"clipLen={_clipLengthSec:F6} leaderSteps={GetLeaderSteps()} totalSteps={totalSteps} " +
                    $"completedLoops={completedLoops} bins={bins} binIdx={_binIdx} startDsp={startDspTime:F3}"
                );

                LoopRoutines();
            }
            _lastEffectiveLen = effectiveLen;
        }

        // 4) Housekeeping
        if (activeMineNodes != null)
            activeMineNodes.RemoveAll(n => n == null);
    }
    public void ManualStart()
    {
        
        _gfm = GameFlowManager.Instance;
        if (_gfm != null)
        {
            _spawnGrid = _gfm.spawnGrid;
            _dust      = _gfm.dustGenerator;
            _trackController = _gfm.controller;
            _phaseTransitionManager = _gfm.phaseTransitionManager;
        }        
        if (drumAudioSource && drumAudioSource.clip) {
            _clipLengthSec = Mathf.Max(drumAudioSource.clip.length, 0f);
        }

        // 🔍 Debug grid scale vs screen
        
        
        if (_gfm != null && _spawnGrid != null && _dust != null)
        {
            float tile   = GetTileDiameterWorld();
            int   w      = _spawnGrid.gridWidth;
            float worldW = tile * (w - 1);
            float scrW   = GetScreenWorldWidth();

            Debug.Log($"[GridScale] tile={tile:F3}, worldWide(grid)={worldW:F3}, screenWide={scrW:F3}, ratio={worldW/scrW:F3}");
        }
        
        isPhaseStarActive = false;
        if (_started) return;
        _started = true;

        if (drumAudioSource == null)
        {
            Debug.LogError("DrumTrack: No AudioSource assigned!");
            return;
        }

        // --- A) START THE CURRENT PHASE CLIP *NOW* (no queued change) ---
        // Establish is the first phase; play its loop immediately so timing is correct.
        var boot = MusicalPhase.Establish;

        if (_phaseTransitionManager != null)
        {
            Debug.Log($"[BOOT] Handling phase {_phaseTransitionManager.currentPhase}"); 
            _phaseTransitionManager.HandlePhaseTransition(boot, "DrumTrack/ManualStart");
        }
        else
        {
            Debug.Log($"[BOOT] no phase transition manager found! {_phaseTransitionManager}");
        }
        var bootProfile = phasePersonalityRegistry != null ? phasePersonalityRegistry.Get(boot) : null;
        if (_dust != null && bootProfile != null)
        {
            _dust.ApplyProfile(bootProfile);
            SyncTileWithScreen();
        }

        AudioClip initialClip = null;
        if (_phaseTransitionManager != null &&
            _phaseTransitionManager.currentMotif != null &&
            beatMoodLibrary != null)  {
            var mood = _phaseTransitionManager.currentMotif.beatMood; 
            initialClip = beatMoodLibrary.GetRandomClip(mood);
            Debug.Log($"[BOOT] Using motif '{_phaseTransitionManager.currentMotif.motifId}' beat mood '{mood}' for initial drum loop: '{(initialClip != null ? initialClip.name : "null")}'.");
        }


        // Clear any queued/scheduled transitions; we're booting the clock cleanly.
        QueuedPhase      = null;
        _pendingDrumLoop = null;

        drumAudioSource.Stop();
        drumAudioSource.clip = initialClip;
        drumAudioSource.loop = true;
        _clipLengthSec       = Mathf.Max(initialClip.length, 0f);

        // Align to DSP so the transport is stable from frame 0.
        double dspStart = AudioSettings.dspTime + 0.05;
        drumAudioSource.PlayScheduled(dspStart);
        startDspTime = dspStart;
        StartCoroutine(DeferredInit());
        StartCoroutine(InitializeDrumLoop());
        isPhaseStarActive = false;
    }
    private IEnumerator DeferredInit()
    {
        yield return null;          // one frame
        EnsureCachedRefs();
        SyncTileWithScreen();
    }
    public void CarveTemporaryDiskFromCollectable(
        Vector3 worldPos,
        float radiusWorld,
        MusicalPhase phase,
        float holdSeconds)
    {
        if (_dust == null) return;
        _dust.CarveTemporaryDiskFromCollectable(worldPos, radiusWorld, phase, holdSeconds);
    }

    public void RequestPhaseStar(MusicalPhase phase, Vector2Int? cellHint = null)
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
    private sealed class OnDestroyRelay : MonoBehaviour  {
        public System.Action onDestroyed;
        private void OnDestroy() { try { onDestroyed?.Invoke(); } catch {} }
    }
    public void ScheduleBeatMoodAndLoopChange(BeatMood mood)
    {
        QueuedBeatMood = mood;

        if (beatMoodLibrary == null)
        {
            Debug.LogWarning($"[DrumTrack] No BeatMoodLibrary assigned; cannot schedule mood {mood}.");
            return;
        }

        var profile = beatMoodLibrary.GetProfile(mood);
        if (profile == null)
        {
            Debug.LogWarning($"[DrumTrack] No BeatMoodProfile found for mood {mood}.");
            return;
        }

        var clip = profile.GetRandomLoopClip();
        if (clip == null)
        {
            Debug.LogWarning($"[DrumTrack] BeatMoodProfile {profile.name} has no loop clips for mood {mood}.");
            return;
        }

        _activeBeatProfile = profile;

        // Apply BPM / loop length from the BeatMood profile.
        drumLoopBPM = profile.bpm;
        totalSteps  = profile.stepsPerLoop;

        ScheduleDrumLoopChange(clip);

    }
    /// <summary>
    /// Apply a normalized intensity value [0..1] to the drum system by
    /// selecting an appropriate BeatMood and scheduling a loop change.
    /// Call this from gameplay systems that compute "how hard the player is going".
    /// </summary>
    public void ApplyBeatIntensity(float intensity01)
    {
        if (beatMoodLibrary == null)
            return;

        // Clamp to [0,1]
        float x = Mathf.Clamp01(intensity01);

        // Ensure thresholds are ordered
        float tLow    = Mathf.Min(lowIntensityThreshold, mediumIntensityThreshold);
        float tMed    = Mathf.Clamp(mediumIntensityThreshold, tLow, highIntensityThreshold);
        float tHigh   = Mathf.Max(highIntensityThreshold, tMed);

        // Map scalar to one of the four moods
        BeatMood target;
        if (x < tLow)
            target = lowIntensityMood;
        else if (x < tMed)
            target = mediumIntensityMood;
        else if (x < tHigh)
            target = highIntensityMood;
        else
            target = extremeIntensityMood;

        // If we already have an active profile with this mood, do nothing.
        if (_activeBeatProfile != null && _activeBeatProfile.mood == target)
            return;

        ScheduleBeatMoodAndLoopChange(target);
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

    public bool TryFindPath(Vector2Int start, Vector2Int goal, List<Vector2Int> outPath)
    {
        outPath.Clear();
        if (start == goal) { outPath.Add(goal); return true; }

        int w = GetSpawnGridWidth(), h = GetSpawnGridHeight();
        bool InBounds(Vector2Int c) => (uint)c.x < (uint)w && (uint)c.y < (uint)h;
        var q = new Queue<Vector2Int>();
        var came = new Dictionary<Vector2Int, Vector2Int>();
        var seen = new HashSet<Vector2Int> { start };
        q.Enqueue(start);

        while (q.Count > 0)
        {
            var cur = q.Dequeue();
            foreach (var n in HexNeighbors(cur))
            {
                if (!InBounds(n) || seen.Contains(n)) continue;
                // 🚫 Dust/Node/etc. block; allow goal even if currently reserved (we'll stop at its center)
                if (!IsSpawnCellAvailable(n.x, n.y) && n != goal) continue;

                came[n] = cur;
                if (n == goal) {
                    // reconstruct
                    var p = n;
                    outPath.Add(p);
                    while (came.TryGetValue(p, out var prev)) { p = prev; outPath.Add(p); }
                    outPath.Reverse();
                    return true;
                }
                seen.Add(n);
                q.Enqueue(n);
            }
        }
        return false;
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
    public Vector2Int FarthestReachableCellInComponent(Vector2Int start)
    {
        int w = GetSpawnGridWidth(), h = GetSpawnGridHeight();
        bool InBounds(Vector2Int c) => (uint)c.x < (uint)w && (uint)c.y < (uint)h;

        var q = new Queue<Vector2Int>();
        var dist = new Dictionary<Vector2Int, int>();
        q.Enqueue(start);
        dist[start] = 0;

        Vector2Int far = start;
        int best = 0;

        while (q.Count > 0)
        {
            var cur = q.Dequeue();
            int d = dist[cur];
            if (d > best) { best = d; far = cur; }

            foreach (var nb in HexNeighbors(cur)) // your existing neighbor func
            {
                if (!InBounds(nb) || dist.ContainsKey(nb)) continue;
                if (!IsSpawnCellAvailable(nb.x, nb.y)) continue; // dust = walls
                dist[nb] = d + 1;
                q.Enqueue(nb);
            }
        }
        return far;
    }
    public Vector3 CellCenter(Vector2Int c) => GridToWorldPosition(c);
    public Vector2Int CellOf(Vector3 world) => WorldToGridPosition(world);
    public void RegisterMinedObject(MinedObject obj)
    {
        if (!activeMinedObjects.Contains(obj))
        {
            activeMinedObjects.Add(obj);
        }
    }
    public void RegisterMineNode(MineNode obj)
    {
        if (!activeMineNodes.Contains(obj))
        {
            activeMineNodes.Add(obj);
        }
    }
    public void UnregisterMinedObject(MinedObject obj)
    {
        Debug.Log($"Removing MinedObject {obj}. Total Count: {activeMinedObjects.Count}");
        activeMinedObjects.Remove(obj);
        Debug.Log($"Mined Object Count Now: {activeMinedObjects.Count}");
    }
    public void CarveTemporaryDiskFromMineNode(Vector3 worldPos, float appetiteMul, MusicalPhase phase, float healDelaySeconds)
    {
        if (_dust == null) return;
        _dust.CarveTemporaryDiskFromMineNode(worldPos, appetiteMul, phase, healDelaySeconds);
    }
    public void CarveTemporaryDiskFromMineNode(
        Vector3 worldPos,
        float appetite,
        MusicalPhase phase,
        float regrowDelaySeconds,
        Color imprintColor,
        Color imprintShadowColor,
        float imprintHardness01)
    {
        if (_dust == null) return;
        _dust.CarveTemporaryDiskFromMineNode(
            worldPos,
            appetite,
            phase,
            regrowDelaySeconds,
            imprintColor,
            imprintShadowColor,
            imprintHardness01
        );
    }


    public MusicalPhase GetCurrentPhaseSafe()
    {
        // DrumTrack is level authority; phaseTransitionManager is already cached here.
        if (_phaseTransitionManager != null) return _phaseTransitionManager.currentPhase;
        return MusicalPhase.Establish;
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
    public bool IsNavCellOpen(int x, int y)
    {
        if (_dust == null) return true;
        return !_dust.HasDustAt(new Vector2Int(x, y));
    }
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
            return Vector2.zero;

        float tile = GetCellWorldSize();
        float x = area.left   + (cell.x + 0.5f) * tile;
        float y = area.bottom + (cell.y + 0.5f) * tile;
        return new Vector2(x, y);
    }

    public float GetDustBandBottomCenterY()
    {
        float tile = GetCellWorldSize();
        PlayArea area = GetPlayAreaWorld();

        var gfm = _gfm; // after caching (see section B)
        var vizOk = (gfm != null && gfm.controller != null && gfm.controller.noteVisualizer != null);

        // If noteViz isn't ready yet, do NOT return 0. Use the camera play area.
        if (!vizOk)
            return area.bottom + tile * 0.5f;

        // Existing behavior when viz is present.
        return area.bottom + tile * 0.5f;
    }

    public Vector2Int WorldToGridPosition(Vector3 worldPos) {
        if (_gfm == null || _spawnGrid == null) return Vector2Int.zero;

        int w = _spawnGrid.gridWidth;
        int h = _spawnGrid.gridHeight;
        if (w <= 0 || h <= 0) return Vector2Int.zero;

        float tile = GetCellWorldSize();
        PlayArea area = GetPlayAreaWorld();

        float gx = (worldPos.x - area.left)   / tile - 0.5f;
        float gy = (worldPos.y - area.bottom) / tile - 0.5f;

        int ix = Mathf.RoundToInt(gx);
        int iy = Mathf.RoundToInt(gy);

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
    private void LoopRoutines()
    { 
        // --- ORDER TRACE (single place to understand boundary sequencing) ---
        int loopsBefore = completedLoops;
        bool gfmOk = (_gfm != null); 
        bool ctrlOk = (gfmOk && _trackController != null); 
        bool cifBefore = (ctrlOk && _trackController.AnyCollectablesInFlight()); 
        Debug.Log($"[LOOP/ENTER] loopsBefore={loopsBefore} isPhaseStarActive={isPhaseStarActive} starNull={(_star == null)} starGO={( _star != null ? (_star.gameObject != null).ToString() : "n/a")} cifBefore={cifBefore}");
        // NOTE: This currently runs BEFORE completedLoops++ and BEFORE OnLoopBoundary event.
        //       Logging here clarifies ordering relative to InstrumentTrack boundary commits.
        if (isPhaseStarActive && _star != null && _star.gameObject != null) {
            if (!cifBefore) {
                Debug.Log($"[LOOP/PHASESTAR] calling OnLoopBoundary_RearmIfNeeded at loopsBefore={loopsBefore} (cifBefore={cifBefore})");
                _star.OnLoopBoundary_RearmIfNeeded();
            }
            else {
                Debug.Log($"[LOOP/PHASESTAR] skipped OnLoopBoundary_RearmIfNeeded because cifBefore={cifBefore}"); 
            }
        }
        completedLoops++;
        Debug.Log($"[LOOP/INC] loopsAfter={completedLoops} (was {loopsBefore}) activeMineNodes(raw)={activeMineNodes.Count}");
        activeMineNodes.RemoveAll(n => n == null); 
        Debug.Log($"[LOOP/INVOKE] invoking OnLoopBoundary loops={completedLoops} activeMineNodes(clean)={activeMineNodes.Count}"); 
        OnLoopBoundary?.Invoke();
        // Post-invoke sampling: shows whether the boundary handlers spawned collectables this same tick.
        bool cifAfter = (ctrlOk && _trackController.AnyCollectablesInFlight()); 
        Debug.Log($"[LOOP/EXIT] loops={completedLoops} cifAfter={cifAfter} activeMineNodes={activeMineNodes.Count}");

    }
    private IEnumerable<Vector2Int> HexNeighbors(Vector2Int c)
    {
        bool even = (c.y & 1) == 0; // even-r offset layout
        if (even)
        {
            yield return new Vector2Int(c.x + 1, c.y);
            yield return new Vector2Int(c.x - 1, c.y);
            yield return new Vector2Int(c.x    , c.y + 1);
            yield return new Vector2Int(c.x - 1, c.y + 1);
            yield return new Vector2Int(c.x    , c.y - 1);
            yield return new Vector2Int(c.x - 1, c.y - 1);
        }
        else
        {
            yield return new Vector2Int(c.x + 1, c.y);
            yield return new Vector2Int(c.x - 1, c.y);
            yield return new Vector2Int(c.x + 1, c.y + 1);
            yield return new Vector2Int(c.x    , c.y + 1);
            yield return new Vector2Int(c.x + 1, c.y - 1);
            yield return new Vector2Int(c.x    , c.y - 1);
        }
    }
    private void ScheduleDrumLoopChange(AudioClip newLoop)
    {
        // Store the new loop clip.
        _pendingDrumLoop = newLoop;

        // Start waiting for the current loop to finish.

        StartCoroutine(WaitAndChangeDrumLoop());
    }
    private IEnumerator WaitAndChangeDrumLoop()
    {
        if (drumAudioSource == null || drumAudioSource.clip == null)
        {
            Debug.LogError("WaitAndChangeDrumLoop: drumAudioSource or its clip is null!");
            yield break;
        }

        float oldLen = drumAudioSource.clip ? drumAudioSource.clip.length : 0f;
        if (oldLen <= kMinLen)
        {
            Debug.LogError("WaitAndChangeDrumLoop: current clip length is zero/invalid.");
            yield break;
        }

        while (drumAudioSource.time < oldLen - 0.05f)
        {
            yield return null;
        }

        if (_pendingDrumLoop == null)
        {
            Debug.LogWarning("WaitAndChangeDrumLoop: No new drum loop was assigned!");
            yield break;
        }

        drumAudioSource.clip = _pendingDrumLoop;
        float newLen = drumAudioSource.clip ? drumAudioSource.clip.length : 0f;
        if (newLen <= kMinLen)
        {
            Debug.LogError("WaitAndChangeDrumLoop: new clip length is zero/invalid.");
            yield break;
        }

        _clipLengthSec = newLen;
        double dspNow  = AudioSettings.dspTime; // schedule on a multiple of the NEW clip length (validated above)
        double cycles  = dspNow / newLen;
        double nextStart = Mathf.CeilToInt((float)cycles) * newLen;
        drumAudioSource.PlayScheduled(nextStart);
        Debug.Log("🎶 New drum loop scheduled");

        if (startDspTime == 0)
        {
            startDspTime   = nextStart;
            _lastLoopCount = 0; // reset; we’re starting a new transport reference
        }

        _pendingDrumLoop = null;
        QueuedPhase      = null;

        // ✅ Slow maze growth instead of instant; use queued or current phase
        
        if (_gfm != null && _dust != null && !_gfm.GhostCycleInProgress)
        {
            var phaseForRegrowth = QueuedPhase ?? _phaseTransitionManager.currentPhase;
            Vector2Int centerCell = WorldToGridPosition(transform.position);

            // Inline what MineNodeProgressionManager.GetHollowRadiusForCurrentPhase() did:
            float radius = 0f;
            if (phasePersonalityRegistry != null)
            {
                var persona = phasePersonalityRegistry.Get(phaseForRegrowth);
                if (persona != null)
                    radius = Mathf.Max(0f, persona.starKeepClearRadiusCells);
            }

            var growthCells = _dust.CalculateMazeGrowth(centerCell, phaseForRegrowth, radius);
            _dust.BeginStaggeredMazeRegrowth(growthCells);
        }

        _lastLoopCount = Mathf.FloorToInt((float)(AudioSettings.dspTime - startDspTime) / _clipLengthSec);
    }
    private float GetTileDiameterWorld()
    {
        // If the play area changes, invalidate the cache.
        if (TryGetPlayAreaWorld(out var area))
        {
            if (!_hasLastPlayAreaForTileCache || !ApproximatelyEqual(area, _lastPlayAreaForTileCache))
            {
                _cachedTileDiameterWorld = 0f;
                _lastPlayAreaForTileCache = area;
                _hasLastPlayAreaForTileCache = true;
            }
        }
        else
        {
            // If we can't compute play area, fall back to cache if available.
            if (_cachedTileDiameterWorld > 0f)
                return _cachedTileDiameterWorld;
        }

        if (_cachedTileDiameterWorld > 0f)
            return _cachedTileDiameterWorld;

        // Compute from grid-to-world mapping (now UI-safe)
        var a = GridToWorldPosition_Legacy(new Vector2Int(0, 0));
        var b = GridToWorldPosition_Legacy(new Vector2Int(1, 0));
        float d = Vector2.Distance(a, b);

        _cachedTileDiameterWorld = (d > 0f) ? d : 1f;
        return _cachedTileDiameterWorld;
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

    private Vector2 GridToWorldPosition_Legacy(Vector2Int cell)
    {
        if (!TryGetPlayAreaWorld(out var area))
            return Vector2.zero;

        int w = Mathf.Max(1, GetSpawnGridWidth());
        int h = Mathf.Max(1, GetSpawnGridHeight());

        // Endpoints should land on the rect edges.
        float nx = (w <= 1) ? 0.5f : (cell.x / (float)(w - 1));
        float ny = (h <= 1) ? 0.5f : (cell.y / (float)(h - 1));

        float x = Mathf.Lerp(area.left, area.right, nx);
        float y = Mathf.Lerp(area.bottom, area.top, ny);
        return new Vector2(x, y);
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