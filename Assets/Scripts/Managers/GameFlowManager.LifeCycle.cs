using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

public partial class GameFlowManager
{
    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
            vehicles = new List<Vehicle>();
            SessionState = new SessionStateCoordinator(this);
            BridgeFlow = new BridgeCoordinator(this, SessionState);
            SceneFlow = new SceneFlowCoordinator(this, SessionState, BridgeFlow);
            localPlayers = SessionState.MutablePlayers;
            SceneManager.sceneLoaded += OnSceneLoaded;
            return;
        }

        Destroy(gameObject);
    }

    private void OnDestroy()
    {
        if (Instance == this)
            SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (scene.name == "TrackSelection" && CurrentState != GameState.Selection)
            StartShipSelectionPhase();
    }
}
