using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using MidiPlayerTK;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

public enum GameState { Begin, Selection, Playing, GameOver }

public partial class GameFlowManager : MonoBehaviour
{
    [Header("Bridge gating")]
    [Tooltip("If true, bridge/cinematic is pending and should gate spawns and re-arm.")]
    public bool BridgePending => SessionState?.BridgePending ?? false;
    
    [Header("Ring Glyph (Motif Rings)")]
    [SerializeField] private MotifRingGlyphApplicator motifRingGlyphApplicator;
    [SerializeField] private BinRingController binRingController;
    [Header("Phase-In FX")]
    [Tooltip("Particle system prefab instantiated at each vehicle position when a new " +
             "phase begins.  Should be a short burst (self-destruct or timed).")]
    [SerializeField] private ParticleSystem vehiclePhaseInFxPrefab;

    [Header("Path Choice")]
    [Tooltip("Everything about path-choice exits — placement, the four road looks, feel and " +
             "timing — lives on this one asset. Unassigned = Resources/Config/PathExitConfig, " +
             "then built-in defaults.")]
    [UnityEngine.Serialization.FormerlySerializedAs("pathExitParticleStyle")]
    [SerializeField] private PathExitConfig pathExitConfig;
    private PathExitConfig _pathExitConfigFallback;

    /// <summary>The assigned path-exit config, else Resources/Config/PathExitConfig, else defaults. Never null.</summary>
    public PathExitConfig PathExitConfig
    {
        get
        {
            if (pathExitConfig != null) return pathExitConfig;
            if (_pathExitConfigFallback == null)
            {
                _pathExitConfigFallback = Resources.Load<PathExitConfig>("Config/PathExitConfig");
                if (_pathExitConfigFallback == null)
                {
                    _pathExitConfigFallback = ScriptableObject.CreateInstance<PathExitConfig>();
                    _pathExitConfigFallback.hideFlags = HideFlags.HideAndDontSave;
                }
            }
            return _pathExitConfigFallback;
        }
    }

    // Forwarders kept so existing callers don't change; the values live on PathExitConfig.
    public float PathExitSealFadeSeconds => PathExitConfig.sealFadeSeconds;
    public float PathExitCloseSeconds => PathExitConfig.closeSeconds;
    public float PathExitDissolveSeconds => PathExitConfig.dissolveSeconds;
    public float PathExitPostSwallowBeatSeconds => PathExitConfig.postSwallowBeatSeconds;
    public float PathEntryPortalCloseSeconds => PathExitConfig.entryRoadCloseSeconds;
    public float PathTransitionOffscreenMargin => PathExitConfig.offscreenMargin;
    public float PathTransitionTravelTimeout => PathExitConfig.travelTimeoutSeconds;
    public int PathExitCaptureDepthCells => PathExitConfig.captureDepthCells;

    [Tooltip("Wisp prefab spawned once per multi-exit path-choice interstitial maze — leads " +
             "the vehicle toward a randomly chosen exit and plays a one-shot bridging sample " +
             "on contact. Leave unassigned to disable the feature.")]
    [SerializeField] private GameObject wispPrefab;
    public GameObject WispPrefab => wispPrefab;

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

    [Header("Competitive Mode")]
    [Min(1), Tooltip("Points awarded for each note a player places in competitive mode.")]
    public int pointsPerNote = 1;
    [Min(1), Tooltip("Multiplier on note points for the player whose vehicle depleted the DiscoveryTrackNode, until that burst clears.")]
    public int discoveryMultiplier = 2;

    [SerializeField] private Transform playerStatsGrid; // assign via inspector if possible
    public Transform PlayerStatsGrid => playerStatsGrid;
    private List<Vehicle> vehicles;

