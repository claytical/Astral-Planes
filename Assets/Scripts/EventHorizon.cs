using UnityEngine;

public class EventHorizon : MonoBehaviour
{
    public int damage = 10;
    public Hazard parentHazard;

    void Start()
    {
        if (transform.parent != null)
            parentHazard = transform.parent.GetComponent<Hazard>();
    }

    void OnTriggerEnter2D(Collider2D coll)
    {
        Vehicle v = coll.GetComponent<Vehicle>();
        if (v != null)
        {
            v.energyLevel -= damage;
            if (v.energyLevel <= 0)
            {
                v.Explode();
            }
        }
    }
}