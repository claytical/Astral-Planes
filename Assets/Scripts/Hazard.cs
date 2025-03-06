using System.Collections;
using System.Collections.Generic;
using UnityEngine;


public class Hazard : MonoBehaviour
{
    
    public int damage;
    public GameObject spawnedFrom;

    // Use this for initialization



    void OnCollisionEnter2D(Collision2D coll)
    {
        Vehicle v = coll.gameObject.GetComponent<Vehicle>();
        if (v != null)
        {
            v.energyLevel -= damage;
            Debug.Log($"VEHICLE ENERGY LEVEL: {v.energyLevel}");
            LocalPlayer player = v.GetComponentInParent<LocalPlayer>();
            if (v.energyLevel <= 0)
            {
                if (player != null)
                {
                    Explode explode = v.GetComponent<Explode>();
                    if (explode != null)
                    {
                        explode.Permanent();
                    }

                    player.Restart();
                }
            }
            else
            {
                Debug.Log(("Taking on negative energy"));
                player.EnergyCollected(damage);
            }

            if (spawnedFrom != null)
            {
                Destroy(spawnedFrom);
            }

            Explode hazardExplosion = GetComponent<Explode>();
            if (hazardExplosion != null)
            {
                hazardExplosion.Permanent();
            }
            else
            {
                Destroy(gameObject);
            }
        }
    }
}

