using UnityEngine;

public class GravityZone : MonoBehaviour
{
    private Vehicle vehicle;
    public float pullForce = 500f; // Adjust force strength
    public int damage = 1; // Adjust damage amount
    public Transform centerPoint; // Assign in inspector or find in Start()
    public Hazard parentHazard; // Ensure this reference is correctly assigned

    private void Start()
    {
        if (centerPoint == null)
        {
            centerPoint = transform; // Defaults to its own position if not assigned
        }
    }

    void OnTriggerEnter2D(Collider2D collision)
    {
        Debug.Log($"{collision.gameObject.name} collided with {gameObject.name}");

        vehicle = collision.gameObject.GetComponent<Vehicle>();

        if (vehicle != null)
        {
            ApplyGravityPull(vehicle);
            ApplyEffects(vehicle);
            transform.Rotate(0, 0, 45); // Rotate the zone 180 degrees ( counter-clockwise)
        }
    }

    private void ApplyGravityPull(Vehicle vehicle)
    {
        Rigidbody2D rb = vehicle.GetComponent<Rigidbody2D>();
        if (rb != null)
        {
            Vector2 forceDirection = (centerPoint.position - vehicle.transform.position).normalized;
            rb.AddForce(forceDirection * pullForce, ForceMode2D.Impulse);
            Debug.Log($"{vehicle.name} pulled toward {centerPoint.position} with force {pullForce}");
        }
    }

    private void ApplyEffects(Vehicle vehicle)
    {
        vehicle.energyLevel = Mathf.Max(0, vehicle.energyLevel - damage);
        vehicle.UpdateEnergyUI();
        parentHazard.AbsorbEnergy(damage);
        Debug.Log($"{vehicle.name} lost {damage} energy due to collision with {gameObject.name}");
    }
}