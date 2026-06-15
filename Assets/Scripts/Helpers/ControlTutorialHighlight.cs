using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class ControlTutorialHighlight : MonoBehaviour
{
    public enum Instruction { None, Drift, Boost, Release }

    public enum ButtonId
    {
        None,
        Stick,
        Boost,
        North,
        East,
        West,
        South,
        Arrows, // single image
        Start
    }

    public event Action OnTutorialFinished;

    [Header("Core UI Refs")]
    [SerializeField] private Image stickImage;
    [SerializeField] private Image boostImage;
    [SerializeField] private TMP_Text instructionText;

    [Header("Face Button Callouts (optional)")]
    [SerializeField] private Image northImage;
    [SerializeField] private Image eastImage;
    [SerializeField] private Image westImage;
    [SerializeField] private Image southImage;

    [Header("Arrows Callout (optional)")]
    [SerializeField] private Image arrowsImage;

    [Header("Start Callout (optional)")]
    [SerializeField] private Image startImage;

    [Header("Copy (Instruction -> Label)")]
    [SerializeField] private string driftText  = "Fly";
    [SerializeField] private string boostText  = "Push";
    [SerializeField] private string releaseText = "Press";

    [Header("Tint")]
    [SerializeField] private Color baseTint      = new Color(1f, 1f, 1f, 0.55f);
    [SerializeField] private Color highlightTint = new Color(1f, 1f, 1f, 1f);
    [SerializeField] private float tintLerpSpeed = 12f;

    [Header("Pulse (scale on highlighted icons only)")]
    [SerializeField] private float pulseAmount = 0.06f;
    [SerializeField] private float pulseSpeed  = 3.0f;
    [SerializeField] private float scaleReturnSpeed = 16f;

    [Header("Tutorial Sequence (Instruction -> Stick/Boost/Both + Label)")]
    [SerializeField] private Instruction[] tutorialSequence = new[]
    {
        Instruction.Drift,
        Instruction.Boost,
        Instruction.Release
    };

    [Header("Press-Any-Button Guide (singular highlights)")]
    [SerializeField] private ButtonId[] pressAnySequence = new[]
    {
        ButtonId.East
    };
    [Header("Instruction Label Fade + Attention")]
    [SerializeField] private CanvasGroup instructionGroup; // optional but recommended
    [SerializeField, Min(0f)] private float labelFadeInSeconds  = 0.12f;
    [SerializeField, Min(0f)] private float labelFadeOutSeconds = 0.12f;
    [SerializeField, Range(0f, 0.25f)] private float labelAttentionScale = 0.06f;
    [SerializeField, Min(0.1f)] private float labelScaleLerpSpeed = 14f;
    [SerializeField] private bool deactivateLabelWhenHidden = true;

    [Header("Audio")]
    [SerializeField] private AudioClip stepConfirmClip;
    [SerializeField, Range(0f, 1f)] private float stepConfirmVolume = 1f;

    [Header("Behavior")]
    [SerializeField] private bool hideTextOnClear = true;

    [Header("3D Pivot Feedback")]
    [SerializeField] private Transform controlSchemePivot;
    [SerializeField] private float stickRotationMaxZ = 30f;
    [SerializeField] private float boostRotationMaxX = 50f;
    [SerializeField] private float buttonPressScale  = 0.85f;
    [SerializeField] private float pivotLerpSpeed    = 8f;

    [Header("Root Fade + Alpha Pulse")]
    [SerializeField] private float rootFadeInSeconds    = 0.35f;
    [SerializeField] private float rootFadeOutSeconds   = 0.25f;
    [SerializeField] private float rootAlphaPulseAmount = 0.07f;
    [SerializeField] private float rootAlphaPulseSpeed  = 1.5f;

    [Header("Fly Movement (Drift step)")]
    [SerializeField] private float flyMaxDistance   = 150f;
    [SerializeField] private float flyPositionSpeed = 6f;

    // ------------------------------------------------------------
    // live input (pushed each frame by ControlTutorialDirector)
    // ------------------------------------------------------------
    private Vector2 _liveStick;
    private float   _liveThrust;
    private bool    _liveButtonHeld;

    // smoothed pivot state
    private float   _smoothRotZ;
    private float   _smoothRotX;
    private float   _smoothScale = 1f;
    private Vector3 _pivotBaseScale = Vector3.one;

    // ------------------------------------------------------------
    // label fade/scale state
    // ------------------------------------------------------------
    private float _labelAlpha = 0f;
    private float _labelTargetAlpha = 0f;
    private Vector3 _labelBaseScale = Vector3.one;
    private bool _labelWantsActive;
    // ------------------------------------------------------------
    // state
    // ------------------------------------------------------------
    private float _t;

    // targets + smoothed weights
    private float _stickTarget, _boostTarget;
    private float _northTarget, _eastTarget, _westTarget, _southTarget;
    private float _arrowsTarget, _startTarget;

    private float _stickW, _boostW;
    private float _northW, _eastW, _westW, _southW;
    private float _arrowsW, _startW;

    // base scales
    private Vector3 _stickBaseScale = Vector3.one;
    private Vector3 _boostBaseScale = Vector3.one;
    private Vector3 _northBaseScale = Vector3.one;
    private Vector3 _eastBaseScale  = Vector3.one;
    private Vector3 _westBaseScale  = Vector3.one;
    private Vector3 _southBaseScale = Vector3.one;
    private Vector3 _arrowsBaseScale= Vector3.one;
    private Vector3 _startBaseScale = Vector3.one;

    // Modes
    private bool _tutorialActive;
    private int  _tutorialIndex = -1;

    private bool _pressAnyActive;
    private int  _pressAnyIndex = -1;

    private Coroutine _pressAnyAutoCo;
    private Coroutine _timedTutorialCo;

    private bool _inputGatedActive;
    private Coroutine _inputGatedCo;
    private AudioSource _audioSource;

    // root fade + position animation
    private CanvasGroup   _rootCanvasGroup;
    private float         _rootAlpha       = 0f;
    private float         _rootAlphaTarget = 0f;
    private RectTransform _rootRectTransform;
    private Vector2       _originPosition;

    public Instruction[] TutorialSequence => tutorialSequence;

    private Instruction CurrentInstruction =>
        (_tutorialActive && _tutorialIndex >= 0 && _tutorialIndex < (tutorialSequence?.Length ?? 0))
            ? tutorialSequence[_tutorialIndex]
            : Instruction.None;

    private void Awake()
    {
        _audioSource = GetComponent<AudioSource>();
        if (!_audioSource) _audioSource = gameObject.AddComponent<AudioSource>();
        CacheBaseScale(stickImage, ref _stickBaseScale);
        CacheBaseScale(boostImage, ref _boostBaseScale);

        CacheBaseScale(northImage, ref _northBaseScale);
        CacheBaseScale(eastImage,  ref _eastBaseScale);
        CacheBaseScale(westImage,  ref _westBaseScale);
        CacheBaseScale(southImage, ref _southBaseScale);

        CacheBaseScale(arrowsImage, ref _arrowsBaseScale);
        CacheBaseScale(startImage,  ref _startBaseScale);
        CacheLabelBaseScale();

        var pivot = controlSchemePivot ? controlSchemePivot : transform;
        _pivotBaseScale = pivot.localScale;

        _rootRectTransform     = GetComponent<RectTransform>();
        _rootCanvasGroup       = GetComponent<CanvasGroup>() ?? gameObject.AddComponent<CanvasGroup>();
        _rootCanvasGroup.alpha = 0f;
        _rootAlpha             = 0f;
        _rootAlphaTarget       = 0f;
        _originPosition        = _rootRectTransform ? _rootRectTransform.anchoredPosition : Vector2.zero;

        if (instructionText)
        {
            _labelBaseScale = instructionText.transform.localScale;

            // If none wired, try auto-find on label object (safe, optional)
            if (!instructionGroup)
                instructionGroup = instructionText.GetComponent<CanvasGroup>();

            // If still none, you can add one automatically:
            if (!instructionGroup)
                instructionGroup = instructionText.gameObject.AddComponent<CanvasGroup>();

            instructionGroup.alpha = 0f;
            if (deactivateLabelWhenHidden) instructionText.gameObject.SetActive(false);
        }
        ApplyLabelVisuals(immediate: true, forceDeactivateIfHidden: true);
        StopAllModes();
        Clear(immediate: true, hideText: hideTextOnClear);
    }
    private void CacheLabelBaseScale()
    {
        if (instructionText)
            _labelBaseScale = instructionText.transform.localScale;
    }
    private void OnDisable()
    {
        StopAllModes();
    }
    private void SetLabelAlphaImmediate(float a)
{
    if (!instructionText) return;
    if (!instructionGroup) return;

    instructionGroup.alpha = Mathf.Clamp01(a);

    // scale attention
    float mul = (a > 0.001f) ? (1f + labelAttentionScale) : 1f;
    instructionText.rectTransform.localScale = _labelBaseScale * mul;

    if (deactivateLabelWhenHidden && a <= 0.001f)
        instructionText.gameObject.SetActive(false);
    else
        instructionText.gameObject.SetActive(true);
}

    private void FadeLabelTo(float targetAlpha, float seconds)
{
    if (!instructionText) return;

    // Ensure CanvasGroup is wired
    if (!instructionGroup) instructionGroup = instructionText.GetComponent<CanvasGroup>();
    if (!instructionGroup) instructionGroup = instructionText.gameObject.AddComponent<CanvasGroup>();

    _labelTargetAlpha = Mathf.Clamp01(targetAlpha);

    // Activate immediately when fading in so UpdateLabelVisuals can write to it
    if (_labelTargetAlpha > 0.001f)
        instructionText.gameObject.SetActive(true);
}
    private void Update()
    {
        _t += Time.unscaledDeltaTime;
        float lerp = 1f - Mathf.Exp(-tintLerpSpeed * Time.unscaledDeltaTime);

        _stickW  = Mathf.Lerp(_stickW,  _stickTarget,  lerp);
        _boostW  = Mathf.Lerp(_boostW,  _boostTarget,  lerp);

        _northW  = Mathf.Lerp(_northW,  _northTarget,  lerp);
        _eastW   = Mathf.Lerp(_eastW,   _eastTarget,   lerp);
        _westW   = Mathf.Lerp(_westW,   _westTarget,   lerp);
        _southW  = Mathf.Lerp(_southW,  _southTarget,  lerp);

        _arrowsW = Mathf.Lerp(_arrowsW, _arrowsTarget, lerp);
        _startW  = Mathf.Lerp(_startW,  _startTarget,  lerp);

        ApplyVisuals(stickImage,  _stickBaseScale,  _stickW);
        ApplyVisuals(boostImage,  _boostBaseScale,  _boostW);

        ApplyVisuals(northImage,  _northBaseScale,  _northW);
        ApplyVisuals(eastImage,   _eastBaseScale,   _eastW);
        ApplyVisuals(westImage,   _westBaseScale,   _westW);
        ApplyVisuals(southImage,  _southBaseScale,  _southW);

        ApplyVisuals(arrowsImage, _arrowsBaseScale, _arrowsW);
        ApplyVisuals(startImage,  _startBaseScale,  _startW);
        UpdateLabelVisuals();
        UpdatePivotFeedback();
        UpdateRootAlpha();
        UpdatePositionAnimation();
    }

    public void SetLiveInput(Vector2 stick, float thrust, bool buttonHeld)
    {
        _liveStick      = stick;
        _liveThrust     = thrust;
        _liveButtonHeld = buttonHeld;
    }

    private void UpdatePivotFeedback()
    {
        var instr = CurrentInstruction;

        float targetRotZ  = 0f;
        float targetRotX  = 0f;
        float targetScale = 1f;

        if (instr == Instruction.Drift || instr == Instruction.Boost || instr == Instruction.Release)
        {
            targetRotZ = -_liveStick.x * stickRotationMaxZ;
 //           if (instr == Instruction.Drift)
 //               targetRotX = -_liveStick.y * stickRotationMaxZ * 0.5f;
        }

        if (instr == Instruction.Boost || instr == Instruction.Release)
            targetRotX = _liveThrust * boostRotationMaxX;

        if (instr == Instruction.Release && _liveButtonHeld)
            targetScale = buttonPressScale;

        float k = 1f - Mathf.Exp(-pivotLerpSpeed * Time.unscaledDeltaTime);
        _smoothRotZ  = Mathf.Lerp(_smoothRotZ,  targetRotZ,  k);
        _smoothRotX  = Mathf.Lerp(_smoothRotX,  targetRotX,  k);
        _smoothScale = Mathf.Lerp(_smoothScale, targetScale, k);

        var pivot = controlSchemePivot ? controlSchemePivot : transform;
        pivot.localRotation = Quaternion.Euler(_smoothRotX, 0f, _smoothRotZ);
        pivot.localScale    = _pivotBaseScale * _smoothScale;
    }
    private void UpdateLabelVisuals()
    {
        if (!instructionText) return;

        // If we intend to show, ensure active before fading in
        if (_labelTargetAlpha > 0.001f)
        {
            if (!instructionText.gameObject.activeSelf)
                instructionText.gameObject.SetActive(true);
        }

        // Fade alpha using unscaled time
        float dt = Time.unscaledDeltaTime;
        float fadeSeconds = (_labelTargetAlpha >= _labelAlpha) ? Mathf.Max(0.0001f, labelFadeInSeconds)
                                                               : Mathf.Max(0.0001f, labelFadeOutSeconds);

        float k = 1f - Mathf.Exp(-(1f / fadeSeconds) * dt);
        _labelAlpha = Mathf.Lerp(_labelAlpha, _labelTargetAlpha, k);

        // Apply alpha to CanvasGroup or TMP_Text color
        if (instructionGroup)
        {
            instructionGroup.alpha = _labelAlpha;
        }
        else
        {
            var c = instructionText.color;
            c.a = _labelAlpha;
            instructionText.color = c;
        }

        // Ease scale up slightly when visible
        float targetScaleMul = (_labelTargetAlpha > 0.001f) ? (1f + labelAttentionScale) : 1f;
        Vector3 targetScale = _labelBaseScale * targetScaleMul;

        float s = 1f - Mathf.Exp(-labelScaleLerpSpeed * dt);
        instructionText.rectTransform.localScale = Vector3.Lerp(
            instructionText.rectTransform.localScale,
            targetScale,
            s
        );

        // Optionally deactivate once fully faded out
        if (deactivateLabelWhenHidden && _labelTargetAlpha <= 0.001f && _labelAlpha <= 0.01f)
        {
            if (instructionText.gameObject.activeSelf)
                instructionText.gameObject.SetActive(false);
        }
    }

    private void ApplyLabelVisuals(bool immediate, bool forceDeactivateIfHidden)
    {
        if (!instructionText) return;

        if (_labelTargetAlpha > 0.001f)
        {
            instructionText.gameObject.SetActive(true);
        }
        else if (forceDeactivateIfHidden && deactivateLabelWhenHidden)
        {
            instructionText.gameObject.SetActive(false);
        }

        if (immediate)
        {
            _labelAlpha = _labelTargetAlpha;

            if (instructionGroup)
                instructionGroup.alpha = _labelAlpha;
            else
            {
                var c = instructionText.color;
                c.a = _labelAlpha;
                instructionText.color = c;
            }

            float targetScaleMul = (_labelTargetAlpha > 0.001f) ? (1f + labelAttentionScale) : 1f;
            instructionText.rectTransform.localScale = _labelBaseScale * targetScaleMul;
        }
    }
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
    // ============================================================
    // Tutorial mode (manual step) - kept for compatibility
    // ============================================================
    public void StartTutorial(bool immediateFirst = true)
    {
        StopAllModes();
        _tutorialActive = true;
        _tutorialIndex = -1;

        if (immediateFirst)
            AdvanceTutorial(immediate: true);
    }

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

    // Stores the step duration so SkipCurrentTimedStep can re-use it
    private float _lastStepSeconds = 1.2f;

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

        // If the target image is missing, you’ll *appear* stuck.
        // This makes that obvious in the console.
        // (Comment out if noisy.)
        // Debug.Log($"[PressAny] Step {_pressAnyIndex}/{pressAnySequence.Length}: {pressAnySequence[_pressAnyIndex]}");

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

    // ============================================================
    // Clear
    // ============================================================
    public void Clear(bool immediate = false, bool hideText = false)
    {
        _stickTarget = _boostTarget = 0f;
        _northTarget = _eastTarget = _westTarget = _southTarget = 0f;
        _arrowsTarget = 0f;
        _startTarget  = 0f;

        if (immediate)
        {
            _stickW = _boostW = 0f;
            _northW = _eastW = _westW = _southW = 0f;
            _arrowsW = 0f;
            _startW = 0f;

            ResetVisual(stickImage,  _stickBaseScale);
            ResetVisual(boostImage,  _boostBaseScale);

            ResetVisual(northImage,  _northBaseScale);
            ResetVisual(eastImage,   _eastBaseScale);
            ResetVisual(westImage,   _westBaseScale);
            ResetVisual(southImage,  _southBaseScale);

            ResetVisual(arrowsImage, _arrowsBaseScale);
            ResetVisual(startImage,  _startBaseScale);

            _smoothRotZ = _smoothRotX = 0f;
            _smoothScale = 1f;
            var pivot = controlSchemePivot ? controlSchemePivot : transform;
            pivot.localRotation = Quaternion.identity;
            pivot.localScale    = _pivotBaseScale;
        }

        if (instructionText)
        {
            if (hideText)
            {
                instructionText.text = "";
                FadeLabelTo(0f, immediate ? 0.0001f : labelFadeOutSeconds);
            }
            // else: leave as-is (caller will set text and fade-in explicitly)
        }
    }

    // ============================================================
    // Internals
    // ============================================================
    private void CacheOriginPosition()
    {
        if (_rootRectTransform)
            _originPosition = _rootRectTransform.anchoredPosition;
    }

    private void FadeRootIn()  => _rootAlphaTarget = 1f;
    private void FadeRootOut() => _rootAlphaTarget = 0f;

    private void UpdateRootAlpha()
    {
        if (!_rootCanvasGroup) return;
        float dt = Time.unscaledDeltaTime;
        float fadeSeconds = (_rootAlphaTarget >= _rootAlpha)
            ? Mathf.Max(0.0001f, rootFadeInSeconds)
            : Mathf.Max(0.0001f, rootFadeOutSeconds);
        float k = 1f - Mathf.Exp(-(1f / fadeSeconds) * dt);
        _rootAlpha = Mathf.Lerp(_rootAlpha, _rootAlphaTarget, k);
        float pulse = Mathf.Sin(_t * rootAlphaPulseSpeed * Mathf.PI * 2f) * rootAlphaPulseAmount;
        _rootCanvasGroup.alpha = Mathf.Clamp01(_rootAlpha + _rootAlpha * pulse);
    }

    private void UpdatePositionAnimation()
    {
        if (!_rootRectTransform) return;
        var instr = CurrentInstruction;

        if (instr == Instruction.Drift || instr == Instruction.Boost || instr == Instruction.Release)
        {
            Vector2 raw = _originPosition + new Vector2(_liveStick.x, _liveStick.y) * flyMaxDistance;
            var parentRT = _rootRectTransform.parent as RectTransform;
            if (parentRT != null)
            {
                Rect r = parentRT.rect;
                raw.x = Mathf.Clamp(raw.x, r.xMin, r.xMax);
                raw.y = Mathf.Clamp(raw.y, r.yMin, r.yMax);
            }
            float k = 1f - Mathf.Exp(-flyPositionSpeed * Time.unscaledDeltaTime);
            _rootRectTransform.anchoredPosition = Vector2.Lerp(_rootRectTransform.anchoredPosition, raw, k);
        }
        else
        {
            float k = 1f - Mathf.Exp(-flyPositionSpeed * Time.unscaledDeltaTime);
            _rootRectTransform.anchoredPosition = Vector2.Lerp(_rootRectTransform.anchoredPosition, _originPosition, k);
        }
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

    private void HighlightButton(ButtonId button, bool immediate)
    {
        // IMPORTANT: if a given Image ref is null, it will look like we’re “stuck”.
        // Make sure your prefab assigns north/east/west/south images if those steps exist.

        switch (button)
        {
            case ButtonId.Stick:   _stickTarget = 1f; if (immediate) _stickW = 1f; break;
            case ButtonId.Boost:   _boostTarget = 1f; if (immediate) _boostW = 1f; break;

            case ButtonId.North:   _northTarget = 1f; if (immediate) _northW = 1f; break;
            case ButtonId.East:    _eastTarget  = 1f; if (immediate) _eastW  = 1f; break;
            case ButtonId.West:    _westTarget  = 1f; if (immediate) _westW  = 1f; break;
            case ButtonId.South:   _southTarget = 1f; if (immediate) _southW = 1f; break;

            case ButtonId.Arrows:  _arrowsTarget = 1f; if (immediate) _arrowsW = 1f; break;
            case ButtonId.Start:   _startTarget  = 1f; if (immediate) _startW  = 1f; break;
        }
    }

    private void ApplyVisuals(Image img, Vector3 baseScale, float w)
    {
        if (!img) return;

        img.color = Color.Lerp(baseTint, highlightTint, w);

        if (w <= 0.001f)
        {
            img.rectTransform.localScale = Vector3.Lerp(
                img.rectTransform.localScale,
                baseScale,
                1f - Mathf.Exp(-scaleReturnSpeed * Time.unscaledDeltaTime)
            );
            return;
        }

        float p = Mathf.Sin(_t * pulseSpeed) * pulseAmount * w;
        Vector3 target = baseScale * (1f + p);

        img.rectTransform.localScale = Vector3.Lerp(
            img.rectTransform.localScale,
            target,
            1f - Mathf.Exp(-scaleReturnSpeed * Time.unscaledDeltaTime)
        );
    }

    private void ResetVisual(Image img, Vector3 baseScale)
    {
        if (!img) return;
        img.color = baseTint;
        img.rectTransform.localScale = baseScale;
    }

    private void CacheBaseScale(Image img, ref Vector3 store)
    {
        if (img) store = img.rectTransform.localScale;
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