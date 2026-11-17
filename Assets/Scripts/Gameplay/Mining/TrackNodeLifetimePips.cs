using UnityEngine;

// Node expiry countdown (LEGIBILITY_AUDIT.md B3): concentric translucent discs behind a
// TrackNode, one per loop it has left before it expires. The innermost disc is the most
// opaque, each disc further out is fainter, and the outermost is nearly translucent — the
// stack reads as a soft halo whose depth is the node's remaining lifetime.
//
//   Loop boundary → the outermost disc swells slightly and fades away (halo contracts).
//   Vehicle hit   → the counter resets, lost discs re-emanate outward from the center.
//   Last loop     → the single remaining disc breathes as a warning.
//
// Discs sit on a child with scale compensation so the halo keeps a constant world size while
// the node shrinks with damage. Reads TrackNode.LoopsRemaining every frame; owns no gameplay
// state. (Class name kept from the first pip-ring version so the Attach call site is unchanged.)
public sealed class TrackNodeLifetimePips : MonoBehaviour
{
    private const float kInnerRadiusMul   = 1.25f;  // innermost disc radius × node body radius
    private const float kRingStepMul      = 0.45f;  // each further disc adds this × body radius
    private const float kMinBodyRadius    = 0.3f;
    private const float kOuterAlpha       = 0.06f;  // outermost disc, always nearly translucent
    private const float kInnerAlphaMin    = 0.18f;  // innermost disc with 1 loop left …
    private const float kInnerAlphaMax    = 0.5f;   // … and with its full lifetime left
    private const float kFadedRadiusMul   = 1.15f;  // a disc fading out swells this much
    private const float kSettleSpeed      = 6f;     // per-second lerp toward targets
    private const float kBreathHz         = 0.35f;
    private const float kBreathAmount     = 0.03f;
    private const float kLastLoopBreath   = 0.12f;
    private const float kRefillFlashSecs  = 0.35f;

    private static Sprite s_discSprite;

    private TrackNode _node;
    private Color _color = Color.white;
    private Transform _halo;
    private SpriteRenderer[] _discs;
    private float[] _radius;     // current displayed radius (world)
    private float[] _alpha;      // current displayed alpha
    private float _bodyRadius;
    private int _total;
    private int _lastRemaining = -1;
    private float _refillStart = -999f;

    public static TrackNodeLifetimePips Attach(TrackNode node, Color color)
    {
        if (node == null || node.ExpireAfterLoops <= 0) return null;
        var halo = node.GetComponent<TrackNodeLifetimePips>();
        if (halo == null) halo = node.gameObject.AddComponent<TrackNodeLifetimePips>();
        halo.Build(node, color);
        return halo;
    }

