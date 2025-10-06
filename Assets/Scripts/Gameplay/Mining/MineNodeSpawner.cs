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
            var profile = _drumTrack.phasePersonalityRegistry != null ? _drumTrack.phasePersonalityRegistry.Get(GameFlowManager.Instance.phaseTransitionManager.currentPhase) : null;
            var role    = directive.role != default ? directive.role : (directive.assignedTrack != null ? directive.assignedTrack.assignedRole : MusicalRole.Lead);
            var tuning  = node.GetComponent<MineNodeTuning>();
            if (tuning != null && profile != null) 
                tuning.Apply(profile, role, profile.personality);
            var rail = node.GetComponent<MineNodeRailAgent>();
            if (rail != null) { 
                rail.ApplySpeed(profile, role, GameFlowManager.Instance.phaseTransitionManager.currentPhase); 
                rail.ReplanToFarthest();
            } 
            var vis     = node.GetComponent<MineNodeCharacterVis>();
            if (vis != null) 
                vis.Configure(role, GameFlowManager.Instance.phaseTransitionManager.currentPhase); rail.SetTargetProvider(() => {
                    // Start from the node’s current cell and choose the farthest reachable passable cell.
                    Vector2Int start = _drumTrack.WorldToGridPosition(rail.transform.position);
                    return _drumTrack.FarthestReachableCellInComponent(start); // helper shown below
                });

                // Kick the first path immediately
            var start = _drumTrack.WorldToGridPosition(rail.transform.position); 
            var goal  = _drumTrack.FarthestReachableCellInComponent(start); 
            var path  = new List<Vector2Int>(); 
            if (GameFlowManager.Instance.activeDrumTrack.TryFindPath(start, goal, path))
                rail.SetPath(path);
        }
        GameFlowManager.Instance.activeDrumTrack.OccupySpawnGridCell(cell.x, cell.y, GridObjectType.Node);
        return node;
    }
    public void SetDrumTrack(DrumTrack drums)
    {
        _drumTrack = drums;
    }


}