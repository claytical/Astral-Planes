using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Cosmetic tutorial callout: a light translucent disc with a stroked edge that expands from a
/// point to enclose its target, holds, then fades out. Follows the target while alive and
/// destroys itself if the target is destroyed. Never gates progression.
/// </summary>
public class TutorialHighlightRing : MonoBehaviour
{
    static Sprite _sprite;
    static readonly List<TutorialHighlightRing> Active = new List<TutorialHighlightRing>();

    Transform _target;
    SpriteRenderer _sr;
    Color _color;
    float _expand, _hold, _fade, _t;

    public static void Spawn(Transform target, float expandSeconds, float holdSeconds, float fadeSeconds, Color color)
    {
        if (target == null) return;

        // One ring per target: a re-pulse replaces the old one.
        for (int i = Active.Count - 1; i >= 0; i--)
        {
            if (Active[i] == null) { Active.RemoveAt(i); continue; }
            if (Active[i]._target == target) { Destroy(Active[i].gameObject); Active.RemoveAt(i); }
        }

        var go = new GameObject("TutorialHighlightRing");
        var ring = go.AddComponent<TutorialHighlightRing>();
        ring._target = target;
        ring._color = color;
        ring._expand = Mathf.Max(0.01f, expandSeconds);
        ring._hold = Mathf.Max(0f, holdSeconds);
        ring._fade = Mathf.Max(0.01f, fadeSeconds);
        ring._sr = go.AddComponent<SpriteRenderer>();
        ring._sr.sprite = GetSprite();
        ring._sr.sortingOrder = 500;
        ring._sr.color = new Color(color.r, color.g, color.b, 0f);
        go.transform.position = ring.TargetCenter(out float r);
        go.transform.localScale = Vector3.one * 0.1f;
        Active.Add(ring);
    }

    public static void CancelAll()
    {
        foreach (var r in Active) if (r != null) Destroy(r.gameObject);
        Active.Clear();
    }

    void OnDestroy() => Active.Remove(this);

    void Update()
    {
        if (_target == null) { Destroy(gameObject); return; }

        _t += Time.unscaledDeltaTime;
        Vector3 center = TargetCenter(out float radius);
        center.z = _target.position.z;
        transform.position = center;

        float expandT = Mathf.Clamp01(_t / _expand);
        float eased = 1f - Mathf.Pow(1f - expandT, 3f);           // ease-out
        float diameter = Mathf.Lerp(0.1f, radius * 2f, eased);
        transform.localScale = new Vector3(diameter, diameter, 1f);

        float alpha = 1f;
        float afterHold = _t - _expand - _hold;
        if (afterHold > 0f) alpha = 1f - Mathf.Clamp01(afterHold / _fade);
        else alpha = Mathf.Lerp(0f, 1f, Mathf.Clamp01(expandT * 3f));  // quick fade-in while growing
        _sr.color = new Color(_color.r, _color.g, _color.b, _color.a * alpha);

        if (afterHold >= _fade) Destroy(gameObject);
    }

    // World-space centre and enclosing radius of the target (UI marker, sprites, or collider).
    Vector3 TargetCenter(out float radius)
    {
        const float minRadius = 0.4f;
        Bounds b;
        if (_target is RectTransform rt)
        {
            var c = new Vector3[4];
            rt.GetWorldCorners(c);
            b = new Bounds(c[0], Vector3.zero);
            for (int i = 1; i < 4; i++) b.Encapsulate(c[i]);
        }
        else
        {
            var srs = _target.GetComponentsInChildren<SpriteRenderer>();
            if (srs.Length > 0)
            {
                b = srs[0].bounds;
                for (int i = 1; i < srs.Length; i++) b.Encapsulate(srs[i].bounds);
            }
            else if (_target.TryGetComponent<Collider2D>(out var col)) b = col.bounds;
            else b = new Bounds(_target.position, Vector3.one * minRadius);
        }
        radius = Mathf.Max(minRadius, Mathf.Max(b.extents.x, b.extents.y) * 1.35f);
        return b.center;
    }

    // Procedural soft disc with a crisp stroke at its edge: no asset dependency.
    static Sprite GetSprite()
    {
        if (_sprite != null) return _sprite;
        const int N = 256;
        var tex = new Texture2D(N, N, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        var px = new Color32[N * N];
        float c = (N - 1) * 0.5f;
        for (int y = 0; y < N; y++)
        for (int x = 0; x < N; x++)
        {
            float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / c; // 0 centre .. 1 edge
            float fill = d < 1f ? Mathf.Lerp(0.10f, 0.22f, d) : 0f;              // light, slightly denser at edge
            float stroke = Mathf.Clamp01(1f - Mathf.Abs(d - 0.94f) / 0.035f);     // ~3.5% stroke band
            float edge = Mathf.Clamp01((1f - d) / 0.02f);                         // antialias outer edge
            float a = Mathf.Max(fill, stroke) * edge;
            px[y * N + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(a * 255f));
        }
        tex.SetPixels32(px);
        tex.Apply();
        _sprite = Sprite.Create(tex, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f), N); // 1 unit across
        return _sprite;
    }
}
