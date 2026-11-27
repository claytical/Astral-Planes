using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>One titled row in an OptionsScreen panel: a value adjusted with Left/Right, or an action.</summary>
public sealed class OptionsRow
{
    public string title;
    /// <summary>Value rows: the text on the right of the row.</summary>
    public Func<string> value;
    /// <summary>Value rows: Left/Right call this with -1/+1; Confirm calls +1.</summary>
    public Action<int> adjust;
    /// <summary>Value rows: false for toggles, so a held direction steps once.</summary>
    public bool repeats = true;
    /// <summary>Optional 0..1 bar drawn between title and value (volume sliders).</summary>
    public Func<float> fill;
    /// <summary>Action rows: Confirm runs this. OptionsScreen hides itself first, so the callback
    /// can show a ModalMenu prompt and reopen the screen afterwards.</summary>
    public Action activate;

    public bool IsValue => adjust != null;

    public static OptionsRow Value(string title, Func<string> value, Action<int> adjust, bool repeats = true, Func<float> fill = null) =>
        new OptionsRow { title = title, value = value, adjust = adjust, repeats = repeats, fill = fill };

    public static OptionsRow Action(string title, Action activate) =>
        new OptionsRow { title = title, activate = activate };
}

public sealed class OptionsCategory
{
    public string name;
    /// <summary>Rebuilt whenever the category is shown, so rows can depend on connected devices.</summary>
    public Func<List<OptionsRow>> rows;
    /// <summary>Optional text under the panel rows (help, the control reference).</summary>
    public Func<string> footer;
}

public sealed class OptionsScreenSpec
{
    public string title = "Options";
    public List<OptionsCategory> categories = new List<OptionsCategory>();
    public int categoryIndex;
    /// <summary>Row to focus in the panel on open; -1 opens with the category column focused.</summary>
    public int rowIndex = -1;
    /// <summary>Only this pad drives the screen; null = any.</summary>
    public Gamepad pad;
    /// <summary>Polled every frame; false closes the screen without calling onBack.</summary>
    public Func<bool> keepOpen;
    /// <summary>Back pressed on the category column.</summary>
    public Action onBack;
    /// <summary>Fired once whenever the screen closes or hides, before onBack or an action row's callback.</summary>
    public Action onClosed;
}

/// <summary>
/// Two-pane Options screen. The left column lists categories and moves with Up/Down. Right or
/// Confirm moves into the right panel of titled rows: Up/Down picks a row and Left/Right adjusts
/// it (held = repeat on sliders). Left on a row with no value, or Back, returns to the category
/// column. Back there closes the screen.
///
/// Built at runtime on its own overlay canvas (sort order 500, like ModalMenu), borrowing
/// ModalMenu's fonts and row template. While it is open it counts as an open modal
/// (ModalMenu.NotifyExternalOpen), so every gameplay input guard respects it.
/// </summary>
public sealed class OptionsScreen : MonoBehaviour
{
    const float CategoryWidth = 460f;
    const float PanelWidth = 1040f;
    const float RowHeight = 96f;
    const float RowSpacing = 18f;
    const float BarWidth = 300f;
    const float FadeSeconds = 0.15f;

    static OptionsScreen _instance;

    public static bool IsOpen => _instance != null && _instance._spec != null;

    OptionsScreenSpec _spec;
    readonly MenuNav _nav = new MenuNav();
    int _cat;
    int _row = -1; // -1 = focus on the category column
    List<OptionsRow> _rows = new List<OptionsRow>();
    int _openedFrame = -1;

    CanvasGroup _group;
    TMP_Text _title, _footer, _hint;
    RectTransform _catColumn, _panelColumn;
    readonly List<ModalMenuButton> _catRows = new List<ModalMenuButton>();
    readonly List<PanelRow> _panelRows = new List<PanelRow>();
    CanvasGroup _catGroup;
    float _alphaTarget;

    sealed class PanelRow
    {
        public ModalMenuButton button;
        public TMP_Text label, value;
        public RectTransform barRoot, barFill;
        public UnityEngine.UI.Image barBack, barFront;
        public string lastValue;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => _instance = null;

