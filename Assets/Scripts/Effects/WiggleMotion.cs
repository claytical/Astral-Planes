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

    // NEW: per-instance desync
    private float wigglePhaseOffset;
    private float driftPhaseOffset;
    private float driftFreqMul = 1f;
    private float driftAmpMul  = 1f;

    void Start()
    {
        rb = GetComponent<Rigidbody2D>();
        basePosition = transform.position;

        // deterministic-ish per-instance jitter
        int seed = GetInstanceID();
        Random.InitState(seed);
        wigglePhaseOffset = Random.value * 1000f;
        driftPhaseOffset  = Random.value * 1000f;
        driftFreqMul      = 0.9f + Random.value * 0.2f;  // 0.9 .. 1.1
        driftAmpMul       = 0.9f + Random.value * 0.3f;  // 0.9 .. 1.2
    }

    void FixedUpdate()
    {
        // 🎵 Angular wiggle (desynced phase)
        float torque = Mathf.Sin((Time.time + wigglePhaseOffset) * wiggleFrequency) * wiggleTorqueStrength;
        rb.AddTorque(torque);

        if (enableDrift)
        {
            // ✅ keep basePosition in sync with physics so we don't fight steering
            basePosition = rb.position;

            float t = (Time.time + driftPhaseOffset) * driftFrequency * driftFreqMul;
            float offsetX = Mathf.Sin(t) * driftAmplitude * driftAmpMul;
            float offsetY = Mathf.Cos(t * 1.1f) * driftAmplitude * driftAmpMul;

            rb.MovePosition(basePosition + new Vector3(offsetX, offsetY, 0f));
        }
    }
}