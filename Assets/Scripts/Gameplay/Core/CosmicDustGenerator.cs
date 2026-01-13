using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.UI;
using Random = UnityEngine.Random;

public class CosmicDustGenerator : MonoBehaviour
{
    public GameObject dustPrefab;
    public Transform activeDustRoot;
    [Header("Visual Exclusion (UI)")]
    [Range(0f, 0.5f)]
    public float visualBottomExcludeViewport = 0.12f;  // bottom 12%: no dust particles
    [Range(0f, 0.2f)]
    public float visualFadeBandViewport = 0.05f;       // next 5%: fade in

    public int iterations = 3;
    [Header("Maze Cycle Control")]
    public MazeCycleMode cycleMode = MazeCycleMode.Progressive;
    public enum MazeCycleMode {
        ClassicLoopAligned, // clear then regrow each loop (old behavior)
        Progressive,        // add/remove features over time; DON'T run classic cycle each loop
        ExternalControlled  // only run when explicitly requested by game logic
    }
    
    Dictionary<Vector2Int, DustImprint> _imprints; 
    // Wear accumulation for imprint abrasion. 0..1 means “scuffed” → “dissipated”.
    private readonly Dictionary<Vector2Int, float> _wear01 = new Dictionary<Vector2Int, float>();
    [Header("Maze Collision Shape")]
    [Tooltip("World-units clearance inside each cell. 0 = watertight.")]
    public float cellClearanceWorld = 0f;

    [Tooltip("CompositeCollider2D edge radius as a fraction of cell size (0.0–0.25 is typical).")]
    [Range(0f, 0.25f)] public float edgeRadiusFrac = 0.08f;
    [Header("Imprint Abrasion (Non-Boost)")] 
    [SerializeField] private float abrasionWearPerSecondAtFullImpact = 0.65f;
    [SerializeField] private float abrasionDissipateFadeSeconds = 0.20f;
    [SerializeField] private float abrasionHardnessAtFullWear = 0.9f;
    struct DustImprint
    {
        public Color color;
        public Color shadowColor;
        public float healDelay;
        public float hardness01;
    }
    // Per owner footprint
    private readonly Dictionary<int, HashSet<Vector2Int>> _keepClearByOwner = new Dictionary<int, HashSet<Vector2Int>>();
    private readonly Dictionary<int, HashSet<Vector2Int>> _vehicleKeepClearCellsByOwner = new Dictionary<int, HashSet<Vector2Int>>();
// Per cell refcount (supports overlaps)
    private readonly Dictionary<Vector2Int, int> _keepClearRefCount = new Dictionary<Vector2Int, int>();
    private readonly HashSet<Vector2Int> _pendingFadeRemovals = new HashSet<Vector2Int>();

    // === Tint diffusion state (Option 2) ===
    private readonly Queue<Vector2Int> _tintDirtyQueue = new Queue<Vector2Int>();
    private readonly HashSet<Vector2Int> _tintDirtySet = new HashSet<Vector2Int>();
    private float _tintDiffusionAccum = 0f;
    private Coroutine _compositeRebuildCo;
    private bool _compositeDirty;
    [Tooltip("Debounce: minimum seconds between classic cycles.")]
    public float minClassicCycleInterval = 0.25f;
    [Header("Vehicle erosion (temporary)")]
    [SerializeField] private float vehicleErodeRadius = 1f;   // world units
    [SerializeField] private int vehicleErodePerTick = 2;       // max cells per call
    [Header("MineNode Erosion")]
    [SerializeField] private float mineNodeErodeRadius = 1f;

    [SerializeField] private int mineNodeErodePerTick = 10;
    [Header("Dust Visual Footprint")]
    [Range(0.8f, 1.6f)]
    public float vwhidustFootprintMul = 1.15f;

    [Header("Tile Sizing")]
    [SerializeField] private float tileDiameterWorld = 1f;          // cached from dustfab.hitbox
    HashSet<Vector2Int> _permanentlyClearedCells;
    Queue<Vector2Int>   _regrowQueue; // already implied by your staggered regrowth
    private readonly HashSet<Vector2Int> _starKeepClearCells = new HashSet<Vector2Int>();
    private readonly HashSet<Vector2Int> _starKeepClearPrev  = new HashSet<Vector2Int>();
    // === Vehicle legibility keep-clear (non-destructive; prevents regrowth around vehicles) ===
// We track per-vehicle keep-clear sets and maintain a refcount per cell so multiple vehicles can overlap safely.
    private readonly Dictionary<int, HashSet<Vector2Int>> _vehicleKeepClearById = new Dictionary<int, HashSet<Vector2Int>>();
    private readonly Dictionary<Vector2Int, int> _vehicleKeepClearRefCounts = new Dictionary<Vector2Int, int>();
    private readonly Queue<List<Vector2Int>> _pendingCorridorRegrowth = new Queue<List<Vector2Int>>();
    public List<GameObject> hexagons = new List<GameObject>();
    private readonly Dictionary<Vector2Int, GameObject> _hexMap = new(); // Position → Hex
    private Dictionary<Vector2Int, bool> _fillMap = new();
    private Dictionary<Vector2Int, Coroutine> _regrowthCoroutines = new();
// Absolute time (Time.time) when the cell should next attempt regrow.
    private readonly Dictionary<Vector2Int, float> _regrowAtTime = new();
    // Per-phase regrow pacing (lets PhaseStarBehaviorProfile drive maze closure without refactoring MusicalPhase).
    private readonly Dictionary<MusicalPhase, float> _regrowDelayMulByPhase = new();
    
    private Dictionary<int, List<Vector2Int>> _featureCells = new(); // featureId -> cells
    private Dictionary<Vector2Int, int> _cellToFeature = new();     // grid -> featureId
    [SerializeField] private Color _mazeTint = new Color(0.7f, 0.7f, 0.7f, .25f);
    private readonly List<Vector2Int> _tmpStarRelease = new List<Vector2Int>(512);
    private readonly List<Vector2Int> _tmpStarClaim   = new List<Vector2Int>(512);
    [Header("Tint Blending (Neighborhood)")]
    [Tooltip("When MineNodes imprint dust colors, blend the imprint toward nearby cell tints to avoid sharp grid seams.")]
    [Range(0, 3)] [SerializeField] private int imprintBlendRadius = 1;
    [Tooltip("0 = no blending (pure imprint). 1 = fully neighborhood average.")]
    [Range(0f, 1f)] [SerializeField] private float imprintNeighborWeight = 0.55f;

    [Header("Tint Diffusion (Option 2)")]
    [Tooltip("If enabled, recently modified dust cells will gradually blend toward their neighbors over time (local diffusion).")]
    [SerializeField] private bool enableTintDiffusion = true;

    [Tooltip("Seconds between diffusion passes. Lower = smoother but more CPU.")]
    [Range(0.02f, 0.5f)] [SerializeField] private float tintDiffusionInterval = 0.12f;

    [Tooltip("Maximum number of dirty cells processed per diffusion pass (prevents spikes).")]
    [Range(16, 2048)] [SerializeField] private int tintDiffusionMaxCellsPerTick = 256;

    [Tooltip("Neighborhood radius used for diffusion averaging (1 = 8-neighborhood).")]
    [Range(0, 3)] [SerializeField] private int tintDiffusionRadius = 1;

    [Tooltip("How strongly each pass nudges a cell toward the neighborhood average (0–1).")]
    [Range(0f, 1f)] [SerializeField] private float tintDiffusionStrength = 0.25f;

    [Tooltip("When a cell changes materially due to diffusion, enqueue its immediate neighbors to propagate blending.")]
    [SerializeField] private bool tintDiffusionPropagateOnChange = true;

    [Tooltip("Minimum per-channel delta required to apply a diffusion step (skips tiny changes).")]
    [Range(0f, 0.05f)] [SerializeField] private float tintDiffusionMinDelta = 0.0025f;

    [Tooltip("How far out to mark cells dirty when a tint-affecting event occurs (imprint, regrow, removal).")]
    [Range(0, 3)] [SerializeField] private int tintDirtyMarkRadius = 1;
    private Queue<int> _featureOrder = new();                       // FIFO for "oldest" removal
    private List<(Vector2Int grid, Vector3 pos)> _pendingSpawns = new();
    private float GetRegrowDelayMul(MusicalPhase phase)
        => _regrowDelayMulByPhase.TryGetValue(phase, out var m) ? m : 1f;

    private readonly HashSet<Vector2Int> _permanentClearCells = new HashSet<Vector2Int>();
    private Coroutine _spawnRoutine;
    private bool _isSpawningMaze = true, _cycleRunning = false;
    private float _commitCooldownUntil, _epochStartTime;
    private int _currentEpoch = 0, _nextFeatureId = 1, _progressiveLoop = 0;
    double _lastClassicCycleDSP; 
    private MusicalPhase _progressivePhase = MusicalPhase.Establish;
    private DrumTrack drums;
    private PhaseTransitionManager phaseTransitionManager;
    private int _mazeBuildId = 0;
    [SerializeField] private int flowTilesPerFrame = 128;   // tune to grid size (e.g., 32x18 grid ≈ 576 → 128–256 is good)
    private int _flowUpdateCursor = 0;
    private Vector2 _lastPhaseBias;
    private int _lastPulseId = -1;
    [SerializeField] private int poolPrewarm = 648;             // tune to your grid size
    [SerializeField] public Transform poolRoot;                 // optional, to keep Hierarchy tidy
    private readonly Stack<GameObject> _dustPool = new();
    [SerializeField] private float maxSpawnMillisPerFrame = 1.2f; // tune for target HW
    public event Action<Vector2Int?> OnMazeReady;

    [SerializeField] private float regrowCooldownSeconds = 3.0f;
    [SerializeField] public bool  progressiveMaze = true;
    [SerializeField] private int   maxFeatures = 3;                 // how many features we keep alive
    [SerializeField] private int   addPerLoop  = 1;                 // features to add each loop
    [SerializeField] private float featureSpawnBudgetFrac = 0.12f;  // of loop seconds, per new feature
    [SerializeField] private float featureFadeDurationFrac = 0.25f; // of loop seconds, when removing oldest
    [SerializeField] private float hexGrowInSeconds = 0.45f;        // visual “grow in” time per hex
    private Vector2[,] flowField;
    [SerializeField] private float globalTurbulence = 0.5f;
    private float hiveTimer;
    private Vector2[,] _flowField;
    [SerializeField] private float hiveShiftInterval = 4f;  // how often the hive changes its mind
    [SerializeField] private float hiveShiftBlend    = 0.40f; // how strongly to blend to the new direction
    [SerializeField] private float baseFlowStrength  = 0.20f; // world units per second
    [SerializeField] private float phaseFlowBias     = 0.15f; // extra per-phase bias
    private int _ffW = -1, _ffH = -1;
    [SerializeField] private float vehicleInfluenceRadius = 3.5f;
    [SerializeField] private float vehicleNudge = 0.25f; // world units per sec
    [SerializeField] private float starErodeRadius  = 1.2f;
    [SerializeField] private int   starErodePerTick = 6;   // how many tiles max per call
    private int _bulkTopologyDepth = 0;
    // Collectable “pressure pocket” hold: prevents regrow while a collectable is present nearby.
    private readonly Dictionary<Vector2Int, float> _collectableHoldUntil = new Dictionary<Vector2Int, float>();

    private bool IsBulkTopology => _bulkTopologyDepth > 0;
    private readonly HashSet<Vector2Int> _starClearCells = new HashSet<Vector2Int>();
    public float TileDiameterWorld
    {
        get => tileDiameterWorld;
        set => tileDiameterWorld = value;
    }
    [Header("Composite Collider (Manual Generation)")]
    [Tooltip("CompositeCollider2D that merges dust colliders. If null, we'll try activeDustRoot, then self.")]
    [SerializeField] private CompositeCollider2D compositeCollider;

    [Tooltip("Debounce interval (seconds) for composite rebuilds. 0 = once per frame.")]
    [SerializeField] private float compositeRebuildMinInterval = 0.0f;

    private float _nextCompositeRebuildAt;
    [Header("Dust Terrain Query")]
    [SerializeField] private LayerMask dustTerrainMask;
    [SerializeField] private float dustQueryRadiusWorld = 1.25f;
    [SerializeField] private int dustQueryMaxColliders = 16;
    [Header("Regrow Veto (Vehicle Overlap)")] 
    [Tooltip("If a vehicle overlaps a cell, regrow/spawn is deferred to prevent collider penetration shoves.")] 
    [SerializeField] private LayerMask vehicleMask;
    [SerializeField] private float regrowVetoRetryDelaySeconds = 0.5f;
    [Tooltip("Overlap box size as a fraction of cellWorldSize.")] 
    [Range(0.25f, 1.25f)] 
    [SerializeField] private float regrowVetoBoxMul = 0.85f; 
    [SerializeField] private int regrowVetoMaxHits = 8; 
    
    private Collider2D[] _vehicleVetoHits;    
    private readonly Collider2D[] _dustHits = new Collider2D[16];
    private int _compositeBatchDepth = 0;

    [SerializeField] private DustClaimManager dustClaims;


/// <summary>
/// True if *any* system should be prevented from spawning dust in this cell right now.
/// (Growth algorithm and regrow coroutine must both respect this.)
/// </summary>


/// <summary>
/// Claim a single grid cell to remain dust-free until now+holdSeconds.
/// priority: higher wins if multiple systems claim the same cell.
/// </summary>

