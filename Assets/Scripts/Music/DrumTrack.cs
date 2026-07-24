using System;
using UnityEngine;
using System.Collections.Generic;
using System.Collections;
using System.Linq;
// One completed playthrough of one authored chapter.
// Sealed at phase completion and added to GardenRecord.

public partial class DrumTrack : MonoBehaviour
{
    private int _boundarySerial = 0;
    public GameObject phaseStarPrefab;
    public GameObject mineNodePrefab;
    private float _pendingBpm;
    private int _pendingTotalSteps;
    private bool _pendingTimingValid;
    [SerializeField] private bool logBeatSeqGates = false;
    private int _motifSetSerial = 0;
    private float _lateBindMotifTimer = 0f;
    private const float kLateBindMotifInterval = 1.0f;
    private float _lastTotalSpentSample = -1f; // baseline sample of TOTAL spent tanks (cumulative)
    private double _lastApplyMotifDsp = -1.0;
    private string _lastApplyMotifId = "";
    [HideInInspector]
    public float drumLoopBPM = 120f;
    [HideInInspector]
    public int totalSteps = 16;
    public AudioSource drumAudioSource;
    [Header("Config")]
    [SerializeField] public DrumTrackConfig config;
    public float timingWindowSteps => config != null ? config.timingWindowSteps : 0.25f;
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
    public List<DiscoveryTrackNode> activeMineNodes = new List<DiscoveryTrackNode>();
    [HideInInspector]
    public int currentStep;

    [Tooltip("Optional RectTransform whose top world-Y overrides DrumTrackConfig.uiBottomPaddingPx for grid bottom — set to NoteVisualizer RT for pixel-perfect alignment with the physical boundary.")]
    [SerializeField] private RectTransform _playAreaBottomAnchor;

    public int completedLoops { get; private set; } = 0;
    private float _gridCheckTimer;
    private readonly float _gridCheckInterval = 10f;
    private float _clipLengthSec;
    private const float kMinLen = 1e-4f; // guard for zero/denorm lengths
    private bool HasValidClipLen => _clipLengthSec > kMinLen;

    private bool _started;
    private AudioClip _pendingDrumLoop;
    private GameFlowManager _gfm;
    private SpawnGrid _spawnGrid;
    private CosmicDustGenerator _dust;
    private InstrumentTrackController _trackController;
    [HideInInspector]
    public StarPool _starPool;
    private PhaseTransitionManager _phaseTransitionManager;

    private int _binIdx = -1;
    private int _binCount = 1; // 1 bin until ArmCohortsOnLoopBoundary / ResyncLeaderBinsNow sets it

    private MotifProfile _motif;
    private List<AudioClip> _entryLoops;
    private List<AudioClip> _intensityLoops;

    private int _entryLoopsRemaining;
    private bool _carryLatched; // one-way: set once any vehicle carries a collectable

    private AudioClip _currentDrumClip;

    public event System.Action OnLoopBoundary; // fire in LoopRoutines()
    public event System.Action<int, int> OnStepChanged; // (stepIndex, leaderSteps)

    private int _lastStepIdx = -1;
    private bool _driveFromEnergy;

    private float _lastIntensity01 = 0f; // for hysteresis
    private float _burnTier = 0f;        // ramp counter: steps up while burning, down when idle

    public float EffectiveLoopLengthSec => (_trackController != null) ? _trackController.GetEffectiveLoopLengthInSeconds() : _clipLengthSec;
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

    private bool _pendingDrumLoopArmed;
    private double _pendingDrumLoopDspStart;

    // Owns world↔grid coordinate mapping and spawn-grid delegation; fully decoupled from
    // DSP/motif state, mirroring how CosmicDustGenerator owns CosmicDustCellRegistry.
    private DrumTrackGridMapper _gridMapperBacking;
    private DrumTrackGridMapper _gridMapper => _gridMapperBacking ??= new DrumTrackGridMapper(
        () => config,
        () => _spawnGrid,
        () => _dust,
        () => _gfm,
        () => _playAreaBottomAnchor);

