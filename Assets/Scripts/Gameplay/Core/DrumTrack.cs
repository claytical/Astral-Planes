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
    public GameObject phaseStarPrefab;
    public MineNodePrefabRegistry nodePrefabRegistry;
    public MinedObjectPrefabRegistry minedObjectPrefabRegistry;
    public PhasePersonalityRegistry phasePersonalityRegistry; 
    public MusicalPhase? QueuedPhase;
    public float drumLoopBPM = 120f;
    public float gridPadding = 1f;
    public int totalSteps = 16;
    public float timingWindowSteps = 1f; // Can shrink to 0.5 or less as game progresses
    public AudioSource drumAudioSource;
    public double startDspTime;
    public List<PhaseSnapshot> SessionPhases = new();
    public List<MinedObject> activeMinedObjects = new List<MinedObject>();
    public List <MineNode> activeMineNodes = new List<MineNode>();
    public bool isPhaseStarActive;
    public int currentStep;
    public int completedLoops { get; private set; } = 0;
    private float _loopLengthInSeconds, _phaseStartTime;
    private float _gridCheckTimer;
    private readonly float _gridCheckInterval = 10f;
    private float _clipLengthSec;
    private const float kMinLen = 1e-4f; // guard for zero/denorm lengths
    private bool HasValidClipLen => _clipLengthSec > kMinLen;

    private PhaseTransitionManager _phaseTransitionManager;
    private bool _started;
    private int _lastLoopCount, _tLoop, _phaseCount;
    private AudioClip _pendingDrumLoop;
    public PhaseStar _star;
    private InstrumentTrackController _trackController;
    private int _binIdx = -1;
    private int _binCount = 4;                   // default; PhaseStar can override per-spawn
    private GameFlowManager GFM => GameFlowManager.Instance;

    public event System.Action OnLoopBoundary; // fire in LoopRoutines()
    public event System.Action<MusicalPhase, PhaseStarBehaviorProfile> OnPhaseStarSpawned;
    public event System.Action<int,int> OnBinChanged; // (idx, binCount)
    private float EffectiveLoopLengthSec => (_trackController != null) ? _trackController.GetEffectiveLoopLengthInSeconds() : _clipLengthSec;
    public float GetLoopLengthInSeconds() => EffectiveLoopLengthSec;
    public float GetClipLengthInSeconds() => _clipLengthSec; // new helper for audio-bound code
    public void SetBinCount(int bins) => _binCount = Mathf.Max(1, bins);

    void Start()
    {
        _phaseTransitionManager = GetComponent<PhaseTransitionManager>();
        _trackController = GameFlowManager.Instance.controller;
        if (drumAudioSource && drumAudioSource.clip) {
            _clipLengthSec = Mathf.Max(drumAudioSource.clip.length, 0f);
        }
    }
    private void Update()
    {
        // 0) Manager may exist but not be ready (or still wiring scenes)
        var gfm = GFM;
        if (gfm == null || !gfm.ReadyToPlay())
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

        float currentTime  = drumAudioSource.time;
        float stepDuration = _clipLengthSec / totalSteps;
        if (stepDuration <= 0f || float.IsInfinity(stepDuration))
            return;

        int absoluteStep = Mathf.FloorToInt(currentTime / stepDuration);
        currentStep = absoluteStep % totalSteps;

        // 3) Loop/bins driven by EFFECTIVE loop length
        float elapsedTime  = (float)(AudioSettings.dspTime - startDspTime);
        float effectiveLen = EffectiveLoopLengthSec;

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
                Debug.Log($"[LOOP] Extended loop {extendedLoop} (effectiveLen={effectiveLen:F2})");
                LoopRoutines();
            }
        }

        // 4) Housekeeping
        if (activeMineNodes != null)
            activeMineNodes.RemoveAll(n => n == null);
    }

