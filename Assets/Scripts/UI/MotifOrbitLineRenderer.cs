using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Draws PhaseLibraryCarousel's orbit path as pooled world-space polylines. Every line wobbles
/// like a record groove; the epicenter is the "needle" — the point where the orbit leaves the
/// selected glyph's left edge, by its playback point. Each note sends a ripple out from there
/// along the whole line (see PulseNeedle).
/// </summary>
public class MotifOrbitLineRenderer : MonoBehaviour
{
    [Tooltip("Same material as the ring glyph contour lines.")]
    [SerializeField] private Material lineMaterial;

    [SerializeField] private Color lineColor = Color.white;
    [SerializeField] private float lineWidth = 0.06f;
    [SerializeField] private int sortingOrder = -1;

    [Header("Needle Shimmer")]
    [Tooltip("Wobbles the whole orbit line, centred on the needle (where the orbit leaves the selected glyph). Off = static lines.")]
    [SerializeField] private bool shimmerEnabled = true;

    [Tooltip("Height of the faint always-on wave, in line widths.")]
    [SerializeField] private float idleAmplitude = 0.12f;

    [Tooltip("Height of the ripple a note launches, in line widths.")]
    [SerializeField] private float pulseAmplitude = 1.2f;

    [Tooltip("Ripple wavelength, as a fraction of the needle run (the stretch from the selected glyph to its neighbour).")]
    [Range(0.05f, 1f)]
    [SerializeField] private float wavelength = 0.22f;

    [Tooltip("How fast a ripple travels away from the needle, in needle-run lengths per second.")]
    [SerializeField] private float rippleSpeed = 1.4f;

    [Tooltip("How far from the needle a ripple reaches before it has faded out, in needle-run lengths.")]
    [SerializeField] private float rippleReach = 3f;

    [Tooltip("Seconds for a ripple (and its brightening) to fade to about a third.")]
    [SerializeField] private float rippleDecaySec = 0.7f;

    [Tooltip("Speed of the slow swell that makes the idle wave wobble, in Hz.")]
    [SerializeField] private float wobbleHz = 0.6f;

    [Tooltip("Extra line width at the peak of a note, as a fraction of the normal width.")]
    [SerializeField] private float widthPulse = 0.6f;

    [Tooltip("How far the line brightens toward white at the peak of a note.")]
    [Range(0f, 1f)]
    [SerializeField] private float brightnessPulse = 0.5f;

    [Tooltip("How far, in world units, the needle runs on past where the orbit leaves the selected glyph's outer ring (the carousel's linePadding gap), so its fading tip lands on the ring contour instead of stopping short of it.")]
    [SerializeField, Min(0f)] private float tipReach = 0.2f;

    [Tooltip("Length, in world units, over which the line fades out at its soft end (where it leaves the right side of the selected glyph). 0 = hard end.")]
    [SerializeField, Min(0f)] private float tipSoftLength = 0.3f;

    [Header("Needle Tip Sparks")]
    [Tooltip("How far back along the line, in world units, the sparks start, so they shoot out of the line and travel on it before reaching the ring.")]
    [SerializeField, Min(0f)] private float tipSparkOffset = 0.15f;

    [Tooltip("Emits a burst of sparks where the needle meets the ring contour, on each note pulse.")]
    [SerializeField] private bool tipSparksEnabled = true;

    [Tooltip("Optional ParticleSystem prefab for the sparks (its first ParticleSystem is used, colored per burst; its material is replaced with a soft additive dot). Empty = a simple built-in spark emitter.")]
    [SerializeField] private GameObject tipSparksPrefab;

    [Tooltip("Particles per burst (built-in emitter, and the burst size sent to a prefab).")]
    [SerializeField, Min(1)] private int tipBurstCount = 10;

    [Tooltip("Spark size in world units (built-in emitter only).")]
    [SerializeField, Min(0.001f)] private float tipSparkSize = 0.05f;

    [Tooltip("Spark travel speed in world units per second (built-in emitter only).")]
    [SerializeField, Min(0f)] private float tipSparkSpeed = 0.8f;

    [Tooltip("Spark lifetime in seconds (built-in emitter only).")]
    [SerializeField, Min(0.05f)] private float tipSparkLifetime = 0.45f;

    private static readonly int TipFadeId = Shader.PropertyToID("_TipFade");
    private MaterialPropertyBlock _tipBlock;
    private readonly List<Vector3> _needlePoints = new();

    private const float IdleHz     = 0.35f;
    private const int   MaxRipples = 6;
    private const int   MaxPoints  = 240;

    // One drawn run: its line, the resting points, and a scratch array for the animated ones.
    private sealed class Run
    {
        public LineRenderer Line;
        public Vector3[] Base = new Vector3[2];
        public Vector3[] Pos  = new Vector3[2];
        public int   Count;
        public Color Color;
        public float Alpha;
    }

