using System;
using System.Collections;
using UnityEngine;

public partial class ControlTutorialHighlight
{
    [Header("Audio")]
    [SerializeField] private AudioClip stepConfirmClip;
    [SerializeField, Range(0f, 1f)] private float stepConfirmVolume = 1f;

    // ============================================================
    // Input-gated tutorial (each step waits for its specific input)
    // ============================================================
    public void StartInputGatedTutorial(Func<bool>[] stepPredicates, bool immediateFirst = true)
    {
        StopAllModes();
        CacheOriginPosition();
        _rootAlpha = 0f; _rootAlphaTarget = 0f;
        if (_rootCanvasGroup) _rootCanvasGroup.alpha = 0f;
        _tutorialActive = true;
        _tutorialIndex = -1;
        _inputGatedActive = true;
        if (_inputGatedCo != null) StopCoroutine(_inputGatedCo);
        _inputGatedCo = StartCoroutine(InputGatedTutorialRoutine(stepPredicates, immediateFirst));
    }

    private IEnumerator InputGatedTutorialRoutine(Func<bool>[] predicates, bool immediateFirst)
    {
        if (!gameObject.activeInHierarchy) gameObject.SetActive(true);

        if (immediateFirst) AdvanceTutorial(immediate: true);

        while (_tutorialActive && _inputGatedActive && gameObject.activeInHierarchy)
        {
            if (_tutorialIndex >= (tutorialSequence?.Length ?? 0)) break;

            Func<bool> predicate = (_tutorialIndex >= 0 && _tutorialIndex < (predicates?.Length ?? 0))
                ? predicates[_tutorialIndex] : null;

            if (predicate != null)
                yield return new WaitUntil(() => !_inputGatedActive || !gameObject.activeInHierarchy || predicate());

            if (!_inputGatedActive) break;

            if (stepConfirmClip && _audioSource)
                _audioSource.PlayOneShot(stepConfirmClip, stepConfirmVolume);

            bool isLastStep = _tutorialIndex >= (tutorialSequence?.Length ?? 0) - 1;

            if (isLastStep)
            {
                FadeRootOut();
                yield return new WaitForSecondsRealtime(rootFadeOutSeconds + 0.05f);
                if (!_inputGatedActive) break;
                AdvanceTutorial(immediate: false);
                break;
            }
            else
            {
                bool isAdditive = _tutorialIndex >= 0
                    && _tutorialIndex < (tutorialSequence?.Length ?? 0)
                    && tutorialSequence[_tutorialIndex] == Instruction.Drift
                    && _tutorialIndex + 1 < (tutorialSequence?.Length ?? 0)
                    && tutorialSequence[_tutorialIndex + 1] == Instruction.Boost;

                bool done = AdvanceTutorial(immediate: false, additive: isAdditive);
                if (done) break;

                yield return new WaitForSecondsRealtime(0.35f);
            }
        }

        _inputGatedActive = false;
        _inputGatedCo = null;
    }
}
