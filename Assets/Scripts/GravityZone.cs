using UnityEngine;

public class GravityZone : MonoBehaviour
{
    public float pullForce = 5f;
    public float maxGravitySize = 5f;
    public float pullSmoothness = 0.05f; // ✅ Controls how smoothly vehicles are pulled
    private Vehicle vehicle;
    private Hazard parentHazard;

    void Start()
    {
        if (transform.parent != null)
            parentHazard = transform.parent.GetComponent<Hazard>();
    }

    void OnTriggerStay2D(Collider2D coll)
    {
        if (vehicle == null)
        {
            vehicle = coll.GetComponent<Vehicle>();
        }
        else 
        {
            // ✅ Introduce random input distortion
            vehicle.ApplyControlDistortion();
        
            // ✅ Drain energy gradually
            float energyDrained = Time.deltaTime * 3;
            vehicle.energyLevel = Mathf.Max(0, vehicle.energyLevel - energyDrained);
            parentHazard.AbsorbEnergy(energyDrained);
        }
    }

}