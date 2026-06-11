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
    public void SetWaitingParticles(Color color)
    {
        if (capturedParticles != null)
        {
            var main = capturedParticles.main;
            main.startColor = new Color(1f, 1f, 1f, color.a);
            capturedParticles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            capturedParticles.Play();
        }
        if (_capturedRenderer != null) _capturedRenderer.sortingOrder = _baseParticleSortOrder + 1;
        if (preCaptureParticles != null)
            preCaptureParticles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
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