    // ---------------------------------------------------------------- API

    public static bool Open(OptionsScreenSpec spec)
    {
        if (spec == null || spec.categories.Count == 0) return false;
        var inst = GetInstance();
        if (inst == null) return false;
        inst.OpenInternal(spec);
        return true;
    }

    /// <summary>Closes without calling onBack.</summary>
    public static void Close()
    {
        if (IsOpen) _instance.CloseInternal();
    }

    static OptionsScreen GetInstance()
    {
        if (_instance != null) return _instance;
        if (ModalMenu.Prefab == null) { Debug.LogWarning("[OptionsScreen] Resources/UI/ModalMenu prefab missing; can't build Options."); return null; }
        var canvas = MenuKit.CreateCanvas("OptionsScreen", 500, out var group);
        DontDestroyOnLoad(canvas.gameObject);
        _instance = canvas.gameObject.AddComponent<OptionsScreen>();
        _instance.Build(canvas.transform, group);
        return _instance;
    }

    // ---------------------------------------------------------------- build

    void Build(Transform root, CanvasGroup group)
    {
        _group = group;
        _group.alpha = 0f;
        MenuKit.Fill(root, "Background", new Color(0f, 0f, 0f, 0.92f));

        var center = new Vector2(0.5f, 0.5f);
        _title = MenuKit.Text(root, "Title", MenuKit.TitleFont, 88f, TextAlignmentOptions.Center,
            new Vector2(0.5f, 1f), new Vector2(0f, -110f), new Vector2(1600f, 140f));

        float left = -(PanelWidth + RowSpacing * 3f) * 0.5f;
        _catColumn = MenuKit.Column(root, "Categories", center, new Vector2(left - 80f, 300f), CategoryWidth, RowSpacing);
        _catGroup = _catColumn.gameObject.AddComponent<CanvasGroup>();
        _panelColumn = MenuKit.Column(root, "Panel", center, new Vector2(left + (CategoryWidth + PanelWidth) * 0.5f + 20f, 300f), PanelWidth, RowSpacing);

        _footer = MenuKit.Text(root, "Footer", MenuKit.BodyFont, 36f, TextAlignmentOptions.TopLeft,
            center, new Vector2(_panelColumn.anchoredPosition.x, -280f), new Vector2(PanelWidth - 40f, 300f));
        _hint = MenuKit.Text(root, "Hint", MenuKit.BodyFont, 30f, TextAlignmentOptions.Center,
            new Vector2(0.5f, 0f), new Vector2(0f, 60f), new Vector2(1600f, 60f));
        _hint.color = new Color(1f, 1f, 1f, 0.55f);

        gameObject.SetActive(false);
    }

    ModalMenuButton CategoryRow(int i)
    {
        while (_catRows.Count <= i)
        {
            var b = MenuKit.Row(_catColumn, "Category" + _catRows.Count, new Vector2(CategoryWidth, RowHeight), TextAlignmentOptions.Center);
            _catRows.Add(b);
        }
        return _catRows[i];
    }

    PanelRow GetPanelRow(int i)
    {
        while (_panelRows.Count <= i)
        {
            var b = MenuKit.Row(_panelColumn, "Row" + _panelRows.Count, new Vector2(PanelWidth, RowHeight), TextAlignmentOptions.MidlineLeft);
            var pr = new PanelRow { button = b, label = b != null ? b.GetComponentInChildren<TMP_Text>(true) : null };
            if (b != null)
            {
                var rt = (RectTransform)b.transform;
                // Value text ends 40 px from the row's right edge; the slider bar sits just left of
                // the room "<  100%  >" needs.
                pr.value = MenuKit.Text(rt, "Value", MenuKit.RowFont, pr.label != null ? pr.label.fontSize : 52f,
                    TextAlignmentOptions.MidlineRight, new Vector2(1f, 0.5f), new Vector2(-260f, 0f), new Vector2(440f, RowHeight));
                pr.value.textWrappingMode = TextWrappingModes.NoWrap;

                pr.barRoot = MenuKit.Rect(rt, "Bar", new Vector2(1f, 0.5f), new Vector2(-280f - BarWidth * 0.5f, 0f), new Vector2(BarWidth, 14f));
                pr.barBack = MenuKit.Fill(pr.barRoot, "Track", new Color(1f, 1f, 1f, 0.18f));
                pr.barFill = MenuKit.Rect(pr.barRoot, "Fill", Vector2.zero, Vector2.zero, Vector2.zero);
                pr.barFill.anchorMin = Vector2.zero;
                pr.barFill.offsetMin = pr.barFill.offsetMax = Vector2.zero;
                pr.barFront = pr.barFill.gameObject.AddComponent<UnityEngine.UI.Image>();
                pr.barFront.raycastTarget = false;
            }
            _panelRows.Add(pr);
        }
        return _panelRows[i];
    }

