using UnityEngine;

public partial class ControlTutorialHighlight
{
    [Header("3D Pivot Feedback")]
    [SerializeField] private float stickRotationMaxZ = 30f;
    [SerializeField] private float boostRotationMaxX = 50f;
    [SerializeField] private float buttonPressScale  = 0.85f;
    [SerializeField] private float pivotLerpSpeed    = 8f;

    [Header("Root Fade + Alpha Pulse")]
    [SerializeField] private float rootFadeInSeconds    = 0.35f;
    [SerializeField] private float rootAlphaPulseAmount = 0.07f;
    [SerializeField] private float rootAlphaPulseSpeed  = 1.5f;

    [Header("Fly Movement (Drift step)")]
    [SerializeField] private float flyMaxDistance   = 150f;
    [SerializeField] private float flyPositionSpeed = 6f;

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
}
