using System.Collections;
using UnityEngine;

public partial class GameFlowManager
{
    public void BeginMotifBridge(string who)
    {
        if (GhostCycleInProgress || BridgePending) return;
        StartCoroutine(BridgeFlow.PlayMotifBridgeAndRestart());
    }
}
