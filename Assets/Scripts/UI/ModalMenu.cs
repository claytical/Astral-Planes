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
    /// <summary>Value rows only: current value text, drawn as "label  <  value  >".</summary>
    public Func<string> value;
    /// <summary>Value rows only: Left/Right call this with -1/+1 (held = repeat); Confirm calls +1.
    /// The modal stays open and the row redraws.</summary>
    public Action<int> onAdjust;
    /// <summary>Value rows only: false for toggles/choices, so holding Left/Right steps once
    /// instead of flipping every repeat tick (volume rows keep true).</summary>
    public bool repeats = true;

    public bool IsValueRow => onAdjust != null;

    public ModalMenuOption(string label, Action onChosen = null)
    {
        this.label = label;
        this.onChosen = onChosen;
    }

    public static ModalMenuOption Value(string label, Func<string> value, Action<int> onAdjust, bool repeats = true) =>
        new ModalMenuOption(label) { value = value, onAdjust = onAdjust, repeats = repeats };

    internal string DisplayText()
    {
        if (!IsValueRow) return label;
        string v = value != null ? value() : string.Empty;
        return $"{label}    <  {v}  >"; // ASCII: the modal font has no arrow glyphs; "< " is never parsed as a TMP tag
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
    /// <summary>Fired once whenever this spec stops being shown — chosen, cancelled, replaced or
    /// closed externally. Runs before any onChosen/onCancel.</summary>
    public Action onClosed;
}

/// <summary>
/// Generic overlay modal: title, optional description, vertical list of options, on its own
/// Screen Space Overlay canvas with a near-opaque black background. Call <see cref="Show"/> from
/// anywhere; the prefab (Resources/UI/ModalMenu.prefab, edited by hand — its builder is gone) is
/// loaded and kept alive across scenes on demand. MainMenu and OptionsScreen reuse its fonts and
/// ButtonTemplate (see <see cref="Prefab"/>).
///
/// Input is polled from the gamepad(s) and keyboard directly — the EventSystem's UI actions asset
/// is unreliable in these scenes, and polling keeps one input path for every scene.
/// Any open modal locks gameplay: input handlers check <see cref="ShouldSuppressInput"/>.
///
/// Left/Right: in a spec with any value row (<see cref="ModalMenuOption.Value"/>), Left/Right only
/// adjust the highlighted value row (held = repeat) and never move the highlight; Up/Down/Tab move.
/// In a spec with no value rows (Yes/No prompts), Left/Right move the highlight as before.
/// </summary>
public class ModalMenu : MonoBehaviour
{
    const string ResourcePath = "UI/ModalMenu";

    /// <summary>True while this modal or another full-screen menu (OptionsScreen, via
    /// <see cref="NotifyExternalOpen"/>) owns input.</summary>
    public static bool IsOpen => _shown || _externalOpen > 0;

    static bool _shown;
    static int _externalOpen;
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
    bool _hasValueRows;
    // Horizontal (value-row) input: held direction, next repeat time, and "must release first"
    // so a Left/Right already held when the modal opened can't adjust anything.
    int _hHeldDir;
    float _hRepeatAt;
    bool _hNeedsRelease;
    const float RepeatDelay = 0.35f;
    const float RepeatInterval = 0.08f;
    Coroutine _fade;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { _shown = false; _externalOpen = 0; _openedFrame = _closedFrame = -1; }

    /// <summary>Another full-screen menu opened (true) or closed (false). Counts toward
    /// <see cref="IsOpen"/>/<see cref="ShouldSuppressInput"/>, so every gameplay guard that checks
    /// the modal also respects it.</summary>
    internal static void NotifyExternalOpen(bool open)
    {
        if (open) { _externalOpen++; _openedFrame = Time.frameCount; }
        else if (_externalOpen > 0) { _externalOpen--; _closedFrame = Time.frameCount; }
    }

    static ModalMenu _prefab;
    /// <summary>The prefab asset (not an instance), for menus that borrow its fonts and row template.</summary>
    internal static ModalMenu Prefab => _prefab != null ? _prefab : (_prefab = Resources.Load<ModalMenu>(ResourcePath));
    internal TMP_Text TitleText => title;
    internal TMP_Text DescriptionText => description;
    internal ModalMenuButton ButtonTemplate => buttonTemplate;

