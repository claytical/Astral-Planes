using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Collectable : MonoBehaviour
{
    public int amount = 1;
    public delegate void OnCollectedHandler();
    public event OnCollectedHandler OnCollected;
    private void OnTriggerEnter2D(Collider2D coll)
    {
        Debug.Log($"Collision detected between {gameObject.transform.parent.name} and {coll.gameObject.transform.parent.name}");

        if (coll.gameObject.GetComponent<Vehicle>())
        {
            coll.gameObject.GetComponent<Vehicle>().CollectEnergy(amount);
            OnCollected?.Invoke(); // Trigger the event
            var explode = GetComponent<Explode>();
            if (explode != null)
            {
                explode.Permanent();  // Destroy the collectable with an effect
            }
        }
    }
}
