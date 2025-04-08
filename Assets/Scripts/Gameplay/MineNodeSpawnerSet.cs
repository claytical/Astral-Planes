using System.Collections.Generic;
using System.Linq;
using UnityEngine;
public enum SpawnerPhase
{
    Establish,     // replaces Intro, GrooveStart
    Evolve,        // replaces InstrumentChoice, Reharmonize
    Intensify,     // replaces Buildup
    Release,       // replaces GrooveDrop, Finale
    Wildcard,       // replaces Experimental
    Pop
}
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

    public GameObject GetMineNode()
    {
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

    private GameObject GetWeightedRandomNode()
    {
        int totalWeight = mineNodes.Sum(n => n.weight);
        int choice = Random.Range(0, totalWeight);
        int current = 0;
        foreach (var node in mineNodes)
        {
            current += node.weight;
            if (choice < current)
                return node.prefab;
        }
        return null;
    }

    private GameObject GetRoundRobinNode()
    {
        var unused = mineNodes.Where(n => !spawnedThisCycle.Contains(n.prefab)).ToList();
        if (unused.Count == 0)
        {
            spawnedThisCycle.Clear(); // reset after full coverage
            unused = mineNodes;
        }

        var chosen = unused[Random.Range(0, unused.Count)];
        spawnedThisCycle.Add(chosen.prefab);
        return chosen.prefab;
    }

    private GameObject GetQuotaBasedNode()
    {
        var quotas = mineNodes.OrderBy(n =>
        {
            int used = spawnCounts.TryGetValue(n.prefab, out int count) ? count : 0;
            return (float)used / Mathf.Max(n.weight, 1); // fewer per weight = higher priority
        }).ToList();

        var chosen = quotas.First();
        if (!spawnCounts.ContainsKey(chosen.prefab))
            spawnCounts[chosen.prefab] = 0;
        spawnCounts[chosen.prefab]++;
        return chosen.prefab;
    }

    public void ResetSpawnState()
    {
        spawnedThisCycle.Clear();
        spawnCounts.Clear();
    }
}
