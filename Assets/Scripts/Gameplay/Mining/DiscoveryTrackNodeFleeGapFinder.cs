using System.Collections.Generic;
using UnityEngine;

// BFS gap-seek for a fleeing DiscoveryTrackNode: finds the nearest reachable dust-free
// side-column perimeter cell (a punched exit or player-carved hole) and picks
// a steering waypoint short of it. Side walls only — top/bottom are never
// treated as escape walls.
public class DiscoveryTrackNodeFleeGapFinder
{
    private const int   kBfsBudget      = 600;
    private const float kRetargetPeriod = 0.5f;
    private const int   kWaypointStride = 3;
    private static readonly Vector2Int[] kNeighbours4 =
    {
        new( 1, 0), new(-1, 0), new( 0, 1), new( 0,-1)
    };

    private readonly Queue<Vector2Int>                  _bfsQueue   = new Queue<Vector2Int>(256);
    private readonly Dictionary<Vector2Int, Vector2Int> _bfsParents = new Dictionary<Vector2Int, Vector2Int>(256);
    private float _retargetTimer;

    public bool       HasGap       { get; private set; }
    public Vector2Int GapCell      { get; private set; }
    public Vector2Int WaypointCell { get; private set; }

    public void Update(DrumTrack drumTrack, Vector2Int fromCell, float deltaTime)
    {
        // Drop the target the moment its cell seals again (regrowth or player action).
        if (HasGap && drumTrack.HasDustAt(GapCell))
        {
            HasGap = false;
            _retargetTimer = 0f;
        }

        _retargetTimer -= deltaTime;
        if (_retargetTimer > 0f) return;
        _retargetTimer = kRetargetPeriod;
        TryFindGap(drumTrack, fromCell);
    }

    private void TryFindGap(DrumTrack drumTrack, Vector2Int fromCell)
    {
        HasGap = false;

        int w = drumTrack.GetSpawnGridWidth();
        int h = drumTrack.GetSpawnGridHeight();
        if (w <= 0 || h <= 0) return;

        fromCell.x = Mathf.Clamp(fromCell.x, 0, w - 1);
        fromCell.y = Mathf.Clamp(fromCell.y, 0, h - 1);
        if (drumTrack.HasDustAt(fromCell)) return; // embedded in dust — retry next interval

        _bfsQueue.Clear();
        _bfsParents.Clear();
        _bfsQueue.Enqueue(fromCell);
        _bfsParents[fromCell] = fromCell;

        int visited = 0;
        while (_bfsQueue.Count > 0 && visited < kBfsBudget)
        {
            var cell = _bfsQueue.Dequeue();
            visited++;

            // BFS order means the first side-column open cell reached is the nearest gap.
            if (cell.x == 0 || cell.x == w - 1)
            {
                SetGap(fromCell, cell);
                return;
            }

            for (int i = 0; i < kNeighbours4.Length; i++)
            {
                var nb = cell + kNeighbours4[i];
                // No wrap: the perimeter must act as a boundary for escape pathing.
                if (nb.x < 0 || nb.y < 0 || nb.x >= w || nb.y >= h) continue;
                if (_bfsParents.ContainsKey(nb)) continue;
                if (drumTrack.HasDustAt(nb)) continue;
                _bfsParents[nb] = cell;
                _bfsQueue.Enqueue(nb);
            }
        }
    }

    private void SetGap(Vector2Int fromCell, Vector2Int gapCell)
    {
        HasGap  = true;
        GapCell = gapCell;

        // Walk the parent chain back from the gap and pick the cell
        // kWaypointStride steps out from the node as the steering waypoint.
        int pathLen = 0;
        for (var c = gapCell; c != fromCell; c = _bfsParents[c]) pathLen++;

        var waypoint = gapCell;
        for (int back = pathLen - kWaypointStride; back > 0; back--)
            waypoint = _bfsParents[waypoint];
        WaypointCell = waypoint;
    }
}
