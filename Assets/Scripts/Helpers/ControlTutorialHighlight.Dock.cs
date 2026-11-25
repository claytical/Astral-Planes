using UnityEngine;

/// <summary>
/// Control-sandbox presentation: each command fades in centred at full size, holds while the
/// player works out the input, and fades out once ControlSandboxDirector detects it. The panel
/// never moves or rescales in the sandbox. While sandbox mode is on, the Drift-step stick-follow
/// in Feedback3D is suppressed (the pose is pinned here). Tilt/squash feedback still runs.
/// </summary>
public partial class ControlTutorialHighlight
{
    private bool _sandboxMode;
    private bool _restorePoseOnDisable;  // non-immediate EndSandbox: fade out in place, then restore
    private Vector3 _sandboxBaseScale = Vector3.one;
    private Vector2 _presentPosition;
    private float _sandboxFadeInSeconds;  // > 0: the root fades in linearly over this long
    private float _sandboxFadeOutSeconds; // > 0: the root fades out linearly over this long

    /// <summary>Sandbox: steady linear fades of these lengths for each command's appearance/dismissal.</summary>
    public void SetSandboxFades(float inSeconds, float outSeconds)
    {
        _sandboxFadeInSeconds  = Mathf.Max(0f, inSeconds);
        _sandboxFadeOutSeconds = Mathf.Max(0f, outSeconds);
    }


    /// <summary>Enters sandbox mode: the panel is pinned centred on screen at its current size, hidden.</summary>
    public void BeginSandbox()
    {
        StopAllModes();
        RestoreSandboxPoseIfPending();
        if (!gameObject.activeSelf) gameObject.SetActive(true);

        _sandboxMode = true;
        _sandboxBaseScale = transform.localScale;
        _presentPosition  = _rootRectTransform ? ComputePresentPosition() : Vector2.zero;
        if (_rootRectTransform) _rootRectTransform.anchoredPosition = _presentPosition;
        _originPosition   = _presentPosition;

        // Sandbox sequence: Drift then Charge (Place is taught later by the narrated tutorial).
        _activeSequence = new[] { Instruction.Drift, Instruction.Boost };
        _tutorialActive = true;
        _tutorialIndex  = -1;
        _rootAlpha = 0f; _rootAlphaTarget = 0f;
        if (_rootCanvasGroup) _rootCanvasGroup.alpha = 0f;
    }

    /// <summary>Fades in sandbox step <paramref name="index"/> (0 = Drift, 1 = Charge), centred and full size.</summary>
    public void PresentSandboxStep(int index)
    {
        if (!_sandboxMode) return;
        var seq = ActiveSequence;
        if (seq == null || index < 0 || index >= seq.Length) return;

        // Each command appears on its own: the previous one has already faded out.
        _tutorialActive = true;
        _tutorialIndex = index;
        HighlightInstruction(seq[index], immediate: true, additive: false);
    }

    /// <summary>Fades the current command out (the input was detected). The panel stays centred.</summary>
    public void DismissSandboxStep()
    {
        if (!_sandboxMode) return;
        FadeRootOut();
        FadeLabelTo(0f, labelFadeOutSeconds);
    }

    public void PlayStepConfirm()
    {
        if (stepConfirmClip && _audioSource)
            _audioSource.PlayOneShot(stepConfirmClip, stepConfirmVolume);
    }

    public float StepConfirmSeconds => stepConfirmClip != null ? stepConfirmClip.length : 0f;

    /// <summary>Fades the panel out and restores its normal pose. Does not raise OnTutorialFinished.</summary>
    public void EndSandbox(bool immediate)
    {
        if (!_sandboxMode) return;
        _sandboxMode = false;
        _sandboxFadeInSeconds = 0f;
        _sandboxFadeOutSeconds = 0f;
        _originPosition = _presentPosition;
        if (immediate) transform.localScale = _sandboxBaseScale;
        else _restorePoseOnDisable = true; // don't pop size mid-fade; restored once hidden
        HideAndClear(immediate);
    }

    private void RestoreSandboxPoseIfPending()
    {
        if (!_restorePoseOnDisable) return;
        _restorePoseOnDisable = false;
        transform.localScale = _sandboxBaseScale;
        if (_rootRectTransform) _rootRectTransform.anchoredPosition = _presentPosition;
    }

    // Returns true when sandbox mode owns position/scale this frame (pinned centred, full size).
    private bool UpdateSandboxPose()
    {
        if (!_sandboxMode || !_rootRectTransform) return false;
        transform.localScale = _sandboxBaseScale;
        _rootRectTransform.anchoredPosition = _presentPosition;
        return true;
    }

    // Poses are laid out on the whole screen (the root canvas rect), not the immediate parent:
    // a scene's PrimaryTutorialAnchor can be a small rect (GeneratedTrack's is 100x100 at
    // bottom-centre). Converted back to the parent's anchoredPosition space at the end.

    private Vector2 ComputePresentPosition()
    {
        if (!TryGetScreenSpace(out var canvasRT, out var parentRT)) return _rootRectTransform.anchoredPosition;
        Vector2 size  = Vector2.Scale(_rootRectTransform.rect.size, (Vector2)_sandboxBaseScale);
        Vector2 pivot = _rootRectTransform.pivot;
        Vector2 pivotInCanvas = canvasRT.rect.center + Vector2.Scale(pivot - new Vector2(0.5f, 0.5f), size);
        return CanvasToAnchored(canvasRT, parentRT, pivotInCanvas);
    }

    private bool TryGetScreenSpace(out RectTransform canvasRT, out RectTransform parentRT)
    {
        parentRT = _rootRectTransform.parent as RectTransform;
        var canvas = GetComponentInParent<Canvas>();
        canvasRT = canvas != null ? canvas.rootCanvas.transform as RectTransform : null;
        if (canvasRT == null) canvasRT = parentRT;
        return parentRT != null && canvasRT != null;
    }

    // Canvas-local point -> this panel's anchoredPosition (anchors assumed collapsed to a point).
    private Vector2 CanvasToAnchored(RectTransform canvasRT, RectTransform parentRT, Vector2 canvasPoint)
    {
        Vector2 parentLocal = parentRT.InverseTransformPoint(canvasRT.TransformPoint(canvasPoint));
        Rect pr = parentRT.rect;
        Vector2 anchorRef = new Vector2(
            Mathf.Lerp(pr.xMin, pr.xMax, (_rootRectTransform.anchorMin.x + _rootRectTransform.anchorMax.x) * 0.5f),
            Mathf.Lerp(pr.yMin, pr.yMax, (_rootRectTransform.anchorMin.y + _rootRectTransform.anchorMax.y) * 0.5f));
        return parentLocal - anchorRef;
    }
}