    public void BeginCompositeBatch() => _compositeBatchDepth++;

    public void EndCompositeBatch()
    {
        _compositeBatchDepth = Mathf.Max(0, _compositeBatchDepth - 1);

        // If we just ended the outermost batch, schedule one rebuild.
        if (_compositeBatchDepth == 0 && _compositeDirty)
            ScheduleCompositeRebuild();
    }
    private void IncKeepClear(Vector2Int cell)
    {
        if (_keepClearRefCount.TryGetValue(cell, out int rc))
            _keepClearRefCount[cell] = rc + 1;
        else
            _keepClearRefCount[cell] = 1;
    }

    private void DecKeepClear(Vector2Int cell)
    {
        if (!_keepClearRefCount.TryGetValue(cell, out int rc)) return;
        rc--;
        if (rc <= 0) _keepClearRefCount.Remove(cell);
        else _keepClearRefCount[cell] = rc;
    }

    public bool TryResolveDustCellFromWorldPoint(Vector2 world, int searchRadiusCells, out Vector2Int resolved)
    {
        resolved = default;
        if (drums == null) return false;

        Vector2Int baseCell = drums.WorldToGridPosition(world);

        // Perfect hit
        if (_hexMap.ContainsKey(baseCell))
        {
            resolved = baseCell;
            return true;
        }

        // Spiral / ring search outward (Manhattan rings)
        for (int r = 1; r <= searchRadiusCells; r++)
        {
            // Top/Bottom rows of ring
            for (int dx = -r; dx <= r; dx++)
            {
                var c1 = new Vector2Int(baseCell.x + dx, baseCell.y + r);
                if (_hexMap.ContainsKey(c1)) { resolved = c1; return true; }

                var c2 = new Vector2Int(baseCell.x + dx, baseCell.y - r);
                if (_hexMap.ContainsKey(c2)) { resolved = c2; return true; }
            }

            // Left/Right cols of ring (skip corners already checked)
            for (int dy = -r + 1; dy <= r - 1; dy++)
            {
                var c1 = new Vector2Int(baseCell.x + r, baseCell.y + dy);
                if (_hexMap.ContainsKey(c1)) { resolved = c1; return true; }

                var c2 = new Vector2Int(baseCell.x - r, baseCell.y + dy);
                if (_hexMap.ContainsKey(c2)) { resolved = c2; return true; }
            }
        }

        return false;
    }
    public bool TryCarveDustAtWorldPoint(Vector2 world, int resolveRadiusCells, float fadeSeconds)
    {
        if (TryResolveDustCellFromWorldPoint(world, resolveRadiusCells, out var cell))
        {
            CarveDustAt(cell, fadeSeconds);
            return true;
        }
        return false;
    }

    private void MarkCompositeDirty()
    {
        EnsureCompositeRef();
        if (compositeCollider == null) return;

        _compositeDirty = true;

        // If batching, do not rebuild now.
        if (_compositeBatchDepth > 0)
            return;

        ScheduleCompositeRebuild();
    }

    private void EnsureCompositeRef()
    {
        if (compositeCollider != null) return;

        if (activeDustRoot != null)
            compositeCollider = activeDustRoot.GetComponent<CompositeCollider2D>();

        if (compositeCollider == null)
            compositeCollider = GetComponent<CompositeCollider2D>();
    }
    private void EnsureImprints()
    {
        if (_imprints == null)
            _imprints = new Dictionary<Vector2Int, DustImprint>();
    }

    public bool IsKeepClearCell(Vector2Int cell)
    {
        // Unified keep-clear refcount store
        if (_keepClearRefCount.TryGetValue(cell, out int c) && c > 0) return true;

        // Star keep-clear set
        if (_starKeepClearCells.Contains(cell)) return true;

        return false;
    }
    private void BeginBulkTopology()
    {
        _bulkTopologyDepth++;
    }

    private void EndBulkTopology()
    {
        _bulkTopologyDepth = Mathf.Max(0, _bulkTopologyDepth - 1);
    }
   
/// <summary>
    /// Keeps a non-destructive legibility pocket around a vehicle.
    /// Unlike erosion, this does NOT mark cells as cleared; it simply prevents regrowth/spawn in the pocket,
    /// and force-removes any existing dust tiles currently occupying those cells.
    /// Multiple vehicles are supported via per-cell reference counting.
    /// </summary>
public void SetVehicleKeepClear(
    int ownerId,
    Vector2Int centerCell,
    int radiusCells,
    MusicalPhase phase,
    bool forceRemoveExisting,
    float forceRemoveFadeSeconds = 0.20f)
{
    if (drums == null) return;

    int w = drums.GetSpawnGridWidth();
    int h = drums.GetSpawnGridHeight();
    if (w <= 0 || h <= 0) return;

    // Clamp center into bounds.
    centerCell.x = Mathf.Clamp(centerCell.x, 0, w - 1);
    centerCell.y = Mathf.Clamp(centerCell.y, 0, h - 1);

    // Get previous footprint for this owner.
    if (!_vehicleKeepClearCellsByOwner.TryGetValue(ownerId, out var prev))
    {
        prev = new HashSet<Vector2Int>();
        _vehicleKeepClearCellsByOwner[ownerId] = prev;
    }

    // Build new footprint.
    var next = new HashSet<Vector2Int>();
    int r = Mathf.Max(0, radiusCells);

    for (int dx = -r; dx <= r; dx++)
    for (int dy = -r; dy <= r; dy++)
    {
        if (dx * dx + dy * dy > r * r) continue;

        var cell = new Vector2Int(centerCell.x + dx, centerCell.y + dy);
        if (cell.x < 0 || cell.y < 0 || cell.x >= w || cell.y >= h) continue;

        next.Add(cell);
    }

    // Release cells no longer in footprint.
    foreach (var cell in prev)
    {
        if (next.Contains(cell)) continue;

        DecKeepClear(cell);

        // If nobody claims it anymore, allow regrow.
        if (!IsDustSpawnBlocked(cell) && !_permanentClearCells.Contains(cell) && !IsKeepClearCell(cell))
        {
            Debug.Log($"[VEHICLE] Requesting regrow for {cell}");

            RequestRegrowCellAt(cell, phase, refreshIfPending: true);
        }
        else
        {
            Debug.Log($"[VEHICLE] {cell} is claimed already");
        }
    }

    // Claim new cells.
    foreach (var cell in next)
    {
        if (prev.Contains(cell)) continue;

        IncKeepClear(cell);

        // CRITICAL SEMANTICS:
        // Only remove dust / touch fillMap when forceRemoveExisting is true (boosting).
        if (forceRemoveExisting)
        {
            if (_hexMap.TryGetValue(cell, out var go))
            {
                // If you support fade-out, call your fade path; otherwise remove to pool.
                RemoveActiveAt(cell, go, toPool: true);
            }

            _fillMap[cell] = false;
            if (_imprints != null) _imprints.Remove(cell);

            // Make sure regrowth is scheduled (it will be held off while claim exists).
            Debug.Log($"[VEHICLE] Scheduling regrow for {cell}");

            RequestRegrowCellAt(cell, phase, refreshIfPending: true);
        }
    }

    // Replace previous with next.
    prev.Clear();
    foreach (var c in next) prev.Add(c);
}
public void ReleaseStarKeepClear(MusicalPhase phase)
{
    _starKeepClearPrev.Clear();
    foreach (var c in _starKeepClearCells) _starKeepClearPrev.Add(c);
    _starKeepClearCells.Clear();

    foreach (var cell in _starKeepClearPrev)
    {
        DecKeepClear(cell);

        if (!IsDustSpawnBlocked(cell) && !_permanentClearCells.Contains(cell) && !IsKeepClearCell(cell))
        {
            RequestRegrowCellAt(cell, phase, refreshIfPending: true);
        }
    }
}
public void ReleaseVehicleKeepClear(int ownerId, MusicalPhase phase)
{
    if (!_vehicleKeepClearCellsByOwner.TryGetValue(ownerId, out var prev) || prev == null) return;

    foreach (var cell in prev)
    {
        DecKeepClear(cell);

        if (!IsDustSpawnBlocked(cell) && !_permanentClearCells.Contains(cell) && !IsKeepClearCell(cell))
        {
            RequestRegrowCellAt(cell, phase, refreshIfPending: true);
        }
    }

    prev.Clear();
    _vehicleKeepClearCellsByOwner.Remove(ownerId);
}
public void ClearVehicleKeepClear(int ownerId)
{
    if (!_vehicleKeepClearById.TryGetValue(ownerId, out var prevCells)) return;

    foreach (var cell in prevCells)
    {
        DecKeepClear(cell);
        RequestRegrowCellAt(cell, GetCurrentPhaseSafe(), delaySeconds: -1f, refreshIfPending: true);
    }

    _vehicleKeepClearById.Remove(ownerId);
}


    private void ScheduleCompositeRebuild()
    {
        EnsureCompositeRef();
        if (compositeCollider == null) return;

        // Throttle rebuild frequency (critical).
        if (compositeRebuildMinInterval > 0f && Time.unscaledTime < _nextCompositeRebuildAt)
            return;

        if (_compositeRebuildCo == null)
        {
            _compositeRebuildCo = StartCoroutine(RebuildCompositeEndOfFrame());
        }
    }

    private IEnumerator RebuildCompositeEndOfFrame()
    {
        // Coalesce multiple dirties into one rebuild.
        yield return new WaitForFixedUpdate();

        try
        {
            EnsureCompositeRef();
            if (compositeCollider == null) yield break;

            // If we are in a bulk/batch mode, let EndBulkTopology schedule the next rebuild.
            if (_bulkTopologyDepth > 0) yield break; // use your actual bulk flag/depth
            // Rebuild now
            compositeCollider.GenerateGeometry();

            _compositeDirty = false;

            _nextCompositeRebuildAt = (compositeRebuildMinInterval > 0f)
                ? (Time.unscaledTime + compositeRebuildMinInterval)
                : Time.unscaledTime;
        }
        finally
        {
            _compositeRebuildCo = null;
        }
    }

    private void Start()
    {
        if (_vehicleVetoHits == null || _vehicleVetoHits.Length != regrowVetoMaxHits) 
            _vehicleVetoHits = new Collider2D[Mathf.Max(1, regrowVetoMaxHits)];
        // In some scenes the generator may be instantiated before the GameFlowManager
        // is fully ready. We do a best-effort bind here, and also lazily re-bind elsewhere.
        EnsureImprints();
        TryEnsureRefs();
        TryEnsureFlowField();
        PrewarmPool();
    }
    private bool IsVehicleOverlappingCellWorld(Vector3 cellWorld, float cellWorldSize)
    {
        Vector2 size = Vector2.one * Mathf.Max(0.001f, cellWorldSize * regrowVetoBoxMul);

        // NOTE: even if vehicleMask is broad/misconfigured, we only veto if a Vehicle is present.
        int hits = Physics2D.OverlapBoxNonAlloc(cellWorld, size, 0f, _vehicleVetoHits, vehicleMask);
        if (hits <= 0) return false;

        for (int i = 0; i < hits; i++)
        {
            var col = _vehicleVetoHits[i];
            if (col == null) continue;

            if (col.GetComponentInParent<Vehicle>() != null)
                return true;
        }

        return false;
    }
    private void TryEnsureRefs()
    {
        if (drums != null && phaseTransitionManager != null) return;

        var gfm = GameFlowManager.Instance;
        if (gfm == null) return;

        if (drums == null) drums = gfm.activeDrumTrack;
        if (phaseTransitionManager == null) phaseTransitionManager = gfm.phaseTransitionManager;
    }

    public void ManualStart()
    {
        var gfm = GameFlowManager.Instance;
        drums = gfm.activeDrumTrack;
        phaseTransitionManager = gfm.phaseTransitionManager;
        if (dustClaims == null) dustClaims = FindObjectOfType<DustClaimManager>();
        TryEnsureFlowField();
        PrewarmPool();
        
    }
    private bool IsDustSpawnBlocked(Vector2Int cell)
    {
        return dustClaims != null && dustClaims.IsBlocked(cell);
    }
    void Update()
    {
        // Flow-field: incremental update each frame (prevents periodic spikes)
        TryEnsureRefs();
        // Tint diffusion: keep visual seams soft around recent changes.
        ProcessTintDiffusion(Time.deltaTime);

        if(drums == null) return;
        IncrementalFlowFieldUpdate();

        // Existing hive "intent" shift timer just changes the target bias occasionally
        hiveTimer += Time.deltaTime;
        if (hiveTimer >= hiveShiftInterval)
        {
            hiveTimer = 0f;
            _lastPhaseBias = ComputePhaseBias(GetCurrentPhaseSafe());
        }
    }
    private void PrewarmPool()
    {
        if (!dustPrefab) return;
        for (int i = 0; i < poolPrewarm; i++)
        {
            
            var go = Instantiate(dustPrefab, new Vector3(9999,9999,0), Quaternion.identity, poolRoot);
            if (i == 0)
            {
                var dust = go.GetComponent<CosmicDust>();
                if (dust != null)
                {
                    float r = dust.GetWorldRadius();
                    if (r > 0f)
                        tileDiameterWorld = Mathf.Max(0.001f, drums.GetCellWorldSize());
                }
            }
            go.SetActive(false);
            _dustPool.Push(go);
        }
    }
    
