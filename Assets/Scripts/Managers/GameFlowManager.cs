using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Gameplay.Mining;
using UnityEditor.Tilemaps;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

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
    private bool _tracksRegistered;
    private bool _uiGridRegistered;
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
                setup.SetActive(true);
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
    public void CheckAllPlayersReady()
    {
        if (localPlayers.All(p => p.IsReady))
        {
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
    StartCoroutine(PlayPhaseBridge(sig, to, perfectTracks, nextStarColor));
}
    public void ArmNextPhaseLoop(MusicalPhase nextPhase)
    {
        _nextPhaseLoopArmed = true;

        // New: pre-stage the chord progression for Option C
        var prof = GetProfileForPhase(nextPhase);
        harmony?.SetActiveProfile(prof, applyImmediately: false);
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
        _uiGridRegistered = true;
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
        localPlayers.Select(p => ShipMusicalProfileLoader.GetProfile(p.GetSelectedShipName())).ToList(),
        controller.noteSetPrefab
    );

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

// Helpers
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

        if (visualizer != null)
        {
            visualizer.velocityMultiplier *= .2f;
            visualizer.waveSpeed *= 0.7f;
        }

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
    private IEnumerator PlayPhaseBridge(PhaseBridgeSignature sig, MusicalPhase nextPhase, List<InstrumentTrack> perfectTracks, Color nextStarColor)
{
    Debug.Log($"Playing Phase Bridge: {sig}");

    GhostCycleInProgress = true;
    FreezeGameplayForBridge();
    // --- Timing locked to loop length ---
    float loopLen = activeDrumTrack ? Mathf.Max(0.05f, activeDrumTrack.GetLoopLengthInSeconds()) : 4f;
    int   bars    = Mathf.Max(1, sig.bars);
    float bridgeSecs = Mathf.Min(bars * loopLen, 20f);  // hard cap safety

    double startDsp = AudioSettings.dspTime;
    double endDsp   = startDsp + bridgeSecs;
// --- Remix all tracks immediately for the bridge ---
    controller?.RemixAllTracksForBridge(GameFlowManager.Instance.phaseTransitionManager.currentPhase, sig);

    // --- Stage harmony for NEXT phase (no audible retune yet) ---
    var nextProf = GetProfileForPhase(nextPhase);
    harmony?.SetActiveProfile(nextProf, applyImmediately:false);

// --- Who plays (bridge uses all instrument tracks) ---
    var players = (controller != null && controller.tracks != null)
        ? new List<InstrumentTrack>(controller.tracks)
        : new List<InstrumentTrack>();
    Debug.Log($"Clearing Screen for Bridge...");
    //controller?.RemixAllTracksForBridge(phaseNow, sig);
    // --- PRE: clear the screen before coral (mine nodes + maze) ---
    ClearScreenForBridge(); // no-yield, kicks internal coroutines if needed

    // --- Prep coral (skips cleanly if prefab/component missing) ---
    CoralVisualizer coral = null;
    List<PhaseSnapshot> bridgeSnaps = null;
    coral = EnsureCoral(); // returns null if prefab or component missing
    if (!disableCoralDuringBridge)
    {
        coral = EnsureCoral();
        if (coral != null && sig.growCoral)
        {
            Debug.Log($"Coral not null");
            bridgeSnaps = new List<PhaseSnapshot> {
                new PhaseSnapshot {
                    Color = nextStarColor,
                    CollectedNotes = new List<PhaseSnapshot.NoteEntry>()
                }
            };
            coral.gameObject.SetActive(true);
        }

    }

    // --- Freeze gameplay affordances + soften visuals ---
    if (sig.includeDrums && activeDrumTrack) activeDrumTrack.SetBridgeAccent(true);
    controller?.SetSpawningEnabled(false);
    if (noteViz && sig.fadeRibbons) yield return StartCoroutine(noteViz.FadeRibbonsTo(0f, 0.25f));

    // --- Apply temporary musical overrides to bridge players ---
    var saved = new List<SavedTrackState>();
    foreach (var t in players)
    {
        var ns = t?.GetActiveNoteSet();
        if (ns == null) continue;
        saved.Add(new SavedTrackState(t, ns.noteBehavior, ns.rhythmStyle));
        Debug.Log($"Saving track state {t} with note behavior {ns.noteBehavior} and {ns.rhythmStyle}");
        ns.ChangeNoteBehavior(t, sig.noteBehaviorOverride);
        ns.rhythmStyle = sig.rhythmOverride;
    }

    // --- Harmony commit timing ---
    bool midCommitted = false;
    if (sig.commitTiming == HarmonyCommit.AtBridgeStart) harmony?.CommitNextChordNow();

    // --- Coral timing (fade in/out total == bridgeSecs) ---
    float fadeIn  = Mathf.Min(0.30f, bridgeSecs * 0.25f);
    float fadeOut = Mathf.Min(0.30f, bridgeSecs * 0.25f);
    float sampleStep    = loopLen / 8f;
    double nextSampleDsp = AudioSettings.dspTime + sampleStep;

    // =========================
    // try/finally (NO yield in finally)
    // =========================
    try
    {
        if (coral != null && bridgeSnaps != null)
            yield return StartCoroutine(FadeCoralAlpha(coral, 0.65f, fadeIn));

        while (AudioSettings.dspTime < endDsp)
        {
            if (!midCommitted && sig.commitTiming == HarmonyCommit.MidBridge &&
                AudioSettings.dspTime - startDsp >= bridgeSecs * 0.5f)
            {
                harmony?.CommitNextChordNow();
                midCommitted = true;
            }

            // Lightweight coral “echo” sampling
            if (coral != null && bridgeSnaps != null && AudioSettings.dspTime >= nextSampleDsp)
            {
                nextSampleDsp += sampleStep;
                foreach (var t in players)
                {
                    int midi = 60; //try { midi = t.GetRepresentativeMidi(); } catch { }
                    int vel  = Mathf.RoundToInt(Mathf.Lerp(90f, 120f, UnityEngine.Random.value));
                    var col  = t != null ? t.trackColor : nextStarColor;
                    bridgeSnaps[0].CollectedNotes.Add(new PhaseSnapshot.NoteEntry(0, midi, vel, col) { Step=0, Note=midi, Velocity=vel, TrackColor=col });
                }
            }

            yield return null;
        }

        if (sig.commitTiming == HarmonyCommit.AtBridgeEnd) harmony?.CommitNextChordNow();

        if (coral != null && bridgeSnaps != null)
        {
            coral.GenerateCoralFromSnapshots(bridgeSnaps);
            yield return StartCoroutine(FadeCoralAlpha(coral, 0.00f, fadeOut));
            coral.gameObject.SetActive(false);
        }
    }
    finally
    {
        // 1) Restore original behaviors (unchanged)
        foreach (var s in saved)
        {
            var ns = s.track?.GetActiveNoteSet();
            if (ns == null) continue;
            ns.ChangeNoteBehavior(s.track, s.originalBehavior);
            ns.rhythmStyle = s.originalRhythm;
        }

        // 2) Apply seed/remix/clear BEFORE regrowing the maze (unchanged)
        var all = controller != null && controller.tracks != null
            ? controller.tracks.Where(t => t != null).ToList()
            : new List<InstrumentTrack>();
        var nonGroove = all.Where(t => t.assignedRole != MusicalRole.Groove).ToList();
        int clearCount = Mathf.Clamp((nonGroove.Count >= 3) ? UnityEngine.Random.Range(2, 4) : 2, 1, Mathf.Max(1, nonGroove.Count));
        var toClear = nonGroove.OrderBy(_ => UnityEngine.Random.value).Take(clearCount).ToList();
        var toRemix = all.Except(toClear).ToList();
        foreach (var t in toClear) { /* your clear logic */ }
        foreach (var t in toRemix) { /* your remix-for-nextPhase logic */ }

        var seeds = PickSeeds(all, sig.seedTrackCountNextPhase, sig.preferredSeedRoles);
        controller?.ApplyPhaseSeedOutcome(nextPhase, seeds); // no-yield
        controller?.ApplySeedVisibility(seeds);              // no-yield

        // ✅ after bridge, before maze gen
        if (phaseTransitionManager.currentPhase != nextPhase)
        {
            Debug.Log($"[BRIDGE] PTM set to {nextPhase} (was {phaseTransitionManager.currentPhase})");
            phaseTransitionManager.HandlePhaseTransition(nextPhase, "GFM/BridgeEnd");
        }

        // ✅ arm once
        activeDrumTrack?.SchedulePhaseAndLoopChange(nextPhase);

        // ✅ kick off new ecological phase
        progressionManager.BootFirstPhaseStar(nextPhase, regenerateMaze: true);

        // 6) Unfreeze gameplay *after* we dispatched the ecology cycle
        controller?.SetSpawningEnabled(true);
        if (sig.includeDrums && activeDrumTrack) activeDrumTrack.SetBridgeAccent(false);
        GhostCycleInProgress = false;
    }

    // Post-g: fade ribbons back (OK to yield here)
    if (noteViz && sig.fadeRibbons) yield return StartCoroutine(noteViz.FadeRibbonsTo(0.55f, 0.25f));
}
    private void ClearScreenForBridge()
    {
        // 1) Despawn lingering mined objects
        if (activeDrumTrack != null)
        {
            activeDrumTrack.ClearAllActiveMinedObjects();
            activeDrumTrack.ClearAllActiveMineNodes();            
        }

        // 2) Clear the maze immediately (or start a quick fade-out if you prefer)
        if (activeDrumTrack != null && dustGenerator != null)
            dustGenerator.ClearMaze();
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
// GameFlowManager.cs
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
    private IEnumerator SpawnNextStarWatchdog(MusicalPhase phase, float timeoutSec)
    {
        if (progressionManager == null || activeDrumTrack == null)
            yield break;

        // Wait up to timeoutSec for any star to come online
        float t = 0f;
        while (t < timeoutSec)
        {
            if (activeDrumTrack.isPhaseStarActive) // PhaseStar.Initialize sets this
            {
                Debug.Log($"[Bridge] ✅ New PhaseStar active for {phase}");
                yield break;
            }
            t += Time.deltaTime;
            yield return null;
        }

        // Only now do a single guarded spawn attempt
        if (!activeDrumTrack.isPhaseStarActive)
        {
            Debug.LogWarning($"[Bridge] ⚠️ No star after {timeoutSec:0.00}s. Spawning once via progression.");
//            progressionManager.SpawnNextPhaseStarWithoutLoopChange();
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
        _tracksRegistered = true;
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
