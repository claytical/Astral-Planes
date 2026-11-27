using TMPro;
using UnityEngine;

/// <summary>
/// Top-of-screen VERSUS banner on the join screen. The mode is picked on the title menu now
/// (StartIntent), so this only reports it: "VERSUS: SURVIVAL" or "VERSUS: SCORE", with that
/// mode's rules, or a reminder that a second player is needed. Hidden for START/CONTINUE (co-op).
/// The class name is kept so the TrackSelection.unity reference still resolves.
/// </summary>
public class ModeSelectPanel : MonoBehaviour
{
    [SerializeField] private GameObject root;
    [SerializeField] private TMP_Text modeLabel;
    [SerializeField] private TMP_Text hintLabel;
    [SerializeField] private string survivalText = "VERSUS: SURVIVAL";
    [SerializeField] private string scoreText = "VERSUS: SCORE";
    [SerializeField] private string eliminationHint = "Place notes to score points, discard to attack. Last player standing wins.";
    [SerializeField] private string motifHint = "Place notes to score points. High score wins when the motif is complete.";
    [SerializeField] private string needsPlayersHint = "Versus needs a second player to join.";

    private void Awake()
    {
        // root must be a child: deactivating this object would stop Update and the panel could never reappear.
        if (root == null || root == gameObject)
            Debug.LogWarning("[ModeSelectPanel] Assign 'root' to a child object that holds the visuals.", this);
    }

    private void Update()
    {
        var session = GameFlowManager.Instance != null ? GameFlowManager.Instance.SessionState : null;
        bool show = session != null && session.CurrentState == GameState.Selection && StartIntent.IsVersus;
        if (root != null && root.activeSelf != show) root.SetActive(show);
        if (!show) return;

        bool survival = StartIntent.VersusMode == GameMode.CompetitiveElimination;
        if (modeLabel != null)
        {
            string label = survival ? survivalText : scoreText;
            if (modeLabel.text != label) modeLabel.text = label;
        }
        if (hintLabel != null)
        {
            string hint = session.Players.Count < StartIntent.VersusMinPlayers ? needsPlayersHint
                : survival ? eliminationHint : motifHint;
            if (hintLabel.text != hint) hintLabel.text = hint;
        }
    }
}