    private void Build(TrackNode node, Color color)
    {
        _node = node;
        _color = color;
        _total = Mathf.Max(1, node.ExpireAfterLoops);
        _lastRemaining = node.LoopsRemaining;

        if (_halo != null) Destroy(_halo.gameObject);
        _halo = new GameObject("Lifetime Halo").transform;
        _halo.SetParent(transform, false);

        _bodyRadius = kMinBodyRadius;
        var col = GetComponent<Collider2D>();
        if (col != null) _bodyRadius = Mathf.Max(_bodyRadius, Mathf.Max(col.bounds.extents.x, col.bounds.extents.y));

        // Behind the node: one below the lowest sorting order among its own sprites.
        int layer = 0, order = 0;
        bool any = false;
        foreach (var sr in GetComponentsInChildren<SpriteRenderer>(true))
        {
            if (!any || sr.sortingOrder < order) { order = sr.sortingOrder; layer = sr.sortingLayerID; }
            any = true;
        }
        order -= 1;

        _discs = new SpriteRenderer[_total];
        _radius = new float[_total];
        _alpha = new float[_total];
        for (int i = 0; i < _total; i++)
        {
            var go = new GameObject($"Halo {i}");
            go.transform.SetParent(_halo, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = GetDiscSprite();
            sr.sortingLayerID = layer;
            // Same order for all discs: they share one color, so alpha-over stacking is
            // order-independent and the overlap naturally builds opacity toward the center.
            sr.sortingOrder = order;
            _discs[i] = sr;
            _radius[i] = _bodyRadius;   // emanate out from the body on spawn
            _alpha[i] = 0f;
        }
        ApplyCompensation();
        Tick(0f);
    }

    private void LateUpdate()
    {
        if (_node == null || _discs == null) return;

        int remaining = _node.LoopsRemaining;
        if (remaining > _lastRemaining)
        {
            // Hit reset the counter: discs that were gone re-emanate from the body outward.
            for (int i = _lastRemaining; i < remaining && i < _total; i++)
                if (i >= 0 && _alpha[i] < 0.01f) _radius[i] = _bodyRadius;
            _refillStart = Time.time;
        }
        _lastRemaining = remaining;

        ApplyCompensation();
        Tick(Time.deltaTime);
    }

    private float TargetRadius(int i) => _bodyRadius * (kInnerRadiusMul + kRingStepMul * i);

    private void Tick(float dt)
    {
        int k = Mathf.Clamp(_lastRemaining, 0, _total);
        float fullness01 = _total > 1 ? (k - 1) / (float)(_total - 1) : 1f;
        float innerAlpha = Mathf.Lerp(kInnerAlphaMin, kInnerAlphaMax, Mathf.Clamp01(fullness01));
        float settle = dt > 0f ? 1f - Mathf.Exp(-kSettleSpeed * dt) : 1f;
        float refill01 = Mathf.Clamp01((Time.time - _refillStart) / kRefillFlashSecs);

        for (int i = 0; i < _total; i++)
        {
            bool live = i < k;
            // Live discs grade from innerAlpha (center) to kOuterAlpha (outermost live disc).
            float t = k > 1 ? i / (float)(k - 1) : 0f;
            float targetA = live ? Mathf.Lerp(innerAlpha, kOuterAlpha, t) : 0f;
            float targetR = live ? TargetRadius(i) : TargetRadius(i) * kFadedRadiusMul;

            _alpha[i] = Mathf.Lerp(_alpha[i], targetA, settle);
            _radius[i] = Mathf.Lerp(_radius[i], targetR, settle);

            // Slow outward-travelling breath (phase lags per disc so it reads as emanating);
            // stronger on the final loop.
            float breathAmt = k == 1 ? kLastLoopBreath : kBreathAmount;
            float breath = 1f + breathAmt * Mathf.Sin((Time.time * kBreathHz - i * 0.18f) * Mathf.PI * 2f);

            Color c = refill01 < 1f ? Color.Lerp(Color.white, _color, refill01) : _color;
            c.a = _alpha[i];
            var sr = _discs[i];
            sr.color = c;
            sr.enabled = _alpha[i] > 0.002f;
            float diameter = 2f * _radius[i] * breath;
            sr.transform.localScale = new Vector3(diameter, diameter, 1f);
        }
    }

    // Keep the halo a constant world size while the node scales with strength.
    private void ApplyCompensation()
    {
        Vector3 s = transform.lossyScale;
        float sx = Mathf.Abs(s.x) > 0.0001f ? 1f / s.x : 1f;
        float sy = Mathf.Abs(s.y) > 0.0001f ? 1f / s.y : 1f;
        _halo.localScale = new Vector3(sx, sy, 1f);
    }

    // Procedural disc with a soft rim, built once and shared (no sprite asset to wire).
    // 1 world unit across at scale 1.
    private static Sprite GetDiscSprite()
    {
        if (s_discSprite != null) return s_discSprite;
        const int n = 128;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        float r = n * 0.5f;
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(r, r)) / r;
            float a = Mathf.Clamp01((1f - d) / 0.08f);
            tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
        }
        tex.Apply();
        s_discSprite = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), n);
        return s_discSprite;
    }
}
