using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Gameplay.Mining;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

public static class SessionGenome
{
    public static int sessionSeed;
    public static System.Random For(string scope) =>
        new System.Random(HashCode.Combine(sessionSeed, scope.GetHashCode()));

    // Call this once per play session (e.g., GameFlowManager start)
    public static void BootNewSessionSeed(int seed)
    {
        sessionSeed = seed;
    }
}

public enum GameState { Begin, Selection, Playing, GameOver }

public class GameFlowManager : MonoBehaviour
{
    public static GameFlowManager Instance { get; private set; }

    private struct PhaseChordMap { public MusicalPhase phase; public ChordProgressionProfile progression; }

    private List<PhaseChordMap> chordMaps = new();

    [Header("Settings")]
    public List<MusicalPhaseProfile> phaseProfiles;

    public HarmonyDirector harmony;
    public InstrumentTrackController controller;
    public ChordChangeArpeggiator arp;
    public DrumTrack activeDrumTrack;
    public MineNodeProgressionManager progressionManager;
    public CosmicDustGenerator dustGenerator;
    public SpawnGrid spawnGrid;
    public NoteSetFactory noteSetFactory;
    public GameObject coralPrefab;  
    public bool disableCoralDuringBridge = true;
    public NoteVisualizer noteViz;    // assign in scene
    public GlitchManager glitch; // Assign dynamically if needed
    public PhaseTransitionManager phaseTransitionManager;
    
    public List<LocalPlayer> localPlayers = new();

    [SerializeField] private Transform playerStatsGrid; // assign via inspector if possible
    public Transform PlayerStatsGrid => playerStatsGrid;
    private bool _setupInFlight, _setupDone;
    private CoralVisualizer _coralInstance;
    private GameState currentState = GameState.Begin;
    private MusicalPhaseProfile _pendingPhaseProfile;  // optional: set when the next star is known
    private Dictionary<string, Action> sceneHandlers;
    private List<Vehicle> vehicles;
    public List<InstrumentTrack> _activeTracks = new();
    private bool _remixArmed = false;                  // true once previous star’s set is completed
    private bool _nextPhaseLoopArmed = false;
    private bool hasGameOverStarted = false;
    public int playerProgress;
    public bool IsBridgeActive => GhostCycleInProgress;
    public bool GhostCycleInProgress { get; private set; }
    public event Action<MusicalPhaseProfile> OnRemixCommitted;
    public event System.Action OnGeneratedTrackReady;

