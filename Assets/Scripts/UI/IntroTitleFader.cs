using System.Collections;
using UnityEngine;

/// <summary>
/// The title on Main. It stays up while the title menu (MainMenu) is showing and fades out while
/// the motif carousel or Options has the screen (<see cref="SetVisible"/>).
/// </summary>
public class IntroTitleFader : MonoBehaviour
{
    [SerializeField] private float fadeDelay = 0.5f;
    [SerializeField] private float fadeDuration = 1.5f;

    private static IntroTitleFader _instance;
    private Coroutine _fade;

    private void Awake() => _instance = this;

    private void OnDestroy()
    {
        if (_instance == this) _instance = null;
    }

    /// <summary>Fades the title in (menu up) or out (carousel / Options up). No-op outside Main.</summary>
    public static void SetVisible(bool visible)
    {
        if (_instance == null || !_instance.isActiveAndEnabled) return;
        _instance.FadeTo(visible ? 1f : 0f, visible ? 0f : _instance.fadeDelay, visible ? 0.3f : _instance.fadeDuration * 0.4f);
    }

    private void FadeTo(float target, float delay, float duration)
    {
        if (_fade != null) StopCoroutine(_fade);
        _fade = StartCoroutine(Fade(target, delay, duration));
    }

    private IEnumerator Fade(float target, float delay, float duration)
    {
        var group = GetComponent<CanvasGroup>();
        if (group == null) group = gameObject.AddComponent<CanvasGroup>();

        float start = group.alpha;
        if (delay > 0f) yield return new WaitForSecondsRealtime(delay * 0.2f);

        float t = 0f;
        while (t < duration)
        {
            t += Time.unscaledDeltaTime;
            group.alpha = Mathf.Lerp(start, target, t / duration);
            yield return null;
        }
        group.alpha = target;
        _fade = null;
    }
}
