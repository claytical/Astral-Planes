using System.Collections;
using UnityEngine;

public partial class GameFlowManager
{
    /// <summary>Fired when a motif actually reaches its completed state (bridge gate passed), before the next motif's OnMotifChanged.</summary>
    public event System.Action OnMotifCompleted;

    public void BeginMotifBridge(string who)
    {
        if (GhostCycleInProgress || BridgePending) return;
        OnMotifCompleted?.Invoke();
        StartCoroutine(BridgeFlow.PlayMotifBridgeAndRestart());
    }
}
