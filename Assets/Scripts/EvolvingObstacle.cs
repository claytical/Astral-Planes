using UnityEngine;
using System;
public class EvolvingObstacle : MonoBehaviour
{
    public enum ObstacleState
    {
        Block,
        Hazard,
        Explosion,
        DrumCollectable
    }

    public ObstacleState currentState = ObstacleState.Block;
    public GameObject[] evolutionPrefab;
    public GameObject explosionPrefab;
    public GameObject debrisPrefab;
    public ParticleSystem particles;
    
    private int evolutionCounter = 0;
    private bool isEvolving = false;
    private DrumTrack drumTrack;
    private GameObject evolvingObstacle;
    public static event Action<GameObject> OnObstacleDestroyed;

    public void SetDrumTrack(DrumTrack drums)
    {
        drumTrack = drums;
    }
    public void Evolve()
    {
        if (isEvolving) return; // Prevent multiple evolutions at once
        isEvolving = true;
        particles.Stop();
        TriggerExplosion();
        if (evolvingObstacle != null)
        {
            Explode explode = evolvingObstacle.GetComponent<Explode>();
            if (explode != null)
            {
                explode.Permanent();
            }
            else
            {
                Destroy(evolvingObstacle);
            }

        }
        if (evolutionCounter >= evolutionPrefab.Length)
        {
            Destroy(this);
        }
        else
        {
            evolvingObstacle = Instantiate(evolutionPrefab[evolutionCounter], transform.position, Quaternion.identity);
            DrumLoopCollectable dlc = evolvingObstacle.GetComponent<DrumLoopCollectable>();
            if (dlc != null)
            {
                Debug.Log("Setting Track and New Pattern");
                dlc.SetTrack(drumTrack);
            }
        }

        evolutionCounter++;
        isEvolving = false;
    }

    private void TriggerExplosion()
    {
        currentState = ObstacleState.Explosion;
        Instantiate(explosionPrefab, transform.position, Quaternion.identity);
        
        // Release physics objects (debris)
        for (int i = 0; i < 5; i++)
        {
            GameObject debris = Instantiate(debrisPrefab, transform.position + (Vector3)UnityEngine.Random.insideUnitCircle * 0.5f, Quaternion.identity);
            Rigidbody2D rb = debris.GetComponent<Rigidbody2D>();
            if (rb) rb.AddForce(UnityEngine.Random.insideUnitCircle * 5f, ForceMode2D.Impulse);
        }
    }
}