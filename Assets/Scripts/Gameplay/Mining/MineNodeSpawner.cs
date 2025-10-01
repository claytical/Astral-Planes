using UnityEngine;
using System.Collections.Generic;

public class MineNodeSpawner : MonoBehaviour
{
    public GameObject nodePrefab;
    public Color resolvedColor;
    private GameObject _spawnedNode;
    private Vector2Int _gridPosition;
    private DrumTrack _drumTrack;
    
    void Start()
    {
        SpriteRenderer childRenderer = GetComponentInChildren<SpriteRenderer>();
        if (childRenderer != null)
        {
            childRenderer.color = resolvedColor;
        }
    }
    public MineNode SpawnNode(Vector2Int cell, MinedObjectSpawnDirective directive)
    {
        _gridPosition = cell;
        Vector3 worldPos = _drumTrack.GridToWorldPosition(cell);
    
        GameObject obj = Instantiate(nodePrefab, worldPos, Quaternion.identity);
        _spawnedNode = obj;
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
                    Vector2Int start = _drumTrack.WorldToGridPosition(rail.transform.position);
                    return _drumTrack.FarthestReachableCellInComponent(start); // helper shown below
                });

                // Kick the first path immediately
                var start = _drumTrack.WorldToGridPosition(rail.transform.position);
                var goal  = _drumTrack.FarthestReachableCellInComponent(start);
                var path  = new List<Vector2Int>();
                if (_drumTrack.TryFindPath(start, goal, path))
                    rail.SetPath(path);
            }

            _drumTrack.OccupySpawnGridCell(cell.x, cell.y, GridObjectType.Node);
        }
        else
        {
            Debug.LogError("🚨 nodePrefab is missing a MineNode component.");
        }
        return node;
    }
    public void SetDrumTrack(DrumTrack drums)
    {
        _drumTrack = drums;
    }


}