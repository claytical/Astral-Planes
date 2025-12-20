using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

public class CosmicDustGenerator : MonoBehaviour
{
    public GameObject dustPrefab;
    public Transform activeDustRoot { get; set; }

    public int iterations = 3;
    [Header("Maze Cycle Control")]
    public MazeCycleMode cycleMode = MazeCycleMode.Progressive;
    public enum MazeCycleMode {
        ClassicLoopAligned, // clear then regrow each loop (old behavior)
        Progressive,        // add/remove features over time; DON'T run classic cycle each loop
        ExternalControlled  // only run when explicitly requested by game logic
    }
    [Tooltip("Debounce: minimum seconds between classic cycles.")]
    public float minClassicCycleInterval = 0.25f;
    [Header("Vehicle erosion (temporary)")]
    [SerializeField] private float vehicleErodeRadius = 1f;   // world units
    [SerializeField] private int vehicleErodePerTick = 2;       // max cells per call
    [Header("Tile Sizing")]
    [SerializeField] private float tileDiameterWorld = 1f;          // cached from dustPrefab.hitbox
    HashSet<Vector2Int> _permanentlyClearedCells;
    Queue<Vector2Int>   _regrowQueue; // already implied by your staggered regrowth
    private readonly HashSet<Vector2Int> _starKeepClearCells = new HashSet<Vector2Int>();
    private readonly HashSet<Vector2Int> _starKeepClearPrev  = new HashSet<Vector2Int>();

// MineNode corridor regrowth on shard ejection
    private readonly Queue<List<Vector2Int>> _pendingCorridorRegrowth = new Queue<List<Vector2Int>>();

    public float TileDiameterWorld
    {
        get => tileDiameterWorld;
        set => tileDiameterWorld = value;
    }

    public List<GameObject> hexagons = new List<GameObject>();
    private readonly Dictionary<Vector2Int, GameObject> _hexMap = new(); // Position → Hex
    private Dictionary<Vector2Int, bool> _fillMap = new();
    private Dictionary<Vector2Int, Coroutine> _regrowthCoroutines = new();
    private Dictionary<int, List<Vector2Int>> _featureCells = new(); // featureId -> cells
    private Dictionary<Vector2Int, int> _cellToFeature = new();     // grid -> featureId
    [SerializeField] private Color _mazeTint = new Color(0.7f, 0.7f, 0.7f, 1f);
    private Queue<int> _featureOrder = new();                       // FIFO for "oldest" removal
    private List<(Vector2Int grid, Vector3 pos)> _pendingSpawns = new();
    private readonly HashSet<Vector2Int> _permanentClearCells = new HashSet<Vector2Int>();
    private Coroutine _spawnRoutine;
    private bool _isSpawningMaze = true, _cycleRunning = false;
    private float _commitCooldownUntil, _epochStartTime;
    private int _currentEpoch = 0, _nextFeatureId = 1, _progressiveLoop = 0;
    double _lastClassicCycleDSP; 
    private MusicalPhase _progressivePhase = MusicalPhase.Establish;
    private DrumTrack _drums;
    private int _mazeBuildId = 0;
    [SerializeField] private int flowTilesPerFrame = 256;   // tune to grid size (e.g., 32x18 grid ≈ 576 → 128–256 is good)
    private int _flowUpdateCursor = 0;
    private Vector2 _lastPhaseBias;
    private int _lastPulseId = -1;
    [SerializeField] private int poolPrewarm = 720;             // tune to your grid size
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
    private readonly HashSet<Vector2Int> _starClearCells = new HashSet<Vector2Int>();

