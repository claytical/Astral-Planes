using UnityEngine;

public class SimpleGravity : MonoBehaviour {
    public float gravity = -9.8f; // Gravity force
    private float velocityY = 0f;
    public bool gravityEnabled = false;

    void Update() {
        if (gravityEnabled) {
            // Increase downward velocity based on gravity and time.
            velocityY += gravity * Time.deltaTime;
            // Apply the movement to the parent's position.
            transform.position += new Vector3(0, velocityY * Time.deltaTime, 0);
        }
    }

    public void ActivateGravity() {
        gravityEnabled = true;
    }
}