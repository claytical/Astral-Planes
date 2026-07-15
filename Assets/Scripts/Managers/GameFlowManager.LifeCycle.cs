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
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (scene.name != "TrackSelection") return;

        if (CurrentState != GameState.Selection)
            StartShipSelectionPhase();

        // Reset plane availability so the Hangar's persisting (DontDestroyOnLoad) state
        // doesn't block ship selection with planes marked in-use from the previous session.
        Hangar.Instance?.ResetForNewSession();

        // Any LocalPlayers that persisted from a prior scene (e.g. joined during the
        // main menu) won't have a ship-selection UI because their Start() ran while
        // the scene name was "Main". Recreate it now so they land in a ready state.
        SessionState.CleanupDestroyedPlayers();
        foreach (var player in SessionState.Players)
        {
            if (player == null) continue;
            player.ResetReady();
            player.CreatePlayerSelect();
        }
    }
}
