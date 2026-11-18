using UnityEngine;

/// <summary>
/// Manual-release cue (LEGIBILITY_AUDIT.md C3). While a ship carries a note in Manual mode:
///  - window not open yet → a thin outline ring in the track color contracts from wide toward the
///    hull, brightening as the window approaches ("don't press yet / get ready").
///  - window open → the outline becomes a filled, low-alpha disc that pulses ("press / hold now").
///    It stays up across a held-button cascade while each next note's window is open, so a run of
///    quick notes reads as one continuous "hold" window.
///
/// All tuning (radii, opacity, pulse, thickness, sorting) lives on VehicleConfig under
/// "Release Cue" and is read every frame, so Inspector tweaks show live in Play mode.
/// Driven every frame by Vehicle.TickReleaseApproachCue, which also assigns <see cref="config"/>.
/// Self-contained: Vehicle adds this component at runtime if the prefab lacks it, and the
/// ring/disc sprites are generated procedurally.
/// </summary>
[DisallowMultipleComponent]
public class VehicleReleaseCue : MonoBehaviour
{
    // Fallbacks used only if no VehicleConfig is assigned (mirror VehicleConfig defaults).
    private const float kDefRingStartRadiusMul = 2.5f;
    private const float kDefRingEndRadiusMul = 1.15f;
    private const float kDefRingAlphaStart = 0.10f;
    private const float kDefRingAlphaEnd = 0.75f;
    private const float kDefRingThickness01 = 0.07f;
    private const float kDefDiscRadiusMul = 1.15f;
    private const float kDefDiscAlphaMin = 0.05f;
    private const float kDefDiscAlphaMax = 0.15f;
    private const float kDefDiscPulseHz = 3f;
    private const int kDefSortingOffset = 1;

    /// <summary>Shared tuning source (Vehicle.vehicleConfig). Assigned by Vehicle each frame.</summary>
    [System.NonSerialized] public VehicleConfig config;

    /// <summary>World point the ring is centered on (Vehicle.NoteCoreAnchor); falls back to this transform.</summary>
    [System.NonSerialized] public Transform centerAnchor;

    private static Sprite s_ringSprite;
    private static float s_ringSpriteThickness = -1f;
    private static Sprite s_discSprite;

    private SpriteRenderer _ring;
    private bool _open;
    private float _openStartTime;
    private float _approach01;
    private Color _color = Color.white;
    private float _hullRadiusWorld = 0.35f;

    /// <summary>
    /// approach01: 0 = hidden, (0..1] = window approaching (1 = opening now).
    /// hullRadiusWorld: radius of the ship's own sprite — the ring is sized relative to it.
    /// </summary>
    public void SetApproach(Color trackColor, float approach01, float hullRadiusWorld)
    {
        _color = trackColor;
        _approach01 = Mathf.Clamp01(approach01);
        _open = false;
        if (hullRadiusWorld > 0.001f) _hullRadiusWorld = hullRadiusWorld;
    }

    /// <summary>Release window is open: show the filled pulsing disc.</summary>
    public void SetOpen(Color trackColor, float hullRadiusWorld)
    {
        if (!_open) _openStartTime = Time.time; // each open starts the pulse at a known phase
        _open = true;
        _color = trackColor;
        _approach01 = 0f;
        if (hullRadiusWorld > 0.001f) _hullRadiusWorld = hullRadiusWorld;
    }

    public void Hide()
    {
        _approach01 = 0f;
        _open = false;
    }

    private void LateUpdate()
    {
        if (!_open && _approach01 <= 0.001f)
        {
            if (_ring != null && _ring.enabled) _ring.enabled = false;
            return;
        }

        EnsureRing();
        _ring.enabled = true;

        var cfg = config;
        float radius;
        Color c = _color;
        if (_open)
        {
            _ring.sprite = GetDiscSprite();
            radius = _hullRadiusWorld * (cfg != null ? cfg.releaseDiscRadiusMul : kDefDiscRadiusMul);
            float hz = cfg != null ? cfg.releaseDiscPulseHz : kDefDiscPulseHz;
            float aMin = cfg != null ? cfg.releaseDiscAlphaMin : kDefDiscAlphaMin;
            float aMax = cfg != null ? cfg.releaseDiscAlphaMax : kDefDiscAlphaMax;
            // Start at the dim end, then pulse.
            float t = Time.time - _openStartTime;
            float pulse = 0.5f - 0.5f * Mathf.Cos(t * 2f * Mathf.PI * Mathf.Max(0.01f, hz));
            c.a = Mathf.Lerp(aMin, aMax, pulse);
        }
        else
        {
            _ring.sprite = GetRingSprite(cfg != null ? cfg.releaseCueRingThickness01 : kDefRingThickness01);
            float rStart = cfg != null ? cfg.releaseRingStartRadiusMul : kDefRingStartRadiusMul;
            float rEnd = cfg != null ? cfg.releaseRingEndRadiusMul : kDefRingEndRadiusMul;
            float aStart = cfg != null ? cfg.releaseRingAlphaStart : kDefRingAlphaStart;
            float aEnd = cfg != null ? cfg.releaseRingAlphaEnd : kDefRingAlphaEnd;
            // Ease-in so the ring visibly accelerates into the hull right before the window.
            float u = _approach01 * _approach01;
            radius = _hullRadiusWorld * Mathf.Lerp(rStart, rEnd, u);
            c.a = Mathf.Lerp(aStart, aEnd, _approach01);
        }
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
        _ring.sprite = GetRingSprite(config != null ? config.releaseCueRingThickness01 : kDefRingThickness01);

        var hull = GetComponent<SpriteRenderer>();
        if (hull != null)
        {
            _ring.sortingLayerID = hull.sortingLayerID;
            _ring.sortingOrder = hull.sortingOrder + (config != null ? config.releaseCueSortingOffset : kDefSortingOffset);
        }
    }

    // Procedural thin annulus, 1 world unit across at scale 1. Rebuilt only when thickness changes.
    private static Sprite GetRingSprite(float thickness01)
    {
        thickness01 = Mathf.Clamp(thickness01, 0.01f, 0.5f);
        if (s_ringSprite != null && Mathf.Abs(s_ringSpriteThickness - thickness01) < 0.0005f) return s_ringSprite;
        if (s_ringSprite != null)
        {
            var oldTex = s_ringSprite.texture;
            Destroy(s_ringSprite);
            if (oldTex != null) Destroy(oldTex);
        }
        const int n = 128;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        float r = n * 0.5f;
        float inner = 1f - thickness01;
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
        s_ringSpriteThickness = thickness01;
        return s_ringSprite;
    }

    // Procedural filled disc with a soft edge, 1 world unit across at scale 1.
    private static Sprite GetDiscSprite()
    {
        if (s_discSprite != null) return s_discSprite;
        const int n = 128;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        float r = n * 0.5f;
        const float soft = 0.025f;
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(r, r)) / r;
            tex.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01((1f - d) / soft)));
        }
        tex.Apply();
        s_discSprite = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), n);
        return s_discSprite;
    }
}
