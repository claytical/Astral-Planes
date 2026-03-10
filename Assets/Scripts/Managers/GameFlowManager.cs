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

public class GameFlowManager : MonoBehaviour
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
    private readonly List<PhaseSnapshot> _motifSnapshots = new();
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
            
            // Skip the motif coral visualizer so it stays visible during the motif bridge
            if (motifCoralVisualizer != null && r.transform.IsChildOf(motifCoralVisualizer.transform))
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
        if (!localPlayers.All(p => p.IsReady)) return;

        // Don’t load GeneratedTrack yet — show the primary tutorial sequence first.
        if (ControlTutorialDirector.Instance != null)
        {
            Debug.Log($"[TUTORIAL] Begin Tutorial Sequence");
            ControlTutorialDirector.Instance.BeginPrimaryTutorialSequence();
            return;
        }

        // Fallback if director is missing
        SessionGenome.BootNewSessionSeed((int)UnityEngine.Random.Range(0, 1000f));
        StartCoroutine(TransitionToScene("GeneratedTrack"));
    }

    public void BeginGameAfterTutorial()
    {
        SessionGenome.BootNewSessionSeed((int)UnityEngine.Random.Range(0, 1000f));
        StartCoroutine(TransitionToScene("GeneratedTrack"));
    }

    public void StartShipSelectionPhase()
    {
        CurrentState = GameState.Selection;
        // No need to call PlayerInput.Instantiate — joining is handled by PlayerInputManager
        Debug.Log("✅ Ship selection phase started. Waiting for players to join.");
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

    public void BeginMotifBridge(MazeArchetype phaseToRestart, string who)
    {
        Debug.Log($"[MOTIF-BRIDGE] BeginMotifBridge phase={phaseToRestart} by={who}");
        StartCoroutine(PlayMotifBridgeAndRestart(phaseToRestart));
    }

    private IEnumerator PlayMotifBridgeAndRestart(MazeArchetype phaseToRestart)
    {
        GhostCycleInProgress = true;
        BridgePending = true;
        FreezeGameplayForBridge();
        SetBridgeCinematicMode(true); // hide maze, dust, vehicles — coral will be the only thing visible

        // Snapshot BEFORE StartNextMotifInPhase → BeginNewMotif clears the tracks.
        var allTracks = (controller?.tracks != null)
            ? controller.tracks.Where(t => t != null).ToList()
            : new List<InstrumentTrack>();

        var motifSnap = BuildPhaseSnapshotForBridge(allTracks, activeDrumTrack);
        motifSnap.Pattern = phaseTransitionManager != null
            ? phaseTransitionManager.currentPhase
            : MazeArchetype.Establish;

        _motifSnapshots.Add(motifSnap);
        ConstellationMemoryStore.StoreSnapshot(_motifSnapshots);

        Debug.Log($"[MOTIF-BRIDGE] Snapshot committed: notes={motifSnap.CollectedNotes.Count} " +
                  $"bins={motifSnap.TrackBins.Count} snapshots={_motifSnapshots.Count}");

        // Derive bridge duration from the actual musical loop length — same pattern as PlayPhaseBridge.
        // This makes the coral grow animation span exactly one full loop replay.
        float motifBridgeSec = (controller != null)
            ? Mathf.Max(1f, controller.GetEffectiveLoopLengthInSeconds())
            : Mathf.Max(1f, motifBridgeHoldSeconds);

        Debug.Log($"[MOTIF-BRIDGE] Bridge duration: {motifBridgeSec:F2}s (motifBridgeHoldSeconds fallback={motifBridgeHoldSeconds})");

        // Show MotifCoralVisualizer — grows over exactly one musical loop.
        if (motifCoralVisualizer != null)
        {
            motifCoralVisualizer.gameObject.SetActive(true);

            // Fit coral to the live play area so it stays within the visible game space.
            if (activeDrumTrack != null && activeDrumTrack.TryGetPlayAreaWorld(out var playArea))
            {
                motifCoralVisualizer.FitToPlayArea(
                    playArea.width,
                    playArea.height,
                    (playArea.left + playArea.right) * 0.5f,
                    (playArea.bottom + playArea.top) * 0.5f
                );
            }

            yield return StartCoroutine(
                motifCoralVisualizer.GrowMotifCoral(
                    motifSnap,
                    motifBridgeSec,
                    ReadAveragedSteer)
            );
            motifCoralVisualizer.gameObject.SetActive(false);
        }
        else
        {
            Debug.LogWarning("[MOTIF-BRIDGE] No MotifCoralVisualizer assigned — holding without visual.");
            yield return new WaitForSeconds(motifBridgeSec);
        }

        // Advance motif — this calls BeginNewMotif() which clears track state.
        yield return StartCoroutine(StartNextMotifInPhase(phaseToRestart));

        SetBridgeCinematicMode(false); // restore maze, dust, vehicles
        GhostCycleInProgress = false;
        BridgePending = false;
    }
    
    private IEnumerator StartNextMotifInPhase(MazeArchetype phase)
{
    // ============================================================
    // RESPONSIBILITY: motif-level reset + motif advance decision
    // ============================================================

    // 0) Hard reset only ONCE per motif boundary
    _motifStartTime = Time.time; // stamp before clearing tracks so coral height is correct
    controller?.BeginNewMotif($"MotifStart {phase}");
    noteViz?.BeginNewMotif_ClearAll(destroyMarkerGameObjects: true);

    if (phaseTransitionManager == null)
    {
        Debug.LogWarning("[GFM] No PhaseTransitionManager; cannot advance motif.");
        yield break;
    }

    // Ensure we're operating within the correct chapter without reinitializing it.
    phaseTransitionManager.EnsureChapterLoaded(phase, "GFM/StartNextMotifInPhase");

    // 1) Try to advance within the chapter
    var newMotif = phaseTransitionManager.AdvanceMotif("GFM/StartNextMotifInPhase");

    if (newMotif == null)
    {
        // Chapter exhausted (loopMotifs==false) -> advance phase/chapter
        var nextPhase = phaseTransitionManager.ResolveNextPhase(phase);
        Debug.Log($"[CHAPTER] Motifs exhausted for phase={phase}. Starting next chapter phase={nextPhase}.");

        phaseTransitionManager.StartChapter(nextPhase, "GFM/StartNextMotifInPhase(Exhausted)");

        // World rebuild only (doHardReset=false; we've already reset for motif boundary)
        yield return StartCoroutine(StartNextPhaseMazeAndStar(nextPhase, doHardReset: false));
        yield break;
    }

    // Motif advanced within same chapter: rebuild world only (no extra reset)
    yield return StartCoroutine(StartNextPhaseMazeAndStar(phase, doHardReset: false));
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
    private void BindPhaseHandlers()
    {
        if (phaseTransitionManager == null) return;

        phaseTransitionManager.OnPhaseChanged -= HandleChapterChanged;
        phaseTransitionManager.OnPhaseChanged += HandleChapterChanged;
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
    private Vector2 ReadAveragedSteer()
    {
        if (localPlayers == null || localPlayers.Count == 0) return Vector2.zero;
        Vector2 sum = Vector2.zero;
        int n = 0;
        foreach (var lp in localPlayers)
        {
            if (lp == null) continue;
            sum += lp.GetMoveInput();
            n++;
        }
        return n > 0 ? Vector2.ClampMagnitude(sum / n, 1f) : Vector2.zero;
    }

    private IEnumerator FadeSpiralCoralAlpha(Transform root, float target, float seconds)
    {
        if (root == null) yield break;

        var meshRs = root.GetComponentsInChildren<MeshRenderer>(includeInactive: true);
        var startA = new Dictionary<MeshRenderer, float>(meshRs.Length);

        foreach (var mr in meshRs)
        {
            if (!mr) continue;
            var mat = mr.material;
            if (!mat || !mat.HasProperty("_Color")) continue;
            startA[mr] = mat.color.a;
        }

        float t = 0f;
        while (t < seconds)
        {
            if (root == null) yield break;
            t += Time.deltaTime;
            float u = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / Mathf.Max(0.0001f, seconds)));

            foreach (var kvp in startA)
            {
                var mr = kvp.Key;
                if (!mr) continue;
                var mat = mr.material;
                if (!mat || !mat.HasProperty("_Color")) continue;

                var c = mat.color;
                c.a = Mathf.Lerp(kvp.Value, target, u);
                mat.color = c;
            }

            yield return null;
        }

        // final snap
        foreach (var kvp in startA)
        {
            var mr = kvp.Key;
            if (!mr) continue;
            var mat = mr.material;
            if (!mat || !mat.HasProperty("_Color")) continue;
            var c = mat.color; c.a = target; mat.color = c;
        }
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

    private PhaseSnapshot BuildPhaseSnapshotForBridge(List<InstrumentTrack> retained, DrumTrack drum)
    {
        var snapshot = new PhaseSnapshot { Timestamp = Time.time };

        // --- PHASE CONTEXT (REPLACE THESE 2 LINES WITH YOUR REAL API) ---
        snapshot.Pattern = MazeArchetype.Establish;   // MusicalPhase
        snapshot.Color = dustGenerator.MazeColor();   // Color (maze/dust/phase tint)
        // ---------------------------------------------------------------

        // Coral animation timing: normalise step → bridge time using the live drum totalSteps.
        snapshot.TotalSteps = (drum != null && drum.totalSteps > 0) ? drum.totalSteps : 16;

        // Root-note highlight: used to give distinct visual treatment to key-root notes.
        snapshot.MotifKeyRootMidi = (phaseTransitionManager != null && phaseTransitionManager.currentMotif != null)
            ? phaseTransitionManager.currentMotif.keyRootMidi
            : 60;

        if (retained == null || retained.Count == 0) return snapshot;

        float motifStartTime = _motifStartTime > 0f ? _motifStartTime : snapshot.Timestamp;

        foreach (var track in retained)
        {
            if (!track) continue;

            var notes = track.GetPersistentLoopNotes();
            if (notes == null || notes.Count == 0) continue;

            Color c       = QuantizeToColor32(ResolveTrackColor(track));
            int   binSize = Mathf.Max(1, track.BinSize());

            // Build per-bin template: step → authored note (for full matched check).
            // A note is "matched" only if the player hit the correct beat AND the correct pitch.
            var templateNoteByBinStep = new Dictionary<int, Dictionary<int, int>>(); // binIndex → (localStep → note)
            for (int b = 0; b < track.maxLoopMultiplier; b++)
            {
                var ns = track.GetNoteSetForBin(b);
                if (ns?.persistentTemplate == null) continue;
                var stepNoteMap = new Dictionary<int, int>();
                foreach (var t in ns.persistentTemplate)
                    stepNoteMap[t.step] = t.note; // template steps are bin-local (0..binSize-1)
                templateNoteByBinStep[b] = stepNoteMap;
            }

            // Emit NoteEntries with BinIndex and IsMatched populated.
            // IsMatched = player hit the authored step AND played the authored note at that step.
            foreach (var n in notes.OrderBy(n => n.stepIndex))
            {
                int binIndex  = n.stepIndex / binSize;
                int localStep = n.stepIndex % binSize;
                bool isMatched = templateNoteByBinStep.TryGetValue(binIndex, out var stepNoteMap)
                                 && stepNoteMap.TryGetValue(localStep, out int authoredNote)
                                 && n.note == authoredNote;

                snapshot.CollectedNotes.Add(new PhaseSnapshot.NoteEntry(
                    step:       n.stepIndex,
                    note:       n.note,
                    velocity:   n.velocity,
                    trackColor: c,
                    binIndex:   binIndex,
                    isMatched:  isMatched
                ));
            }

            // Emit TrackBinData entries — one per allocated bin on this track.
            int allocatedBins = Mathf.Max(1, track.loopMultiplier);
            for (int b = 0; b < allocatedBins; b++)
            {
                var binNotes = notes.Where(n => n.stepIndex / binSize == b).ToList();
                templateNoteByBinStep.TryGetValue(b, out var tplMap);

                int matched   = 0;
                int unmatched = 0;
                var collectedSteps = new List<int>();
                foreach (var n in binNotes)
                {
                    int localStep = n.stepIndex % binSize;
                    collectedSteps.Add(localStep);
                    bool noteMatched = tplMap != null
                                       && tplMap.TryGetValue(localStep, out int authoredNote)
                                       && n.note == authoredNote;
                    if (noteMatched) matched++;
                    else unmatched++;
                }

                snapshot.TrackBins.Add(new PhaseSnapshot.TrackBinData
                {
                    TrackColor         = c,
                    Role               = track.assignedRole,
                    BinIndex           = b,
                    IsFilled           = track.IsBinFilled(b),
                    CompletionTime     = track.GetBinCompletionTime(b),
                    MotifStartTime     = motifStartTime,
                    MatchedNoteCount   = matched,
                    UnmatchedNoteCount = unmatched,
                    CollectedSteps     = collectedSteps,
                });
            }
        }

        int matchedTotal   = snapshot.CollectedNotes.Count(n => n.IsMatched);
        int unmatchedTotal = snapshot.CollectedNotes.Count - matchedTotal;
        Debug.Log($"[PHASE SNAPSHOT FINALIZE] phase={snapshot.Pattern} notes={snapshot.CollectedNotes.Count} " +
                  $"matched={matchedTotal} unmatched={unmatchedTotal} bins={snapshot.TrackBins.Count} " +
                  $"totalSteps={snapshot.TotalSteps} rootMidi={snapshot.MotifKeyRootMidi}");
        return snapshot;
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
    private IEnumerator StartNextPhaseMazeAndStar(MazeArchetype nextPhase, bool doHardReset = true)
{
    // ============================================================
    // RESPONSIBILITY: chapter wiring + maze rebuild + PhaseStar spawn
    // ============================================================

    var drums = activeDrumTrack;
    var dust  = dustGenerator;

    if (doHardReset)
    {
        // Only do this when the caller truly wants a full phase start reset.
        controller?.BeginNewMotif($"PhaseStart {nextPhase}");
        noteViz?.BeginNewMotif_ClearAll(destroyMarkerGameObjects: true);
    }

    if (drums == null) { Debug.LogWarning("[GFM] No DrumTrack."); yield break; }
    if (dust  == null) { Debug.LogWarning("[GFM] No CosmicDustGenerator."); yield break; }

    // Chapter wiring:
    // - if phase changes: StartChapter (resets motif index, sets currentMotif, applies motif to audio/tracks)
    // - if same phase: EnsureChapterLoaded only (do NOT reset motif index)
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
        drums.GetSpawnGridWidth() > 0 &&
        drums.GetSpawnGridHeight() > 0 &&
        Camera.main != null);

    // 1) Choose star cell
    var starCell = drums.GetRandomAvailableCell();
    if (starCell.x < 0)
    {
        Debug.LogWarning("[GFM] No available cell for PhaseStar.");
        yield break;
    }

    // 2) Gather vehicle grid cells (for carve pockets)
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

    // Keep regrowth veto consistent immediately (not one frame later in Update()).
    dust.SetReservedVehicleCells(_vehicleCellsScratch);

    // 3) Apply phase profile to dust generator BEFORE maze generation so that
    //    BuildMazeRoleImprints can read _activeProfile.dominantRole.
    //    (HandleChapterChanged fires after StartChapter above, but GenerateMaze
    //    runs synchronously in a coroutine and may execute before the event
    //    propagates — apply explicitly here to guarantee ordering.)
    var profileForPhase = drums.phasePersonalityRegistry != null
        ? drums.phasePersonalityRegistry.Get(nextPhase)
        : null;
    if (profileForPhase != null)
        dust.ApplyProfile(profileForPhase);

    // 4) Build maze (dust fill + carve star pocket + carve vehicle pockets)
    yield return StartCoroutine(
        dust.GenerateMazeForPhaseWithPaths(
            nextPhase,
            starCell,
            _vehicleCellsScratch,
            totalSpawnDuration: 1.0f
        )
    );

    // 4) Spawn star at same cell used by dust
    drums.RequestPhaseStar(nextPhase, starCell);

    Debug.Log($"[GFM] Maze+Star started: phase={nextPhase} starCell={starCell} vehicleCells={_vehicleCellsScratch.Count}");
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
