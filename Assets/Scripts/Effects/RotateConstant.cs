using UnityEngine;

public enum RotationMode
{
    Uniform,
    Swirl,
    SpiralOutward,
    Randomized
}

public class RotateConstant : MonoBehaviour
{

    public RotationMode rotationMode = RotationMode.Uniform;
    public float baseSpeed = 1f;
    public int shardIndex = 0;
    public int totalShards = 1;

    private Vector3 accelerate;
    private float pulseAngle = 30f;
    private float pulseDuration = 0.2f;
    private float pulseTimer = 0f;
    private bool pulsing = false;
    private int pulseDirection = 1;
    
    void Start()
    {
        ApplyRotationSettings();
    }

    void Update()
    {
        if (pulsing)
        {
            pulseTimer += Time.deltaTime;
            float t = pulseTimer / pulseDuration;
            float eased = Mathf.Sin(t * Mathf.PI); // smooth in-out
            float angle = pulseDirection * pulseAngle * eased;
            transform.localRotation = Quaternion.Euler(0, 0, angle);

            if (pulseTimer >= pulseDuration)
            {
                pulsing = false;
            }
        }
        else
        {
            transform.Rotate(accelerate * Time.deltaTime);
        }
    }
    public void ApplyRotationSettings()
    {
        switch (rotationMode)
        {
            case RotationMode.Uniform:
                accelerate = new Vector3(0, 0, baseSpeed);
                break;

            case RotationMode.Swirl:
                accelerate = new Vector3(0, 0, (shardIndex % 2 == 0 ? baseSpeed : -baseSpeed));
                break;

            case RotationMode.SpiralOutward:
                float offset = Mathf.Lerp(-baseSpeed, baseSpeed, shardIndex / (float)Mathf.Max(1, totalShards - 1));
                accelerate = new Vector3(0, 0, offset);
                break;

            case RotationMode.Randomized:
            default:
                accelerate = new Vector3(Random.Range(-baseSpeed, baseSpeed), Random.Range(-baseSpeed, baseSpeed), Random.Range(-baseSpeed, baseSpeed));
                break;
        }
    }

}