    private struct SavedTrackState {
    public InstrumentTrack track;
    public NoteBehavior originalBehavior;
    public RhythmStyle originalRhythm;
    public SavedTrackState(InstrumentTrack t, NoteBehavior b, RhythmStyle r) { track=t; originalBehavior=b; originalRhythm=r; }
}
    public GameState CurrentState
    {
        get => currentState;
        set => currentState = value;
    }
    public void JoinGame()
    {
        var intro = FindByNameIncludingInactive("IntroScreen");
        var setup = FindByNameIncludingInactive("GameSetupScreen");

        if (intro != null && intro.activeSelf)
        {
            intro.SetActive(false);
            if (setup != null)
            {
                string title = FindObjectsByType<TextMeshProUGUI>(FindObjectsSortMode.InstanceID).FirstOrDefault().text = "Select your vessel...";
                Debug.Log($"Set title to {title}");
                setup.SetActive(true);
            }
        }
    }
    public void RegisterPlayer(LocalPlayer player)
    {
        localPlayers.Add(player);
    }
    public bool ReadyToPlay()
    {
        return CurrentState == GameState.Playing && localPlayers.Count > 0;
    }
    private void SetBridgeVisualMode(bool on)
    {
        // When ON: hide gameplay visuals (maze + noteviz), coral is shown by PlayPhaseBridge.
        // When OFF: show gameplay visuals again.
        if (dustGenerator.poolRoot) dustGenerator.poolRoot.gameObject.SetActive(!on);

        if (noteViz && noteViz.GetUIParent())
            noteViz.GetUIParent().gameObject.SetActive(!on);
    }
    public void CheckAllPlayersReady()
    {
        if (localPlayers.All(p => p.IsReady))
        {
            SessionGenome.BootNewSessionSeed((int)UnityEngine.Random.Range(0, 1000f));
            StartCoroutine(TransitionToScene("GeneratedTrack"));
        }
    }
    public void StartShipSelectionPhase()
    {
        CurrentState = GameState.Selection;
        // No need to call PlayerInput.Instantiate — joining is handled by PlayerInputManager
        Debug.Log("✅ Ship selection phase started. Waiting for players to join.");
    }
    public void BeginPhaseBridge(MusicalPhase to, List<InstrumentTrack> perfectTracks, Color nextStarColor)
{
    Debug.Log($"[BRIDGE] Starting bridge for nextPhase={to.ToString() ?? "<null>"} at t={AudioSettings.dspTime:0.00}");

    var sig = BridgeLibrary.For(phaseTransitionManager.currentPhase, to);
    StartCoroutine(PlayPhaseBridge(sig, to));
}
    public void CheckAllPlayersOutOfEnergy()
    {
        if (hasGameOverStarted) return;
        if (GhostCycleInProgress) return;
        if (localPlayers.Where(p => p != null).All(p => !p.IsReady || p.GetVehicleEnergy() <= 0f))

        {
            hasGameOverStarted = true;
            StartCoroutine(HandleGameOverSequence());
        }
    }
    public void QuitToSelection()
    {
        localPlayers.Clear();
        StartCoroutine(TransitionToScene("Main"));
    }
    
    public void RegisterPlayerStatsGrid(Transform grid)
    {
        if (!grid) return;
        playerStatsGrid = grid;
        Debug.Log($"[GFM] Registered PlayerStatsGrid from scene '{grid.gameObject.scene.name}'.");
    }

    public void UnregisterPlayerStatsGrid(Transform grid)
    {
        if (playerStatsGrid == grid)
        {
            playerStatsGrid = null;
            Debug.Log("[GFM] PlayerStatsGrid unregistered (scene unload?).");
        }
    }
    private void HandleTrackSceneSetup()
    {
        if (_setupDone || _setupInFlight) return;
        StartCoroutine(HandleTrackSceneSetupAsync());
    }