    // Scratch list for dust regrow veto (prevents collider "trap" when a cell regrows under a vehicle).
    private readonly List<Vector2Int> _vehicleCellsScratch = new List<Vector2Int>(8);
    public List<InstrumentTrack> _activeTracks = new();
    public bool GhostCycleInProgress
    {
        get => SessionState?.GhostCycleInProgress ?? false;
        private set => SessionState?.SetGhostCycleInProgress(value);
    }
    public SessionStateCoordinator SessionState { get; private set; }
    public BridgeCoordinator BridgeFlow { get; private set; }
    public SceneFlowCoordinator SceneFlow { get; private set; }
    public PathChoiceCoordinator PathChoice { get; private set; }
    
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
            binRingController = FindAnyObjectByType<BinRingController>(FindObjectsInactive.Include);
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
        //
        // Resolve the bin via a non-consuming preview of the same logic SpawnCollectableBurst
        // will use (BinFrontierAllocator.PeekBinForNextSpawn), rather than reading the raw
        // cursor directly. A raw cursor read can be stale relative to where the collectable
        // actually lands: AdvanceOtherTrackCursors bumps a track's cursor when a sibling track
        // extends the leader bin count, even though this track hasn't itself advanced. If that
        // track's earlier bin later ascends and gets refilled, the allocator correctly redirects
        // back to the emptied bin (GetFirstEmptyAllocatedBin) while a raw cursor read would
        // still point past it — baking the wrong bin's chord into the refill.
        //
        // Deliberately uses the "Peek" variant, not GetBinForNextSpawn itself: that method
        // consumes a one-shot "allow advance" token (ConsumeAllowAdvanceNextBurst) meant to be
        // read exactly once, at the real spawn decision in
        // InstrumentTrackCompositionSpawner.SpawnBurst. Calling it here too would silently
        // swallow that token before the real spawn sees it, blocking the track from ever
        // advancing to its next bin.
        int targetBin = track.controller != null
            ? track.controller.PeekBinForNextSpawn(track)
            : track.GetBinCursor();
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
    
    private Coroutine _quitRoutine;

    public void QuitToSelection()
    {
        if (_quitRoutine != null) return; // already quitting
        // Every session flow (bridge, path choice, track setup, game over) runs as a coroutine
        // on this persistent object. QuitToSelection nulls the scene refs they use, so stop them
        // first instead of letting them resume against torn-down state.
        AbortSessionCoroutines();
        // Start fading any wisp bridge sample now rather than after players are torn down
        // (TransitionToScene also does this as the catch-all for every scene load).
        Wisp.FadeOutActiveBridgeSamples(0.25f);
        _quitRoutine = StartCoroutine(QuitToSelectionRoutine());
    }

    private IEnumerator QuitToSelectionRoutine()
    {
        yield return SceneFlow.QuitToSelection();
        _quitRoutine = null;
    }

    private void AbortSessionCoroutines()
    {
        StopAllCoroutines();
        _quitRoutine = null;
        SceneFlow.ResetAfterAbortedFlows();
    }

    public void RegisterPlayerStatsGrid(Transform grid)
    {
        SceneFlow.RegisterPlayerStatsGrid(grid);
    }

    public void UnregisterPlayerStatsGrid(Transform grid)
    {
        SceneFlow.UnregisterPlayerStatsGrid(grid);
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


    // Scene-load handlers live on SceneFlowCoordinator (HandleTrackFinishedSceneSetup /
    // HandleTrackSelectionSceneSetup). An unused duplicate of them here was removed 2026-11-17.
    private IEnumerator HandleGameOverSequence()
    {
        InstrumentTrackController itc = FindAnyObjectByType<InstrumentTrackController>(); 
        NoteVisualizer visualizer = FindAnyObjectByType<NoteVisualizer>();

        // Before BeginGameOverFade: it stretches every loop note to 1920 ticks, which would
        // corrupt the unfinished record TrackFinished plays back.
        BridgeFlow?.CaptureUnfinishedSnapshot();

        itc?.BeginGameOverFade();

        yield return new WaitForSeconds(2f);

        if (visualizer != null)
        {
            visualizer.GetUIParent().gameObject.SetActive(false);
        }

        yield return new WaitForSeconds(2f);

        yield return StartCoroutine(SceneFlow.FadeScreen(toBlack: true));
        yield return TransitionToScene("TrackFinished");
        yield return StartCoroutine(SceneFlow.FadeScreen(toBlack: false));
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
    public float GetMotifBridgeHoldSeconds() => PathExitConfig.motifBridgeHoldSeconds;
    public MotifRingGlyphApplicator GetMotifRingGlyphApplicator() => motifRingGlyphApplicator;
    public float GetVehiclePhaseInDelaySeconds() => PathExitConfig.vehiclePhaseInDelaySeconds;
    public DrumTrack ResolveDrumTrack() =>
        activeDrumTrack != null ? activeDrumTrack : (activeDrumTrack = FindAnyObjectByType<DrumTrack>());
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


}
