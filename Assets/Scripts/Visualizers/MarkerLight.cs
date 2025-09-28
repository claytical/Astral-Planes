using System.Collections;
using UnityEngine;

public class MarkerLight : MonoBehaviour
{
    public SpriteRenderer sr;     // assign or auto-find
    public float pulseScale = 1.25f;
    public float pulseTime  = 0.2f;

    private Color _base;
    private Vector3 _startScale;

    void Awake()
    {
        if (sr == null) sr = GetComponentInChildren<SpriteRenderer>();
        if (sr != null) _base = sr.color;
        _startScale = transform.localScale;
    }

    // Switch from gray → track color and give a quick pulse
    public void LightUp(Color trackColor)
    {
        if (sr == null) return;
        sr.color = trackColor;
        StopAllCoroutines();
        StartCoroutine(Pulse());
    }

    private IEnumerator Pulse()
    {
        float t = 0f;
        while (t < pulseTime)
        {
            t += Time.deltaTime;
            float u = Mathf.SmoothStep(0f,1f,t/pulseTime);
            transform.localScale = Vector3.Lerp(_startScale, _startScale * pulseScale, u);
            yield return null;
        }
        // ease back
        t = 0f;
        while (t < pulseTime)
        {
            t += Time.deltaTime;
            float u = Mathf.SmoothStep(0f,1f,t/pulseTime);
            transform.localScale = Vector3.Lerp(_startScale * pulseScale, _startScale, u);
            yield return null;
        }
    }

    // If you want a pre-lit gray, call this on spawn:
    public void SetGrey(Color grey) { if (sr) sr.color = grey; }
}