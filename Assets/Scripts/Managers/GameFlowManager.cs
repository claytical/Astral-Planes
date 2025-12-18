using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Gameplay.Mining;
using TMPro;
using UnityEngine;
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
    public bool demoMode = true;
    private struct PhaseChordMap { public MusicalPhase phase; public ChordProgressionProfile progression; }

    private List<PhaseChordMap> chordMaps = new();

    [Header("Settings")]
    public List<MusicalPhaseProfile> phaseProfiles;

    public HarmonyDirector harmony;
    public InstrumentTrackController controller;
    public ChordChangeArpeggiator arp;
    public DrumTrack activeDrumTrack;
    public CosmicDustGenerator dustGenerator;
    public SpawnGrid spawnGrid;
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

    public NoteSet GenerateNotes(InstrumentTrack track)
    {
        var ns = phaseTransitionManager.noteSetFactory.Generate(track, phaseTransitionManager.currentMotif);
        return ns;
    }
    void Start()
    {
        sceneHandlers = new()
        {
            // { "GeneratedTrack", HandleTrackSceneSetup },  // remove this
            { "TrackFinished", HandleTrackFinishedSceneSetup },
            { "TrackSelection", HandleTrackSelectionSceneSetup },
        };

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
        Debug.Log($"[GFM] QuitToSelection: destroying players and resetting state (demoMode={demoMode})");

        // 1) Destroy all LocalPlayer instances so input & state are fully reset.
        if (localPlayers != null && localPlayers.Count > 0)
        {
            foreach (var lp in localPlayers)
            {
                if (lp == null) continue;

                var go = lp.gameObject;
                Debug.Log($"[GFM] Destroying LocalPlayer GameObject '{go.name}'");
                Destroy(go);
            }
            localPlayers.Clear();
        }

        // 2) Clear any per-run collections.
        vehicles?.Clear();
        _activeTracks?.Clear();

        // 3) Reset run-specific flags / state so next run starts clean.
        hasGameOverStarted   = false;
        GhostCycleInProgress = false;
        _setupDone           = false;
        _setupInFlight       = false;
        currentState         = GameState.Begin;

        // 4) Drop scene-local references so the next GeneratedTrack must re-register.
        phaseTransitionManager = null;
        activeDrumTrack        = null;
        controller             = null;
        dustGenerator          = null;
        spawnGrid              = null;
        glitch                 = null;
        arp                    = null;
        playerStatsGrid        = null;

        // 5) Load Main (Intro/Selection) scene.
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
private IEnumerator HandleTrackSceneSetupAsync()
{
    _setupInFlight = true;
    vehicles.Clear();

    var activeScene = SceneManager.GetActiveScene().name;
    if (activeScene != "GeneratedTrack")
    {
        Debug.LogWarning($"[GFM] HandleTrackSceneSetupAsync invoked but active scene is '{activeScene}'. Aborting.");
        _setupInFlight = false;
        yield break;
    }

    // Filter out destroyed players
    localPlayers = localPlayers.Where(p => p != null).ToList();
    if (localPlayers.Count == 0)
    {
        Debug.LogError("[GFM] No local players available during GeneratedTrack setup.");
        _setupInFlight = false;
        yield break;
    }

    // --- GATING / CORE REFS ---

    float hardTimeout = 8f;
    float startTime = Time.time;

    Debug.Log("[GFM] [SETUP] Begin. Trying initial TryFillTracksBundleIfMissing.");
    TryFillTracksBundleIfMissing();

    yield return new WaitUntil(() =>
        HaveAllCoreRefs() || Time.time - startTime > hardTimeout
    );

    // final attempt
    TryFillTracksBundleIfMissing();

    if (!HaveAllCoreRefs())
    {
        Debug.LogError("[GFM] Track setup timed out — missing core refs." +
                       $"\nDrum:{activeDrumTrack} Ctrl:{controller} " +
                       $"\nDust:{dustGenerator} Viz:{noteViz} Harm:{harmony} PTM:{phaseTransitionManager}" +
                       $"\nARP:{arp} Grid:{spawnGrid} UIGrid:{playerStatsGrid}");
        _setupInFlight = false;
        yield break;
    }

    Debug.Log("[GFM] [SETUP] Core refs present." +
              $"\n  Drum: {activeDrumTrack}" +
              $"\n  Ctrl: {controller}" +
              $"\n  Viz : {noteViz}" +
              $"\n  Harm: {harmony}" +
              $"\n  ARP : {arp}" +
              $"\n  PTM : {phaseTransitionManager}" +
              $"\n  Grid: {spawnGrid}" +
              $"\n  UI  : {playerStatsGrid}");

    currentState = GameState.Playing;

    // Precompute ship profiles for logging
    var shipProfiles = localPlayers
        .Select(p => ShipMusicalProfileLoader.GetProfile(p.GetSelectedShipName()))
        .ToList();

    Debug.Log("[GFM] [SETUP] Ship profiles resolved: " +
              string.Join(", ", shipProfiles.Select(sp => sp ? sp.name : "<null>")));

    // --------------------
    // STEP 1: Configure tracks
    // --------------------
    Debug.Log("[GFM] [STEP 1] ConfigureTracksFromShips BEGIN");
    controller.ConfigureTracksFromShips(shipProfiles);
    Debug.Log("[GFM] [STEP 1] ConfigureTracksFromShips END");

    // --------------------
    // STEP 2: Bind graph + init viz/harmony
    // --------------------
    Debug.Log("[GFM] [STEP 2] Bind ARP + init NoteViz/Harmony BEGIN");
    arp.Bind(activeDrumTrack, controller);
    noteViz.Initialize();
    harmony.Initialize(activeDrumTrack, controller, arp);
    Debug.Log("[GFM] [STEP 2] Bind ARP + init NoteViz/Harmony END");

    // --------------------
    // STEP 3: Launch players
    // --------------------
    Debug.Log("[GFM] [STEP 3] Launch players BEGIN");
    foreach (var lp in localPlayers)
    {
        if (lp == null)
        {
            Debug.LogWarning("[GFM] [STEP 3] Skipping null LocalPlayer.");
            continue;
        }

        Debug.Log($"[GFM] [STEP 3] Launching player: {lp.name}");
        lp.Launch();
    }
    Debug.Log("[GFM] [STEP 3] Launch players END");

    // --------------------
    // STEP 4: Start drums
    // --------------------
    Debug.Log("[GFM] [STEP 4] activeDrumTrack.ManualStart BEGIN");
    activeDrumTrack.ManualStart();
    Debug.Log("[GFM] [STEP 4] activeDrumTrack.ManualStart END");

    // --------------------
    // STEP 5: Apply harmony profile
    // --------------------
    Debug.Log("[GFM] [STEP 5] harmony.SetActiveProfile BEGIN");
    var phase = phaseTransitionManager.currentPhase;

    Debug.Log($"[GFM] [STEP 5] currentPhase = {phase}");
    var prof = GetProfileForPhase(phase);
    harmony.SetActiveProfile(prof, applyImmediately: true);
    Debug.Log("[GFM] [STEP 5] harmony.SetActiveProfile END");
    StartMazeAndStarForPhase(phase);
    _setupDone = true;
    _setupInFlight = false;
    Debug.Log("[GFM] [SETUP] Completed successfully.");

    yield break;
}

    private bool HaveAllCoreRefs()
{
    return activeDrumTrack && controller &&
           dustGenerator && noteViz && harmony && phaseTransitionManager &&
           arp && spawnGrid && playerStatsGrid;
}
    private void TryFillTracksBundleIfMissing()
{
    // If anchors already set these, these lines do nothing.
    activeDrumTrack      = activeDrumTrack      ? activeDrumTrack      : FindFirstObjectByType<DrumTrack>(FindObjectsInactive.Include);
    controller           = controller           ? controller           : FindFirstObjectByType<InstrumentTrackController>(FindObjectsInactive.Include);
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
        // Synchronous scene swap
        SceneManager.LoadScene(sceneName, LoadSceneMode.Single);

        // Let the new scene's Awake/Start run
        yield return null;


        if (sceneHandlers != null && sceneHandlers.TryGetValue(sceneName, out var handler))
        {
            // Now ALWAYS call the handler, including for GeneratedTrack
            handler?.Invoke();
        }
  
    }
    public void BeginGeneratedTrackSetup()
    {
        if (_setupDone || _setupInFlight) return;
        StartCoroutine(HandleTrackSceneSetupAsync());
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

    // A) Start bridge immediately on the long-loop boundary (no extra lap).
    var activeNow = allTracks.Where(t => t.GetPersistentLoopNotes()?.Count > 0).ToList();

    // ----------------------------------------------------------------------
    // B) Drop 1–3 tracks, remix remaining into the bridge signature
    // ----------------------------------------------------------------------
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
        viz?.TriggerNoteBlastOff(t);
        t.ClearLoopedNotes(TrackClearType.Remix);
        t.ResetBinStateForNewPhase();
    }

    // Snap visual grid to retained leader so X positions match what you hear
    if (viz && drum)
    {
        int leaderMulRetained = ComputeLeaderMulFor(retain.Count > 0 ? retain : Enumerable.Empty<InstrumentTrack>());
        int leaderStepsRetained = Mathf.Max(1, leaderMulRetained) * Mathf.Max(1, drum.totalSteps);
        viz.RequestLeaderGridChange(leaderStepsRetained);
        // NoteVisualizer applies pending grid changes on its next loop boundary.
    }
    SetBridgeVisualMode(true);

    // ----------------------------------------------------------------------
    // C) Play the REMIXED bridge ONCE while showing only CORAL
    // ----------------------------------------------------------------------
    if (viz && viz.GetUIParent()) viz.GetUIParent().gameObject.SetActive(false);

    var coral = EnsureCoral(); 
    if (coral != null) {
        coral.gameObject.SetActive(true); 
        float bridgeOnceSec = controller != null
            ? controller.GetEffectiveLoopLengthInSeconds()
            : LeaderLoopSecondsFor(retain.Count > 0 ? retain : allTracks);

        var snapshot = BuildPhaseSnapshotForBridge(retain, activeDrumTrack); 
        yield return StartCoroutine(AnimateCoralBridge(coral, snapshot, bridgeOnceSec));
    }
    else
    {
        float bridgeOnceSec = controller != null
            ? controller.GetEffectiveLoopLengthInSeconds()
            : LeaderLoopSecondsFor(retain.Count > 0 ? retain : allTracks);

        yield return new WaitForSeconds(bridgeOnceSec);
    }

    // ----------------------------------------------------------------------
    // D) Clear all tracks, then EITHER:
    //    - In demoMode: return to Main (no next phase),
    //    - Or in full game: seed & start next phase as before.
    // ----------------------------------------------------------------------
    var stillActive = new List<InstrumentTrack>();
    foreach (var t in allTracks)
    {
        var notes = t.GetPersistentLoopNotes();
        if (notes != null && notes.Count > 0) stillActive.Add(t);
    }
    foreach (var t in stillActive)
        t.ClearLoopedNotes(TrackClearType.Remix);

    if (coral != null) coral.gameObject.SetActive(false);
    SetBridgeVisualMode(false);
    if (viz && viz.GetUIParent()) viz.GetUIParent().gameObject.SetActive(true);

    // ========= DEMO SHORT-CIRCUIT =========
    if (demoMode)
    {
        // tidy up bridge accent / flags
        if (sig.includeDrums && drum) drum.SetBridgeAccent(false);
        GhostCycleInProgress = false;
        SetBridgeVisualMode(false);

        // Go back to the starting screen (Main) and clear players.
        // This uses your existing path.
        QuitToSelection();
        yield break;
    }
    // ========= END DEMO SHORT-CIRCUIT =========

    // Seeds / visibility for the next phase (full game path)
    var seeds = PickSeeds(allTracks, sig.seedTrackCountNextPhase, sig.preferredSeedRoles);
    controller?.ApplyPhaseSeedOutcome(nextPhase, seeds);
    controller?.ApplySeedVisibility(seeds);
    ResetPhaseBinStateAndGrid();

    if (activeDrumTrack != null)
    {
        // New path: start next phase maze & PhaseStar, carving corridors from
        // the current Vehicle positions to the star cell.
        StartCoroutine(StartNextPhaseMazeAndStar(nextPhase));
    }
    else
    {
        Debug.LogWarning("[BRIDGE] No DrumTrack available after bridge; cannot start next phase star.");
    }

    if (sig.includeDrums && drum) drum.SetBridgeAccent(false);
    GhostCycleInProgress = false;
    SetBridgeVisualMode(false);
}

