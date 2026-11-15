using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

public sealed class ModalMenuOption
{
    public string label;
    public Action onChosen;

    public ModalMenuOption(string label, Action onChosen = null)
    {
        this.label = label;
        this.onChosen = onChosen;
    }
}

public sealed class ModalMenuSpec
{
    public string title;
    /// <summary>Optional; hidden when null/empty.</summary>
    public string description;
    public List<ModalMenuOption> options = new List<ModalMenuOption>();
    public int defaultIndex;
    /// <summary>East / Escape close the modal (and invoke <see cref="onCancel"/>).</summary>
    public bool cancelable = true;
    public Action onCancel;
    /// <summary>Gamepad allowed to drive the modal. Null = any connected gamepad.</summary>
    public Gamepad pad;
    /// <summary>Polled every frame while open; returning false closes the modal (no onCancel).</summary>
    public Func<bool> keepOpen;
}

/// <summary>
/// Generic overlay modal: title, optional description, vertical list of options, on its own
/// Screen Space Overlay canvas with a near-opaque black background. Call <see cref="Show"/> from
/// anywhere; the prefab (Resources/UI/ModalMenu.prefab, built by
/// Tools > Astral Planes > Build Modal Menu Prefab) is loaded and kept alive across scenes on demand.
///
/// Input is polled from the gamepad(s) and keyboard directly — the EventSystem's UI actions asset
/// is unreliable in these scenes, and polling keeps one input path for every scene.
/// Any open modal locks gameplay: input handlers check <see cref="ShouldSuppressInput"/>.
/// </summary>
public class ModalMenu : MonoBehaviour
{
    const string ResourcePath = "UI/ModalMenu";

    public static bool IsOpen { get; private set; }

    static int _openedFrame = -1;
    static int _closedFrame = -1;
    /// <summary>True while open, and on the frames it opened or closed (so that press can't leak into gameplay).</summary>
    public static bool ShouldSuppressInput =>
        IsOpen || Time.frameCount == _closedFrame || Time.frameCount == _openedFrame;

    static ModalMenu _instance;
    static bool _loadFailed;

    [SerializeField] CanvasGroup group;
    [SerializeField] TMP_Text title;
    [SerializeField] TMP_Text description;
    [SerializeField] RectTransform buttonList;
    [SerializeField] ModalMenuButton buttonTemplate;
    [SerializeField] float fadeSeconds = 0.15f;

    readonly List<ModalMenuButton> _rows = new List<ModalMenuButton>();
    ModalMenuSpec _spec;
    int _selected;
    bool _stickHeld;
    Coroutine _fade;

    /// <summary>The spec currently shown, or null. Lets callers toggle their own modal.</summary>
    public static ModalMenuSpec Current => IsOpen && _instance != null ? _instance._spec : null;

    /// <summary>Shows <paramref name="spec"/>, replacing whatever modal is open. Returns false if the prefab is missing.</summary>
    public static bool Show(ModalMenuSpec spec)
    {
        if (spec == null || spec.options == null || spec.options.Count == 0)
        {
            Debug.LogWarning("[ModalMenu] Show called without options.");
            return false;
        }
        var inst = GetInstance();
        if (inst == null) return false;
        inst.ShowInternal(spec);
        return true;
    }

    /// <summary>Closes the open modal without invoking any callback.</summary>
    public static void Close()
    {
        if (IsOpen && _instance != null) _instance.CloseInternal();
    }

    static ModalMenu GetInstance()
    {
        if (_instance != null) return _instance;
        if (_loadFailed) return null;

        var prefab = Resources.Load<ModalMenu>(ResourcePath);
        if (prefab == null)
        {
            Debug.LogWarning("[ModalMenu] Resources/" + ResourcePath + " not found — run Tools > Astral Planes > Build Modal Menu Prefab.");
            _loadFailed = true;
            return null;
        }
        _instance = Instantiate(prefab);
        _instance.name = "ModalMenu";
        DontDestroyOnLoad(_instance.gameObject);
        if (_instance.buttonTemplate != null) _instance.buttonTemplate.gameObject.SetActive(false);
        return _instance;
    }

    void ShowInternal(ModalMenuSpec spec)
    {
        _spec = spec;
        _selected = Mathf.Clamp(spec.defaultIndex, 0, spec.options.Count - 1);
        _stickHeld = true; // ignore a stick already deflected at open
        IsOpen = true;
        _openedFrame = Time.frameCount;

        if (title != null) title.text = spec.title ?? string.Empty;
        if (description != null)
        {
            bool hasDesc = !string.IsNullOrEmpty(spec.description);
            description.gameObject.SetActive(hasDesc);
            if (hasDesc) description.text = spec.description;
        }
        BuildRows(spec.options);
        RefreshHighlight();

        if (!gameObject.activeSelf)
        {
            if (group != null) group.alpha = 0f;
            gameObject.SetActive(true);
        }
        if (group != null) group.blocksRaycasts = true;
        StartFade(1f);
    }

