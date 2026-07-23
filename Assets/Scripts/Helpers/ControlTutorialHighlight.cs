using System;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public partial class ControlTutorialHighlight : MonoBehaviour
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
    [SerializeField] private TMP_Text instructionText;

    [Header("Instruction Label Fade + Attention")]
    [SerializeField, Min(0f)] private float labelFadeOutSeconds = 0.12f;

    [Header("Behavior")]
    [SerializeField] private bool hideTextOnClear = true;

    [Header("3D Pivot Feedback")]
    [SerializeField] private Transform controlSchemePivot;

    [Header("Root Fade + Alpha Pulse")]
    [SerializeField] private float rootFadeOutSeconds   = 0.25f;

    [Header("Tutorial Sequence (Instruction -> Stick/Boost/Both + Label)")]
    [SerializeField] private Instruction[] tutorialSequence = new[]
    {
        Instruction.Drift,
        Instruction.Boost,
        Instruction.Release
    };

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
    // state
    // ------------------------------------------------------------
    private float _t;

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

    private void OnDisable()
    {
        StopAllModes();
    }

    private void Update()
    {
        _t += Time.unscaledDeltaTime;

        TickButtonVisuals();
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
}
