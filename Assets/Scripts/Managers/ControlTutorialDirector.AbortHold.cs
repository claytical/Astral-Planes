public partial class ControlTutorialDirector
{
    // Authored in the Editor as a child of primaryPrefab's "Primary" sub-hierarchy (Control
    // Scheme.prefab) — NOT the "Mini" sub-hierarchy LocalPlayer.miniTutorialPrefab points to.
    // Discovered fresh each scene load in RebuildPrimaryInstanceForScene(). Deliberately not
    // routed through primaryInstance/ShowWaitingFor — ShowWaitingFor calls StopAllModes()
    // internally, which would kill the running Drift/Boost/Release tutorial coroutine on an
    // early release. AbortHoldPrompt is a separate component, so it never touches that state.
    private AbortHoldPrompt _abortHoldPrompt;

    public void BeginAbortHoldUI(string label, float holdDurationSeconds) => _abortHoldPrompt?.BeginHold(label, holdDurationSeconds);

    public void UpdateAbortHoldUI(float t01) => _abortHoldPrompt?.UpdateHold(t01);

    public void CancelAbortHoldUI() => _abortHoldPrompt?.CancelHold();

    public void EndAbortHoldUI() => _abortHoldPrompt?.EndHold();

    // ── Hold-to-rejoin slot ────────────────────────────────────────────────────
    // A gamepad that backed out must hold confirm to rejoin (TrackSelectionJoinController).
    // While it holds, a player-sized slot appears in the ship-select row (Hangar.planeSelection,
    // a HorizontalLayoutGroup) next to the remaining players' panels — never the full-screen
    // primary prompt, which would cover them. The slot holds a compact control-scheme callout
    // ("rejoin", confirm highlighted for that pad: A on Switch) and fades in as the hold fills:
    // the mirror of the leave-hold fade-out on PlayerSelect.
    private const float RejoinSlotMinAlpha = 0.15f;
    private const float RejoinSlotFill = 0.8f; // callout width as a fraction of the slot width

    private UnityEngine.GameObject _rejoinSlot;
    private UnityEngine.CanvasGroup _rejoinSlotGroup;

    public void BeginRejoinHoldUI(UnityEngine.InputSystem.Gamepad pad)
    {
        EndRejoinHoldUI();

        var row = Hangar.Instance != null ? Hangar.Instance.planeSelection : null;
        if (row == null || primaryPrefab == null) return;

        _rejoinSlot = new UnityEngine.GameObject("Rejoin Slot", typeof(UnityEngine.RectTransform), typeof(UnityEngine.CanvasGroup));
        var slot = (UnityEngine.RectTransform)_rejoinSlot.transform;
        slot.SetParent(row.transform, worldPositionStays: false);
        slot.localScale = UnityEngine.Vector3.one;

        // Match a real player panel so the row lays out as if a player were there.
        var sibling = row.GetComponentInChildren<PlayerSelect>();
        slot.sizeDelta = sibling != null
            ? ((UnityEngine.RectTransform)sibling.transform).sizeDelta
            : new UnityEngine.Vector2(300f, 600f);

        _rejoinSlotGroup = _rejoinSlot.GetComponent<UnityEngine.CanvasGroup>();
        _rejoinSlotGroup.alpha = RejoinSlotMinAlpha;
        _rejoinSlotGroup.interactable = false;
        _rejoinSlotGroup.blocksRaycasts = false;

        var callout = Instantiate(primaryPrefab, slot);
        callout.SetActive(true);
        var calloutRt = callout.transform as UnityEngine.RectTransform;
        callout.transform.localPosition = UnityEngine.Vector3.zero;
        callout.transform.localRotation = UnityEngine.Quaternion.identity;

        // Scale the callout to fit the slot's width.
        float width = calloutRt != null ? calloutRt.rect.width : 0f;
        float scale = width > 1f ? slot.sizeDelta.x * RejoinSlotFill / width : primaryCompactScale.x;
        callout.transform.localScale = new UnityEngine.Vector3(scale, scale, 1f);

        // The clone carries the primary's hold bar; the fade is the cue here.
        foreach (var bar in callout.GetComponentsInChildren<AbortHoldPrompt>(true))
            bar.gameObject.SetActive(false);

        var highlight = callout.GetComponentInChildren<ControlTutorialHighlight>(true);
        if (highlight != null)
        {
            highlight.gameObject.SetActive(true);
            highlight.SetVisible(true);
            highlight.SetControllerContext(pad);
            highlight.ShowWaitingFor(ControlTutorialHighlight.ButtonId.South, immediate: true, overrideText: "rejoin");
        }
    }

    public void UpdateRejoinHoldUI(float t01)
    {
        if (_rejoinSlotGroup == null) return;
        float eased = UnityEngine.Mathf.SmoothStep(0f, 1f, UnityEngine.Mathf.Clamp01(t01));
        _rejoinSlotGroup.alpha = UnityEngine.Mathf.Lerp(RejoinSlotMinAlpha, 1f, eased);
    }

    public void EndRejoinHoldUI()
    {
        if (_rejoinSlot != null) Destroy(_rejoinSlot);
        _rejoinSlot = null;
        _rejoinSlotGroup = null;
    }
}