    private readonly List<LineRenderer> _pool = new();
    private readonly List<Run> _runs = new();
    private int _used;

    private Vector3 _epicenter;
    private float   _refLength = 1f;   // length of the needle run; ripple distances are measured in these
    private readonly List<double> _ripples = new(); // dsp start times
    private Vector2 _tipDir = Vector2.right;       // direction the needle run is heading as it reaches the ring
    private bool _hasNeedle;                         // whether the current layout drew a needle (so _epicenter is its tip)
    private readonly List<(double dsp, Color color)> _pendingSparks = new();
    private ParticleSystem _sparks;

    public void Clear()
    {
        for (int i = 0; i < _pool.Count; i++) _pool[i].gameObject.SetActive(false);
        _used = 0;
        _hasNeedle = false;
    }

    /// <param name="softStart">Fades this run's start to transparent (the soft end of the line, to the right of the selected glyph).</param>
    /// <param name="shimmer">Marks this run as the needle: its end is the wobble's epicenter and
    /// its length the unit ripples travel in. Without one the last epicenter is kept.</param>
    public void DrawPolyline(IReadOnlyList<Vector3> points, float alpha, bool shimmer = false, bool softStart = false)
    {
        if (points.Count < 2) return;
        var lr = Next(alpha);

        if (shimmer && tipReach > 0f)
        {
            var d = (Vector2)(points[points.Count - 1] - points[points.Count - 2]);
            if (d.sqrMagnitude > 1e-8f)
            {
                _needlePoints.Clear();
                for (int i = 0; i < points.Count; i++) _needlePoints.Add(points[i]);
                _needlePoints.Add(points[points.Count - 1] + (Vector3)(d.normalized * tipReach));
                points = _needlePoints;
            }
        }

        if (softStart)
        {
            // The line's far end, leaving the selected glyph on its right, fades in/out over tipSoftLength
            // (the shader fades the start of the line for a negative value).
            _tipBlock ??= new MaterialPropertyBlock();
            float len = Mathf.Max(0.01f, PathLength(points));
            lr.GetPropertyBlock(_tipBlock);
            _tipBlock.SetFloat(TipFadeId, tipSoftLength > 0f ? -Mathf.Clamp(tipSoftLength / len, 0.001f, 1f) : 0f);
            lr.SetPropertyBlock(_tipBlock);
        }

        if (shimmerEnabled)
        {
            if (shimmer)
            {
                _epicenter = points[points.Count - 1];
                _hasNeedle = true;
                var heading = (Vector2)(points[points.Count - 1] - points[points.Count - 2]);
                _tipDir = heading.sqrMagnitude > 1e-8f ? heading.normalized : Vector2.right;
                _refLength = Mathf.Max(0.01f, PathLength(points));
            }
            var run = RunFor(_used - 1);
            run.Line  = lr;
            run.Color = lr.startColor;
            run.Alpha = alpha;
            Resample(run, points);
            lr.positionCount = run.Count;
            lr.SetPositions(run.Base);
            return;
        }

        lr.positionCount = points.Count;
        for (int i = 0; i < points.Count; i++) lr.SetPosition(i, points[i]);
    }

    /// <summary>Launches a ripple out from the needle at the given audio-clock time.</summary>
    public void PulseNeedle(double startDsp, Color sparkColor)
    {
        if (tipSparksEnabled && _pendingSparks.Count < MaxRipples * 2) _pendingSparks.Add((startDsp, sparkColor));
        PulseNeedle(startDsp);
    }

    public void PulseNeedle(double startDsp)
    {
        if (!shimmerEnabled) return;
        if (_ripples.Count >= MaxRipples) _ripples.RemoveAt(0);
        _ripples.Add(startDsp);
    }

    private Run RunFor(int index)
    {
        while (_runs.Count <= index) _runs.Add(new Run());
        return _runs[index];
    }

    private static float PathLength(IReadOnlyList<Vector3> pts)
    {
        float total = 0f;
        for (int i = 1; i < pts.Count; i++) total += Vector3.Distance(pts[i - 1], pts[i]);
        return total;
    }

