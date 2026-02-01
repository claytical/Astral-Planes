using UnityEngine;
using UnityEngine.UIElements;

public class SpinObject : MonoBehaviour
{
    public Vector3 rotationVector = Vector3.zero;
    // Update is called once per frame
    void Update()
    {
        transform.Rotate(rotationVector);
    }
}
