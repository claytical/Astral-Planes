using System.Collections;
using UnityEngine;

/// <summary>
/// Fades a wisp's bridge-sample AudioSource to silence, then stops it and destroys its
/// GameObject. Lives on the sample's own (unparented, DontDestroyOnLoad) GameObject so the fade
/// survives the wisp and the interstitial maze being torn down. See Wisp.FadeOutActiveBridgeSamples.
/// </summary>
public class BridgeSampleFader : MonoBehaviour
{
    private AudioSource _source;

    public void Begin(AudioSource source, float seconds)
    {
        _source = source;
        StopAllCoroutines();
        if (_source == null || seconds <= 0f)
        {
            if (_source != null) _source.Stop();
            Destroy(gameObject);
            return;
        }
        StartCoroutine(FadeRoutine(seconds));
    }

    private IEnumerator FadeRoutine(float seconds)
    {
        float v0 = _source.volume;
        float t = 0f;
        while (t < seconds && _source != null)
        {
            t += Time.unscaledDeltaTime;
            float k = 1f - Mathf.Clamp01(t / seconds);
            _source.volume = v0 * k * k;   // ease-out: most of the drop happens early
            yield return null;
        }
        if (_source != null) _source.Stop();
        Destroy(gameObject);
    }
}
