using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

public enum GameState { Begin, Selection, Playing, GameOver }
[System.Serializable]
public struct CollectedNote
{
    public int step;          // optional for coral (not used there)
    public int note;          // MIDI (0–127); coral doesn’t use it now
    public int velocity;      // 0–127  ← CoralVisualizer uses this
    public Color trackColor;  //         ← CoralVisualizer uses this
}
public class GameFlowManager : MonoBehaviour
{
    public static GameFlowManager Instance { get; private set; }
    [System.Serializable]
    public struct PhaseChordMap { public MusicalPhase phase; public ChordProgressionProfile progression; }

    [Header("Harmony")]
    public HarmonyDirector harmony;
    public List<PhaseChordMap> chordMaps = new();

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
    public CoralVisualizer coralPrefab;  
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
    if (coralPrefab == null) return null;
    _coralInstance = Instantiate(coralPrefab);
    _coralInstance.gameObject.SetActive(false);
    return _coralInstance;
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
    double startDsp = AudioSettings.dspTime;

    // Durations from the current loop
    int   totalSteps = activeDrumTrack ? activeDrumTrack.totalSteps : 16;
    float loopLen    = activeDrumTrack ? activeDrumTrack.GetLoopLengthInSeconds() : 4f;
    float bridgeSecs = Mathf.Max(0.05f, sig.bars * loopLen);

    // Who plays the bridge
    var players = new List<InstrumentTrack>();
    var cand = (sig.useOnlyPerfectTracks && perfectTracks != null && perfectTracks.Count > 0)
        ? new List<InstrumentTrack>(perfectTracks)
        : (controller != null && controller.tracks != null ? new List<InstrumentTrack>(controller.tracks) : new List<InstrumentTrack>());
    for (int i = 0; i < cand.Count && players.Count < sig.maxBridgeTracks; i++)
        if (cand[i] != null) players.Add(cand[i]);

    // Stage harmony for next phase (no audible retune yet)
    var nextProf = GetProfileForPhase(nextPhase);
    harmony?.SetActiveProfile(nextProf, applyImmediately: false);

    // Optional coral (skips cleanly if prefab/instance missing)
    CoralVisualizer coral = null;
    List<PhaseSnapshot> bridgeSnaps = null;
    if (sig.growCoral)
    {
        coral = EnsureCoral();
        if (coral != null)
        {
            bridgeSnaps = new List<PhaseSnapshot> {
                new PhaseSnapshot {
                    color = nextStarColor,
                    collectedNotes = new List<PhaseSnapshot.NoteEntry>()
                }
            };
            coral.gameObject.SetActive(true);
        }
    }

    // Drum accent during bridge
    if (sig.includeDrums && activeDrumTrack) activeDrumTrack.SetBridgeAccent(true);

    // Freeze spawns + fade ribbons down
    controller?.SetSpawningEnabled(false);
    if (noteViz && sig.fadeRibbons) yield return StartCoroutine(noteViz.FadeRibbonsTo(0f, 0.4f));

    // Apply temporary musical overrides
    var saved = new List<SavedTrackState>();
    foreach (var t in players)
    {
        var ns = t?.GetActiveNoteSet();
        if (ns == null) continue;
        saved.Add(new SavedTrackState(t, ns.noteBehavior, ns.rhythmStyle));
        ns.ChangeNoteBehavior(t, sig.noteBehaviorOverride);
        ns.rhythmStyle = sig.rhythmOverride;
    }

    // Harmony commit timing
    bool midCommitted = false;
    if (sig.commitTiming == HarmonyCommit.AtBridgeStart) harmony?.CommitNextChordNow();

    // Coral sampling cadence (approx 1/8th)
    float  sampleStep    = loopLen / 8f;
    double nextSampleDsp = AudioSettings.dspTime + sampleStep;