    private IEnumerator HandleTrackSceneSetupAsync()
{
    _setupInFlight = true;
    vehicles.Clear();
//MusicalPhaseLibrary.i
    // Wait until the Generated Track scene is actually loaded AND
    // either (a) anchors registered, or (b) the objects exist to be found.
    float hardTimeout = 8f; // seconds
    float start = Time.time;

    // First, opportunistically fill refs if anchors weren’t used yet
    TryFillTracksBundleIfMissing();

    // Gate until everything we need is present (anchors OR finds)
    yield return new WaitUntil(() =>
        HaveAllCoreRefs() || Time.time - start > hardTimeout
    );

    // Final safety net: one last try to fill any missing refs
    TryFillTracksBundleIfMissing();

    if (!HaveAllCoreRefs())
    {
        Debug.LogError("[GFM] Track setup timed out — missing core refs." +
                       $"\nDrum:{activeDrumTrack} Ctrl:{controller} Prog:{progressionManager}" +
                       $"\nDust:{dustGenerator} Viz:{noteViz} Harm:{harmony} PTM:{phaseTransitionManager}" +
                       $"\nARP:{arp} Grid:{spawnGrid} UIGrid:{playerStatsGrid}");
        _setupInFlight = false;
        yield break;
    }

    currentState = GameState.Playing;

    // 1) Configure tracks FIRST
    controller.ConfigureTracksFromShips(
        localPlayers.Select(p => ShipMusicalProfileLoader.GetProfile(p.GetSelectedShipName())).ToList());

    // 2) Bind graph + init viz/harmony (no audio yet)
    arp.Bind(activeDrumTrack, controller);
    noteViz.Initialize();
    harmony.Initialize(activeDrumTrack, controller, arp);

    // 3) Launch players (their Launch() already waits for UI grid)
    foreach (var p in localPlayers)
    {
//        p.OnLaunched += v => { if (v) vehicles.Add(v); };
        p.Launch(); // parameterless, self-synchronizing
    }

    // 4) Start drums AFTER wiring
    activeDrumTrack.ManualStart();

    // 5) Apply profile + announce ready
    var prof = GetProfileForPhase(phaseTransitionManager.currentPhase);
    harmony.SetActiveProfile(prof, applyImmediately: true);

    OnGeneratedTrackReady?.Invoke();

    _setupDone = true;
    _setupInFlight = false;
}
    private bool HaveAllCoreRefs()
{
    return activeDrumTrack && controller && progressionManager &&
           dustGenerator && noteViz && harmony && phaseTransitionManager &&
           arp && spawnGrid && playerStatsGrid;
}
    private void TryFillTracksBundleIfMissing()
{
    // If anchors already set these, these lines do nothing.
    activeDrumTrack      = activeDrumTrack      ? activeDrumTrack      : FindFirstObjectByType<DrumTrack>(FindObjectsInactive.Include);
    controller           = controller           ? controller           : FindFirstObjectByType<InstrumentTrackController>(FindObjectsInactive.Include);
    progressionManager   = progressionManager   ? progressionManager   : FindFirstObjectByType<MineNodeProgressionManager>(FindObjectsInactive.Include);
    dustGenerator        = dustGenerator        ? dustGenerator        : FindFirstObjectByType<CosmicDustGenerator>(FindObjectsInactive.Include);
    noteViz              = noteViz              ? noteViz              : FindFirstObjectByType<NoteVisualizer>(FindObjectsInactive.Include);
    harmony              = harmony              ? harmony              : FindFirstObjectByType<HarmonyDirector>(FindObjectsInactive.Include);
    phaseTransitionManager = phaseTransitionManager ? phaseTransitionManager : FindFirstObjectByType<PhaseTransitionManager>(FindObjectsInactive.Include);
    arp                  = arp                  ? arp                  : FindFirstObjectByType<ChordChangeArpeggiator>(FindObjectsInactive.Include);
    spawnGrid            = spawnGrid            ? spawnGrid            : FindFirstObjectByType<SpawnGrid>(FindObjectsInactive.Include);

    if (!playerStatsGrid)
    {
        // Prefer anchor if you added it
        var gridAnchor = FindFirstObjectByType<PlayerStatsGridAnchor>(FindObjectsInactive.Include);
        if (gridAnchor) RegisterPlayerStatsGrid(gridAnchor.transform);
    }
}
    private void HandleTrackFinishedSceneSetup()
    {
        currentState = GameState.GameOver;
        harmony?.CancelBoostArp();
        harmony?.Initialize(null, null, null); // drops subscriptions safely
    }
    private void HandleTrackSelectionSceneSetup()
    {
        currentState = GameState.Selection;
        hasGameOverStarted = false;
        // Remove null/destroyed references first
        localPlayers = localPlayers.Where(p => p != null).ToList();

        foreach (var player in localPlayers)
        {
            player.IsReady = false;
            player.CreatePlayerSelect();
            player.SetStats();
        }
    }
    private IEnumerator HandleGameOverSequence()
    {
        InstrumentTrackController itc = FindAnyObjectByType<InstrumentTrackController>(); 
        GalaxyVisualizer galaxy = FindAnyObjectByType<GalaxyVisualizer>();
        NoteVisualizer visualizer = FindAnyObjectByType<NoteVisualizer>();
        
        itc?.BeginGameOverFade();
        
        foreach (var star in FindObjectsByType<StarTwinkle>(FindObjectsSortMode.None))
        {
            star.twinkleAmount *= 2f;
            star.twinkleSpeed *= 1.5f;
            star.transform.localScale *= 1.5f;
        }

        yield return new WaitForSeconds(2f);

        if (visualizer != null)
        {
            visualizer.GetUIParent().gameObject.SetActive(false);
        }

        if (galaxy != null && activeDrumTrack.SessionPhases != null)
        {
            foreach (var snapshot in activeDrumTrack.SessionPhases)
            {
                galaxy.AddSnapshot(snapshot);
            }
        }
        ConstellationMemoryStore.StoreSnapshot(activeDrumTrack.SessionPhases);

        yield return new WaitForSeconds(2f);

        StartCoroutine(TransitionToScene("TrackFinished"));
    }

