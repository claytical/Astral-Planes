using UnityEngine;

public class PlanetOrbit : MonoBehaviour
{
    public Transform center;
    public float orbitRadius = 1f;
    public float orbitSpeed = 10f;

    private float _angle;

    void Start()
    {
        _angle = Random.Range(0f, 360f);
    }

    void Update()
    {
        _angle += orbitSpeed * Time.deltaTime;
        float rad = _angle * Mathf.Deg2Rad;
        transform.position = center.position + new Vector3(Mathf.Cos(rad), Mathf.Sin(rad), 0f) * orbitRadius;
    }
}