using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using MidiPlayerTK;
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

public partial class GameFlowManager : MonoBehaviour
{
    [Header("Bridge gating")]
    [Tooltip("If true, bridge/cinematic is pending and should gate spawns and re-arm.")]
    public bool BridgePending = false;

    [Tooltip("If true, prevent SpawnCollectableBurst from creating collectables while bridge is pending/in progress.")]
    public bool suppressCollectableSpawnsDuringBridge = true;

    [Tooltip("Max extra loops to wait for collectables to clear before starting the bridge.")]
    public int phaseBridgeWaitMaxLoops = 64;
    [Header("Coral (New Spiral)")]
    [SerializeField] private MotifCoralVisualizer motifCoralVisualizer;
    [SerializeField, Min(0f)] private float motifBridgeHoldSeconds = 4f;
    [SerializeField] private bool useSpiralCoralDuringBridge = true;
    [SerializeField] private Transform coralRoot; // scene object root
    private float _bridgeDrumStartVolume = 1f;
// Optional: fade using renderer alpha (Standard shader)
    [SerializeField] private bool fadeSpiralCoralDuringBridge = true;
    [SerializeField, Min(0f)] private float spiralCoralFadeTarget = 0.65f;
    [Header("Phase-In FX")]
    [Tooltip("Particle system prefab instantiated at each vehicle position when a new " +
             "phase begins.  Should be a short burst (self-destruct or timed).")]
    [SerializeField] private ParticleSystem vehiclePhaseInFxPrefab;

    [Tooltip("Seconds to wait after the vehicle phase-in FX before spawning the PhaseStar. " +
             "Gives the player a moment to orient before the star walks in.")]
    [SerializeField, Min(0f)] private float vehiclePhaseInDelaySeconds = 1.2f;

    public static GameFlowManager Instance { get; private set; }
    public bool demoMode = true;


    public HarmonyDirector harmony;
    public InstrumentTrackController controller;
    
    public DrumTrack activeDrumTrack;
    public CosmicDustGenerator dustGenerator;
    public SpawnGrid spawnGrid;
    public NoteVisualizer noteViz;    // assign in scene
    public PhaseTransitionManager phaseTransitionManager;
    
    public List<LocalPlayer> localPlayers = new();

    [SerializeField] private Transform playerStatsGrid; // assign via inspector if possible
    public Transform PlayerStatsGrid => playerStatsGrid;
    private bool _setupInFlight, _setupDone;
    private readonly List<MotifSnapshot> _motifSnapshots = new();
    private float _motifStartTime = 0f; // stamped when each motif begins, used for coral branch height
    private GameState currentState = GameState.Begin;
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
    
    public void RegisterMotifCoralVisualizer(MotifCoralVisualizer vis)
    {
        motifCoralVisualizer = vis;
        Debug.Log($"[CORAL:MOTIF] Registered MotifCoralVisualizer: {vis?.name}");
    }

    public GameState CurrentState
    {
        get => currentState;
        set => currentState = value;
    }
    