    private IEnumerator TransitionToScene(string sceneName)
    {
        AsyncOperation load = SceneManager.LoadSceneAsync(sceneName);
        if (load != null)
        {
            load.allowSceneActivation = false;

            yield return new WaitUntil(() => load.progress >= 0.9f);
            load.allowSceneActivation = true;

            yield return new WaitUntil(() => load.isDone);
        }

        if (sceneHandlers.TryGetValue(sceneName, out var handler))
        {
            handler?.Invoke();
        }
    }
    private ChordProgressionProfile GetProfileForPhase(MusicalPhase phase)
    {
        for (int i = 0; i < chordMaps.Count; i++) if (chordMaps[i].phase == phase) return chordMaps[i].progression;
        return null;
    }
    private List<InstrumentTrack> PickSeeds(List<InstrumentTrack> pool, int n, MusicalRole[] preferred)
    {
        var list = new List<InstrumentTrack>();
        if (pool == null || pool.Count == 0 || n <= 0) return list;

        // try preferred roles first
        if (preferred != null && preferred.Length > 0)
        {
            foreach (var role in preferred)
            {
                var t = pool.Find(p => p != null && p.assignedRole == role && !list.Contains(p));
                if (t != null) { list.Add(t); if (list.Count >= n) return list; }
            }
        }
        // fill remaining
        foreach (var t in pool)
        {
            if (t != null && !list.Contains(t)) { list.Add(t); if (list.Count >= n) break; }
        }
        return list;
    }
// GameFlowManager.cs
private IEnumerator PlayPhaseBridge(PhaseBridgeSignature sig, MusicalPhase nextPhase)
{
    // ===== Lock gameplay & prep =====
    GhostCycleInProgress = true;
    FreezeGameplayForBridge();                      // despawn collectables, gate input

    var drum = activeDrumTrack;
    var viz  = noteViz;
    var ctrl = controller;

    float drumLoopSec = drum ? Mathf.Max(0.05f, drum.GetLoopLengthInSeconds()) : 4f;

    // Stage harmony for NEXT phase silently (no audible retune yet)
    var nextProf = GetProfileForPhase(nextPhase);
    harmony?.SetActiveProfile(nextProf, applyImmediately: false);

    if (sig.includeDrums && drum) drum.SetBridgeAccent(true);

    // Snapshot track list
    var allTracks = (ctrl && ctrl.tracks != null)
        ? ctrl.tracks.Where(t => t != null).ToList()
        : new List<InstrumentTrack>();

    // Utility to compute how long "one full loop" is for a set of tracks
    int ComputeLeaderMulFor(IEnumerable<InstrumentTrack> set)
    {
        int maxMul = 1;
        bool any = false;
        foreach (var t in set)
        {
            if (!t) continue;
            var notes = t.GetPersistentLoopNotes();
            if (notes != null && notes.Count > 0)
            {
                any = true;
                maxMul = Mathf.Max(maxMul, Mathf.Max(1, t.loopMultiplier));
            }
        }
        if (any) return maxMul;

        // fallbacks (same as before)
        foreach (var t in allTracks)
        {
            if (!t) continue;
            var notes = t.GetPersistentLoopNotes();
            if (notes != null && notes.Count > 0)
                maxMul = Mathf.Max(maxMul, Mathf.Max(1, t.loopMultiplier));
        }
        return Mathf.Max(1, maxMul);
    }
    float LeaderLoopSecondsFor(IEnumerable<InstrumentTrack> set) => drumLoopSec * ComputeLeaderMulFor(set);

    // ----------------------------------------------------------------------
    // A) Play the completed 4-track loop ONCE (no remix, no clearing)
    // ----------------------------------------------------------------------
    var activeNow = allTracks.Where(t => t.GetPersistentLoopNotes()?.Count > 0).ToList();
    float leaderA = LeaderLoopSecondsFor(activeNow.Count > 0 ? activeNow : allTracks);
    yield return new WaitForSeconds(leaderA);       // exactly one tour of what the player built

    // ----------------------------------------------------------------------
    // B) Drop 1–3 tracks, remix remaining into the bridge signature
    //    (Audio: clear dropped tracks + remix retained. Visuals: align grid)
    // ----------------------------------------------------------------------
    // Decide retain vs drop
    activeNow = allTracks.Where(t => t.GetPersistentLoopNotes()?.Count > 0).ToList();
    int mustRetainAtLeast = 1;                      // always leave something to remix
    int canDrop = Mathf.Clamp(activeNow.Count - mustRetainAtLeast, 0, 2);
    int dropCount = (canDrop > 0) ? UnityEngine.Random.Range(1, canDrop + 1) : 0;

    var shuffled = activeNow.OrderBy(_ => UnityEngine.Random.value).ToList();
    var toDrop   = shuffled.Take(dropCount).ToList();
    var retain   = shuffled.Skip(dropCount).ToList();

    // Remix retained set
    ctrl?.RemixAllTracksForBridge(phaseTransitionManager.currentPhase, sig);

    // Drop (audio + immediate visual cleanup)
    foreach (var t in toDrop)
    {
        // Visual flourish (optional)
        viz?.TriggerNoteBlastOff(t);
        // Audio/model clear
        t.ClearLoopedNotes(TrackClearType.Remix);
        t.ResetBinStateForNewPhase();
    }

    // Snap visual grid to retained leader so X positions match what you hear
    if (viz && drum)
    {
        int leaderMulRetained = ComputeLeaderMulFor(retain.Count > 0 ? retain : Enumerable.Empty<InstrumentTrack>());
        int leaderStepsRetained = Mathf.Max(1, leaderMulRetained) * Mathf.Max(1, drum.totalSteps);
        viz.RequestLeaderGridChange(leaderStepsRetained);
        // Note: NoteVisualizer applies pending grid changes on its next loop boundary.
    }
    SetBridgeVisualMode(true);
    // ----------------------------------------------------------------------
    // C) Play the REMIXED bridge ONCE while showing only CORAL (placeholder)
    // ----------------------------------------------------------------------
    // Hide visualizer rows, show coral placeholder
    if (viz && viz.GetUIParent()) viz.GetUIParent().gameObject.SetActive(false);

    var coral = EnsureCoral();
    if (coral != null)
    {
        coral.gameObject.SetActive(true);
        // Drive a simple placeholder "growth" animation over the bridge duration
        float bridgeOnceSec = LeaderLoopSecondsFor(retain.Count > 0 ? retain : allTracks);
        StartCoroutine(GrowCoralPlaceholder(coral, bridgeOnceSec, retain));
        yield return new WaitForSeconds(bridgeOnceSec); // play remixed bridge once
    }
    else
    {
        // No coral prefab? Still wait one retained loop with visuals hidden.
        float bridgeOnceSec = LeaderLoopSecondsFor(retain.Count > 0 ? retain : allTracks);
        yield return new WaitForSeconds(bridgeOnceSec);
    }

    // ----------------------------------------------------------------------
    // D) Clear all tracks, change drum loop & maze, spawn the next Phase Star
    // ----------------------------------------------------------------------
    var stillActive = new List<InstrumentTrack>();
    foreach (var t in allTracks)
    {
        var notes = t.GetPersistentLoopNotes();
        if (notes != null && notes.Count > 0) stillActive.Add(t);
    }
    foreach (var t in stillActive) t.ClearLoopedNotes(TrackClearType.Remix);

    // Optionally fade coral back out fast
    if (coral != null) { StartCoroutine(FadeCoralAlpha(coral, 0f, 0.35f)); }

    // Re-enable visualizer for next phase
    if (viz && viz.GetUIParent()) viz.GetUIParent().gameObject.SetActive(true);

    // Seeds / visibility for the next phase
    var seeds = PickSeeds(allTracks, sig.seedTrackCountNextPhase, sig.preferredSeedRoles);
    controller?.ApplyPhaseSeedOutcome(nextPhase, seeds);
    controller?.ApplySeedVisibility(seeds);

    if (phaseTransitionManager.currentPhase != nextPhase)
        phaseTransitionManager.HandlePhaseTransition(nextPhase, "GFM/BridgeEnd");
    SetBridgeVisualMode(false);
    ResetPhaseBinStateAndGrid();
    // Schedule drums & boot next phase’s maze/star
    activeDrumTrack?.SchedulePhaseAndLoopChange(nextPhase);
    dustGenerator.ClearMaze();
    progressionManager.BootFirstPhaseStar(nextPhase, regenerateMaze: true);

    if (sig.includeDrums && drum) drum.SetBridgeAccent(false);
    GhostCycleInProgress = false;
}
public float GetBeatInterval()
{
    var drums = activeDrumTrack;
    return drums != null ? drums.drumLoopBPM : 0.5f;
}

private void ResetPhaseBinStateAndGrid()
{
    // 1) Tracks: cursors & per-burst guards
    if (controller?.tracks != null)
        foreach (var t in controller.tracks)
            if (t) t.ResetBinStateForNewPhase();

    controller?.ResetControllerBinGuards();

    // 2) Visual grid: snap back to one-bin leader (drum bin size)
    if (activeDrumTrack && noteViz)
    {
        int leaderSteps = Mathf.Max(1, activeDrumTrack.totalSteps); // 1 bin
        noteViz.RequestLeaderGridChange(leaderSteps);
        // Let NoteVisualizer apply on its next loop boundary; grid is clean.
    }
}

// Placeholder growth: very safe (no dependency on a specific CoralVisualizer API)
private IEnumerator GrowCoralPlaceholder(CoralVisualizer cv, float seconds, List<InstrumentTrack> retained)
{
    if (cv == null) yield break;

    // Color hint: blend retained track colors, fallback to white
    Color tint = Color.white;
    if (retained != null && retained.Count > 0)
    {
        float r = 0, g = 0, b = 0;
        foreach (var t in retained) { r += t.trackColor.r; g += t.trackColor.g; b += t.trackColor.b; }
        tint = new Color(r / retained.Count, g / retained.Count, b / retained.Count, 1f);
    }

    // Try to color sprites/meshes if available
    foreach (var sr in cv.GetComponentsInChildren<SpriteRenderer>(true)) if (sr) sr.color = tint;
    foreach (var mr in cv.GetComponentsInChildren<MeshRenderer>(true))
    {
        if (!mr) continue;
        var mat = mr.material; if (mat && mat.HasProperty("_Color")) { var c = mat.color; c = Color.Lerp(c, tint, 0.8f); mat.color = c; }
    }

    // Simple scale-up over the bridge duration
    var root = cv.transform;
    Vector3 start = Vector3.zero;
    Vector3 end   = Vector3.one;
    float t2 = 0f, dur = Mathf.Max(0.05f, seconds);
    while (t2 < 1f && cv)
    {
        t2 += Time.deltaTime / dur;
        float u = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t2));
        root.localScale = Vector3.LerpUnclamped(start, end, u);
        yield return null;
    }
    if (cv) root.localScale = end;
}

    private CoralVisualizer EnsureCoral()
    {
        if (_coralInstance != null) return _coralInstance;

        if (coralPrefab == null)
        {
            Debug.LogWarning("[Bridge/Coral] No coralPrefab assigned; skipping coral.");
            return null;
        }

        var go = Instantiate(coralPrefab);
        _coralInstance = go.GetComponentInChildren<CoralVisualizer>(true);
        if (_coralInstance == null)
        {
            Debug.LogWarning("[Bridge/Coral] Coral prefab has no CoralVisualizer component (root or children). Skipping coral.");
            return null;
        }

        _coralInstance.gameObject.SetActive(false);
        return _coralInstance;
    }
    private void FreezeGameplayForBridge()
    {
        // A) Despawn/disable any existing Collectable so players can't pick them up
        foreach (var c in FindObjectsOfType<Collectable>(includeInactive: true))
        {
            // choose ONE: destroy or just disable collider+script
             Destroy(c.gameObject);

        }

        // B) Pause/disable spawners that aren’t already guarded
        // (You already guard most things with GhostCycleInProgress; this is just a belt & suspenders)
        foreach (var star in FindObjectsOfType<PhaseStar>(includeInactive: true))
            star.enabled = false; // optional if you already gate spawn/hit logic
    }

    private IEnumerator FadeCoralAlpha(CoralVisualizer cv, float target, float seconds)
{
    if (cv == null) yield break;

    var lineRs   = cv.GetComponentsInChildren<LineRenderer>(includeInactive:true);
    var meshRs   = cv.GetComponentsInChildren<MeshRenderer>(includeInactive:true);
    var spriteRs = cv.GetComponentsInChildren<SpriteRenderer>(includeInactive:true);

    var startLine  = new Dictionary<LineRenderer,(float sa,float ea)>(lineRs.Length);
    foreach (var lr in lineRs) {
        if (!lr) continue;
        startLine[lr] = (lr.startColor.a, lr.endColor.a);
    }

    var startMesh  = new Dictionary<MeshRenderer,float>(meshRs.Length);
    foreach (var mr in meshRs) {
        if (!mr) continue;
        var mat = mr.material; if (!mat || !mat.HasProperty("_Color")) continue;
        startMesh[mr] = mat.color.a;
    }

    var startSprite = new Dictionary<SpriteRenderer,float>(spriteRs.Length);
    foreach (var sr in spriteRs) {
        if (!sr) continue;
        startSprite[sr] = sr.color.a;
    }

// 2) Now do the timed fade using TryGetValue guards (your loop already does this).

    float t = 0f;
    while (t < seconds)
    {
        // if the whole coral is gone, stop gracefully
        if (cv == null) yield break;

        t += Time.deltaTime;
        float u = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / seconds));

        bool anyAlive = false;

        // Lines
        foreach (var lr in lineRs)
        {
            if (!lr) continue; // destroyed since snapshot
            if (!startLine.TryGetValue(lr, out var se)) continue;

            anyAlive = true;
            // read-modify-write guarded
            var c0 = lr.startColor; c0.a = Mathf.Lerp(se.sa, target, u);
            var c1 = lr.endColor;   c1.a = Mathf.Lerp(se.ea, target, u);
            lr.startColor = c0; lr.endColor = c1;
        }

        // Meshes
        foreach (var mr in meshRs)
        {
            if (!mr) continue;
            if (!startMesh.TryGetValue(mr, out var a0)) continue;

            var mat = mr.material;
            if (!mat) continue;
            if (!mat.HasProperty("_Color")) continue;

            anyAlive = true;
            var c = mat.color; c.a = Mathf.Lerp(a0, target, u);
            mat.color = c;
        }

        // Sprites
        foreach (var sr in spriteRs)
        {
            if (!sr) continue;
            if (!startSprite.TryGetValue(sr, out var a0)) continue;

            anyAlive = true;
            var c = sr.color; c.a = Mathf.Lerp(a0, target, u);
            sr.color = c;
        }

        // If everything was destroyed mid-fade, bail out so the bridge can continue
        if (!anyAlive) yield break;

        yield return null;
    }

    // Final snap (in case the loop exited by time) with guards
    foreach (var lr in lineRs)
    {
        if (!lr) continue;
        if (!startLine.TryGetValue(lr, out var se)) continue;
        var c0 = lr.startColor; c0.a = target;
        var c1 = lr.endColor;   c1.a = target;
        lr.startColor = c0; lr.endColor = c1;
    }
    foreach (var mr in meshRs)
    {
        if (!mr) continue;
        var mat = mr.material; if (!mat || !mat.HasProperty("_Color")) continue;
        var c = mat.color; c.a = target; mat.color = c;
    }
    foreach (var sr in spriteRs)
    {
        if (!sr) continue;
        var c = sr.color; c.a = target; sr.color = c;
    }
}
    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
            vehicles = new List<Vehicle>();
            sceneHandlers = new()
            {
                { "GeneratedTrack", HandleTrackSceneSetup },
                { "TrackFinished", HandleTrackFinishedSceneSetup },
                { "TrackSelection", HandleTrackSelectionSceneSetup },
            };
            // Ensure noteSetFactory is found
            if (noteSetFactory == null)
            {
                noteSetFactory = FindAnyObjectByType<NoteSetFactory>();
                if (noteSetFactory == null)
                    Debug.LogError("❌ NoteSetFactory could not be found in scene!");
            }

        }
        else
        {
            Destroy(gameObject);
        }
        OnRemixCommitted += _ => harmony?.CommitNextChordNow();
        MusicalPhaseLibrary.InitializeProfiles(phaseProfiles);
    }
    private GameObject FindByNameIncludingInactive(string name)
    {
        foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
        {
            var match = root.GetComponentsInChildren<Transform>(true)
                .FirstOrDefault(t => t.name == name);
            if (match != null)
                return match.gameObject;
        }

        Debug.LogWarning($"[GameFlowManager] Could not find GameObject named '{name}' (including inactive).");
        return null;
    }
    public void RegisterTracksBundle(TracksBundleAnchor a)
    {
        if (!a) return;

        phaseTransitionManager = a.phaseTransitionManager;
        activeDrumTrack        = a.drumTrack;
        controller             = a.controller;
        dustGenerator          = a.dustGenerator;
        progressionManager     = a.progressionManager;
        spawnGrid              = a.spawnGrid;          // <- now reliably assigned
        glitch                 = a.glitchManager;      // <- now reliably assigned
        arp                    = a.arp;
        Debug.Log("[GFM] Tracks bundle registered from Generated Track scene.");
    }

    public void UnregisterTracksBundle(TracksBundleAnchor a)
    {
        if (!a) return;
        // Clear refs if the same bundle unloads
        if (phaseTransitionManager == a.phaseTransitionManager) phaseTransitionManager = null;
        if (activeDrumTrack        == a.drumTrack)              activeDrumTrack        = null;
        if (controller             == a.controller)             controller             = null;
        if (dustGenerator          == a.dustGenerator)          dustGenerator          = null;
        if (progressionManager     == a.progressionManager)     progressionManager     = null;
        if (spawnGrid              == a.spawnGrid)              spawnGrid              = null;
        if (glitch                 == a.glitchManager)          glitch                 = null;
        if (arp                    == a.arp)                    arp                    = null;
    }
    
}
