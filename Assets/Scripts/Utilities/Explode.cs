using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;


public class Explode : MonoBehaviour
{
    public GameObject explosion;
    public float lifetime;
    public bool randomizeLifetimer = false;
    private float explosionTimer;

    void Start()
    {
        if (randomizeLifetimer)
        {
            explosionTimer = Time.time + Random.Range(0f, lifetime);
        }
        else
        {
            explosionTimer = Time.time + lifetime;
        }
    }

    void Update()
    {
        if (lifetime > 0)
        {
            if (Time.time > explosionTimer)
            {
                Permanent();
                lifetime = 0;
            }
        }
    }

    public void ApplyLifetimeProfile(LifetimeProfile profile)
    {
        if (profile == null) return;

        lifetime = profile.lifetime;
        randomizeLifetimer = profile.randomizeLifetime;

        if (randomizeLifetimer)
        {
            explosionTimer = Time.time + Random.Range(0f, lifetime);
        }
        else
        {
            explosionTimer = Time.time + lifetime;
        }
    }

    public void DelayedExplosion(float delay = 0.1f)
    {
        Invoke(nameof(Permanent), delay);
    }
    public void Permanent()
    {
        if (GetComponent<Vehicle>())
        { 
            Destroy(gameObject, 1);
            return;
        }

        if (GetComponent<Rigidbody2D>())
        {
            GetComponent<Rigidbody2D>().linearVelocity = Vector2.zero;
        }

        if (explosion != null)
        {
            Instantiate(explosion, transform.position, Quaternion.identity);
        }
        Destroy(this.gameObject);
    }


}
