using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using GameObject = UnityEngine.GameObject;

[System.Serializable]
public enum MineNodeSelectionMode
{
    WeightedRandom,
    RoundRobinUniqueFirst,
    QuotaBased
}

public class MinedObjectSpawnDirective
{
    public MinedObjectType minedObjectType;
    public MusicalRole role;
    public InstrumentTrack assignedTrack;
    public RemixUtility remixUtility;
    public NoteSetSeries noteSetSeries;
    public TrackModifierType trackModifierType;

    public Color displayColor;
    public GameObject prefab;
    public GameObject minedObjectPrefab;
    public Vector2Int spawnCell;
}

[System.Serializable]
public class WeightedMineNode
{
    public MinedObjectType minedObjectType;
    public TrackModifierType trackModifierType;
    public MusicalRole role;
    public int weight;
    public NoteSetSeries noteSetSeries;

    public int quota;

    public MinedObjectSpawnDirective ToDirective(InstrumentTrack track, Color color, MineNodePrefabRegistry nodeRegistry, MinedObjectPrefabRegistry objectRegistry)
    {
        Debug.Log($"Returning directive for {minedObjectType} / {trackModifierType}");

        RemixUtility remixUtil = null;

        var phaseManager = track.drumTrack.progressionManager;
        if (phaseManager != null)
        {
            int index = phaseManager.GetCurrentPhaseIndex();
            if (index >= 0 && index < phaseManager.phaseQueue.phaseGroups.Count)
            {
                var group = phaseManager.phaseQueue.phaseGroups[index];
                remixUtil = group.remixUtilities.FirstOrDefault(r => r.targetRole == track.assignedRole);
            }
        }



        return new MinedObjectSpawnDirective
        {
            minedObjectType = this.minedObjectType,
            role = this.role,
            assignedTrack = track,
            noteSetSeries = this.noteSetSeries,
            trackModifierType = this.trackModifierType,
            displayColor = color,
            minedObjectPrefab = objectRegistry.GetPrefab(minedObjectType, trackModifierType),
            prefab = nodeRegistry.GetPrefab(minedObjectType, trackModifierType),
            remixUtility = remixUtil // ✅ assign here
        };
    }

}

