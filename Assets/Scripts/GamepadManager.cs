using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

public class GamepadManager : MonoBehaviour
{
    public GameObject introScreen;
    public GameObject gameSetupScreen;
    public GameObject finalStatsScreen;
    public GameObject statsPrefab;
    public Hangar hangar;
    public TextMeshProUGUI quoteText; // Reference to your TMP text object
    public float quoteDisplayDuration = 5f; // How long the quote should be visible
    public float fadeDuration = 1f; // Duration of fade in/out

    private List<Gamepad> connectedGamepads;
    private List<LocalPlayer> localPlayers = new List<LocalPlayer>();
    public static GamepadManager Instance { get; private set; }
    private bool gameInProgress = false;

    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }
        else
        {
            Destroy(gameObject);
        }
    }

    void Start()
    {
        connectedGamepads = new List<Gamepad>();
        foreach (var gamepad in Gamepad.all)
        {
            connectedGamepads.Add(gamepad);
        }
    }

    public void RegisterPlayer(LocalPlayer player)
    {
        localPlayers.Add(player);
    }

    public void CheckAllPlayersReady()
    {
        foreach (LocalPlayer player in localPlayers)
        {
            if (!player.IsReady)
            {
                return; // Exit if any player is not ready
            }
        }
        StartGame(); // All players are ready
    }



    public void HideAllPlanes()
    {
        foreach (LocalPlayer player in localPlayers)
        {
            player.Hide();
        }
    }

    public void ShowAllPlanes()
    {
        foreach (LocalPlayer player in localPlayers)
        {
            player.Show();
        }
    }

    public bool CheckAstralPlaneAlignment()
    {
        for(int i = 0; i < localPlayers.Count; i++)
        {
            if(localPlayers[i].astralPlaneIndex != localPlayers[0].astralPlaneIndex)
            {
                return false;
            }
        }
        return true;
    }
    /*
    public void ResetPortal()
    {
        portalComplete = false;
    }

    public void HidePortal()
    {
        portalComplete = true;
    }
    */
    public int MaxItemsCollected()
    {
        int count = 0;
        foreach(LocalPlayer player in localPlayers)
        {
            count += player.GetComponent<PlayerStatsTracking>().itemsCollected;
        }
        return count;
    }

    public void QuitGame()
    {
        SceneManager.LoadScene("TrackSelection");
        Destroy(this.gameObject);
    }
    public void CheckAllPlayersOutOfEnergy()
    {
        bool allOut = true;
        foreach (var player in localPlayers)
        {
            if (player.IsReady && player.GetVehicleEnergy() > 0f)
            {
                allOut = false;
                break;
            }
        }

        if (allOut)
        {
            StartCoroutine(HandleGameOverSequence());
        }
    }
    private IEnumerator HandleGameOverSequence()
    {
        InstrumentTrackController itc = FindObjectOfType<InstrumentTrackController>();
        DrumTrack drumTrack = FindObjectOfType<DrumTrack>();

        // 1. Sustain notes longer
        itc.BeginGameOverFade();

        // 2. Fade to black
        GameObject fadeOverlay = GameObject.Find("FadeOverlay");
        CanvasGroup canvasGroup = fadeOverlay?.GetComponent<CanvasGroup>();

        if (canvasGroup != null)
        {
            for (float t = 0; t < 1f; t += Time.deltaTime)
            {
                canvasGroup.alpha = Mathf.Lerp(0f, 1f, t);
                yield return null;
            }
            canvasGroup.alpha = 1f;
        }

        // 3. Wait for the rest of the current loop
        if (drumTrack != null)
        {
            double remaining = drumTrack.GetRemainingLoopTime();
            yield return new WaitForSeconds((float)remaining);
        }

        // 4. Fade out drum loop volume
        if (drumTrack?.drumAudioSource != null)
        {
            AudioSource music = drumTrack.drumAudioSource;
            float startVolume = music.volume;
            for (float t = 0; t < 1f; t += Time.deltaTime)
            {
                music.volume = Mathf.Lerp(startVolume, 0f, t);
                yield return null;
            }
            music.Stop();
            music.volume = startVolume; // reset for next round
        }

        // 5. Transition to final scene with quote
        LoadNewScene("TrackFinished");
    }

    public void CheckAllPlayersGone()
    {
        foreach (LocalPlayer player in localPlayers)
        {
            if (player.IsReady)
            {
                return; // Exit if any player is not ready
            }
        }
        // ALL PLAYERS GONE
        Debug.Log("Game Over");
        LoadNewScene("TrackFinished");
    }

    private void StartGame()
    {
        Debug.Log("All players are ready. Starting the game!");
        LoadNewScene("GeneratedTrack");
    }

    public void JoinGame(PlayerInput playerInput)
    {
        if (introScreen.activeSelf)
        {
            introScreen.SetActive(false);
            gameSetupScreen.SetActive(true);
        }
        var gamepad = playerInput.devices[0] as Gamepad;
        if (gamepad != null && !connectedGamepads.Contains(gamepad))
        {
            connectedGamepads.Add(gamepad);
        }
    }

    public void LeaveGame(PlayerInput playerInput)
    {
        Debug.Log(playerInput.GetInstanceID().ToString("PLAYER 0 LEFT"));
        var gamepad = playerInput.devices[0] as Gamepad;
        if (gamepad != null && connectedGamepads.Contains(gamepad))
        {
            connectedGamepads.Remove(gamepad);
        }
    }

    public string CurrentScene()
    {
        return SceneManager.GetActiveScene().name;
    }

    public void LoadNewScene(string sceneName)
    {
        StartCoroutine(LoadSceneAsync(sceneName));
    }

    private IEnumerator LoadSceneAsync(string sceneName)
    {
        AsyncOperation asyncLoad = SceneManager.LoadSceneAsync(sceneName);
        asyncLoad.allowSceneActivation = false; // Prevent scene activation until we're ready

        while (!asyncLoad.isDone)
        {
            if (asyncLoad.progress >= 0.9f)
            {
                // Example: Different quotes for different scenes
                if (sceneName.Equals("Track"))
                {
                    string[] trackQuotes = new string[]
                    {
                        "In the dark, we survive by clinging to the light."
                    };
                    yield return StartCoroutine(DisplayRandomQuote(trackQuotes));
                }
                else if (sceneName.Equals("TrackFinished"))
                {
                    string[] finishedQuotes = new string[]
                    {
                        "Every end is a new beginning, written in the fabric of the universe.",
                        "In the silence of the cosmos, your essence lingers.",
                        "The path ends here, but the astral currents will carry you onward.",
                        "Your light may dim, but the astral planes await your return."
                    };
                    yield return StartCoroutine(DisplayRandomQuote(finishedQuotes));
                }
                else if (sceneName.Equals("TrackSelection"))
                {
                    string[] selectionQuotes = new string[]
                    {
                        "Choose your path wisely.",
                        "The road ahead is full of possibilities.",
                        "Select the challenge that suits you best."
                    };
                    yield return StartCoroutine(DisplayRandomQuote(selectionQuotes));
                }

                // Allow the scene to activate
                asyncLoad.allowSceneActivation = true;
            }
            yield return null;
        }
        // Scene is now loaded and activated
        Debug.Log("New scene loaded: " + sceneName);
        HandleSceneSetup(sceneName);
    }

    private void HandleSceneSetup(string sceneName)
    {
        if (sceneName.Equals("GeneratedTrack"))
        {
            HandleTrackSceneSetup();
            StartSelectedTrack();
            GameObject go = GameObject.Find("QuoteText");
            if(go)
            {
                quoteText = go.GetComponent<TextMeshProUGUI>();
            }
        }
        else if (sceneName.Equals("TrackFinished"))
        {
            HandleTrackFinishedSceneSetup();
            GameObject go = GameObject.Find("QuoteText");
            if (go)
            {
                quoteText = go.GetComponent<TextMeshProUGUI>();
            }
        }
        else if (sceneName.Equals("TrackSelection"))
        {
            HandleTrackSelectionSceneSetup();
        }
        else
        {
            Debug.LogWarning($"Unhandled scene: {sceneName}");
        }
    }

    private void StartSelectedTrack()
    {
        InstrumentTrackController instrumentTrackController = FindFirstObjectByType<InstrumentTrackController>();

        DrumTrack drumTrack = FindFirstObjectByType<DrumTrack>();
        drumTrack.ManualStart();
        for (int i = 0; i < localPlayers.Count; i++)
        {
            localPlayers[i].Launch(drumTrack);
        }

        List<ShipMusicalProfile> shipProfiles = localPlayers
            .Select(lp => lp.GetSelectedShipName())
            .Where(name => ShipMusicalProfiles.PresetsByShip.ContainsKey(name))
            .Select(name => ShipMusicalProfiles.PresetsByShip[name])
            .ToList();

        instrumentTrackController.ConfigureTracksFromShips(shipProfiles);
        Debug.Log(instrumentTrackController.GenerateCrypticIntroPhrase());

    }
    private void HandleTrackSceneSetup()
    {
        Debug.Log("Handle Track Scene Setup");
        gameInProgress = true;
    }

    public bool ReadyToPlay()
    {
        return gameInProgress;
    }
    private void HandleTrackFinishedSceneSetup()
    {
        Debug.Log("Track Finished! Show Stats");

        // Find the parent object with the tag "PlayersUI"
        GameObject playersUIParent = GameObject.FindGameObjectWithTag("PlayersUI");

        if (playersUIParent == null)
        {
            Debug.LogError("Parent object with tag 'PlayersUI' not found.");
            return;
        }

        if (statsPrefab == null)
        {
            Debug.LogError("PlayerStatsPrefab not found in Resources.");
            return;
        }

        // Get all players' stats
        PlayerStatsTracking[] allPlayerStats = new PlayerStatsTracking[localPlayers.Count];
        for (int i = 0; i < localPlayers.Count; i++)
        {
            allPlayerStats[i] = localPlayers[i].GetComponent<PlayerStatsTracking>();
        }

    }
    private void HandleTrackSelectionSceneSetup()
    {
        for (int i = 0; i < localPlayers.Count; i++)
        {
            localPlayers[i].CreatePlayerSelect();
            localPlayers[i].SetStats();
        }
    }

    private IEnumerator DisplayRandomQuote(string[] quotes)
    {
        if (quotes == null || quotes.Length == 0)
        {
            Debug.LogWarning("No quotes provided for DisplayRandomQuote.");
            yield break;
        }

        // Select a random quote from the provided array
        string selectedQuote = quotes[Random.Range(0, quotes.Length)];
        quoteText.text = selectedQuote;

        // Fade in
        yield return StartCoroutine(FadeTextIn());

        // Display the quote for the specified duration
        yield return new WaitForSeconds(quoteDisplayDuration);

        // Fade out
        yield return StartCoroutine(FadeTextOut());
    }

    private IEnumerator FadeTextIn()
    {
        float elapsedTime = 0f;
        Color color = quoteText.color;
        while (elapsedTime < fadeDuration)
        {
            elapsedTime += Time.deltaTime;
            color.a = Mathf.Clamp01(elapsedTime / fadeDuration);
            quoteText.color = color;
            yield return null;
        }
    }

    private IEnumerator FadeTextOut()
    {
        float elapsedTime = 0f;
        Color color = quoteText.color;
        while (elapsedTime < fadeDuration)
        {
            elapsedTime += Time.deltaTime;
            color.a = Mathf.Clamp01(1f - (elapsedTime / fadeDuration));
            quoteText.color = color;
            yield return null;
        }
    }

    public void StartRumble(Gamepad gamepad, float lowFrequency, float highFrequency, float duration)
    {
        if (gamepad == null)
        {
            Debug.Log("Gamepad is null");
            return;
        }
        gamepad.SetMotorSpeeds(lowFrequency, highFrequency);
        StartCoroutine(StopRumbleAfterDuration(gamepad, duration));
    }

    public void StartRumble(float lowFrequency, float highFrequency, float duration)
    {
        var device = GetComponent<PlayerInput>().devices[0];
        if (device is UnityEngine.InputSystem.Switch.SwitchProControllerHID switchProController)
        {
            switchProController.SetMotorSpeeds(lowFrequency, highFrequency);
            StartCoroutine(StopRumble(switchProController, duration));
        }
    }

    private IEnumerator StopRumble(UnityEngine.InputSystem.Switch.SwitchProControllerHID switchProController, float duration)
    {
        yield return new WaitForSeconds(duration);
        switchProController.SetMotorSpeeds(0, 0);
    }

    private IEnumerator StopRumbleAfterDuration(Gamepad gamepad, float duration)
    {
        yield return new WaitForSeconds(duration);
        gamepad.SetMotorSpeeds(0, 0);
    }

    public void TriggerRumbleForAll(float lowFrequency, float highFrequency, float duration)
    {
        foreach (var gamepad in connectedGamepads)
        {
            StartRumble(gamepad, lowFrequency, highFrequency, duration);
        }
    }
}