    // ---------------------------------------------------------------- open / close

    void OpenInternal(OptionsScreenSpec spec)
    {
        bool wasOpen = _spec != null;
        if (wasOpen && _spec != spec) EndSpec();
        _spec = spec;
        _nav.pad = spec.pad;
        _nav.RequireRelease();
        _openedFrame = Time.frameCount;
        if (!wasOpen) ModalMenu.NotifyExternalOpen(true);

        _title.text = spec.title ?? "Options";
        _cat = Mathf.Clamp(spec.categoryIndex, 0, spec.categories.Count - 1);
        BuildCategories();
        ShowCategory(_cat);
        _row = spec.rowIndex >= 0 && _rows.Count > 0 ? Mathf.Clamp(spec.rowIndex, 0, _rows.Count - 1) : -1;
        RefreshAll();

        gameObject.SetActive(true);
        _alphaTarget = 1f;
    }

    void CloseInternal()
    {
        EndSpec();
        _spec = null;
        _alphaTarget = 0f;
        ModalMenu.NotifyExternalOpen(false);
    }

    void EndSpec()
    {
        var cb = _spec?.onClosed;
        if (_spec != null) _spec.onClosed = null;
        cb?.Invoke();
    }

    void OnDisable()
    {
        if (_spec != null) { EndSpec(); _spec = null; ModalMenu.NotifyExternalOpen(false); }
    }

    void OnDestroy()
    {
        if (_instance == this) _instance = null;
    }

    // ---------------------------------------------------------------- content

    void BuildCategories()
    {
        for (int i = 0; i < _catRows.Count; i++) if (_catRows[i] != null) _catRows[i].gameObject.SetActive(false);
        for (int i = 0; i < _spec.categories.Count; i++)
        {
            var b = CategoryRow(i);
            if (b == null) continue;
            b.gameObject.SetActive(true);
            b.SetLabel(_spec.categories[i].name);
        }
    }

    void ShowCategory(int index)
    {
        _cat = index;
        _rows = _spec.categories[index].rows?.Invoke() ?? new List<OptionsRow>();
        for (int i = 0; i < _panelRows.Count; i++) if (_panelRows[i].button != null) _panelRows[i].button.gameObject.SetActive(false);
        for (int i = 0; i < _rows.Count; i++)
        {
            var pr = GetPanelRow(i);
            if (pr.button == null) continue;
            pr.button.gameObject.SetActive(true);
            pr.button.SetLabel(_rows[i].title);
            pr.lastValue = null;
            bool hasBar = _rows[i].fill != null;
            pr.barRoot.gameObject.SetActive(hasBar);
            pr.value.gameObject.SetActive(_rows[i].IsValue);
        }
    }

