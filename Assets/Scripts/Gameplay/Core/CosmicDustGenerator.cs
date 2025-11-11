using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

public class CosmicDustGenerator : MonoBehaviour
{
    public GameObject dustPrefab;
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
    public List<GameObject> hexagons = new List<GameObject>();
    private readonly Dictionary<Vector2Int, GameObject> _hexMap = new(); // Position → Hex
    private Dictionary<Vector2Int, bool> _fillMap = new();
    private Dictionary<Vector2Int, Coroutine> _regrowthCoroutines = new();
    private Dictionary<int, List<Vector2Int>> _featureCells = new(); // featureId -> cells
    private Dictionary<Vector2Int, int> _cellToFeature = new();     // grid -> featureId
    [SerializeField] private Color _mazeTint = new Color(0.7f, 0.7f, 0.7f, 1f);
    private Queue<int> _featureOrder = new();                       // FIFO for "oldest" removal
    private List<(Vector2Int grid, Vector3 pos)> _pendingSpawns = new();
    
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
    [SerializeField] private int poolPrewarm = 200;             // tune to your grid size
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
// direction bias per region, changes slowly over time
    private Vector2[,] flowField;
    [SerializeField] private float globalTurbulence = 0.5f;
    private float hiveTimer;
// 🔹 Hive-mind flow field
    private Vector2[,] _flowField;
    [SerializeField] private float hiveShiftInterval = 4f;  // how often the hive changes its mind
    [SerializeField] private float hiveShiftBlend    = 0.40f; // how strongly to blend to the new direction
    [SerializeField] private float baseFlowStrength  = 0.20f; // world units per second
    [SerializeField] private float phaseFlowBias     = 0.15f; // extra per-phase bias
// cache grid size so we can rebuild the field when grid changes
    private int _ffW = -1, _ffH = -1;
    [SerializeField] private float vehicleInfluenceRadius = 3.5f;
    [SerializeField] private float vehicleNudge = 0.25f; // world units per sec
    [SerializeField] private float starErodeRadius  = 1.2f;
    [SerializeField] private int   starErodePerTick = 6;   // how many tiles max per call

    public void ReactToVehicle(Vector3 vehPos, Vector2 vehDir, MusicalPhase phase)
    {
        // quick sweep of hexagons; if you track them by grid, you can cull smarter
        foreach (var hex in hexagons)
        {
            if (!hex) continue;
            float d = Vector2.Distance(vehPos, hex.transform.position);
            if (d > vehicleInfluenceRadius) continue;

            // phase logic: attract vs. repel
            Vector2 nudge =
                phase == MusicalPhase.Intensify ? -vehDir :
                phase == MusicalPhase.Release   ?  vehDir * 0.5f :
                phase == MusicalPhase.Wildcard  ? (Random.insideUnitCircle * 0.5f) :
                Vector2.zero;

            if (nudge.sqrMagnitude > 0.0001f)
                hex.transform.position += (Vector3)(nudge.normalized * vehicleNudge * Time.deltaTime);
        }
    }
    private void PrewarmPool()
    {
        if (!dustPrefab) return;
        for (int i = 0; i < poolPrewarm; i++)
        {
            var go = Instantiate(dustPrefab, new Vector3(9999,9999,0), Quaternion.identity, poolRoot);
            go.SetActive(false);
            _dustPool.Push(go);
        }
    }
    private void Start()
    {
        TryEnsureFlowField();
        PrewarmPool();
    }

// CosmicDustGenerator.cs
    private GameObject GetDustFromPool()
    {
        // Pop until we find a live object
        while (_dustPool.Count > 0)
        {
            var go = _dustPool.Pop();
            if (!go) continue; // was destroyed after being pooled

            go.SetActive(true);

            // Hard reset visuals + physics so it's never an invisible blocker
            var dust = go.GetComponent<CosmicDust>();
            if (dust == null) dust = go.AddComponent<CosmicDust>();
            dust.OnSpawnedFromPool(_mazeTint); // restores collider, layer, alpha=1, scale=full

            return go;
        }

        // None in pool: create fresh and normalize through the same path
        var created = Instantiate(dustPrefab, poolRoot);
        created.SetActive(true);

        var d = created.GetComponent<CosmicDust>();
        if (d == null) d = created.AddComponent<CosmicDust>();
        d.OnSpawnedFromPool(_mazeTint);

        return created;
    }

