using UnityEngine;
using System;

public class MineNodeSpawner : MonoBehaviour
{
    public ParticleSystem particles;
    
    public GameObject nodePrefab; // ✅ Assign Block Obstacle Prefab in Inspector
    private GameObject spawnedNode;
    public GameObject SpawnedNode => spawnedNode;
    private Vector2Int gridPosition;
    private DrumTrack drumTrack;
    public Color resolvedColor;
    
    void Start()
    {
        SpriteRenderer childRenderer = GetComponentInChildren<SpriteRenderer>();
        if (childRenderer != null)
        {
            childRenderer.color = resolvedColor;
            
        }

    }
   
    public void SetDrumTrack(DrumTrack drums)
    {
        drumTrack = drums;
    }

    public void SpawnNode(Vector2Int position)
    {
        gridPosition = position;
        Vector3 worldPos = drumTrack.GridToWorldPosition(gridPosition);
        if (nodePrefab == null)
        {
            Debug.LogError("No node prefab assigned on " + gameObject.name);
        }
        spawnedNode = Instantiate(nodePrefab, worldPos, Quaternion.identity);

        MineNode nodeScript = spawnedNode.GetComponent<MineNode>();
            
        if (nodeScript != null)
        {
            resolvedColor = ShardColorUtility.ResolveColorFromMineNodePrefab(nodePrefab, drumTrack.trackController);
            drumTrack.RegisterMineNode(nodeScript);
            nodeScript.SetParentEvolvingObstacle(this);
            nodeScript.SetDrumTrack(drumTrack);
            drumTrack.OccupySpawnGridCell(gridPosition.x, gridPosition.y, GridObjectType.Node);  
            nodeScript.LockColor(resolvedColor);
        }
        else
        {
            Debug.LogError("No MineNode script assigned on " + gameObject.name);
        }
        
    }

    public void OnObstacleKnockedLoose()
    {
        
        drumTrack.FreeSpawnCell(gridPosition.x, gridPosition.y);
        drumTrack.RemoveActiveMineNode(gameObject);
        Destroy(gameObject); // ✅ Remove this EvolvingObstacle from the game
    }

}