using UnityEngine;

public partial class GameFlowManager
{
    public void RegisterPlayer(LocalPlayer player) => SessionState.RegisterPlayer(player);

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
                Debug.Log("[TUTORIAL] Begin Tutorial Sequence");
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