    public NoteSet GenerateNotes(InstrumentTrack track, int entropy = 0)
    {
        if (track == null) return null;

        // Primary path: return the pre-generated NoteSet for the bin this track is
        // about to fill. This is deterministic and consistent with what
        // ConfigureTracksForCurrentPhaseAndMotif already authored.
        int targetBin = track.GetBinCursor();
        // Clamp to valid range defensively.
        targetBin = Mathf.Clamp(targetBin, 0, track.maxLoopMultiplier - 1);

        var prebuilt = track.GetNoteSetForBin(targetBin);
        if (prebuilt != null)
        {
            Debug.Log($"[GFM:GenerateNotes] track={track.name} bin={targetBin} -> returning prebuilt NoteSet (cfg deterministic)");
            return prebuilt;
        }

        // Fallback: bin was not pre-generated (e.g. hot-reload in editor, missing motif).
        // Generate on the fly for this specific bin so behavior remains correct.
        Debug.LogWarning($"[GFM:GenerateNotes] track={track.name} bin={targetBin} prebuilt missing, generating on-the-fly.");
        return phaseTransitionManager.noteSetFactory.GenerateForBin(
            track, phaseTransitionManager.currentMotif, targetBin, entropy);

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
    // STEP 2: Choose phase chapter

    var startPhase = MazeArchetype.Release;

    // STEP 2.5: Choose the chapter + first paragraph motif BEFORE starting drums.
    if (phaseTransitionManager != null)
    {
        // Pick your intended starting chapter here (you used Release as boot elsewhere).
        // If you want Establish as the first chapter, change MazeArchetype.Release -> MazeArchetype.Establish.
        phaseTransitionManager.StartChapter(MazeArchetype.Release, "GFM/TrackSetup");
    }
    else
    {
        Debug.LogWarning("[GFM] No PhaseTransitionManager; DrumTrack will boot from inspector/default clip.");
    }

    activeDrumTrack.ManualStart();
    dustGenerator.ManualStart();
    
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
    
    harmony.SetActiveProfile(phaseTransitionManager.currentMotif.chordProgression, applyImmediately: true);
    Debug.Log("[GFM] [STEP 5] harmony.SetActiveProfile END");
    StartMazeAndStarForPhase(phase);
    _setupDone = true;
    _setupInFlight = false;
    Debug.Log("[GFM] [SETUP] Completed successfully.");

    yield break;
}
    public float GetTotalSpentEnergyTanks()
    {
        float total = 0f;

        if (vehicles != null && vehicles.Count > 0)
        {
            for (int i = 0; i < vehicles.Count; i++)
            {
                var v = vehicles[i];
                if (v == null || !v.isActiveAndEnabled) continue;
                total += v.GetCumulativeSpentTanks();
            }
            return total;
        }

        // Fallback: scene scan (consistent with your dust-regrow logic).
        var vs = FindObjectsOfType<Vehicle>();
        for (int i = 0; i < vs.Length; i++)
        {
            var v = vs[i];
            if (v == null || !v.isActiveAndEnabled) continue;
            total += v.GetCumulativeSpentTanks();
        }

        return total;
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

        if (!playerStatsGrid) {
            // Prefer anchor if you added it
            var gridAnchor = FindFirstObjectByType<PlayerStatsGridAnchor>(FindObjectsInactive.Include);
            if (gridAnchor) RegisterPlayerStatsGrid(gridAnchor.transform);
        }
    }

