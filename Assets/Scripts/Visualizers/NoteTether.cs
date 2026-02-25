using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(LineRenderer))]
public class NoteTether : MonoBehaviour
{
    [Header("Endpoints")]
    public Transform start; // usually the Collectable transform
    public Transform end;   // the ribbon marker transform
    public InstrumentTrack boundTrack;
    public int boundStep = -1;
    public NoteVisualizer boundVisualizer;
    [Header("Curve")]
    [Range(8,128)] public int segments = 32;
    public float sag = 0.6f;          // how much the curve dips
    public float noiseAmp = 0.15f;    // lateral wobble
    public float noiseSpeed = 1.5f;
    [Header("Shimmer (Particles)")]
    public bool shimmerEnabled = true;
    public float shimmerRatePerUnit = 6f;    // particles per second per unit of curve length
    public float shimmerLifetime = 0.35f;
    public float shimmerSize = 0.03f;
    public float shimmerAlpha = 0.5f;

    private ParticleSystem _shimmerPS;
    private float _curveLength;

    [Header("Style")]
    public float baseWidth = 0.05f;
    public Color baseColor = new Color(1f,1f,1f,0.35f);
    public Color litColor  = Color.white;
    public float fadeAfterSeconds = 0.4f;

    private LineRenderer _lr;
    private Vector3[] _points;
    private CollectableParticles _dripEmitter;
    [SerializeField] private float dripBaseSpeed = 0.6f;
    [SerializeField] private float dripGravity   = 0.0f;
    [SerializeField] private float _endpointNullGrace = 0.6f;  // was 0.1f — allow layout/marker creation
    private float _endpointNullTimer = 0f;
    private float _rebindPollInterval = 0.05f;
    private float _rebindTimer = 0f;
    [SerializeField] private Camera worldCamera;   // optional override; falls back to Camera.main
    [SerializeField] private Camera uiCamera;      // optional override for Screen Space - Camera canvas
    [SerializeField] private float worldZOverride = float.NaN; // if set, force the tether's Z
    private Vector3 _lastEndWorld;

