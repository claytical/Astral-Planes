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

    private Rigidbody2D _rb;
    private Vector3 _basePosition;

    // NEW: per-instance desync
    private float _wigglePhaseOffset;
    private float _driftPhaseOffset;
    private float _driftFreqMul = 1f;
    private float _driftAmpMul  = 1f;

    void Start()
    {
        _rb = GetComponent<Rigidbody2D>();
        _basePosition = transform.position;

        // deterministic-ish per-instance jitter
        int seed = GetInstanceID();
        Random.InitState(seed);
        _wigglePhaseOffset = Random.value * 1000f;
        _driftPhaseOffset  = Random.value * 1000f;
        _driftFreqMul      = 0.9f + Random.value * 0.2f;  // 0.9 .. 1.1
        _driftAmpMul       = 0.9f + Random.value * 0.3f;  // 0.9 .. 1.2
    }

    void FixedUpdate()
    {
        // 🎵 Angular wiggle (desynced phase)
        float torque = Mathf.Sin((Time.time + _wigglePhaseOffset) * wiggleFrequency) * wiggleTorqueStrength;
        _rb.AddTorque(torque);

        if (enableDrift)
        {
            // ✅ keep basePosition in sync with physics so we don't fight steering
            _basePosition = _rb.position;

            float t = (Time.time + _driftPhaseOffset) * driftFrequency * _driftFreqMul;
            float offsetX = Mathf.Sin(t) * driftAmplitude * _driftAmpMul;
            float offsetY = Mathf.Cos(t * 1.1f) * driftAmplitude * _driftAmpMul;

            _rb.MovePosition(_basePosition + new Vector3(offsetX, offsetY, 0f));
        }
    }
}