using System.Collections;
using UnityEngine;

public class VisualNoteMarker : MonoBehaviour
{
    private SpriteRenderer _spriteRenderer;
    private ParticleSystem _particleSystem;
    public bool IsLit { get; set; }

    public void Initialize(Color color)
    {
        _spriteRenderer = GetComponentInChildren<SpriteRenderer>();
        if (_spriteRenderer != null)
        {
            _spriteRenderer.color = color;
        }
        _particleSystem = GetComponentInChildren<ParticleSystem>();
        if (_particleSystem != null)
        {
            ParticleSystem.MainModule main = _particleSystem.main;
            main.startColor = color;
        }
        StartCoroutine(FadeAndScaleIn(color));
        
    }
    
    private IEnumerator FadeAndScaleIn(Color targetColor)
    {
        targetColor.a = .5f;
        if (_spriteRenderer == null) yield break;

        float duration = 2f;
        float elapsed = 0f;

        Color startColor = new Color(targetColor.r, targetColor.g, targetColor.b, 0f);
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

        if (_particleSystem != null)
        {
            var main = _particleSystem.main;
            main.startColor = targetColor;
            _particleSystem.Play();
        }
    }

}