    // --- Arc-length cache (add these fields somewhere in the same class) ---
private float[] _cumDist;   // cumulative distance at each point
private float   _totalLen;  // total curve length
private int     _cachedCount = -1;
private int     _cachedHash  = 0;

private void RebuildArcLengthCacheIfNeeded()
{
    int n = _points != null ? _points.Length : 0;
    if (n < 2)
    {
        _cumDist = null;
        _totalLen = 0f;
        _cachedCount = n;
        _cachedHash = 0;
        return;
    }

    // Lightweight "did the points change?" hash
    // (good enough for runtime; avoids rebuilding every call)
    unchecked
    {
        int h = 17;
        // sample a few points to avoid O(n) hashing every time
        int step = Mathf.Max(1, n / 8);
        for (int k = 0; k < n; k += step)
        {
            var p = _points[k];
            h = h * 31 + p.x.GetHashCode();
            h = h * 31 + p.y.GetHashCode();
            h = h * 31 + p.z.GetHashCode();
        }

        if (_cumDist != null && _cachedCount == n && _cachedHash == h && _totalLen > 0.0001f)
            return; // cache still valid

        _cachedCount = n;
        _cachedHash = h;

        if (_cumDist == null || _cumDist.Length != n)
            _cumDist = new float[n];

        _cumDist[0] = 0f;
        float sum = 0f;

        for (int i = 1; i < n; i++)
        {
            sum += Vector3.Distance(_points[i - 1], _points[i]);
            _cumDist[i] = sum;
        }

        _totalLen = Mathf.Max(0.0001f, sum);
    }
}

public Vector3 EvaluatePosition01(float t)
{
    if (_points == null || _points.Length == 0)
        return (start ? start.position : transform.position);

    if (_points.Length == 1)
        return _points[0];

    // Catmull-Rom through sampled points for smooth velocity/curvature.
    // Treat endpoints as clamped.
    t = Mathf.Clamp01(t);

    float scaled = t * (_points.Length - 1);
    int i1 = Mathf.FloorToInt(scaled);
    float u = scaled - i1;

    int i0 = Mathf.Clamp(i1 - 1, 0, _points.Length - 1);
    int i2 = Mathf.Clamp(i1 + 1, 0, _points.Length - 1);
    int i3 = Mathf.Clamp(i1 + 2, 0, _points.Length - 1);

    Vector3 p0 = _points[i0];
    Vector3 p1 = _points[i1];
    Vector3 p2 = _points[i2];
    Vector3 p3 = _points[i3];

    // Catmull-Rom spline (uniform)
    float u2 = u * u;
    float u3 = u2 * u;

    return 0.5f * (
        (2f * p1) +
        (-p0 + p2) * u +
        (2f * p0 - 5f * p1 + 4f * p2 - p3) * u2 +
        (-p0 + 3f * p1 - 3f * p2 + p3) * u3
    );
}
public float GetCurveLength()
{
    if (_points == null || _points.Length < 2) return 0f;
    RebuildArcLengthCacheIfNeeded();
    return _totalLen;
}
    void Update()
{
    // If either endpoint is missing, try to (re)acquire before destroying
    if (!start || !end)
    {
        // Try to resolve 'end' from the visualizer’s marker table every few frames
        if (!end && boundVisualizer && boundTrack && boundStep >= 0)
        {
            _rebindTimer -= Time.deltaTime;
            if (_rebindTimer <= 0f)
            {
                _rebindTimer = _rebindPollInterval;
                if (boundVisualizer.noteMarkers != null &&
                    boundVisualizer.noteMarkers.TryGetValue((boundTrack, boundStep), out var t) &&
                    t)
                {
                    end = t;
                    // Immediately rebuild once the anchor appears
                    _endpointNullTimer = 0f;
                }
            }
        }

        _endpointNullTimer += Time.deltaTime;
        if (_endpointNullTimer > _endpointNullGrace)
        {
            // If we still have a visualizer + binding info, don’t destroy yet—keep trying.
            // Only self-destruct if we truly have no way to resolve the anchor anymore.
            bool hopeless = (!boundVisualizer || !boundTrack || boundStep < 0);
            if (hopeless)
            {
                Destroy(gameObject);
                return;
            }
            // Otherwise, keep waiting; just skip rendering this frame.
            return;
        }

        // Skip rendering this frame but keep the tether alive while we wait.
        return;
    }

    // endpoints present — render normally
    _endpointNullTimer = 0f;

    RebuildCurve();
    _curveLength = ComputeCurveLength();

    if (_dripEmitter != null)
    {
        Vector3 endW = ResolveEndWorldPosition();
        Vector3 dir  = (endW - start.position).normalized;
        _dripEmitter.SetDripDirection(dir, dripBaseSpeed, dripGravity);
    }

    EmitShimmer(Time.deltaTime);
}
private Vector3 ResolveEndWorldPosition()
{
    if (end == null) return _lastEndWorld;

    // If this is a RectTransform, it *might* be UI — but it also might be World Space canvas.
    RectTransform rt = end as RectTransform ?? end.GetComponent<RectTransform>();
    if (rt != null)
    {
        var canvas = rt.GetComponentInParent<Canvas>();

        // ✅ World Space canvas: RectTransform positions are already world-space.
        if (canvas != null && canvas.renderMode == RenderMode.WorldSpace)
        {
            var wp = rt.position;
            if (!float.IsNaN(worldZOverride)) wp.z = worldZOverride;
            else if (start) wp.z = start.position.z;
            return _lastEndWorld = wp;
        }

        // Screen Space (Overlay/Camera): convert via screen point.
        var uiCam = this.uiCamera != null ? this.uiCamera : (canvas ? canvas.worldCamera : null);
        Vector2 screen = RectTransformUtility.WorldToScreenPoint(uiCam, rt.position);

        var wcam = this.worldCamera != null ? this.worldCamera : Camera.main;
        if (wcam == null) return _lastEndWorld;

        float depth;
        if (wcam.orthographic)
        {
            depth = 0f; // ignored for ortho
        }
        else
        {
            var planeZ = (start ? start.position.z : 0f);
            depth = Mathf.Abs(planeZ - wcam.transform.position.z);
            if (depth < 0.001f) depth = 10f;
        }

        var wp2 = wcam.ScreenToWorldPoint(new Vector3(screen.x, screen.y, depth));
        if (!float.IsNaN(worldZOverride)) wp2.z = worldZOverride;
        else if (start) wp2.z = start.position.z;

        return _lastEndWorld = wp2;
    }

    // Non-UI object: just use its world position
    return _lastEndWorld = end.position;
}
    void Awake()
    {
        _lr = GetComponent<LineRenderer>() ?? gameObject.AddComponent<LineRenderer>();
        _lr.useWorldSpace = true;
        _lr.positionCount = segments;
        _lr.widthMultiplier = baseWidth;
        _lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        _lr.receiveShadows = false;
        _lr.material = _lr.material ? _lr.material : new Material(Shader.Find("Sprites/Default"));

        // neutral gradient; will be replaced in SetEndpoints
        var g = new Gradient();
        g.SetKeys(
            new[] { new GradientColorKey(baseColor, 0f), new GradientColorKey(baseColor, 1f) },
            new[] { new GradientAlphaKey(baseColor.a, 0f), new GradientAlphaKey(baseColor.a, 1f) }
        );
        _lr.colorGradient = g;

        _points = new Vector3[segments];

        // ✅ Make sure we actually have a PS ready
        EnsureShimmer();
    }


