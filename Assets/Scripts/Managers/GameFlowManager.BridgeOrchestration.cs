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

        // Competitive motif mode: the run ends here. The bridge still plays the ring's spin-off, then
        // shows the match results instead of the interstitial maze.
        bool endMatch = SessionState.IsCompetitive && SessionState.Mode == GameMode.CompetitiveMotif;
        StartCoroutine(BridgeFlow.PlayMotifBridgeAndRestart(endMatch));
    }
}
