using System.Collections.Generic;
using UnityEngine;

// Weights star-spawn candidate cells toward the region the player has already carved: a
// multi-source BFS from every active vehicle's current cell, walking only through cells
// without dust (same connectivity rule as WispExitPathfinder), gives each reachable cell a
// hop-distance through the player's own cleared corridors. Cells with no such connection
// (walled off by solid dust from every vehicle) still get a small flat floor weight rather
// than zero, so a rare "lucky" spawn in a disconnected pocket stays possible — this is a soft
// bias toward rewarding good carving, not a hard reachability gate.
public static class StarSpawnReachabilityWeighter
{
    private const int kBfsBudget = 4000;

    private static readonly Vector2Int[] kNeighbours4 =
    {
        new(1, 0), new(-1, 0), new(0, 1), new(0, -1)
    };

    public static Vector2Int PickWeighted(
        DrumTrack drum,
        IReadOnlyList<Vector2Int> candidates,
        IReadOnlyList<Vector2Int> vehicleCells,
        float falloff,
        float unreachableFloorWeight01)
    {
        if (candidates == null || candidates.Count == 0) return new Vector2Int(-1, -1);
        if (candidates.Count == 1) return candidates[0];

        var dist = BuildDistanceField(drum, vehicleCells);

        var weights = new float[candidates.Count];
        float totalWeight = 0f;
        for (int i = 0; i < candidates.Count; i++)
        {
            float w = (dist != null && dist.TryGetValue(candidates[i], out int d))
                ? 1f / (1f + Mathf.Max(0f, falloff) * d)
                : Mathf.Max(0.0001f, unreachableFloorWeight01);
            weights[i] = w;
            totalWeight += w;
        }

        if (totalWeight <= 0f)
            return candidates[Random.Range(0, candidates.Count)];

        float roll = Random.value * totalWeight;
        float accum = 0f;
        for (int i = 0; i < candidates.Count; i++)
        {
            accum += weights[i];
            if (roll <= accum) return candidates[i];
        }
        return candidates[candidates.Count - 1];
    }

    // Multi-source BFS: every vehicle cell starts at distance 0, so each reached cell's
    // distance is automatically the hop count to its NEAREST vehicle, without running one
    // BFS per vehicle.
    private static Dictionary<Vector2Int, int> BuildDistanceField(DrumTrack drum, IReadOnlyList<Vector2Int> sources)
    {
        if (drum == null || sources == null || sources.Count == 0) return null;

        int w = drum.GetSpawnGridWidth();
        int h = drum.GetSpawnGridHeight();
        if (w <= 0 || h <= 0) return null;

        var dist = new Dictionary<Vector2Int, int>();
        var queue = new Queue<Vector2Int>();
        foreach (var s in sources)
        {
            var c = new Vector2Int(Mathf.Clamp(s.x, 0, w - 1), Mathf.Clamp(s.y, 0, h - 1));
            if (dist.ContainsKey(c)) continue;
            dist[c] = 0;
            queue.Enqueue(c);
        }
        if (queue.Count == 0) return null;

        int visited = 0;
        while (queue.Count > 0 && visited < kBfsBudget)
        {
            var cell = queue.Dequeue();
            visited++;
            int d = dist[cell];

            for (int i = 0; i < kNeighbours4.Length; i++)
            {
                var nb = cell + kNeighbours4[i];
                if (nb.x < 0 || nb.y < 0 || nb.x >= w || nb.y >= h) continue;
                if (dist.ContainsKey(nb)) continue;
                if (drum.HasDustAt(nb)) continue;
                dist[nb] = d + 1;
                queue.Enqueue(nb);
            }
        }

        return dist;
    }
}