private IEnumerator StartNextPhaseMazeAndStar(MusicalPhase nextPhase)
{
    var drums = activeDrumTrack;
    var dust  = dustGenerator;

    if (drums == null)
    {
        Debug.LogWarning("[GFM] Cannot start next phase maze: no DrumTrack.");
        yield break;
    }
    // ---- 1) Wait for players to actually spawn their Vehicles / lanes ----
    float waitStart   = Time.time;
    const float maxWait = 5f; // safety
    while (localPlayers != null &&
           localPlayers.Any(lp => lp && lp.plane == null) &&
           Time.time - waitStart < maxWait)
    {
        yield return null;
    }

    // Filter to only live players
    var livePlayers = (localPlayers ?? new List<LocalPlayer>())
        .Where(lp => lp != null && lp.plane != null)
        .ToList();

    // ---- 2) Build vehicleCells from actual spawned positions ----
    var vehicleCells = new List<Vector2Int>();

    if (livePlayers.Count > 0)
    {
        foreach (var lp in livePlayers)
        {
            // You can use either lp.transform.position (lane anchor)
            // or lp.plane.transform.position (actual ship position).
            // Using the lane anchor keeps corridors stable.
            Vector3 worldPos = lp.transform.position;
            Vector2Int cell  = drums.WorldToGridPosition(worldPos);
            vehicleCells.Add(cell);
        }
    }
    else if (vehicles != null && vehicles.Count > 0)
    {
        foreach (var v in vehicles)
        {
            if (!v) continue;
            Vector2Int cell = drums.WorldToGridPosition(v.transform.position);
            vehicleCells.Add(cell);
        }
    }

    // ---- 3) Choose ONE starCell and keep it consistent ----
    // Pick a random star cell that is not already occupied by a vehicle cell.
    Vector2Int starCell = drums.GetRandomAvailableCell();
    int safety = 32;
    while (vehicleCells.Contains(starCell) && safety-- > 0)
    {
        starCell = drums.GetRandomAvailableCell();
    }

    // ---- 4) Handle "no dust" fallback cleanly ----
    if (dust == null)
    {
        Debug.LogWarning("[GFM] No CosmicDustGenerator; spawning PhaseStar without maze.");
        drums.RequestPhaseStar(nextPhase, starCell);
        yield break;
    }

    // ---- 5) Normal path: spawn star + build maze using the SAME starCell ----
    drums.RequestPhaseStar(nextPhase, starCell);

    Debug.Log(
        $"[GFM] StartNextPhaseMazeAndStar: phase={nextPhase}, " +
        $"starCell={starCell}, vehicles={vehicleCells.Count}"
    );

    yield return StartCoroutine(
        dust.GenerateMazeForPhaseWithPaths(
            nextPhase,
            starCell,
            vehicleCells
        )
    );
}

