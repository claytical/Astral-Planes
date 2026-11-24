using UnityEngine;

/// <summary>
/// Control-sandbox presentation: the panel either "presents" a command (centred, full size)
/// or "docks" (shrinks into the bottom-right corner) while players practise it in their
/// vehicles. Driven by ControlSandboxDirector; smoothed in Update like the rest of the panel.
/// While sandbox mode is on, the Drift-step stick-follow in Feedback3D is suppressed — it
/// would fight the dock target. Tilt/squash feedback still runs.
/// </summary>
public partial class ControlTutorialHighlight
{
    private bool _sandboxMode;
    private bool _docked;
    private bool _restorePoseOnDisable;  // non-immediate EndSandbox: fade out in place, then restore
    private float _dockScale = 0.4f;
    private float _dockMargin = 32f;
    private float _dockLerpSpeed = 5f;
    private RectTransform _dockAvoid;          // e.g. the lower-third narration panel
    private Vector3 _sandboxBaseScale = Vector3.one;
    private Vector2 _presentPosition;

    public bool IsSandboxMode => _sandboxMode;

    /// <summary>Enters sandbox mode with the panel's current pose as the "present" pose.</summary>
    public void BeginSandbox(float dockScale, float dockMargin, float dockLerpSpeed, RectTransform avoidRect)
    {
        StopAllModes();
        RestoreSandboxPoseIfPending();
        if (!gameObject.activeSelf) gameObject.SetActive(true);

        _sandboxMode   = true;
        _docked        = false;
        _dockScale     = Mathf.Clamp(dockScale, 0.05f, 1f);
        _dockMargin    = Mathf.Max(0f, dockMargin);
        _dockLerpSpeed = Mathf.Max(0.1f, dockLerpSpeed);
        _dockAvoid     = avoidRect;

        _sandboxBaseScale = transform.localScale;
        _presentPosition  = _rootRectTransform ? ComputePresentPosition() : Vector2.zero;
        if (_rootRectTransform) _rootRectTransform.anchoredPosition = _presentPosition; // first present starts in place
        _originPosition   = _presentPosition;

        // Sandbox sequence: Drift then Charge (Place is taught later by the narrated tutorial).
        _activeSequence = new[] { Instruction.Drift, Instruction.Boost };
        _tutorialActive = true;
        _tutorialIndex  = -1;
        _rootAlpha = 0f; _rootAlphaTarget = 0f;
        if (_rootCanvasGroup) _rootCanvasGroup.alpha = 0f;
    }

    /// <summary>Shows sandbox step <paramref name="index"/> (0 = Drift, 1 = Charge) centred and full size.</summary>
    public void PresentSandboxStep(int index)
    {
        if (!_sandboxMode) return;
        var seq = ActiveSequence;
        if (seq == null || index < 0 || index >= seq.Length) return;

        // Drift -> Charge keeps the stick lit and adds the trigger, same as the old sequence.
        bool additive = _tutorialIndex >= 0 && _tutorialIndex < seq.Length
                        && seq[_tutorialIndex] == Instruction.Drift && seq[index] == Instruction.Boost;
        _tutorialActive = true;
        _tutorialIndex = index;
        HighlightInstruction(seq[index], immediate: false, additive: additive);
        _docked = false;
    }

    /// <summary>Shrinks the panel into the bottom-right corner (label + live feedback stay on).</summary>
    public void DockToCorner() { if (_sandboxMode) _docked = true; }

    /// <summary>Grows the panel back to its centred, full-size pose.</summary>
    public void PresentCentered() { if (_sandboxMode) _docked = false; }

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
        _docked = false;
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

    // Returns true when sandbox mode owns position/scale this frame.
    private bool UpdateSandboxPose()
    {
        if (!_sandboxMode || !_rootRectTransform) return false;

        Vector2 targetPos   = _docked ? ComputeDockPosition() : _presentPosition;
        Vector3 targetScale = _docked ? _sandboxBaseScale * _dockScale : _sandboxBaseScale;

        float k = 1f - Mathf.Exp(-_dockLerpSpeed * Time.unscaledDeltaTime);
        _rootRectTransform.anchoredPosition = Vector2.Lerp(_rootRectTransform.anchoredPosition, targetPos, k);
        transform.localScale = Vector3.Lerp(transform.localScale, targetScale, k);
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

    // Bottom-right of the screen, inset by the margin. If the avoid rect (lower-third narration)
    // overlaps that corner horizontally, the panel sits on top of it instead.
    private Vector2 ComputeDockPosition()
    {
        if (!TryGetScreenSpace(out var canvasRT, out var parentRT)) return _presentPosition;

        Rect r = canvasRT.rect;
        Vector2 size  = Vector2.Scale(_rootRectTransform.rect.size, (Vector2)(_sandboxBaseScale * _dockScale));
        Vector2 pivot = _rootRectTransform.pivot;

        float right  = r.xMax - _dockMargin;
        float bottom = r.yMin + _dockMargin;
        float left   = right - size.x;

        if (_dockAvoid != null && _dockAvoid.gameObject.activeInHierarchy)
        {
            var corners = new Vector3[4];
            _dockAvoid.GetWorldCorners(corners);
            Vector2 aMin = canvasRT.InverseTransformPoint(corners[0]);
            Vector2 aMax = canvasRT.InverseTransformPoint(corners[2]);
            bool overlapsX = aMax.x > left && aMin.x < right;
            if (overlapsX && aMax.y + _dockMargin > bottom)
                bottom = aMax.y + _dockMargin;
        }

        // Where the pivot must sit for the scaled panel to occupy [left..right] x [bottom..].
        Vector2 pivotInCanvas = new Vector2(left + size.x * pivot.x, bottom + size.y * pivot.y);
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
