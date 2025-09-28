using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(Rigidbody2D))]
public class MineNodeRailAgent : MonoBehaviour
{
    public float cellsPerSecond = 4.0f;      // movement speed in cells
    public float replanCooldown = 0.15f;     // avoid thrashing
    public bool  snapAtCorners  = true;
    public event System.Action OnPlanStart;
    public event System.Action OnPlanReady;
    public bool HasPath => path != null && pathIndex < path.Count;

    Rigidbody2D rb;
    DrumTrack drum;
    List<Vector2Int> path = new();
    int pathIndex = 0;
    float lastReplanTime = -999f;

    public System.Func<Vector2Int> targetProvider; // plug a goal function (vehicle, corner, etc.)

    void Awake()
    {
        rb = GetComponent<Rigidbody2D>();
        drum = FindAnyObjectByType<DrumTrack>();
    }

    public void SetPath(IList<Vector2Int> cells)
    {
        path.Clear();
        if (cells != null) path.AddRange(cells);
        pathIndex = 0;
    }

    public void SetTargetProvider(System.Func<Vector2Int> provider) => targetProvider = provider;

    void FixedUpdate()
    {
        if (drum == null) return;

        // If we have no path or we've reached the end, (re)plan toward target
        if (NeedsPath())
        {
            TryPlanToTarget();
            rb.linearVelocity = Vector2.zero;
            return;
        }

        // Move toward next cell center
        var next = path[pathIndex];
        Vector2 nextPos = drum.CellCenter(next);
        Vector2 curPos  = rb.position;
        Vector2 delta   = nextPos - curPos;

        float step = cellsPerSecond * Time.fixedDeltaTime * drum.GetCellWorldSize();// implement GetCellWorldSize() or divide by your world scale
        if (delta.magnitude <= step)
        {
            // Arrive at cell center
            rb.position = nextPos;
            pathIndex++;

            // At intersections or if the next step is blocked by freshly spawned dust, replan
            if (Time.time - lastReplanTime > replanCooldown)
            {
                if (pathIndex >= path.Count || !drum.IsSpawnCellAvailable(path[pathIndex].x, path[pathIndex].y))
                {
                    TryPlanToTarget();
                }
            }
            rb.linearVelocity = Vector2.zero;
        }
        else
        {
            // Constant-speed glide along corridor
            rb.linearVelocity = delta.normalized * (cellsPerSecond * drum.GetCellWorldSize());
        }
    }

    bool NeedsPath() => pathIndex >= path.Count || path.Count == 0;

    void TryPlanToTarget()
    {
        if (targetProvider == null) return;
        Vector2Int start = drum.CellOf(rb.position);
        Vector2Int goal  = targetProvider.Invoke();
        var scratch = new List<Vector2Int>();
        if (drum.TryFindPath(start, goal, scratch))
        {
            SetPath(scratch);
            OnPlanStart?.Invoke();     
        }
        else
        {
            // No path? Nudge randomly to nearest free neighbor
            foreach (var n in Neighbors(start))
            {
                if (drum.IsSpawnCellAvailable(n.x, n.y)) { SetPath(new[]{ n }); break; }
            }
        }

        lastReplanTime = Time.time;
    }

    IEnumerable<Vector2Int> Neighbors(Vector2Int c)
    {
        bool even = (c.y & 1) == 0;
        var dirs = even
            ? new[]{ new Vector2Int(+1,0), new Vector2Int(-1,0), new Vector2Int(0,+1), new Vector2Int(-1,+1), new Vector2Int(0,-1), new Vector2Int(-1,-1)}
            : new[]{ new Vector2Int(+1,0), new Vector2Int(-1,0), new Vector2Int(+1,+1), new Vector2Int(0,+1), new Vector2Int(+1,-1), new Vector2Int(0,-1)};
        foreach (var d in dirs) yield return c + d;
    }
}
