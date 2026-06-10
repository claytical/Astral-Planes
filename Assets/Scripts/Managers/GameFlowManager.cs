using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using MidiPlayerTK;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

public enum GameState { Begin, Selection, Playing, GameOver }

public partial class GameFlowManager : MonoBehaviour
{
    [Header("Bridge gating")]
    [Tooltip("If true, bridge/cinematic is pending and should gate spawns and re-arm.")]
    public bool BridgePending => SessionState?.BridgePending ?? false;
    
    [Header("Glyph (2D Bridge Visualizer)")]
    [SerializeField] private GlyphApplicator motifGlyphApplicator;

    [Header("Ring Glyph (Motif Rings)")]
    [SerializeField] private MotifRingGlyphApplicator motifRingGlyphApplicator;
    [SerializeField] private BinRingController binRingController;
    [SerializeField, Min(0f)] private float motifBridgeHoldSeconds = 4f;
    [Header("Phase-In FX")]
    [Tooltip("Particle system prefab instantiated at each vehicle position when a new " +
             "phase begins.  Should be a short burst (self-destruct or timed).")]
    [SerializeField] private ParticleSystem vehiclePhaseInFxPrefab;

    [Tooltip("Seconds to wait after the vehicle phase-in FX before spawning the PhaseStar. " +
             "Gives the player a moment to orient before the star walks in.")]
    [SerializeField, Min(0f)] private float vehiclePhaseInDelaySeconds = 1.2f;

    public static GameFlowManager Instance { get; private set; }
    public bool demoMode = true;

    [Header("Debug")]
    [SerializeField] public bool verboseLogging;
    public static bool VerboseLogging => Instance != null && Instance.verboseLogging;


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
    private GameState currentState = GameState.Begin;
    private Dictionary<string, Action> sceneHandlers;
    private List<Vehicle> vehicles;

    // Scratch list for dust regrow veto (prevents collider "trap" when a cell regrows under a vehicle).
    private readonly List<Vector2Int> _vehicleCellsScratch = new List<Vector2Int>(8);
    public List<InstrumentTrack> _activeTracks = new();
    [Header("Phase Bridge Audio")]
    [Tooltip("Seconds to fade out the outgoing motif during the bridge cinematic.")]
    [SerializeField] private float phaseBridgeFadeOutSeconds = 1.0f;

    [Tooltip("Seconds to fade in the next motif after the new phase begins.")]
    [SerializeField] private float phaseBridgeFadeInSeconds  = 0.6f;
    public bool GhostCycleInProgress
    {
        get => SessionState?.GhostCycleInProgress ?? false;
        private set => SessionState?.SetGhostCycleInProgress(value);
    }
    public SessionStateCoordinator SessionState { get; private set; }
    public BridgeCoordinator BridgeFlow { get; private set; }
    public SceneFlowCoordinator SceneFlow { get; private set; }
    
    public void RegisterGlyphApplicator(GlyphApplicator applicator)
    {
        motifGlyphApplicator = applicator;
    }

    public void RegisterRingGlyphApplicator(MotifRingGlyphApplicator applicator)
    {
        motifRingGlyphApplicator = applicator;
    }

    public void RegisterBinRingController(BinRingController ctrl)
    {
        binRingController = ctrl;
    }

    public BinRingController GetBinRingController() => binRingController;

    public void SetupBinRingController()
    {
        if (binRingController == null)
            binRingController = FindFirstObjectByType<BinRingController>(FindObjectsInactive.Include);
        binRingController?.Setup(activeDrumTrack, controller?.tracks);
    }

    public GameState CurrentState
    {
        get => SessionState?.CurrentState ?? GameState.Begin;
        set => SessionState?.SetCurrentState(value);
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
            if (GameFlowManager.VerboseLogging) Debug.Log($"[GFM:GenerateNotes] track={track.name} bin={targetBin} -> returning prebuilt NoteSet (cfg deterministic)");
            return prebuilt;
        }