    // Evenly spaced points along the run, dense enough for the wave however coarsely the orbit is sampled.
    private void Resample(Run run, IReadOnlyList<Vector3> pts)
    {
        float total = PathLength(pts);
        int   n     = Mathf.Clamp(Mathf.CeilToInt(total / Mathf.Max(0.005f, lineWidth * 1.5f)) + 1, 2, MaxPoints);
        if (run.Base.Length < n) { run.Base = new Vector3[n]; run.Pos = new Vector3[n]; }
        run.Count = n;

        int   seg      = 1;
        float segStart = 0f;
        float segLen   = Vector3.Distance(pts[0], pts[1]);
        for (int k = 0; k < n; k++)
        {
            float target = total * k / (n - 1);
            while (seg < pts.Count - 1 && target > segStart + segLen)
            {
                segStart += segLen;
                seg++;
                segLen = Vector3.Distance(pts[seg - 1], pts[seg]);
            }
            float f = segLen > 1e-5f ? Mathf.Clamp01((target - segStart) / segLen) : 0f;
            run.Base[k] = Vector3.Lerp(pts[seg - 1], pts[seg], f);
        }
    }

    // Ripple height at a distance (in needle-run lengths) from the epicenter, summed over live ripples.
    private float RippleAt(float dist, double now, float decay, float sigma)
    {
        float reachFade = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(rippleReach * 0.5f, rippleReach, dist));
        if (reachFade <= 0f) return 0f;

