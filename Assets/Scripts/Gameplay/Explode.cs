using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

public class Explode : MonoBehaviour
{
    public GameObject explosion;
    public float respawnTimer = 10f;
    public float lifetime;
    public bool randomizeLifetimer = false;
    private float explosionTimer;
    private Vector3 originalPosition;
    private Quaternion originalRotation;
    private Vector3 originalScale;

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
        originalPosition = transform.position;
        originalRotation = transform.rotation;
        originalScale = transform.localScale;
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


    public void UntilNextSet()
    {
        Instantiate(explosion, transform.position, Quaternion.identity);
        gameObject.SetActive(false);
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

        Instantiate(explosion, transform.position, Quaternion.identity);
        Destroy(this.gameObject);
    }


}