    public IEnumerator GenerateMazeForPhaseWithPaths(
        MusicalPhase phase,
        Vector2Int starCell,
        IReadOnlyList<Vector2Int> vehicleCells,
        float totalSpawnDuration = 1.0f)
    {
        if (drums == null)
        {
            Debug.LogError("[MAZE] No DrumTrack available; cannot build maze.");
            yield break;
        }

        Debug.Log($"[MAZE] GenerateMazeForPhaseWithPaths START phase={phase} starCell={starCell} vehicleCount={(vehicleCells != null ? vehicleCells.Count : 0)} permCount(before grid wait)={_permanentClearCells.Count}");

        Debug.Log("[MAZE] Waiting for Spawn Grid");
        yield return new WaitUntil(() =>
            drums.HasSpawnGrid() &&
            drums.GetSpawnGridWidth()  > 0 &&
            drums.GetSpawnGridHeight() > 0 &&
            Camera.main != null);
        Debug.Log("[MAZE] Spawn grid available.");

        // 1) Clear any existing dust
        Debug.Log($"[MAZE] ClearMaze() called from GenerateMazeForPhaseWithPaths; permCount BEFORE ClearMaze={_permanentClearCells.Count}");
        ClearMaze();
        Debug.Log($"[MAZE] ClearMaze() finished; permCount AFTER ClearMaze={_permanentClearCells.Count}");

        int w = drums.GetSpawnGridWidth();
        int h = drums.GetSpawnGridHeight();
        Debug.Log($"[MAZE] Maze grid size: {w}x{h}");

        // Build a mask of reserved cells (star + vehicles)
        var reserved = new HashSet<Vector2Int> { starCell };
        _permanentClearCells.Add(starCell);

        if (vehicleCells != null)
        {
            for (int i = 0; i < vehicleCells.Count; i++)
            {
                var v = vehicleCells[i];
                reserved.Add(v);
                _permanentClearCells.Add(v);
                Debug.Log($"[MAZE] Mark vehicleCell[{i}] permanent: {v} permCount={_permanentClearCells.Count}");
            }
        }

        var cellsToFill = new List<(Vector2Int grid, Vector3 pos)>();

        for (int x = 0; x < w; x++)
        {
            for (int y = 0; y < h; y++)
            {
                Vector2Int g = new Vector2Int(x, y);
                if (!reserved.Contains(g))
                {
                    Vector3 world = drums.GridToWorldPosition(g);
                    cellsToFill.Add((g, world));
                }
            }
        }

        Debug.Log($"[MAZE] cellsToFill count={cellsToFill.Count}");

        float spawnDuration = Mathf.Clamp(totalSpawnDuration, 0.05f, 3.0f);
        Debug.Log($"[MAZE] StaggeredGrowthFitDuration with spawnDuration={spawnDuration}");
        yield return StartCoroutine(StaggeredGrowthFitDuration(cellsToFill, spawnDuration));

        // 2) Carve generous holes around the star and each vehicle
        const int starHoleRadiusCells    = 3; // was 1 — make the star sit in a clearly open pocket
        const int vehicleHoleRadiusCells = 2; // local pocket around each ship

        // Star pocket
        
        CarvePermanentDisk(starCell, starHoleRadiusCells);
        BuildStarPocketSet(starCell, starHoleRadiusCells);
        // Vehicle pockets + corridors
        if (vehicleCells != null)
        {
            foreach (var vehCell in vehicleCells)
            {
                // Local clearing so the ship is never visually "in" the dust
                CarvePermanentDisk(vehCell, vehicleHoleRadiusCells);

                // 3) Carve a corridor from this vehicle to the PhaseStar
                Debug.Log($"[MAZE] GenerateMazeForPhaseWithPaths carving tunnel from vehicle {vehCell} to star {starCell}");
                CarveProgressiveTunnelToStar(vehCell, starCell); 
            }
        }

        Debug.Log(
            $"[MNDBG] MazeReady: phase={phase}, hexCount={_hexMap.Count}, permCount={_permanentClearCells.Count}"
        );
        OnMazeReady?.Invoke(starCell);
    }
    private void BuildStarPocketSet(Vector2Int starCell, int radiusCells)
    {
        _starClearCells.Clear();

        if (drums == null) return;
        int w = drums.GetSpawnGridWidth();
        int h = drums.GetSpawnGridHeight();

        for (int dx = -radiusCells; dx <= radiusCells; dx++)
        {
            for (int dy = -radiusCells; dy <= radiusCells; dy++)
            {
                var gp = new Vector2Int(starCell.x + dx, starCell.y + dy);

                if ((uint)gp.x >= (uint)w || (uint)gp.y >= (uint)h)
                    continue;

                if (dx * dx + dy * dy > radiusCells * radiusCells)
                    continue;

                _starClearCells.Add(gp);
            }
        }
    }
    public bool IsEffectivelyOpenCell(Vector2Int gp)
    {
        // Anything “never dust”
        if (_permanentClearCells.Contains(gp)) return true;
        if (IsKeepClearCell(gp)) return true; // you already have this privately in the file

        // Temporary open pockets for collectables (hold gate)
        if (_collectableHoldUntil != null && _collectableHoldUntil.TryGetValue(gp, out float until))
        {
            if (Time.time < until) return true;

            // Expired: clean up so we don’t accumulate dead entries
            _collectableHoldUntil.Remove(gp);
        }

        return false;
    }

    public bool IsEffectivelyDustCell(Vector2Int gp)
    {
        // If it must be treated as open, it’s not dust (even if a dust GO exists due to timing)
        if (IsEffectivelyOpenCell(gp)) return false;

        // Otherwise, it’s dust only if there’s an active dust cell in the map
        return _hexMap != null && _hexMap.ContainsKey(gp);
    }
    public bool HasDustAt(Vector2Int gridPos)
    {
        return _hexMap.ContainsKey(gridPos);
    }

    /// <summary>
    /// Returns a normalized 0..1 "dust density" at the given world position.
    /// For now this is binary: 1 if a dust hex exists in the cell, otherwise 0.
    /// </summary>
    public float SampleDensity01(Vector3 worldPos)
    {
        if (drums == null) return 0f;

        Vector2Int cell = drums.WorldToGridPosition(worldPos);
        return HasDustAt(cell) ? 1f : 0f;
    }
    public void SetStarKeepClearWorld(Vector2 centerWorld, float radiusWorld, MusicalPhase phase)
    {
        if (drums == null) return;

        Vector2Int center = drums.WorldToGridPosition(centerWorld);
        float cellWorld = Mathf.Max(0.001f, drums.GetCellWorldSize());
        int radiusCells = Mathf.CeilToInt(radiusWorld / cellWorld);

        // Star pocket updates over time; replace the previous set for this phase.
        SetStarKeepClear(center, radiusCells, phase, forceRemoveExisting: true);
    }
    /// <summary>
    /// Schedule regrowth back into the same cell unless it becomes permanently cleared or blocked.
    /// This is your authoritative "TemporaryClear" state.
    /// </summary>
    private void RequestRegrowCellAt(
        Vector2Int gridPos,
        MusicalPhase phase,
        float delaySeconds = -1f,
        bool refreshIfPending = false,
        bool clearImprintOnRefresh = false)
    {
        if (!IsInBounds(gridPos)) { 

            if (_regrowthCoroutines != null && _regrowthCoroutines.TryGetValue(gridPos, out var pending))
            { 
                if (pending != null) StopCoroutine(pending);
                _regrowthCoroutines.Remove(gridPos);

            } 
            return;
        }
        
        bool shouldSchedule = !_permanentClearCells.Contains(gridPos);
        
        // If dust already exists here, no need to schedule.
        if (shouldSchedule && _hexMap != null && _hexMap.ContainsKey(gridPos))
            shouldSchedule = false;
        

        if (!shouldSchedule)
            return;

        // Compute delay (phase default unless explicitly overridden).
        float delay = delaySeconds >= 0f ? delaySeconds : phase switch
        {
            MusicalPhase.Establish  => 16f,
            MusicalPhase.Evolve     => 12f,
            MusicalPhase.Intensify  => 8f,
            MusicalPhase.Release    => 32f,
            MusicalPhase.Wildcard   => 16f,
            MusicalPhase.Pop        => 24f,
            _ => 32f
        };

        delay *= GetRegrowDelayMul(phase);
       
        _regrowthCoroutines[gridPos] = StartCoroutine(RegrowCellAfterDelay(gridPos, delay));
    }

    public void CarveTemporaryDiskFromMineNode(
        Vector3 centerWorld,
        float appetite,
        MusicalPhase phase,
        float regrowDelaySeconds,
        Color imprintColor,
        Color imprintShadowColor,
        float imprintHardness01)

    {
        if (drums == null) return;
        EnsureImprints();
        float cellSize = Mathf.Max(0.001f, drums.GetCellWorldSize());
        float rWorld   = Mathf.Max(0.2f, mineNodeErodeRadius * Mathf.Max(0.1f, appetite));
        int   rCells   = Mathf.CeilToInt(rWorld / cellSize);
        int w = drums.GetSpawnGridWidth();
        int h = drums.GetSpawnGridHeight();
        Vector2Int c = drums.WorldToGridPosition(centerWorld);
        int removed = 0;
        int budget  = Mathf.RoundToInt(mineNodeErodePerTick * Mathf.Clamp(appetite, 0.4f, 2f));

        float hardness01 = Mathf.Clamp01(imprintHardness01);

        for (int gx = c.x - rCells; gx <= c.x + rCells; gx++)
        {
            for (int gy = c.y - rCells; gy <= c.y + rCells; gy++)
            {
                if (gx < 0 || gy < 0 || gx >= w || gy >= h) continue;
                Vector2Int gp = new Vector2Int(gx, gy);

                // Never touch permanent-clear cells.
                if (_permanentClearCells.Contains(gp)) continue;

                // Clear if present.
                if (_hexMap.ContainsKey(gp))
                {
                    DespawnDustAt(gp);
                    removed++;
                }

                // Record/refresh MineNode imprint for regrowth semantics.
                // Blend the imprint toward its neighborhood so MineNode color marks don't create hard seams.
                // (This also sets us up for optional diffusion later by ensuring the initial condition is already smooth.)
                Color blendedImprint = BlendImprintWithNeighbors(gp, imprintColor, imprintBlendRadius, imprintNeighborWeight);
                _imprints[gp] = new DustImprint
                {
                    color = blendedImprint,
                    shadowColor =  imprintShadowColor,
                    healDelay = regrowDelaySeconds,
                    hardness01 = hardness01
                };

                // Diffusion prep: this cell (and neighbors) will be nudged toward local averages over time.
                MarkTintDirty(gp, tintDirtyMarkRadius);

                // Always refresh the regrow timer (even if cell is already empty).
                // This is what allows a stationary node to keep a pocket/corridor open.
                RequestRegrowCellAt(gp, phase, regrowDelaySeconds, refreshIfPending: true);

                if (removed >= budget) return;
            }
        }
    }

