using System.Collections.Generic;
using System.Linq;
using UnityEngine;

[System.Serializable]
public enum MineNodeSelectionMode
{
    WeightedRandom,
    RoundRobinUniqueFirst,
    QuotaBased
}

[System.Serializable]
public class WeightedMineNode
{
    public GameObject prefab;
    public int weight = 1;
}

public class MineNodeSpawnerSet : MonoBehaviour
{
    public List<WeightedMineNode> mineNodes;
    public MineNodeSelectionMode selectionMode = MineNodeSelectionMode.WeightedRandom;

    private HashSet<GameObject> spawnedThisCycle = new();     // Used by RoundRobin
    private Dictionary<GameObject, int> spawnCounts = new();   // Used by Quota
    public InstrumentTrackController trackController; // Set this via script or inspector

    public GameObject GetMineNode()
    {
        var filtered = mineNodes.Where(n => IsPrefabUseful(n.prefab)).ToList();
        if (filtered.Count == 0) filtered = mineNodes; // fallback
        switch (selectionMode)
        {
            case MineNodeSelectionMode.RoundRobinUniqueFirst:
                return GetRoundRobinNode();
            case MineNodeSelectionMode.QuotaBased:
                return GetQuotaBasedNode();
            case MineNodeSelectionMode.WeightedRandom:
            default:
                return GetWeightedRandomNode();
        }
    }
    private bool IsPrefabUseful(GameObject prefab)
    {
        var utility = prefab.GetComponent<TrackUtilityMinedObject>();
        if (utility == null) return true;

        var roleTrack = FindTrackForRole(utility.targetRole); // you may need to inject controller
        return roleTrack != null && IsUtilityRelevant(utility.type, roleTrack);
    }

    private GameObject GetWeightedRandomNode()
    {
        var useful = FilterUsefulNodes();
        if (useful.Count == 0) useful = mineNodes;

        int totalWeight = useful.Sum(n => n.weight);
        int choice = Random.Range(0, totalWeight);
        int current = 0;
        foreach (var node in useful)
        {
            current += node.weight;
            if (choice < current)
                return node.prefab;
        }
        return null;
    }

    private bool IsUtilityRelevant(TrackModifierType type, InstrumentTrack track)
    {
        switch (type)
        {
            case TrackModifierType.Clear:
                return track.GetNoteDensity() > 0;

            case TrackModifierType.Remix:
                return track.GetNoteDensity() < 4;
            case TrackModifierType.Expansion:
                return track.loopMultiplier < track.maxLoopMultiplier;
            case TrackModifierType.Contract:
                return track.loopMultiplier > 1 && track.GetNoteDensity() >= 6;
            case TrackModifierType.Solo:
            case TrackModifierType.Magic:
            case TrackModifierType.MoodShift:
            case TrackModifierType.StructureShift:
                return true; // context-aware, so allowed
            /*case TrackModifierType.Drift:
                return track.drumTrack.driftoneManager != null && !track.drumTrack.driftoneManager.isDriftoneActive;
                */
            default:
                return true;
        }
    }

    private GameObject GetRoundRobinNode()
    {
        // Find unused + useful prefabs
        var unused = mineNodes
            .Where(n => !spawnedThisCycle.Contains(n.prefab))
            .Where(n => IsPrefabUseful(n.prefab))
            .ToList();

        // Fallback: allow full list if filtered out everything
        if (unused.Count == 0)
        {
            spawnedThisCycle.Clear();

            var fallback = mineNodes
                .Where(n => IsPrefabUseful(n.prefab))
                .ToList();

            if (fallback.Count == 0)
                fallback = mineNodes; // if still empty, allow everything

            var chosen = fallback[Random.Range(0, fallback.Count)];
            spawnedThisCycle.Add(chosen.prefab);
            return chosen.prefab;
        }

        var selected = unused[Random.Range(0, unused.Count)];
        spawnedThisCycle.Add(selected.prefab);
        return selected.prefab;
    }

    private InstrumentTrack FindTrackForRole(MusicalRole role)
    {
        return trackController?.tracks?.FirstOrDefault(t => t.assignedRole == role);
    }
    private List<WeightedMineNode> FilterUsefulNodes()
    {
        return mineNodes.Where(n =>
        {
            var utility = n.prefab.GetComponent<TrackUtilityMinedObject>();
            if (utility == null) return true; // non-utility node
            var track = FindTrackForRole(utility.targetRole);
            return track != null && IsUtilityRelevant(utility.type, track);
        }).ToList();
    }

    private GameObject GetQuotaBasedNode()
    {
        var quotas = mineNodes.OrderBy(n =>
        {
            int used = spawnCounts.GetValueOrDefault(n.prefab, 0);
            return (float)used / Mathf.Max(n.weight, 1); // fewer per weight = higher priority
        }).ToList();

        var chosen = quotas.First();
        spawnCounts.TryAdd(chosen.prefab, 0);
        spawnCounts[chosen.prefab]++;
        return chosen.prefab;
    }

    public void ResetSpawnState()
    {
        spawnedThisCycle.Clear();
        spawnCounts.Clear();
    }
}