    // -------- try/finally (no catch!) so yields are legal --------
    try
    {
        // Bridge wait with optional mid-commit and coral echo sampling
        while (AudioSettings.dspTime - startDsp < bridgeSecs)
        {
            float elapsed = (float)(AudioSettings.dspTime - startDsp);

            if (!midCommitted && sig.commitTiming == HarmonyCommit.MidBridge && elapsed >= bridgeSecs * 0.5f)
            {
                harmony?.CommitNextChordNow();
                midCommitted = true;
            }

            if (coral != null && bridgeSnaps != null && AudioSettings.dspTime >= nextSampleDsp)
            {
                nextSampleDsp += sampleStep;

                foreach (var t in players)
                {
                    int midi = 60; // fallback middle C
                    int vel = Mathf.RoundToInt(Mathf.Lerp(90f, 120f, UnityEngine.Random.value));
                    var col = t != null ? t.trackColor : nextStarColor;
                   
                    bridgeSnaps[0].collectedNotes.Add(new PhaseSnapshot.NoteEntry(0, midi, vel, col));
                }
            }

            yield return null;
        }

        if (sig.commitTiming == HarmonyCommit.AtBridgeEnd) harmony?.CommitNextChordNow();

        if (coral != null && bridgeSnaps != null)
        {
            coral.GenerateCoralFromSnapshots(bridgeSnaps);
            yield return StartCoroutine(FadeCoralAlpha(coral, 0.65f, 0.30f)); // fade in
            yield return new WaitForSeconds(0.25f);
            yield return StartCoroutine(FadeCoralAlpha(coral, 0.00f, 0.30f)); // fade out
            coral.gameObject.SetActive(false);
        }
    }
    finally
    {
        // Restore original behaviors
        foreach (var s in saved)
        {
            var ns = s.track?.GetActiveNoteSet();
            if (ns == null) continue;
            ns.ChangeNoteBehavior(s.track, s.originalBehavior);
            ns.rhythmStyle = s.originalRhythm;
        }

        // Seeds for clarity in new phase
        var seeds = PickSeeds(players, sig.seedTrackCountNextPhase, sig.preferredSeedRoles);
        controller?.ApplySeedVisibility(seeds);

        // Fade ribbons back up
        if (noteViz && sig.fadeRibbons)
        {
            StartCoroutine(noteViz.FadeRibbonsTo(0.55f, 0.35f));
        }

        // Spawn next star (bridge owns the handoff)
        progressionManager?.SpawnNextPhaseStarWithoutLoopChange();

        // Unfreeze + clear accent + release flag
        controller?.SetSpawningEnabled(true);
        if (sig.includeDrums && activeDrumTrack) activeDrumTrack.SetBridgeAccent(false);
        ghostCycleInProgress = false;
    }
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
            yield return StartCoroutine(DisplayRandomQuote(sceneName));
            load.allowSceneActivation = true;

            yield return new WaitUntil(() => load.isDone);
        }

        if (sceneHandlers.TryGetValue(sceneName, out var handler))
        {
            handler?.Invoke();
        }
    }

    private IEnumerator DisplayRandomQuote(string sceneName)
    {
        var quoteGO = FindByNameIncludingInactive("QuoteText");
        var quoteText = quoteGO?.GetComponent<TextMeshProUGUI>();
        if (quoteText != null)
        {
            string quote = sceneName switch
            {
                "TrackFinished" => "Whether you burned out or broke through—\nthe system remembers the motion.",
                "TrackSelection" => "Choose your path wisely.",
                "GeneratedTrack" => "The first note is yours.",
                _ => string.Empty
            };

            quoteText.text = quote;
            yield return StartCoroutine(FadeTextIn(quoteText));
            yield return new WaitForSeconds(quoteDisplayDuration);
            yield return StartCoroutine(FadeTextOut(quoteText));
        }
    }

    private IEnumerator FadeTextIn(TextMeshProUGUI quoteText)
    {
        float t = 0f;
        while (t < fadeDuration)
        {
            t += Time.deltaTime;
            var color = quoteText.color;
            color.a = Mathf.Clamp01(t / fadeDuration);
            quoteText.color = color;
            yield return null;
        }
    }

    private IEnumerator FadeTextOut(TextMeshProUGUI quoteText)
    {
        float t = 0f;
        while (t < fadeDuration)
        {
            t += Time.deltaTime;
            var color = quoteText.color;
            color.a = Mathf.Clamp01(1f - t / fadeDuration);
            quoteText.color = color;
            yield return null;
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

        if (galaxy != null && activeDrumTrack.sessionPhases != null)
        {
            foreach (var snapshot in activeDrumTrack.sessionPhases)
            {
                galaxy.AddSnapshot(snapshot);
            }
        }
        ConstellationMemoryStore.StoreSnapshot(activeDrumTrack.sessionPhases);

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