        // Fallback: bin was not pre-generated (e.g. hot-reload in editor, missing motif).
        // Generate on the fly for this specific bin so behavior remains correct.
        Debug.LogWarning($"[GFM:GenerateNotes] track={track.name} bin={targetBin} prebuilt missing, generating on-the-fly.");
        return phaseTransitionManager.noteSetFactory.GenerateForBin(
            track, phaseTransitionManager.currentMotif, targetBin, entropy);

    }

    /// <summary>
    /// Single source of truth for "any collectables are currently in flight" across all tracks.
    /// Also owns stale-list pruning so all callers share identical list hygiene.
    /// </summary>
    public bool AnyCollectablesInFlightGlobal()
    {
        var trackController = controller;
        var trackList = trackController?.tracks;
        if (trackList == null) return false;

        foreach (var track in trackList)
        {
            if (track == null) continue;
            track.PruneSpawnedCollectables();

            var spawned = track.spawnedCollectables;
            if (spawned == null) continue;

            for (int i = 0; i < spawned.Count; i++)
            {
                var go = spawned[i];
                if (go != null && go.activeInHierarchy)
                    return true;
            }
        }

        return false;
    }
    void Start()
    {
        sceneHandlers = new()
        {
            { "TrackFinished", HandleTrackFinishedSceneSetup },
            { "TrackSelection", HandleTrackSelectionSceneSetup },
        };
    }
    
    public void QuitToSelection()
    {
        StartCoroutine(SceneFlow.QuitToSelection());
    }

    public void RegisterPlayerStatsGrid(Transform grid)
    {
        SceneFlow.RegisterPlayerStatsGrid(grid);
    }

    public void UnregisterPlayerStatsGrid(Transform grid)
    {
        SceneFlow.UnregisterPlayerStatsGrid(grid);
    }
    private IEnumerator HandleTrackSceneSetupAsync()
    {
        _setupInFlight = true;
        vehicles.Clear();

        if (SceneManager.GetActiveScene().name != "GeneratedTrack")
        {
            Debug.LogWarning($"[GFM] HandleTrackSceneSetupAsync invoked outside GeneratedTrack. Aborting.");
            _setupInFlight = false;
            yield break;
        }

        localPlayers = localPlayers.Where(p => p != null).ToList();
        if (localPlayers.Count == 0)
        {
            Debug.LogError("[GFM] No local players available during GeneratedTrack setup.");
            _setupInFlight = false;
            yield break;
        }

        // Wait for scene refs to register (with hard timeout).
        float startTime = Time.time;
        TryFillTracksBundleIfMissing();
        yield return new WaitUntil(() => HaveAllCoreRefs() || Time.time - startTime > 8f);
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

        var shipProfiles = localPlayers
            .Select(p => ShipMusicalProfileLoader.GetProfile(p.GetSelectedShipName()))
            .ToList();

        ConfigureSessionSystems(shipProfiles);  // steps 1–3: tracks, chapter, viz, timing authorities

        LaunchPlayers();                         // step 4
        yield return null;                       // one frame for vehicles to register

        ApplyInitialHarmonyProfile();            // step 5

        yield return StartCoroutine(StartNextPhaseMazeAndStar(doHardReset: false));  // step 6
        yield return null;                       // let maze side-effects settle

        currentState = GameState.Playing;
        _setupDone = true;
        _setupInFlight = false;
        if (GameFlowManager.VerboseLogging) Debug.Log("[GFM] [SETUP] Completed successfully.");
    }

    private void ConfigureSessionSystems(System.Collections.Generic.List<ShipMusicalProfile> shipProfiles)
    {
        if (GameFlowManager.VerboseLogging) Debug.Log("[GFM] [SETUP] Ship profiles: " +
            string.Join(", ", shipProfiles.Select(sp => sp ? sp.name : "<null>")));

        // Step 1 — configure track voices from ship selections
        controller.ConfigureTracksFromShips(shipProfiles);

        // Step 1b — load first chapter/motif
        if (PhaseLibraryStartConfig.HasPendingStart)
        {
            phaseTransitionManager.StartChapter(PhaseLibraryStartConfig.PhaseIndex, "GFM/Setup/Library");
            phaseTransitionManager.JumpToMotifIndex(PhaseLibraryStartConfig.MotifIndex, "GFM/Setup/Library");
            PhaseLibraryStartConfig.Consume();
        }
        else
        {
            phaseTransitionManager.StartChapter(phaseTransitionManager.FirstPhaseIndex, "GFM/Setup");
        }

        // Step 2 — graph-facing systems
        noteViz.Initialize();
        harmony.Initialize(activeDrumTrack, controller);

        // Step 3 — timing/grid authorities (must precede player launch)
        activeDrumTrack.ManualStart();
        dustGenerator.ManualStart();
    }

    private void LaunchPlayers()
    {
        foreach (var lp in localPlayers)
        {
            if (lp == null) { Debug.LogWarning("[GFM] Skipping null LocalPlayer during launch."); continue; }
            if (GameFlowManager.VerboseLogging) Debug.Log($"[GFM] Launching player: {lp.name}");
            lp.Launch();
        }
    }

    private void ApplyInitialHarmonyProfile()
    {
        if (phaseTransitionManager.currentMotif != null)
            harmony.SetActiveProfile(phaseTransitionManager.currentMotif.chordProgression, applyImmediately: true);
    }
    public float GetTotalSpentEnergyTanks()
    {
        float total = 0f;
        if (vehicles == null) return total;
        for (int i = 0; i < vehicles.Count; i++)
        {
            var v = vehicles[i];
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
    private void HandleTrackFinishedSceneSetup()
    {
        currentState = GameState.GameOver;
        harmony?.Initialize(null, null); // drops subscriptions safely
    }
    private void HandleTrackSelectionSceneSetup()
    {
        currentState = GameState.Selection;
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

        yield return new WaitForSeconds(2f);
        StartCoroutine(TransitionToScene("TrackFinished"));
    }
    
    /// <summary>
    /// Averages the stick input from all active LocalPlayers for use in bridge steering.
    /// Returns Vector2.zero if no players are present. Result is clamped to magnitude 1.
    /// </summary>
    
    private IEnumerator TransitionToScene(string sceneName)
    {
        yield return StartCoroutine(SceneFlow.TransitionToScene(sceneName));
    }
    public void BeginGeneratedTrackSetup()
    {
        SceneFlow.BeginGeneratedTrackSetup();
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
    private IEnumerator StartNextPhaseMazeAndStar(bool doHardReset = true)
{
    // ============================================================
    // RESPONSIBILITY: chapter wiring + maze rebuild + vehicle FX + PhaseStar entry
    // ============================================================

    var drums = activeDrumTrack;
    var dust  = dustGenerator;

    if (doHardReset)
    {
        controller?.BeginNewMotif($"Next PhaseStart");
        noteViz?.BeginNewMotif_ClearAll(destroyMarkerGameObjects: true);
    }

    if (drums == null) { Debug.LogWarning("[GFM] No DrumTrack."); yield break; }
    if (dust  == null) { Debug.LogWarning("[GFM] No CosmicDustGenerator."); yield break; }

    // Chapter wiring.
    if (phaseTransitionManager != null)
    {
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

    // 2) Gather vehicle grid cells (carve pockets around active vehicles).
    _vehicleCellsScratch.Clear();
    if (vehicles != null)
        for (int i = 0; i < vehicles.Count; i++)
        {
            var v = vehicles[i];
            if (v == null || !v.isActiveAndEnabled) continue;
            _vehicleCellsScratch.Add(drums.WorldToGridPosition(v.transform.position));
        }
    dust.SetReservedVehicleCells(_vehicleCellsScratch);

    // 3) Apply phase profile to dust generator before maze generation.
    var profileForPhase = phaseTransitionManager?.currentMotif?.starBehavior;
    if (profileForPhase != null)
        dust.ApplyProfile(profileForPhase);

    // Apply motif active roles to dust so Voronoi and regrowth use only this motif's roles.
    // Read from phaseTransitionManager.currentMotif — the star doesn't exist yet at this point.
    var motifRoles = phaseTransitionManager?.currentMotif?.GetActiveRoles();
    dust.ApplyActiveRoles(motifRoles);
    dust.ApplyMotifGeoConfig(phaseTransitionManager?.currentMotif);

    // 4) Build maze; vehicle trap ring cells are injected into the stagger list so they
    //    grow in simultaneously with maze cells rather than appearing as a separate event.
    var trapCallback = BuildTrapCallback(phaseTransitionManager?.currentMotif);
    yield return StartCoroutine(
        dust.GenerateMazeForPhaseWithPaths(
            starCell,
            _vehicleCellsScratch,
            totalSpawnDuration: 1.0f,
            onBeforeGrowth: trapCallback
        )
    );

    // 5) Vehicle phase-in FX (gives player a visual "new phase" cue before the star walks in)
    SpawnPhaseInFX();
    if (vehiclePhaseInDelaySeconds > 0f)
        yield return new WaitForSeconds(vehiclePhaseInDelaySeconds);

    // 6) Start the star (vehicle traps already growing from step 4 callback)
    drums.RequestPhaseStar(starCell);
    dust.ResetMazeGenerationFlag();
}

private System.Action<List<(Vector2Int, Vector3)>> BuildTrapCallback(MotifProfile trapMotif)
{
    if (trapMotif == null || !trapMotif.spawnVehicleTrap || dustGenerator == null || activeDrumTrack == null)
        return null;

    return cellsToFill =>
    {
        var allVehicles = vehicles ?? new List<Vehicle>();
        foreach (var v in allVehicles)
        {
            if (v == null || !v.isActiveAndEnabled) continue;
            Vector2Int center = activeDrumTrack.WorldToGridPosition(v.transform.position);
            if (trapMotif.trapShape == TrapShape.Circle)
            {
                int inner = Mathf.Max(0, trapMotif.trapRadius - 1);
                dustGenerator.InjectTrapCellsIntoStagger(cellsToFill, center, trapMotif.trapRadius, inner, trapMotif.trapRole);
            }
            else
            {
                int r = trapMotif.trapRadius;
                var perim = new List<Vector2Int>(r * 8);
                for (int dx = -r; dx <= r; dx++)
                {
                    perim.Add(new Vector2Int(center.x + dx, center.y + r));
                    perim.Add(new Vector2Int(center.x + dx, center.y - r));
                }
                for (int dy = -r + 1; dy <= r - 1; dy++)
                {
                    perim.Add(new Vector2Int(center.x - r, center.y + dy));
                    perim.Add(new Vector2Int(center.x + r, center.y + dy));
                }
                dustGenerator.InjectTrapCellsFromList(cellsToFill, perim, trapMotif.trapRole);
            }
        }
    };
}

private void SpawnPhaseInFX()
{
    if (vehiclePhaseInFxPrefab == null) return;
    var activeVehicles = vehicles ?? new List<Vehicle>();
    foreach (var v in activeVehicles)
    {
        if (v == null || !v.isActiveAndEnabled) continue;
        var fx = Instantiate(vehiclePhaseInFxPrefab, v.transform.position, Quaternion.identity);
        fx.Play();
        Destroy(fx.gameObject, Mathf.Max(fx.main.duration + fx.main.startLifetime.constantMax, 4f));
    }
}
    private void Update()
    {
        // Feed current vehicle grid positions to the dust generator so regrowth can be vetoed deterministically.
        if (dustGenerator == null || activeDrumTrack == null) return;

        _vehicleCellsScratch.Clear();
        if (vehicles != null)
        {
            for (int i = 0; i < vehicles.Count; i++)
            {
                var v = vehicles[i];
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
        SceneFlow.RegisterTracksBundle(a);
    }
    public void UnregisterTracksBundle(TracksBundleAnchor a)
    {
        SceneFlow.UnregisterTracksBundle(a);
    }
    
    // Destroys any lingering note tether GameObjects (including inactive) so they never persist across phases.
    // We key off name and component type to be resilient to prefab/script renames.
    public void CleanupAllNoteTethers()
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
            if (GameFlowManager.VerboseLogging) Debug.Log($"[BRIDGE] CleanupAllNoteTethers destroyed={killed}");
    }

    // Coordinator facade helpers
    public IReadOnlyList<Vehicle> GetVehicles() => vehicles;
    public void ClearVehicles() => vehicles?.Clear();
    public void ClearActiveTracks() => _activeTracks?.Clear();
    public void SetPlayerStatsGrid(Transform grid) => playerStatsGrid = grid;
    public void ClearPlayerStatsGrid() => playerStatsGrid = null;
    public float GetMotifBridgeHoldSeconds() => motifBridgeHoldSeconds;
    public MotifRingGlyphApplicator GetMotifRingGlyphApplicator() => motifRingGlyphApplicator;
    public float GetVehiclePhaseInDelaySeconds() => vehiclePhaseInDelaySeconds;
    public static Color QuantizeToColor32(Color c)
    {
        Color32 cc = (Color32)c;
        return new Color(cc.r / 255f, cc.g / 255f, cc.b / 255f, cc.a / 255f);
    }
    public void PlayVehiclePhaseInFx()
    {
        if (vehiclePhaseInFxPrefab == null) return;
        var activeVehicles = vehicles ?? new List<Vehicle>();

        foreach (var v in activeVehicles)
        {
            if (v == null || !v.isActiveAndEnabled) continue;
            var fx = Instantiate(vehiclePhaseInFxPrefab, v.transform.position, Quaternion.identity);
            fx.Play();
            Destroy(fx.gameObject, Mathf.Max(fx.main.duration + fx.main.startLifetime.constantMax, 4f));
        }
    }

    public void SpawnVehicleTraps(MotifProfile motif)
    {
        if (motif == null || !motif.spawnVehicleTrap)
        {
            if (GameFlowManager.VerboseLogging) Debug.Log($"[TRAP] SpawnVehicleTraps skipped: motif={motif?.motifId ?? "null"} spawnVehicleTrap={motif?.spawnVehicleTrap}");
            return;
        }
        if (dustGenerator == null || activeDrumTrack == null)
        {
            Debug.LogWarning($"[TRAP] SpawnVehicleTraps skipped: dustGenerator={dustGenerator} activeDrumTrack={activeDrumTrack}");
            return;
        }

        var roleProfile = MusicalRoleProfileLibrary.GetProfile(motif.trapRole);
        Color roleColor = roleProfile != null ? roleProfile.GetBaseColor() : Color.white;

        var allVehicles = vehicles ?? new List<Vehicle>();

        if (GameFlowManager.VerboseLogging) Debug.Log($"[TRAP] SpawnVehicleTraps motif={motif.motifId} shape={motif.trapShape} radius={motif.trapRadius} vehicles={allVehicles.Count}");

        for (int i = 0; i < allVehicles.Count; i++)
        {
            var v = allVehicles[i];
            if (v == null || !v.isActiveAndEnabled) continue;
            Vector2Int center = activeDrumTrack.WorldToGridPosition(v.transform.position);
            if (GameFlowManager.VerboseLogging) Debug.Log($"[TRAP] Spawning trap around vehicle[{i}] at grid {center}");

            if (motif.trapShape == TrapShape.Circle)
            {
                int inner = Mathf.Max(0, motif.trapRadius - 1);
                int processed = dustGenerator.GrowVoidDustDiskFromGrid(
                    centerGP: center,
                    outerRadiusCells: motif.trapRadius,
                    imprintRole: motif.trapRole,
                    hueRgb: roleColor,
                    energyAtCenter01: 1f,
                    falloffExp: 1f,
                    growInSeconds: motif.trapGrowSeconds,
                    fillWedges01To4: 4,
                    vehicleCells: null,
                    vehicleNoSpawnRadiusCells: 0,
                    innerRadiusCellsExclusive: inner,
                    hideRole: true);
                if (GameFlowManager.VerboseLogging) Debug.Log($"[TRAP] GrowVoidDustDiskFromGrid processed={processed} center={center} outer={motif.trapRadius} inner={inner}");
            }
            else
            {
                int r = motif.trapRadius;
                var perimeter = new List<Vector2Int>(r * 8);
                for (int dx = -r; dx <= r; dx++)
                {
                    perimeter.Add(new Vector2Int(center.x + dx, center.y + r));
                    perimeter.Add(new Vector2Int(center.x + dx, center.y - r));
                }
                for (int dy = -r + 1; dy <= r - 1; dy++)
                {
                    perimeter.Add(new Vector2Int(center.x - r, center.y + dy));
                    perimeter.Add(new Vector2Int(center.x + r, center.y + dy));
                }
                dustGenerator.SpawnDustAtCells(perimeter, motif.trapRole, roleColor,
                    1f, motif.trapGrowSeconds, hideRole: true);
            }
        }
    }

}
