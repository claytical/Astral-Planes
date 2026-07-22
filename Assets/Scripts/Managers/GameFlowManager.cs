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

        yield return StartCoroutine(SceneFlow.FadeScreenToBlack());
        yield return TransitionToScene("TrackFinished");
        yield return StartCoroutine(SceneFlow.FadeScreenFromBlack());
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
