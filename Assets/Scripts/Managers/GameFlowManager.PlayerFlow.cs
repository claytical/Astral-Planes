using UnityEngine;

public partial class GameFlowManager
{
    public void RegisterPlayer(LocalPlayer player) => SessionState.RegisterPlayer(player);

    /// <summary>Removes a single player backing out of ship select. Re-shows the join prompt if that was the last one.</summary>
    public void UnregisterPlayer(LocalPlayer player)
    {
        if (SessionState.UnregisterPlayer(player))
        {
            ControlTutorialDirector.Instance?.ShowJoinPrompt();
            return;
        }

        // Someone is still here. Don't strand them: the mode lock only made sense for the old
        // roster, and if everyone left standing is already ready (e.g. the player who left was
        // the only one still choosing) the session should proceed instead of waiting forever.
        var tutorial = ControlTutorialDirector.Instance;
        bool tutorialRunning = tutorial != null && tutorial.IsPrimaryTutorialRunning;
        if (CurrentState == GameState.Selection && !tutorialRunning)
        {
            SessionState.UnlockMode();
            CheckAllPlayersReady();
        }
    }

    /// <summary>
    /// A single player holding East mid-tutorial: they leave, the others carry on. Only when
    /// nobody would remain does this fall back to the full abort back to the join screen.
    /// </summary>
    public void AbortTutorialForPlayer(LocalPlayer player)
    {
        int remaining = 0;
        foreach (var p in SessionState.Players)
            if (p != null && p != player) remaining++;

        if (remaining == 0)
        {
            AbortTutorialSession();
            return;
        }

        ControlTutorialDirector.Instance?.UnregisterMini(player);
        player.LeaveSessionNow();
    }

    /// <summary>Aborts an in-progress tutorial (held-East back-out): tears down all joined players and returns to the join screen.</summary>
    public void AbortTutorialSession()
    {
        ControlTutorialDirector.Instance?.AbortPrimaryTutorial();
        AbortSessionCoroutines(); // see QuitToSelection
        StartCoroutine(SceneFlow.ReturnToJoinScreen());
    }

    public bool ReadyToPlay() => SessionState.ReadyToPlay();

    private void SetBridgeVisualMode(bool on)
    {
        if (dustGenerator) dustGenerator.activeDustRoot.gameObject.SetActive(!on);
        if (noteViz && noteViz.GetUIParent())
            noteViz.GetUIParent().gameObject.SetActive(!on);
    }

    public void CheckAllPlayersReady()
    {
        // The control lesson is no longer a TrackSelection screen: everyone ready -> GeneratedTrack,
        // which opens in the control sandbox (ControlSandboxDirector) before the first motif.
        SessionState.CheckAllPlayersReady(
            beginTutorial: BeginGameAfterTutorial,
            beginGameplay: BeginGameAfterTutorial);
    }

    /// <summary>Multiplayer: the extra confirm after everyone is ready locks the mode and starts the tutorial.</summary>
    public void ConfirmModeSelection()
    {
        SessionState.LockMode();
        CheckAllPlayersReady();
    }

    /// <summary>
    /// Set when leaving TrackSelection for a new session; consumed by
    /// SceneFlowCoordinator.HandleTrackSceneSetupAsync, which then opens in the control sandbox.
    /// </summary>
    public bool RunControlSandbox { get; set; }

    /// <summary>True while the GeneratedTrack control sandbox is running (before its path is taken).</summary>
    public static bool SandboxActive { get; set; }

    public void BeginGameAfterTutorial()
    {
        // Solo: the sandbox runs once — only while the tutorial isn't done (no ring saved yet), and
        // it leads into the Tutorial Motif. Multiplayer: it always opens the session, then the
        // chosen motif grows in around the ships.
        int playerCount = 0;
        foreach (var p in SessionState.Players) if (p != null) playerCount++;
        bool tutorialDone = RingSessionStore.LoadAllRingsFromDisk().Count > 0;
        RunControlSandbox = playerCount > 1 || !tutorialDone;
        // Raised now, not when the sandbox starts: NarratedTutorialController starts on scene load
        // (before setup reaches the sandbox) and would otherwise see phase 0 and begin narrating.
        SandboxActive = RunControlSandbox;
        SessionGenome.BootNewSessionSeed((int)Random.Range(0, 1000f));
        StartCoroutine(SceneFlow.TransitionToScene("GeneratedTrack"));
    }

    public void StartShipSelectionPhase() => SessionState.StartShipSelectionPhase();

    public void CheckAllPlayersOutOfEnergy()
    {
        if (SessionState.IsCompetitive)
        {
            CheckCompetitiveMatchEnd();
            return;
        }

        if (SessionState.CheckAllPlayersOutOfEnergy())
            StartCoroutine(HandleGameOverSequence());
    }
}
