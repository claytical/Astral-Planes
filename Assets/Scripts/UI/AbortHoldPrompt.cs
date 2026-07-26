using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Reusable hold-to-confirm visual: a fill bar + label driven by a 0..1 progress value.
/// Attach to any prefab (over a player's energy bar, below the tutorial UI, etc.) and wire
/// the three fields in the Inspector. Drivers (LocalPlayer, TrackSelectionJoinController,
/// ControlTutorialDirector) call BeginHold/UpdateHold/CancelHold/EndHold.
/// </summary>
public class AbortHoldPrompt : MonoBehaviour
{
    [SerializeField] private CanvasGroup group;
    [SerializeField] private Image fill;
    [SerializeField] private TMP_Text label;

    private string _baseLabel;
    private float _holdDurationSeconds;

    public void BeginHold(string labelText, float holdDurationSeconds)
    {
        _baseLabel = labelText;
        _holdDurationSeconds = holdDurationSeconds;
        if (group != null) group.alpha = 1f;
        if (fill != null) fill.fillAmount = 0f;
        if (label != null) label.text = FormatLabel(_holdDurationSeconds);
    }

    public void UpdateHold(float t01)
    {
        float clamped = Mathf.Clamp01(t01);
        if (fill != null) fill.fillAmount = clamped;
        float remaining = _holdDurationSeconds * (1f - clamped);
        if (label != null) label.text = FormatLabel(remaining);
    }

    private string FormatLabel(float secondsRemaining) =>
        $"{_baseLabel} in {Mathf.Max(0, Mathf.CeilToInt(secondsRemaining))} Seconds";

    public void CancelHold()
    {
        if (group != null) group.alpha = 0f;
    }

    public void EndHold()
    {
        if (group != null) group.alpha = 0f;
    }
}
