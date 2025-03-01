using UnityEngine;

public class SimpleGravity : MonoBehaviour {
    private float velocityY = 0f;
    private float gravity = -9.8f; // Default gravity force
    public bool gravityEnabled = false;

    void Update() {
        if (gravityEnabled) {
            velocityY += gravity * Time.deltaTime;
            transform.position += new Vector3(0, velocityY * Time.deltaTime, 0);

            Debug.Log($"{gameObject.name} - Gravity Enabled: {gravityEnabled}, Velocity: {velocityY}, Position Y: {transform.position.y}");
        }
    }

    public void ActivateGravity(float g)
    {
        gravity = g;
        velocityY = -0.1f; // Ensure downward motion starts
        gravityEnabled = true;
        Debug.Log($"{gameObject.name} - Gravity Activated with {gravity}, Initial Velocity: {velocityY}");
    }
}