    void CloseInternal()
    {
        IsOpen = false;
        _closedFrame = Time.frameCount;
        _spec = null;
        if (group != null) group.blocksRaycasts = false;
        StartFade(0f);
    }

    void BuildRows(List<ModalMenuOption> options)
    {
        if (buttonTemplate == null || buttonList == null) return;
        while (_rows.Count < options.Count)
        {
            var row = Instantiate(buttonTemplate, buttonList);
            row.name = "Option" + _rows.Count;
            _rows.Add(row);
        }
        for (int i = 0; i < _rows.Count; i++)
        {
            bool used = i < options.Count;
            _rows[i].gameObject.SetActive(used);
            if (used) _rows[i].SetLabel(options[i].label);
        }
    }

    void RefreshHighlight()
    {
        if (_spec == null) return;
        for (int i = 0; i < _spec.options.Count && i < _rows.Count; i++)
            _rows[i].SetSelected(i == _selected);
    }

    void OnDisable()
    {
        // Deactivated externally: never leave gameplay locked.
        if (IsOpen) { IsOpen = false; _closedFrame = Time.frameCount; _spec = null; }
        _fade = null;
    }

    void OnDestroy()
    {
        if (_instance == this) { _instance = null; IsOpen = false; }
    }

    void Update()
    {
        if (!IsOpen || _spec == null) return;

        if (_spec.keepOpen != null && !_spec.keepOpen()) { CloseInternal(); return; }

        // The press that opened us must not also act on us.
        if (Time.frameCount == _openedFrame) return;

        int step = 0;
        bool confirm = false, cancel = false;

        if (_spec.pad != null) PollPad(_spec.pad, ref step, ref confirm, ref cancel);
        else
        {
            var pads = Gamepad.all;
            for (int i = 0; i < pads.Count; i++) PollPad(pads[i], ref step, ref confirm, ref cancel);
        }
        UpdateStickLatch();

        var kb = Keyboard.current;
        if (kb != null)
        {
            if (kb.upArrowKey.wasPressedThisFrame || kb.leftArrowKey.wasPressedThisFrame) step = -1;
            else if (kb.downArrowKey.wasPressedThisFrame || kb.rightArrowKey.wasPressedThisFrame || kb.tabKey.wasPressedThisFrame) step = +1;
            confirm |= kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame;
            cancel |= kb.escapeKey.wasPressedThisFrame;
        }

        int count = _spec.options.Count;
        if (step != 0)
        {
            _selected = (_selected + step + count) % count;
            RefreshHighlight();
        }

        if (confirm)
        {
            var chosen = _spec.options[_selected];
            CloseInternal(); // close first so the callback may open another modal
            chosen.onChosen?.Invoke();
        }
        else if (cancel && _spec.cancelable)
        {
            var onCancel = _spec.onCancel;
            CloseInternal();
            onCancel?.Invoke();
        }
    }

    // Steps are assigned (not summed) so a device reported twice (Steam Deck) can't double-step.
    void PollPad(Gamepad pad, ref int step, ref bool confirm, ref bool cancel)
    {
        if (pad == null) return;
        if (pad.dpad.up.wasPressedThisFrame || pad.dpad.left.wasPressedThisFrame) step = -1;
        else if (pad.dpad.down.wasPressedThisFrame || pad.dpad.right.wasPressedThisFrame) step = +1;

        Vector2 s = pad.leftStick.ReadValue();
        bool vertical = Mathf.Abs(s.y) >= Mathf.Abs(s.x);
        float v = vertical ? s.y : s.x;
        if (Mathf.Abs(v) > 0.6f && !_stickHeld && step == 0)
            step = vertical ? (v > 0f ? -1 : +1) : (v > 0f ? +1 : -1);

        confirm |= pad.buttonSouth.wasPressedThisFrame;
        cancel |= pad.buttonEast.wasPressedThisFrame;
    }

    void UpdateStickLatch()
    {
        bool deflected = false;
        if (_spec.pad != null) deflected = _spec.pad.leftStick.ReadValue().magnitude > 0.6f;
        else
            foreach (var p in Gamepad.all)
                if (p.leftStick.ReadValue().magnitude > 0.6f) { deflected = true; break; }
        _stickHeld = deflected;
    }

    void StartFade(float target)
    {
        if (_fade != null) StopCoroutine(_fade);
        if (!isActiveAndEnabled || group == null)
        {
            if (group != null) group.alpha = target;
            if (target <= 0f && !IsOpen) gameObject.SetActive(false);
            return;
        }
        _fade = StartCoroutine(FadeTo(target));
    }

    IEnumerator FadeTo(float target)
    {
        float from = group.alpha, t = 0f;
        while (t < fadeSeconds)
        {
            t += Time.unscaledDeltaTime;
            group.alpha = Mathf.Lerp(from, target, Mathf.Clamp01(t / fadeSeconds));
            yield return null;
        }
        group.alpha = target;
        _fade = null;
        if (target <= 0f && !IsOpen) gameObject.SetActive(false);
    }
}