    public void CarveTemporaryDiskFromMineNode(Vector3 centerWorld, float appetite, MusicalPhase phase, float regrowDelaySeconds) { 
        if (drums == null) return;
        float cellSize = Mathf.Max(0.001f, drums.GetCellWorldSize()); 
        float rWorld   = Mathf.Max(0.2f, mineNodeErodeRadius * Mathf.Max(0.1f, appetite)); 
        int   rCells   = Mathf.CeilToInt(rWorld / cellSize);
        int w = drums.GetSpawnGridWidth(); 
        int h = drums.GetSpawnGridHeight();
        Vector2Int c = drums.WorldToGridPosition(centerWorld);
        int removed = 0; 
        int budget  = Mathf.RoundToInt(mineNodeErodePerTick * Mathf.Clamp(appetite, 0.4f, 2f));
        
        for (int gx = c.x - rCells; gx <= c.x + rCells; gx++) {
            for (int gy = c.y - rCells; gy <= c.y + rCells; gy++) {
                if (gx < 0 || gy < 0 || gx >= w || gy >= h) continue;
                Vector2Int gp = new Vector2Int(gx, gy);
                // Never touch permanent-clear cells.
                if (_permanentClearCells.Contains(gp)) continue;
                // Clear if present.
                if (_hexMap.ContainsKey(gp)) { 
                    DespawnDustAt(gp); 
                    removed++;
                }
                // Always refresh the regrow timer (even if cell is already empty).
                // // This is what allows a stationary node to keep a pocket/corridor open.
                RequestRegrowCellAt(gp, phase, regrowDelaySeconds, refreshIfPending: true);
                if (removed >= budget) return; 
            }
        }
    }
private IEnumerator RegrowCellAfterDelay(Vector2Int gridPos, float initialDelaySeconds)
{
    if (!IsInBounds(gridPos)) { 
        _regrowthCoroutines.Remove(gridPos); 
        yield break;
    }
    float delaySeconds = Mathf.Max(0.0f, initialDelaySeconds);

    while (true)
    {
        if (!IsInBounds(gridPos)) { 
            _regrowthCoroutines.Remove(gridPos); 
            yield break;
        }
        // This log should now reflect the true wait each attempt.
        yield return new WaitForSeconds(delaySeconds);

        // Permanent veto: end the pipeline.
        if (_permanentClearCells.Contains(gridPos))
        {
            _regrowthCoroutines.Remove(gridPos);
            yield break;
        }

        // If already exists, we're done.
        if (_hexMap.ContainsKey(gridPos))
        {
            _regrowthCoroutines.Remove(gridPos);
            yield break;
        }

// Keep-clear (PhaseStar pocket / vehicle pocket / etc.)
        if (IsKeepClearCell(gridPos))
        {
            // Wait until ALL keep-clear sources release (vehicle refcount OR star set).
            yield return new WaitUntil(() =>
                !IsKeepClearCell(gridPos) ||
                _permanentClearCells.Contains(gridPos) ||
                !IsInBounds(gridPos));

            if (!IsInBounds(gridPos) || _permanentClearCells.Contains(gridPos))
            {
                _regrowthCoroutines.Remove(gridPos);
                yield break;
            }

            delaySeconds = 0f;
            continue;
        }


        // If you have other temporary vetoes, keep them as "continue" not "yield break".
        if (IsDustSpawnBlocked(gridPos))
        {
            delaySeconds = Mathf.Max(0.05f, regrowVetoRetryDelaySeconds);
            continue;
        }

        // Spawn-grid occupancy (MineNodes etc.) — retry.
        if (!drums.IsSpawnCellAvailable(gridPos.x, gridPos.y))
        {
            delaySeconds = Mathf.Max(0.05f, regrowVetoRetryDelaySeconds);
            continue;
        }

        Vector3 world = drums.GridToWorldPosition(gridPos);
        float cellWorldSize = Mathf.Max(0.001f, drums.GetCellWorldSize());

        // Vehicle overlap — retry.
        if (IsVehicleOverlappingCellWorld(world, cellWorldSize))
        {
            delaySeconds = Mathf.Max(0.05f, regrowVetoRetryDelaySeconds);
            continue;
        }

        // Collectable occupancy — retry.
        if (!Collectable.IsCellFreeStatic(gridPos))
        {
            delaySeconds = Mathf.Max(0.05f, regrowVetoRetryDelaySeconds);
            continue;
        }

        // --- Spawn dust (success path) ---
        var go = GetDustFromPool();
        go.transform.SetPositionAndRotation(world, Quaternion.identity);

        _hexMap[gridPos] = go;
        hexagons.Add(go);

        if (go.TryGetComponent<CosmicDust>(out var dust))
        {
            dust.ResetVisualToBase();
        }

        ScheduleCompositeRebuild();

        // SUCCESS: clear bookkeeping and exit.
        _regrowthCoroutines.Remove(gridPos);
        yield break;
    }
}
private bool IsInBounds(Vector2Int gp) { 
    if (drums == null) return false; 
    int w = drums.GetSpawnGridWidth(); 
    int h = drums.GetSpawnGridHeight(); 
    if (w <= 0 || h <= 0) return false; 
    return gp.x >= 0 && gp.y >= 0 && gp.x < w && gp.y < h;
}
    public void ReleaseCollectableHoldAt(Vector2Int gridPos, MusicalPhase phase, float regrowDelaySeconds = 0.15f)
    {
        if (_collectableHoldUntil.ContainsKey(gridPos))
            _collectableHoldUntil.Remove(gridPos);

        // Ask for a regrow soon. This respects permanent/keep-clear rules internally.
        RequestRegrowCellAt(
            gridPos,
            phase,
            delaySeconds: Mathf.Max(0.01f, regrowDelaySeconds),
            refreshIfPending: true,
            clearImprintOnRefresh: false
        );
    }
    public void ClaimTemporaryDiskForCollectable(
        Vector3 centerWorld,
        float radiusWorld,
        MusicalPhase phase,
        float holdSeconds,
        int ownerId,
        int priority = 50)
    {
        if (drums == null) return;

        // 1) Actually carve/hold the pocket (this despawns dust and schedules regrow)
        CarveTemporaryDiskFromCollectable(centerWorld, radiusWorld, phase, holdSeconds);

        // 2) Also claim cells in DustClaimManager so regrow attempts self-delay while the pocket is maintained
        //    (DustPocketRoutine refreshes this periodically, so expiry behaves as a "keep alive".)
        if (dustClaims == null) return;

        float cellSize = Mathf.Max(0.001f, drums.GetCellWorldSize());
        int rCells = Mathf.CeilToInt(Mathf.Max(0.1f, radiusWorld) / cellSize);

        int w = drums.GetSpawnGridWidth();
        int h = drums.GetSpawnGridHeight();
        if (w <= 0 || h <= 0) return;

        Vector2Int c = drums.WorldToGridPosition(centerWorld);
        c.x = Mathf.Clamp(c.x, 0, w - 1);
        c.y = Mathf.Clamp(c.y, 0, h - 1);

        string owner = $"Collectable#{ownerId}";

        for (int gx = c.x - rCells; gx <= c.x + rCells; gx++)
        for (int gy = c.y - rCells; gy <= c.y + rCells; gy++)
        {
            if (gx < 0 || gy < 0 || gx >= w || gy >= h) continue;

            int dx = gx - c.x;
            int dy = gy - c.y;
            if ((dx * dx + dy * dy) > (rCells * rCells)) continue;

            var gp = new Vector2Int(gx, gy);

            // Claim the ACTUAL cell (gp), not the center (c).
            // Use an expiry so it regrows if the collectable stops refreshing.
            dustClaims.ClaimCell(owner, gp, DustClaimType.TemporaryCarve, seconds: holdSeconds, refresh: true);
        }
    }
    public void CarveTemporaryDiskFromCollectable(
        Vector3 centerWorld,
        float radiusWorld,
        MusicalPhase phase,
        float holdSeconds)
    {
        if (drums == null) return;

        float cellSize = Mathf.Max(0.001f, drums.GetCellWorldSize());
        float rWorld   = Mathf.Max(0.1f, radiusWorld);
        int rCells     = Mathf.CeilToInt(rWorld / cellSize);

        int w = drums.GetSpawnGridWidth();
        int h = drums.GetSpawnGridHeight();
        if (w <= 0 || h <= 0) return;

        Vector2Int c = drums.WorldToGridPosition(centerWorld);

        // Visibility pocket, not a corridor. Limit actual despawns, but still mark holds for the full disk.
        int despawnBudget = Mathf.Max(6, Mathf.RoundToInt(mineNodeErodePerTick * 0.35f));
        int despawned = 0;

        float until = Time.time + Mathf.Max(0.05f, holdSeconds);

        for (int gx = c.x - rCells; gx <= c.x + rCells; gx++)
        {
            for (int gy = c.y - rCells; gy <= c.y + rCells; gy++)
            {
                if (gx < 0 || gy < 0 || gx >= w || gy >= h) continue;

                var gp = new Vector2Int(gx, gy);

                if (_permanentClearCells.Contains(gp)) continue;
                if (IsKeepClearCell(gp)) continue;

                // 1) ALWAYS extend hold time for this cell (even if it's currently empty).
                if (_collectableHoldUntil.TryGetValue(gp, out float prev))
                    _collectableHoldUntil[gp] = Mathf.Max(prev, until);
                else
                    _collectableHoldUntil[gp] = until;

                // 2) ALWAYS ensure a regrow coroutine exists (it will self-delay using _collectableHoldUntil).
                // Do not thrash/refresh if already pending.
                RequestRegrowCellAt(gp, phase, holdSeconds, refreshIfPending: false);

                // 3) Only despawn dust up to budget; the rest still gets "held/regrow scheduled".
                if (despawned < despawnBudget && _hexMap.ContainsKey(gp))
                {
                    DespawnDustAt(gp);
                    despawned++;
                }
            }
        }
    }

    /// <summary>
    /// Keeps a maneuvering pocket around the PhaseStar. Cells in this set are force-cleared and excluded from regrowth.
    /// Cells leaving the pocket are scheduled to regrow in-place.
   
   
    public void SetStarKeepClear(Vector2Int centerCell, int radiusCells, MusicalPhase phase, bool forceRemoveExisting)
    {
        if (drums == null) return; 
        int w = drums.GetSpawnGridWidth();
        int h = drums.GetSpawnGridHeight(); 
        if (w <= 0 || h <= 0) return;
        // Build desired keep-clear set for this tick
        var next = new HashSet<Vector2Int>();
        for (int dy = -radiusCells; dy <= radiusCells; dy++)
        {
            for (int dx = -radiusCells; dx <= radiusCells; dx++)
            {
                if ((dx * dx + dy * dy) > radiusCells * radiusCells) continue;
                var gp = new Vector2Int(centerCell.x + dx, centerCell.y + dy); 
                // Clamp keep-clear sets to valid grid cells to prevent OOB regrow churn.
                if (gp.x < 0 || gp.y < 0 || gp.x >= w || gp.y >= h) continue; 
                next.Add(gp);
            }
        }

        // Diff current vs desired
        _tmpStarRelease.Clear();
        _tmpStarClaim.Clear();

        foreach (var c in _starKeepClearCells)
            if (!next.Contains(c)) _tmpStarRelease.Add(c);

        foreach (var c in next)
            if (!_starKeepClearCells.Contains(c)) _tmpStarClaim.Add(c);

        // RELEASE -> allow regrow (schedule once)
        for (int i = 0; i < _tmpStarRelease.Count; i++)
        {
            var cell = _tmpStarRelease[i];
            _starKeepClearCells.Remove(cell);

            RequestRegrowCellAt(
                gridPos: cell,
                phase: phase,
                delaySeconds: -1f,
                refreshIfPending: true,
                clearImprintOnRefresh: false
            );
        }

        // CLAIM -> keep empty; optionally clear any existing dust; DO NOT schedule regrow
        for (int i = 0; i < _tmpStarClaim.Count; i++)
        {
            var cell = _tmpStarClaim[i];
            _starKeepClearCells.Add(cell);

            if (forceRemoveExisting && _hexMap.TryGetValue(cell, out var go) && go)
            {
                // use YOUR signature: (grid, go, toPool)
                RemoveActiveAt(cell, go, toPool: true);
            }
        }
    }
    public void ClearStarKeepClear(MusicalPhase phase)
    {
        if (_starKeepClearCells.Count == 0) return;

        _starKeepClearPrev.Clear();
        foreach (var c in _starKeepClearCells) _starKeepClearPrev.Add(c);
        _starKeepClearCells.Clear();

        foreach (var cell in _starKeepClearPrev)
            RequestRegrowCellAt(cell, phase);
    }

    private void CarveProgressiveTunnelToStar(Vector2Int start, Vector2Int starCell)
    {
        if (drums == null) return;

        int w = drums.GetSpawnGridWidth();
        int h = drums.GetSpawnGridHeight();

        // Safety: prevent infinite loops if something goes wrong.
        int maxSteps = w * h;

        var cur = start;
        var visited = new HashSet<Vector2Int> { cur };
        const int tunnelRadiusCells = 3;
        // If we're already touching the star pocket, nothing to do.
        if (IsConnectedToStarPocket(cur))
        {
            Debug.Log($"[MAZE] Already connected to star pocket: {cur}");
            return;
            
        }

        for (int step = 0; step < maxSteps; step++)
        {
            // Stop as soon as we connect into the star’s already-cleared space.
            if (IsConnectedToStarPocket(cur))
            {
                Debug.Log($"[MAZE] Already connected to star pocket at step: {step}. Max steps: {maxSteps}");
                return;
            }

            // Get neighbors and order them so "first" is biased toward the star.
            var dirs = GetHexDirections(cur.y);

            Vector2Int bestNext = default;
            bool foundDustyNeighbor = false;
            Vector2Int bestEmptyNext = default; 
            bool foundEmptyNeighbor = false;
            int bestScore = int.MaxValue;

            foreach (var d in dirs)
            {
                var n = cur + d;

                // Bounds
                if ((uint)n.x >= (uint)w || (uint)n.y >= (uint)h)
                    continue;

                // Avoid immediate loops if possible.
                // (If we get boxed in, we’ll loosen this below.)
                if (visited.Contains(n))
                    continue;

                int dx = n.x - starCell.x;
                int dy = n.y - starCell.y;
                int score = dx * dx + dy * dy;
                bool hasDust = _hexMap.ContainsKey(n); 
                if (hasDust) { 
                    // Prefer dusty neighbors: these are the cells we actually excavate.
                    if (score < bestScore) { 
                        bestScore = score; 
                        bestNext = n;
                        foundDustyNeighbor = true;
                    }
                }
                else { 
                    // Track the best empty neighbor as a fallback movement step.
                    // NOTE: do not overwrite a better dusty choice; empty only used if no dust found.
                    if (!foundEmptyNeighbor) { 
                        bestEmptyNext = n; 
                        foundEmptyNeighbor = true;
                    }
                    else { 
                        int ex = bestEmptyNext.x - starCell.x; 
                        int ey = bestEmptyNext.y - starCell.y; 
                        int emptyScore = ex * ex + ey * ey; 
                        if (score < emptyScore) 
                            bestEmptyNext = n;
                    }
                }
            }

            // If we found a dusty neighbor, carve and advance.
            if (foundDustyNeighbor)
            {
//                DespawnDustAtAndMarkPermanent(bestNext);
                CarvePermanentDisk(bestNext, tunnelRadiusCells);
                visited.Add(bestNext);
                cur = bestNext;
                continue;
            }
            if (foundEmptyNeighbor) { 
                visited.Add(bestEmptyNext); 
                cur = bestEmptyNext; 
                continue;
            }
            // If we didn't find a dusty, unvisited neighbor, relax the "visited" constraint
            // to prevent dead-ends from stopping excavation prematurely.
            bestScore = int.MaxValue;
            foreach (var d in dirs)
            {
                var n = cur + d;
                if ((uint)n.x >= (uint)w || (uint)n.y >= (uint)h)
                    continue;

                if (!_hexMap.ContainsKey(n))
                    continue;

                int dx = n.x - starCell.x;
                int dy = n.y - starCell.y;
                int score = dx * dx + dy * dy;

                if (score < bestScore)
                {
                    bestScore = score;
                    bestNext = n;
                    foundDustyNeighbor = true;
                }
            }

            if (foundDustyNeighbor)
            {
                CarvePermanentDisk(bestNext, tunnelRadiusCells);
//                DespawnDustAtAndMarkPermanent(bestNext);
                // NOTE: we do not add to visited here necessarily, because we're relaxing loops;
                // but adding is still useful for debug/inspection.
                visited.Add(bestNext);
                cur = bestNext;
                Debug.Log($"[MAZE] Found Dusty Neighbor: {cur}");
                continue;
            }

            // No dusty neighbor at all. We are surrounded by empty space.
            // If we're not connected yet, something about the connectivity criteria is mismatched,
            // so exit to avoid infinite churn.
            Debug.LogWarning($"[MAZE] Progressive tunnel stuck at {cur}; no dusty neighbors and not connected to star pocket.");
            return;
        }

        Debug.LogWarning($"[MAZE] Progressive tunnel exceeded maxSteps={maxSteps} start={start} star={starCell}. Possible loop.");
    }
    public bool IsCellDeepInDust(Vector2Int cell, int bufferCells)
    {
        if (bufferCells <= 0) bufferCells = 1;

        if (IsPermanentlyClearCell(cell)) return false;
        if (!HasDustAt(cell)) return false;

        // Require surrounding ring to also be dust (no open adjacency)
        for (int dx = -bufferCells; dx <= bufferCells; dx++)
        {
            for (int dy = -bufferCells; dy <= bufferCells; dy++)
            {
                if (dx == 0 && dy == 0) continue;

                var n = new Vector2Int(cell.x + dx, cell.y + dy);
                if (IsPermanentlyClearCell(n)) return false;
                if (!HasDustAt(n)) return false;
            }
        }
        return true;
    }
    private bool IsConnectedToStarPocket(Vector2Int cell)
    {
        // If the current cell is in the star pocket, we’re done.
        if (_starClearCells.Contains(cell))
            return true;

        // Or if we are adjacent to the star pocket, we’ve connected.
        foreach (var d in GetHexDirections(cell.y))
        {
            var n = cell + d;
            if (_starClearCells.Contains(n))
                return true;
        }

        return false;
    }
    
