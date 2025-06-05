// DiamondVisual.cs — Updated for Ghost mode isolation

using UnityEngine;
using System.Collections;

public class DiamondVisual : MonoBehaviour
{
    public SpriteRenderer sprite;
    public Transform visualTarget; // Assign visualOnly from DiamondGhost
    public float spinSpeed = 90f;
    public float baseScale = 1f;
    public float pulseAmplitude = 0.1f;
    public float pulseSpeed = 2f;
    private float hueOffset;
    private bool shouldRotate = true;
    private float storedHue;
    public Color assignedColor;
    public DiamondVisualMode mode = DiamondVisualMode.Normal;

    public enum DiamondVisualMode
    {
        Normal,
        Ghost
    }

    void Awake()
    {
        if (visualTarget == null) visualTarget = transform;
        hueOffset = Random.Range(0f, 2f * Mathf.PI);
    }

    void Update()
    {
        switch (mode)
        {
            case DiamondVisualMode.Normal:
                RunNormalBehavior(); break;
            case DiamondVisualMode.Ghost:
                RunGhostBehavior(); break;
        }
    }

    private void RunNormalBehavior()
    {
        if (shouldRotate)
        {
            visualTarget.Rotate(Vector3.forward, spinSpeed * Time.deltaTime);
        }

        float scale = baseScale + Mathf.Sin(Time.time * pulseSpeed) * pulseAmplitude;
        visualTarget.localScale = Vector3.one * scale;

        Color c = assignedColor;
        c.a = 0.3f + 0.2f * Mathf.Sin(Time.time * 2f);
        sprite.color = c;
    }

    private void RunGhostBehavior()
    {
        Color colorA = new Color(2f, 0.3f, 0.3f, 1f);
        Color colorB = new Color(1f, 1f, 0.4f, 1f);
        float flicker = Mathf.PingPong(Time.time * 24f + hueOffset, 1f);
        sprite.color = Color.Lerp(colorA, colorB, flicker);

        float twitch = baseScale * (1f + Mathf.Sign(Mathf.Sin(Time.time * 25f)) * 0.1f);
        visualTarget.localScale = Vector3.one * twitch;

        if (shouldRotate)
        {
            float spin = Mathf.Sin(Time.time * 15f + hueOffset) * spinSpeed;
            visualTarget.Rotate(Vector3.forward, spin * Time.deltaTime);
        }
    }

    public void SetAssignedColor(Color color)
    {
        assignedColor = color;
        sprite.color = color;
        storedHue = 0f;
    }

    public void RotateToCross(float delay = 0.2f)
    {
        StartCoroutine(RotateAfterDelay(delay));
    }

    private IEnumerator RotateAfterDelay(float delay)
    {
        yield return new WaitForSeconds(delay);

        Quaternion startRot = visualTarget.rotation;
        Quaternion targetRot = Quaternion.Euler(0, 0, 90f);
        float t = 0f;
        float duration = 0.4f;

        while (t < 1f)
        {
            t += Time.deltaTime / duration;
            visualTarget.rotation = Quaternion.Slerp(startRot, targetRot, t);
            yield return null;
        }

        visualTarget.rotation = targetRot;
    }
}