using UnityEngine;

/// <summary>
/// Manual-release approach ring (LEGIBILITY_AUDIT.md C3). While a ship carries a note in Manual
/// mode and the next release window hasn't opened yet, a thin ring in the note's track color
/// contracts from wide toward the hull, getting brighter as the window approaches. The moment the
/// window opens the ring vanishes and the release tether (NoteTether, shown only while the window
/// is open) takes over — so: ring closing in = "get ready", tether = "now".
///
/// Driven every frame by Vehicle.TickReleaseApproachCue. Self-contained: Vehicle adds this
/// component at runtime if the prefab lacks it, and the ring sprite is generated procedurally.
/// (Replaces the original growing-circle + step-bar cue, inert since its setters were removed.)
/// </summary>
[DisallowMultipleComponent]
public class VehicleReleaseCue : MonoBehaviour
{
    [Header("Approach Ring")]
    [Tooltip("Ring radius when it first appears, as a multiple of the ship sprite's radius.")]
    [SerializeField] private float ringStartRadiusMul = 2.5f;
    [Tooltip("Ring radius as the window opens, as a multiple of the ship sprite's radius (just outside the hull).")]
    [SerializeField] private float ringEndRadiusMul = 1.15f;
    [SerializeField, Range(0f, 1f)] private float ringAlphaStart = 0.15f;
    [SerializeField, Range(0f, 1f)] private float ringAlphaEnd = 0.85f;
    [Tooltip("Sorting order relative to the vehicle sprite (negative = behind the hull).")]
    [SerializeField] private int sortingOrderOffset = 1;

    private const float kRingThickness01 = 0.07f; // ring band as a fraction of the sprite radius

    private static Sprite s_ringSprite;

    private SpriteRenderer _ring;
    private float _approach01;
    private Color _color = Color.white;
    private float _hullRadiusWorld = 0.35f;

    /// <summary>World point the ring is centered on (Vehicle.NoteCoreAnchor); falls back to this transform.</summary>
    public Transform centerAnchor;

    /// <summary>
    /// approach01: 0 = hidden, (0..1] = window approaching (1 = opening now).
    /// hullRadiusWorld: radius of the ship's own sprite — the ring is sized relative to it.
    /// </summary>
    public void SetApproach(Color trackColor, float approach01, float hullRadiusWorld)
    {
        _color = trackColor;
        _approach01 = Mathf.Clamp01(approach01);
        if (hullRadiusWorld > 0.001f) _hullRadiusWorld = hullRadiusWorld;
    }

    public void Hide() => _approach01 = 0f;

    private void LateUpdate()
    {
        if (_approach01 <= 0.001f)
        {
            if (_ring != null && _ring.enabled) _ring.enabled = false;
            return;
        }

        EnsureRing();
        _ring.enabled = true;

        // Ease-in so the ring visibly accelerates into the hull right before the window.
        float u = _approach01 * _approach01;
        float radius = _hullRadiusWorld * Mathf.Lerp(ringStartRadiusMul, ringEndRadiusMul, u);
        Color c = _color;
        c.a = Mathf.Lerp(ringAlphaStart, ringAlphaEnd, _approach01);
        _ring.color = c;

        Vector3 ls = transform.lossyScale;
        float d = radius * 2f;
        _ring.transform.localScale = new Vector3(
            Mathf.Abs(ls.x) > 0.0001f ? d / ls.x : d,
            Mathf.Abs(ls.y) > 0.0001f ? d / ls.y : d, 1f);
        _ring.transform.rotation = Quaternion.identity; // stays round/upright as the ship turns
        _ring.transform.position = centerAnchor != null ? centerAnchor.position : transform.position;
    }

    private void EnsureRing()
    {
        if (_ring != null) return;
        var go = new GameObject("Release Approach Ring");
        go.transform.SetParent(transform, false);
        _ring = go.AddComponent<SpriteRenderer>();
        _ring.sprite = GetRingSprite();

        var hull = GetComponent<SpriteRenderer>();
        if (hull != null)
        {
            _ring.sortingLayerID = hull.sortingLayerID;
            _ring.sortingOrder = hull.sortingOrder + sortingOrderOffset;
        }
    }

    // Procedural thin annulus, 1 world unit across at scale 1.
    private static Sprite GetRingSprite()
    {
        if (s_ringSprite != null) return s_ringSprite;
        const int n = 128;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        float r = n * 0.5f;
        float inner = 1f - kRingThickness01;
        const float soft = 0.025f;
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(r, r)) / r;
            float outerA = Mathf.Clamp01((1f - d) / soft);
            float innerA = Mathf.Clamp01((d - inner) / soft);
            tex.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Min(outerA, innerA)));
        }
        tex.Apply();
        s_ringSprite = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), n);
        return s_ringSprite;
    }
}
