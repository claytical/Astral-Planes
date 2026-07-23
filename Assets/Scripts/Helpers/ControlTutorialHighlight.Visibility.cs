using System.Collections;
using UnityEngine;

public partial class ControlTutorialHighlight
{
    // ============================================================
    // Public visibility helpers
    // ============================================================
    public void SetVisible(bool visible)
    {
        if (!visible) StopAllModes();
        gameObject.SetActive(visible);
    }

    public void HideAndClear(bool immediate = true)
    {
        StopAllModes();

        if (immediate)
        {
            _rootAlpha = 0f; _rootAlphaTarget = 0f;
            if (_rootCanvasGroup) _rootCanvasGroup.alpha = 0f;
            if (_rootRectTransform) _rootRectTransform.anchoredPosition = _originPosition;
            _labelTargetAlpha = 0f;
            if (instructionText) instructionText.text = "";
            SetLabelAlphaImmediate(0f);

            Clear(immediate: true, hideText: true);
            gameObject.SetActive(false);
            return;
        }

        // Non-immediate: fade root and label out, then disable.
        FadeRootOut();
        if (instructionText) instructionText.text = "";
        Clear(immediate: false, hideText: true);
        FadeLabelTo(0f, labelFadeOutSeconds);

        // If you truly want the whole highlight to disappear after the label fades:
        StartCoroutine(DisableSelfAfterSeconds(labelFadeOutSeconds));
    }

    private IEnumerator DisableSelfAfterSeconds(float seconds)
    {
        yield return new WaitForSecondsRealtime(Mathf.Max(0.0001f, seconds));
        gameObject.SetActive(false);
    }

    private void StopAllModes()
    {
        _pressAnyActive = false;
        _pressAnyIndex = -1;

        _tutorialActive = false;
        _tutorialIndex = -1;
        _inputGatedActive = false;

        if (_pressAnyAutoCo != null) { StopCoroutine(_pressAnyAutoCo); _pressAnyAutoCo = null; }
        if (_timedTutorialCo != null) { StopCoroutine(_timedTutorialCo); _timedTutorialCo = null; }
        if (_inputGatedCo != null) { StopCoroutine(_inputGatedCo); _inputGatedCo = null; }
    }
}