    private void Start()
    {
        TryEnsureFlowField();
        PrewarmPool();
    }
    void Update()
    {
        // Flow-field: incremental update each frame (prevents periodic spikes)
        IncrementalFlowFieldUpdate();

        // Existing hive "intent" shift timer just changes the target bias occasionally
        hiveTimer += Time.deltaTime;
        if (hiveTimer >= hiveShiftInterval)
        {
            hiveTimer = 0f;
            _lastPhaseBias = ComputePhaseBias(GameFlowManager.Instance.phaseTransitionManager.currentPhase);
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
                        tileDiameterWorld = r * 2f;
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
        if (_drums == null)
            _drums = GameFlowManager.Instance?.activeDrumTrack ?? FindObjectOfType<DrumTrack>();
        if (_drums == null)
        {
            Debug.LogError("[MAZE] No DrumTrack available; cannot build maze.");
            yield break;
        }

        Debug.Log($"[MAZE] GenerateMazeForPhaseWithPaths START phase={phase} starCell={starCell} vehicleCount={(vehicleCells != null ? vehicleCells.Count : 0)} permCount(before grid wait)={_permanentClearCells.Count}");

        Debug.Log("[MAZE] Waiting for Spawn Grid");
        yield return new WaitUntil(() =>
            _drums.HasSpawnGrid() &&
            _drums.GetSpawnGridWidth()  > 0 &&
            _drums.GetSpawnGridHeight() > 0 &&
            Camera.main != null);
        Debug.Log("[MAZE] Spawn grid available.");

        // 1) Clear any existing dust
        Debug.Log($"[MAZE] ClearMaze() called from GenerateMazeForPhaseWithPaths; permCount BEFORE ClearMaze={_permanentClearCells.Count}");
        ClearMaze();
        Debug.Log($"[MAZE] ClearMaze() finished; permCount AFTER ClearMaze={_permanentClearCells.Count}");

        int w = _drums.GetSpawnGridWidth();
        int h = _drums.GetSpawnGridHeight();
        Debug.Log($"[MAZE] Maze grid size: {w}x{h}");

        // Build a mask of reserved cells (star + vehicles)
        var reserved = new HashSet<Vector2Int> { starCell };
        _permanentClearCells.Add(starCell);
        Debug.Log($"[MAZE] Mark starCell permanent: {starCell} permCount={_permanentClearCells.Count}");

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
                    Vector3 world = _drums.GridToWorldPosition(g);
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

        if (_drums == null) return;
        int w = _drums.GetSpawnGridWidth();
        int h = _drums.GetSpawnGridHeight();

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
    if (_drums == null)
        _drums = GameFlowManager.Instance?.activeDrumTrack ?? FindObjectOfType<DrumTrack>();
    if (_drums == null) return 0f;

    Vector2Int cell = _drums.WorldToGridPosition(worldPos);
    return HasDustAt(cell) ? 1f : 0f;
}

/// <summary>
/// Schedule regrowth back into the same cell unless it becomes permanently cleared or blocked.
/// This is your authoritative "TemporaryClear" state.
/// </summary>
public void RequestRegrowCellAt(Vector2Int gridPos, MusicalPhase phase, float delaySeconds = -1f)
{
    if (_permanentClearCells.Contains(gridPos)) return;
    if (_starKeepClearCells.Contains(gridPos)) return;
    if (_regrowthCoroutines.ContainsKey(gridPos)) return;

    float delay = delaySeconds >= 0f ? delaySeconds : phase switch
    {
        MusicalPhase.Establish  => 8f,
        MusicalPhase.Evolve     => 6f,
        MusicalPhase.Intensify  => 3f,
        MusicalPhase.Release    => 5f,
        MusicalPhase.Wildcard   => 2.5f,
        MusicalPhase.Pop        => 2f,
        _ => 4f
    };

    _regrowthCoroutines[gridPos] = StartCoroutine(RegrowCellAfterDelay(gridPos, delay));
}

private IEnumerator RegrowCellAfterDelay(Vector2Int gridPos, float delaySeconds)
{
    yield return new WaitForSeconds(delaySeconds);
    _regrowthCoroutines.Remove(gridPos);

    if (_permanentClearCells.Contains(gridPos)) yield break;
    if (_starKeepClearCells.Contains(gridPos)) yield break;
    if (_hexMap.ContainsKey(gridPos)) yield break;

    if (_drums == null)
        _drums = GameFlowManager.Instance?.activeDrumTrack ?? FindObjectOfType<DrumTrack>();
    if (_drums == null || dustPrefab == null) yield break;

    // Avoid regrowing into occupied spawn cells (MineNodes, etc.). Vehicles do not occupy the spawn grid.
    if (!_drums.IsSpawnCellAvailable(gridPos.x, gridPos.y)) yield break;

    Vector3 world = _drums.GridToWorldPosition(gridPos);

    var go = GetDustFromPool();
    go.transform.SetPositionAndRotation(world, Quaternion.identity);
    _hexMap[gridPos] = go;
    hexagons.Add(go);
    if (go.TryGetComponent<CosmicDust>(out var dust))
    {
        dust.SetDrumTrack(_drums);
        dust.SetTint(_mazeTint);
        dust.PrepareForReuse();
    }

    _fillMap[gridPos] = true;
}
/// <summary>
/// Keeps a maneuvering pocket around the PhaseStar. Cells in this set are force-cleared and excluded from regrowth.
/// Cells leaving the pocket are scheduled to regrow in-place.
/// </summary>
public void SetStarKeepClear(Vector2Int centerCell, int radiusCells, MusicalPhase phase)
{
    if (_drums == null)
        _drums = GameFlowManager.Instance?.activeDrumTrack ?? FindObjectOfType<DrumTrack>();
    if (_drums == null) return;

    _starKeepClearPrev.Clear();
    foreach (var c in _starKeepClearCells) _starKeepClearPrev.Add(c);
    _starKeepClearCells.Clear();

    int w = _drums.GetSpawnGridWidth();
    int h = _drums.GetSpawnGridHeight();
    if (w <= 0 || h <= 0) return;

    int r = Mathf.Max(0, radiusCells);
    for (int dx = -r; dx <= r; dx++)
    for (int dy = -r; dy <= r; dy++)
    {
        if (dx * dx + dy * dy > r * r) continue;
        var cell = new Vector2Int(centerCell.x + dx, centerCell.y + dy);
        if (cell.x < 0 || cell.y < 0 || cell.x >= w || cell.y >= h) continue;

        _starKeepClearCells.Add(cell);

        if (_hexMap.TryGetValue(cell, out var go))
            RemoveActiveAt(cell, go, toPool: true);

        _fillMap[cell] = false;
    }

    foreach (var prev in _starKeepClearPrev)
        if (!_starKeepClearCells.Contains(prev))
            RequestRegrowCellAt(prev, phase);
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
        if (_drums == null) return;

        int w = _drums.GetSpawnGridWidth();
        int h = _drums.GetSpawnGridHeight();

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
                Debug.Log($"[MAZE] Found Dusty Neighbor at {bestNext.x}, {bestNext.y}");

                continue;
            }
            if (foundEmptyNeighbor) { 
                visited.Add(bestEmptyNext); 
                cur = bestEmptyNext; 
                Debug.Log($"[MAZE] Found empty neighbor: {cur}");

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
        if (_drums == null)
            _drums = GameFlowManager.Instance?.activeDrumTrack ?? FindObjectOfType<DrumTrack>();
        if (_drums == null)
            return;

        int w = _drums.GetSpawnGridWidth();
        int h = _drums.GetSpawnGridHeight();

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

    public void DespawnDustAtAndMarkPermanent_Public(Vector2Int gridPos)
    {
        DespawnDustAtAndMarkPermanent(gridPos);
    }
    
    /// <summary>
    /// Called when a MineNode retires. We enqueue the corridor it carved so it can be regrown
    /// when the next MineNode spawns.
    /// </summary>
    public void EnqueueCorridorForNextNodeSpawn(IReadOnlyList<Vector2Int> carvedPath)
    {
        if (carvedPath == null || carvedPath.Count < 2) return;
        _pendingCorridorRegrowth.Enqueue(new List<Vector2Int>(carvedPath));
    }

    /// <summary>
    /// Called when a new MineNode is spawned (PhaseStar shard ejection completes).
    /// Regrows the previously-carved corridor in reverse order to create closing pockets.
    /// </summary>
    public void RegrowPreviousCorridorOnNewNodeSpawn(MusicalPhase phase)
    {
        if (_pendingCorridorRegrowth.Count == 0) return;
        var path = _pendingCorridorRegrowth.Dequeue();
        BeginCorridorRegrowthReverse(path, phase);
    }


    public void EnqueueCorridorForNextShard(IReadOnlyList<Vector2Int> carvedPath)
    {
        if (carvedPath == null || carvedPath.Count < 2) return;
        _pendingCorridorRegrowth.Enqueue(new List<Vector2Int>(carvedPath));
    }

    public void TriggerNextCorridorRegrowthOnShard(MusicalPhase phase)
    {
        if (_pendingCorridorRegrowth.Count == 0) return;
        BeginCorridorRegrowthReverse(_pendingCorridorRegrowth.Dequeue(), phase);
    }
    
    private void BeginCorridorRegrowthReverse(List<Vector2Int> carvedPath, MusicalPhase phase)
    {
        if (carvedPath == null || carvedPath.Count == 0) return;

        if (_drums == null)
            _drums = GameFlowManager.Instance?.activeDrumTrack ?? FindObjectOfType<DrumTrack>();
        if (_drums == null) return;

        var cellsToGrow = new List<(Vector2Int, Vector3)>(carvedPath.Count);

        for (int i = carvedPath.Count - 1; i >= 0; i--)
        {
            var cell = carvedPath[i];
            if (_permanentClearCells.Contains(cell)) continue;
            if (_starKeepClearCells.Contains(cell)) continue;
            if (_hexMap.ContainsKey(cell)) continue;
            if (!_drums.IsSpawnCellAvailable(cell.x, cell.y)) continue;

            cellsToGrow.Add((cell, _drums.GridToWorldPosition(cell)));
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

            if (activeDustRoot != null)
            {
                go.transform.SetParent(activeDustRoot, worldPositionStays:false);
            }
            else
            {
                go.transform.SetParent(transform, false);
            }
            go.SetActive(true);

            // Hard reset visuals + physics so it's never an invisible blocker
            var dust = go.GetComponent<CosmicDust>();
            if (dust == null) dust = go.AddComponent<CosmicDust>();
            dust.OnSpawnedFromPool(_mazeTint); // restores collider, layer, alpha=1, scale=full

            return go;
        }

        // None in pool: create fresh and normalize through the same path
        var created = Instantiate(dustPrefab, poolRoot);
        if (activeDustRoot != null)
        {
            created.transform.SetParent(activeDustRoot, worldPositionStays:false);
        }
        else
        {
            created.transform.SetParent(transform, worldPositionStays:false);
        }
        created.SetActive(true);

        var d = created.GetComponent<CosmicDust>();
        if (d == null) d = created.AddComponent<CosmicDust>();
        d.OnSpawnedFromPool(_mazeTint);

        return created;
    }
    
    private void ReturnDustToPool(GameObject go)
    {
        if (!go) return;
        // Ensure it cannot slow/block anything and cannot render particles.
        if (go.TryGetComponent<CosmicDust>(out var dust)) { 
            dust.DespawnToPoolInstant(); // includes Stop+Clear particles and disables collider
        }
        else { 
            // Fallback safety if prefab ever changes.
            var col = go.GetComponent<Collider2D>(); 
            if (col) col.enabled = false; 
            var ps = go.GetComponentInChildren<ParticleSystem>(true); 
            if (ps) { 
                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear); 
                ps.Clear(true);
            } 
            go.SetActive(false); 
        }
        var targetRoot = poolRoot != null ? poolRoot : transform;
        go.transform.SetParent(targetRoot, worldPositionStays:false);
        _dustPool.Push(go);
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

    public void ErodeDustDisk(Vector3 centerWorld, float appetite = 1f)
    {
        if (GameFlowManager.Instance?.activeDrumTrack == null) return;
        var dt = GameFlowManager.Instance.activeDrumTrack;

        float rWorld = Mathf.Max(0.2f, starErodeRadius * appetite);
        int removed = 0;

        int W = dt.GetSpawnGridWidth(), H = dt.GetSpawnGridHeight();
        var c = dt.WorldToGridPosition(centerWorld);
        float cellSize = Mathf.Max(0.001f, dt.GetCellWorldSize());
        int rCells = Mathf.CeilToInt(rWorld / cellSize);

        for (int x = c.x - rCells; x <= c.x + rCells; x++) {
            for (int y = c.y - rCells; y <= c.y + rCells; y++) {
                if (x < 0 || y < 0 || x >= W || y >= H)
                    continue;
                var gp = new Vector2Int(x, y);
            bool isPerm = _permanentClearCells.Contains(gp);
            bool hasHex = _hexMap.TryGetValue(gp, out var go);
            // 1) Never touch already-permanent cells.
            if (isPerm)
                continue;
            // 2) If we have dust registered here, despawn it
            if (hasHex)
            {
                DespawnDustAt(gp);
                removed++;
                int budget = Mathf.RoundToInt(
                    starErodePerTick * Mathf.Clamp(appetite, 0.4f, 2f));

                if (removed >= budget)
                {
                    return;
                }
            }
        }
    }
}
    public void ErodeDustDiskFromVehicle(Vector3 centerWorld, float appetite = 1f)
    {
        var gfm  = GameFlowManager.Instance;
        var drums = gfm != null ? gfm.activeDrumTrack : null;
        if (drums == null) return;

        int w = drums.GetSpawnGridWidth();
        int h = drums.GetSpawnGridHeight();
        if (w <= 0 || h <= 0) return;

        float cellSize = Mathf.Max(0.001f, drums.GetCellWorldSize());
        float rWorld   = Mathf.Max(0.1f, vehicleErodeRadius * appetite);
        int rCells     = Mathf.CeilToInt(rWorld / cellSize);

        Vector2Int centerGrid = drums.WorldToGridPosition(centerWorld);

        int removed   = 0;
        int budget    = Mathf.RoundToInt(
            vehicleErodePerTick * Mathf.Clamp(appetite, 0.4f, 2f)
        );

        for (int gx = centerGrid.x - rCells; gx <= centerGrid.x + rCells; gx++)
        {
            for (int gy = centerGrid.y - rCells; gy <= centerGrid.y + rCells; gy++)
            {
                if (gx < 0 || gy < 0 || gx >= w || gy >= h)
                    continue;

                Vector2Int gp = new Vector2Int(gx, gy);

                // Don’t touch permanent clear cells (star / maze corridors)
                if (_permanentClearCells.Contains(gp))
                    continue;

                // If there is dust here, despawn it without marking permanent
                if (_hexMap.TryGetValue(gp, out var go))
                {
                    DespawnDustAt(gp);   // ✅ TEMPORARY hole, regen can refill later
                    removed++;

                    if (removed >= budget)
                        return;
                }
            }
        }
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
        if (GameFlowManager.Instance?.activeDrumTrack == null) return;

        int w = GameFlowManager.Instance.activeDrumTrack.GetSpawnGridWidth();
        int h = GameFlowManager.Instance.activeDrumTrack.GetSpawnGridHeight();
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
        if (_flowField == null || GameFlowManager.Instance?.activeDrumTrack == null)
            return Vector2.zero;

        var grid = GameFlowManager.Instance.activeDrumTrack.WorldToGridPosition(worldPos);
        if ((uint)grid.x >= (uint)_ffW || (uint)grid.y >= (uint)_ffH) return Vector2.zero;

        return _flowField[grid.x, grid.y] * baseFlowStrength;
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
        float fit = Mathf.Clamp(GameFlowManager.Instance.activeDrumTrack.GetLoopLengthInSeconds()*0.25f, 0.08f, GameFlowManager.Instance.activeDrumTrack.GetLoopLengthInSeconds()*0.5f);
        _spawnRoutine = StartCoroutine(StaggeredGrowthFitDuration(cellsToGrow, fit));
    }

    public void RetintExisting(float seconds = 0.35f) {
        foreach (var go in hexagons) { 
            if (!go) continue; 
            var d = go.GetComponent<CosmicDust>(); 
            if (d != null) StartCoroutine(d.RetintOver(seconds, _mazeTint));
        }
    }
    public void ClearMaze() {
        Debug.Log($"[MAZE] ClearMaze() START activeDust={_hexMap.Count} permCount={_permanentClearCells.Count}");

        var snapshot = new List<KeyValuePair<Vector2Int, GameObject>>(_hexMap);
        foreach (var kv in snapshot)
        {
            RemoveActiveAt(kv.Key, kv.Value, toPool: true);
        }
        hexagons.Clear(); // visual list only

        // Reset per-phase permanent corridors / holes
        _permanentClearCells.Clear();

        Debug.Log($"[MAZE] ClearMaze() END activeDust={_hexMap.Count} permCount(afterClear)={_permanentClearCells.Count}");
    }

    private IEnumerator RunLoopAlignedMazeCycle(MusicalPhase phase, Vector2Int centerCell, float loopSeconds, float regrowOffsetFrac, float destroySpanFrac)  {
        if (Time.time < _commitCooldownUntil)
            yield break; // skip this loop’s global destroy/regrow
        if (progressiveMaze)
        {
            // optional: do a tiny progressive tick here; or do nothing at all
            // yield return StartCoroutine(ProgressiveLoopTick(...));
            yield break; // <- prevents classic destroy→regrow
        }
        // Progressive for Intensify / Wildcard / Pop
        if (phase == MusicalPhase.Intensify || phase == MusicalPhase.Wildcard || phase == MusicalPhase.Pop)
        {
            // Wait a short offset so new features don’t spawn right at beat 0
            float delay = Mathf.Clamp(loopSeconds * regrowOffsetFrac, 0f, loopSeconds * 0.5f);
            if (delay > 0f) yield return new WaitForSeconds(delay);

            // Add (and possibly fade out) features incrementally
            yield return StartCoroutine(ProgressiveLoopTick(phase, centerCell, loopSeconds));

            // NO global destroy here — features stay until bumped by maxFeatures policy
            yield break;
        }

        // --- Original non-progressive behavior for other phases ---
        float destroyDuration = Mathf.Clamp(loopSeconds * destroySpanFrac, 0.05f, loopSeconds);
        StartCoroutine(BreakEntireMazeSequenced(centerCell, destroyDuration));

        float regrowDelay = Mathf.Clamp(loopSeconds * regrowOffsetFrac, 0f, loopSeconds * 0.9f);
        yield return new WaitForSeconds(regrowDelay);

        var cells = CalculateMazeGrowth(centerCell, phase, hollowRadius: 0f, avoidStarHole: false);
        float regrowBudget = Mathf.Clamp(loopSeconds * 0.10f, 0.08f, loopSeconds * 0.25f);
        yield return StartCoroutine(StaggeredGrowthFitDuration(cells, regrowBudget));
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

    public bool TryRequestLoopAlignedCycle(MusicalPhase phase, Vector2Int centerCell, float loopSeconds, float breakFrac, float growFrac)
    { 
        if (cycleMode != MazeCycleMode.ClassicLoopAligned) return false;
        if (_cycleRunning) return false;
        _drums = GameFlowManager.Instance.activeDrumTrack;
        double now = AudioSettings.dspTime; 
        if (now - _lastClassicCycleDSP < minClassicCycleInterval) return false; 
        StartCoroutine(_RunClassicCycleGuard(phase, centerCell, loopSeconds, breakFrac, growFrac)); 
        _lastClassicCycleDSP = now; 
        return true;
    }
    public IEnumerator GenerateMazeThenPlacePhaseStar(MusicalPhase phase) { 
        if (_drums == null) 
            _drums = GameFlowManager.Instance?.activeDrumTrack ?? FindObjectOfType<DrumTrack>();
        // If we *still* don't have one, bail clearly
        if (_drums == null) {
            Debug.LogError("[MAZE] No DrumTrack available; cannot build maze or place star."); yield break;
        }
        // ✅ Wait until the grid and camera are actually usable
        yield return new WaitUntil(() => _drums.HasSpawnGrid() &&
                                         _drums.GetSpawnGridWidth()  > 0 &&
                                         _drums.GetSpawnGridHeight() > 0 &&
                                         Camera.main != null);
        var center = new Vector2Int(_drums.GetSpawnGridWidth()/2, _drums.GetSpawnGridHeight()/2);
        // Primary layout should respect the current phase personality
         var walls = CalculateMazeGrowth(center, phase, hollowRadius: 0f, avoidStarHole: false);
         if (walls == null || walls.Count == 0) { 
             Debug.LogWarning("[MAZE] Phase-specific pattern returned 0 — retrying with carved walls fallback."); 
             walls = CalculateCarvedMazeWalls(onScreenOnly:true, braidChance:0.12f, corridorThickness:1); 
             if (walls == null || walls.Count == 0) 
                 walls = CalculateCarvedMazeWalls(onScreenOnly:false, braidChance:0.12f, corridorThickness:1);
         }
        if (walls == null || walls.Count == 0) {
            Debug.LogWarning("[MAZE] Fallback to CA seed."); walls = Build_CA(center, hollowRadius:0f, avoidStarHole:false);
        }

        try
        {
            StartCoroutine(StaggeredGrowthFitDuration(walls, 1f));
        }
        catch (Exception ex) {
            Debug.LogWarning($"[MAZE] Growth exception: {ex.Message}");
        }
         // Secondary guard (just in case)
         _isSpawningMaze = false;
        yield return null;
        Vector2Int cell = _drums.GetRandomAvailableCell();
        Debug.Log($"[MAZE] OnMazeReady firing cell={cell}");
        if (cell.x == -1) cell = ForceReserveCellNearCenter(); // will bulldoze a dust hex if needed

        int buildId = ++_mazeBuildId;

        if (_lastPulseId != buildId)
        {
            _lastPulseId = buildId;
            Debug.Log($"[MAZE] OnMazeReady firing cell={cell} (buildId={buildId})");

            // Notify any listeners (analytics, etc.)
            OnMazeReady?.Invoke(cell);

            // NEW: directly request the PhaseStar from the DrumTrack.
            if (_drums != null)
            {
                _drums.RequestPhaseStar(phase, cell);
            }
            else
            {
                Debug.LogWarning("[MAZE] No DrumTrack available to RequestPhaseStar.");
            }
        }
        else
        {
            Debug.LogWarning($"[MAZE] Duplicate OnMazeReady suppressed (buildId={buildId})");
        }
    }
    private bool IsWorldPositionInsideScreen(Vector3 worldPos) {
        var cam = Camera.main; 
        if (!cam) return true; // no camera yet → don't cull
        Vector3 viewport = cam.WorldToViewportPoint(worldPos); 
        return viewport.x >= 0f && viewport.x <= 1f && viewport.y >= 0f && viewport.y <= 1f;
    } 
    private Vector2Int ForceReserveCellNearCenter()
    {
        int w = GameFlowManager.Instance.activeDrumTrack.GetSpawnGridWidth();
        int h = GameFlowManager.Instance.activeDrumTrack.GetSpawnGridHeight();
        var center = new Vector2Int(w/2, h/2);

        for (int r = 0; r < Mathf.Max(w,h); r++)
        {
            for (int x = center.x - r; x <= center.x + r; x++)
            for (int y = center.y - r; y <= center.y + r; y++)
            {
                var p = new Vector2Int(Mathf.Clamp(x,0,w-1), Mathf.Clamp(y,0,h-1));
                if (_hexMap.TryGetValue(p, out var hex) && hex != null)
                {
                    Destroy(hex);
                    GameFlowManager.Instance.activeDrumTrack.FreeSpawnCell(p.x, p.y);
                    DespawnDustAt(p);
                    return p;
                }
            }
        }
        return new Vector2Int(-1,-1);
    }
   private IEnumerator StaggeredGrowthFitDuration(List<(Vector2Int grid, Vector3 pos)> cells, float totalDuration)
{
    // Keep pacing similar, but enforce a per-frame millisecond budget
    float deadlineStep = Mathf.Max(0.0f, totalDuration / Mathf.Max(1, cells.Count));

    _isSpawningMaze = true;
    try
    {
        var gfm   = GameFlowManager.Instance;
        var drums = (_drums != null) ? _drums : (gfm != null ? gfm.activeDrumTrack : null);
        if (drums == null) drums = FindObjectOfType<DrumTrack>();
        _drums = drums;

        // We need phase for ConfigureForPhase; fall back safely.
        var ptm   = (gfm != null) ? gfm.phaseTransitionManager : null;
        var phase = (ptm != null) ? ptm.currentPhase : MusicalPhase.Establish;

        float lastPacedAt = Time.realtimeSinceStartup;
        int i = 0;

        while (i < cells.Count)
        {
            float frameStart  = Time.realtimeSinceStartup;
            float frameBudget = maxSpawnMillisPerFrame / 1000f;

            // do as much as we can this frame within budget
            while (i < cells.Count && (Time.realtimeSinceStartup - frameStart) < frameBudget)
            {
                var (grid, pos) = cells[i++];

                // ---------------------------
                // GATING (3-state model)
                // ---------------------------

                // Never place dust into permanent player corridors.
                if (_permanentClearCells.Contains(grid))
                    continue;

                // Never place dust into the star's keep-clear pocket (maneuvering space).
                if (_starKeepClearCells.Contains(grid))
                    continue;

                // Already has dust registered.
                if (_hexMap.ContainsKey(grid))
                    continue;

                // Avoid object-occupied cells (MineNodes, collectables, etc.)
                // Dust itself does NOT occupy spawnGrid in the 3-state model.
                if (gfm != null && gfm.spawnGrid != null && !gfm.spawnGrid.IsCellAvailable(grid.x, grid.y))
                    continue;

                // Must have a drum track and prefab to place dust.
                if (_drums == null || dustPrefab == null)
                    continue;

                // ---------------------------
                // SPAWN + REGISTER
                // ---------------------------
                var hex = GetDustFromPool();
                hex.transform.SetPositionAndRotation(pos, Quaternion.identity);
                hexagons.Add(hex);

                if (hex.TryGetComponent<CosmicDust>(out var dust))
                {
                    dust.PrepareForReuse();
                    dust.SetDrumTrack(_drums);
                    dust.SetGrowInDuration(hexGrowInSeconds);
                    dust.SetTint(_mazeTint);
                    dust.ConfigureForPhase(phase);
                    dust.Begin();
                }

                // This is the authoritative "DustOccupied" record.
                RegisterHex(grid, hex);
            }

            // Maintain the overall pacing without blocking the main thread for long
            float elapsedSinceLast = Time.realtimeSinceStartup - lastPacedAt;
            if (elapsedSinceLast < deadlineStep)
                yield return new WaitForSeconds(deadlineStep - elapsedSinceLast);
            else
                yield return null;

            lastPacedAt = Time.realtimeSinceStartup;
        }
    }
    finally
    {
        _isSpawningMaze = false;
    }
}

    IEnumerator _RunClassicCycleGuard(MusicalPhase phase, Vector2Int centerCell, float loopSeconds, float breakFrac, float growFrac) {
        _cycleRunning = true; 
        try { 
            yield return StartCoroutine(RunLoopAlignedMazeCycle(phase, centerCell, loopSeconds, breakFrac, growFrac));
        }finally
        { _cycleRunning = false;
        }
    }
    private void ResetProgressiveIfPhaseChanged(MusicalPhase phase)
    {
        if (_progressivePhase == phase) return;
        // clear everything from prior phase
        foreach (var kv in new Dictionary<Vector2Int, GameObject>(_hexMap))
        {
            if (kv.Value != null) Destroy(kv.Value);
            GameFlowManager.Instance.activeDrumTrack.FreeSpawnCell(kv.Key.x, kv.Key.y);
            DespawnDustAt(kv.Key);
        }
        _featureOrder.Clear();
        _featureCells.Clear();
        _cellToFeature.Clear();
        _progressivePhase = phase;
        _progressiveLoop = 0;
    }
    private void RemoveActiveAt(Vector2Int grid, GameObject go, bool toPool = true) {
        // Free grid cell by key (authoritative)
        GameFlowManager.Instance?.activeDrumTrack?.FreeSpawnCell(grid.x, grid.y); 
        // Drop from registries
        _hexMap.Remove(grid); 
        if (go) hexagons.Remove(go);
        // Pool the object
        if (toPool && go) ReturnDustToPool(go);
    }
    private IEnumerator ProgressiveLoopTick(MusicalPhase phase, Vector2Int centerCell, float loopSeconds) {
        ResetProgressiveIfPhaseChanged(phase);
        // 1) Remove oldest if adding would exceed max
        int toAdd = Mathf.Max(1, addPerLoop);
        int willBe = _featureOrder.Count + toAdd;
        while (maxFeatures > 0 && willBe > maxFeatures && _featureOrder.Count > 0)
        {
            int oldest = _featureOrder.Dequeue();
            float fade = Mathf.Clamp(loopSeconds * featureFadeDurationFrac, 0.1f, loopSeconds);
            StartCoroutine(FadeOutFeature(oldest, fade));
            willBe--;
        }

        // 2) Add new feature(s)
        for (int i = 0; i < toAdd; i++)
        {
            int id = _nextFeatureId++;
            var cells = GenerateFeatureCellsForPhase(phase, centerCell, id);
            _featureCells[id] = new List<Vector2Int>(cells.Count);
            foreach (var (grid, _) in cells)
            {
                _featureCells[id].Add(grid);
                _cellToFeature[grid] = id;
            }
            _featureOrder.Enqueue(id);

            float budget = Mathf.Clamp(loopSeconds * featureSpawnBudgetFrac, 0.08f, loopSeconds * 0.6f);
            yield return StartCoroutine(StaggeredGrowthFitDuration(cells, budget));
        }
        _progressiveLoop++;
    }
    private List<(Vector2Int, Vector3)> GenerateFeatureCellsForPhase(
        MusicalPhase phase,
        Vector2Int center,
        int featureId)
    {
        List<(Vector2Int, Vector3)> raw;

        switch (phase) {
            case MusicalPhase.Intensify:
                int ringIndex = 2 + (_featureOrder.Count * 3); // 2,5,8,...
                raw = Build_RingBand(center, ringIndex, thickness: 1, jitterCells: 0.25f);
                break;
            case MusicalPhase.Wildcard:
                raw = Build_SingleStroke(maxLen: 12, stepJitter: 0.35f, dilate: 0.15f);
                break;
            case MusicalPhase.Pop:
                int offset = (_progressiveLoop % 3);
                raw = Build_PopDots(step: 3, phaseOffset: offset);
                break;
            default:
                var ca = Build_CA(center, hollowRadius: 0f, avoidStarHole: false);
                int count = Mathf.Min(12, Mathf.Max(0, _pendingSpawns.Count));
                raw = (count > 0 && ca.Count >= count) ? ca.GetRange(0, count) : ca;
                break;
        }

        return FilterOutPermanent(raw);
    }

    private List<(Vector2Int, Vector3)> Build_RingBand(Vector2Int center, int distance, int thickness, float jitterCells) {
        var growth = new List<(Vector2Int, Vector3)>();
        int w = GameFlowManager.Instance.activeDrumTrack.GetSpawnGridWidth();
        int h = GameFlowManager.Instance.activeDrumTrack.GetSpawnGridHeight();

        // distance map via BFS (reuse your hex neighbors)
        var dist = new Dictionary<Vector2Int, int>();
        var q = new Queue<Vector2Int>();
        q.Enqueue(center); dist[center] = 0;

        while (q.Count > 0) {
            var p = q.Dequeue();
            foreach (var d in GetHexDirections(p.y)) {
                var n = p + d;
                if ((uint)n.x >= (uint)w || (uint)n.y >= (uint)h) continue;
                if (!GameFlowManager.Instance.activeDrumTrack.IsSpawnCellAvailable(n.x, n.y)) continue;
                if (dist.ContainsKey(n)) continue;
                dist[n] = dist[p] + 1;
                q.Enqueue(n);
            }
        }
        foreach (var kv in dist) {
            int d = kv.Value;
            // band match with small jitter and thickness
            bool inBand = Mathf.Abs(d - distance + Random.Range(-jitterCells, jitterCells)) <= thickness * 0.5f;
            if (!inBand) continue;
            var grid  = kv.Key;
            var world = GameFlowManager.Instance.activeDrumTrack.GridToWorldPosition(grid);
            if (!IsWorldPositionInsideScreen(world)) continue;
            growth.Add((grid, world));
        }
        return growth;
    }
    private List<(Vector2Int, Vector3)> Build_SingleStroke(int maxLen, float stepJitter, float dilate) {
        var pts = new HashSet<Vector2Int>();
        int W = GameFlowManager.Instance.activeDrumTrack.GetSpawnGridWidth();
        int H = GameFlowManager.Instance.activeDrumTrack.GetSpawnGridHeight();
        // random valid start
        Vector2Int p = new(Random.Range(0, W), Random.Range(0, H));
        int guard = 0;
        while (guard++ < 64 && (!GameFlowManager.Instance.activeDrumTrack.IsSpawnCellAvailable(p.x, p.y) || !IsWorldPositionInsideScreen(GameFlowManager.Instance.activeDrumTrack.GridToWorldPosition(p)))) {
            p = new(Random.Range(0, W), Random.Range(0, H));
        }
        if (guard >= 64) return new();

        int len = Random.Range(maxLen / 2, maxLen + 1);
        Vector2Int cur = p;
        for (int i = 0; i < len; i++) {
            pts.Add(cur);
            var dirs = GetHexDirections(cur.y);
            var nxt = dirs[Random.Range(0, dirs.Count)];
            if (Random.value < stepJitter) nxt = dirs[Random.Range(0, dirs.Count)];
            var n = cur + nxt;
            if ((uint)n.x >= (uint)W || (uint)n.y >= (uint)H) break;
            if (!GameFlowManager.Instance.activeDrumTrack.IsSpawnCellAvailable(n.x, n.y)) break;
            if (!IsWorldPositionInsideScreen(GameFlowManager.Instance.activeDrumTrack.GridToWorldPosition(n))) break;
            cur = n;
        }

        if (dilate > 0f) {
            var thick = new HashSet<Vector2Int>(pts);
            foreach (var c in pts)
                foreach (var n in GetHexDirections(c.y))
                    if (Random.value < dilate) thick.Add(c + n);
            pts = thick;
        }

        var list = new List<(Vector2Int, Vector3)>();
        foreach (var g in pts)
            list.Add((g, GameFlowManager.Instance.activeDrumTrack.GridToWorldPosition(g)));
        return list;
    }
    private IEnumerator FadeOutFeature(int featureId, float duration) {
        if (!_featureCells.TryGetValue(featureId, out var cells)) yield break;
        foreach (var cell in cells) {
            if (_hexMap.TryGetValue(cell, out var go) && go != null) {
                if (go.TryGetComponent<CosmicDust>(out var dust))
                    dust.StartFadeAndScaleDown(duration);
                else
                    StartCoroutine(FadeTransformThenDestroy(go.transform, duration)); // schedule grid/map cleanup synchronized with fade
                StartCoroutine(RemoveMappingAfter(cell, duration));
            } 
            _cellToFeature.Remove(cell); 
        } 
        _featureCells.Remove(featureId);
    }
    private IEnumerator BreakEntireMazeSequenced(Vector2Int origin, float totalDuration) {
        // Snapshot the current maze tiles
        var targets = new List<(Vector2Int pos, float dist, GameObject go)>();
        foreach (var kv in _hexMap) {
            var pos = kv.Key;
            var go  = kv.Value;
            if (go == null) continue;
            float d = Vector2Int.Distance(origin, pos);
            targets.Add((pos, d, go));
        }
        if (targets.Count == 0) yield break;
        // Radial ordering = aesthetically pleasing wave front
        targets.Sort((a, b) => a.dist.CompareTo(b.dist));

        int count = targets.Count;
        float perStep = totalDuration / count;        // spacing between starts
        float fadeDuration = perStep;                 // each fade completes before next start; last ends at totalDuration

        for (int i = 0; i < count; i++) {
            var (pos, _, go) = targets[i];
            if (go != null) {
            // Prefer a gentle fade if this tile is a CosmicDust; otherwise scale & disable.
                if (go.TryGetComponent<CosmicDust>(out var dust)) {
                    dust.StartFadeAndScaleDown(fadeDuration);
                }
                else {
                    // Fallback for non-dust: simple scale+fade via a temp coroutine
                    StartCoroutine(FadeTransformThenDestroy(go.transform, fadeDuration));
                }
                // Schedule registry cleanup to match fade completion
                StartCoroutine(RemoveMappingAfter(pos, fadeDuration));
            }
            // Evenly spaced starts; ensures the final tile finishes at exactly totalDuration.
            if (i < count - 1) yield return new WaitForSeconds(perStep);
        }
    }
    private IEnumerator RemoveMappingAfter(Vector2Int pos, float delay) {
        yield return new WaitForSeconds(delay);
        
    }
    private IEnumerator FadeTransformThenDestroy(Transform t, float duration) {
        var s0 = t.localScale;
        var sprites = t.GetComponentsInChildren<SpriteRenderer>(includeInactive: true);
        var start = new Color[sprites.Length];
        for (int i = 0; i < sprites.Length; i++) start[i] = sprites[i].color;

        float tsec = 0f;
        while (tsec < duration) {
            tsec += Time.deltaTime;
            float u = Mathf.SmoothStep(0f, 1f, tsec / duration);
            t.localScale = Vector3.Lerp(s0, Vector3.zero, u);
            for (int i = 0; i < sprites.Length; i++) {
                if (sprites[i] == null) continue;
                var c = start[i]; c.a = Mathf.Lerp(start[i].a, 0f, u);
                sprites[i].color = c;
            }
            yield return null;
        }
        if (t != null) Destroy(t.gameObject);
    }
    private void RegisterHex(Vector2Int gridPos, GameObject hex) {
        if (_hexMap.TryGetValue(gridPos, out var existing) && existing != null && existing != hex) { 
            // IMPORTANT: do NOT free the SpawnGrid cell here; we’re replacing visual content only.
            // Just retire the orphan safely.
            hexagons.Remove(existing); 
            ReturnDustToPool(existing);
        }
        _hexMap[gridPos] = hex;
    }

    private List<(Vector2Int, Vector3)> CalculateCarvedMazeWalls(bool onScreenOnly = true, float braidChance = 0.0f, int corridorThickness = 1) { 
        var growth = new List<(Vector2Int, Vector3)>();

    int w = GameFlowManager.Instance.activeDrumTrack.GetSpawnGridWidth();   // grid size comes from SpawnGrid
    int h = GameFlowManager.Instance.activeDrumTrack.GetSpawnGridHeight();  // :contentReference[oaicite:3]{index=3}
    if (w <= 0 || h <= 0) { 
        Debug.LogError($"[MAZE] Invalid grid size W={w} H={h} — grid not initialized yet."); return growth; 
    }
    // 1) Candidate cells = free cells (and on-screen if requested)
    var candidates = new HashSet<Vector2Int>();
    for (int x = 0; x < w; x++)
    for (int y = 0; y < h; y++)
    {
        if (!GameFlowManager.Instance.activeDrumTrack.IsSpawnCellAvailable(x, y)) continue;      // :contentReference[oaicite:4]{index=4}
        var c = new Vector2Int(x, y);
        if (onScreenOnly && !IsWorldPositionInsideScreen(GameFlowManager.Instance.activeDrumTrack.GridToWorldPosition(c))) // :contentReference[oaicite:5]{index=5}
            continue;
        candidates.Add(c);
    } 
    if (candidates.Count == 0) { 
        Debug.LogWarning($"[MAZE] No candidate cells. onScreenOnly={onScreenOnly}, W={w}, H={h}. Example avail[0,0]={GameFlowManager.Instance.activeDrumTrack.IsSpawnCellAvailable(0,0)}"); 
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
        Vector3 world = GameFlowManager.Instance.activeDrumTrack.GridToWorldPosition(cell);       // :contentReference[oaicite:7]{index=7}
        growth.Add((cell, world));
    }
    return growth;
}
    private static Vector2Int Any(HashSet<Vector2Int> set) {
    foreach (var v in set) return v;             // returns the first enumerated element
    return new Vector2Int(-1, -1);
}
    public void DespawnDustAt(Vector2Int gridPos) {
        bool isPermanent = _permanentClearCells.Contains(gridPos);
        

        if (_hexMap.TryGetValue(gridPos, out var go))
            RemoveActiveAt(gridPos, go, toPool: true);
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
    private float GetFillProbability(MusicalPhase phase)
    {
        return phase switch
        {
            MusicalPhase.Establish => 0.20f,
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

        Vector3 centerWorld = GameFlowManager.Instance.activeDrumTrack.GridToWorldPosition(center);
        float fillChance = GetFillProbability(MusicalPhase.Establish);
        int W = GameFlowManager.Instance.activeDrumTrack.GetSpawnGridWidth();
        int H = GameFlowManager.Instance.activeDrumTrack.GetSpawnGridHeight();

        for (int x = 0; x < W; x++)
        for (int y = 0; y < H; y++)
        {
            var pos = new Vector2Int(x, y);
            if (!GameFlowManager.Instance.activeDrumTrack.IsSpawnCellAvailable(x, y)) continue;

            if (avoidStarHole && hollowRadius > 0f)
            {
                float d = Vector3.Distance(GameFlowManager.Instance.activeDrumTrack.GridToWorldPosition(pos), centerWorld);
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
            var world = GameFlowManager.Instance.activeDrumTrack.GridToWorldPosition(grid);
            if (!IsWorldPositionInsideScreen(world)) continue;
            growth.Add((grid, world));
        }
        return growth;
    }
    private List<(Vector2Int, Vector3)> Build_RingChokepoints(Vector2Int center, int ringSpacing, int ringThickness, float jitter, float hollowRadius, bool avoidStarHole) {
        var growth = new List<(Vector2Int, Vector3)>();
        int W = GameFlowManager.Instance.activeDrumTrack.GetSpawnGridWidth();
        int H = GameFlowManager.Instance.activeDrumTrack.GetSpawnGridHeight();
        Vector3 centerW = GameFlowManager.Instance.activeDrumTrack.GridToWorldPosition(center);

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
                if (!GameFlowManager.Instance.activeDrumTrack.IsSpawnCellAvailable(n.x, n.y)) continue;
                if (seen.Contains(n)) continue;

                // optional star hole
                if (avoidStarHole && hollowRadius > 0f)
                {
                    float dd = Vector3.Distance(GameFlowManager.Instance.activeDrumTrack.GridToWorldPosition(n), centerW);
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
            var world = GameFlowManager.Instance.activeDrumTrack.GridToWorldPosition(grid);
            if (!IsWorldPositionInsideScreen(world)) continue;
            growth.Add((grid, world));
        }

        return growth;
    }
    private List<(Vector2Int, Vector3)> Build_DrunkenStrokes(int strokes, int maxLen, float stepJitter, float dilate)
    {
        var growth = new HashSet<Vector2Int>();
        int W = GameFlowManager.Instance.activeDrumTrack.GetSpawnGridWidth();
        int H = GameFlowManager.Instance.activeDrumTrack.GetSpawnGridHeight();

        for (int s = 0; s < strokes; s++)
        {
            // random start on-screen & available
            Vector2Int p = new(
                Random.Range(0, W),
                Random.Range(0, H)
            );
            int safety = 0;
            while (safety++ < 100 &&
                   (!GameFlowManager.Instance.activeDrumTrack.IsSpawnCellAvailable(p.x, p.y) ||
                    !IsWorldPositionInsideScreen(GameFlowManager.Instance.activeDrumTrack.GridToWorldPosition(p))))
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
                if (!GameFlowManager.Instance.activeDrumTrack.IsSpawnCellAvailable(n.x, n.y)) break;
                if (!IsWorldPositionInsideScreen(GameFlowManager.Instance.activeDrumTrack.GridToWorldPosition(n))) break;

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
            list.Add((g, GameFlowManager.Instance.activeDrumTrack.GridToWorldPosition(g)));
        return list;
    }
    private List<(Vector2Int, Vector3)> Build_PopDots(int step, int phaseOffset) {
        var growth = new List<(Vector2Int, Vector3)>();
        int W = GameFlowManager.Instance.activeDrumTrack.GetSpawnGridWidth();
        int H = GameFlowManager.Instance.activeDrumTrack.GetSpawnGridHeight();

        for (int x = 0; x < W; x++)
        for (int y = 0; y < H; y++)
        {
            if (!GameFlowManager.Instance.activeDrumTrack.IsSpawnCellAvailable(x, y)) continue;
            // simple periodic mask → polka dots
            if (((x + (y * 2) + phaseOffset) % step) != 0) continue;

            var grid = new Vector2Int(x, y);
            var world = GameFlowManager.Instance.activeDrumTrack.GridToWorldPosition(grid);
            if (!IsWorldPositionInsideScreen(world)) continue;

            growth.Add((grid, world));
        }
        return growth;
    }
#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        if (!Application.isPlaying) return;

        if (_drums == null)
            _drums = GameFlowManager.Instance?.activeDrumTrack ?? FindObjectOfType<DrumTrack>();
        if (_drums == null) return;

        int w = _drums.GetSpawnGridWidth();
        int h = _drums.GetSpawnGridHeight();
        if (w <= 0 || h <= 0) return;

        float cellSize = _drums.GetCellWorldSize(); // whatever your API is here

        foreach (var kvp in _hexMap)
        {
            var gp = kvp.Key;
            var world = _drums.GridToWorldPosition(gp);

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


}