    public void BindByStep(InstrumentTrack track, int step, NoteVisualizer viz) {
        boundTrack = track; 
        boundStep  = step;
        boundVisualizer = viz;
    }
    public IEnumerator PulseToEnd(float seconds = 0.45f, System.Action onArrive = null)
    {
        // animate a bright tip running along the gradient
        float t = 0f;
        while (t < seconds)
        {
            t += Time.deltaTime;
            float u = Mathf.Clamp01(t / seconds);
            ApplyHeadGlow(u, 0.15f);
            yield return null;
        }
        onArrive?.Invoke();
        // fade out the whole line
        yield return StartCoroutine(FadeOut(fadeAfterSeconds));
        Destroy(gameObject);
    }
    public void SetEndpoints(Transform a, Transform b, Color c, float widthMul = 1f)
    {
        start = a; end = b; baseColor = c;
        if (_lr)
        {
            // “electric filament” gradient
            var g = new Gradient();
            g.SetKeys(
                new []
                {
                    new GradientColorKey(c * 0.6f, 0f),
                    new GradientColorKey(Color.white, 0.5f),
                    new GradientColorKey(c, 1f)
                },
                new []
                {
                    new GradientAlphaKey(0f, 0f),
                    new GradientAlphaKey(Mathf.Clamp01(c.a * 0.9f), 0.12f),
                    new GradientAlphaKey(Mathf.Clamp01(c.a), 0.5f),
                    new GradientAlphaKey(Mathf.Clamp01(c.a * 0.7f), 0.88f),
                    new GradientAlphaKey(0f, 1f)
                }
            );
            _lr.colorGradient = g;
            _lr.widthMultiplier = baseWidth * widthMul;
        }

        EnsureShimmer();
        if (_shimmerPS != null)
        {
            var main = _shimmerPS.main;
            // colored sparks with occasional white pops
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(c.r, c.g, c.b, shimmerAlpha),
                Color.white
            );
        }
    }

    private void EnsureShimmer()
    {
        if (_shimmerPS != null) return;
        var go = new GameObject("ShimmerPS");
        go.transform.SetParent(transform, worldPositionStays: false);
        _shimmerPS = go.AddComponent<ParticleSystem>();

        var main = _shimmerPS.main;
        main.loop = false;
        main.simulationSpace = ParticleSystemSimulationSpace.World;

        // 🔧 Visibility bump — tweak to taste
        main.startLifetime = Mathf.Max(0.25f, shimmerLifetime);  // e.g., 0.35–0.6
        main.startSize     = Mathf.Max(0.06f, shimmerSize);      // e.g., 0.06–0.12

        // gradient set later in SetEndpoints
        main.maxParticles = 2048;

        var emission = _shimmerPS.emission;
        emission.enabled = false; // we Emit() manually

        var shape = _shimmerPS.shape;
        shape.enabled = false;

        var _renderer = _shimmerPS.GetComponent<ParticleSystemRenderer>();
        _renderer.material = new Material(Shader.Find("Sprites/Default"));
        _renderer.sortingLayerName = "Foreground";
        _renderer.sortingOrder = 40; // 🔧 ensure on top of dust/maze
    }
    private float ComputeCurveLength()
    {
        float len = 0f;
        for (int i = 1; i < _points.Length; i++)
            len += Vector3.Distance(_points[i-1], _points[i]);
        return len;
    }
    private void EmitShimmer(float deltaTime)
    {
        if (!shimmerEnabled || _shimmerPS == null) return;
        float particlesToEmit = _curveLength * shimmerRatePerUnit * Mathf.Max(0.0001f, deltaTime);

        int emits = Mathf.FloorToInt(particlesToEmit);
        for (int i = 0; i < emits; i++)
        {
            // pick a random point along the line
            float t = Random.value;
            int idx = Mathf.Clamp(Mathf.RoundToInt(t * (_points.Length - 1)), 0, _points.Length - 1);
            Vector3 pos = _points[idx];

            var ep = new ParticleSystem.EmitParams
            {
                position = pos + (Vector3)(Random.insideUnitCircle * 0.025f),
                startLifetime = shimmerLifetime * Random.Range(0.8f, 1.2f),
                startSize = shimmerSize * Random.Range(0.8f, 1.3f),
                startColor = new Color(baseColor.r, baseColor.g, baseColor.b, shimmerAlpha * Random.Range(0.6f, 1f))
            };
            _shimmerPS.Emit(ep, 1);
        }
    }
