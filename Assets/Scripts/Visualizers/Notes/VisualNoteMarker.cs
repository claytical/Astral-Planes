using System.Collections;
using UnityEngine;

public class VisualNoteMarker : MonoBehaviour
{
    private SpriteRenderer _spriteRenderer;
    public ParticleSystem capturedParticles;
    public ParticleSystem preCaptureParticles;
    public bool IsLit { get; set; }

    private ParticleSystemRenderer _capturedRenderer;
    private int _baseParticleSortOrder;

    void Awake()
    {
        if (_spriteRenderer == null)
            _spriteRenderer = GetComponentInChildren<SpriteRenderer>();
        if (capturedParticles != null)
        {
            _capturedRenderer = capturedParticles.GetComponent<ParticleSystemRenderer>();
            if (_capturedRenderer != null) _baseParticleSortOrder = _capturedRenderer.sortingOrder;
        }
    }
    public void ApplyLitColor(Color color)
    {
        IsLit = true;

        if (_spriteRenderer == null) _spriteRenderer = GetComponentInChildren<SpriteRenderer>();
        if (_spriteRenderer != null)
        {
            var c = color;
            c.a = _spriteRenderer.color.a; // preserve current alpha choice if you want
            _spriteRenderer.color = c;
        }

        if (_capturedRenderer != null) _capturedRenderer.sortingOrder = _baseParticleSortOrder;
        ApplyParticleColor(capturedParticles, color);
        if (preCaptureParticles != null)
            preCaptureParticles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
    }

    private static void ApplyParticleColor(ParticleSystem ps, Color color)
    {
        if (ps == null) return;

        var main = ps.main;
        main.startColor = new Color(color.r, color.g, color.b, 1f);

        var trails = ps.trails;
        if (trails.enabled)
            trails.colorOverLifetime = new ParticleSystem.MinMaxGradient(color);

        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        ps.Play();
    }
    [Header("Placeholder Ring")]
    [SerializeField] private float ringRadius = 0.35f;
    [SerializeField] private float ringParticleSize = 0.03f;
    [SerializeField] private float ringEmissionRate = 40f;
    [SerializeField] private float ringLifetime = 0.6f;
    [SerializeField] private float ringAlpha = 0.8f;
    private bool _ringConfigured;

    // Reuses the (otherwise unused) Pre Capture system as an empty ring of tiny dots.
    private void ConfigureRingParticles()
    {
        if (_ringConfigured || preCaptureParticles == null) return;
        _ringConfigured = true;

        var ps = preCaptureParticles;
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        var main = ps.main;
        main.loop = true;
        main.playOnAwake = false;
        main.simulationSpace = ParticleSystemSimulationSpace.Local;
        main.startSpeed = 0f;
        main.startLifetime = ringLifetime;
        main.startSize = ringParticleSize;
        main.startColor = new Color(1f, 1f, 1f, ringAlpha);
        main.gravityModifier = 0f;

        var shape = ps.shape;
        shape.enabled = true;
        shape.shapeType = ParticleSystemShapeType.Circle;
        shape.radius = ringRadius;
        shape.radiusThickness = 0f; // edge only
        shape.arc = 360f;
        shape.position = Vector3.zero;
        shape.rotation = Vector3.zero;
        shape.scale = Vector3.one;

        var emission = ps.emission;
        emission.enabled = true;
        emission.rateOverTime = ringEmissionRate;

        var vel = ps.velocityOverLifetime;
        vel.enabled = false;
        var force = ps.forceOverLifetime;
        force.enabled = false;

        var col = ps.colorOverLifetime;
        col.enabled = true;
        var g = new Gradient();
        g.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.3f), new GradientAlphaKey(0f, 1f) });
        col.color = new ParticleSystem.MinMaxGradient(g);
    }

    public void SetWaitingParticles(Color color)
    {
        if (capturedParticles != null)
            capturedParticles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        if (_capturedRenderer != null) _capturedRenderer.sortingOrder = _baseParticleSortOrder + 1;

        if (preCaptureParticles != null)
        {
            ConfigureRingParticles();
            var rend = preCaptureParticles.GetComponent<ParticleSystemRenderer>();
            if (rend != null) rend.sortingOrder = _baseParticleSortOrder + 1;
            preCaptureParticles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            preCaptureParticles.Play();
        }
    }

    public void LightUp(Color color)
    {
        IsLit = true;

        // Sprite
        if (_spriteRenderer == null)
            _spriteRenderer = GetComponentInChildren<SpriteRenderer>();
        if (_spriteRenderer != null)
        {
            var c = color;
            c.a = Mathf.Max(_spriteRenderer.color.a, 0.5f);
            _spriteRenderer.color = c;
        }

        if (_capturedRenderer != null) _capturedRenderer.sortingOrder = _baseParticleSortOrder;
        if (preCaptureParticles != null)
            preCaptureParticles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        if (capturedParticles != null)
        {
            var capMain = capturedParticles.main;
            capMain.startColor = new Color(color.r, color.g, color.b, 1f);

            if (!capturedParticles.isPlaying)
                capturedParticles.Play(true);
        }
    }

    public void Initialize(Color color)
    {
        if (_spriteRenderer == null)
            _spriteRenderer = GetComponentInChildren<SpriteRenderer>();

        if (_spriteRenderer != null)
            _spriteRenderer.color = color;

        if (capturedParticles != null)
        {
            var main = capturedParticles.main;
            main.startColor = new Color(color.r, color.g, color.b, 1f);
            capturedParticles.Clear();
            if (!capturedParticles.isPlaying) capturedParticles.Play(true);
        }

        if (preCaptureParticles != null)
            preCaptureParticles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        StartCoroutine(FadeAndScaleIn(color));
    }

    private IEnumerator FadeAndScaleIn(Color targetColor)
    {
        if (_spriteRenderer == null) yield break;

        float duration = 2f;
        float elapsed = 0f;

        Color startColor = new Color(targetColor.r, targetColor.g, targetColor.b, 0f);
        targetColor.a = .5f;
        _spriteRenderer.color = startColor;

        Vector3 originalScale = transform.localScale;
        transform.localScale = Vector3.zero;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t = elapsed / duration;

            _spriteRenderer.color = Color.Lerp(startColor, targetColor, t);
            transform.localScale = Vector3.Slerp(Vector3.zero, originalScale, Mathf.SmoothStep(0f, 1f, t));
            yield return null;
        }

        _spriteRenderer.color = targetColor;
        transform.localScale = originalScale;
    }
}