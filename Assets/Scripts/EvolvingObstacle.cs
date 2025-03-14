using UnityEngine;
using System;
public enum ObstacleState
{
    Block,
    Hazard,
    Explosion,
    DrumCollectable
}

public class EvolvingObstacle : MonoBehaviour
{
    public GameObject explosionPrefab;
    public GameObject debrisPrefab;
    public ParticleSystem particles;
    
    public GameObject obstaclePrefab; // ✅ Assign Block Obstacle Prefab in Inspector
    private GameObject spawnedObstacle;
    private Vector2Int gridPosition;
    private DrumTrack drumTrack;

    public static event Action<GameObject> OnObstacleDestroyed;

    public void SetDrumTrack(DrumTrack drums)
    {
        drumTrack = drums;
    }

    public void SpawnObstacle(Vector2Int position)
    {
        gridPosition = position;
        Vector3 worldPos = drumTrack.GridToWorldPosition(gridPosition);
        spawnedObstacle = Instantiate(obstaclePrefab, worldPos, Quaternion.identity);

        // Assign position tracking to the spawned obstacle
        Obstacle obsScript = spawnedObstacle.GetComponent<Obstacle>();
        if (obsScript != null)
        {
            obsScript.SetParentEvolvingObstacle(this); // ✅ Link to this EvolvingObstacle
            obsScript.SetDrumTrack(drumTrack);
        }

        drumTrack.spawnGrid.OccupyCell(gridPosition.x, gridPosition.y, GridObjectType.Obstacle);
    }

    public void OnObstacleKnockedLoose()
    {
        Debug.Log($"Obstacle at {gridPosition} knocked loose, removing EvolvingObstacle.");
        drumTrack.spawnGrid.FreeCell(gridPosition.x, gridPosition.y);
        drumTrack.activeObstacles.Remove(gameObject);
        Destroy(gameObject); // ✅ Remove this EvolvingObstacle from the game
    }
    private void TriggerExplosion()
    {
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