using System.Collections;
using UnityEngine;

/// <summary>
/// Fades out the intro title when the player already has stored ring sessions.
/// </summary>
public class IntroTitleFader : MonoBehaviour
{
    [SerializeField] private float fadeDelay = 0.5f;
    [SerializeField] private float fadeDuration = 1.5f;

    private void Start()
    {
        if (RingSessionStore.LoadAllRingsFromDisk().Count > 0)
            StartCoroutine(FadeOut());
    }

    private IEnumerator FadeOut()
    {
        var group = GetComponent<CanvasGroup>();
        if (group == null) group = gameObject.AddComponent<CanvasGroup>();

        float start = group.alpha;
        if (fadeDelay > 0f) yield return new WaitForSecondsRealtime(fadeDelay);

        float t = 0f;
        while (t < fadeDuration)
        {
            t += Time.unscaledDeltaTime;
            group.alpha = Mathf.Lerp(start, 0f, t / fadeDuration);
            yield return null;
        }
        group.alpha = 0f;
    }
}