    private void HandleChapterChanged(MazeArchetype oldP, MazeArchetype newP)
    {
        var prof = activeDrumTrack.phasePersonalityRegistry != null ? activeDrumTrack.phasePersonalityRegistry.Get(newP) : null;
        if (dustGenerator != null && prof != null)
            dustGenerator.ApplyProfile(prof);
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
            player.ResetReady();
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
    
    /// <summary>
    /// Averages the stick input from all active LocalPlayers for use in coral bridge steering.
    /// Returns Vector2.zero if no players are present. Result is clamped to magnitude 1.
    /// </summary>
    
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
    private void BindSceneVoicesToTimingAuthority()
    {
        if (activeDrumTrack == null) return;

        // Bind all MidiVoices in scene that are NOT owned by an InstrumentTrack.
        // InstrumentTrack already binds its own MidiVoice in Awake.
        var voices = FindObjectsOfType<MidiVoice>(includeInactive: true);
        foreach (var v in voices)
        {
            if (v == null) continue;

            // If this voice lives under an InstrumentTrack, let InstrumentTrack binding stand.
            if (v.GetComponentInParent<InstrumentTrack>() != null)
                continue;

            v.SetDrumTrack(activeDrumTrack);

            // Optional: ensure it has a MidiStreamPlayer (common for Solo/FX child objects).
            var player = v.GetComponent<MidiStreamPlayer>() ?? v.GetComponentInParent<MidiStreamPlayer>();
            if (player != null)
                v.SetMidiStreamPlayer(player);
        }

        // Also ensure SoloVoice has its DrumTrack reference, if it isn't manually set.
        var solos = FindObjectsOfType<SoloVoice>(includeInactive: true);
        foreach (var s in solos)
        {
            if (s == null) continue;
            // SoloVoice already subscribes; this just prevents an unset reference case.
            // (If drumTrack is [SerializeField], you can add a setter later if needed.)
        }
    }
    private static Color QuantizeToColor32(Color c)
    {
        Color32 cc = (Color32)c;
        return new Color(cc.r / 255f, cc.g / 255f, cc.b / 255f, cc.a / 255f);
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
private IEnumerator StartNextPhaseMazeAndStar(MazeArchetype nextPhase, bool doHardReset = true)
{
    // ============================================================
    // RESPONSIBILITY: chapter wiring + maze rebuild + vehicle FX + PhaseStar entry
    // ============================================================

    var drums = activeDrumTrack;
    var dust  = dustGenerator;

    if (doHardReset)
    {
        controller?.BeginNewMotif($"PhaseStart {nextPhase}");
        noteViz?.BeginNewMotif_ClearAll(destroyMarkerGameObjects: true);
    }

    if (drums == null) { Debug.LogWarning("[GFM] No DrumTrack."); yield break; }
    if (dust  == null) { Debug.LogWarning("[GFM] No CosmicDustGenerator."); yield break; }

    // Chapter wiring.
    if (phaseTransitionManager != null)
    {
        if (phaseTransitionManager.currentPhase != nextPhase)
            phaseTransitionManager.StartChapter(nextPhase, "GFM/StartNextPhaseMazeAndStar");
        else
            phaseTransitionManager.EnsureChapterLoaded(nextPhase, "GFM/StartNextPhaseMazeAndStar(SamePhase)");
    }

    ResetPhaseBinStateAndGrid();

    yield return new WaitUntil(() =>
        drums.HasSpawnGrid() &&
        drums.GetSpawnGridWidth()  > 0 &&
        drums.GetSpawnGridHeight() > 0 &&
        Camera.main != null);

    // 1) Choose star cell.
    var starCell = drums.GetRandomAvailableCell();
    if (starCell.x < 0)
    {
        Debug.LogWarning("[GFM] No available cell for PhaseStar.");
        yield break;
    }

    // 2) Gather vehicle grid cells (carve pockets).
    _vehicleCellsScratch.Clear();

    if (vehicles != null && vehicles.Count > 0)
    {
        for (int i = 0; i < vehicles.Count; i++)
        {
            var v = vehicles[i];
            if (v == null || !v.isActiveAndEnabled) continue;
            _vehicleCellsScratch.Add(drums.WorldToGridPosition(v.transform.position));
        }
    }
    else
    {
        var vs = FindObjectsOfType<Vehicle>();
        for (int i = 0; i < vs.Length; i++)
        {
            var v = vs[i];
            if (v == null || !v.isActiveAndEnabled) continue;
            _vehicleCellsScratch.Add(drums.WorldToGridPosition(v.transform.position));
        }
    }

    dust.SetReservedVehicleCells(_vehicleCellsScratch);

    // 3) Apply phase profile to dust generator before maze generation.
    var profileForPhase = drums.phasePersonalityRegistry != null
        ? drums.phasePersonalityRegistry.Get(nextPhase)
        : null;
    if (profileForPhase != null)
        dust.ApplyProfile(profileForPhase);

    // Apply motif active roles to dust so Voronoi and regrowth use only this motif's roles.
    var motifRoles = activeDrumTrack?._star?.GetMotifActiveRoles();
    dust.ApplyActiveRoles(motifRoles);

    // 4) Build maze.
    yield return StartCoroutine(
        dust.GenerateMazeForPhaseWithPaths(
            nextPhase,
            starCell,
            _vehicleCellsScratch,
            totalSpawnDuration: 1.0f
        )
    );

    // ── 5) Vehicle phase-in FX ────────────────────────────────────────────────
    // Trigger a particle burst at each active vehicle position so the player
    // gets a clear "new phase begins" signal before the star enters.
    if (vehiclePhaseInFxPrefab != null)
    {
        var activeVehicles = vehicles != null && vehicles.Count > 0
            ? vehicles
            : new List<Vehicle>(FindObjectsOfType<Vehicle>());

        foreach (var v in activeVehicles)
        {
            if (v == null || !v.isActiveAndEnabled) continue;
            var fx = Instantiate(vehiclePhaseInFxPrefab, v.transform.position, Quaternion.identity);
            fx.Play();
            // Auto-destroy after the longest reasonable burst duration.
            Destroy(fx.gameObject, Mathf.Max(fx.main.duration + fx.main.startLifetime.constantMax, 4f));
        }
    }

    // Hold briefly so the FX reads before the star starts walking in.
    if (vehiclePhaseInDelaySeconds > 0f)
        yield return new WaitForSeconds(vehiclePhaseInDelaySeconds);

    // ── 6) Spawn star (off-screen entry via EnterFromOffScreen) ───────────────
    drums.RequestPhaseStar(nextPhase, starCell);

    Debug.Log($"[GFM] Maze+Star started: phase={nextPhase} starCell={starCell} " +
              $"vehicleCells={_vehicleCellsScratch.Count}");
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
        Debug.Log("[GFM] Tracks bundle registered from Generated Track scene.");
        BindSceneVoicesToTimingAuthority();
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
