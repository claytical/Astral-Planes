using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Builds MainMenu and OptionsScreen in code: overlay canvases, text, plain rects and rows that
/// copy ModalMenu's ButtonTemplate. Fonts and row look come from Resources/UI/ModalMenu.prefab,
/// so restyling that prefab restyles every menu, and no new prefab or builder is needed.
/// </summary>
internal static class MenuKit
{
    public static readonly Color LabelNormal = new Color(0.8f, 0.8f, 0.85f, 1f);   // ModalMenuButton defaults
    public static readonly Color LabelSelected = Color.black;

    static ModalMenu Source => ModalMenu.Prefab;

    public static TMP_FontAsset TitleFont => Source != null && Source.TitleText != null ? Source.TitleText.font : null;
    public static TMP_FontAsset BodyFont => Source != null && Source.DescriptionText != null ? Source.DescriptionText.font : null;
    public static TMP_FontAsset RowFont
    {
        get
        {
            var t = Source != null ? Source.ButtonTemplate : null;
            var label = t != null ? t.GetComponentInChildren<TMP_Text>(true) : null;
            return label != null ? label.font : null;
        }
    }

    public static Canvas CreateCanvas(string name, int sortOrder, out CanvasGroup group)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(CanvasGroup));
        var canvas = go.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = sortOrder;
        var scaler = go.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;
        group = go.GetComponent<CanvasGroup>();
        group.interactable = false;
        group.blocksRaycasts = false;
        return canvas;
    }

    public static RectTransform Rect(Transform parent, string name, Vector2 anchor, Vector2 pos, Vector2 size)
    {
        var go = new GameObject(name, typeof(RectTransform));
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        rt.anchorMin = rt.anchorMax = anchor;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
        return rt;
    }

    public static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
    }

    public static Image Fill(Transform parent, string name, Color color)
    {
        var rt = Rect(parent, name, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
        Stretch(rt);
        var img = rt.gameObject.AddComponent<Image>();
        img.color = color;
        img.raycastTarget = false;
        return img;
    }

    public static TextMeshProUGUI Text(Transform parent, string name, TMP_FontAsset font, float size,
        TextAlignmentOptions align, Vector2 anchor, Vector2 pos, Vector2 box)
    {
        var rt = Rect(parent, name, anchor, pos, box);
        var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
        if (font != null) t.font = font;
        t.fontSize = size;
        t.alignment = align;
        t.color = Color.white;
        t.raycastTarget = false;
        t.richText = false; // labels like "<  80%  >" must never parse as tags
        t.textWrappingMode = TextWrappingModes.Normal;
        return t;
    }

    /// <summary>A column that stacks rows top-down at their LayoutElement sizes.</summary>
    public static RectTransform Column(Transform parent, string name, Vector2 anchor, Vector2 pos, float width, float spacing)
    {
        var rt = Rect(parent, name, anchor, pos, new Vector2(width, 0f));
        rt.pivot = new Vector2(0.5f, 1f);
        var layout = rt.gameObject.AddComponent<VerticalLayoutGroup>();
        layout.childAlignment = TextAnchor.UpperCenter;
        layout.spacing = spacing;
        layout.childControlWidth = layout.childControlHeight = true;
        layout.childForceExpandWidth = layout.childForceExpandHeight = false;
        var fitter = rt.gameObject.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        return rt;
    }

    /// <summary>A copy of ModalMenu's ButtonTemplate sized <paramref name="size"/>; null if the prefab is missing.</summary>
    public static ModalMenuButton Row(Transform parent, string name, Vector2 size, TextAlignmentOptions align)
    {
        var template = Source != null ? Source.ButtonTemplate : null;
        if (template == null) return null;
        var row = Object.Instantiate(template, parent, false);
        row.name = name;
        row.gameObject.SetActive(true);
        var le = row.GetComponent<LayoutElement>();
        if (le == null) le = row.gameObject.AddComponent<LayoutElement>();
        le.preferredWidth = le.minWidth = size.x;
        le.preferredHeight = le.minHeight = size.y;
        ((RectTransform)row.transform).sizeDelta = size;
        var label = row.GetComponentInChildren<TMP_Text>(true);
        if (label != null)
        {
            label.alignment = align;
            label.richText = false;
            if (align == TextAlignmentOptions.MidlineLeft || align == TextAlignmentOptions.Left)
                label.margin = new Vector4(40f, 0f, 40f, 0f);
        }
        return row;
    }
}