    private void ReturnDustToPool(GameObject go)
    {
        if (!go) return;
        go.SetActive(false);
        go.transform.SetParent(poolRoot, worldPositionStays:false);
        _dustPool.Push(go);
    }
    public void ErodeDustDisk(Vector3 centerWorld, float appetite = 1f)
    {
        if (GameFlowManager.Instance?.activeDrumTrack == null) return;
        var dt = GameFlowManager.Instance.activeDrumTrack;

        float rWorld = Mathf.Max(0.2f, starErodeRadius * appetite);
        int removed = 0;

        // scan a small neighborhood in grid space
        int W = dt.GetSpawnGridWidth(), H = dt.GetSpawnGridHeight();
        var c = dt.WorldToGridPosition(centerWorld);
        int rCells = Mathf.CeilToInt(rWorld / Mathf.Max(0.001f, dt.GetCellWorldSize()));

        for (int x = c.x - rCells; x <= c.x + rCells; x++)
        for (int y = c.y - rCells; y <= c.y + rCells; y++)
        {
            if ((uint)x >= (uint)W || (uint)y >= (uint)H) continue;
            var gp = new Vector2Int(x, y);
            if (!_hexMap.TryGetValue(gp, out var go) || !go) continue;

            Vector3 wp = dt.GridToWorldPosition(gp);
            if ((wp - centerWorld).sqrMagnitude > rWorld * rWorld) continue;

            // fade out & free the cell — reuse your existing helpers
            if (go.TryGetComponent<CosmicDust>(out var dust))
            {
                if (dust != null && dust.isActiveAndEnabled)
                    dust.StartFadeAndScaleDown(.5f);            }

            dt.FreeSpawnCell(x, y);
            DespawnDustAt(gp);
            removed++;

            if (removed >= Mathf.RoundToInt(starErodePerTick * Mathf.Clamp(appetite, 0.4f, 2f)))
                return; // throttled
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
        if (w <= 0 || h <= 0) return;

        if (_flowField == null || w != _ffW || h != _ffH)
        {
            _flowField = new Vector2[w, h];
            _ffW = w; _ffH = h;
            for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++)
                _flowField[x, y] = Random.insideUnitCircle.normalized;
        }
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
    private void ShiftFlowFieldByPhase(MusicalPhase phase)
    {
        if (_flowField == null) return;

        // Global phase bias (directional “intent”)
        Vector2 bias =
            phase == MusicalPhase.Establish  ? new Vector2( 0f, -1f) :
            phase == MusicalPhase.Evolve     ? new Vector2( 0.6f, 0f) :
            phase == MusicalPhase.Intensify  ? new Vector2( 0f,  1f) :
            phase == MusicalPhase.Release    ? new Vector2(-0.6f, 0f) :
            phase == MusicalPhase.Wildcard   ? Random.insideUnitCircle.normalized :
            /* Pop */                          new Vector2( 0f, -0.4f);

        bias = bias.normalized * phaseFlowBias;

        for (int x = 0; x < _ffW; x++)
        for (int y = 0; y < _ffH; y++)
        {
            // new target = some noise + phase bias
            Vector2 target = (Random.insideUnitCircle.normalized * 0.7f + bias).normalized;
            _flowField[x, y] = Vector2.Lerp(_flowField[x, y], target, hiveShiftBlend);
            if (_flowField[x, y].sqrMagnitude < 1e-4f) _flowField[x, y] = Vector2.up; // avoid zeros
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
        _mazeTint = profile.mazeColor;
        // Make dust appear “more/less deliberate” per personality
        // (Shorter grow-in for hectic phases; slower for patient ones.)
        hexGrowInSeconds = Mathf.Clamp(
            profile.feedsDust ? 1.1f : 0.45f,     // DarkStar-ish → slower swell
            0.15f, 2.0f);

        // Bias turbulence/lateral feel into newly spawned dust
        // (Used by CosmicDust.Begin → ConfigureForPhase + the per-hex overrides.)
        // If you want these to be stronger, thread them deeper into the dust prefab defaults.
        var wildcardish = profile.personality == PhasePersonality.Wildcard;
        var intensify   = profile.personality == PhasePersonality.Intensify;

        // Example: nudge our progressive knobs a bit
        if (wildcardish)
        {
            featureSpawnBudgetFrac = 0.16f;
            featureFadeDurationFrac = 0.20f;
        }
        else if (intensify)
        {
            featureSpawnBudgetFrac = 0.12f;
            featureFadeDurationFrac = 0.30f;
        }
        else
        {
            featureSpawnBudgetFrac = 0.12f;
            featureFadeDurationFrac = 0.25f;
        }
        regrowCooldownSeconds = Mathf.Clamp(0.6f * profile.dustRegrowDelayMul, 0.1f, 3.0f);
    }
    public void BeginStaggeredMazeRegrowth(List<(Vector2Int, Vector3)> cellsToGrow)
    {
        if (_spawnRoutine != null)
            StopCoroutine(_spawnRoutine); 
        float fit = Mathf.Clamp(GameFlowManager.Instance.activeDrumTrack.GetLoopLengthInSeconds()*0.25f, 0.08f, GameFlowManager.Instance.activeDrumTrack.GetLoopLengthInSeconds()*0.5f);
        _spawnRoutine = StartCoroutine(StaggeredGrowthFitDuration(cellsToGrow, fit));
    }
    public bool TryGetLowDensityZone(out Vector3 world, int sampleRadius = 3)
    {
        world = Vector3.zero;
        if (GameFlowManager.Instance?.activeDrumTrack == null) return false;

        var dt = GameFlowManager.Instance.activeDrumTrack;
        // Pick a few random samples; choose the one with most available cells nearby
        int bestScore = -1;
        Vector2Int bestCell = new(-1,-1);

        for (int i = 0; i < 16; i++)
        {
            var c = new Vector2Int(
                UnityEngine.Random.Range(0, dt.GetSpawnGridWidth()),
                UnityEngine.Random.Range(0, dt.GetSpawnGridHeight())
            );

            int score = 0;
            for (int dx = -sampleRadius; dx <= sampleRadius; dx++)
            for (int dy = -sampleRadius; dy <= sampleRadius; dy++)
            {
                var p = new Vector2Int(c.x + dx, c.y + dy);
                if ((uint)p.x >= (uint)dt.GetSpawnGridWidth() ||
                    (uint)p.y >= (uint)dt.GetSpawnGridHeight()) continue;
                if (dt.IsSpawnCellAvailable(p.x, p.y)) score++;
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestCell  = c;
            }
        }

        if (bestScore <= 0) return false;
        world = dt.GridToWorldPosition(bestCell);
        return true;
    }
    public void RetintExisting(float seconds = 0.35f) {
        foreach (var go in hexagons) { 
            if (!go) continue; 
            var d = go.GetComponent<CosmicDust>(); 
            if (d != null) StartCoroutine(d.RetintOver(seconds, _mazeTint));
        }
    }
    public void TriggerRegrowth(Vector2Int freedCell, MusicalPhase phase) { 
        if (_regrowthCoroutines.ContainsKey(freedCell)) return; 
        
        _regrowthCoroutines[freedCell] = StartCoroutine(RegrowElsewhere(freedCell, phase)); 
    }
    public void RemoveHex(Vector2Int gridPos) {
        _hexMap.Remove(gridPos);
    } 
    public void ClearMaze() {
        // Snapshot because RemoveActiveAt mutates _hexMap
        var snapshot = new List<KeyValuePair<Vector2Int, GameObject>>(_hexMap); 
        foreach (var kv in snapshot) { 
            // If you want a micro “poof”, replace ReturnDustToPool with a short fade.
            RemoveActiveAt(kv.Key, kv.Value, toPool: true);
        }
        hexagons.Clear(); // visual list only
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
        float regrowBudget = Mathf.Clamp(loopSeconds * 0.20f, 0.08f, loopSeconds * 0.45f);
        yield return StartCoroutine(StaggeredGrowthFitDuration(cells, regrowBudget));
    }

    public List<(Vector2Int, Vector3)> CalculateMazeGrowth(Vector2Int center, MusicalPhase phase, float hollowRadius = 0f, bool avoidStarHole = false)
    {
        switch (phase) {
            case MusicalPhase.Establish:
                return Build_CA(center, hollowRadius, avoidStarHole);
            case MusicalPhase.Evolve:
                // Carved labyrinth walls: structured, more “designed” corridors
                return CalculateCarvedMazeWalls(onScreenOnly: true, braidChance: 0.22f, corridorThickness: 1);
            case MusicalPhase.Intensify:
                // Concentric ring chokepoints (tightens play, clear choke rings)
                return Build_RingChokepoints(center, ringSpacing: 3, ringThickness: 1, jitter: 0.25f, hollowRadius, avoidStarHole);
            case MusicalPhase.Wildcard:
                // Drunken strokes (chaotic scribbles that still respect screen/occupancy)
                return Build_DrunkenStrokes(strokes: 6, maxLen: 14, stepJitter: 0.35f, dilate: 0.35f);
            case MusicalPhase.Release:
                // Open flow: mostly passages, few walls
                return CalculateCarvedMazeWalls(onScreenOnly: true, braidChance: 0.60f, corridorThickness: 2);
            case MusicalPhase.Pop:
                // Bold dotted pattern with rhythmic spacing
                return Build_PopDots(step: 3, phaseOffset: 0);
            default:
                return Build_CA(center, hollowRadius, avoidStarHole);
        }
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
        // (after you pick 'cell')
// PICK ONE PLACE to invoke, not two:
        int buildId = ++_mazeBuildId;

        if (_lastPulseId != buildId) {
            _lastPulseId = buildId;
            Debug.Log($"[MAZE] OnMazeReady firing cell={cell} (buildId={buildId})");
            OnMazeReady?.Invoke(cell);
        } else {
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
        float lastPacedAt = Time.realtimeSinceStartup;
        int i = 0;
        while (i < cells.Count)
        {
            float frameStart = Time.realtimeSinceStartup;
            float frameBudget = maxSpawnMillisPerFrame / 1000f;

            // do as much as we can this frame within budget
            while (i < cells.Count && (Time.realtimeSinceStartup - frameStart) < frameBudget)
            {
                var (grid, pos) = cells[i++];

                var hex = GetDustFromPool();
                hex.transform.SetPositionAndRotation(pos, Quaternion.identity);
                GameFlowManager.Instance.activeDrumTrack.OccupySpawnGridCell(grid.x, grid.y, GridObjectType.Dust);
                hexagons.Add(hex);

                if (hex.TryGetComponent<CosmicDust>(out var dust))
                {
                    dust.PrepareForReuse();
                    dust.SetDrumTrack(GameFlowManager.Instance.activeDrumTrack);
                    dust.SetGrowInDuration(hexGrowInSeconds);
                    dust.SetTint(_mazeTint);
                    dust.ConfigureForPhase(GameFlowManager.Instance.phaseTransitionManager.currentPhase);
                    dust.Begin();
                }
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
    private List<(Vector2Int, Vector3)> GenerateFeatureCellsForPhase(MusicalPhase phase, Vector2Int center, int featureId) {
        switch (phase) {
            case MusicalPhase.Intensify:
                // outward bands; spacing 3 keeps density reasonable
                int ringIndex = 2 + (_featureOrder.Count * 3); // 2,5,8,...
                return Build_RingBand(center, ringIndex, thickness: 1, jitterCells: 0.25f);
            case MusicalPhase.Wildcard:
                // one scribble per loop, modest length
                return Build_SingleStroke(maxLen: 12, stepJitter: 0.35f, dilate: 0.15f);
            case MusicalPhase.Pop:
                // move a dot mask over time
                int offset = (_progressiveLoop % 3); // cycles 0..2 for step=3
                return Build_PopDots(step: 3, phaseOffset: offset);
                // Evolve/Release/Establish fall back to your existing layout in RunLoopAlignedMazeCycle
            default:
                // small CA patch so even non-progressive phases can still add something
                return Build_CA(center, hollowRadius: 0f, avoidStarHole: false)
                   .GetRange(0, Mathf.Min(12, Mathf.Max(0, _pendingSpawns.Count))); // tiny sprinkle
        }
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
        DespawnDustAt(pos);
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
    private IEnumerator RegrowElsewhere(Vector2Int freedCell, MusicalPhase phase) {
        // reuse your phase-based delay so timing still feels musical
        float delay = phase switch { 
            MusicalPhase.Establish  => 8f, 
            MusicalPhase.Evolve     => 6f, 
            MusicalPhase.Intensify  => 3f, 
            MusicalPhase.Release    => 5f, 
            MusicalPhase.Wildcard   => 2.5f, 
            MusicalPhase.Pop        => 2f, 
            _ => 4f };
             yield return new WaitForSeconds(delay);
        
        //Build a fresh pattern and pick the first valid free cell that isn’t the one we freed.
        var center = GameFlowManager.Instance.activeDrumTrack.WorldToGridPosition(Vector2.zero);
        var pattern = CalculateMazeGrowth(center, phase, hollowRadius: 0f, avoidStarHole: false); // your existing routine
        foreach (var (grid, world) in pattern) {
            if (grid == freedCell) continue; 
            if (_hexMap.ContainsKey(grid)) continue;                       // already has dust
            if (!GameFlowManager.Instance.activeDrumTrack.IsSpawnCellAvailable(grid.x, grid.y)) continue;// reserved by something else
            // Spawn one dust tile and register it
            var go = GetDustFromPool();
            go.transform.SetParent(transform, worldPositionStays:false);
            go.transform.SetPositionAndRotation(world, Quaternion.identity);
            _hexMap[grid] = go; 
            hexagons.Add(go);
            GameFlowManager.Instance.activeDrumTrack.OccupySpawnGridCell(grid.x, grid.y, GridObjectType.Dust); // ✅ DUST, not Node
            if (go.TryGetComponent<CosmicDust>(out var dust)) {
                dust.SetDrumTrack(GameFlowManager.Instance.activeDrumTrack);
                dust.SetTint(_mazeTint);
                dust.Begin();
            }
            _regrowthCoroutines.Remove(freedCell);
            yield break;
        }
    } 
    public void DespawnDustAt(Vector2Int gridPos) { 
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
}
