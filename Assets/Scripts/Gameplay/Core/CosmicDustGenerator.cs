using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class CosmicDustGenerator : MonoBehaviour
{
    public DrumTrack drumTrack;
    public GameObject hexagonShieldPrefab;
// New public knobs (defaults are fast; tweak in Inspector)
    public int iterations = 3;
    public List<GameObject> hexagons = new List<GameObject>();
    private Dictionary<Vector2Int, bool> fillMap = new();
    private Dictionary<Vector2Int, GameObject> hexMap = new(); // Position → Hex
    private Dictionary<Vector2Int, Coroutine> regrowthCoroutines = new();
    private HashSet<Vector2Int> _recentSpawns = new();
    [SerializeField] private float regrowCooldownSeconds = 3.0f;
    
    private List<(Vector2Int grid, Vector3 pos)> pendingSpawns = new();
    private Coroutine spawnRoutine;
    private bool isToxic;
    private bool isSpawningMaze = true;
    // === Progressive maze knobs ===
    [SerializeField] public bool  progressiveMaze = true;
    [SerializeField] private int   maxFeatures = 3;                 // how many features we keep alive
    [SerializeField] private int   addPerLoop  = 1;                 // features to add each loop
    [SerializeField] private float featureSpawnBudgetFrac = 0.12f;  // of loop seconds, per new feature
    [SerializeField] private float featureFadeDurationFrac = 0.25f; // of loop seconds, when removing oldest
    [SerializeField] private float hexGrowInSeconds = 0.45f;        // visual “grow in” time per hex
    private float _commitCooldownUntil;
    int currentEpoch = 0;
    float epochStartTime = 0f;

// === Progressive state ===
    private int _nextFeatureId = 1;
    private Queue<int> _featureOrder = new();                       // FIFO for "oldest" removal
    private Dictionary<int, List<Vector2Int>> _featureCells = new(); // featureId -> cells
    private Dictionary<Vector2Int, int> _cellToFeature = new();     // grid -> featureId
    private MusicalPhase _progressivePhase = MusicalPhase.Establish;
    private int _progressiveLoop = 0;
    public enum MazeCycleMode {
                ClassicLoopAligned, // clear then regrow each loop (old behavior)
                Progressive,        // add/remove features over time; DON'T run classic cycle each loop
                ExternalControlled  // only run when explicitly requested by game logic
                }
   [Header("Maze Cycle Control")]
   public MazeCycleMode cycleMode = MazeCycleMode.ClassicLoopAligned;
   [Tooltip("Debounce: minimum seconds between classic cycles.")]
   public float minClassicCycleInterval = 0.25f;
   double _lastClassicCycleDSP; 
   bool _cycleRunning;

   private IEnumerator CooldownCell(Vector2Int c)
   {
       yield return new WaitForSeconds(regrowCooldownSeconds);
       _recentSpawns.Remove(c);
   }
   public void BeginRegrowWindow()
    {
        currentEpoch++;
        epochStartTime = Time.time;
    }

    public bool IsInRegrowGrace(float seconds) => Time.time - epochStartTime < seconds;
    public int GetCurrentEpoch() => currentEpoch;
    public float GetEpochAge() => Time.time - epochStartTime;
    private bool IsWorldPositionInsideScreen(Vector3 worldPos) {
        var cam = Camera.main; 
        if (!cam) return true; // no camera yet → don't cull
        Vector3 viewport = cam.WorldToViewportPoint(worldPos); 
        return viewport.x >= 0f && viewport.x <= 1f && viewport.y >= 0f && viewport.y <= 1f;
    } 
    public void BeginStaggeredMazeRegrowth(List<(Vector2Int, Vector3)> cellsToGrow)
    {
        Debug.Log($"[MAZE] Regrow START, tiles={cellsToGrow.Count}");

        if (spawnRoutine != null)
            StopCoroutine(spawnRoutine); 
        float fit = Mathf.Clamp(drumTrack.GetLoopLengthInSeconds()*0.25f, 0.08f, drumTrack.GetLoopLengthInSeconds()*0.5f);
        spawnRoutine = StartCoroutine(StaggeredGrowthFitDuration(cellsToGrow, fit));
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
    private IEnumerator StaggeredGrowthFitDuration(List<(Vector2Int, Vector3)> cells, float totalDuration)
    {
        Debug.Log($"StaggeredGrowthFitDuration: {totalDuration} spawningMaze: {isSpawningMaze}");
        int count = Mathf.Max(1, cells.Count);
        float perHexDelay = Mathf.Max(0f, totalDuration / count); // pack tightly if needed

        isSpawningMaze = true;
        Debug.Log($"cells.Count: {cells.Count}");
        
        foreach (var (grid, pos) in cells)
        {
            GameObject hex = Instantiate(hexagonShieldPrefab, pos, Quaternion.identity);
            drumTrack.OccupySpawnGridCell(grid.x, grid.y, GridObjectType.Dust);
            hexagons.Add(hex);

            if (hex.TryGetComponent<CosmicDust>(out var dust))
            {
                dust.SetEpoch(GetCurrentEpoch());
                dust.SwitchType(isToxic ? CosmicDust.CosmicDustType.Depleting : CosmicDust.CosmicDustType.Friendly);
                dust.SetColor(Color.gray); 
                dust.SetDrumTrack(drumTrack);
                dust.SetGrowInDuration(hexGrowInSeconds);    
                dust.ConfigureForPhase(drumTrack.currentPhase);
                dust.Begin();                               
            }

            RegisterHex(grid, hex);
            if (perHexDelay > 0f) yield return new WaitForSeconds(perHexDelay);
            else yield return null;
        }
        isSpawningMaze = false;
        Debug.Log($"SPawning maze false again");
    }
    public bool TryRequestLoopAlignedCycle(
                MusicalPhase phase,
                Vector2Int centerCell,
                float loopSeconds,
                float breakFrac,
                float growFrac)
        {
                if (cycleMode != MazeCycleMode.ClassicLoopAligned) return false;
                if (_cycleRunning) return false;
                double now = AudioSettings.dspTime;
                if (now - _lastClassicCycleDSP < minClassicCycleInterval) return false;
                StartCoroutine(_RunClassicCycleGuard(phase, centerCell, loopSeconds, breakFrac, growFrac));
                _lastClassicCycleDSP = now;
                return true;
            }

    IEnumerator _RunClassicCycleGuard(
                MusicalPhase phase, Vector2Int centerCell, float loopSeconds, float breakFrac, float growFrac)
        {
            _cycleRunning = true;
            try
            {
                    yield return StartCoroutine(RunLoopAlignedMazeCycle(phase, centerCell, loopSeconds, breakFrac, growFrac));
                }
            finally
            {
                    _cycleRunning = false;
                }
        }
    private void ResetProgressiveIfPhaseChanged(MusicalPhase phase)
    {
        if (_progressivePhase == phase) return;
        // clear everything from prior phase
        foreach (var kv in new Dictionary<Vector2Int, GameObject>(hexMap))
        {
            if (kv.Value != null) Destroy(kv.Value);
            drumTrack.FreeSpawnCell(kv.Key.x, kv.Key.y);
            RemoveHex(kv.Key);
        }
        _featureOrder.Clear();
        _featureCells.Clear();
        _cellToFeature.Clear();
        _progressivePhase = phase;
        _progressiveLoop = 0;
    }
    private IEnumerator ProgressiveLoopTick(
        MusicalPhase phase, Vector2Int centerCell, float loopSeconds)
    {
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
    MusicalPhase phase, Vector2Int center, int featureId)
{
    switch (phase)
    {
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
                   .GetRange(0, Mathf.Min(12, Mathf.Max(0, pendingSpawns.Count))); // tiny sprinkle
    }
}

// Single concentric band at a given hex distance (with optional jitter/thickness)
    private List<(Vector2Int, Vector3)> Build_RingBand(Vector2Int center, int distance, int thickness, float jitterCells)
{
    var growth = new List<(Vector2Int, Vector3)>();
    int W = drumTrack.GetSpawnGridWidth();
    int H = drumTrack.GetSpawnGridHeight();

    // distance map via BFS (reuse your hex neighbors)
    var dist = new Dictionary<Vector2Int, int>();
    var q = new Queue<Vector2Int>();
    q.Enqueue(center); dist[center] = 0;

    while (q.Count > 0)
    {
        var p = q.Dequeue();
        foreach (var d in GetHexDirections(p.y))
        {
            var n = p + d;
            if ((uint)n.x >= (uint)W || (uint)n.y >= (uint)H) continue;
            if (!drumTrack.IsSpawnCellAvailable(n.x, n.y)) continue;
            if (dist.ContainsKey(n)) continue;
            dist[n] = dist[p] + 1;
            q.Enqueue(n);
        }
    }

    foreach (var kv in dist)
    {
        int d = kv.Value;
        // band match with small jitter and thickness
        bool inBand = Mathf.Abs(d - distance + Random.Range(-jitterCells, jitterCells)) <= thickness * 0.5f;
        if (!inBand) continue;

        var grid  = kv.Key;
        var world = drumTrack.GridToWorldPosition(grid);
        if (!IsWorldPositionInsideScreen(world)) continue;
        growth.Add((grid, world));
    }
    return growth;
}

// One “drunken” stroke
    private List<(Vector2Int, Vector3)> Build_SingleStroke(int maxLen, float stepJitter, float dilate) {
    var pts = new HashSet<Vector2Int>();
    int W = drumTrack.GetSpawnGridWidth();
    int H = drumTrack.GetSpawnGridHeight();

    // random valid start
    Vector2Int p = new(Random.Range(0, W), Random.Range(0, H));
    int guard = 0;
    while (guard++ < 64 &&
           (!drumTrack.IsSpawnCellAvailable(p.x, p.y) ||
            !IsWorldPositionInsideScreen(drumTrack.GridToWorldPosition(p)))) {
        p = new(Random.Range(0, W), Random.Range(0, H));
    }
    if (guard >= 64) return new();

    int len = Random.Range(maxLen / 2, maxLen + 1);
    Vector2Int cur = p;
    for (int i = 0; i < len; i++)
    {
        pts.Add(cur);
        var dirs = GetHexDirections(cur.y);
        var nxt = dirs[Random.Range(0, dirs.Count)];
        if (Random.value < stepJitter) nxt = dirs[Random.Range(0, dirs.Count)];
        var n = cur + nxt;

        if ((uint)n.x >= (uint)W || (uint)n.y >= (uint)H) break;
        if (!drumTrack.IsSpawnCellAvailable(n.x, n.y)) break;
        if (!IsWorldPositionInsideScreen(drumTrack.GridToWorldPosition(n))) break;

        cur = n;
    }

    if (dilate > 0f)
    {
        var thick = new HashSet<Vector2Int>(pts);
        foreach (var c in pts)
            foreach (var n in GetHexDirections(c.y))
                if (Random.value < dilate) thick.Add(c + n);
        pts = thick;
    }

    var list = new List<(Vector2Int, Vector3)>();
    foreach (var g in pts)
        list.Add((g, drumTrack.GridToWorldPosition(g)));
    return list;
}
    private IEnumerator FadeOutFeature(int featureId, float duration) {
        if (!_featureCells.TryGetValue(featureId, out var cells)) yield break;

        foreach (var cell in cells) {
            if (hexMap.TryGetValue(cell, out var go) && go != null) {
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

    public IEnumerator RunLoopAlignedMazeCycle(MusicalPhase phase, Vector2Int centerCell, float loopSeconds, float regrowOffsetFrac, float destroySpanFrac)  {
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

    private IEnumerator BreakEntireMazeSequenced(Vector2Int origin, float totalDuration) {
    // Snapshot the current maze tiles
    var targets = new List<(Vector2Int pos, float dist, GameObject go)>();
    foreach (var kv in hexMap)
    {
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

    for (int i = 0; i < count; i++)
    {
        var (pos, _, go) = targets[i];
        if (go != null)
        {
            // Prefer a gentle fade if this tile is a CosmicDust; otherwise scale & disable.
            if (go.TryGetComponent<CosmicDust>(out var dust))
            {
                dust.StartFadeAndScaleDown(fadeDuration);
            }
            else
            {
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

    private IEnumerator RemoveMappingAfter(Vector2Int pos, float delay)
{
    yield return new WaitForSeconds(delay);
    drumTrack.FreeSpawnCell(pos.x, pos.y);
    RemoveHex(pos); // keep hexMap in sync
}

    public void RegenerateForPhase(MusicalPhaseProfile profile)
    {
        StartCoroutine(RegenerateForPhaseRoutine(profile));
    }

    private IEnumerator RegenerateForPhaseRoutine(MusicalPhaseProfile profile)
    {
        float loopSeconds = drumTrack.GetLoopLengthInSeconds();
        _commitCooldownUntil = Time.time + loopSeconds; // skip one loop of automatic cycling
        var center = new Vector2Int(drumTrack.GetSpawnGridWidth()/2, drumTrack.GetSpawnGridHeight()/2);

        // 1) Clear with a radial, musical-feel fade
        yield return StartCoroutine(BreakEntireMazeSequenced(center, Mathf.Clamp(loopSeconds * 0.35f, 0.1f, loopSeconds)));

        // 2) Grow the new pattern for this phase with a short, packed stagger
        var cells = CalculateMazeGrowth(center, profile.phase, hollowRadius: 0f, avoidStarHole: false);
        float budget = Mathf.Clamp(loopSeconds * 0.20f, 0.08f, loopSeconds * 0.45f);
        yield return StartCoroutine(StaggeredGrowthFitDuration(cells, budget));
    }

    private IEnumerator FadeTransformThenDestroy(Transform t, float duration)
{
    var s0 = t.localScale;
    var sprites = t.GetComponentsInChildren<SpriteRenderer>(includeInactive: true);
    var start = new Color[sprites.Length];
    for (int i = 0; i < sprites.Length; i++) start[i] = sprites[i].color;

    float tsec = 0f;
    while (tsec < duration)
    {
        tsec += Time.deltaTime;
        float u = Mathf.SmoothStep(0f, 1f, tsec / duration);

        t.localScale = Vector3.Lerp(s0, Vector3.zero, u);
        for (int i = 0; i < sprites.Length; i++)
        {
            if (sprites[i] == null) continue;
            var c = start[i]; c.a = Mathf.Lerp(start[i].a, 0f, u);
            sprites[i].color = c;
        }
        yield return null;
    }

    if (t != null) Destroy(t.gameObject);
}
    
    private void RegisterHex(Vector2Int gridPos, GameObject hex)
    {
        hexMap[gridPos] = hex;
    }

    public void RemoveHex(Vector2Int gridPos)
    {
        hexMap.Remove(gridPos);
    }
    // Orchestrator you can call from phase start:
    public IEnumerator GenerateMazeThenPlacePhaseStar(MusicalPhase phase, SpawnStrategyProfile profile)
    { 
        Debug.Log($"Generating Maze with Profile {profile}"); 
        // ✅ Wait until the grid and camera are actually usable
        yield return new WaitUntil(() => drumTrack != null &&
                                         drumTrack.HasSpawnGrid() &&
                                         drumTrack.GetSpawnGridWidth()  > 0 &&
                                         drumTrack.GetSpawnGridHeight() > 0 &&
                                         Camera.main != null);
        
        var center = new Vector2Int(drumTrack.GetSpawnGridWidth()/2, drumTrack.GetSpawnGridHeight()/2); 
        // Primary layout
        var walls = CalculateCarvedMazeWalls(onScreenOnly:true, braidChance:0.12f, corridorThickness:1); 
        if (walls == null || walls.Count == 0)
        { 
            Debug.LogWarning("[MAZE] Primary pattern returned 0 — retrying without on-screen cull.");
            walls = CalculateCarvedMazeWalls(onScreenOnly:false, braidChance:0.12f, corridorThickness:1);
        } 
        if (walls == null || walls.Count == 0) {
            Debug.LogWarning("[MAZE] Fallback to CA seed."); walls = Build_CA(center, hollowRadius:0f, avoidStarHole:false);
        }
        StartCoroutine(StaggeredGrowthFitDuration(walls, drumTrack.GetLoopLengthInSeconds() * 0.20f));

        // Wait until StaggeredGrowth has finished occupying cells
        yield return new WaitWhile(() => isSpawningMaze);
        yield return null; // one extra frame for Occupy() to settle

        // Try to place PhaseStar into any free cell
        Vector2Int cell = drumTrack.GetRandomAvailableCell();
        if (cell.x == -1)
            cell = ForceReserveCellNearCenter(); // fallback (below)

        if (cell.x != -1)
            drumTrack.SpawnPhaseStarAtCell(phase, profile, cell);
    }

    public void ApplyProfile(PhaseStarBehaviorProfile profile)
    {
        if (profile == null) return;
    
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
    }

    private Vector2Int ForceReserveCellNearCenter()
    {
        int w = drumTrack.GetSpawnGridWidth();
        int h = drumTrack.GetSpawnGridHeight();
        var center = new Vector2Int(w/2, h/2);

        for (int r = 0; r < Mathf.Max(w,h); r++)
        {
            for (int x = center.x - r; x <= center.x + r; x++)
            for (int y = center.y - r; y <= center.y + r; y++)
            {
                var p = new Vector2Int(Mathf.Clamp(x,0,w-1), Mathf.Clamp(y,0,h-1));
                if (hexMap.TryGetValue(p, out var hex) && hex != null)
                {
                    Destroy(hex);
                    drumTrack.FreeSpawnCell(p.x, p.y);
                    RemoveHex(p);
                    return p;
                }
            }
        }
        return new Vector2Int(-1,-1);
    }
    
    private List<(Vector2Int, Vector3)> CalculateCarvedMazeWalls(bool onScreenOnly = true, float braidChance = 0.0f, int corridorThickness = 1) { 
        var growth = new List<(Vector2Int, Vector3)>();

    int W = drumTrack.GetSpawnGridWidth();   // grid size comes from SpawnGrid
    int H = drumTrack.GetSpawnGridHeight();  // :contentReference[oaicite:3]{index=3}
    if (W <= 0 || H <= 0) { 
        Debug.LogError($"[MAZE] Invalid grid size W={W} H={H} — grid not initialized yet."); return growth; 
    }
    // 1) Candidate cells = free cells (and on-screen if requested)
    var candidates = new HashSet<Vector2Int>();
    for (int x = 0; x < W; x++)
    for (int y = 0; y < H; y++)
    {
        if (!drumTrack.IsSpawnCellAvailable(x, y)) continue;      // :contentReference[oaicite:4]{index=4}
        var c = new Vector2Int(x, y);
        if (onScreenOnly && !IsWorldPositionInsideScreen(drumTrack.GridToWorldPosition(c))) // :contentReference[oaicite:5]{index=5}
            continue;
        candidates.Add(c);
    } 
    if (candidates.Count == 0) { 
        Debug.LogWarning($"[MAZE] No candidate cells. onScreenOnly={onScreenOnly}, W={W}, H={H}. Example avail[0,0]={drumTrack.IsSpawnCellAvailable(0,0)}"); 
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
            if ((uint)n.x < (uint)W && (uint)n.y < (uint)H && candidates.Contains(n))
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
        Vector3 world = drumTrack.GridToWorldPosition(cell);       // :contentReference[oaicite:7]{index=7}
        growth.Add((cell, world));
    }
    return growth;
}
    private static Vector2Int Any(HashSet<Vector2Int> set) {
    foreach (var v in set) return v;             // returns the first enumerated element
    return new Vector2Int(-1, -1);
}

    public void GenerateDust(Vector2Int center, MusicalPhase phase, float hollowRadius = 2.5f, bool toxic = false)
    {
        isToxic = toxic;
        fillMap.Clear();
        Vector3 centerWorld = drumTrack.GridToWorldPosition(center);
        float fillChance = GetFillProbability(phase);
        int gridWidth = drumTrack.GetSpawnGridWidth();
        int gridHeight = drumTrack.GetSpawnGridHeight();

        for (int x = 0; x < gridWidth; x++)
        {
            for (int y = 0; y < gridHeight; y++)
            {
                Vector2Int pos = new(x, y);
                if (!drumTrack.IsSpawnCellAvailable(x, y)) continue;

                // Skip area near PhaseStar
                Vector3 cellWorld = drumTrack.GridToWorldPosition(pos);
                float distanceToCenter = Vector3.Distance(cellWorld, centerWorld);
                if (distanceToCenter < hollowRadius) continue;

                fillMap[pos] = Random.value < fillChance;
            }
        }


        // Run CA iterations
        for (int i = 0; i < iterations; i++)
        {
            Dictionary<Vector2Int, bool> next = new();

            foreach (var cell in fillMap.Keys)
            {
                int neighbors = CountFilledNeighbors(cell);
                bool current = fillMap[cell];

                if (current && (neighbors < 2 || neighbors > 4))
                    next[cell] = false;
                else if (!current && neighbors == 3)
                    next[cell] = true;
                else
                    next[cell] = current;
            }

            fillMap = next;
        }

// Spawn hex shields
        foreach (var kv in fillMap)
        {
            if (!kv.Value) continue;

            Vector2Int gridPos = kv.Key;

            Vector2Int relativeGridPos = gridPos - center;
            if (!IsHexInsideScreen(relativeGridPos, drumTrack.GetGridCellSize())) continue;

            Vector3 relativePos = drumTrack.HexToWorldPosition(relativeGridPos, drumTrack.GetGridCellSize());
            Vector3 worldPos = centerWorld + relativePos;

            pendingSpawns.Add((gridPos, worldPos));
        }
        if (spawnRoutine != null) StopCoroutine(spawnRoutine);
        float fit = Mathf.Clamp(drumTrack.GetLoopLengthInSeconds()*0.25f, 0.08f, drumTrack.GetLoopLengthInSeconds()*0.5f);
        spawnRoutine = StartCoroutine(StaggeredGrowthFitDuration(pendingSpawns, fit));
    }
     public IEnumerator TriggerDelayedRegrowth(Vector2Int freedCell) {
        // Small musical offset before appearing elsewhere
        yield return new WaitForSeconds(Random.Range(0.2f, 0.5f));

        // 1) Compute a fresh pattern set for the current phase (on-screen & free filtering happens downstream)
        var center = drumTrack.WorldToGridPosition(drumTrack.transform.position);
        var pattern = CalculateMazeGrowth(center, drumTrack.currentPhase, hollowRadius: 0f, avoidStarHole: false); // returns (grid, world)

        // 2) Choose a NEW cell that is:
        //    - in the pattern
        //    - not occupied (fillMap/hexMap tell you)
        //    - not the freed cell (avoid flicker)
        //    - not very recently spawned (prevents “ping-pong”)
        Vector2Int? pick = null;
        foreach (var (g, _) in pattern)
        {
            if (g == freedCell) continue;
            if (_recentSpawns.Contains(g)) continue;
            if (hexMap.ContainsKey(g)) continue;                // already has dust
            if (!drumTrack.IsSpawnCellAvailable(g.x, g.y)) continue; // reserved by nodes etc.
            pick = g;
            break;
        }

        if (pick.HasValue)
        {
            var grid = pick.Value;
            var pos  = drumTrack.GridToWorldPosition(grid);
            SpawnOneDust(grid, pos, drumTrack.currentPhase);
            _recentSpawns.Add(grid);
            StartCoroutine(CooldownCell(grid));
        }
        // If no spot available, just skip — we’ll try again on future destroys.
    }

    // Spawns a single dust hex and registers it in maps (mini version of your staggered grow)
    private void SpawnOneDust(Vector2Int grid, Vector3 worldPos, MusicalPhase phase)
    {
        if (!drumTrack.IsSpawnCellAvailable(grid.x, grid.y)) return;
        var go = Instantiate(hexagonShieldPrefab, worldPos, Quaternion.identity, transform);
        hexMap[grid] = go;
        fillMap[grid] = true;
        drumTrack.OccupySpawnGridCell(grid.x, grid.y, GridObjectType.Dust);
        if (go.TryGetComponent<CosmicDust>(out var dust))
        {
            dust.SetDrumTrack(drumTrack);
            dust.SetPhaseColor(phase);          // phase tint
            dust.SetGrowInDuration(hexGrowInSeconds);
            dust.Begin();                        // grow-in + alpha fade
        }
    }
    public void TriggerRegrowth(Vector2Int freedCell, MusicalPhase phase) { 
        if (regrowthCoroutines.ContainsKey(freedCell)) return; 
        regrowthCoroutines[freedCell] = StartCoroutine(RegrowElsewhere(freedCell, phase)); 
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
        var center = drumTrack.WorldToGridPosition(drumTrack.transform.position);
        var pattern = CalculateMazeGrowth(center, phase, hollowRadius: 0f, avoidStarHole: false); // your existing routine
        foreach (var (grid, world) in pattern) {
            if (grid == freedCell) continue; 
            if (hexMap.ContainsKey(grid)) continue;                       // already has dust
            if (!drumTrack.IsSpawnCellAvailable(grid.x, grid.y)) continue;// reserved by something else
            // Spawn one dust tile and register it
            var go = Instantiate(hexagonShieldPrefab, world, Quaternion.identity, transform); 
            hexMap[grid] = go; 
            drumTrack.OccupySpawnGridCell(grid.x, grid.y, GridObjectType.Dust); // ✅ DUST, not Node
            if (go.TryGetComponent<CosmicDust>(out var dust)) {
                dust.SetDrumTrack(drumTrack);
                dust.SetPhaseColor(phase);
                dust.Begin();
            }
            regrowthCoroutines.Remove(freedCell);
            yield break;
        }
    }
    private bool IsHexInsideScreen(Vector2Int gridPos, float cellSize, float bottomY = -4.25f, float topY = 4.25f)
    {
        float height = Mathf.Sqrt(3f) / 2f * cellSize;
        float y = gridPos.y * height + (gridPos.x % 2 == 1 ? height / 2f : 0f);
        return y >= bottomY && y <= topY;
    }

    private int CountFilledNeighbors(Vector2Int cell)
    {
        int count = 0;
        foreach (var dir in GetHexDirections(cell.y))
        {
            Vector2Int neighbor = cell + dir;
            if (fillMap.TryGetValue(neighbor, out bool filled) && filled)
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
        fillMap.Clear();

        Vector3 centerWorld = drumTrack.GridToWorldPosition(center);
        float fillChance = GetFillProbability(MusicalPhase.Establish);
        int W = drumTrack.GetSpawnGridWidth();
        int H = drumTrack.GetSpawnGridHeight();

        for (int x = 0; x < W; x++)
        for (int y = 0; y < H; y++)
        {
            var pos = new Vector2Int(x, y);
            if (!drumTrack.IsSpawnCellAvailable(x, y)) continue;

            if (avoidStarHole && hollowRadius > 0f)
            {
                float d = Vector3.Distance(drumTrack.GridToWorldPosition(pos), centerWorld);
                if (d < hollowRadius) continue;
            }

            fillMap[pos] = Random.value < fillChance;
        }

        for (int i = 0; i < iterations; i++)
        {
            var next = new Dictionary<Vector2Int, bool>();
            foreach (var cell in fillMap.Keys)
            {
                int n = CountFilledNeighbors(cell);
                bool cur = fillMap[cell];
                if (cur && (n < 2 || n > 4)) next[cell] = false;
                else if (!cur && n == 3)     next[cell] = true;
                else                         next[cell] = cur;
            }
            fillMap = next;
        }

        foreach (var kv in fillMap)
        {
            if (!kv.Value) continue;
            var grid = kv.Key;
            var world = drumTrack.GridToWorldPosition(grid);
            if (!IsWorldPositionInsideScreen(world)) continue;
            growth.Add((grid, world));
        }
        return growth;
    }
    private List<(Vector2Int, Vector3)> Build_RingChokepoints(
        Vector2Int center, int ringSpacing, int ringThickness, float jitter, float hollowRadius, bool avoidStarHole)
    {
        var growth = new List<(Vector2Int, Vector3)>();
        int W = drumTrack.GetSpawnGridWidth();
        int H = drumTrack.GetSpawnGridHeight();
        Vector3 centerW = drumTrack.GridToWorldPosition(center);

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
                if (!drumTrack.IsSpawnCellAvailable(n.x, n.y)) continue;
                if (seen.Contains(n)) continue;

                // optional star hole
                if (avoidStarHole && hollowRadius > 0f)
                {
                    float dd = Vector3.Distance(drumTrack.GridToWorldPosition(n), centerW);
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
            var world = drumTrack.GridToWorldPosition(grid);
            if (!IsWorldPositionInsideScreen(world)) continue;
            growth.Add((grid, world));
        }

        return growth;
    }
    private List<(Vector2Int, Vector3)> Build_DrunkenStrokes(int strokes, int maxLen, float stepJitter, float dilate)
    {
        var growth = new HashSet<Vector2Int>();
        int W = drumTrack.GetSpawnGridWidth();
        int H = drumTrack.GetSpawnGridHeight();

        for (int s = 0; s < strokes; s++)
        {
            // random start on-screen & available
            Vector2Int p = new(
                Random.Range(0, W),
                Random.Range(0, H)
            );
            int safety = 0;
            while (safety++ < 100 &&
                   (!drumTrack.IsSpawnCellAvailable(p.x, p.y) ||
                    !IsWorldPositionInsideScreen(drumTrack.GridToWorldPosition(p))))
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
                if (!drumTrack.IsSpawnCellAvailable(n.x, n.y)) break;
                if (!IsWorldPositionInsideScreen(drumTrack.GridToWorldPosition(n))) break;

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
            list.Add((g, drumTrack.GridToWorldPosition(g)));
        return list;
    }
    private List<(Vector2Int, Vector3)> Build_PopDots(int step, int phaseOffset)
    {
        var growth = new List<(Vector2Int, Vector3)>();
        int W = drumTrack.GetSpawnGridWidth();
        int H = drumTrack.GetSpawnGridHeight();

        for (int x = 0; x < W; x++)
        for (int y = 0; y < H; y++)
        {
            if (!drumTrack.IsSpawnCellAvailable(x, y)) continue;
            // simple periodic mask → polka dots
            if (((x + (y * 2) + phaseOffset) % step) != 0) continue;

            var grid = new Vector2Int(x, y);
            var world = drumTrack.GridToWorldPosition(grid);
            if (!IsWorldPositionInsideScreen(world)) continue;

            growth.Add((grid, world));
        }
        return growth;
    }
    
    public void ClearMaze()
    {
        foreach (var t in hexagons)
        {
            if(t == null) continue;
            Explode explode = t.GetComponent<Explode>();
            if (explode != null)
            {
                explode.Permanent();
            }
            Vector2Int gridPos = drumTrack.WorldToGridPosition(t.transform.position);
            drumTrack.FreeSpawnCell(gridPos.x, gridPos.y);
        }
        drumTrack.activeHexagons.Clear();

    }
}
