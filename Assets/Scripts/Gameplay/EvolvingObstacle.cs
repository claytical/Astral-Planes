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
    public ParticleSystem particles;
    
    public GameObject obstaclePrefab; // ✅ Assign Block Obstacle Prefab in Inspector
    private GameObject spawnedObstacle;
    private Vector2Int gridPosition;
    private DrumTrack drumTrack;

    public static event Action<GameObject> OnObstacleDestroyed;

    public void SetDrumTrack(DrumTrack drums)
    {
        Debug.Log($"Setting DrumTrack for EvolvingObstacle {gameObject.name}");
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
            drumTrack.OccupySpawnGridCell(gridPosition.x, gridPosition.y, GridObjectType.Obstacle);
        }
        DrumLoopCollectable collectable = spawnedObstacle.GetComponent<DrumLoopCollectable>();
        if (collectable != null)
        {
            collectable.SetTracks(drumTrack);
            Explode explode = spawnedObstacle.GetComponent<Explode>();
            if (explode != null)
            {
                explode.Permanent();
            }
        }


    }

    public void OnObstacleKnockedLoose()
    {
        Debug.Log($"Obstacle at {gridPosition} knocked loose, removing EvolvingObstacle.");
        drumTrack.FreeSpawnCell(gridPosition.x, gridPosition.y);
        drumTrack.RemoveActiveObstacle(gameObject);
        Destroy(gameObject); // ✅ Remove this EvolvingObstacle from the game
    }

}