    public bool TryGetPlayAreaWorld(out PlayArea area) => _gridMapper.TryGetPlayAreaWorld(out area);
    public Vector2 GridToWorldPosition(Vector2Int cell) => _gridMapper.GridToWorldPosition(cell);
    public Vector2Int WorldToGridPosition(Vector3 worldPos) => _gridMapper.WorldToGridPosition(worldPos);
    public Vector2Int WrapGridCell(Vector2Int gp) => _gridMapper.WrapGridCell(gp);
    public float GetCellWorldSize() => _gridMapper.GetCellWorldSize();
    public Vector2Int CellOf(Vector3 world) => _gridMapper.CellOf(world);
    public void OccupySpawnCell(int x, int y, GridObjectType type) => _gridMapper.OccupySpawnCell(x, y, type);
    public int GetSpawnGridWidth() => _gridMapper.GetSpawnGridWidth();
    public int GetSpawnGridHeight() => _gridMapper.GetSpawnGridHeight();
    public bool HasDustAt(Vector2Int cell) => _gridMapper.HasDustAt(cell);
    public bool TryGetDustAt(Vector2Int cell, out CosmicDust dust) => _gridMapper.TryGetDustAt(cell, out dust);
    public bool IsSpawnCellAvailable(int x, int y) => _gridMapper.IsSpawnCellAvailable(x, y);
    public bool HasSpawnGrid() => _gridMapper.HasSpawnGrid();
    public void ResetSpawnCellBehavior(int x, int y) => _gridMapper.ResetSpawnCellBehavior(x, y);
    public void FreeSpawnCell(int x, int y) => _gridMapper.FreeSpawnCell(x, y);
    public Vector2Int GetRandomAvailableCell() => _gridMapper.GetRandomAvailableCell();
    public void RefreshPlayAreaLock() => _gridMapper.RefreshPlayAreaLock();
    public void SyncTileWithScreen() => _gridMapper.SyncTileWithScreen();

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
        int prev = _binCount;
        _binCount = Mathf.Max(1, bins);
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

        _gridMapper.AutoSizeSpawnGridIfEnabled();

        // Optional debug: grid to screen scale sanity
        if (_gfm != null && _spawnGrid != null && _dust != null)
        {
            float tile = _gridMapper.GetCellWorldSize();
            int w = _spawnGrid.gridWidth;
            float worldW = tile * (w - 1);
            float scrW = _gridMapper.GetScreenWorldWidth();
            if (GameFlowManager.VerboseLogging) Debug.Log($"[GridScale] tile={tile:F3}, worldWide(grid)={worldW:F3}, screenWide={scrW:F3}, ratio={worldW / Mathf.Max(0.0001f, scrW):F3}");
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
                if (GameFlowManager.VerboseLogging) Debug.Log($"[BOOT] DrumTrack.ManualStart: motif already applied by PTM ({_motif.motifId}); skipping ApplyMotif.");
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
        initialClip = ChooseEntryClip();

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
            if (GameFlowManager.VerboseLogging) Debug.Log($"[BOOT] Drum transport started (motif): clip={initialClip.name} dspStart={dspStart:F3} bpm={drumLoopBPM} steps={totalSteps}");
        }
        else
        {
            // We don't know bpm/steps in fallback mode; keep whatever inspector/default values were set.
            if (GameFlowManager.VerboseLogging) Debug.Log($"[BOOT] Drum transport started (fallback): clip={initialClip.name} dspStart={dspStart:F3} (PTM motif missing)");
        }
    }

    public void RequestPhaseStar(Vector2Int? cellHint = null)
    {
        if (_starPool != null)
        {
            if (GameFlowManager.VerboseLogging) Debug.Log("[SpawnGuard] StarPool already active; abort.");
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

        if (GameFlowManager.VerboseLogging) Debug.Log("[DrumTrack] StarPool created and initialized.");
    }

    public void RegisterMineNode(DiscoveryTrackNode obj)
    {
        if (!activeMineNodes.Contains(obj))
        {
            activeMineNodes.Add(obj);
        }
    }

    public void UnregisterMineNode(DiscoveryTrackNode obj)
    {
        activeMineNodes.Remove(obj);
    }
}
