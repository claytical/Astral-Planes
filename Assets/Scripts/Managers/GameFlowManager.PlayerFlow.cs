using UnityEngine;

public partial class GameFlowManager
{
    public void RegisterPlayer(LocalPlayer player) => SessionState.RegisterPlayer(player);

    /// <summary>Removes a single player backing out of ship select. Re-shows the join prompt if that was the last one.</summary>
    public void UnregisterPlayer(LocalPlayer player)
    {
        if (SessionState.UnregisterPlayer(player))
            ControlTutorialDirector.Instance?.ShowJoinPrompt();
    }

    /// <summary>Aborts an in-progress tutorial (held-East back-out): tears down all joined players and returns to the join screen.</summary>
    public void AbortTutorialSession()
    {
        ControlTutorialDirector.Instance?.AbortPrimaryTutorial();
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
        SessionState.CheckAllPlayersReady(
            beginTutorial: () =>
            {
                if (GameFlowManager.VerboseLogging) Debug.Log("[TUTORIAL] Begin Tutorial Sequence");
                ControlTutorialDirector.Instance.BeginPrimaryTutorialSequence();
            },
            beginGameplay: BeginGameAfterTutorial);
    }

    public void BeginGameAfterTutorial()
    {
        SessionGenome.BootNewSessionSeed((int)Random.Range(0, 1000f));
        StartCoroutine(SceneFlow.TransitionToScene("GeneratedTrack"));
    }

    public void StartShipSelectionPhase() => SessionState.StartShipSelectionPhase();

    public void CheckAllPlayersOutOfEnergy()
    {
        if (SessionState.CheckAllPlayersOutOfEnergy())
            StartCoroutine(HandleGameOverSequence());
    }
}
