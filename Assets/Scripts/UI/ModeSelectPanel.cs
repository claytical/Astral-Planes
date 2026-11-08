using TMPro;
using UnityEngine;

/// <summary>
/// Top-of-screen Co-op / Competitive (Elimination | Motif Completion) indicator, shown during selection once 2+ players have joined.
/// Confirmed players toggle the mode with D-pad left/right, i.e. the Selection Next/Previous actions (see LocalPlayer.TryToggleGameMode).
/// </summary>
public class ModeSelectPanel : MonoBehaviour
{
    [SerializeField] private GameObject root;
    [SerializeField] private TMP_Text modeLabel;
    [SerializeField] private string coopText = "CO-OP";
    [SerializeField] private string eliminationText = "COMPETITIVE: ELIMINATION";
    [SerializeField] private string motifText = "COMPETITIVE: MOTIF COMPLETION";
    [SerializeField] private string hintText = "Confirmed players: D-pad left/right to switch";
    [SerializeField] private TMP_Text hintLabel;
    [Tooltip("After this many idle seconds the control hint is replaced by the current mode's description.")]
    [SerializeField] private float idleSecondsBeforeModeHint = 5f;
    [Tooltip("Shown once every player is confirmed, until one more confirm press locks the mode and starts the tutorial.")]
    [SerializeField] private string confirmHint = "Press confirm to start with this mode";
    [SerializeField] private string coopHint = "Work together to complete motifs and explore the sonic fields together.";
    [SerializeField] private string eliminationHint = "Place notes to score points, discard to attack. Last player standing wins.";
    [SerializeField] private string motifHint = "Place notes to score points. High score wins when the motif is complete.";

    private SessionStateCoordinator _session;
    private float _lastActivityTime;
    private bool _wasShown;
    // Once a player has switched modes they know the controls, so the control hint is done until the panel reappears.
    private bool _hasSwitched;

    private void Awake()
    {
        // root must be a child: deactivating this object would stop Update and the panel could never reappear.
        if (root == null || root == gameObject)
            Debug.LogWarning("[ModeSelectPanel] Assign 'root' to a child object that holds the visuals.", this);
    }

    private void OnEnable() => Refresh();

    private void OnDisable() => Unbind();

    private void Update()
    {
        // SessionState is created by GameFlowManager; bind as soon as it exists.
        var current = GameFlowManager.Instance != null ? GameFlowManager.Instance.SessionState : null;
        if (current != _session) Bind(current);
        Refresh();
    }

    private void Bind(SessionStateCoordinator session)
    {
        Unbind();
        _session = session;
        if (_session != null) _session.ModeChanged += OnModeChanged;
    }

    private void Unbind()
    {
        if (_session != null) _session.ModeChanged -= OnModeChanged;
        _session = null;
    }

    private void OnModeChanged(GameMode _)
    {
        _hasSwitched = true;
        Refresh();
    }

    private void Refresh()
    {
        bool show = _session != null &&
                    _session.CurrentState == GameState.Selection &&
                    _session.Players.Count >= 2 &&
                    !_session.ModeLocked;
        if (root != null && root.activeSelf != show) root.SetActive(show);
        if (show && !_wasShown)
        {
            _lastActivityTime = Time.unscaledTime;
            _hasSwitched = false;
        }
        _wasShown = show;
        if (!show) return;

        if (modeLabel != null)
            modeLabel.text = _session.Mode switch
            {
                GameMode.CompetitiveElimination => eliminationText,
                GameMode.CompetitiveMotif => motifText,
                _ => coopText,
            };

        if (hintLabel != null)
        {
            bool idle = _hasSwitched || Time.unscaledTime - _lastActivityTime >= idleSecondsBeforeModeHint;
            string hint = _session.AwaitingModeConfirm ? confirmHint : !idle ? hintText : _session.Mode switch
            {
                GameMode.CompetitiveElimination => eliminationHint,
                GameMode.CompetitiveMotif => motifHint,
                _ => coopHint,
            };
            if (hintLabel.text != hint) hintLabel.text = hint;
        }
    }
}
