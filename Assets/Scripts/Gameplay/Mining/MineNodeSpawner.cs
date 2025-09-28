using UnityEngine;
using System.Collections.Generic;

public class MineNodeSpawner : MonoBehaviour
{
    public ParticleSystem particles;
    
    public GameObject nodePrefab;
    private GameObject spawnedNode;
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
    public MineNode SpawnNode(Vector2Int cell, MinedObjectSpawnDirective directive)
    {
        gridPosition = cell;
        Vector3 worldPos = drumTrack.GridToWorldPosition(cell);
    
        GameObject obj = Instantiate(nodePrefab, worldPos, Quaternion.identity);
        spawnedNode = obj;
        Debug.Log($"[MineNodeSpawner] SpawnNode -> '{obj.name}' ({obj.GetInstanceID()}) at {worldPos}, parent={obj.transform.parent?.name}");
        
        var node = obj.GetComponent<MineNode>();

        if (node != null)
        {
            Debug.Log($"Initializing with role:{directive.role} assignedTrack:{directive.assignedTrack} roleprofile:{directive.roleProfile} noteset:{directive.noteSet}");
            node.Initialize(directive); 
            var rail = node.GetComponent<MineNodeRailAgent>();
            if (rail != null)
            {
                rail.SetTargetProvider(() =>
                {
                    // Start from the node’s current cell and choose the farthest reachable passable cell.
                    Vector2Int start = drumTrack.WorldToGridPosition(rail.transform.position);
                    return drumTrack.FarthestReachableCellInComponent(start); // helper shown below
                });

                // Kick the first path immediately
                var start = drumTrack.WorldToGridPosition(rail.transform.position);
                var goal  = drumTrack.FarthestReachableCellInComponent(start);
                var path  = new List<Vector2Int>();
                if (drumTrack.TryFindPath(start, goal, path))
                    rail.SetPath(path);
            }

            drumTrack.OccupySpawnGridCell(cell.x, cell.y, GridObjectType.Node);
        }
        else
        {
            Debug.LogError("🚨 nodePrefab is missing a MineNode component.");
        }
        return node;
    }


}