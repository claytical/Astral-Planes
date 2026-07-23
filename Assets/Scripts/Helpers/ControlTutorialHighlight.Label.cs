using UnityEngine;

public partial class ControlTutorialHighlight
{
    [Header("Instruction Label Fade + Attention")]
    [SerializeField] private CanvasGroup instructionGroup; // optional but recommended
    [SerializeField, Min(0f)] private float labelFadeInSeconds  = 0.12f;
    [SerializeField, Range(0f, 0.25f)] private float labelAttentionScale = 0.06f;
    [SerializeField, Min(0.1f)] private float labelScaleLerpSpeed = 14f;
    [SerializeField] private bool deactivateLabelWhenHidden = true;

    private float _labelAlpha = 0f;
    private float _labelTargetAlpha = 0f;
    private Vector3 _labelBaseScale = Vector3.one;

    private void CacheLabelBaseScale()
    {
        if (instructionText)
            _labelBaseScale = instructionText.transform.localScale;
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
}
