using UnityEngine;

public class TrailColorAssigner : MonoBehaviour
{
    private TrailRenderer trail;
    private Color baseColor;

    void Start()
    {
        trail = GetComponent<TrailRenderer>();
        if (trail != null)
        {
            baseColor = GetComponent<SpriteRenderer>().color;
            if (baseColor != Color.white)
            {
                Color start = baseColor;
                start.a = 0.3f;
                Color end = baseColor;
                end.a = 0f;
                trail.startColor = start;
                trail.endColor = end;
            }
        }
    }
}
