// PlayerStatsGridAnchor.cs
using UnityEngine;

public sealed class PlayerStatsGridAnchor : MonoBehaviour
{
    void OnEnable()
    {
        var gfm = GameFlowManager.Instance;
        if (gfm) gfm.RegisterPlayerStatsGrid(transform);
    }

    void OnDisable()
    {
        var gfm = GameFlowManager.Instance;
        if (gfm) gfm.UnregisterPlayerStatsGrid(transform);
    }
}