
using System.Collections;
using System.Collections.Generic;
using UnityEngine;


public class SpawnsObjects : MonoBehaviour
{
    public GameObject objectToSpawn;
    public GameObject spawningParticles;

    public enum SpawnType { TIME, MIDI}
    public SpawnType spawnType;
    [Header("Time Based Spawn Options")]

    public float timeBetweenSpawns = 5f;
    public float spawnedObjectLifetime = 10f; // Time in seconds before the spawned object is destroyed
    
    private ParticleSystem particles;
    private float nextSpawnTime;
    private GameObject so;

    private GameObject spawnedObject;
    
    [Header("MIDI Based Spawn Options")]
    public bool drumBased = false;
    public int matchValue = -1; // Match specific value (-1 means ignore)
    public float matchDuration = -1f; // Match specific duration (-1 means ignore)
    public int matchVelocity = -1; // Match specific velocity (-1 means ignore)


    // Start is called before the first frame update
    void Start()
    {
        if (spawningParticles)
        {
            spawningParticles = Instantiate(spawningParticles, transform);
            spawningParticles.transform.position = transform.position;
            particles = spawningParticles.GetComponent<ParticleSystem>();
        }

        if (objectToSpawn && timeBetweenSpawns > 0)
        {
            // Set the initial spawn time
            nextSpawnTime = Time.time + timeBetweenSpawns;
        }


    }
    // Update is called once per frame
    void FixedUpdate()
    {
        if (spawnType == SpawnType.TIME)
        {
            if (timeBetweenSpawns > 0 && Time.time >= nextSpawnTime)
            {
                // Spawn the object if the spawn time is met and no object is currently active
                if (!so)
                {
                    SpawnObject();
                }

                // Set the next spawn time
                nextSpawnTime = Time.time + timeBetweenSpawns;
            }
        }
    }

    public void StartParticles()
    {
        if (particles)
        {
            particles.Play();
        }
    }

    public void SpawnObject()
    {
        if (particles)
        {
            particles.Stop();
        }

        // Create a new object
        so = Instantiate(objectToSpawn, transform.parent);
        so.transform.position = transform.position;

        // Add the SpawnedObject component and set its lifetime
        SpawnedObject spawnedObject = so.AddComponent<SpawnedObject>();
        spawnedObject.SetLifeTime(spawnedObjectLifetime, gameObject);

        Debug.Log("New object spawned: " + so.name);

        // Reactivate particles if needed
        StartParticles();
    }

    // Optionally, handle object collection or destruction
    public void OnObjectCollectedOrDestroyed()
    {
        so = null; // Reset the reference to allow spawning the next object
    }
}
