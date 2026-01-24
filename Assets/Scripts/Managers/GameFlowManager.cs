using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
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
    [Header("Bridge gating")]
    [Tooltip("If true, bridge/cinematic is pending and should gate spawns and re-arm.")]
    public bool BridgePending = false;

    [Tooltip("If true, prevent SpawnCollectableBurst from creating collectables while bridge is pending/in progress.")]
    public bool suppressCollectableSpawnsDuringBridge = true;

    [Tooltip("Max extra loops to wait for collectables to clear before starting the bridge.")]
    public int phaseBridgeWaitMaxLoops = 64;

    public static GameFlowManager Instance { get; private set; }
    public bool demoMode = true;
    private struct PhaseChordMap { public MazeArchetype phase; public ChordProgressionProfile progression; }

    private List<PhaseChordMap> chordMaps = new();

    [Header("Settings")]
    public List<MusicalPhaseProfile> phaseProfiles;

    public HarmonyDirector harmony;
    public InstrumentTrackController controller;
    //public ChordChangeArpeggiator arp;
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
    private readonly List<PhaseSnapshot> _motifSnapshots = new();
    private GameState currentState = GameState.Begin;
    private MusicalPhaseProfile _pendingPhaseProfile;  // optional: set when the next star is known
    private Dictionary<string, Action> sceneHandlers;
    private List<Vehicle> vehicles;

    // Scratch list for dust regrow veto (prevents collider "trap" when a cell regrows under a vehicle).
    private readonly List<Vector2Int> _vehicleCellsScratch = new List<Vector2Int>(8);
    public List<InstrumentTrack> _activeTracks = new();
    private bool _remixArmed = false;                  // true once previous star’s set is completed
    private bool _nextPhaseLoopArmed = false;
    private bool hasGameOverStarted = false;
    private readonly List<Renderer> _bridgeHiddenRenderers = new();

    [Header("Phase Bridge Audio")]
    [Tooltip("Seconds to fade out the outgoing motif during the bridge cinematic.")]
    [SerializeField] private float phaseBridgeFadeOutSeconds = 1.0f;

    [Tooltip("Seconds to fade in the next motif after the new phase begins.")]
    [SerializeField] private float phaseBridgeFadeInSeconds  = 0.6f;
    private readonly Dictionary<InstrumentTrack, float> _bridgeMidiStartVolumes = new();
    public bool GhostCycleInProgress { get; private set; }
    public event Action<MusicalPhaseProfile> OnRemixCommitted;
    
    // Bridge suppression: when true, tracks should not spawn collectables during bridge pending/in-progress.
    public static bool ShouldSuppressCollectableSpawns
    {
        get
        {
            var gfm = Instance;
            if (gfm == null) return false;
            return gfm.suppressCollectableSpawnsDuringBridge && (gfm.BridgePending || gfm.GhostCycleInProgress);
        }
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
    private void SetBridgeCinematicMode(bool on)
    {
        // Existing behavior: hide maze + note UI
        SetBridgeVisualMode(on);

        if (on)
            HideNonCoralRenderersForBridge();
        else
            RestoreNonCoralRenderersAfterBridge();
    }

    private void HideNonCoralRenderersForBridge()
    {
        _bridgeHiddenRenderers.Clear();

        // Hide Vehicles + any other visible gameplay renderers.
        // We intentionally do NOT touch the coral instance here.
        foreach (var r in FindObjectsOfType<Renderer>(includeInactive: true))
        {
            if (!r) continue;

            // Skip anything belonging to the coral instance (if it already exists)
            if (_coralInstance != null && r.transform.IsChildOf(_coralInstance.transform))
                continue;

            // Skip UI canvases (they’re already handled by SetBridgeVisualMode)
            if (r.GetComponentInParent<Canvas>(true) != null)
                continue;

            // Only hide things that are currently visible
            if (r.enabled)
            {
                _bridgeHiddenRenderers.Add(r);
                r.enabled = false;
            }
        }
    }

    private void RestoreNonCoralRenderersAfterBridge()
    {
        for (int i = 0; i < _bridgeHiddenRenderers.Count; i++)
        {
            var r = _bridgeHiddenRenderers[i];
            if (r) r.enabled = true;
        }
        _bridgeHiddenRenderers.Clear();
    }

    public NoteSet GenerateNotes(InstrumentTrack track, int entropy = 0)
    {
        var ns = phaseTransitionManager.noteSetFactory.Generate(track, phaseTransitionManager.currentMotif, entropy);
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
        if (dustGenerator) dustGenerator.gameObject.SetActive(!on);

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
    public void BeginPhaseBridge(MazeArchetype to, List<InstrumentTrack> perfectTracks, Color nextStarColor)
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
                       $"\nGrid:{spawnGrid} UIGrid:{playerStatsGrid}");
        _setupInFlight = false;
        yield break;
    }

    Debug.Log("[GFM] [SETUP] Core refs present." +
              $"\n  Drum: {activeDrumTrack}" +
              $"\n  Ctrl: {controller}" +
              $"\n  Viz : {noteViz}" +
              $"\n  Harm: {harmony}" +
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

    noteViz.Initialize();
    harmony.Initialize(activeDrumTrack, controller);
    Debug.Log("[GFM] [STEP 2] Bind ARP + init NoteViz/Harmony END");


    // --------------------
    // STEP 3: Start drums
    // --------------------
    Debug.Log("[GFM] [STEP 3] activeDrumTrack.ManualStart BEGIN");
    activeDrumTrack.ManualStart();
    dustGenerator.ManualStart();
    Debug.Log("[GFM] [STEP 3] activeDrumTrack.ManualStart END");
    
    // --------------------
    // STEP 4: Launch players
    // --------------------
    Debug.Log("[GFM] [STEP 4] Launch players BEGIN");
    foreach (var lp in localPlayers)
    {
        if (lp == null)
        {
            Debug.LogWarning("[GFM] [STEP 4] Skipping null LocalPlayer.");
            continue;
        }

        Debug.Log($"[GFM] [STEP 4] Launching player: {lp.name}");
        lp.Launch();
    }
    Debug.Log("[GFM] [STEP 4] Launch players END");


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
    public float GetMostSpentEnergyTanks() {
        float best = 0f;
        if (vehicles != null && vehicles.Count > 0) {
            for (int i = 0; i < vehicles.Count; i++) {
                var v = vehicles[i]; 
                if (v == null || !v.isActiveAndEnabled) continue; 
                best = Mathf.Max(best, v.GetCumulativeSpentTanks());
            } 
            return best;
        }
    
                // Fallback: scene scan (consistent with your dust-regrow logic).
        var vs = FindObjectsOfType<Vehicle>(); 
        for (int i = 0; i < vs.Length; i++) {
            var v = vs[i]; 
            if (v == null || !v.isActiveAndEnabled) continue; 
            best = Mathf.Max(best, v.GetCumulativeSpentTanks()); }
        return best;
    }
    public void RegisterVehicle(Vehicle v)
    {
        if (v == null) return;
        if (vehicles == null) vehicles = new List<Vehicle>();
        if (!vehicles.Contains(v)) vehicles.Add(v);
    }

    public void UnregisterVehicle(Vehicle v)
    {
        if (v == null || vehicles == null) return;
        vehicles.Remove(v);
    }

    private bool HaveAllCoreRefs() { 
        return activeDrumTrack && controller && dustGenerator && noteViz && harmony && phaseTransitionManager && spawnGrid && playerStatsGrid;
    }
    private void TryFillTracksBundleIfMissing() {
    // If anchors already set these, these lines do nothing.
    activeDrumTrack      = activeDrumTrack      ? activeDrumTrack      : FindFirstObjectByType<DrumTrack>(FindObjectsInactive.Include);
    controller           = controller           ? controller           : FindFirstObjectByType<InstrumentTrackController>(FindObjectsInactive.Include);
    dustGenerator        = dustGenerator        ? dustGenerator        : FindFirstObjectByType<CosmicDustGenerator>(FindObjectsInactive.Include);
    noteViz              = noteViz              ? noteViz              : FindFirstObjectByType<NoteVisualizer>(FindObjectsInactive.Include);
    harmony              = harmony              ? harmony              : FindFirstObjectByType<HarmonyDirector>(FindObjectsInactive.Include);
    phaseTransitionManager = phaseTransitionManager ? phaseTransitionManager : FindFirstObjectByType<PhaseTransitionManager>(FindObjectsInactive.Include);
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
        harmony?.Initialize(null, null); // drops subscriptions safely
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
        NoteVisualizer visualizer = FindAnyObjectByType<NoteVisualizer>();
        
        itc?.BeginGameOverFade();

        yield return new WaitForSeconds(2f);

        if (visualizer != null)
        {
            visualizer.GetUIParent().gameObject.SetActive(false);
        }

        foreach (var snapshot in activeDrumTrack.SessionPhases) { 
            //                galaxy.AddSnapshot(snapshot);
        }
      
        yield return new WaitForSeconds(2f); if (_motifSnapshots != null && _motifSnapshots.Count > 0) { 
            ConstellationMemoryStore.SaveSessionToDisk(_motifSnapshots);
        }
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

    private ChordProgressionProfile GetProfileForPhase(MazeArchetype phase)
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

    private IEnumerator PlayPhaseBridge(PhaseBridgeSignature sig, MazeArchetype nextPhase)
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
    // ----------------------------------------------------------------------
    // COMMIT MOTIF SNAPSHOT (for coral garden) BEFORE remix/drop/clear.
    // This represents the completed motif that just ended.
    // ----------------------------------------------------------------------
    var motifSnapshot = BuildPhaseSnapshotForBridge(allTracks, activeDrumTrack);
    // Preserve semantic phase identity for storage and later browsing.
    motifSnapshot.Pattern = phaseTransitionManager != null ? phaseTransitionManager.currentPhase : MazeArchetype.Establish;
    // Color is optional; set if you have a motif/phase color source.
    // motifSnapshot.Color = <your phase color>;
    _motifSnapshots.Add(motifSnapshot); 
    ConstellationMemoryStore.StoreSnapshot(_motifSnapshots);
    Debug.Log(
        $"[CORAL STORE] snapshots={_motifSnapshots.Count} " +
        $"totalNotes={_motifSnapshots.Sum(s => s.CollectedNotes.Count)}"
    );

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
    // ----------------------------------------------------------------------
    // B) No remixing: play the completed motif loop ONCE in cinematic mode
    // ----------------------------------------------------------------------
    activeNow = allTracks.Where(t => t != null && t.GetPersistentLoopNotes()?.Count > 0).ToList();

    // "One full loop" is authoritative to the audio system's leader steps.
    // Prefer controller.GetEffectiveLoopLengthInSeconds() (uses DrumTrack clip length + leaderSteps).
    float bridgeOnceSec = (ctrl != null)
        ? Mathf.Max(0.05f, ctrl.GetEffectiveLoopLengthInSeconds())
        : LeaderLoopSecondsFor(activeNow.Count > 0 ? activeNow : allTracks);

    SetBridgeCinematicMode(true);

    // ----------------------------------------------------------------------
    // C) Play the REMIXED bridge ONCE while showing only CORAL
    // ----------------------------------------------------------------------
    if (viz && viz.GetUIParent()) viz.GetUIParent().gameObject.SetActive(false);

    var coral = EnsureCoral(); 
    if (coral != null) {
        coral.gameObject.SetActive(true); 
        // Hide gameplay UI during the cinematic loop
        if (viz && viz.GetUIParent()) viz.GetUIParent().gameObject.SetActive(false);

        // Run coral growth across exactly one full loop while the completed motif plays as-is.
        // Schedule an end-of-loop fade-out so the next motif can start from silence.
        var coralAnim = StartCoroutine(AnimateCoralBridge(coral, motifSnapshot, bridgeOnceSec));

        float fadeOutSec = Mathf.Clamp(phaseBridgeFadeOutSeconds, 0.05f, bridgeOnceSec);
        yield return new WaitForSeconds(Mathf.Max(0f, bridgeOnceSec - fadeOutSec));
        yield return StartCoroutine(FadeOutBridgeAudio(allTracks, drum, fadeOutSec));

        // Ensure the coral loop completes (safe even if fadeOutSec == bridgeOnceSec)
        yield return coralAnim;
    }
    else
    {
        // No coral visualizer available; still hold for exactly one loop and fade audio.
        float fadeOutSec = Mathf.Clamp(phaseBridgeFadeOutSeconds, 0.05f, bridgeOnceSec);

        yield return new WaitForSeconds(Mathf.Max(0f, bridgeOnceSec - fadeOutSec));
        yield return StartCoroutine(FadeOutBridgeAudio(allTracks, drum, fadeOutSec));
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
        t.ClearLoopedNotes();

        // Reset bin/loop-width state so the next motif does not inherit multi-bin UI/loop spans.
        // (We keep note content cleared above; this is strictly about loopMultiplier/bin cursors/fill flags.)
        if (controller != null)
            controller.ResetControllerBinGuards(); 
        
        controller.BeginNewMotif();
        
        foreach (var t in allTracks)
            if (t != null)
                t.ResetBinsForPhase();

        if (activeDrumTrack != null)
            activeDrumTrack.SetBinCount(1);

        if (viz != null)
            viz.ConfigureBinStrip(1);

        if (coral != null) coral.gameObject.SetActive(false);
            // Keep cinematic mode active until the next phase maze/star are ready to avoid visual flash.

            // ========= DEMO SHORT-CIRCUIT =========
    if (demoMode) {
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

            
            // Commit the staged harmony profile now that the outgoing motif has faded.
            // This ensures the next phase begins with the correct progression without an audible retune mid-bridge.
        harmony?.CommitNextChordNow(); // commit staged motif chord profile on next downbeat
    // Seeds / visibility for the next phase (full game path)
        var seeds = PickSeeds(allTracks, sig.seedTrackCountNextPhase, sig.preferredSeedRoles);
        controller?.ApplyPhaseSeedOutcome(nextPhase, seeds);
        controller?.ApplySeedVisibility(seeds);
        ResetPhaseBinStateAndGrid();

        if (activeDrumTrack != null)
        {
            // New path: start next phase maze & PhaseStar, carving corridors from
            // the current Vehicle positions to the star cell.
            // New path: start next phase maze & PhaseStar, carving corridors from
    // the current Vehicle positions to the star cell.
    yield return StartCoroutine(StartNextPhaseMazeAndStar(nextPhase));

    // Now that the new phase geometry/star exist, we can safely restore visuals without a flash.
    SetBridgeCinematicMode(false);
   if (viz && viz.GetUIParent()) viz.GetUIParent().gameObject.SetActive(true);
        // Bring audio back for the new motif.
        StartCoroutine(FadeInBridgeAudio(allTracks, drum, phaseBridgeFadeInSeconds)); 
        }
        else {
                Debug.LogWarning("[BRIDGE] No DrumTrack available after bridge; cannot start next phase star."); 
        } 
        if (sig.includeDrums && drum) drum.SetBridgeAccent(false); 
    GhostCycleInProgress = false;
    SetBridgeVisualMode(false);
}

    private IEnumerator StartNextPhaseMazeAndStar(MazeArchetype nextPhase)
{
    var drums = activeDrumTrack;
    var dust  = dustGenerator;

    // === Motif boundary hard reset ===
    // Requirement: when a new PhaseStar begins, we start from silence and an empty NoteVisualizer.
    // Do this BEFORE we request the next PhaseStar / spawn anything for the new phase.
    if (controller != null)
        controller.BeginNewMotif($"PhaseStart {nextPhase}");

    // Redundant safety: if the controller isn't wired to the NoteVisualizer in the scene,
    // force-clear it here so we never carry markers into the next phase.
    if (noteViz != null)
        noteViz.BeginNewMotif_ClearAll(destroyMarkerGameObjects: true);

    if (drums == null)
    {
        Debug.LogWarning("[GFM] Cannot start next phase maze: no DrumTrack.");
        yield break;
    }
    
    // === RE-ARM PHASE/MOTIF -> DRUMS AFTER RESET ===
    // // BeginNewMotif clears track content + visuals. We must immediately select the next motif and
    // hand it back to the DrumTrack so driveFromEnergy/sequence is live.
    if (phaseTransitionManager != null) {
        phaseTransitionManager.HandlePhaseTransition(nextPhase, "GFM/StartNextPhaseMazeAndStar");
    }
    else {
        Debug.LogWarning("[GFM] No PhaseTransitionManager; cannot arm motif for next phase.");
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
        Debug.Log(
            $"[PHASE SNAPSHOT FINALIZE] phase={snapshot.Pattern} notes={{snapshot.CollectedNotes.Count}})"
        );

        return snapshot;
    }

    private IEnumerator AnimateCoralBridge(CoralVisualizer coral, PhaseSnapshot snapshot, float bridgeDurationSec)
    {
        // Always hold exactly one loop worth of time (audio is authoritative)
        float dur = Mathf.Max(0.05f, bridgeDurationSec);

        if (!coral)
        {
            yield return new WaitForSeconds(dur);
            yield break;
        }

        // If there are no notes, still show the coral object (core) and rotate for the loop.
        bool hasNotes = snapshot != null &&
                        snapshot.CollectedNotes != null &&
                        snapshot.CollectedNotes.Count > 0;

        // 1) Force the coral growth animation to span the entire bridge loop.
        // This is the main fix for “animation is really fast.”
        if (coral.style != null)
            coral.style.growSeconds = dur;

        // 2) Start the growth render
        if (hasNotes)
            coral.RenderPhaseCoral(snapshot, CoralState.Drawing);
        else
            coral.RenderPhaseCoral(snapshot, CoralState.Rendered);

        // 3) Rotate only the coral for the entire loop
        // (rotation speed can be tuned; start with something readable and calm)
        float degPerSec = 28f;

        float t = 0f;
        while (t < dur)
        {
            t += Time.deltaTime;

            // rotate around world Y so it reads as a sculpture turning on display
            coral.transform.Rotate(0f, degPerSec * Time.deltaTime, 0f, Space.World);

            yield return null;
        }
    }

    private IEnumerator FadeOutBridgeAudio(List<InstrumentTrack> tracks, DrumTrack drum, float durationSeconds)
    {
        float dur = Mathf.Max(0.01f, durationSeconds);

        // Cache starting volumes so we can restore/fade-in for the next motif.
        _bridgeMidiStartVolumes.Clear();

        if (tracks != null)
        {
            foreach (var t in tracks)
            {
                if (t == null) continue;
                var player = t.midiStreamPlayer;
                if (player == null) continue;

                // Note: MPTK_Volume is expected to be 0..1.
                _bridgeMidiStartVolumes[t] = player.MPTK_Volume;
            }
        }

        float drumStart = 1f;
        if (drum != null && drum.drumAudioSource != null)
            drumStart = drum.drumAudioSource.volume;

        float elapsed = 0f;
        while (elapsed < dur)
        {
            elapsed += Time.deltaTime;
            float k = Mathf.Clamp01(elapsed / dur);

            // Instruments
            foreach (var kvp in _bridgeMidiStartVolumes)
            {
                var t = kvp.Key;
                if (t == null) continue;
                var player = t.midiStreamPlayer;
                if (player == null) continue;

                player.MPTK_Volume = Mathf.Lerp(kvp.Value, 0f, k);
            }

            // Drums
            if (drum != null && drum.drumAudioSource != null)
                drum.drumAudioSource.volume = Mathf.Lerp(drumStart, 0f, k);

            yield return null;
        }

        // Hard-set to silence to avoid residual.
        foreach (var kvp in _bridgeMidiStartVolumes)
        {
            var t = kvp.Key;
            if (t == null) continue;
            var player = t.midiStreamPlayer;
            if (player != null) player.MPTK_Volume = 0f;
        }
        if (drum != null && drum.drumAudioSource != null)
            drum.drumAudioSource.volume = 0f;
    }

    private IEnumerator FadeInBridgeAudio(List<InstrumentTrack> tracks, DrumTrack drum, float durationSeconds)
    {
        float dur = Mathf.Max(0.01f, durationSeconds);

        // If we never faded out (or tracks changed), fall back to 1.0.
        float drumTarget = 1f;
        if (drum != null && drum.drumAudioSource != null)
            drumTarget = Mathf.Max(0f, drum.drumAudioSource.volume);

        // Determine per-track targets.
        var targets = new Dictionary<InstrumentTrack, float>();
        if (tracks != null)
        {
            foreach (var t in tracks)
            {
                if (t == null) continue;
                var player = t.midiStreamPlayer;
                if (player == null) continue;

                float target = 1f;
                if (_bridgeMidiStartVolumes.TryGetValue(t, out var cached))
                    target = cached;

                targets[t] = target;

                // Ensure we're starting at silence.
                player.MPTK_Volume = 0f;
            }
        }

        // Ensure drums start at silence.
        if (drum != null && drum.drumAudioSource != null)
            drum.drumAudioSource.volume = 0f;

        float elapsed = 0f;
        while (elapsed < dur)
        {
            elapsed += Time.deltaTime;
            float k = Mathf.Clamp01(elapsed / dur);

            foreach (var kvp in targets)
            {
                var t = kvp.Key;
                if (t == null) continue;
                var player = t.midiStreamPlayer;
                if (player == null) continue;

                player.MPTK_Volume = Mathf.Lerp(0f, kvp.Value, k);
            }

            if (drum != null && drum.drumAudioSource != null)
                drum.drumAudioSource.volume = Mathf.Lerp(0f, drumTarget, k);

            yield return null;
        }

        foreach (var kvp in targets)
        {
            var t = kvp.Key;
            if (t == null) continue;
            var player = t.midiStreamPlayer;
            if (player != null) player.MPTK_Volume = kvp.Value;
        }
        if (drum != null && drum.drumAudioSource != null)
            drum.drumAudioSource.volume = drumTarget;
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
    public void StartMazeAndStarForPhase(MazeArchetype phase)
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
        CleanupAllNoteTethers();

        // Despawn collectables
        foreach (var c in FindObjectsOfType<Collectable>())
        {
            if (c != null) Destroy(c.gameObject);
        }

        // Despawn MineNodes (critical)
        foreach (var n in FindObjectsOfType<MineNode>())
        {
            if (n != null) Destroy(n.gameObject);
        }

        // Optional but recommended: remove all existing PhaseStars during the bridge
        foreach (var s in FindObjectsOfType<PhaseStar>())
        {
            if (s != null) Destroy(s.gameObject);
        }

        // Ensure DrumTrack doesn't keep an old reference
        if (activeDrumTrack != null)
        {
            activeDrumTrack.isPhaseStarActive = false;
            // If _star is accessible, clear it. If it’s private, add a public helper on DrumTrack (see below).
            try { activeDrumTrack._star = null; } catch {}
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
    }

    private void Update()
    {
        // Feed current vehicle grid positions to the dust generator so regrowth can be vetoed deterministically.
        if (dustGenerator == null || activeDrumTrack == null) return;

        _vehicleCellsScratch.Clear();

        // Prefer an explicit list if your flow populates it; otherwise fall back to scene scan.
        Vehicle[] vs = null;
        if (vehicles != null && vehicles.Count > 0)
        {
            // Filter nulls/inactive.
            for (int i = 0; i < vehicles.Count; i++)
            {
                var v = vehicles[i];
                if (v == null || !v.isActiveAndEnabled) continue;
                _vehicleCellsScratch.Add(activeDrumTrack.WorldToGridPosition(v.transform.position));
            }
        }
        else
        {
            vs = FindObjectsOfType<Vehicle>();
            for (int i = 0; i < vs.Length; i++)
            {
                var v = vs[i];
                if (v == null || !v.isActiveAndEnabled) continue;
                _vehicleCellsScratch.Add(activeDrumTrack.WorldToGridPosition(v.transform.position));
            }
        }
        dustGenerator.SetReservedVehicleCells(_vehicleCellsScratch);
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
    }
    

    // Destroys any lingering note tether GameObjects (including inactive) so they never persist across phases.
    // We key off name and component type to be resilient to prefab/script renames.
    private void CleanupAllNoteTethers()
    {
        // Resources.FindObjectsOfTypeAll includes inactive objects; required because bridge may disable/hide UI.
        var allTransforms = Resources.FindObjectsOfTypeAll<Transform>();
        if (allTransforms == null) return;

        int killed = 0;
        for (int i = 0; i < allTransforms.Length; i++)
        {
            var tr = allTransforms[i];
            if (tr == null) continue;

            // Skip assets/prefabs not in a valid scene
            if (!tr.gameObject.scene.IsValid()) continue;

            string n = tr.name ?? string.Empty;

            bool nameHit = n.IndexOf("tether", System.StringComparison.OrdinalIgnoreCase) >= 0;

            bool typeHit = false;
            if (!nameHit)
            {
                // Look for any MonoBehaviour whose type name contains "Tether"
                var mbs = tr.GetComponents<MonoBehaviour>();
                if (mbs != null)
                {
                    for (int k = 0; k < mbs.Length; k++)
                    {
                        var mb = mbs[k];
                        if (mb == null) continue;
                        var tn = mb.GetType().Name;
                        if (tn.IndexOf("Tether", System.StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            typeHit = true;
                            break;
                        }
                    }
                }
            }

            if (!(nameHit || typeHit)) continue;

            // Do not destroy the NoteVisualizer root if it happens to match a loose name rule.
            if (tr.GetComponent<Canvas>() != null) continue;

            // Prefer destroying the root tether object (often the parent) to avoid orphan children.
            GameObject go = tr.gameObject;
            if (go != null)
            {
                Destroy(go);
                killed++;
            }
        }

        if (killed > 0)
            Debug.Log($"[BRIDGE] CleanupAllNoteTethers destroyed={killed}");
    }

}
