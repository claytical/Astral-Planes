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

    void Start()
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
                accelerate = new Vector3(0, 0, Random.Range(-baseSpeed, baseSpeed));
                break;
        }
    }

    void Update()
    {
        transform.Rotate(accelerate * Time.deltaTime);
    }
}