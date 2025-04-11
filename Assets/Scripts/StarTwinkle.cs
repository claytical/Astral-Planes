using UnityEngine;

public class StarTwinkle : MonoBehaviour
{
    public float twinkleSpeed = 2f;
    public float twinkleAmount = 0.05f;

    private Vector3 originalScale;

    void Start()
    {
        originalScale = transform.localScale;
    }

    void Update()
    {
        float phase = Time.time * twinkleSpeed + GetInstanceID() % 1000;
        float scale = 1f + Mathf.Sin(phase) * twinkleAmount;
        transform.localScale = originalScale * scale;
    }
}