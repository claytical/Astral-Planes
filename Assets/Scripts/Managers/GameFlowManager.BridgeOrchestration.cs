using System.Collections;
using UnityEngine;

public partial class GameFlowManager
{
    public void BeginMotifBridge(string who)
    {
        StartCoroutine(BridgeFlow.PlayMotifBridgeAndRestart());
    }
}
