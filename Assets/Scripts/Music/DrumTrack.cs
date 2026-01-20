using UnityEngine;
using System.Collections.Generic;
using System.Collections;
using System.Linq;
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
    //public MineNodePrefabRegistry nodePrefabRegistry;
    public GameObject mineNodePrefab;
//    public MinedObjectPrefabRegistry minedObjectPrefabRegistry;
    public PhasePersonalityRegistry phasePersonalityRegistry; 
    public MusicalPhase? QueuedPhase;
    [Header("Play Area Mapping")] 
    [Tooltip("If true, GetPlayAreaWorld() is clamped to Dust Band (min/max Y). If false, grid uses full screen minus UI reserve.")] 
    [SerializeField] private bool clampPlayAreaToDustBand = false;    [Header("UI Safe Area (Viewport)")]
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
    public double leaderStartDspTime { get; private set; }
    public List<PhaseSnapshot> SessionPhases = new();
    public List <MineNode> activeMineNodes = new List<MineNode>();
    public bool isPhaseStarActive;
    public int currentStep;
    [Header("Dust Band Mapping (Viewport Y)")]
    [Range(0f, 1f)] [SerializeField] private float dustBandMinY = 0.00f; // bottom of screen
    [Range(0f, 1f)] [SerializeField] private float dustBandMaxY = 0.80f; // 80% up the screen
    [SerializeField] private float dustBandTopInsetWorld = 0f;           // optional extra inset in world units
    [Header("Grid Sizing (Pixel-driven)")] 
    [Tooltip("Reference screen width used to derive a default cell pixel size (e.g., 1920).")] 
    [SerializeField] private int referenceWidthPx = 1920;
    [Tooltip("Reference grid columns used with referenceWidthPx to derive cell pixel size (e.g., 36).")] 
    [SerializeField] private int referenceColumns = 36;
    [Tooltip("Bottom UI padding in pixels to exclude from the grid area (e.g., 160).")] 
    [SerializeField] private int uiBottomPaddingPx = 160;
    [Tooltip("If true, DrumTrack will resize SpawnGrid at runtime to fill the usable screen.")] 
    [SerializeField] private bool autoSizeSpawnGridToScreen = true;
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
    [Header("Play Area Mapping")]
    [SerializeField] private bool lockPlayAreaAfterInit = true;

    private PlayArea _lockedPlayArea;
    private bool _hasLockedPlayArea = false;

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

        // ---- Leader-loop transport (single source of truth) ----
        // startDspTime is the audio clip schedule anchor; leaderStartDspTime is the leader-loop transport anchor.
        // leaderStartDspTime may be rebased at boundaries when EffectiveLoopLengthSec changes (expand/collapse).
        if (leaderStartDspTime <= 0.0) 
            leaderStartDspTime = startDspTime;
        
        double effLen = Mathf.Max(0.0001f, EffectiveLoopLengthSec); // leader loop length (seconds)
        
        // Advance leader-loop boundaries monotonically using the transport anchor (no floor(elapsed/len) reinterpretation).
        // Guard against rare multi-boundary catchup (e.g., hitch) and handler-driven len changes.
        const int kMaxBoundaryCatchup = 4;
        int catchup = 0; 
        while (AudioSettings.dspTime - leaderStartDspTime >= effLen && catchup < kMaxBoundaryCatchup) {
            leaderStartDspTime += effLen; 
            completedLoops++; 
            _lastEffectiveLoopCount = completedLoops; // keep legacy field coherent for debugging
            // True "leader loop boundary"
            OnLoopBoundary?.Invoke();
            
            // Handlers (expand/collapse) can change EffectiveLoopLengthSec. Re-snapshot for the next cycle.
            effLen = Mathf.Max(0.0001f, EffectiveLoopLengthSec); 
            catchup++;
        }


        int leaderSteps = GetLeaderSteps();
        // Use the EFFECTIVE loop length so step indexing stays aligned with expanded bins.
        float elapsedTime  = (float)(AudioSettings.dspTime - leaderStartDspTime);
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
            // IMPORTANT:
            // OnLoopBoundary is already fired by the leader-loop catchup loop above.
            // Do not fire it a second time via LoopRoutines(), otherwise expansion commits
            // can desynchronize and appear to "never happen" or happen twice.
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
            initialClip = beatMoodLibrary.GetFirstClip(mood);
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
        leaderStartDspTime = dspStart;
        StartCoroutine(DeferredInit());
        StartCoroutine(InitializeDrumLoop());
        isPhaseStarActive = false;
    }
    private IEnumerator DeferredInit()
    {
        yield return null;          // one frame
        EnsureCachedRefs();
        AutoSizeSpawnGridIfEnabled();
        SyncTileWithScreen();
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

        MusicalPhase phase = GetCurrentPhaseSafe();

        _dust.CarveTemporaryCellFromVehicle(
            worldPos,
            phase,
            healDelaySeconds,
            resolveRadiusCells
        );
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
    public CompositeCollider2D GetDustCompositeCollider()
    {
        return _dust != null ? _dust.DustCompositeCollider : null;
    }

    public bool TryGetDustAt(Vector2Int cell, out CosmicDust dust)
    {
        dust = null;
        return _dust != null && _dust.TryGetDustAt(cell, out dust);
    }

    public int CarveTemporaryCellFromMineNode(
        Vector3 worldPos,
        MusicalPhase phase,
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

        var clip = profile.GetFirstLoopClip();
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


    public float ComputeBinFillIntensity(IReadOnlyList<InstrumentTrack> tracks)
    {
        if (tracks == null || tracks.Count == 0) return 0.25f;

        int binsPerTrack = Mathf.Max(1, _binCount);

        int activeTracks = 0;
        int filled = 0;

        for (int t = 0; t < tracks.Count; t++)
        {
            var tr = tracks[t];
            if (tr == null) continue;

            activeTracks++;

            for (int b = 0; b < binsPerTrack; b++)
                if (tr.IsBinFilled(b)) filled++;
        }

        if (activeTracks <= 0) return 0.25f;

        int totalBins = binsPerTrack * activeTracks;

        if (filled <= 0) return 1f / totalBins;
        return (float)filled / totalBins;
    }


    public void QueueBeatMoodFromBinFill(IReadOnlyList<InstrumentTrack> tracks)
    {
        float intensity = ComputeBinFillIntensity(tracks);

        // Map intensity -> mood using your existing threshold fields and queue slot.【turn69file13†L11-L35】
        // If you already have ApplyBeatIntensity(float), call that instead.
        if (intensity < lowIntensityThreshold)       QueuedBeatMood = lowIntensityMood;
        else if (intensity < mediumIntensityThreshold) QueuedBeatMood = mediumIntensityMood;
        else if (intensity < highIntensityThreshold)   QueuedBeatMood = highIntensityMood;
        else                                         QueuedBeatMood = extremeIntensityMood;
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