    private void CarvePermanentDisk(Vector2Int center, int radiusCells)
    {
        if (drums == null)
            return;

        int w = drums.GetSpawnGridWidth();
        int h = drums.GetSpawnGridHeight();

        for (int dx = -radiusCells; dx <= radiusCells; dx++)
        {
            for (int dy = -radiusCells; dy <= radiusCells; dy++)
            {
                var gp = new Vector2Int(center.x + dx, center.y + dy);

                // Bounds check
                if (gp.x < 0 || gp.y < 0 || gp.x >= w || gp.y >= h)
                    continue;

                // Optionally use a circular mask instead of square:
                if (dx * dx + dy * dy > radiusCells * radiusCells)
                    continue;

                DespawnDustAtAndMarkPermanent(gp);
            }
        }
    }
    
    public bool IsPermanentlyClearCell(Vector2Int gridPos)
    {
        return _permanentClearCells.Contains(gridPos);
    }
    
    private List<(Vector2Int grid, Vector3 world)> FilterOutPermanent(
        List<(Vector2Int, Vector3)> source)
    {
        if (source == null || source.Count == 0 || _permanentClearCells.Count == 0)
            return source;

        var result = new List<(Vector2Int, Vector3)>(source.Count);
        foreach (var (grid, world) in source)
        {
            if (_permanentClearCells.Contains(grid)) continue;
            result.Add((grid, world));
        }
        return result;
    }

    private void DespawnDustAtAndMarkPermanent(Vector2Int gridPos)
    {
        bool wasAlreadyPermanent = _permanentClearCells.Contains(gridPos);
        _permanentClearCells.Add(gridPos);
        
        DespawnDustAt(gridPos);
    }
    

    public void RegrowPreviousCorridorOnNewNodeSpawn(MusicalPhase phase)
    {
        if (_pendingCorridorRegrowth.Count == 0) return;
        var path = _pendingCorridorRegrowth.Dequeue();
        BeginCorridorRegrowthReverse(path, phase);
    }
    private void BeginCorridorRegrowthReverse(List<Vector2Int> carvedPath, MusicalPhase phase)
    {
        if (carvedPath == null || carvedPath.Count == 0) return;
        
        if (drums == null) return;

        var cellsToGrow = new List<(Vector2Int, Vector3)>(carvedPath.Count);

        for (int i = carvedPath.Count - 1; i >= 0; i--)
        {
            var cell = carvedPath[i];
            if (_permanentClearCells.Contains(cell)) continue;
            if (IsKeepClearCell(cell)) continue;
            if (_hexMap.ContainsKey(cell)) continue;
            if (!drums.IsSpawnCellAvailable(cell.x, cell.y)) continue;

            cellsToGrow.Add((cell, drums.GridToWorldPosition(cell)));
        }

        if (cellsToGrow.Count == 0) return;

        // Reuse your existing “staggered growth” feel.
        BeginStaggeredMazeRegrowth(cellsToGrow);
    }

    private GameObject GetDustFromPool()
    {
        // Pop until we find a live object
        while (_dustPool.Count > 0)
        {
            var go = _dustPool.Pop();
            if (!go) continue; // was destroyed after being pooled

            var parentRoot = (activeDustRoot != null) ? activeDustRoot : transform; // never parent ACTIVE dust to poolRoot
            go.transform.SetParent(parentRoot, worldPositionStays: false);
            go.SetActive(true);
            Debug.Log($"[POOL] spawn go='{go.name}' parent='{go.transform.parent?.name}' active={go.activeInHierarchy} scale={go.transform.localScale}");
            // Hard reset visuals + physics so it's never an invisible blocker
            var dust = go.GetComponent<CosmicDust>();
            if (dust == null) dust = go.AddComponent<CosmicDust>();
            dust.OnSpawnedFromPool(_mazeTint); // restores collider, layer, alpha=1, scale=full
 
            MarkCompositeDirty();
            return go;
        }

        // None in pool: create fresh and normalize through the same path
        var created = Instantiate(dustPrefab, poolRoot);
        var parentRoot2 = (activeDustRoot != null) ? activeDustRoot : transform; // never parent ACTIVE dust to poolRoot
        created.transform.SetParent(parentRoot2, worldPositionStays: false);
        created.SetActive(true);

        var d = created.GetComponent<CosmicDust>();
        if (d == null) d = created.AddComponent<CosmicDust>();
        d.OnSpawnedFromPool(_mazeTint);

        return created;
    }
    public void NotifyCompositeDirty() { 
        MarkCompositeDirty();
    }
    public void ReturnDustToPoolPublic(GameObject go) {
        if (!go) return;
        ReturnDustToPool(go);
    }
    private void ReturnDustToPool(GameObject go)
    {
        if (!go) return;

        // Ensure it cannot slow/block anything and cannot render particles.
        if (go.TryGetComponent<CosmicDust>(out var dust))
        { 
            dust.DespawnToPoolInstant(); // stops/clears particles + disables collider (and may disable child GO)
            if(go.activeSelf) go.SetActive(false);
        }
        else
        {
            // Fallback safety if prefab ever changes.
            var col = go.GetComponent<Collider2D>();
            if (col) col.enabled = false;

            var ps = go.GetComponentInChildren<ParticleSystem>(true);
            if (ps)
            {
                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                ps.Clear(true);
            }
        }

        // CRITICAL: pooled objects must be inactive (prevents "active but internally disabled" zombies)
        go.SetActive(false);

        var targetRoot = poolRoot != null ? poolRoot : transform;
        go.transform.SetParent(targetRoot, worldPositionStays: false);

        _dustPool.Push(go);
        MarkCompositeDirty();
    }
  
    [ContextMenu("Audit Orphan Dust")]
    public void AuditOrphanDust()
    {
        var all = GetComponentsInChildren<CosmicDust>(true);
        int tracked = 0, orphan = 0;
        var trackedSet = new HashSet<GameObject>(_hexMap.Values);

        foreach (var d in all)
        {
            if (trackedSet.Contains(d.gameObject)) tracked++;
            else orphan++;
        }

        Debug.Log($"[DUST-AUDIT] totalUnderGenerator={all.Length} tracked={tracked} orphan={orphan} hexMapCount={_hexMap.Count}");
    }
    
    private Vector2 ComputePhaseBias(MusicalPhase phase)
    {
        Vector2 bias =
            phase == MusicalPhase.Establish  ? new Vector2( 0f, -1f) :
            phase == MusicalPhase.Evolve     ? new Vector2( 0.6f, 0f) :
            phase == MusicalPhase.Intensify  ? new Vector2( 0f,  1f) :
            phase == MusicalPhase.Release    ? new Vector2(-0.6f, 0f) :
            phase == MusicalPhase.Wildcard  ? Random.insideUnitCircle.normalized :
            /* Pop */                          new Vector2( 0f, -0.4f);
        return bias.normalized * phaseFlowBias;
    }
    private void TryEnsureFlowField()
    {

        int w = drums.GetSpawnGridWidth();
        int h = drums.GetSpawnGridHeight();
        poolPrewarm = Mathf.Max(poolPrewarm, w, h);

    }
    
    private void IncrementalFlowFieldUpdate()
    {
        if (_flowField == null) return;
        int total = _ffW * _ffH;
        if (total == 0) return;

        int steps = Mathf.Clamp(flowTilesPerFrame, 1, total);
        for (int n = 0; n < steps; n++)
        {
            int idx = _flowUpdateCursor++;
            if (_flowUpdateCursor >= total) _flowUpdateCursor = 0;

            int x = idx % _ffW;
            int y = idx / _ffW;

            // compute target with a little noise + last bias
            Vector2 target = (Random.insideUnitCircle.normalized * 0.7f + _lastPhaseBias).normalized;
            _flowField[x, y] = Vector2.Lerp(_flowField[x, y], target, hiveShiftBlend);
            if (_flowField[x, y].sqrMagnitude < 1e-4f) _flowField[x, y] = Vector2.up;
        }
    }