    /// <summary>The spec currently shown, or null. Lets callers toggle their own modal.</summary>
    public static ModalMenuSpec Current => _shown && _instance != null ? _instance._spec : null;

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
        if (_shown && _instance != null) _instance.CloseInternal();
    }

    static ModalMenu GetInstance()
    {
        if (_instance != null) return _instance;
        if (_loadFailed) return null;

        var prefab = Prefab;
        if (prefab == null)
        {
            Debug.LogWarning("[ModalMenu] Resources/" + ResourcePath + " not found.");
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
        if (_shown && _spec != null && _spec != spec) EndSpec(); // replaced: the old spec is done
        _spec = spec;
        _hasValueRows = spec.options.Exists(o => o.IsValueRow);
        _hHeldDir = 0;
        _hNeedsRelease = true;
        _selected = Mathf.Clamp(spec.defaultIndex, 0, spec.options.Count - 1);
        _stickHeld = true; // ignore a stick already deflected at open
        _shown = true;
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
        EndSpec();
        _shown = false;
        _closedFrame = Time.frameCount;
        _spec = null;
        if (group != null) group.blocksRaycasts = false;
        StartFade(0f);
    }

    /// <summary>Fires the current spec's onClosed exactly once.</summary>
    void EndSpec()
    {
        var s = _spec;
        if (s == null || s.onClosed == null) return;
        var cb = s.onClosed;
        s.onClosed = null; // a spec reshown later re-arms it via its builder
        cb();
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
            if (used) _rows[i].SetLabel(options[i].DisplayText());
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
        if (_shown) { EndSpec(); _shown = false; _closedFrame = Time.frameCount; _spec = null; }
        _fade = null;
    }

    void OnDestroy()
    {
        if (_instance == this) { _instance = null; _shown = false; }
    }

    void Update()
    {
        if (!_shown || _spec == null) return;

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
            if (kb.upArrowKey.wasPressedThisFrame || (!_hasValueRows && kb.leftArrowKey.wasPressedThisFrame)) step = -1;
            else if (kb.downArrowKey.wasPressedThisFrame || (!_hasValueRows && kb.rightArrowKey.wasPressedThisFrame) || kb.tabKey.wasPressedThisFrame) step = +1;
            confirm |= kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame;
            cancel |= kb.escapeKey.wasPressedThisFrame;
        }

        int count = _spec.options.Count;
        if (step != 0)
        {
            _selected = (_selected + step + count) % count;
            RefreshHighlight();
        }

        if (_hasValueRows)
        {
            int adjust = PollHorizontalRepeat(out bool isRepeat);
            var row = _spec.options[_selected];
            if (adjust != 0 && row.IsValueRow && (!isRepeat || row.repeats)) Adjust(adjust);
        }

        if (confirm && _spec.options[_selected].IsValueRow)
        {
            Adjust(+1); // Confirm steps a value row (wraps for toggles) and keeps the menu open
        }
        else if (confirm)
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

    void Adjust(int dir)
    {
        var opt = _spec.options[_selected];
        opt.onAdjust(dir);
        // The callback may have replaced or closed the modal (e.g. a confirm sub-prompt).
        if (_spec == null || !_shown) return;
        if (_selected < _rows.Count && _selected < _spec.options.Count)
            _rows[_selected].SetLabel(_spec.options[_selected].DisplayText());
    }

    /// <summary>Held Left/Right direction from the allowed pad(s) and keyboard, as one step on
    /// press then repeats while held. Returns -1, 0 or +1.</summary>
    int PollHorizontalRepeat(out bool isRepeat)
    {
        isRepeat = false;
        int dir = 0;
        if (_spec.pad != null) dir = HeldHorizontal(_spec.pad);
        else
            foreach (var p in Gamepad.all) { dir = HeldHorizontal(p); if (dir != 0) break; }

        var kb = Keyboard.current;
        if (dir == 0 && kb != null)
        {
            if (kb.leftArrowKey.isPressed) dir = -1;
            else if (kb.rightArrowKey.isPressed) dir = +1;
        }

        if (dir == 0) { _hHeldDir = 0; _hNeedsRelease = false; return 0; }
        if (_hNeedsRelease) return 0;

        float now = Time.unscaledTime;
        if (dir != _hHeldDir)
        {
            _hHeldDir = dir;
            _hRepeatAt = now + RepeatDelay;
            return dir;
        }
        if (now >= _hRepeatAt)
        {
            _hRepeatAt = now + RepeatInterval;
            isRepeat = true;
            return dir;
        }
        return 0;
    }

    static int HeldHorizontal(Gamepad pad)
    {
        if (pad == null) return 0;
        if (pad.dpad.left.isPressed) return -1;
        if (pad.dpad.right.isPressed) return +1;
        Vector2 s = pad.leftStick.ReadValue();
        if (Mathf.Abs(s.x) > 0.6f && Mathf.Abs(s.x) > Mathf.Abs(s.y)) return s.x > 0f ? +1 : -1;
        return 0;
    }

    // Steps are assigned (not summed) so a device reported twice (Steam Deck) can't double-step.
    // With value rows, horizontal input belongs to PollHorizontalRepeat, never to the highlight.
    void PollPad(Gamepad pad, ref int step, ref bool confirm, ref bool cancel)
    {
        if (pad == null) return;
        if (pad.dpad.up.wasPressedThisFrame || (!_hasValueRows && pad.dpad.left.wasPressedThisFrame)) step = -1;
        else if (pad.dpad.down.wasPressedThisFrame || (!_hasValueRows && pad.dpad.right.wasPressedThisFrame)) step = +1;

        Vector2 s = pad.leftStick.ReadValue();
        bool vertical = Mathf.Abs(s.y) >= Mathf.Abs(s.x);
        float v = vertical ? s.y : s.x;
        if (Mathf.Abs(v) > 0.6f && !_stickHeld && step == 0 && (vertical || !_hasValueRows))
            step = vertical ? (v > 0f ? -1 : +1) : (v > 0f ? +1 : -1);

        confirm |= PadSemantics.ConfirmButton(pad).wasPressedThisFrame;
        cancel |= PadSemantics.BackButton(pad).wasPressedThisFrame;
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
            if (target <= 0f && !_shown) gameObject.SetActive(false);
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
        if (target <= 0f && !_shown) gameObject.SetActive(false);
    }
}
