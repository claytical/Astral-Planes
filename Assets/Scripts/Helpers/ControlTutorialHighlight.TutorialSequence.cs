using System.Collections;
using UnityEngine;

public partial class ControlTutorialHighlight
{
    [Header("Copy (Instruction -> Label)")]
    [SerializeField] private string driftText  = "Fly";
    [SerializeField] private string boostText  = "Push";
    [SerializeField] private string releaseText = "Press";

    // Stores the step duration so SkipCurrentTimedStep can re-use it
    private float _lastStepSeconds = 1.2f;

    public bool AdvanceTutorial(bool immediate = false, bool additive = false)
    {
        if (!_tutorialActive) return true;

        if (tutorialSequence == null || tutorialSequence.Length == 0)
        {
            EndTutorial();
            return true;
        }

        _tutorialIndex++;

        if (_tutorialIndex >= tutorialSequence.Length)
        {
            EndTutorial();
            return true;
        }

        HighlightInstruction(tutorialSequence[_tutorialIndex], immediate, additive);
        return false;
    }

    public void EndTutorial()
    {
        _tutorialActive = false;
        _tutorialIndex = -1;
        Clear(immediate: true, hideText: hideTextOnClear);
        OnTutorialFinished?.Invoke();
    }

    // ============================================================
    // Tutorial mode (TIMED)
    // ============================================================
    /// Aborts the current step's wait timer and advances to the next step immediately.
    public void SkipCurrentTimedStep()
    {
        if (!_tutorialActive) return;
        if (_inputGatedActive) return;

        // Stop the running timed coroutine
        if (_timedTutorialCo != null)
        {
            StopCoroutine(_timedTutorialCo);
            _timedTutorialCo = null;
        }

        // Advance one step now, then re-launch the timed loop to wait for the next
        bool done = AdvanceTutorial(immediate: true);
        if (!done)
        {
            _timedTutorialCo = StartCoroutine(TimedTutorialRoutineWaitOnly(_lastStepSeconds));
        }
    }

    public void StartTimedTutorial(float stepSeconds = 1.2f, bool immediateFirst = true)
    {
        StopAllModes();
        CacheOriginPosition();
        _rootAlpha = 0f; _rootAlphaTarget = 0f;
        if (_rootCanvasGroup) _rootCanvasGroup.alpha = 0f;
        _lastStepSeconds = Mathf.Max(0.05f, stepSeconds);

        _tutorialActive = true;
        _tutorialIndex = -1;

        if (_timedTutorialCo != null) StopCoroutine(_timedTutorialCo);
        _timedTutorialCo = StartCoroutine(TimedTutorialRoutine(_lastStepSeconds, immediateFirst));
    }

    /// Runs the timed loop starting from the current index (step already shown), just waiting before advancing.
    private IEnumerator TimedTutorialRoutineWaitOnly(float stepSeconds)
    {
        while (_tutorialActive && gameObject.activeInHierarchy)
        {
            if (_tutorialIndex >= (tutorialSequence?.Length ?? 0))
                break;

            yield return new WaitForSecondsRealtime(stepSeconds);

            if (!_tutorialActive) break;

            bool done = AdvanceTutorial(immediate: false);
            if (done) break;
        }

        _timedTutorialCo = null;
    }

    private IEnumerator TimedTutorialRoutine(float stepSeconds, bool immediateFirst)
    {
        // Ensure we're visible + able to run coroutines
        if (!gameObject.activeInHierarchy)
            gameObject.SetActive(true);

        if (immediateFirst)
            AdvanceTutorial(immediate: true);
        else
            Clear(immediate: true, hideText: false);

        while (_tutorialActive && gameObject.activeInHierarchy)
        {
            // If tutorial already completed, break.
            if (_tutorialIndex >= (tutorialSequence?.Length ?? 0))
                break;

            yield return new WaitForSecondsRealtime(stepSeconds);

            if (!_tutorialActive) break;

            bool done = AdvanceTutorial(immediate: false);
            if (done) break;
        }

        _timedTutorialCo = null;
    }

    // ============================================================
    // Instruction -> Label + Stick/Boost/Both mapping.
    // ============================================================
    public void HighlightInstruction(Instruction instruction, bool immediate = false, bool additive = false)
    {
        // leaving press-any
        _pressAnyActive = false;
        _pressAnyIndex = -1;

        if (instructionText)
        {
            instructionText.text = InstructionToString(instruction);
            FadeLabelTo(1f, labelFadeInSeconds);
        }

        if (!additive)
            Clear(immediate: immediate, hideText: false);

        switch (instruction)
        {
            case Instruction.Drift:
                HighlightButton(ButtonId.Stick, immediate);
                break;
            case Instruction.Boost:
                HighlightButton(ButtonId.Stick, immediate);
                HighlightButton(ButtonId.Boost, immediate);
                break;
            case Instruction.Release:
                HighlightButton(ButtonId.South, immediate);
                break;
        }

        FadeRootIn();
    }

    private string InstructionToString(Instruction i)
    {
        return i switch
        {
            Instruction.Drift  => driftText,
            Instruction.Boost  => boostText,
            Instruction.Release => releaseText,
            _ => ""
        };
    }
}
