using UnityEngine;
using System.Collections;

public class DiamondVisual : MonoBehaviour
{
    public SpriteRenderer sprite;
    public float spinSpeed = 90f;
    public float baseScale = 1f;
    public float pulseAmplitude = 0.1f;
    public float pulseSpeed = 2f;

    private float hueOffset;
    private bool shouldRotate = true;
    private float storedHue;
    public Color assignedColor;


    void Start()
    {
//        hueOffset = Random.Range(0f, 1f);
    }
    void Update()
    {
        if (shouldRotate)
        {
            transform.Rotate(Vector3.forward, spinSpeed * Time.deltaTime);
        }

        float scale = baseScale + Mathf.Sin(Time.time * pulseSpeed) * pulseAmplitude;
        transform.localScale = Vector3.one * scale;

        // 🌟 Breathing alpha pulse (twinkling star)
        assignedColor.a = 0.3f + 0.2f * Mathf.Sin(Time.time * 2f); // base + subtle pulse

        sprite.color = assignedColor;
    }

    public void SetAssignedColor(Color color)
    {
        assignedColor = color;
        sprite.color = color;
        storedHue = 0f; // optional: neutralize hue logic
    }

    public void SetHue(float hue)
    {
        storedHue = hue;
        Color color = Color.HSVToRGB(hue, 0.8f, 1f);
        sprite.color = color;
        assignedColor = color;
    }

    public Color GetColor()
    {
        return Color.HSVToRGB(storedHue, 0.8f, 1f);
    }

    public IEnumerator FlyTo(Vector3 target)
    {
        shouldRotate = false;

        Vector3 start = transform.position;
        float duration = 0.6f;
        float t = 0f;

        while (t < 1f)
        {
            t += Time.deltaTime / duration;
            transform.position = Vector3.Lerp(start, target, t);
            transform.localScale = Vector3.Lerp(Vector3.one, Vector3.one * 0.4f, t);
            yield return null;
        }

        Destroy(gameObject);
    }

    public void RotateToCross(float delay = 0.2f)
    {
        StartCoroutine(RotateAfterDelay(delay));
    }

    private IEnumerator RotateAfterDelay(float delay)
    {
        yield return new WaitForSeconds(delay);

        Quaternion startRot = transform.rotation;
        Quaternion targetRot = Quaternion.Euler(0, 0, 90f);
        float t = 0f;
        float duration = 0.4f;

        while (t < 1f)
        {
            t += Time.deltaTime / duration;
            transform.rotation = Quaternion.Slerp(startRot, targetRot, t);
            yield return null;
        }

        transform.rotation = targetRot;
    }
}