    public Vector2 SampleFlowAtWorld(Vector3 worldPos)
    {
        if (_flowField == null)
            return Vector2.zero;

        var grid = drums.WorldToGridPosition(worldPos);
        if ((uint)grid.x >= (uint)_ffW || (uint)grid.y >= (uint)_ffH) return Vector2.zero;

        return _flowField[grid.x, grid.y] * baseFlowStrength;
    }
    
// =====================================================================
// Dust Weather Force Field (terrain maze)
// Each active dust cell behaves like a local force field that repels vehicles
// back toward empty space. The PhaseStar controls the global "weather" by phase.
// =====================================================================

[Tooltip("If enabled, logs dust force sampling counts occasionally for debugging.")]
    [SerializeField] private bool dustWeatherDebug = false;
    [SerializeField] private float dustWeatherDebugInterval = 0.5f;
    private float _nextDustWeatherDebugAt = 0f;

[Header("Dust Weather Force Field (Terrain)")]
[Tooltip("How many dust tiles outward we sample when computing the repulsion vector.")]
[SerializeField] private float dustInfluenceRadiusTiles = 1.75f;

[Tooltip("Max force (Newtons) applied when a vehicle is deeply embedded in dust proximity.")]
[SerializeField] private float dustMaxForce = 18f;

[Tooltip("How much of the repulsion direction is rotated 90° (current-like). 0 = none.")]
[Range(0f, 1f)]
[SerializeField] private float dustTangentialFrac = 0.35f;

[Tooltip("How much the global flow-field influences the dust force direction.")]
[Range(0f, 1f)]
[SerializeField] private float dustFlowFrac = 0.25f;

[Tooltip("How much turbulent noise influences the dust force direction (scaled per phase).")]
[Range(0f, 1f)]
[SerializeField] private float dustTurbulenceFrac = 0.15f;

[SerializeField] private float dustNoiseScale = 0.65f;
[SerializeField] private float dustNoiseSpeed = 0.60f;
[SerializeField] private float dustFootprintMul = 1f;

private struct DustWeatherParams
{
    public float repelMul;
    public float tangentialMul;
    public float flowMul;
    public float turbulenceMul;
    public float drainPerSecond;
}

private DustWeatherParams GetDustWeatherParams(MusicalPhase phase)
{
    // These are intentionally simple, phase-level defaults. Later, MineNode regrowth
    // can override at a per-cell granularity (instrument role weather).
    return phase switch
    {
        MusicalPhase.Establish => new DustWeatherParams
        {
            repelMul = 1.0f, tangentialMul = 0.25f, flowMul = 0.35f, turbulenceMul = 0.05f, drainPerSecond = 0.015f
        },
        MusicalPhase.Evolve => new DustWeatherParams
        {
            repelMul = 0.95f, tangentialMul = 0.65f, flowMul = 0.45f, turbulenceMul = 0.20f, drainPerSecond = 0.022f
        },
        MusicalPhase.Intensify => new DustWeatherParams
        {
            repelMul = 1.35f, tangentialMul = 0.20f, flowMul = 0.25f, turbulenceMul = 0.45f, drainPerSecond = 0.040f
        },
        MusicalPhase.Release => new DustWeatherParams
        {
            repelMul = 0.85f, tangentialMul = 0.15f, flowMul = 0.25f, turbulenceMul = 0.05f, drainPerSecond = 0.018f
        },
        MusicalPhase.Wildcard => new DustWeatherParams
        {
            repelMul = 1.05f, tangentialMul = 0.55f, flowMul = 0.55f, turbulenceMul = 0.90f, drainPerSecond = 0.030f
        },
        MusicalPhase.Pop => new DustWeatherParams
        {
            repelMul = 1.10f, tangentialMul = 0.20f, flowMul = 0.15f, turbulenceMul = 0.25f, drainPerSecond = 0.016f
        },
        _ => new DustWeatherParams
        {
            repelMul = 1.0f, tangentialMul = 0.25f, flowMul = 0.25f, turbulenceMul = 0.10f, drainPerSecond = 0.020f
        }
    };
}

/// <summary>
/// Computes the terrain-dust force-field vector at a vehicle position.
/// Returns true if any active dust cells are influencing the vehicle.
/// </summary>
public bool TryGetDustWeatherForce(
    Vector3 vehicleWorld,
    MusicalPhase phase,
    out Vector2 force,
    out float influence01,
    out float drainPerSecond)
{
    force = Vector2.zero;
    influence01 = 0f;
    drainPerSecond = 0f;
    if (dustWeatherDebug)
    {
        Debug.Log($"[DustWeather] ENTER vehicleWorld={vehicleWorld} phase={phase}", this);
    }

    if (drums == null)
    {
        if (dustWeatherDebug)
            Debug.Log("[DustWeather] EXIT drums==null (force field disabled)", this);
        return false;
    }

    float tile = Mathf.Max(0.0001f, tileDiameterWorld);
    float radiusWorld = dustInfluenceRadiusTiles * tile;
    float radiusSqr = radiusWorld * radiusWorld;

    // Sample neighborhood in grid-space (fast), then weight by world distance.
    Vector2Int center = drums.WorldToGridPosition(vehicleWorld);
    int rCells = Mathf.CeilToInt(dustInfluenceRadiusTiles) + 1;

    Vector2 p = vehicleWorld;

// Find dust colliders near the vehicle.
    int hitCount = Physics2D.OverlapCircleNonAlloc(p, dustQueryRadiusWorld, _dustHits, dustTerrainMask);

    if (hitCount <= 0)
    {
        if (dustWeatherDebug)
            Debug.Log($"[DustWeather] EXIT noColliders p={p} r={dustQueryRadiusWorld}", this);
        return false;
    }

    Vector2 sumAway = Vector2.zero;
    float best = 0f;

    for (int i = 0; i < hitCount; i++)
    {
        var col = _dustHits[i];
        if (!col) continue;

        // Closest point on dust to vehicle.
        Vector2 cp = col.ClosestPoint(p);

        Vector2 dv = p - cp;
        float dist = dv.magnitude;

        // If we're exactly on the surface, create a stable direction away from collider center.
        if (dist < 1e-4f)
            dv = (Vector2)(p - (Vector2)col.bounds.center);

        float t = 1f - Mathf.Clamp01(dist / dustQueryRadiusWorld); // 1..0
        float w = t * t;

        if (dv.sqrMagnitude > 1e-6f)
            sumAway += dv.normalized * w;

        if (w > best) best = w;
    }

    if (sumAway.sqrMagnitude < 1e-6f)
    {
        if (dustWeatherDebug)
            Debug.Log($"[DustWeather] EXIT weakSum p={p} hitCount={hitCount}", this);
        return false;
    }


    if (sumAway.sqrMagnitude < 1e-6f)
    {
        if (dustWeatherDebug)
        {
            Debug.Log($"[DustWeather] EXIT noInfluence center={center} influencingCells={hitCount} radiusWorld={radiusWorld:0.00}", this);
        }

        return false;
    }

    var wp = GetDustWeatherParams(phase);
    drainPerSecond = wp.drainPerSecond;

    // Base repel direction
    Vector2 repelDir = sumAway.normalized;

    // Tangential current (rotate 90°)
    Vector2 tangDir = new Vector2(-repelDir.y, repelDir.x);

    // Global flow contribution (already scaled in SampleFlowAtWorld)
    Vector2 flow = SampleFlowAtWorld(vehicleWorld);

    // Coherent turbulence (Perlin) for non-jittery noise
    float nx = Mathf.PerlinNoise(vehicleWorld.x * dustNoiseScale, Time.time * dustNoiseSpeed) - 0.5f;
    float ny = Mathf.PerlinNoise(vehicleWorld.y * dustNoiseScale, Time.time * dustNoiseSpeed + 17.3f) - 0.5f;
    Vector2 turb = new Vector2(nx, ny);
    if (turb.sqrMagnitude > 1e-6f) turb.Normalize();

    Vector2 dir =
        (repelDir * wp.repelMul) +
        (tangDir * (dustTangentialFrac * wp.tangentialMul)) +
        (flow * (dustFlowFrac * wp.flowMul)) +
        (turb * (dustTurbulenceFrac * wp.turbulenceMul));

    if (dir.sqrMagnitude < 1e-6f)
        dir = repelDir;

    influence01 = Mathf.Clamp01(best);
    force = dir.normalized * (dustMaxForce * influence01);
    if (dustWeatherDebug)
    {
        Debug.Log(
            $"[DustWeather] OK center={center} influencingCells={hitCount} " +
            $"best={best:0.00} influence01={influence01:0.00} force={force} tileDia={tileDiameterWorld:0.00}",
            this
        );
    }
    return true;
}
    private Color GetCellVisualColor(Vector2Int cell)
    {
        // Prefer live dust tint if the cell currently exists.
        if (TryGetDustAt(cell, out var dust) && dust != null)
            return dust.CurrentTint;

        // Otherwise prefer a pending MineNode imprint if present.
        if (_imprints != null && _imprints.TryGetValue(cell, out var imp))
            return imp.color;

        // Fallback to the current maze tint.
        return _mazeTint;
    }

    private Color BlendImprintWithNeighbors(Vector2Int cell, Color target, int radius, float neighborWeight)
    {
        radius = Mathf.Max(0, radius);
        neighborWeight = Mathf.Clamp01(neighborWeight);
        if (radius == 0 || neighborWeight <= 0f)
            return target;

        int count = 0;
        float r = 0f, g = 0f, b = 0f, a = 0f;

        for (int dy = -radius; dy <= radius; dy++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                var c = GetCellVisualColor(new Vector2Int(cell.x + dx, cell.y + dy));
                r += c.r; g += c.g; b += c.b; a += c.a;
                count++;
            }
        }

        if (count <= 0)
            return target;

