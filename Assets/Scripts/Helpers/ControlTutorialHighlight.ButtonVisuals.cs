using UnityEngine;
using UnityEngine.UI;

public partial class ControlTutorialHighlight
{
    [Header("Core UI Refs")]
    [SerializeField] private Image stickImage;
    [SerializeField] private Image boostImage;

    [Header("Face Button Callouts (optional)")]
    [SerializeField] private Image northImage;
    [SerializeField] private Image eastImage;
    [SerializeField] private Image westImage;
    [SerializeField] private Image southImage;

    [Header("Arrows Callout (optional)")]
    [SerializeField] private Image arrowsImage;

    [Header("Start Callout (optional)")]
    [SerializeField] private Image startImage;

    [Header("Tint")]
    [SerializeField] private Color baseTint      = new Color(1f, 1f, 1f, 0.55f);
    [SerializeField] private Color highlightTint = new Color(1f, 1f, 1f, 1f);
    [SerializeField] private float tintLerpSpeed = 12f;

    [Header("Pulse (scale on highlighted icons only)")]
    [SerializeField] private float pulseAmount = 0.06f;
    [SerializeField] private float pulseSpeed  = 3.0f;
    [SerializeField] private float scaleReturnSpeed = 16f;

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

    private void TickButtonVisuals()
    {
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
}
