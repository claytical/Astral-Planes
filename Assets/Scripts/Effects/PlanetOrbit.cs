using UnityEngine;

public class PlanetOrbit : MonoBehaviour
{
    public Transform center;
    public float orbitRadius = 1f;
    public float orbitSpeed = 10f;

    private float angle;

    void Start()
    {
        angle = Random.Range(0f, 360f);
    }

    void Update()
    {
        angle += orbitSpeed * Time.deltaTime;
        float rad = angle * Mathf.Deg2Rad;
        transform.position = center.position + new Vector3(Mathf.Cos(rad), Mathf.Sin(rad), 0f) * orbitRadius;
    }
}