        Color avg = new Color(r / count, g / count, b / count, a / count);
        return Color.Lerp(target, avg, neighborWeight);
    }

    public bool TryGetDustAt(Vector2Int cell, out CosmicDust dust) {
        dust = null;
        if (!_hexMap.TryGetValue(cell, out var go) || go == null) return false;
        return go.TryGetComponent(out dust) && dust != null;
    }

    public void ApplyProfile(PhaseStarBehaviorProfile profile)
    {
        if (profile == null) return;

        // Keep color per phase – that’s useful, low-risk feedback.
        _mazeTint = profile.mazeColor;
    }

    public void BeginStaggeredMazeRegrowth(List<(Vector2Int, Vector3)> cellsToGrow)
    {
        if (_spawnRoutine != null)
            StopCoroutine(_spawnRoutine); 
        float fit = Mathf.Clamp(drums.GetLoopLengthInSeconds()*0.25f, 0.08f, drums.GetLoopLengthInSeconds()*0.5f);
        _spawnRoutine = StartCoroutine(StaggeredGrowthFitDuration(cellsToGrow, fit));
    }

    public void RetintExisting(float seconds = 0.35f) {
        foreach (var go in hexagons) { 
            if (!go) continue; 
            var d = go.GetComponent<CosmicDust>(); 
            if (d != null) StartCoroutine(d.RetintOver(seconds, _mazeTint));
        }
    }

    private void ClearMaze()
    {
        BeginCompositeBatch();
        try
        {
            var snapshot = new List<KeyValuePair<Vector2Int, GameObject>>(_hexMap);
            foreach (var kv in snapshot)
                RemoveActiveAt(kv.Key, kv.Value, toPool: true);

            hexagons.Clear();
            _permanentClearCells.Clear();
        }
        finally
        {
            EndCompositeBatch(); // one rebuild at end
        }
    }

    public List<(Vector2Int, Vector3)> CalculateMazeGrowth(
        Vector2Int center,
        MusicalPhase phase,
        float hollowRadius = 0f,
        bool avoidStarHole = false)
    {
        List<(Vector2Int, Vector3)> raw;

        switch (phase) {
            case MusicalPhase.Establish:
                raw = Build_CA(center, hollowRadius, avoidStarHole);
                break;
            case MusicalPhase.Evolve:
                raw = CalculateCarvedMazeWalls(onScreenOnly: true, braidChance: 0.22f, corridorThickness: 1);
                break;
            case MusicalPhase.Intensify:
                raw = Build_RingChokepoints(center, ringSpacing: 3, ringThickness: 1, jitter: 0.25f, hollowRadius, avoidStarHole);
                break;
            case MusicalPhase.Wildcard:
                raw = Build_DrunkenStrokes(strokes: 6, maxLen: 14, stepJitter: 0.35f, dilate: 0.35f);
                break;
            case MusicalPhase.Release:
                raw = CalculateCarvedMazeWalls(onScreenOnly: true, braidChance: 0.60f, corridorThickness: 2);
                break;
            case MusicalPhase.Pop:
                raw = Build_PopDots(step: 3, phaseOffset: 0); // or whatever you currently do
                break;
            default:
                raw = Build_CA(center, hollowRadius: 0f, avoidStarHole: false);
                break;
        }

        return FilterOutPermanent(raw);
    }
    public bool IsDustTerrainCollider(Collider2D col) { 
        if (col == null) return false;
        // Primary: layer mask
        int bit = 1 << col.gameObject.layer; 
        if ((dustTerrainMask.value & bit) != 0) return true;
        // Fallback: collider belongs under THIS generator (composite + children)
        var owner = col.GetComponentInParent<CosmicDustGenerator>(); 
        return owner == this;
    }

    // ---------------------------------------------------------------------
    // Dust Terrain Contour API (CompositeCollider2D)
    // Used by PhaseStarMotion2D to steer along corridors/edges without "eating" dust.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Returns the CompositeCollider2D used to represent dust terrain, if available.
    /// </summary>
    public CompositeCollider2D DustCompositeCollider => compositeCollider;


    /// <summary>
    /// Removes dust topology immediately (opens corridor) but fades visuals and pools afterward.
    /// Boost carving should use this, NOT DespawnDustAt.
    /// </summary>
    public void CarveDustAt(Vector2Int gridPos, float fadeSeconds)
    {
        if (_permanentClearCells.Contains(gridPos)) return;
        if (!_hexMap.TryGetValue(gridPos, out var go) || go == null) return;

        RemoveActiveAt(gridPos, go, toPool: false);

        if (go.TryGetComponent<CosmicDust>(out var dust))
            dust.DissipateAndPoolVisualOnly(Mathf.Max(0.01f, fadeSeconds));
        else
            ReturnDustToPool(go);

        // Schedule regrow (will be held off by keep-clear and other vetoes inside coroutine)
        RequestRegrowCellAt(gridPos, GetCurrentPhaseSafe(), refreshIfPending: true);
    }
    private bool IsWorldPositionInsideScreen(Vector3 worldPos) {
        var cam = Camera.main; 
        if (!cam) return true; // no camera yet → don't cull
        Vector3 viewport = cam.WorldToViewportPoint(worldPos); 
        return viewport.x >= 0f && viewport.x <= 1f && viewport.y >= 0f && viewport.y <= 1f;
    } 
        private IEnumerator StaggeredGrowthFitDuration(List<(Vector2Int grid, Vector3 pos)> cells, float totalDuration) {
        // Keep pacing similar, but enforce a per-frame millisecond budget
        float deadlineStep = Mathf.Max(0.0f, totalDuration / Mathf.Max(1, cells.Count));

        _isSpawningMaze = true;
        BeginBulkTopology();
        try
        {
            if (drums == null) drums = FindObjectOfType<DrumTrack>();
            if (drums == null || dustPrefab == null) yield break;

            drums.SyncTileWithScreen();
            float cellWorldSize = Mathf.Max(0.001f, drums.GetCellWorldSize());

            MusicalPhase phaseNow = GetCurrentPhaseSafe();

            float lastPacedAt = Time.realtimeSinceStartup;
            int i = 0;

            // Track which dust we spawned so we can re-enable colliders in a controlled way.
            // (Avoid scanning _hexMap; we want an explicit list.)
            var spawnedDust = new List<CosmicDust>(cells.Count);

            while (i < cells.Count)
            {
                float frameStart  = Time.realtimeSinceStartup;
                float frameBudget = Mathf.Max(0f, maxSpawnMillisPerFrame) / 1000f;

                while (i < cells.Count && (Time.realtimeSinceStartup - frameStart) < frameBudget)
                {
                    var (grid, pos) = cells[i++];

                    // ---------------------------
                    // GATING
                    // ---------------------------
                    if (_permanentClearCells.Contains(grid)) continue;
                    if (IsKeepClearCell(grid)) continue;
                    if (_hexMap.ContainsKey(grid)) continue;
                    if (!drums.IsSpawnCellAvailable(grid.x, grid.y)) continue;
                    if (IsDustSpawnBlocked(grid)) continue;          // includes permanent + keepclear + claims/holds
                    if (!Collectable.IsCellFreeStatic(grid)) continue; // never grow dust on top of a collectable
                    // ---------------------------
                    // SPAWN + REGISTER (VISUAL FIRST)
                    // ---------------------------
                    var hex = GetDustFromPool();
                    hex.transform.SetPositionAndRotation(pos, Quaternion.identity);
                    hexagons.Add(hex);

                    if (hex.TryGetComponent<CosmicDust>(out var dust))
                    {
                        dust.SetTrackBundle(this, drums, phaseNow);
                        dust.SetCellSizeDrivenScale(cellWorldSize, dustFootprintMul, cellClearanceWorld);

                        dust.PrepareForReuse();
                        dust.SetGrowInDuration(hexGrowInSeconds);
                        dust.SetTint(_mazeTint);
                        dust.ConfigureForPhase(phaseNow);
                        dust.Begin();

                        // Critical: keep collider OFF during bulk topology changes.
                        dust.SetTerrainColliderEnabled(false);
                        spawnedDust.Add(dust);
                    }

                    // This updates _hexMap but (during bulk) will NOT schedule composite rebuilds.
                    RegisterHex(grid, hex);
                }

                // Pacing
                float elapsedSinceLast = Time.realtimeSinceStartup - lastPacedAt;
                if (elapsedSinceLast < deadlineStep)
                    yield return new WaitForSeconds(deadlineStep - elapsedSinceLast);
                else
                    yield return null;

                lastPacedAt = Time.realtimeSinceStartup;
            }

            // --------------------------------------------------------------------
            // PHYSICS PHASE: re-enable colliders gradually, then rebuild composite ONCE
            // --------------------------------------------------------------------
            int j = 0;
            while (j < spawnedDust.Count)
            {
                float frameStart  = Time.realtimeSinceStartup;
                float frameBudget = Mathf.Max(0f, maxSpawnMillisPerFrame) / 1000f;

                while (j < spawnedDust.Count && (Time.realtimeSinceStartup - frameStart) < frameBudget)
                {
                    var d = spawnedDust[j++];
                    if (d != null)
                        d.SetTerrainColliderEnabled(true);
                }

                yield return null;
            }

            // Now rebuild composite once after colliders are in their final state.
            EnsureCompositeRef();
            if (compositeCollider != null)
            {
                compositeCollider.GenerateGeometry();
    #if UNITY_EDITOR
                Debug.Log($"[DUST:COMPOSITE] GenerateGeometry -> pathCount={compositeCollider.pathCount}", this);
    #endif

                _compositeDirty = false;
                _nextCompositeRebuildAt = (compositeRebuildMinInterval > 0f)
                    ? (Time.unscaledTime + compositeRebuildMinInterval)
                    : Time.unscaledTime;
            }
        }
        finally
        {
            EndBulkTopology();
            _isSpawningMaze = false;
        }
    }


    // === Tint diffusion (Option 2) ===
    private void EnqueueTintDirty(Vector2Int cell)
    {
        if (_tintDirtySet.Add(cell))
            _tintDirtyQueue.Enqueue(cell);
    }

    private void MarkTintDirty(Vector2Int center, int radius)
    {
        radius = Mathf.Max(0, radius);
        EnqueueTintDirty(center);

        if (radius == 0) return;

        for (int dy = -radius; dy <= radius; dy++)
        for (int dx = -radius; dx <= radius; dx++)
        {
            if (dx == 0 && dy == 0) continue;
            EnqueueTintDirty(new Vector2Int(center.x + dx, center.y + dy));
        }
    }

    private float ColorMaxAbsDelta(Color a, Color b)
    {
        float dr = Mathf.Abs(a.r - b.r);
        float dg = Mathf.Abs(a.g - b.g);
        float db = Mathf.Abs(a.b - b.b);
        float da = Mathf.Abs(a.a - b.a);
        return Mathf.Max(Mathf.Max(dr, dg), Mathf.Max(db, da));
    }

    private Color ComputeNeighborAverageColor(Vector2Int cell, int radius, out int count)
    {
        radius = Mathf.Max(0, radius);
        count = 0;

        if (radius == 0)
            return GetCellVisualColor(cell);

        float r = 0f, g = 0f, b = 0f, a = 0f;

        for (int dy = -radius; dy <= radius; dy++)
        for (int dx = -radius; dx <= radius; dx++)
        {
            if (dx == 0 && dy == 0) continue;

            Color c = GetCellVisualColor(new Vector2Int(cell.x + dx, cell.y + dy));
            r += c.r; g += c.g; b += c.b; a += c.a;
            count++;
        }

        if (count <= 0)
            return GetCellVisualColor(cell);

        return new Color(r / count, g / count, b / count, a / count);
    }

    private void ProcessTintDiffusion(float dt)
    {
        if (!enableTintDiffusion) return;

        _tintDiffusionAccum += dt;
        if (_tintDiffusionAccum < tintDiffusionInterval) return;
        _tintDiffusionAccum = 0f;

        if (_tintDirtyQueue.Count == 0) return;

        int budget = Mathf.Max(1, tintDiffusionMaxCellsPerTick);
        int radius = tintDiffusionRadius;

        for (int i = 0; i < budget && _tintDirtyQueue.Count > 0; i++)
        {
            Vector2Int cell = _tintDirtyQueue.Dequeue();
            _tintDirtySet.Remove(cell);

            if (!TryGetDustAt(cell, out var dust) || dust == null)
                continue;

            // Compute neighborhood average (includes live dust + pending imprints + maze fallback)
            Color avg = ComputeNeighborAverageColor(cell, radius, out int nCount);
            if (nCount <= 0) continue;

            Color cur = dust.CurrentTint;
            Color next = Color.Lerp(cur, avg, Mathf.Clamp01(tintDiffusionStrength));

            if (ColorMaxAbsDelta(cur, next) < tintDiffusionMinDelta)
                continue;

            dust.SetTint(next);

            if (tintDiffusionPropagateOnChange)
            {
                // Propagate softly to immediate neighbors only (prevents runaway queue growth).
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    EnqueueTintDirty(new Vector2Int(cell.x + dx, cell.y + dy));
                }
            }
        }
    }

private void RemoveActiveAt(Vector2Int grid, GameObject go, bool toPool = true) {
        // Free grid cell by key (authoritative)
        drums.FreeSpawnCell(grid.x, grid.y); 
        // Drop from registries
        _hexMap.Remove(grid); 
        if (go) hexagons.Remove(go);
        // Pool the object
        if (toPool && go) ReturnDustToPool(go);

        // Diffusion prep: removing dust can expose the base tint and create hard seams.
        MarkTintDirty(grid, tintDirtyMarkRadius);

        MarkCompositeDirty();
        Debug.Log($"[DUSTGEN] RemoveActiveAt grid={grid} hexMapHasAfter={_hexMap.ContainsKey(grid)} go={(go ? go.name : "null")}", this);
    }
    
        private void RegisterHex(Vector2Int gridPos, GameObject hex)
    {
        if (_hexMap.TryGetValue(gridPos, out var existing) && existing != null && existing != hex)
        {
            hexagons.Remove(existing);
            ReturnDustToPool(existing);
        }

        _hexMap[gridPos] = hex;

        // During bulk spawn/regrow, defer composite rebuild until the end.
        if (IsBulkTopology)
        {
            _compositeDirty = true;
            return;
        }

        MarkCompositeDirty();
    }

        private List<(Vector2Int, Vector3)> CalculateCarvedMazeWalls(bool onScreenOnly = true, float braidChance = 0.0f, int corridorThickness = 1) { 
        var growth = new List<(Vector2Int, Vector3)>();

    int w = drums.GetSpawnGridWidth();   // grid size comes from SpawnGrid
    int h = drums.GetSpawnGridHeight();  // :contentReference[oaicite:3]{index=3}
    if (w <= 0 || h <= 0) { 
        Debug.LogError($"[MAZE] Invalid grid size W={w} H={h} — grid not initialized yet."); return growth; 
    }
    // 1) Candidate cells = free cells (and on-screen if requested)
    var candidates = new HashSet<Vector2Int>();
    for (int x = 0; x < w; x++)
    for (int y = 0; y < h; y++)
    {
        if (!drums.IsSpawnCellAvailable(x, y)) continue;      // :contentReference[oaicite:4]{index=4}
        var c = new Vector2Int(x, y);
        if (IsDustSpawnBlocked(c)) continue;
        if (onScreenOnly && !IsWorldPositionInsideScreen(drums.GridToWorldPosition(c))) // :contentReference[oaicite:5]{index=5}
            continue;
        candidates.Add(c);
    } 
    if (candidates.Count == 0) { 
        Debug.LogWarning($"[MAZE] No candidate cells. onScreenOnly={onScreenOnly}, W={w}, H={h}. Example avail[0,0]={drums.IsSpawnCellAvailable(0,0)}"); 
        return growth;
    }
    // 2) Carve passages via randomized DFS
    var passages = new HashSet<Vector2Int>();
    var stack = new Stack<Vector2Int>();

    Vector2Int start = Any(candidates);
    if (start.x < 0) return growth; // no candidates

    passages.Add(start);
    stack.Push(start);

    // local: hex neighbors using your odd-column layout (HexToWorldPosition uses x%2) :contentReference[oaicite:6]{index=6}
    IEnumerable<Vector2Int> Neighbors(Vector2Int p)
    {
        foreach (var d in GetHexDirections(p.y))
        {
            var n = p + d;
            if ((uint)n.x < (uint)w && (uint)n.y < (uint)h && candidates.Contains(n))
                yield return n;
        }
    }

    var rng = new System.Random();
    while (stack.Count > 0)
    {
        var cur = stack.Peek();
        var unvisited = new List<Vector2Int>();
        foreach (var n in Neighbors(cur))
            if (!passages.Contains(n)) unvisited.Add(n);

        if (unvisited.Count == 0) { stack.Pop(); continue; }

        // choose a random neighbor and "carve" to it
        var next = unvisited[rng.Next(unvisited.Count)];
        passages.Add(next);
        stack.Push(next);
    }

    // 3) Optional braiding (add a few loops)
    if (braidChance > 0f)
    {
        var toOpen = new List<Vector2Int>();
        foreach (var cell in candidates)
        {
            if (passages.Contains(cell)) continue;
            int touching = 0;
            foreach (var n in Neighbors(cell)) if (passages.Contains(n)) touching++;
            if (touching >= 2 && Random.value < braidChance)
                toOpen.Add(cell);
        }
        foreach (var c in toOpen) passages.Add(c);
    }

    // 4) Optional corridor thickening by dilating passages
    if (corridorThickness > 1)
    {
        var expanded = new HashSet<Vector2Int>(passages);
        for (int r = 1; r < corridorThickness; r++)
        {
            var ring = new HashSet<Vector2Int>(expanded);
            foreach (var p in ring)
                foreach (var n in Neighbors(p)) expanded.Add(n);
        }
        passages = expanded;
    }

    // 5) Walls = candidates \ passages → spawn dust there
    foreach (var cell in candidates)
    {
        if (passages.Contains(cell)) continue; // leave corridor empty
        Vector3 world = drums.GridToWorldPosition(cell);       // :contentReference[oaicite:7]{index=7}
        growth.Add((cell, world));
    }
    return growth;
}
        private static Vector2Int Any(HashSet<Vector2Int> set) {
        foreach (var v in set) return v;             // returns the first enumerated element
        return new Vector2Int(-1, -1);
    } 
    private MusicalPhase GetCurrentPhaseSafe()
    {
        TryEnsureRefs();
        return (phaseTransitionManager != null) ? phaseTransitionManager.currentPhase : MusicalPhase.Establish;
    }  
    public void DespawnDustAt(Vector2Int gridPos)
    {
        if (_permanentClearCells.Contains(gridPos)) return;

        if (_hexMap.TryGetValue(gridPos, out var go) && go != null)
            RemoveActiveAt(gridPos, go, toPool: true);

        // CRITICAL: schedule regrow.
        RequestRegrowCellAt(gridPos, GetCurrentPhaseSafe(), refreshIfPending: true);
    }
    private int CountFilledNeighbors(Vector2Int cell)
    {
        int count = 0;
        foreach (var dir in GetHexDirections(cell.y))
        {
            Vector2Int neighbor = cell + dir;
            if (_fillMap.TryGetValue(neighbor, out bool filled) && filled)
                count++;
        }
        return count;
    }
    private List<Vector2Int> GetHexDirections(int row)
    {
        // Even-q offset coordinates
        return row % 2 == 0 ? new List<Vector2Int>
        {
            new(1, 0), new(0, 1), new(-1, 1),
            new(-1, 0), new(-1, -1), new(0, -1)
        } : new List<Vector2Int>
        {
            new(1, 0), new(1, 1), new(0, 1),
            new(-1, 0), new(0, -1), new(1, -1)
        };
    }
    
