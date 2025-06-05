using System.Collections;
using UnityEngine;

using UnityEngine;

public class VisualNoteMarker : MonoBehaviour
{
    private SpriteRenderer spriteRenderer;
    private ParticleSystem particleSystem;
    public void Initialize(Color color)
    {
        spriteRenderer = GetComponentInChildren<SpriteRenderer>();
        if (spriteRenderer != null)
        {
            spriteRenderer.color = color;
        }
        particleSystem = GetComponentInChildren<ParticleSystem>();
        if (particleSystem != null)
        {
            ParticleSystem.MainModule main = particleSystem.main;
            main.startColor = color;
        }
        StartCoroutine(FadeAndScaleIn(color));
        
    }
    

    private IEnumerator FadeAndScaleIn(Color targetColor)
    {
        targetColor.a = .5f;
        if (spriteRenderer == null) yield break;

        float duration = 2f;
        float elapsed = 0f;

        Color startColor = new Color(targetColor.r, targetColor.g, targetColor.b, 0f);
        spriteRenderer.color = startColor;

        Vector3 originalScale = transform.localScale;
        transform.localScale = Vector3.zero;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t = elapsed / duration;

            // Lerp alpha and scale
            spriteRenderer.color = Color.Lerp(startColor, targetColor, t);
            transform.localScale = Vector3.Slerp(Vector3.zero, originalScale, Mathf.SmoothStep(0f, 1f, t));
            yield return null;
        }

        spriteRenderer.color = targetColor;
        transform.localScale = originalScale;

        if (particleSystem != null)
        {
            var main = particleSystem.main;
            main.startColor = targetColor;
            particleSystem.Play();
        }
    }

}
