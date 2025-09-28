using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(LineRenderer))]
public class NoteTether : MonoBehaviour
{
    [Header("Endpoints")]
    public Transform start; // usually the Collectable transform
    public Transform end;   // the ribbon marker transform

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

    private ParticleSystem shimmerPS;
    private float curveLength;

    [Header("Style")]
    public float baseWidth = 0.05f;
    public Color baseColor = new Color(1f,1f,1f,0.35f);
    public Color litColor  = Color.white;
    public float fadeAfterSeconds = 0.4f;

    private LineRenderer lr;
    private Vector3[] points;

    void Awake()
    {
        lr = GetComponent<LineRenderer>();
        if (!lr) lr = gameObject.AddComponent<LineRenderer>();
        lr.useWorldSpace = true;
        lr.positionCount = segments;
        lr.widthMultiplier = baseWidth;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows = false;
        lr.material = lr.material ? lr.material : new Material(Shader.Find("Sprites/Default"));

        var g = new Gradient();
        g.SetKeys(
            new []
            {
                new GradientColorKey(baseColor,0f),
                new GradientColorKey(baseColor,1f)
            },
            new []
            {
                new GradientAlphaKey(baseColor.a,0f),
                new GradientAlphaKey(baseColor.a,1f)
            }
        );
        lr.colorGradient = g;
        points = new Vector3[segments];
        curveLength = ComputeCurveLength();
        EmitShimmer(Time.deltaTime);
    }
    // Add to NoteTether.cs
    public Vector3 EvaluatePosition01(float t)
    {
        if (points == null || points.Length == 0) return (start ? start.position : transform.position);
        if (points.Length == 1) return points[0];
        float f = Mathf.Clamp01(t) * (points.Length - 1);
        int i = Mathf.FloorToInt(f);
        int j = Mathf.Min(i + 1, points.Length - 1);
        float u = f - i;
        return Vector3.Lerp(points[i], points[j], u);
    }

    private void EnsureShimmer()
    {
        if (shimmerPS != null) return;
        var go = new GameObject("ShimmerPS");
        go.transform.SetParent(transform, worldPositionStays: false);
        shimmerPS = go.AddComponent<ParticleSystem>();
        var main = shimmerPS.main;
        main.loop = false;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.startLifetime = shimmerLifetime;
        main.startSize = shimmerSize;
        main.startColor = new Color(baseColor.r, baseColor.g, baseColor.b, shimmerAlpha);
        main.maxParticles = 2048;

        var emission = shimmerPS.emission;
        emission.enabled = false; // we Emit() manually

        var shape = shimmerPS.shape;
        shape.enabled = false;

        var renderer = shimmerPS.GetComponent<ParticleSystemRenderer>();
        renderer.material = new Material(Shader.Find("Sprites/Default"));
        renderer.sortingLayerName = "Foreground"; // adjust to your layering
        renderer.sortingOrder = 20;
    }
    // compute length each frame (after lr.SetPositions)
    private float ComputeCurveLength()
    {
        float len = 0f;
        for (int i = 1; i < points.Length; i++)
            len += Vector3.Distance(points[i-1], points[i]);
        return len;
    }

    private void EmitShimmer(float deltaTime)
    {
        if (!shimmerEnabled || shimmerPS == null) return;
        float particlesToEmit = curveLength * shimmerRatePerUnit * Mathf.Max(0.0001f, deltaTime);

        int emits = Mathf.FloorToInt(particlesToEmit);
        for (int i = 0; i < emits; i++)
        {
            // pick a random point along the line
            float t = Random.value;
            int idx = Mathf.Clamp(Mathf.RoundToInt(t * (points.Length - 1)), 0, points.Length - 1);
            Vector3 pos = points[idx];

            var ep = new ParticleSystem.EmitParams
            {
                position = pos + (Vector3)(Random.insideUnitCircle * 0.025f),
                startLifetime = shimmerLifetime * Random.Range(0.8f, 1.2f),
                startSize = shimmerSize * Random.Range(0.8f, 1.3f),
                startColor = new Color(baseColor.r, baseColor.g, baseColor.b, shimmerAlpha * Random.Range(0.6f, 1f))
            };
            shimmerPS.Emit(ep, 1);
        }
    }
    public void SetEndpoints(Transform a, Transform b, Color c, float widthMul = 1f)
    {
        start = a; end = b; baseColor = c;
        if (lr) { lr.startColor = lr.endColor = c; lr.widthMultiplier = baseWidth * widthMul; }
        Update();
    }


    void Update()
    {
        if (start == null || end == null) { Destroy(gameObject); return; }
        RebuildCurve();
    }

    private void RebuildCurve()
    {
        Vector3 a = start.position;
        Vector3 d = end.position;
        Vector3 mid = (a + d) * 0.5f;

        // control points: a slight dip + forward pull toward end
        Vector3 dir   = (d - a);
        Vector3 right = Vector3.Cross(dir.normalized, Vector3.forward);
        float dist    = dir.magnitude;

        Vector3 b = a + 0.33f * dir + Vector3.down * sag * Mathf.Clamp(dist, 0f, 5f);
        Vector3 c = a + 0.66f * dir + Vector3.down * sag * 0.5f * Mathf.Clamp(dist, 0f, 5f);

        float t, it;
        float time = Time.time * noiseSpeed;
        for (int i = 0; i < segments; i++)
        {
            t  = i / (float)(segments - 1);
            it = 1f - t;

            // cubic Bezier
            Vector3 p = it*it*it * a
                      + 3f*it*it*t * b
                      + 3f*it*t*t * c
                      + t*t*t * d;

            // Per-segment small organic wobble (perpendicular)
            float wob = (Mathf.PerlinNoise(i * 0.17f, time) - 0.5f) * 2f;
            p += right * wob * noiseAmp;

            points[i] = p;
        }
        lr.SetPositions(points);
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

            if (t <= head && t >= Mathf.Max(0, head - headSpan))
            {
                float k = Mathf.InverseLerp(head - headSpan, head, t);
                c = Color.Lerp(baseColor, litColor, k * 0.9f + 0.1f);
                a = Mathf.Lerp(baseColor.a, 1f, k);
            }

            cks.Add(new GradientColorKey(c, t));
            aks.Add(new GradientAlphaKey(a, t));
        }

        g.SetKeys(cks.ToArray(), aks.ToArray());
        lr.colorGradient = g;
    }

    private IEnumerator FadeOut(float seconds)
    {
        float t = 0f;
        while (t < seconds)
        {
            t += Time.deltaTime;
            float a = Mathf.Lerp(baseColor.a, 0f, t / seconds);
            UpdateGradient(baseColor, a);
            lr.widthMultiplier = Mathf.Lerp(baseWidth, 0f, t / seconds);
            yield return null;
        }
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
        lr.colorGradient = g;
    }
}