        float sum = 0f;
        for (int r = 0; r < _ripples.Count; r++)
        {
            float age = (float)(now - _ripples[r]);
            if (age < 0f) continue;
            float d = dist - rippleSpeed * age;
            sum += pulseAmplitude * Mathf.Exp(-age / decay)
                 * Mathf.Exp(-(d * d) / (sigma * sigma)) * Mathf.Sin(2f * Mathf.PI * d / wavelength);
        }
        return sum * reachFade;
    }

    // Strength (0-1) of the ripples at a distance, for the width / brightness pulse.
    private float BumpAt(float dist, double now, float decay, float sigma)
    {
        float bump = 0f;
        for (int r = 0; r < _ripples.Count; r++)
        {
            float age = (float)(now - _ripples[r]);
            if (age < 0f) continue;
            float d = dist - rippleSpeed * age;
            bump += Mathf.Exp(-age / decay) * Mathf.Exp(-(d * d) / (4f * sigma * sigma));
        }
        return Mathf.Min(1f, bump);
    }

    // Soft round additive dot built from the line material: black contributes nothing, so no dark box
    // shows over the line, and the radial fade replaces a sprite texture.
    private Material _sparkMaterial;
    private Material SparkMaterial()
    {
        if (_sparkMaterial != null) return _sparkMaterial;
        if (lineMaterial == null) return null;
        _sparkMaterial = new Material(lineMaterial);
        if (_sparkMaterial.HasProperty("_EdgeSoftness")) _sparkMaterial.SetFloat("_EdgeSoftness", 0f);
        if (_sparkMaterial.HasProperty("_Radial"))       _sparkMaterial.SetFloat("_Radial", 1f);
        if (_sparkMaterial.HasProperty("_SrcBlend"))     _sparkMaterial.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        if (_sparkMaterial.HasProperty("_DstBlend"))     _sparkMaterial.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.One);
        return _sparkMaterial;
    }

    // Spark emitter at the needle tip: the prefab's ParticleSystem if assigned, else a small built-in one.
    private ParticleSystem EnsureSparks()
    {
        if (_sparks != null) return _sparks;

        if (tipSparksPrefab != null)
        {
            var go = Instantiate(tipSparksPrefab, transform);
            _sparks = go.GetComponentInChildren<ParticleSystem>();
            if (_sparks != null)
            {
                var m = _sparks.main;
                m.simulationSpace = ParticleSystemSimulationSpace.World;
                m.playOnAwake = false;
                var em = _sparks.emission;
                em.enabled = false;
                var prefabRenderer = _sparks.GetComponent<ParticleSystemRenderer>();
                prefabRenderer.sortingOrder = sortingOrder + 100;
                // The prefab's own (usually opaque-black-background) particle sprite would leave a dark gap
                // over the line, so it gets the same soft additive dot as the built-in sparks.
                var soft = SparkMaterial();
                if (soft != null) prefabRenderer.sharedMaterial = soft;
                return _sparks;
            }
        }

        var obj = new GameObject("NeedleTipSparks");
        obj.transform.SetParent(transform, worldPositionStays: false);
        _sparks = obj.AddComponent<ParticleSystem>();
        _sparks.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        var main = _sparks.main;
        main.playOnAwake      = false;
        main.loop             = false;
        main.simulationSpace  = ParticleSystemSimulationSpace.World;
        main.startLifetime    = tipSparkLifetime;
        main.startSpeed       = tipSparkSpeed;
        main.startSize        = tipSparkSize;
        main.maxParticles     = 256;

        var emission = _sparks.emission;
        emission.enabled = false;

        var shape = _sparks.shape;
        shape.shapeType = ParticleSystemShapeType.Cone; // axis is the emitter's +Z, aimed per burst at the ring
        shape.angle     = 25f;
        shape.radius    = 0.01f;

        var fade = _sparks.colorOverLifetime;
        fade.enabled = true;
        var g = new Gradient();
        g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                  new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0f, 1f) });
        fade.color = g;

        var shrink = _sparks.sizeOverLifetime;
        shrink.enabled = true;
        shrink.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 0.2f));

        var pr = _sparks.GetComponent<ParticleSystemRenderer>();
        pr.sortingOrder = sortingOrder + 100;
        var spark = SparkMaterial();
        if (spark != null) pr.sharedMaterial = spark;
        return _sparks;
    }

    private void EmitDueSparks(double now)
    {
        for (int i = _pendingSparks.Count - 1; i >= 0; i--)
        {
            var (dsp, color) = _pendingSparks[i];
            if (dsp > now) continue;
            _pendingSparks.RemoveAt(i);
            if (!_hasNeedle || now - dsp > 0.5) continue; // no tip on screen, or the moment already passed

            var ps = EnsureSparks();
            if (ps == null) continue;
            // Move the emitter instead of overriding the position in EmitParams: an overridden position
            // bypasses the shape module, which left the particles with no direction (they drifted along
            // Z, i.e. sat still on screen).
            ps.transform.SetPositionAndRotation(_epicenter - (Vector3)(_tipDir * tipSparkOffset), Quaternion.LookRotation(new Vector3(_tipDir.x, _tipDir.y, 0f)));
            var m = ps.main;
            m.startColor = color;
            ps.Emit(tipBurstCount);
        }
    }

    private void Update()
    {
        EmitDueSparks(AudioSettings.dspTime);

        if (!shimmerEnabled || _used == 0) return;

        double now   = AudioSettings.dspTime;
        float  t     = (float)(now % 1000.0);
        float  decay = Mathf.Max(0.05f, rippleDecaySec);
        float  sigma = wavelength * 0.6f;
        float  swell = 0.6f + 0.4f * Mathf.Sin(2f * Mathf.PI * wobbleHz * t);

        for (int r = _ripples.Count - 1; r >= 0; r--)
            if (now - _ripples[r] > decay * 6f) _ripples.RemoveAt(r);

        float invRef = 1f / _refLength;
        for (int i = 0; i < _used && i < _runs.Count; i++)
        {
            var run = _runs[i];
            if (run.Line == null) continue;

            float nearest = float.MaxValue;
            for (int k = 0; k < run.Count; k++)
            {
                var   b    = run.Base[k];
                float dist = Vector2.Distance(b, _epicenter) * invRef;
                nearest = Mathf.Min(nearest, dist);

                // The idle wave runs out from the needle too, fading only gently with distance.
                float idleFade = Mathf.Lerp(1f, 0.4f, Mathf.SmoothStep(0f, 1f, dist / (rippleReach * 1.5f)));
                float y = idleAmplitude * swell * idleFade
                        * Mathf.Sin(2f * Mathf.PI * (dist / wavelength - IdleHz * t));
                y += RippleAt(dist, now, decay, sigma);

                b.y += y * lineWidth * run.Alpha;
                run.Pos[k] = b;
            }
            run.Line.SetPositions(run.Pos);

            float bump = BumpAt(nearest, now, decay, sigma);
            run.Line.widthMultiplier = lineWidth * (1f + widthPulse * bump);
            var c = Color.Lerp(run.Color, new Color(1f, 1f, 1f, run.Color.a), brightnessPulse * bump);
            run.Line.startColor = c;
            run.Line.endColor   = c;
        }
    }

    private LineRenderer Next(float alpha)
    {
        if (_used == _pool.Count) _pool.Add(CreateLine());
        var lr = _pool[_used++];
        lr.gameObject.SetActive(true);
        lr.SetPropertyBlock(null); // a reused line may have been the needle (soft tip)
        lr.widthMultiplier = lineWidth; // a reused line may have been left widened by the needle
        var c = lineColor;
        c.a *= alpha;
        lr.startColor = c;
        lr.endColor   = c;
        return lr;
    }

    private LineRenderer CreateLine()
    {
        var go = new GameObject("OrbitLine");
        go.transform.SetParent(transform, worldPositionStays: false);
        var lr = go.AddComponent<LineRenderer>();
        if (lineMaterial != null) lr.material = lineMaterial;
        lr.useWorldSpace     = true;
        lr.textureMode       = LineTextureMode.Stretch; // RingUnlit's edge softness reads V across the width
        lr.alignment         = LineAlignment.View;
        lr.widthMultiplier   = lineWidth;
        lr.numCornerVertices = 4;
        lr.numCapVertices    = 4;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows    = false;
        lr.sortingOrder      = sortingOrder;
        return lr;
    }
}
