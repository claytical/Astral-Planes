using System.Collections;
using TMPro;
using UnityEngine;

/// <summary>One-line-at-a-time narrator label with fades. Binds to an authored panel, or builds its own overlay canvas.</summary>
public class TutorialPromptView : MonoBehaviour
{
    [SerializeField] private TMP_Text label;
    [SerializeField] private float lineFadeSeconds = 0.45f;   // sequential lines: slow cross-fade
    [SerializeField] private float eventFadeSeconds = 0.15f;  // event-triggered: snappy

    Coroutine _fade;
    CanvasGroup _group; // bound mode: fades the whole authored panel (background + text) together

    /// <summary>
    /// Binds to an authored panel (e.g. Canvas/UI/Tutorial). The user's font/size/colour/alignment are
    /// left alone; only a CanvasGroup on the panel animates. Activates the panel, starting fully faded.
    /// </summary>
    public static TutorialPromptView Bind(Transform panel, TMP_Text label)
    {
        var view = panel.gameObject.GetComponent<TutorialPromptView>();
        if (view == null) view = panel.gameObject.AddComponent<TutorialPromptView>();
        view.label = label;

        var group = panel.GetComponent<CanvasGroup>();
        if (group == null) group = panel.gameObject.AddComponent<CanvasGroup>();
        group.alpha = 0f;
        group.interactable = false;
        group.blocksRaycasts = false; // the panel's Image has raycast target on; don't eat clicks
        view._group = group;

        // Nested override-sorting canvas so the text draws above ring meshes (orders 1-4, dots 10000).
        var cv = panel.GetComponent<Canvas>();
        if (cv == null) cv = panel.gameObject.AddComponent<Canvas>();
        cv.overrideSorting = true;
        var parentCanvas = panel.parent != null ? panel.parent.GetComponentInParent<Canvas>(true) : null;
        if (parentCanvas != null) cv.sortingLayerID = parentCanvas.sortingLayerID;
        cv.sortingOrder = 20000;

        label.text = string.Empty;
        panel.gameObject.SetActive(true);
        return view;
    }

    /// <summary>Hides the bound panel again (returns the authored default of inactive).</summary>
    public void Deactivate()
    {
        if (_group != null) { _group.alpha = 0f; _group.gameObject.SetActive(false); }
        else HideImmediate();
    }

    /// <summary>
    /// Binds to the authored lower-third panel at <paramref name="panelPath"/> (root name, then a
    /// Transform.Find path, e.g. "Canvas/UI/Tutorial" with a TMP_Text child) in the active scene.
    /// Returns null (after a warning) if it isn't there. Shared by every narrated sequence so they
    /// all read from the same place on screen.
    /// </summary>
    public static TutorialPromptView BindAuthored(string panelPath, string logTag = "Tutorial")
    {
        string path = string.IsNullOrEmpty(panelPath) ? "Canvas/UI/Tutorial" : panelPath;
        int slash = path.IndexOf('/');
        string rootName = slash < 0 ? path : path.Substring(0, slash);
        string rest = slash < 0 ? null : path.Substring(slash + 1);

        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        foreach (var root in scene.GetRootGameObjects())
        {
            if (root.name != rootName) continue;
            // Transform.Find reaches inactive children, unlike GameObject.Find.
            var panel = rest == null ? root.transform : root.transform.Find(rest);
            if (panel == null) continue;
            var label = panel.GetComponentInChildren<TMP_Text>(true);
            if (label == null) continue;
            return Bind(panel, label);
        }

        Debug.LogWarning($"[{logTag}] Authored panel '{path}' (with a TMP_Text child) not found in '{scene.name}'; using a generated overlay.");
        return null;
    }

    /// <summary>"{s}" is a plural suffix: "Wanderer{s}" -> "Wanderers" with 2+ players.</summary>
    public static string ExpandTokens(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("{s}")) return text;
        int players = GameFlowManager.Instance?.SessionState?.Players?.Count ?? 1;
        return text.Replace("{s}", players >= 2 ? "s" : "");
    }

    /// <summary>The bound panel's rect (null for a generated overlay) — lets other UI keep clear of it.</summary>
    public RectTransform PanelRect => _group != null ? _group.transform as RectTransform : null;

    public static TutorialPromptView Create(Transform parent)
    {
        var root = new GameObject("TutorialPrompt", typeof(Canvas));
        root.transform.SetParent(parent, false);
        var canvas = root.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 200;

        var textGo = new GameObject("Line", typeof(RectTransform), typeof(TextMeshProUGUI));
        textGo.transform.SetParent(root.transform, false);
        var rt = (RectTransform)textGo.transform;
        rt.anchorMin = new Vector2(0.1f, 0.06f);
        rt.anchorMax = new Vector2(0.9f, 0.2f);
        rt.offsetMin = rt.offsetMax = Vector2.zero;

        var t = textGo.GetComponent<TextMeshProUGUI>();
        t.alignment = TextAlignmentOptions.Center;
        t.fontSize = 36;
        t.enableAutoSizing = true;
        t.fontSizeMin = 18;
        t.fontSizeMax = 40;
        t.raycastTarget = false;
        t.color = new Color(1, 1, 1, 0);

        var view = root.AddComponent<TutorialPromptView>();
        view.label = t;
        return view;
    }

    /// <summary>Shows a line. eventDriven=true uses the short fade (gate fired / interruption).</summary>
    public IEnumerator Show(string text, bool eventDriven)
    {
        float d = eventDriven ? eventFadeSeconds : lineFadeSeconds;
        yield return FadeTo(0f, d * 0.5f);
        label.text = text;
        yield return FadeTo(1f, d);
    }

    public IEnumerator Hide() => FadeTo(0f, lineFadeSeconds);

    public void HideImmediate() => SetAlpha(0f);

    float GetAlpha() => _group != null ? _group.alpha : label.color.a;

    void SetAlpha(float a)
    {
        if (_group != null) { _group.alpha = a; return; }
        if (label == null) return;
        var c = label.color;
        c.a = a;
        label.color = c;
    }

    IEnumerator FadeTo(float a, float seconds)
    {
        float from = GetAlpha(), t = 0f;
        if (seconds <= 0f || Mathf.Approximately(from, a)) { SetAlpha(a); yield break; }
        while (t < seconds)
        {
            t += Time.unscaledDeltaTime;
            SetAlpha(Mathf.Lerp(from, a, t / seconds));
            yield return null;
        }
        SetAlpha(a);
    }
}
