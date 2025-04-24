using UnityEngine;

[RequireComponent(typeof(Rigidbody2D))]
public class WiggleMotion : MonoBehaviour
{
    [Header("Rotation Wiggle")]
    public float wiggleTorqueStrength = 1f;
    public float wiggleFrequency = 2f;

    [Header("Position Drift (optional)")]
    public bool enableDrift = false;
    public float driftAmplitude = 0.05f;
    public float driftFrequency = 1f;

    private Rigidbody2D rb;
    private Vector3 basePosition;

    void Start()
    {
        rb = GetComponent<Rigidbody2D>();
        basePosition = transform.position;
    }

    void FixedUpdate()
    {
        // 🎵 Angular wiggle
        float torque = Mathf.Sin(Time.time * wiggleFrequency) * wiggleTorqueStrength;
        rb.AddTorque(torque);

        // ✨ Optional position wiggle (small drifting like it's vibrating)
        if (enableDrift)
        {
            float offsetX = Mathf.Sin(Time.time * driftFrequency) * driftAmplitude;
            float offsetY = Mathf.Cos(Time.time * driftFrequency * 1.1f) * driftAmplitude;
            rb.MovePosition(basePosition + new Vector3(offsetX, offsetY, 0f));
        }
    }
}