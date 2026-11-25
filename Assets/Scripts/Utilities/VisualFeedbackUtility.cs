using System.Collections;
using UnityEngine;

public static class VisualFeedbackUtility
{
    // restScale: the scale to pulse around and return to. Pass the object's authored scale: snapshotting
    // the live scale (the old behaviour) baked in any squash still running when this started.
    // scaleLocked: while true, another system owns scale (e.g. a path-exit shrink) — tint only.
    public static IEnumerator SpectrumFlickerWithPulse(SpriteRenderer sr, Transform t, float duration = 0.4f, float scaleMultiplier = 1.2f, Color? baseTintOverride = null, bool cycleHue = false,
        Vector3? restScale = null, System.Func<bool> scaleLocked = null)
    {
        float tElapsed = 0f;

        Vector3 initialScale = restScale ?? t.localScale;
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
            if (scaleLocked == null || !scaleLocked()) t.localScale = initialScale * pulse;

            yield return null;
        }

        if (t != null && (scaleLocked == null || !scaleLocked()))
            t.localScale = initialScale;

        sr.color = originalColor;
    }
    public static IEnumerator BoundaryThudFeedback(SpriteRenderer sprite, Transform objTransform, Vector2 normal, float duration = 0.2f,
        Vector3? restScale = null, System.Func<bool> scaleLocked = null)
    {
        if (sprite == null || objTransform == null) yield break;

        Vector3 originalScale = restScale ?? objTransform.localScale;
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
        Color flickerColor = Color.gray;
        flickerColor.a = flickerAlpha;

        float elapsed = 0f;

        while (elapsed < duration)
        {
            float t = elapsed / duration;

            // Interpolate scale
            if (scaleLocked == null || !scaleLocked())
                objTransform.localScale = Vector3.Lerp(squishScale, originalScale, t);

            // Interpolate flicker
            sprite.color = Color.Lerp(flickerColor, originalColor, t);

            elapsed += Time.deltaTime;
            yield return null;
        }

        if (objTransform != null && (scaleLocked == null || !scaleLocked()))
            objTransform.localScale = originalScale;
        sprite.color = originalColor;
    }

}