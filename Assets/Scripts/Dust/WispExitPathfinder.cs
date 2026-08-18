using System.Collections.Generic;
using UnityEngine;

// Goal-directed BFS pathfinder for a Wisp: unlike DiscoveryTrackNodeFleeGapFinder (which
// searches for the nearest side-column escape), this walks to a specific, already-known
// target cell — a path-choice exit's AnchorCell, handed down by PathChoiceCoordinator —
// and exposes the route as a waypoint list to advance through one cell at a time.
public class WispExitPathfinder
{
    private const int kBfsBudget = 4000;

    private static readonly Vector2Int[] kNeighbours4 =
    {
        new(1, 0), new(-1, 0), new(0, 1), new(0, -1)
    };

    public IReadOnlyList<Vector2Int> Waypoints { get; private set; }
    public bool HasPath { get; private set; }

    public bool TryFindPath(DrumTrack drumTrack, Vector2Int fromCell, Vector2Int targetCell)
    {
        HasPath = false;
        Waypoints = null;

        int w = drumTrack.GetSpawnGridWidth();
        int h = drumTrack.GetSpawnGridHeight();
        if (w <= 0 || h <= 0) return false;

        fromCell.x = Mathf.Clamp(fromCell.x, 0, w - 1);
        fromCell.y = Mathf.Clamp(fromCell.y, 0, h - 1);

        var queue = new Queue<Vector2Int>();
        var parents = new Dictionary<Vector2Int, Vector2Int>();
        queue.Enqueue(fromCell);
        parents[fromCell] = fromCell;

        bool found = fromCell == targetCell;
        int visited = 0;
        while (!found && queue.Count > 0 && visited < kBfsBudget)
        {
            var cell = queue.Dequeue();
            visited++;

            for (int i = 0; i < kNeighbours4.Length; i++)
            {
                var nb = cell + kNeighbours4[i];
                if (nb.x < 0 || nb.y < 0 || nb.x >= w || nb.y >= h) continue;
                if (parents.ContainsKey(nb)) continue;
                // The target cell itself (a maze exit's anchor) is always walkable even
                // if dust bookkeeping hasn't caught up yet — everything else must be clear.
                if (nb != targetCell && drumTrack.HasDustAt(nb)) continue;
                parents[nb] = cell;
                queue.Enqueue(nb);

                if (nb == targetCell) { found = true; break; }
            }
        }

        if (!found) return false;

        var path = new List<Vector2Int>();
        for (var c = targetCell; c != fromCell; c = parents[c])
            path.Add(c);
        path.Reverse();

        Waypoints = path;
        HasPath = path.Count > 0;
        return HasPath;
    }
}