public int CarveTemporaryCellFromMineNode(
    Vector3 centerWorld,
    MusicalPhase phase,
    float regrowDelaySeconds,
    Color imprintColor,
    Color imprintShadowColor,
    float imprintHardness01,
    int resolveRadiusCells,
    float appetiteMul = 1f)
{
    if (drums == null) return 0;

    EnsureImprints();

    int w = drums.GetSpawnGridWidth();
    int h = drums.GetSpawnGridHeight();

    Vector2Int c = drums.WorldToGridPosition(centerWorld);

    // Footprint is cell-based, so 0 = 1 cell, 1 = 3x3, etc.
    int rCells = Mathf.Max(0, resolveRadiusCells);

    // Budget is "how many *present* dust cells we may remove this tick".
    // We still refresh regrow timers across the footprint.
    int removed = 0;
    int budget  = Mathf.RoundToInt(mineNodeErodePerTick * Mathf.Clamp(appetiteMul, 0.4f, 2f));

    float hardness01 = Mathf.Clamp01(imprintHardness01);

    for (int gx = c.x - rCells; gx <= c.x + rCells; gx++)
    {
        for (int gy = c.y - rCells; gy <= c.y + rCells; gy++)
        {
            if (gx < 0 || gy < 0 || gx >= w || gy >= h) continue;

            Vector2Int gp = new Vector2Int(gx, gy);

            // Never touch permanent-clear or keep-clear cells.
            if (_permanentClearCells.Contains(gp)) continue;
            if (IsKeepClearCell(gp)) continue;

            // Record/refresh MineNode imprint for regrowth semantics.
            Color blendedImprint = BlendImprintWithNeighbors(gp, imprintColor, imprintBlendRadius, imprintNeighborWeight);
            _imprints[gp] = new DustImprint
            {
                color       = blendedImprint,
                shadowColor = imprintShadowColor,
                healDelay   = regrowDelaySeconds,
                hardness01  = hardness01
            };

            MarkTintDirty(gp, tintDirtyMarkRadius);

            // Clear if present, respecting budget.
            if (_hexMap.ContainsKey(gp))
            {
                if (removed < budget)
                {
                    DespawnDustAt(gp);
                    removed++;
                }
            }

            // Always refresh regrow timer (even if already empty).
            RequestRegrowCellAt(gp, phase, regrowDelaySeconds, refreshIfPending: true);
        }
    }

    return removed;
}

    public void CarveTemporaryCellFromVehicle(
        Vector3 worldPos,
        MusicalPhase phase,
        float healDelaySeconds,
        int resolveRadiusCells = 0)
    {
        if (drums == null) return;

        int w = drums.GetSpawnGridWidth();
        int h = drums.GetSpawnGridHeight();
        if (w <= 0 || h <= 0) return;

        Vector2Int cell;
        if (resolveRadiusCells > 0)
        {
            if (!TryResolveDustCellFromWorldPoint(worldPos, resolveRadiusCells, out cell))
                cell = drums.WorldToGridPosition(worldPos);
        }
        else
        {
            cell = drums.WorldToGridPosition(worldPos);
        }

        if (cell.x < 0 || cell.y < 0 || cell.x >= w || cell.y >= h)
            return;

        // Open corridor immediately
        if (_hexMap.ContainsKey(cell))
            DespawnDustAt(cell);

        // Vehicle carving explicitly removes any MineNode imprint
        _imprints.Remove(cell);

        // Schedule normal dust regrow
        RequestRegrowCellAt(
            cell,
            phase,
            healDelaySeconds,
            refreshIfPending: true
        );
    }

    private float GetFillProbability(MusicalPhase phase)
    {
        return phase switch
        {
            MusicalPhase.Establish => 0.90f,
            MusicalPhase.Evolve => 0.30f,
            MusicalPhase.Intensify => 0.45f,
            MusicalPhase.Release => 0.15f,
            MusicalPhase.Wildcard => 0.40f + Random.Range(-0.1f, 0.1f),
            _ => 0.35f
        };
    }
    private List<(Vector2Int, Vector3)> Build_CA(Vector2Int center, float hollowRadius, bool avoidStarHole)
    {
        List<(Vector2Int, Vector3)> growth = new();
        _fillMap.Clear();

        Vector3 centerWorld = drums.GridToWorldPosition(center);
        float fillChance = GetFillProbability(MusicalPhase.Establish);
        int W = drums.GetSpawnGridWidth();
        int H = drums.GetSpawnGridHeight();

        for (int x = 0; x < W; x++)
        for (int y = 0; y < H; y++)
        {
            var pos = new Vector2Int(x, y);
            if (!drums.IsSpawnCellAvailable(x, y)) continue;

            if (avoidStarHole && hollowRadius > 0f)
            {
                float d = Vector3.Distance(drums.GridToWorldPosition(pos), centerWorld);
                if (d < hollowRadius) continue;
            }

            _fillMap[pos] = Random.value < fillChance;
        }

        for (int i = 0; i < iterations; i++)
        {
            var next = new Dictionary<Vector2Int, bool>();
            foreach (var cell in _fillMap.Keys)
            {
                int n = CountFilledNeighbors(cell);
                bool cur = _fillMap[cell];
                if (cur && (n < 2 || n > 4)) next[cell] = false;
                else if (!cur && n == 3)     next[cell] = true;
                else                         next[cell] = cur;
            }
            _fillMap = next;
        }

        foreach (var kv in _fillMap)
        {
            if (!kv.Value) continue;
            var grid = kv.Key;
            var world = drums.GridToWorldPosition(grid);
            if (!IsWorldPositionInsideScreen(world)) continue;
            growth.Add((grid, world));
        }
        return growth;
    }
    private List<(Vector2Int, Vector3)> Build_RingChokepoints(Vector2Int center, int ringSpacing, int ringThickness, float jitter, float hollowRadius, bool avoidStarHole) {
        var growth = new List<(Vector2Int, Vector3)>();
        int W = drums.GetSpawnGridWidth();
        int H = drums.GetSpawnGridHeight();
        Vector3 centerW = drums.GridToWorldPosition(center);

        // BFS distance by hex steps (avoids axial conversion headaches)
        var dist = new Dictionary<Vector2Int, int>();
        var q = new Queue<Vector2Int>();
        var seen = new HashSet<Vector2Int>();
        q.Enqueue(center); dist[center] = 0; seen.Add(center);

        while (q.Count > 0)
        {
            var p = q.Dequeue();
            int row = p.y;
            foreach (var d in GetHexDirections(row))
            {
                var n = p + d;
                if ((uint)n.x >= (uint)W || (uint)n.y >= (uint)H) continue;
                if (!drums.IsSpawnCellAvailable(n.x, n.y)) continue;
                if (seen.Contains(n)) continue;

                // optional star hole
                if (avoidStarHole && hollowRadius > 0f)
                {
                    float dd = Vector3.Distance(drums.GridToWorldPosition(n), centerW);
                    if (dd < hollowRadius) continue;
                }

                seen.Add(n);
                dist[n] = dist[p] + 1;
                q.Enqueue(n);
            }
        }

        // Place dust on certain rings (distance bands)
        foreach (var kv in dist)
        {
            int d = kv.Value;
            // ring test with jitter: e.g., (d % spacing) < thickness (+/- jitter)
            float r = d % ringSpacing;
            bool onRing = r < ringThickness + Random.Range(-jitter, jitter);
            if (!onRing) continue;

            var grid = kv.Key;
            var world = drums.GridToWorldPosition(grid);
            if (!IsWorldPositionInsideScreen(world)) continue;
            growth.Add((grid, world));
        }

        return growth;
    }
    private List<(Vector2Int, Vector3)> Build_DrunkenStrokes(int strokes, int maxLen, float stepJitter, float dilate)
    {
        var growth = new HashSet<Vector2Int>();
        int W = drums.GetSpawnGridWidth();
        int H = drums.GetSpawnGridHeight();

        for (int s = 0; s < strokes; s++)
        {
            // random start on-screen & available
            Vector2Int p = new(
                Random.Range(0, W),
                Random.Range(0, H)
            );
            int safety = 0;
            while (safety++ < 100 &&
                   (!drums.IsSpawnCellAvailable(p.x, p.y) ||
                    !IsWorldPositionInsideScreen(drums.GridToWorldPosition(p))))
            {
                p = new(Random.Range(0, W), Random.Range(0, H));
            }

            if (safety >= 100) continue;

            int len = Random.Range(maxLen / 2, maxLen + 1);
            Vector2Int cur = p;
            for (int i = 0; i < len; i++)
            {
                growth.Add(cur);

                // jittered random neighbor
                var dirs = GetHexDirections(cur.y);
                var nxt = dirs[Random.Range(0, dirs.Count)];
                if (Random.value < stepJitter) nxt = dirs[Random.Range(0, dirs.Count)];
                var n = cur + nxt;

                if ((uint)n.x >= (uint)W || (uint)n.y >= (uint)H) break;
                if (!drums.IsSpawnCellAvailable(n.x, n.y)) break;
                if (!IsWorldPositionInsideScreen(drums.GridToWorldPosition(n))) break;

                cur = n;
            }
        }

        // Optional dilation to thicken strokes
        if (dilate > 0f)
        {
            var thick = new HashSet<Vector2Int>(growth);
            foreach (var c in growth)
            foreach (var n in GetHexDirections(c.y))
                if (Random.value < dilate) thick.Add(c + n);
            growth = thick;
        }

        // Pack into list
        var list = new List<(Vector2Int, Vector3)>();
        foreach (var g in growth)
            list.Add((g, drums.GridToWorldPosition(g)));
        return list;
    }
    private List<(Vector2Int, Vector3)> Build_PopDots(int step, int phaseOffset) {
        var growth = new List<(Vector2Int, Vector3)>();
        int W = drums.GetSpawnGridWidth();
        int H = drums.GetSpawnGridHeight();

        for (int x = 0; x < W; x++)
        for (int y = 0; y < H; y++)
        {
            if (!drums.IsSpawnCellAvailable(x, y)) continue;
            // simple periodic mask → polka dots
            if (((x + (y * 2) + phaseOffset) % step) != 0) continue;

            var grid = new Vector2Int(x, y);
            var world = drums.GridToWorldPosition(grid);
            if (!IsWorldPositionInsideScreen(world)) continue;

            growth.Add((grid, world));
        }
        return growth;
    }
#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        if (!Application.isPlaying) return;

        if (drums == null) return;

        int w = drums.GetSpawnGridWidth();
        int h = drums.GetSpawnGridHeight();
        if (w <= 0 || h <= 0) return;

        float cellSize = drums.GetCellWorldSize(); // whatever your API is here

        foreach (var kvp in _hexMap)
        {
            var gp = kvp.Key;
            var world = drums.GridToWorldPosition(gp);

            // Color by type: dust, permanent corridor, empty
            bool hasDust = kvp.Value != null && kvp.Value.activeInHierarchy;
            bool isPermanent = _permanentClearCells.Contains(gp);

            if (hasDust)
                Gizmos.color = Color.cyan;
            else if (isPermanent)
                Gizmos.color = Color.yellow;
            else
                Gizmos.color = new Color(1f, 1f, 1f, 0.1f);

            Gizmos.DrawWireCube(world, new Vector3(cellSize, cellSize, 0.0f));
        }
    }
#endif

#if UNITY_EDITOR
    [ContextMenu("DEBUG/Dump Composite State")]
    public void DEBUG_DumpCompositeState()
    {
        EnsureCompositeRef();
        var cc = compositeCollider;

        var rb = cc != null ? cc.attachedRigidbody : GetComponent<Rigidbody2D>();
        Debug.Log(
            $"[DUST:COMPOSITE] gen='{name}' layer={gameObject.layer} " +
            $"cc={(cc ? "OK" : "NULL")} cc.enabled={(cc ? cc.enabled : false)} " +
            $"cc.paths={(cc ? cc.pathCount : -1)} " +
            $"rb={(rb ? rb.bodyType.ToString() : "NULL")} " +
            $"activeDustRoot='{(activeDustRoot ? activeDustRoot.name : "NULL")}'",
            this
        );

        // Spot-check one active dust tile
        if (activeDustRoot != null)
        {
            var dust = activeDustRoot.GetComponentInChildren<CosmicDust>(true);
            if (dust != null)
            {
                var poly = dust.GetComponent<PolygonCollider2D>();
                Debug.Log(
                    $"[DUST:TILE] sample='{dust.name}' layer={dust.gameObject.layer} " +
                    $"poly={(poly ? "OK" : "NULL")} poly.enabled={(poly ? poly.enabled : false)} " +
                    $"poly.usedByComposite={(poly ? poly.usedByComposite : false)} " +
                    $"poly.isTrigger={(poly ? poly.isTrigger : false)}",
                    dust
                );
            }
            else
            {
                Debug.Log("[DUST:TILE] No CosmicDust found under activeDustRoot.", this);
            }
        }
    }
#endif

}