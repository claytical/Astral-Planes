using System.Collections;
using UnityEngine;

public class VisualNoteMarker : MonoBehaviour
{ 
    private SpriteRenderer _spriteRenderer;
    public ParticleSystem capturedParticles;
    public ParticleSystem preCaptureParticles;
    public bool IsLit { get; set; }

    public void SetWaitingParticles(Color color)
    {
        if (preCaptureParticles != null)
        {
            ParticleSystem.MainModule pre = preCaptureParticles.main;
            pre.startColor = color;
        }
    }

    public void ReduceStartSize(float _startSize)
    {
        ParticleSystem.MainModule particles = capturedParticles.main;
        ParticleSystem.MinMaxCurve minMax = particles.startSize;

        float currentSize = minMax.constantMin;
        currentSize -= _startSize;
        currentSize = minMax.constantMin;
        particles.startSize = _startSize;
    }
    public void Initialize(Color color)
    {
        _spriteRenderer = GetComponentInChildren<SpriteRenderer>();
        if (_spriteRenderer != null)
        {
            _spriteRenderer.color = color;
        }
        if (capturedParticles != null)
        {
            ParticleSystem.MainModule main = capturedParticles.main;
            main.startColor = color;
        }

        if (preCaptureParticles != null)
        {
            ParticleSystem.MainModule pre = preCaptureParticles.main;
            pre.startColor = color;
            preCaptureParticles.Play();
        }

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

            // Lerp alpha and scale
            _spriteRenderer.color = Color.Lerp(startColor, targetColor, t);
            transform.localScale = Vector3.Slerp(Vector3.zero, originalScale, Mathf.SmoothStep(0f, 1f, t));
            yield return null;
        }

        _spriteRenderer.color = targetColor;
        transform.localScale = originalScale;

        if (capturedParticles != null)
        {
            targetColor.a = 1f;
            var main = capturedParticles.main;
            main.startColor = targetColor;
            capturedParticles.Play();
        }
    }

}
