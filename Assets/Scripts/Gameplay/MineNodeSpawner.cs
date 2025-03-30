using UnityEngine;
using System;

public class MineNodeSpawner : MonoBehaviour
{
    public ParticleSystem particles;
    
    public GameObject nodePrefab; // ✅ Assign Block Obstacle Prefab in Inspector
    private GameObject spawnedNode;
    private Vector2Int gridPosition;
    private DrumTrack drumTrack;

    public static event Action<GameObject> OnObstacleDestroyed;

    public void SetDrumTrack(DrumTrack drums)
    {
        Debug.Log($"Setting DrumTrack for EvolvingObstacle {gameObject.name}");
        drumTrack = drums;
    }

    public void SpawnNode(Vector2Int position)
    {
        gridPosition = position;
        Vector3 worldPos = drumTrack.GridToWorldPosition(gridPosition);
        spawnedNode = Instantiate(nodePrefab, worldPos, Quaternion.identity);

        MineNode obsScript = spawnedNode.GetComponent<MineNode>();
        if (obsScript != null)
        {
            obsScript.SetParentEvolvingObstacle(this);
            obsScript.SetDrumTrack(drumTrack);
            drumTrack.OccupySpawnGridCell(gridPosition.x, gridPosition.y, GridObjectType.Node);
        }

        // Handle drum loop collectables
        DrumLoopCollectable collectable = spawnedNode.GetComponent<DrumLoopCollectable>();
        if (collectable != null)
        {
            collectable.SetDrums(drumTrack);
            Explode explode = spawnedNode.GetComponent<Explode>();
            if (explode != null) explode.Permanent();
        }
        
    }

    public void OnObstacleKnockedLoose()
    {
        Debug.Log($"Obstacle at {gridPosition} knocked loose, removing EvolvingObstacle.");
        drumTrack.FreeSpawnCell(gridPosition.x, gridPosition.y);
        drumTrack.RemoveActiveMineNode(gameObject);
        Destroy(gameObject); // ✅ Remove this EvolvingObstacle from the game
    }

}