public float GetBeatInterval()
{
    var drums = activeDrumTrack;
    return drums != null ? drums.drumLoopBPM : 0.5f;
}
    private PhaseSnapshot BuildPhaseSnapshotForBridge(List<InstrumentTrack> retained, DrumTrack drum){
        var snapshot = new PhaseSnapshot { Timestamp = Time.time };

       if (retained == null || retained.Count == 0) return snapshot;

        // Build CollectedNotes, ordered by musical step so kinks occur in musical order.
        foreach (var track in retained) {
            if (!track) continue;
            var notes = track.GetPersistentLoopNotes();
            if (notes == null || notes.Count == 0) continue;
            var c = ResolveTrackColor(track);
            // notes: List<(int stepIndex, int note, int duration, float velocity)>
            foreach (var n in notes.OrderBy(n => n.stepIndex)) {
                // Map directly to PhaseSnapshot.NoteEntry
                snapshot.CollectedNotes.Add(new PhaseSnapshot.NoteEntry(step:     n.stepIndex, note:     n.note, velocity: n.velocity, trackColor: c)); 
            } 
        }
        return snapshot;
    }

    private IEnumerator AnimateCoralBridge(CoralVisualizer coral, PhaseSnapshot snapshot, float bridgeDurationSec) {
        if (!coral || snapshot == null || snapshot.CollectedNotes == null || snapshot.CollectedNotes.Count == 0) {
            // Nothing to render; still hold timing so audio/visual windows match.
            yield return new WaitForSeconds(bridgeDurationSec);
            yield break;
        }

        // Fire-and-forget animation; CoralVisualizer handles growth internally.
        coral.RenderPhaseCoral(snapshot, CoralState.Drawing);

        // Hold visual focus for exactly one leader loop while bridge plays.
        float t = 0f;
        while (t < bridgeDurationSec) {
            t += Time.deltaTime;
            yield return null; 
        }
    }

/// <summary>
/// Adapter to obtain the per-track color for snapshot entries.
/// </summary>
private Color ResolveTrackColor(InstrumentTrack t)
{
    // Per your API: InstrumentTrack.trackColor
    return (t != null) ? t.trackColor : Color.white;
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

public void StartMazeAndStarForPhase(MusicalPhase phase)
{
    StartCoroutine(StartNextPhaseMazeAndStar(phase));
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
        if (spawnGrid              == a.spawnGrid)              spawnGrid              = null;
        if (glitch                 == a.glitchManager)          glitch                 = null;
        if (arp                    == a.arp)                    arp                    = null;
    }
    
}
