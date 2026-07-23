using System.Collections;
using UnityEngine;

public partial class ControlTutorialHighlight
{
    [Header("Press-Any-Button Guide (singular highlights)")]
    [SerializeField] private ButtonId[] pressAnySequence = new[]
    {
        ButtonId.East
    };

    // ============================================================
    // Press-any mode (AUTO CYCLING)
    // ============================================================
    public void BeginPressAnyButtonGuideAuto(
        bool immediateFirst = true,
        string text = "Press any button",
        float stepSeconds = 1.0f,
        bool loop = true)
    {
        StopAllModes();
        _pressAnyActive = true;
        _pressAnyIndex = -1;

        if (instructionText)
        {
            instructionText.text = text;
            FadeLabelTo(1f, labelFadeInSeconds);
        }
        FadeRootIn();
        if (_pressAnyAutoCo != null) StopCoroutine(_pressAnyAutoCo);
        _pressAnyAutoCo = StartCoroutine(PressAnyAutoRoutine(Mathf.Max(0.05f, stepSeconds), loop, immediateFirst));
    }

    private IEnumerator PressAnyAutoRoutine(float stepSeconds, bool loop, bool immediateFirst)
    {
        if (!gameObject.activeInHierarchy)
            gameObject.SetActive(true);

        if (immediateFirst)
            StepPressAny(immediate: true);
        else
            Clear(immediate: true, hideText: false);

        while (_pressAnyActive && gameObject.activeInHierarchy)
        {
            yield return new WaitForSecondsRealtime(stepSeconds);

            if (!_pressAnyActive) break;

            bool done = StepPressAny(immediate: false);
            if (done)
            {
                if (loop)
                {
                    _pressAnyActive = true;
                    _pressAnyIndex = -1;
                    StepPressAny(immediate: false);
                }
                else break;
            }
        }

        _pressAnyAutoCo = null;
    }

    private bool StepPressAny(bool immediate)
    {
        if (!_pressAnyActive) return true;

        if (pressAnySequence == null || pressAnySequence.Length == 0)
        {
            _pressAnyActive = false;
            Clear(immediate: true, hideText: hideTextOnClear);
            return true;
        }

        _pressAnyIndex++;

        if (_pressAnyIndex >= pressAnySequence.Length)
        {
            _pressAnyActive = false;
            Clear(immediate: true, hideText: hideTextOnClear);
            return true;
        }

        Clear(immediate: immediate, hideText: false);
        HighlightButton(pressAnySequence[_pressAnyIndex], immediate);

        return false;
    }

    // ============================================================
    // Single button prompt (no cycling)
    // ============================================================
    public void ShowWaitingFor(ButtonId button, bool immediate = false, string overrideText = null)
    {
        StopAllModes();
        FadeRootIn();

        if (instructionText)
        {
            instructionText.text = overrideText ?? "";
            if (string.IsNullOrEmpty(instructionText.text)) FadeLabelTo(0f, labelFadeOutSeconds);
            else FadeLabelTo(1f, labelFadeInSeconds);
        }

        Clear(immediate: immediate, hideText: false);
        HighlightButton(button, immediate);
    }
}
