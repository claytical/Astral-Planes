using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class ControlTutorialHighlight : MonoBehaviour
{
    public enum Instruction { None, Drift, Boost, Charge }

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
    [SerializeField] private string driftText  = "Drift";
    [SerializeField] private string boostText  = "Boost";
    [SerializeField] private string chargeText = "Charge";

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
        Instruction.Charge
    };

    [Header("Press-Any-Button Guide (singular highlights)")]
    [SerializeField] private ButtonId[] pressAnySequence = new[]
    {
        ButtonId.Stick,
        ButtonId.North,
        ButtonId.East,
        ButtonId.West,
        ButtonId.South
    };

    [Header("Behavior")]
    [SerializeField] private bool hideTextOnClear = true;

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

    private void Awake()
    {
        CacheBaseScale(stickImage, ref _stickBaseScale);
        CacheBaseScale(boostImage, ref _boostBaseScale);

        CacheBaseScale(northImage, ref _northBaseScale);
        CacheBaseScale(eastImage,  ref _eastBaseScale);
        CacheBaseScale(westImage,  ref _westBaseScale);
        CacheBaseScale(southImage, ref _southBaseScale);

        CacheBaseScale(arrowsImage, ref _arrowsBaseScale);
        CacheBaseScale(startImage,  ref _startBaseScale);

        StopAllModes();
        Clear(immediate: true, hideText: hideTextOnClear);
    }

    private void OnDisable()
    {
        StopAllModes();
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
        Clear(immediate: immediate, hideText: true);
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

    public bool AdvanceTutorial(bool immediate = false)
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

        HighlightInstruction(tutorialSequence[_tutorialIndex], immediate);
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
    public void StartTimedTutorial(float stepSeconds = 1.2f, bool immediateFirst = true)
    {
        StopAllModes();

        _tutorialActive = true;
        _tutorialIndex = -1;

        if (_timedTutorialCo != null) StopCoroutine(_timedTutorialCo);
        _timedTutorialCo = StartCoroutine(TimedTutorialRoutine(Mathf.Max(0.05f, stepSeconds), immediateFirst));
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
            instructionText.gameObject.SetActive(true);
            instructionText.text = text;
        }

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

        if (instructionText)
        {
            instructionText.gameObject.SetActive(true);
            instructionText.text = overrideText ?? "";
        }

        Clear(immediate: immediate, hideText: false);
        HighlightButton(button, immediate);
    }

    // ============================================================
    // Instruction -> Label + Stick/Boost/Both mapping.
    // ============================================================
    public void HighlightInstruction(Instruction instruction, bool immediate = false)
    {
        // leaving press-any
        _pressAnyActive = false;
        _pressAnyIndex = -1;

        if (instructionText)
        {
            instructionText.gameObject.SetActive(true);
            instructionText.text = InstructionToString(instruction);
        }

        Clear(immediate: immediate, hideText: false);

        switch (instruction)
        {
            case Instruction.Drift:
                HighlightButton(ButtonId.Stick, immediate);
                break;
            case Instruction.Boost:
                HighlightButton(ButtonId.Boost, immediate);
                break;
            case Instruction.Charge:
                HighlightButton(ButtonId.Stick, immediate);
                HighlightButton(ButtonId.Boost, immediate);
                break;
        }
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
        }

        if (instructionText)
        {
            if (hideText)
            {
                instructionText.text = "";
                instructionText.gameObject.SetActive(false);
            }
            else
            {
                // leave it active, but don’t force-empty unless you want to
                // (your prompts set text explicitly)
                instructionText.gameObject.SetActive(true);
            }
        }
    }

    // ============================================================
    // Internals
    // ============================================================
    private void StopAllModes()
    {
        _pressAnyActive = false;
        _pressAnyIndex = -1;

        _tutorialActive = false;
        _tutorialIndex = -1;

        if (_pressAnyAutoCo != null) { StopCoroutine(_pressAnyAutoCo); _pressAnyAutoCo = null; }
        if (_timedTutorialCo != null) { StopCoroutine(_timedTutorialCo); _timedTutorialCo = null; }
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
            Instruction.Charge => chargeText,
            _ => ""
        };
    }
}