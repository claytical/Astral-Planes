using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

public enum GameState { Begin, Selection, Playing, GameOver }

public class GameFlowManager : MonoBehaviour
{
    public static GameFlowManager Instance { get; private set; }
    public struct PhaseChordMap { public MusicalPhase phase; public ChordProgressionProfile progression; }

    public HarmonyDirector harmony;
    private List<PhaseChordMap> chordMaps = new();

    [Header("Settings")]
    public float quoteDisplayDuration = 5f;
    public float fadeDuration = 1f;

    public List<LocalPlayer> localPlayers = new();
    private GameState currentState = GameState.Begin;
    public GlitchManager glitch; // Assign dynamically if needed

    private Dictionary<string, Action> sceneHandlers;
    private List<Vehicle> vehicles;
    public List<Vehicle> GetAllVehicles() => vehicles;
    private bool hasGameOverStarted = false;
    public bool IsBridgeActive => ghostCycleInProgress;

// --- Ghost/burst state so PhaseStar can wait correctly ---
    public bool ghostCycleInProgress { get; private set; }

    private List<InstrumentTrack> activeTracks = new();
    public InstrumentTrackController controller;
    public DrumTrack activeDrumTrack;
    public NoteSetFactory noteSetFactory;
// At top-level fields
    private bool remixArmed = false;                  // true once previous star’s set is completed
    private MusicalPhaseProfile pendingPhaseProfile;  // optional: set when the next star is known
    public bool IsRemixArmed => remixArmed;
    private bool _nextPhaseLoopArmed = false;
    private MusicalPhase _armedNextPhase;
    public event System.Action<MusicalPhaseProfile> OnRemixCommitted;
    // GameFlowManager.cs (fields)
    private CoralVisualizer _coralInstance;
    public GameObject coralPrefab;  
    public NoteVisualizer noteViz;    // assign in scene
    public MineNodeProgressionManager progressionManager; // ensure assigned

// Save/restore container for temporary overrides
private struct SavedTrackState {
    public InstrumentTrack track;
    public NoteBehavior originalBehavior;
    public RhythmStyle originalRhythm;
    public SavedTrackState(InstrumentTrack t, NoteBehavior b, RhythmStyle r) { track=t; originalBehavior=b; originalRhythm=r; }
}

// Entry point: call me from PhaseStar on completion
public void BeginPhaseBridge(MusicalPhase from, MusicalPhase to, List<InstrumentTrack> perfectTracks, Color nextStarColor)
{
    Debug.Log($"Beginning phase bridge {from} to {to}");
    var sig = BridgeLibrary.For(from, to);
    StartCoroutine(PlayPhaseBridge(sig, to, perfectTracks, nextStarColor));
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

private IEnumerator _SpawnNextStarWatchdog(string tag, float timeoutSec = 1.5f)
{
    if (progressionManager == null || activeDrumTrack == null)
        yield break;

    // Try once
    progressionManager.SpawnNextPhaseStarWithoutLoopChange();

    // Wait for DrumTrack to report the new star is active
    float t = 0f;
    while (t < timeoutSec)
    {
        if (activeDrumTrack.isPhaseStarActive) yield break;
        t += Time.deltaTime;
        yield return null;
    }

    // Retry once, louder
    Debug.LogWarning($"[Bridge/SpawnWatchdog] No PhaseStar after {timeoutSec:0.00}s ({tag}). Retrying spawn.");
    progressionManager.SpawnNextPhaseStarWithoutLoopChange();

    // Wait a bit more, then give a last-ditch log (so we know where to look next)
    t = 0f;
    while (t < 1.0f)
    {
        if (activeDrumTrack.isPhaseStarActive) yield break;
        t += Time.deltaTime;
        yield return null;
    }
    Debug.LogError("[Bridge/SpawnWatchdog] PhaseStar still not active. Check DrumTrack.SpawnPhaseStar and PhaseQueue setup.");
}

private IEnumerator FadeCoralAlpha(CoralVisualizer cv, float target, float seconds)
{
    if (cv == null) yield break;
    float t = 0f;
    var lineRs = cv.GetComponentsInChildren<LineRenderer>(includeInactive: true);
    var meshRs = cv.GetComponentsInChildren<MeshRenderer>(includeInactive: true);
    var spriteRs = cv.GetComponentsInChildren<SpriteRenderer>(includeInactive: true);

    // snapshot
    var startLine = new Dictionary<LineRenderer,(float,float)>();
    foreach (var lr in lineRs) startLine[lr] = (lr.startColor.a, lr.endColor.a);
    var startMesh = new Dictionary<MeshRenderer,float>();
    foreach (var mr in meshRs) startMesh[mr] = mr.material.color.a;
    var startSprite = new Dictionary<SpriteRenderer,float>();
    foreach (var sr in spriteRs) startSprite[sr] = sr.color.a;

    while (t < seconds)
    {
        t += Time.deltaTime;
        float u = Mathf.SmoothStep(0f,1f, Mathf.Clamp01(t/seconds));

        foreach (var lr in lineRs)
        {
            var (sa, ea) = startLine[lr];
            var c0 = lr.startColor; c0.a = Mathf.Lerp(sa, target, u);
            var c1 = lr.endColor;   c1.a = Mathf.Lerp(ea, target, u);
            lr.startColor = c0; lr.endColor = c1;
        }
        foreach (var mr in meshRs)
        {
            var c = mr.material.color; c.a = Mathf.Lerp(startMesh[mr], target, u);
            mr.material.color = c;
        }
        foreach (var sr in spriteRs)
        {
            var c = sr.color; c.a = Mathf.Lerp(startSprite[sr], target, u);
            sr.color = c;
        }
        yield return null;
    }
}

private IEnumerator PlayPhaseBridge(
    PhaseBridgeSignature sig,
    MusicalPhase nextPhase,
    List<InstrumentTrack> perfectTracks,
    Color nextStarColor)
{
    ghostCycleInProgress = true;

    // --- Timing locked to loop length ---
    float loopLen = activeDrumTrack ? Mathf.Max(0.05f, activeDrumTrack.GetLoopLengthInSeconds()) : 4f;
    int   bars    = Mathf.Max(1, sig.bars);
    float bridgeSecs = Mathf.Min(bars * loopLen, 20f);  // hard cap safety

    double startDsp = AudioSettings.dspTime;
    double endDsp   = startDsp + bridgeSecs;

    // --- Stage harmony for NEXT phase (no audible retune yet) ---
    var nextProf = GetProfileForPhase(nextPhase);
    harmony?.SetActiveProfile(nextProf, applyImmediately:false);

    // --- Who plays (perfect-only by default) ---
    var players = new List<InstrumentTrack>();
    var cand = (sig.useOnlyPerfectTracks && perfectTracks != null && perfectTracks.Count > 0)
        ? new List<InstrumentTrack>(perfectTracks)
        : (controller != null && controller.tracks != null ? new List<InstrumentTrack>(controller.tracks) : new List<InstrumentTrack>());
    for (int i = 0; i < cand.Count && players.Count < sig.maxBridgeTracks; i++)
        if (cand[i] != null) players.Add(cand[i]);

    // --- PRE: clear the screen before coral (mine nodes + maze) ---
    ClearScreenForBridge(); // no-yield, kicks internal coroutines if needed

    // --- Prep coral (skips cleanly if prefab/component missing) ---
    CoralVisualizer coral = null;
    List<PhaseSnapshot> bridgeSnaps = null;
    coral = EnsureCoral(); // returns null if prefab or component missing
    if (coral != null && sig.growCoral)
    {
        bridgeSnaps = new List<PhaseSnapshot> {
            new PhaseSnapshot {
                Color = nextStarColor,
                CollectedNotes = new List<PhaseSnapshot.NoteEntry>()
            }
        };
        coral.gameObject.SetActive(true);
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
        // --- NON-yield cleanup & setup for new phase ---

        // 1) Restore original behaviors
        foreach (var s in saved)
        {
            var ns = s.track?.GetActiveNoteSet();
            if (ns == null) continue;
            ns.ChangeNoteBehavior(s.track, s.originalBehavior);
            ns.rhythmStyle = s.originalRhythm;
        }

        // 2) Apply seed/remix/clear BEFORE regrowing the maze
        var seeds = PickSeeds(players, sig.seedTrackCountNextPhase, sig.preferredSeedRoles);
        controller?.ApplyPhaseSeedOutcome(nextPhase, seeds); // no-yield
        controller?.ApplySeedVisibility(seeds);              // no-yield

        // 3) Kick a loop-aligned maze transition/build for the new phase visuals
        StartCoroutine(GenerateMazeAndPlaceStar(nextPhase));
        // 4) Spawn next star with watchdog so we can’t stall
        StartCoroutine(SpawnNextStarWatchdog(nextPhase, 2f));

        // 5) Unfreeze + clear accent + release bridge flag
        controller?.SetSpawningEnabled(true);
        if (sig.includeDrums && activeDrumTrack) activeDrumTrack.SetBridgeAccent(false);
        ghostCycleInProgress = false;
    }

    // Post-finally: fade ribbons back (OK to yield here)
    if (noteViz && sig.fadeRibbons) yield return StartCoroutine(noteViz.FadeRibbonsTo(0.55f, 0.25f));
}
// Clear all on-screen mined objects + current maze before coral
private void ClearScreenForBridge()
{
    // 1) Despawn lingering mined objects
    if (activeDrumTrack != null)
        activeDrumTrack.ClearAllActiveMinedObjects();

    // 2) Clear the maze immediately (or start a quick fade-out if you prefer)
    if (activeDrumTrack != null && activeDrumTrack.hexMazeGenerator != null)
        activeDrumTrack.hexMazeGenerator.ClearMaze();
}

// Start a loop-aligned rebuild for the next phase’s maze visuals
public void BeginMazeTransitionForNextPhase(float loopLenSeconds)
{
    // If your maze generator has a timed build, call it here.
    // Else, just rebuild now to keep order deterministic.
    if (activeDrumTrack != null && activeDrumTrack.hexMazeGenerator != null)
    {
        
    }
}
// GameFlowManager.cs
private IEnumerator GenerateMazeAndPlaceStar(MusicalPhase nextPhase)
{
    // Resolve from DrumTrack (source of truth)
    var drum = activeDrumTrack ?? FindObjectOfType<DrumTrack>();
    if (drum == null)
    {
        Debug.LogError("[Bridge] DrumTrack is null; cannot build maze.");
        yield break;
    }

    var gen  = drum.hexMazeGenerator;                       // may be null (fallback below)
    var prog = drum.progressionManager ?? progressionManager;
    if (prog == null)
    {
        Debug.LogError("[Bridge] progressionManager is null; cannot select spawn strategy.");
        yield break;
    }

    // 1) Get the MusicalPhaseGroup for this phase
    var group = prog.GetPhaseGroup(nextPhase);              // see §2 below
    if (group == null)
    {
        Debug.LogError($"[Bridge] No MusicalPhaseGroup found for phase {nextPhase}.");
        yield break;
    }

    // 2) Select a strategy using your existing API
    var profile = prog.SelectSpawnStrategy(group);
    if (profile == null)
    {
        Debug.LogError($"[Bridge] No SpawnStrategyProfile available for group {group} ({nextPhase}).");
        yield break;
    }

    // 3) Pre-clear any lingering objects so growth isn’t occluded
    drum.ClearAllActiveMinedObjects();

    if (gen == null)
    {
        // No generator? Fallback to direct random-cell spawn.
        Debug.LogWarning("[Bridge] No hexMazeGenerator; falling back to DrumTrack.SpawnPhaseStar.");
        drum.SpawnPhaseStar(nextPhase, profile);
        yield break;
    }

    // 4) Clear old maze, then build + place star via your robust pipeline
    gen.ClearMaze();
    yield return StartCoroutine(gen.GenerateMazeThenPlacePhaseStar(nextPhase, profile));

    // 5) Safety: if placement failed silently, try one direct spawn attempt
    if (!drum.isPhaseStarActive)
    {
        Debug.LogWarning("[Bridge] Maze built but no PhaseStar active; attempting direct DrumTrack.SpawnPhaseStar.");
        drum.SpawnPhaseStar(nextPhase, profile);
    }
}


// Spawn watchdog: guarantees a new PhaseStar or logs loudly
private IEnumerator SpawnNextStarWatchdog(MusicalPhase phase, float timeoutSec)
{
    if (progressionManager == null || activeDrumTrack == null)
        yield break;

    progressionManager.SpawnNextPhaseStarWithoutLoopChange();

    float t = 0f;
    while (t < timeoutSec)
    {
        if (activeDrumTrack.isPhaseStarActive)
        {
            Debug.Log($"[Bridge] ✅ New PhaseStar active for {phase}");
            yield break;
        }
        t += Time.deltaTime;
        yield return null;
    }

    Debug.LogWarning($"[Bridge] ⚠️ PhaseStar for {phase} not active after {timeoutSec:0.00}s. Retrying.");
    progressionManager.SpawnNextPhaseStarWithoutLoopChange();

    t = 0f;
    while (t < 1.0f)
    {
        if (activeDrumTrack.isPhaseStarActive) yield break;
        t += Time.deltaTime;
        yield return null;
    }
    Debug.LogError("[Bridge] ❌ PhaseStar still not active. Check DrumTrack.SpawnPhaseStar / phase group setup.");
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

// helper already exists in your class: returns ChordProgressionProfile for a phase
private ChordProgressionProfile GetProfileForPhase(MusicalPhase phase)
{
    for (int i = 0; i < chordMaps.Count; i++) if (chordMaps[i].phase == phase) return chordMaps[i].progression;
    return null;
}

    public void ArmNextPhaseLoop(MusicalPhase nextPhase)
    {
        _armedNextPhase = nextPhase;
        _nextPhaseLoopArmed = true;

        // New: pre-stage the chord progression for Option C
        var prof = GetProfileForPhase(nextPhase);
        harmony?.SetActiveProfile(prof, applyImmediately: false);
    }
    
    private void CheckAllTracksExpired()
    {
        Debug.Log($"[CheckAllTracksExpired] hasGameOverStarted={hasGameOverStarted}, activeTracks={activeTracks.Count}, ghostCycleInProgress={ghostCycleInProgress}");

        if (!hasGameOverStarted && activeTracks.Count == 0 && !ghostCycleInProgress)
        {
            Debug.Log("🛑 All instrument tracks expired. Triggering game over.");
            StartCoroutine(HandleGameOverSequence());
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
    }

    public GameState CurrentState
    {
        get => currentState;
        set => currentState = value;
    }

    public void RegisterPlayer(LocalPlayer player)
    {
        localPlayers.Add(player);
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
    
    public void CheckAllPlayersReady()
    {
        if (localPlayers.All(p => p.IsReady))
        {
            StartCoroutine(TransitionToScene("GeneratedTrack"));
        }
    }

    public void CheckAllPlayersOutOfEnergy()
    {
        if (hasGameOverStarted) return;
        if (ghostCycleInProgress) return;
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

        var quoteGO = FindByNameIncludingInactive("QuoteText");
        var quoteText = quoteGO?.GetComponent<TextMeshProUGUI>();
        if (quoteText != null)
        {
            quoteText.text = "Your harmony echoes among the stars.";
            quoteText.alpha = 0;

            for (float t = 0; t < 1f; t += Time.deltaTime)
            {
                quoteText.alpha = Mathf.Lerp(0, 1, t);
                yield return null;
            }
        }

        yield return new WaitForSeconds(4f);
        StartCoroutine(TransitionToScene("TrackFinished"));
    }

    private void HandleTrackSceneSetup()
    {
        vehicles.Clear();
        currentState = GameState.Playing;
        activeDrumTrack = FindAnyObjectByType<DrumTrack>();
        activeDrumTrack?.ManualStart();
        harmony = FindAnyObjectByType<HarmonyDirector>();
        var arp   = FindAnyObjectByType<ChordChangeArpeggiator>();
        controller = FindAnyObjectByType<InstrumentTrackController>();
        if (controller != null)
        {
            Debug.Log($"Configuring Tracks from Ships...");
            controller.ConfigureTracksFromShips(
                localPlayers.Select(p => ShipMusicalProfileLoader.GetProfile(p.GetSelectedShipName())).ToList(), controller.noteSetPrefab
            );
        }
        // e.g., GameFlowManager.HandleTrackSceneSetup()
        arp.Bind(activeDrumTrack, controller);


        foreach (var player in localPlayers)
        {
            player.Launch(FindAnyObjectByType<DrumTrack>());
            vehicles.Add(player.plane);
        }
        harmony.Initialize(activeDrumTrack, controller, arp);

        // Apply the progression for the phase we’re starting in
        var prof = GetProfileForPhase(activeDrumTrack.currentPhase);
        harmony.SetActiveProfile(prof, applyImmediately: true);        
    }

    private void HandleTrackFinishedSceneSetup()
    {
        currentState = GameState.GameOver;
        harmony?.CancelBoostArp();
        harmony?.Initialize(null, null, null); // drops subscriptions safely
    }
    public void StartShipSelectionPhase()
    {
        CurrentState = GameState.Selection;
        // No need to call PlayerInput.Instantiate — joining is handled by PlayerInputManager
        Debug.Log("✅ Ship selection phase started. Waiting for players to join.");
    }

    public string SelectedMode { get; private set; }

    public void SetSelectedMode(string mode)
    {
        SelectedMode = mode;
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


    public bool ReadyToPlay()
    {
        return CurrentState == GameState.Playing && localPlayers.Count > 0;
    }

    public Transform GetUIParent()
    {
        var visualizer = FindFirstObjectByType<NoteVisualizer>();
        if (visualizer == null) return null;
        
        Transform t = visualizer.GetUIParent().Find("GameStats");
        Debug.Log($"{t.name} found");
        // Search for a child named "GameStats" in its UI hierarchy
        return visualizer.GetUIParent().Find("GameStats");
    }
    
}
