using TMPro;
using UnityEngine;
using UnityEngine.UI;

public partial class ControlTutorialDirector
{
    // Minimal hold-to-confirm overlay for aborting the tutorial (East held on the Selection map).
    // Deliberately kept separate from primaryInstance/ShowWaitingFor — ShowWaitingFor calls
    // StopAllModes() internally, which would kill the running Drift/Boost/Release tutorial
    // coroutine on an early release. This overlay never touches primaryInstance's state.
    private CanvasGroup _abortHoldOverlay;
    private Image _abortHoldFill;
    private TextMeshProUGUI _abortHoldText;
    private string _abortHoldLabel;

    private void EnsureAbortHoldUI()
    {
        if (_abortHoldOverlay != null) return;

        var go = new GameObject("AbortHoldOverlay", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(CanvasGroup));
        go.transform.SetParent(transform, worldPositionStays: false);

        var canvas = go.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = short.MaxValue - 1;

        var scaler = go.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);

        _abortHoldOverlay = go.GetComponent<CanvasGroup>();
        _abortHoldOverlay.alpha = 0f;
        _abortHoldOverlay.blocksRaycasts = false;
        _abortHoldOverlay.interactable = false;

        var barGo = new GameObject("Bar", typeof(RectTransform), typeof(Image));
        barGo.transform.SetParent(go.transform, false);
        var barRect = (RectTransform)barGo.transform;
        barRect.anchorMin = new Vector2(0.5f, 0.08f);
        barRect.anchorMax = new Vector2(0.5f, 0.08f);
        barRect.sizeDelta = new Vector2(420f, 36f);
        barRect.anchoredPosition = Vector2.zero;
        barGo.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.4f);

        var fillGo = new GameObject("Fill", typeof(RectTransform), typeof(Image));
        fillGo.transform.SetParent(barGo.transform, false);
        _abortHoldFill = fillGo.GetComponent<Image>();
        _abortHoldFill.color = new Color(1f, 1f, 1f, 0.9f);
        _abortHoldFill.type = Image.Type.Filled;
        _abortHoldFill.fillMethod = Image.FillMethod.Horizontal;
        _abortHoldFill.fillAmount = 0f;
        var fillRect = (RectTransform)fillGo.transform;
        fillRect.anchorMin = Vector2.zero;
        fillRect.anchorMax = Vector2.one;
        fillRect.offsetMin = Vector2.zero;
        fillRect.offsetMax = Vector2.zero;

        var textGo = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        textGo.transform.SetParent(go.transform, false);
        _abortHoldText = textGo.GetComponent<TextMeshProUGUI>();
        _abortHoldText.alignment = TextAlignmentOptions.Center;
        _abortHoldText.fontSize = 28f;
        _abortHoldText.color = Color.white;
        var textRect = (RectTransform)textGo.transform;
        textRect.anchorMin = new Vector2(0.5f, 0.08f);
        textRect.anchorMax = new Vector2(0.5f, 0.08f);
        textRect.sizeDelta = new Vector2(420f, 48f);
        textRect.anchoredPosition = new Vector2(0f, 40f);
    }

    public void BeginAbortHoldUI(string label = "Hold East to leave...")
    {
        EnsureAbortHoldUI();
        _abortHoldLabel = label;
        _abortHoldOverlay.alpha = 1f;
        _abortHoldFill.fillAmount = 0f;
        _abortHoldText.text = label;
    }

    public void UpdateAbortHoldUI(float t01)
    {
        if (_abortHoldOverlay == null) return;
        float clamped = Mathf.Clamp01(t01);
        _abortHoldFill.fillAmount = clamped;
        _abortHoldText.text = $"{_abortHoldLabel} {Mathf.RoundToInt(clamped * 100f)}%";
    }

    public void CancelAbortHoldUI()
    {
        if (_abortHoldOverlay == null) return;
        _abortHoldOverlay.alpha = 0f;
    }

    public void EndAbortHoldUI()
    {
        if (_abortHoldOverlay == null) return;
        _abortHoldOverlay.alpha = 0f;
    }
}
