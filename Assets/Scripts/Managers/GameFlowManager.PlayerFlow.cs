using System.Linq;
using UnityEngine;
using Debug = UnityEngine.Debug;

public partial class GameFlowManager
{
    public void RegisterPlayer(LocalPlayer player)
    {
        localPlayers.Add(player);
    }

    public bool ReadyToPlay()
    {
        return CurrentState == GameState.Playing && localPlayers.Count > 0;
    }

    private void SetBridgeVisualMode(bool on)
    {
        // When ON: hide gameplay visuals (maze + noteviz), coral is shown by PlayPhaseBridge.
        // When OFF: show gameplay visuals again.
        if (dustGenerator) dustGenerator.activeDustRoot.gameObject.SetActive(!on);

        if (noteViz && noteViz.GetUIParent())
            noteViz.GetUIParent().gameObject.SetActive(!on);
    }

    public void CheckAllPlayersReady()
    {
        if (!localPlayers.All(p => p.IsReady)) return;

        // Don’t load GeneratedTrack yet — show the primary tutorial sequence first.
        if (ControlTutorialDirector.Instance != null)
        {
            Debug.Log($"[TUTORIAL] Begin Tutorial Sequence");
            ControlTutorialDirector.Instance.BeginPrimaryTutorialSequence();
            return;
        }

        // Fallback if director is missing
        SessionGenome.BootNewSessionSeed((int)UnityEngine.Random.Range(0, 1000f));
        StartCoroutine(TransitionToScene("GeneratedTrack"));
    }

    public void BeginGameAfterTutorial()
    {
        SessionGenome.BootNewSessionSeed((int)UnityEngine.Random.Range(0, 1000f));
        StartCoroutine(TransitionToScene("GeneratedTrack"));
    }

    public void StartShipSelectionPhase()
    {
        CurrentState = GameState.Selection;
        // No need to call PlayerInput.Instantiate — joining is handled by PlayerInputManager
        Debug.Log("✅ Ship selection phase started. Waiting for players to join.");
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
}
