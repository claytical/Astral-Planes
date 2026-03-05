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

    public void SetGrey(Color color)
    {
        if (sr) sr.color = color;
        Color c = sr.color;
        c.a = .3f;
        sr.color = c;
    }
    public void LightUp(Color trackColor)
    {
        if (sr == null) return;
        sr.color = trackColor;
        var vnm = GetComponent<VisualNoteMarker>();
        if (vnm != null)
            vnm.ApplyLitColor(trackColor);
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


}