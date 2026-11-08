using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Competitive match-end overlay: headline, per-player vehicles with totals (winner in rainbow), and a
/// prompt. While shown, any button press (after a short grace delay) returns to the main menu.
/// <see cref="root"/> may be this object (disabled in the scene) or a child that holds the visuals.
/// </summary>
public class MatchResultsPanel : MonoBehaviour
{
    [SerializeField] private GameObject root;
    [SerializeField] private TMP_Text headlineText;
    [Tooltip("Container (e.g. HorizontalLayoutGroup) the per-player vehicle results are spawned under.")]
    [SerializeField] private Transform vehicleRow;
    [SerializeField] private VehicleResultView vehicleResultPrefab;
    [SerializeField] private TMP_Text promptText;
    [SerializeField] private string prompt = "Press any button to return to the main menu";
    [Tooltip("Seconds before a press is accepted, so the input that ended the match doesn't dismiss it.")]
    [SerializeField] private float inputGraceSeconds = 1f;

    private bool _shown;
    private bool _leaving;
    private float _acceptInputAt;

    private void Awake()
    {
        // root may be this object itself (found while disabled, activated by Show). Awake then runs
        // on that first activation, so never deactivate it here.
        if (root == null) root = gameObject;
        if (root != gameObject) root.SetActive(false);
    }

    public void Show(MatchEndReason reason, IReadOnlyList<(LocalPlayer player, int total)> scores)
    {
        _shown = true;
        _leaving = false;
        _acceptInputAt = Time.unscaledTime + inputGraceSeconds;

        if (headlineText != null) headlineText.text = HeadlineFor(reason);
        int winnerIndex = WinnerIndex(scores, out bool tie);
        BuildVehicleRow(scores, tie ? -1 : winnerIndex);
        if (promptText != null) promptText.text = prompt;
        if (root != null) root.SetActive(true);
    }

    private static string HeadlineFor(MatchEndReason reason) => reason switch
    {
        MatchEndReason.MotifComplete => "MOTIF COMPLETE",
        MatchEndReason.LastShipStanding => "LAST SHIP STANDING",
        _ => "EVERYONE ELIMINATED",
    };

    // Index of the highest score; `tie` is true when that score is shared.
    private static int WinnerIndex(IReadOnlyList<(LocalPlayer player, int total)> scores, out bool tie)
    {
        int best = int.MinValue, bestCount = 0, bestIndex = -1;
        for (int i = 0; i < scores.Count; i++)
        {
            if (scores[i].total > best) { best = scores[i].total; bestCount = 1; bestIndex = i; }
            else if (scores[i].total == best) bestCount++;
        }
        tie = bestCount > 1;
        return bestIndex;
    }

    private void BuildVehicleRow(IReadOnlyList<(LocalPlayer player, int total)> scores, int winnerIndex)
    {
        if (vehicleRow == null || vehicleResultPrefab == null) return;

        for (int i = vehicleRow.childCount - 1; i >= 0; i--)
            Destroy(vehicleRow.GetChild(i).gameObject);

        for (int i = 0; i < scores.Count; i++)
        {
            var (player, total) = scores[i];
            var view = Instantiate(vehicleResultPrefab, vehicleRow);
            view.Bind(player.DisplaySprite, player.DisplayColor, total);
            view.SetWinner(i == winnerIndex);
        }
    }

    private void Update()
    {
        if (!_shown || _leaving || Time.unscaledTime < _acceptInputAt) return;
        if (!AnyButtonPressedThisFrame()) return;

        _leaving = true;
        GameFlowManager.Instance?.QuitToSelection();
    }

    private static bool AnyButtonPressedThisFrame()
    {
        var kb = Keyboard.current;
        if (kb != null && kb.anyKey.wasPressedThisFrame) return true;

        var mouse = Mouse.current;
        if (mouse != null && (mouse.leftButton.wasPressedThisFrame || mouse.rightButton.wasPressedThisFrame)) return true;

        foreach (var pad in Gamepad.all)
        {
            if (pad.buttonSouth.wasPressedThisFrame || pad.buttonEast.wasPressedThisFrame ||
                pad.buttonWest.wasPressedThisFrame || pad.buttonNorth.wasPressedThisFrame ||
                pad.startButton.wasPressedThisFrame || pad.selectButton.wasPressedThisFrame ||
                pad.leftShoulder.wasPressedThisFrame || pad.rightShoulder.wasPressedThisFrame)
                return true;
        }
        return false;
    }
}
