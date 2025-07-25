using System.Collections;
using UnityEngine;

public static class VisualFeedbackUtility
{
    // Inside VisualFeedbackUtility.cs

    public static IEnumerator SpectrumFlickerWithPulse(
        SpriteRenderer sr,
        Transform t,
        float duration = 0.4f,
        float scaleMultiplier = 1.2f,
        Color? baseTintOverride = null,
        bool cycleHue = false
    )
    {
        float tElapsed = 0f;

        // Instead of snapshotting original state *before* animation, do it *right before restore*
        Vector3 initialScale = t.localScale;
        Color originalColor = sr.color;
        Color baseColor = baseTintOverride ?? sr.color;

        while (tElapsed < duration)
        {
            tElapsed += Time.deltaTime;
            float progress = tElapsed / duration;

            Color flickerColor = cycleHue
                ? Color.HSVToRGB(Mathf.Repeat(tElapsed * 3f, 1f), 0.8f, 1f)
                : baseColor;

            flickerColor.a = originalColor.a;
            sr.color = flickerColor;

            float pulse = 1f + Mathf.Sin(progress * Mathf.PI) * (scaleMultiplier - 1f);
            t.localScale = initialScale * pulse;

            yield return null;
        }

        // Revalidate scale in case external system changed it (e.g. teleport or cutscene)
        // Only restore if scale is still within expected bounds
        if (t != null && t.localScale.magnitude < 100f) // sanity guard
        {
            t.localScale = initialScale; 
        }

        sr.color = originalColor;
    }
    public static IEnumerator BoundaryThudFeedback(SpriteRenderer sprite, Transform objTransform, Vector2 normal, float duration = 0.2f)
    {
        if (sprite == null || objTransform == null) yield break;

        Vector3 originalScale = objTransform.localScale;
        Color originalColor = sprite.color;

        // Choose axis to squish based on collision direction
        Vector3 squishScale = originalScale;
        if (Mathf.Abs(normal.x) > Mathf.Abs(normal.y))
        {
            // Horizontal wall → squish X
            squishScale.x *= 0.5f;
            squishScale.y *= 1.2f;
        }
        else
        {
            // Vertical wall → squish Y
            squishScale.y *= 0.5f;
            squishScale.x *= 1.2f;
        }

        float flickerAlpha = 0.5f;
        Color flickerColor = Color.red;
        flickerColor.a = flickerAlpha;

        float elapsed = 0f;

        while (elapsed < duration)
        {
            float t = elapsed / duration;

            // Interpolate scale
            objTransform.localScale = Vector3.Lerp(squishScale, originalScale, t);

            // Interpolate flicker
            sprite.color = Color.Lerp(flickerColor, originalColor, t);

            elapsed += Time.deltaTime;
            yield return null;
        }

        objTransform.localScale = originalScale;
        sprite.color = originalColor;
    }

}