public void ManualStart()
{
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
        if (_phaseTransitionManager.currentPhase != boot)
        {
            _phaseTransitionManager.HandlePhaseTransition(boot, "DrumTrack/ManualStart");
        }
    }

    var bootProfile = phasePersonalityRegistry != null ? phasePersonalityRegistry.Get(boot) : null;
    if (GameFlowManager.Instance.dustGenerator != null && bootProfile != null)
    {
        GameFlowManager.Instance.dustGenerator.ApplyProfile(bootProfile);
        GameFlowManager.Instance.dustGenerator.cycleMode     = CosmicDustGenerator.MazeCycleMode.Progressive;
        GameFlowManager.Instance.dustGenerator.progressiveMaze = true;
    }

    var initialClip = MusicalPhaseLibrary.GetRandomClip(boot);
    if (initialClip == null)
    {
        Debug.LogError("DrumTrack.ManualStart: No Establish clip found.");
        return;
    }

    // Clear any queued/scheduled transitions; we're booting the clock cleanly.
    QueuedPhase      = null;
    _pendingDrumLoop = null;

    drumAudioSource.Stop();
    drumAudioSource.clip = initialClip;
    drumAudioSource.loop = true;
    _trackController     = GameFlowManager.Instance.controller;
    _clipLengthSec       = Mathf.Max(initialClip.length, 0f);

    // Align to DSP so the transport is stable from frame 0.
    double dspStart = AudioSettings.dspTime + 0.05;
    drumAudioSource.PlayScheduled(dspStart);
    startDspTime = dspStart;

    // If InitializeDrumLoop only sets counters/grid, keep it. If it also calls PlayScheduled, remove the call below.
    StartCoroutine(InitializeDrumLoop());
    isPhaseStarActive = false;

    var dustGen = GameFlowManager.Instance?.dustGenerator;
    if (dustGen != null)
    {
        boot = MusicalPhase.Establish;
        StartCoroutine(dustGen.GenerateMazeThenPlacePhaseStar(boot));
    }
    else
    {
        // Fallback: if there is no dust generator, at least spawn a PhaseStar so progression can start.
        Debug.LogWarning("[DrumTrack] No CosmicDustGenerator; spawning Establish PhaseStar directly.");
        Vector2Int? cellHint = null;
        if (HasSpawnGrid())
        {
            cellHint = GetRandomAvailableCell();
        }
        RequestPhaseStar(boot, cellHint);
    }
}


    public void RequestPhaseStar(MusicalPhase phase, Vector2Int? cellHint = null)
    {
        Debug.Log($"[Spawn] RequestPhaseStar phase={phase} active={isPhaseStarActive} " +
                  $"hint={(cellHint.HasValue ? cellHint.Value.ToString() : "<none>")} " +
                  $"tracks={(GameFlowManager.Instance?.controller?.tracks?.Length ?? 0)} " +
                  $"prefab={(phaseStarPrefab ? "ok":"NULL")}");
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
        var gfm = GameFlowManager.Instance;
        var grid = gfm ? gfm.spawnGrid : null;
        var ctrl = gfm ? gfm.controller : null;
        if (!ctrl || ctrl.tracks == null || ctrl.tracks.Length == 0)
        {
            Debug.LogError("[Spawn] No instrument tracks available.");
            return;
        }

        // Pick a cell (prefer hint)
        Vector2Int cell = cellHint ?? (grid != null ? grid.GetRandomAvailableCell() : GetRandomAvailableCell());
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
    killer.onDestroyed += () => isPhaseStarActive = false;
    killer.onDestroyed += () => { isPhaseStarActive = false; if (grid != null) grid.FreeCell(cell.x, cell.y); };
    // Behavior profile + dust
    var profileAsset = phasePersonalityRegistry ? phasePersonalityRegistry.Get(phase) : null;
    if (gfm && gfm.dustGenerator && profileAsset) gfm.dustGenerator.ApplyProfile(profileAsset);
    if (gfm && gfm.dustGenerator) gfm.dustGenerator.RetintExisting(0.4f);

    // Targets
    IEnumerable<InstrumentTrack> targets = ctrl.tracks
        .Where(t => t != null)
        .OrderBy(_ => UnityEngine.Random.value)
        .Take(4)
        .ToList();

    // Wire star
//    _star.SetSpawnStrategyProfile(strategy);
    _star.Initialize(this, targets, profileAsset, phase);
    _star.WireBinSource(this);

    OnPhaseStarSpawned?.Invoke(phase, profileAsset);
}
    private sealed class OnDestroyRelay : MonoBehaviour
{
    public System.Action onDestroyed;
    private void OnDestroy() { try { onDestroyed?.Invoke(); } catch {} }
}
    public float TryGetBPM()
    {
        // return current BPM if known; otherwise <= 0 to signal "unknown"
        return drumLoopBPM > 0 ? drumLoopBPM : 0f;
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
    public void SchedulePhaseAndLoopChange(MusicalPhase nextPhase)
    {
        QueuedPhase = nextPhase;
        var clip = MusicalPhaseLibrary.GetRandomClip(nextPhase);
        if (clip == null)
        {
            Debug.LogWarning($"SchedulePhaseAndLoopChange: No drum loop found for phase {nextPhase}");
            return;
        }
        ScheduleDrumLoopChange(clip); 
        if (GameFlowManager.Instance.dustGenerator != null)
        {
            GameFlowManager.Instance.dustGenerator.cycleMode = CosmicDustGenerator.MazeCycleMode.Progressive;
        }
    }
    public void SetBridgeAccent(bool on)
    {
        // Simple example: LPF + lower hats when on
        // Wire into your mixer/filters as appropriate.
    }
    public void RestructureTracksWithRemixLogic()
    {
        foreach (var t in GameFlowManager.Instance.controller.tracks)
        {
            RemixTrack(t);
        }

        GameFlowManager.Instance.controller.UpdateVisualizer();
    }
    public float GetCellWorldSize()
    {
        // distance (world units) between adjacent cell centers, matching GridToWorldPosition mapping
        var a = GridToWorldPosition(new Vector2Int(0, 0));
        var b = GridToWorldPosition(new Vector2Int(1, 0));
        return Vector2.Distance(a, b);
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
    public void UnregisterMinedObject(MinedObject obj)
    {
        Debug.Log($"Removing MinedObject {obj}. Total Count: {activeMinedObjects.Count}");
        activeMinedObjects.Remove(obj);
        Debug.Log($"Mined Object Count Now: {activeMinedObjects.Count}");
    }
    private void ValidateSpawnGrid()
    {
        var gfm = GFM;
        if (gfm == null || gfm.spawnGrid == null)
            return;

        var grid = gfm.spawnGrid;

        for (int x = 0; x < grid.gridWidth; x++)
        {
            for (int y = 0; y < grid.gridHeight; y++)
            {
                if (!grid.IsCellAvailable(x, y))
                {
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
                        Debug.LogWarning($"Watchdog freed orphaned cell at {x},{y}");
                        grid.FreeCell(x, y);
                    }
                }
            }
        }
    }

    public float GetScreenWorldWidth()
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

    public float GetScreenWorldHeight()
    {
        var cam = Camera.main;
        var gfm = GFM;
        if (!cam || gfm == null || gfm.controller == null || gfm.controller.noteVisualizer == null)
        {
            Debug.LogWarning("[DrumTrack] GetScreenWorldHeight: missing Camera or controller/noteVisualizer.");
            return 0f;
        }

        float z        = -cam.transform.position.z;
        Vector3 topRight = cam.ViewportToWorldPoint(new Vector3(1f, 1f, z));
        float bottomY = gfm.controller.noteVisualizer.GetTopWorldY();

        return (topRight.y - gridPadding) - (bottomY + gridPadding);
    }

    public float GetNoteVisualizerTopY()
    {
        return GameFlowManager.Instance.controller.noteVisualizer.GetTopWorldY();
    }

    public int GetSpawnGridWidth()
    {
        return GameFlowManager.Instance.spawnGrid.gridWidth;
    }
    public bool IsSpawnCellAvailable(int x, int y)
    {
        return GameFlowManager.Instance.spawnGrid.IsCellAvailable(x, y);
    }
    public bool HasSpawnGrid()
    {
        return GameFlowManager.Instance.spawnGrid != null;
    }
    public void OccupySpawnGridCell(int x, int y, GridObjectType gridObjectType)
    {
        GameFlowManager.Instance.spawnGrid.OccupyCell(x, y, gridObjectType);
    }
    public void ResetSpawnCellBehavior(int x, int y)
    {
        GameFlowManager.Instance.spawnGrid.ResetCellBehavior(x, y);
    }
    public void FreeSpawnCell(int x, int y)
    {
        GameFlowManager.Instance.spawnGrid.FreeCell(x, y);
    }
    public Vector2Int GetRandomAvailableCell()
    {
        return GameFlowManager.Instance.spawnGrid.GetRandomAvailableCell();
    }
    public Vector3 GridToWorldPosition(Vector2Int gridPos)
    {
        var cam = Camera.main;
        var gfm = GFM;
        if (!cam || gfm == null || gfm.spawnGrid == null || gfm.controller == null || gfm.controller.noteVisualizer == null)
        {
            Debug.LogWarning("[DrumTrack] GridToWorldPosition: missing Camera, spawnGrid, or controller.");
            return Vector3.zero;
        }

        float cameraDistance = -cam.transform.position.z;

        Vector3 bottomLeft = cam.ViewportToWorldPoint(new Vector3(0, 0, cameraDistance));
        Vector3 topRight   = cam.ViewportToWorldPoint(new Vector3(1, 1, cameraDistance));

        float normalizedX = gridPos.x / (float)(gfm.spawnGrid.gridWidth  - 1);
        float normalizedY = gridPos.y / (float)(gfm.spawnGrid.gridHeight - 1);

        float worldX  = Mathf.Lerp(bottomLeft.x + gridPadding, topRight.x - gridPadding, normalizedX);
        float bottomY = gfm.controller.noteVisualizer.GetTopWorldY();
        float worldY  = Mathf.Lerp(bottomY + gridPadding, topRight.y - gridPadding, normalizedY);

        return new Vector3(worldX, worldY, 0f);
    }

    public Vector2Int WorldToGridPosition(Vector3 worldPos)
    {
        var cam = Camera.main;
        var gfm = GFM;
        if (!cam || gfm == null || gfm.spawnGrid == null)
        {
            Debug.LogWarning("[DrumTrack] WorldToGridPosition: missing Camera or spawnGrid.");
            return Vector2Int.zero;
        }

        float cameraDistance = -cam.transform.position.z;

        Vector3 bottomLeft = cam.ViewportToWorldPoint(new Vector3(0, 0, cameraDistance));
        Vector3 topRight   = cam.ViewportToWorldPoint(new Vector3(1, 1, cameraDistance));

        float normalizedX = Mathf.InverseLerp(bottomLeft.x, bottomLeft.x + (topRight.x - bottomLeft.x), worldPos.x);
        float normalizedY = Mathf.InverseLerp(bottomLeft.y, bottomLeft.y + (topRight.y - bottomLeft.y), worldPos.y);

        int gridX = Mathf.Clamp(
            Mathf.RoundToInt(normalizedX * (gfm.spawnGrid.gridWidth  - 1)),
            0, gfm.spawnGrid.gridWidth  - 1);
        int gridY = Mathf.Clamp(
            Mathf.RoundToInt(normalizedY * (gfm.spawnGrid.gridHeight - 1)),
            0, gfm.spawnGrid.gridHeight - 1);

        return new Vector2Int(gridX, gridY);
    }

    public float GetTimeToLoopEnd(bool effective = true) { 
        float L = effective ? EffectiveLoopLengthSec : _clipLengthSec; 
        if (L <= 0f) return 0f; 
        float elapsed = (float)((AudioSettings.dspTime - startDspTime) % L); 
        return Mathf.Max(0f, L - elapsed);
    }
    public int GetSpawnGridHeight()
    {
        return GameFlowManager.Instance.spawnGrid.gridHeight;
    }
    public void ClearAllActiveMineNodes()
    {
        // Destroy tracked nodes if any
        if (activeMineNodes != null)
        {
            foreach (var n in activeMineNodes.ToList())
                if (n) Destroy(n.gameObject);
            activeMineNodes.Clear();
        }

        // Belt-and-suspenders: purge any stragglers not in the list
        foreach (var node in FindObjectsByType<MineNode>(FindObjectsSortMode.None))
            if (node) Destroy(node.gameObject);
    }
    public void ClearAllActiveMinedObjects()
    {
        // Clear MinedObjects (notes, modifiers, etc.)
        foreach (MinedObject obj in activeMinedObjects.ToList())
        {
            if (obj == null) continue;
            Destroy(obj.gameObject);
        }
        activeMinedObjects.Clear();
        
        // Reset grid
        GameFlowManager.Instance.spawnGrid?.ClearAll();
        ValidateSpawnGrid();
        
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
    { if (isPhaseStarActive &&  _star.gameObject != null && !GameFlowManager.Instance.controller.AnyCollectablesInFlight())
            _star.OnLoopBoundary_RearmIfNeeded();
        Debug.Log($"Loop Routine Running: {GetLoopLengthInSeconds()}");

        // Only breathe the maze between stars (or when you explicitly want a reset)
        if (GameFlowManager.Instance.dustGenerator != null
            && !GameFlowManager.Instance.GhostCycleInProgress
            && !isPhaseStarActive)  // 👈 add this guard
        {
            float loopSeconds = _trackController.GetEffectiveLoopLengthInSeconds();
            Vector2Int centerCell = WorldToGridPosition(transform.position);
            GameFlowManager.Instance.dustGenerator.TryRequestLoopAlignedCycle(GameFlowManager.Instance.phaseTransitionManager.currentPhase, centerCell, loopSeconds, 0.25f, 0.50f);
        }
        completedLoops++;
        OnLoopBoundary?.Invoke();
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
    var gfm = GameFlowManager.Instance;
    if (gfm != null && gfm.dustGenerator != null && !gfm.GhostCycleInProgress)
    {
        var phaseForRegrowth = QueuedPhase ?? gfm.phaseTransitionManager.currentPhase;
        Vector2Int centerCell = WorldToGridPosition(transform.position);

        // Inline what MineNodeProgressionManager.GetHollowRadiusForCurrentPhase() did:
        float radius = 0f;
        if (phasePersonalityRegistry != null)
        {
            var persona = phasePersonalityRegistry.Get(phaseForRegrowth);
            if (persona != null)
                radius = Mathf.Max(0f, persona.starHoleRadius);
        }

        var growthCells = gfm.dustGenerator.CalculateMazeGrowth(centerCell, phaseForRegrowth, radius);
        gfm.dustGenerator.BeginStaggeredMazeRegrowth(growthCells);
    }

    _lastLoopCount = Mathf.FloorToInt((float)(AudioSettings.dspTime - startDspTime) / _clipLengthSec);
}

    private void RemixTrack(InstrumentTrack track)
    {
        track.ClearLoopedNotes(TrackClearType.Remix);
        var noteSet = track.GetCurrentNoteSet();
        if (noteSet == null) return;

        var profile = MusicalRoleProfileLibrary.GetProfile(track.assignedRole);
        noteSet.noteBehavior = profile.defaultBehavior;
        switch (profile.role) {
            case MusicalRole.Bass:
                noteSet.rhythmStyle = (GameFlowManager.Instance.phaseTransitionManager.currentPhase == MusicalPhase.Pop) ? RhythmStyle.FourOnTheFloor : RhythmStyle.Sparse;
                break;
            case MusicalRole.Lead:
                noteSet.rhythmStyle = RhythmStyle.Syncopated;
                break;
            case MusicalRole.Harmony:
                noteSet.chordPattern = (GameFlowManager.Instance.phaseTransitionManager.currentPhase == MusicalPhase.Intensify) ? ChordPattern.Arpeggiated : ChordPattern.RootTriad;
                break;
            case MusicalRole.Groove:
                noteSet.rhythmStyle = RhythmStyle.Dense;
                break;
        }

        noteSet.Initialize(track, track.GetTotalSteps());

        AddRandomNotes(track, noteSet, 6); // Initial remix
        if (track.GetNoteDensity() < 4)
            AddRandomNotes(track, noteSet, 4 - track.GetNoteDensity()); // Pad sparsity
    }
    public int GetLeaderSteps()
    {
        var ctrl = GameFlowManager.Instance?.controller;
        if (ctrl == null || ctrl.tracks == null || ctrl.tracks.Length == 0)
            return totalSteps;

        int maxMul = 1;
        foreach (var t in ctrl.tracks)
        {
            if (t == null) continue;
            var notes = t.GetPersistentLoopNotes();
            if (notes != null && notes.Count > 0)
                maxMul = Mathf.Max(maxMul, Mathf.Max(1, t.loopMultiplier));
        }
        return totalSteps * maxMul;
    }


    private void AddRandomNotes(InstrumentTrack track, NoteSet noteSet, int count)
    {
        var steps = noteSet.GetStepList();
        var pitches = noteSet.GetNoteList();
        if (steps.Count == 0 || pitches.Count == 0) return;

        for (int i = 0; i < count; i++)
        {
            int step = steps[Random.Range(0, steps.Count)];
            int note = noteSet.GetNextArpeggiatedNote(step);
            int duration = track.CalculateNoteDuration(step, noteSet);
            float velocity = UnityEngine.Random.Range(60f, 100f);
            track.AddNoteToLoop(step, note, duration, velocity);
        }
    }

}