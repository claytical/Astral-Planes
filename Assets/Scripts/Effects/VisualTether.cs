// 👇 Add this new script to your project
using UnityEngine;
using System.Collections;
public class VisualTether : MonoBehaviour
{
    public Transform target;
    public Vector3 targetPosition;
    public bool useStaticTarget = false;
    public float duration = 0.5f;
    public AnimationCurve travelCurve = AnimationCurve.EaseInOut(0, 0, 1, 1);
    public ParticleSystem travelParticles;
    public SpriteRenderer travelSprite;

    private Vector3 start;
    private float elapsed = 0f;

    public void SetTargetPosition(Vector3 pos)
    {
        targetPosition = pos;
        useStaticTarget = true;
    }

    void Start()
    {
        start = transform.position;
    }

    public void SetColor(Color _color)
    {

        travelSprite.color = _color;
        ParticleSystem.MainModule mainModule = travelParticles.main;
        mainModule.startColor = travelSprite.color;
    }
    void Update()
    {
        elapsed += Time.deltaTime;
        float t = Mathf.Clamp01(elapsed / duration);
        Vector3 end = useStaticTarget ? targetPosition : (target ? target.position : transform.position);
        transform.position = Vector3.Lerp(start, end, travelCurve.Evaluate(t));

        if (t >= 1f)
        {
            Destroy(gameObject);
        }
    }
}
