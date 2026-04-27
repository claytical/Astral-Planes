using System.Collections.Generic;
using UnityEngine;

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
            return;
        }

        Destroy(gameObject);
    }
}