private void RebuildCurve()
{
    Vector3 aW = start.position;
    Vector3 dW = ResolveEndWorldPosition();

    var cam = worldCamera != null ? worldCamera : Camera.main;
    if (cam == null)
    {
        // Fallback: straight line if no camera
        for (int i = 0; i < segments; i++)
        {
            float t = (segments <= 1) ? 1f : i / (float)(segments - 1);
            _points[i] = Vector3.Lerp(aW, dW, t);
        }
        _lr.SetPositions(_points);
        return;
    }

    // Use a stable depth plane for Screen<->World conversion
    float depth = cam.orthographic
        ? 0f
        : Mathf.Abs((aW.z) - cam.transform.position.z);

    Vector3 aS = cam.WorldToScreenPoint(new Vector3(aW.x, aW.y, cam.orthographic ? 0f : aW.z));
    Vector3 dS = cam.WorldToScreenPoint(new Vector3(dW.x, dW.y, cam.orthographic ? 0f : dW.z));
    aS.z = depth;
    dS.z = depth;

    Vector2 delta = (Vector2)(dS - aS);
    float dist = delta.magnitude;
    if (dist < 1f) dist = 1f;

    // Tube bend in screen pixels (clamped so it stays in frame)
    float sagPx = sag * 120f; // sag is in "world feel"; convert to px
    sagPx = Mathf.Clamp(sagPx, 12f, 160f);

    // Compute a normal direction in screen space
    Vector2 dir = delta / dist;
    Vector2 n = new Vector2(-dir.y, dir.x); // perpendicular

    // Choose bend sign that keeps midpoint inside viewport
    float margin = 24f;
    float midY = (aS.y + dS.y) * 0.5f;

    // If bending "up" would exceed top, bend down; if bending "down" exceeds bottom, bend up.
    float upY = midY + sagPx;
    float dnY = midY - sagPx;
    float top = Screen.height - margin;
    float bot = margin;

    float bendSign = 1f;
    if (upY > top && dnY >= bot) bendSign = -1f;
    else if (dnY < bot && upY <= top) bendSign = 1f;
    else
    {
        // default: bend toward screen center
        bendSign = (midY < Screen.height * 0.5f) ? 1f : -1f;
    }

    Vector2 bend = n * (sagPx * bendSign);

    // Control points in screen space:
    // - first control stays near start (tube intake)
    // - second stays near end (tube exhaust)
    float c1Dist = Mathf.Clamp(dist * 0.10f, 18f, 120f);
    float c2Dist = Mathf.Clamp(dist * 0.12f, 22f, 160f);

    Vector2 bS = (Vector2)aS + dir * c1Dist + bend;
    Vector2 cS = (Vector2)dS - dir * c2Dist + bend * 0.35f;

    float time = Time.time * noiseSpeed;

    for (int i = 0; i < segments; i++)
    {
        float u = (segments <= 1) ? 1f : i / (float)(segments - 1);

        // Late-commit feel WITHOUT going offscreen (works in screen space)
        float t = Mathf.Pow(u, 2.2f);

        float it = 1f - t;

        Vector2 pS =
            it * it * it * (Vector2)aS +
            3f * it * it * t * bS +
            3f * it * t * t * cS +
            t * t * t * (Vector2)dS;

        // Small wobble in screen space (converted from your noiseAmp)
        float wob = (Mathf.PerlinNoise(i * 0.17f, time) - 0.5f) * 2f;
        // inside your for-loop, after computing wob:
        float wobTaper = 1f;
        const float wobOffAt = 0.80f;
        if (u > wobOffAt)
            wobTaper = 1f - Mathf.InverseLerp(wobOffAt, 1f, u);

        pS += n * wob * (noiseAmp * 35f) * wobTaper;
        // Clamp to screen bounds so we never go offscreen
        pS.x = Mathf.Clamp(pS.x, margin, Screen.width - margin);
        pS.y = Mathf.Clamp(pS.y, margin, Screen.height - margin);

        Vector3 pW = cam.ScreenToWorldPoint(new Vector3(pS.x, pS.y, depth));

        // Z discipline
        if (!float.IsNaN(worldZOverride)) pW.z = worldZOverride;
        else pW.z = aW.z;

        _points[i] = pW;
    }

    _lr.positionCount = _points.Length;
    _lr.SetPositions(_points);
}
    private void ApplyHeadGlow(float head, float headSpan)
    {
        // Build a gradient that brightens the leading segment [head-headSpan, head]
        Gradient g = new Gradient();
        var cks = new List<GradientColorKey>();
        var aks = new List<GradientAlphaKey>();

        for (int i = 0; i < 6; i++)
        {
            float t = i / 5f;
            Color c = baseColor;
            float a = baseColor.a;
            litColor = Color.Lerp(c, litColor, .5f);
            if (t <= head && t >= Mathf.Max(0, head - headSpan))
            {
                float k = Mathf.InverseLerp(head - headSpan, head, t);
                c = Color.Lerp(baseColor, litColor, k * 0.9f + 0.1f);
                a = Mathf.Lerp(baseColor.a, 1f, Mathf.Max(.25f, k));
            }

            cks.Add(new GradientColorKey(c, t));
            aks.Add(new GradientAlphaKey(a, t));
        }

        g.SetKeys(cks.ToArray(), aks.ToArray());
        _lr.colorGradient = g;
    }
    private void UpdateGradient(Color color, float alpha)
    {
        var g = new Gradient();
        g.SetKeys(
            new []
            {
                new GradientColorKey(color,0f),
                new GradientColorKey(color,1f)
            },
            new []
            {
                new GradientAlphaKey(alpha,0f),
                new GradientAlphaKey(alpha,1f)
            }
        );
        _lr.colorGradient = g;
    }
    private IEnumerator FadeOut(float seconds)
    {
        float t = 0f;
        while (t < seconds)
        {
            t += Time.deltaTime;
            float a = Mathf.Lerp(baseColor.a, 0f, t / seconds);
            UpdateGradient(baseColor, a);
            _lr.widthMultiplier = Mathf.Lerp(baseWidth, 0f, t / seconds);
            yield return null;
        }
    }

}
