using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

public enum GameState { Begin, Selection, Playing, GameOver }

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

    private ChordProgressionProfile GetProfileForPhase(MusicalPhase phase)
    {
        Debug.Log($"Getting Profile for Phase: {phase}");
        for (int i = 0; i < chordMaps.Count; i++)
        {
            Debug.Log($"Profile for Phase: {chordMaps[i].phase} / {phase}");
            if (chordMaps[i].phase == phase) return chordMaps[i].progression;
        }
        return null; // optional: fall back to a default
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
