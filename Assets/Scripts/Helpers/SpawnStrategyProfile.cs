using System.Collections.Generic;
using System.Linq;
using Gameplay.Mining;
using UnityEngine;

[CreateAssetMenu(fileName = "SpawnStrategyProfile", menuName = "Astral Planes/Spawn Strategy Profile")]
public class SpawnStrategyProfile : ScriptableObject
{
    public List<WeightedMineNode> mineNodes;
    public MineNodePrefabRegistry nodePrefabRegistry; // assign in Inspector
    public MinedObjectPrefabRegistry objectPrefabRegistry;
    public MineNodeSelectionMode selectionMode = MineNodeSelectionMode.WeightedRandom;

    private HashSet<WeightedMineNode> spawnedThisCycle = new();
    private Dictionary<WeightedMineNode, int> spawnCounts = new();

    public MinedObjectSpawnDirective GetMinedObjectDirective(InstrumentTrackController trackController)
    {
        WeightedMineNode selected = GetWeightedMineNode(trackController);
        if (selected == null) return null;

        InstrumentTrack track = trackController.FindTrackByRole(selected.role);
        if (track == null) return null;

        Color color = ShardColorUtility.RoleColor(selected.role);
        MinedObjectSpawnDirective directive =
            selected.ToDirective(track, color, nodePrefabRegistry, objectPrefabRegistry);
            Debug.Log($"Selected Role: {selected.role}, Directive Role: {directive.role}, Track: {track.name}, Directive Track: {directive.assignedTrack}, {directive.prefab}");
        return directive; // ✅ dual registries
    }


    private WeightedMineNode GetWeightedMineNode(InstrumentTrackController trackController)
    {
        List<WeightedMineNode> candidates = mineNodes
            .Where(n => IsDirectiveUseful(n, trackController, trackController.activeTrack.drumTrack.currentPhase.ToString()))
            .ToList();
        if (mineNodes == null || mineNodes.Count == 0)
        {
            Debug.LogError("❌ No mineNodes defined in SpawnStrategyProfile!");
            return null;
        }

        if (candidates.Count == 0) candidates = mineNodes;

        switch (selectionMode)
        {
            case MineNodeSelectionMode.WeightedRandom:
                int totalWeight = candidates.Sum(n => n.weight);
                int choice = Random.Range(0, totalWeight);
                int current = 0;
                foreach (var node in candidates)
                {
                    current += node.weight;
                    if (choice < current)
                        return node;
                }
                break;

            case MineNodeSelectionMode.RoundRobinUniqueFirst:
                var unused = candidates.Where(n => !spawnedThisCycle.Contains(n)).ToList();
                if (unused.Count == 0)
                {
                    spawnedThisCycle.Clear();
                    unused = candidates;
                }
                var rrChoice = unused[Random.Range(0, unused.Count)];
                spawnedThisCycle.Add(rrChoice);
                return rrChoice;

            case MineNodeSelectionMode.QuotaBased:
                var quotas = candidates.OrderBy(n =>
                {
                    int used = spawnCounts.GetValueOrDefault(n, 0);
                    return (float)used / Mathf.Max(n.weight, 1);
                }).ToList();
                var quotaChoice = quotas.First();
                spawnCounts.TryAdd(quotaChoice, 0);
                spawnCounts[quotaChoice]++;
                return quotaChoice;
        }

        return null;
    }

    private bool IsDirectiveUseful(WeightedMineNode node, InstrumentTrackController trackController, string phaseLabel)
    {
        var track = trackController?.tracks?.FirstOrDefault(t => t.assignedRole == node.role);
        if (track == null) return false;
        if (node.minedObjectType == MinedObjectType.NoteSpawner)
        {
            if (node.noteSetSeries == null || string.IsNullOrEmpty(node.noteSetSeries.label))
                return false;

            return node.noteSetSeries.label.Equals(phaseLabel, System.StringComparison.OrdinalIgnoreCase);

        }
        
        
        if (node.minedObjectType == MinedObjectType.TrackUtility)
            return IsUtilityRelevant(node.trackModifierType, track);

        return true;
    }

    private bool IsUtilityRelevant(TrackModifierType type, InstrumentTrack track)
    {
        return type switch
        {
            TrackModifierType.Clear => track.GetNoteDensity() > 0,
            TrackModifierType.Remix => track.GetNoteDensity() < 4,
            TrackModifierType.RootShift => true,
            TrackModifierType.ChordProgression => true,
            TrackModifierType.RhythmStyle => true,
            _ => true
        };
    }

    public void ResetSpawnState()
    {
        spawnedThisCycle.Clear();
        spawnCounts.Clear();
    }
}