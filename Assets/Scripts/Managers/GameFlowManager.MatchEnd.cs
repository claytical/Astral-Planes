using System.Collections.Generic;
using UnityEngine;

// Competitive-mode match end (elimination / motif completion). Unlike HandleGameOverSequence this
// stays in the GeneratedTrack scene: gameplay freezes, the loop keeps playing, and
// MatchResultsPanel shows the scores until a button press returns to Main.
public partial class GameFlowManager
{
    public bool MatchEnded => SessionState != null && SessionState.MatchEnded;

    private void CheckCompetitiveMatchEnd()
    {
        int alive = SessionState.AliveCount;
        if (alive == 0)
            BeginCompetitiveMatchEnd(MatchEndReason.AllEliminated);
        else if (SessionState.Mode == GameMode.CompetitiveElimination && alive <= 1)
            BeginCompetitiveMatchEnd(MatchEndReason.LastShipStanding);
    }

    // Clears the scene for the results panel: vehicles hidden (renderers only, so vehicle state is
    // untouched), dust faded unless the motif bridge already faded it.
    private void HideGameplayVisuals(MatchEndReason reason)
    {
        foreach (var v in GetVehicles())
            if (v != null)
                foreach (var r in v.GetComponentsInChildren<Renderer>(true))
                    r.enabled = false;

        if (reason != MatchEndReason.MotifComplete && dustGenerator != null)
        {
            dustGenerator.HardStopRegrowthForBridge(hideTransientDust: true);
            dustGenerator.BeginSlowFadeAllDust(1f);
        }
    }

    public void BeginCompetitiveMatchEnd(MatchEndReason reason)
    {
        if (!SessionState.TryBeginMatchEnd()) return;

        BridgeFlow.FreezeGameplayForMatchEnd();
        HideGameplayVisuals(reason);

        var scores = new List<(LocalPlayer player, int total)>();
        foreach (var lp in SessionState.Players)
        {
            if (lp == null) continue;
            lp.SetInputLocked(true);
            var tracking = lp.GetComponent<PlayerStatsTracking>();
            scores.Add((lp, tracking != null ? tracking.CompetitiveTotal : 0));
        }

        var panel = FindAnyObjectByType<MatchResultsPanel>(FindObjectsInactive.Include);
        if (panel == null)
        {
            Debug.LogWarning("[GFM] Competitive match ended but no MatchResultsPanel is in the scene; no button press will return to Main.");
            return;
        }
        panel.Show(reason, scores);
    }
}
