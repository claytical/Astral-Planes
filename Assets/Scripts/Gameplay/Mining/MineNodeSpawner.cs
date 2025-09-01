using UnityEngine;
using System;

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
        var node = obj.GetComponent<MineNode>();
        if (node != null)
        {
            node.Initialize(directive);
            var gaits = node.GetComponent<MineNodeGaits>();
            if (gaits != null)
            {
                gaits.SetPhaseProvider(() => drumTrack.progressionManager.GetCurrentPhaseName());
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