    void RefreshAll()
    {
        bool panelFocus = _row >= 0;
        for (int i = 0; i < _spec.categories.Count && i < _catRows.Count; i++)
            if (_catRows[i] != null) _catRows[i].SetSelected(i == _cat);
        _catGroup.alpha = panelFocus ? 0.55f : 1f;

        for (int i = 0; i < _rows.Count && i < _panelRows.Count; i++)
        {
            var pr = _panelRows[i];
            if (pr.button == null) continue;
            bool sel = panelFocus && i == _row;
            pr.button.SetSelected(sel);
            Color c = sel ? MenuKit.LabelSelected : MenuKit.LabelNormal;
            pr.value.color = c;
            pr.barFront.color = c;
            pr.barBack.color = sel ? new Color(0f, 0f, 0f, 0.2f) : new Color(1f, 1f, 1f, 0.18f);
        }
        RefreshValues();

        string footer = _spec.categories[_cat].footer?.Invoke();
        _footer.text = footer ?? string.Empty;
        _hint.text = panelFocus
            ? "Up / Down  choose     Left / Right  change     Back  categories"
            : "Up / Down  choose     Right  open     Back  close";
    }

    void RefreshValues()
    {
        for (int i = 0; i < _rows.Count && i < _panelRows.Count; i++)
        {
            var row = _rows[i];
            var pr = _panelRows[i];
            if (pr.button == null || !row.IsValue) continue;
            string v = row.value != null ? row.value() : string.Empty;
            bool sel = _row == i;
            string shown = sel ? $"<  {v}  >" : v;
            if (shown != pr.lastValue) { pr.value.text = shown; pr.lastValue = shown; }
            if (row.fill != null)
                pr.barFill.anchorMax = new Vector2(Mathf.Clamp01(row.fill()), 1f);
        }
    }

    // ---------------------------------------------------------------- input

    void Update()
    {
        // Fade (unscaled), then deactivate once fully hidden.
        if (_group.alpha != _alphaTarget)
        {
            _group.alpha = Mathf.MoveTowards(_group.alpha, _alphaTarget, Time.unscaledDeltaTime / FadeSeconds);
            if (_group.alpha <= 0f && _spec == null) { gameObject.SetActive(false); return; }
        }
        if (_spec == null) return;

        if (_spec.keepOpen != null && !_spec.keepOpen()) { CloseInternal(); return; }
        if (Time.frameCount == _openedFrame) return;

        var f = _nav.Poll();
        if (f.back && MenuNav.EscapePressedThisFrame) MenuNav.ClaimEscape();

        if (_row < 0) UpdateCategories(f);
        else UpdatePanel(f);

        if (_spec != null) RefreshValues(); // values may change elsewhere (publish, B key, restore)
    }

    void UpdateCategories(MenuNav.Frame f)
    {
        int count = _spec.categories.Count;
        if (f.vertical != 0)
        {
            ShowCategory((_cat + f.vertical + count) % count);
            RefreshAll();
            return;
        }
        if ((f.horizontal > 0 && !f.hRepeat) || f.confirm)
        {
            if (_rows.Count == 0) return;
            _row = 0;
            _nav.RequireRelease(); // the held Right must not also nudge the first slider
            RefreshAll();
            return;
        }
        if (f.back)
        {
            var onBack = _spec.onBack;
            CloseInternal();
            onBack?.Invoke();
        }
    }

    void UpdatePanel(MenuNav.Frame f)
    {
        if (_rows.Count == 0) { _row = -1; RefreshAll(); return; }
        var row = _rows[_row];

        if (f.vertical != 0)
        {
            _row = (_row + f.vertical + _rows.Count) % _rows.Count;
            RefreshAll();
            return;
        }
        if (f.back || (f.horizontal < 0 && !f.hRepeat && !row.IsValue))
        {
            _row = -1;
            _nav.RequireRelease();
            RefreshAll();
            return;
        }
        if (f.horizontal != 0 && row.IsValue && (!f.hRepeat || row.repeats))
        {
            Adjust(row, f.horizontal);
            return;
        }
        if (f.confirm)
        {
            if (row.IsValue) { Adjust(row, +1); return; }
            if (row.activate != null)
            {
                var act = row.activate;
                CloseInternal(); // hide first so the action can show a ModalMenu prompt
                act();
            }
        }
    }

    void Adjust(OptionsRow row, int dir)
    {
        row.adjust(dir);
        if (_spec == null) return;
        // Some adjustments change which rows exist (none today) or the footer (control reference).
        RefreshAll();
    }
}
