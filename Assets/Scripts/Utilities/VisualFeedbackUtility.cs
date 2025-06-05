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

}