using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Presentation zoom for the main (orthographic) camera: brings attention to one thing (the
/// docked record) without anything appearing to move.
///
///  • Anchored zoom: the camera scales about the focus point, so that point keeps its exact screen
///    position while it grows; it never slides toward the centre. The zoomed view is always a
///    sub-rectangle of the home view.
///  • Centred zoom (HoldCentered): the camera also pans so the focus point ends up in the middle of
///    the screen — for the collected record's exit, which plays big and centred like the original
///    full-screen record. Pan and zoom share one progress value, so releasing it pans and zooms home
///    together.
///  • Spotlight: as the camera zooms in, a dimming layer fades in over everything else (world and
///    HUD); the focus object (its renderers, via a SortingGroup) and the note tethers
///    (sorting layer Foreground, order ≥ 10) stay above it. Zooming out fades it away again.
///
/// Callers push a focus and pop it when done; while any focus is held the camera eases to the most
/// recent one, otherwise it eases home. Purely visual: gameplay reads GameplayFrame, which reports
/// the home framing whatever the camera is doing. Lives on the main camera; GetOrCreate adds it on
/// first use and captures home then (the camera never moves otherwise in GeneratedTrack).
/// </summary>
[DefaultExecutionOrder(32000)] // after every other LateUpdate
[DisallowMultipleComponent]
public sealed class CameraZoomDirector : MonoBehaviour
{
    // Only ever added at runtime (GetOrCreate), so these were never Inspector-tunable — consts.
    // Seconds to ease most of the way to a new framing (SmoothDamp time).
    private const float smoothTime  = 0.45f;
    // Strongest dim (1 = everything but the focus and the tethers fully hidden).
    private const float dimStrength = 1f;
    // Fraction of the zoom-in at which the dim is at full strength.
    private const float dimFullAt   = 0.6f;
    // Colour everything but the focus fades to while zoomed.
    private static readonly Color dimColor = Color.black;

    // Dim sits in Foreground above the HUD canvas (order 0); the focus sits just above it, and the
    // note tethers (NoteTether: Foreground 10 and 40) stay above both.
    private const string kSpotLayer      = "Foreground";
    private const int    kDimOrder       = 6;
    private const int    kSpotlightOrder = 8;

    /// <summary>
    /// Keeps UI readable during a spotlight. The zoom's dim covers camera-space canvases (whatever
    /// their sorting), but never Screen Space - Overlay ones. Moves <paramref name="rt"/> (a
    /// full-screen-relative child of a camera-space canvas) into a runtime overlay canvas that
    /// mirrors that canvas's scaler, so its authored anchors, size and position are unchanged.
    /// No-op if it is already in an overlay canvas. Returns true if it moved.
    /// </summary>
    public static bool MoveToOverlay(RectTransform rt)
    {
        if (rt == null) return false;
        var src  = rt.parent != null ? rt.parent.GetComponentInParent<Canvas>(true) : null;
        var root = src != null ? src.rootCanvas : null;
        if (root == null || root.renderMode == RenderMode.ScreenSpaceOverlay) return false;

        var overlay = GetOverlayFor(root);
        rt.SetParent(overlay.transform, false);
        // A nested override-sorting canvas (added for world-space ring meshes) would now sort among
        // the overlay canvases themselves, above menus; the overlay root already draws over the world.
        if (rt.TryGetComponent<Canvas>(out var nested)) nested.overrideSorting = false;
        return true;
    }

    private static readonly System.Collections.Generic.Dictionary<Canvas, Canvas> s_overlays =
        new System.Collections.Generic.Dictionary<Canvas, Canvas>();

    private static Canvas GetOverlayFor(Canvas root)
    {
        if (s_overlays.TryGetValue(root, out var existing) && existing != null) return existing;
        PruneDeadOverlays(); // scene changes destroy canvases; drop their stale keys before adding


        var go = new GameObject($"{root.name} (Zoom-Safe Overlay)", typeof(RectTransform), typeof(Canvas),
                                typeof(UnityEngine.UI.CanvasScaler));
        if (root.gameObject.scene.IsValid())
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, root.gameObject.scene);
        var cv = go.GetComponent<Canvas>();
        cv.renderMode   = RenderMode.ScreenSpaceOverlay;
        cv.sortingOrder = 100;

        var from = root.GetComponent<UnityEngine.UI.CanvasScaler>();
        var to   = go.GetComponent<UnityEngine.UI.CanvasScaler>();
        if (from != null)
        {
            to.uiScaleMode            = from.uiScaleMode;
            to.referenceResolution    = from.referenceResolution;
            to.screenMatchMode        = from.screenMatchMode;
            to.matchWidthOrHeight     = from.matchWidthOrHeight;
            to.scaleFactor            = from.scaleFactor;
            to.referencePixelsPerUnit = from.referencePixelsPerUnit;
            to.physicalUnit           = from.physicalUnit;
            to.fallbackScreenDPI      = from.fallbackScreenDPI;
            to.defaultSpriteDPI       = from.defaultSpriteDPI;
        }
        s_overlays[root] = cv;
        return cv;
    }

    private static readonly List<Canvas> s_deadOverlayKeys = new List<Canvas>();
    private static void PruneDeadOverlays()
    {
        foreach (var kv in s_overlays)
            if (kv.Key == null || kv.Value == null) s_deadOverlayKeys.Add(kv.Key);
        for (int i = 0; i < s_deadOverlayKeys.Count; i++) s_overlays.Remove(s_deadOverlayKeys[i]);
        s_deadOverlayKeys.Clear();
    }

    private Camera  _cam;
    private bool    _hasHome;
    private Vector3 _homePos;
    private float   _homeHalfH;

    private struct Focus { public int Id; public Vector2 Anchor; public float Scale; public GameObject Spotlight; public bool Centered; }
    private readonly List<Focus> _focus = new List<Focus>(4);
    private int _nextId = 1;

    private float          _scale = 1f;        // current view size as a fraction of home
    private float          _scaleVel;
    private Vector2        _anchor;
    private bool           _centered;          // the current anchor pans to screen centre (HoldCentered)
    private float          _deepestScale = 1f; // the latest focus's scale; the dim is measured against it
    private GameObject     _spotlight;
    private SortingGroup   _spotGroup;
    private bool           _spotGroupAdded;
    private SpriteRenderer _dim;

    public bool    HasHome        => _hasHome;
    public Vector3 HomePosition   => _homePos;
    public float   HomeHalfHeight => _homeHalfH;

    private float _targetScale = 1f;

    /// <summary>True once the camera has arrived at its current target framing (zoomed in or home).</summary>
    public bool IsSettled => Mathf.Abs(_scale - _targetScale) < 0.01f && Mathf.Abs(_scaleVel) < 0.05f;

    /// <summary>True while any focus is held.</summary>
    public bool IsFocused => _focus.Count > 0;

    /// <summary>True while the main camera is anywhere but its home framing.</summary>
    public static bool CameraAway { get; private set; }

    /// <summary>True while a focus is held or the camera is still away (zooming in, holding, or easing home).</summary>
    public static bool ZoomActive { get; private set; }

    /// <summary>The live director (on the main camera), or null if none has been created. Cheap: no
    /// Camera.main / GetComponent lookup, so it's safe to poll every frame.</summary>
    public static CameraZoomDirector Current { get; private set; }

    private void Awake() { Current = this; }

    private void OnDestroy() { if (Current == this) Current = null; }

    /// <summary>Main camera's director, created if missing. Null without a main camera.</summary>
    public static CameraZoomDirector GetOrCreate()
    {
        var cam = Camera.main;
        if (cam == null || !cam.orthographic) return null;
        var d = cam.GetComponent<CameraZoomDirector>();
        if (d == null) d = cam.gameObject.AddComponent<CameraZoomDirector>();
        d.EnsureHome();
        return d;
    }

    private void EnsureHome()
    {
        if (_hasHome) return;
        _cam = GetComponent<Camera>();
        if (_cam == null) return;
        _homePos   = _cam.transform.position;
        _homeHalfH = _cam.orthographicSize;
        _anchor    = _homePos;
        _hasHome   = true;
    }

    /// <summary>
    /// Zoom about <paramref name="anchor"/> until about <paramref name="halfHeight"/> world units show
    /// above and below it, widening if needed so <paramref name="mustShow"/> stays fully in view.
    /// <paramref name="spotlight"/> (optional) stays visible above the dim. Returns an id for <see cref="Release"/>.
    /// </summary>
    public int Hold(Vector2 anchor, float halfHeight, Bounds? mustShow = null, GameObject spotlight = null)
    {
        EnsureHome();
        int id = _nextId++;
        float s = _homeHalfH > 0f ? Mathf.Clamp(halfHeight / _homeHalfH, 0.05f, 1f) : 1f;
        if (mustShow.HasValue) s = Mathf.Max(s, ScaleToContain(anchor, mustShow.Value));
        _focus.Add(new Focus { Id = id, Anchor = anchor, Scale = Mathf.Min(1f, s), Spotlight = spotlight });
        ZoomActive = true;
        return id;
    }

    /// <summary>
    /// Zoom in on <paramref name="center"/> and pan it to the middle of the screen, until about
    /// <paramref name="halfHeight"/> world units show above and below it. Releasing it pans and zooms
    /// home together. Returns an id for <see cref="Release"/>.
    /// </summary>
    public int HoldCentered(Vector2 center, float halfHeight, GameObject spotlight = null)
    {
        EnsureHome();
        int id = _nextId++;
        float s = _homeHalfH > 0f ? Mathf.Clamp(halfHeight / _homeHalfH, 0.05f, 1f) : 1f;
        _focus.Add(new Focus { Id = id, Anchor = center, Scale = s, Spotlight = spotlight, Centered = true });
        ZoomActive = true;
        return id;
    }

    public void Release(int id)
    {
        for (int i = _focus.Count - 1; i >= 0; i--)
            if (_focus[i].Id == id) { _focus.RemoveAt(i); return; }
    }

    /// <summary>Drops every focus (bridge, game over, new session); the camera eases home.</summary>
    public void ReleaseAll() => _focus.Clear();

    // Smallest scale whose anchored view (the anchor keeps its screen spot) still contains `b`.
    private float ScaleToContain(Vector2 anchor, Bounds b)
    {
        float halfW  = _homeHalfH * (_cam != null ? _cam.aspect : 16f / 9f);
        float left   = _homePos.x - halfW, right = _homePos.x + halfW;
        float bottom = _homePos.y - _homeHalfH, top = _homePos.y + _homeHalfH;
        float s = 0f;
        if (anchor.x - left   > 1e-4f) s = Mathf.Max(s, (anchor.x - b.min.x) / (anchor.x - left));
        if (right - anchor.x  > 1e-4f) s = Mathf.Max(s, (b.max.x - anchor.x) / (right - anchor.x));
        if (anchor.y - bottom > 1e-4f) s = Mathf.Max(s, (anchor.y - b.min.y) / (anchor.y - bottom));
        if (top - anchor.y    > 1e-4f) s = Mathf.Max(s, (b.max.y - anchor.y) / (top - anchor.y));
        return s;
    }

    private void LateUpdate()
    {
        if (!_hasHome || _cam == null) return;

        float target = 1f;
        if (_focus.Count > 0)
        {
            var f = _focus[_focus.Count - 1];
            target        = f.Scale;
            _deepestScale = f.Scale;
            // Switch anchor (and spotlight) only from home, so the pinned point never jumps.
            if (_scale > 0.995f || _spotlight == null)
            {
                _anchor   = f.Anchor;
                _centered = f.Centered;
                SetSpotlight(f.Spotlight);
            }
        }

        _targetScale = target;
        _scale = Mathf.SmoothDamp(_scale, target, ref _scaleVel, smoothTime);
        if (_focus.Count == 0 && _scale > 0.9995f) { _scale = 1f; _scaleVel = 0f; }

        float span = 1f - _deepestScale;
        float k    = span > 0.001f ? Mathf.Clamp01((1f - _scale) / span) : 0f;

        var a = new Vector3(_anchor.x, _anchor.y, _homePos.z);
        if (_centered)
            // Centred: pan toward the anchor in step with the zoom (k: 0 at home, 1 fully in).
            _cam.transform.position = Vector3.Lerp(_homePos, a, Mathf.SmoothStep(0f, 1f, k));
        else
            // Anchored: view = anchor + (home − anchor)·s, so the anchor's screen position never changes.
            _cam.transform.position = a + (_homePos - a) * _scale;
        _cam.orthographicSize   = _homeHalfH * _scale;
        CameraAway = _scale < 0.9995f;
        ZoomActive = CameraAway || _focus.Count > 0;
        ApplyDim(dimStrength * Mathf.SmoothStep(0f, 1f, k / Mathf.Max(0.1f, dimFullAt)));
    }

    private void ApplyDim(float alpha)
    {
        bool on = alpha > 0.001f;
        if (on) EnsureDim();
        if (_dim != null)
        {
            _dim.enabled = on;
            if (on)
            {
                var c = dimColor; c.a = alpha;
                _dim.color = c;
                float h = _cam.orthographicSize * 2f * 1.05f;
                _dim.transform.localPosition = new Vector3(0f, 0f, _cam.nearClipPlane + 0.05f);
                _dim.transform.localRotation = Quaternion.identity;
                _dim.transform.localScale    = new Vector3(h * _cam.aspect, h, 1f);
            }
        }
        if (_spotGroup != null) _spotGroup.enabled = on;
        if (!on && _focus.Count == 0) SetSpotlight(null);
    }

    private void EnsureDim()
    {
        if (_dim != null) return;
        var go = new GameObject("ZoomDim");
        go.transform.SetParent(transform, false);
        _dim = go.AddComponent<SpriteRenderer>();
        var tex = Texture2D.whiteTexture;
        _dim.sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), tex.width);
        _dim.sortingLayerName = kSpotLayer;
        _dim.sortingOrder     = kDimOrder;
        _dim.enabled = false;
    }

    private void SetSpotlight(GameObject go)
    {
        if (_spotlight == go) return;
        if (_spotGroup != null)
        {
            if (_spotGroupAdded) Destroy(_spotGroup); else _spotGroup.enabled = false;
        }
        _spotlight = go;
        _spotGroup = null;
        _spotGroupAdded = false;
        if (go == null) return;
        _spotGroup = go.GetComponent<SortingGroup>();
        if (_spotGroup == null) { _spotGroup = go.AddComponent<SortingGroup>(); _spotGroupAdded = true; }
        _spotGroup.sortingLayerName = kSpotLayer;
        _spotGroup.sortingOrder     = kSpotlightOrder;
        _spotGroup.enabled          = false; // ApplyDim turns it on with the dim
    }

    private void OnDisable()
    {
        CameraAway = false;
        ZoomActive = false;
        if (_dim != null) _dim.enabled = false;
        if (_spotGroup != null) _spotGroup.enabled = false;
    }
}
