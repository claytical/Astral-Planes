using System;
using System.Collections.Generic;
using System.Linq;
using Gameplay.Mining;
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

    [Range(0, 100)]
    public int weight = 1;

    public NoteSetSeries noteSetSeries;

    public int quota = 1;

    [Tooltip("Leave empty for all phases")]
    public List<MusicalPhase> allowedPhases;

    [Tooltip("Higher = rarer")]
    public int rarityTier = 0;

    [Tooltip("If true, requires player to have collected at least one remix")]
    public bool requiresRemixToSpawn = false;

    public MinedObjectSpawnDirective ToDirective(
        InstrumentTrack track,
        Color color,
        MineNodePrefabRegistry nodeRegistry,
        MinedObjectPrefabRegistry objectRegistry)
    {
        Debug.Log($"🧭 Spawning directive for {minedObjectType} / {trackModifierType}");

        RemixUtility remixUtil = null;

        var phaseManager = track?.drumTrack?.progressionManager;
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
            remixUtility = remixUtil
        };
    }

    public override string ToString()
    {
        return $"{role} | {minedObjectType} [{trackModifierType}] | weight: {weight}, quota: {quota}, rarity: {rarityTier}";
    }
}
