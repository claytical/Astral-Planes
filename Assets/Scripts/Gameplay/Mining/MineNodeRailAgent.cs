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
    public bool HasPath => _path != null && _pathIndex < _path.Count;

    private Rigidbody2D _rb;
    private DrumTrack _drum;
    private List<Vector2Int> _path = new();
    private int _pathIndex = 0;
    private float _lastReplanTime = -999f;

    private System.Func<Vector2Int> _targetProvider; // plug a goal function (vehicle, corner, etc.)

    void Awake()
    {
        _rb = GetComponent<Rigidbody2D>();
        _drum = FindAnyObjectByType<DrumTrack>();
    }
    void FixedUpdate()
    {
        if (_drum == null) return;

        // If we have no path or we've reached the end, (re)plan toward target
        if (NeedsPath())
        {
            TryPlanToTarget();
            _rb.linearVelocity = Vector2.zero;
            return;
        }

        // Move toward next cell center
        var next = _path[_pathIndex];
        Vector2 nextPos = _drum.CellCenter(next);
        Vector2 curPos  = _rb.position;
        Vector2 delta   = nextPos - curPos;

        float step = cellsPerSecond * Time.fixedDeltaTime * _drum.GetCellWorldSize();// implement GetCellWorldSize() or divide by your world scale
        if (delta.magnitude <= step)
        {
            // Arrive at cell center
            _rb.position = nextPos;
            _pathIndex++;

            // At intersections or if the next step is blocked by freshly spawned dust, replan
            if (Time.time - _lastReplanTime > replanCooldown)
            {
                if (_pathIndex >= _path.Count || !_drum.IsSpawnCellAvailable(_path[_pathIndex].x, _path[_pathIndex].y))
                {
                    TryPlanToTarget();
                }
            }
            _rb.linearVelocity = Vector2.zero;
        }
        else
        {
            // Constant-speed glide along corridor
            _rb.linearVelocity = delta.normalized * (cellsPerSecond * _drum.GetCellWorldSize());
        }
    }

    bool NeedsPath() => _pathIndex >= _path.Count || _path.Count == 0;
public void ApplySpeed(PhaseStarBehaviorProfile profile, MusicalRole role, MusicalPhase phase)
    {
        float baseCps = (profile != null && profile.baseCellsPerSecond > 0f) ? profile.baseCellsPerSecond : 3.5f;
        float roleMul = 1f;
        switch (role)
        {
            case MusicalRole.Bass:    roleMul = 0.75f; break;
            case MusicalRole.Harmony: roleMul = 1.00f; break;
            case MusicalRole.Groove:  roleMul = 1.10f; break;
            case MusicalRole.Lead:
            default:                  roleMul = 1.25f; break;
        }
        float phaseMul = 1f;
        if (profile != null)
        {
            switch (phase)
            {
                case MusicalPhase.Establish:  phaseMul = profile.establishSpeedMul; break;
                case MusicalPhase.Evolve:     phaseMul = profile.evolveSpeedMul;    break;
                case MusicalPhase.Intensify:  phaseMul = profile.intensifySpeedMul; break;
                case MusicalPhase.Release:    phaseMul = profile.releaseSpeedMul;   break;
                case MusicalPhase.Wildcard:   phaseMul = profile.wildcardSpeedMul;  break;
                case MusicalPhase.Pop:        phaseMul = profile.popSpeedMul;       break;
            }
        }
        else
        {
            phaseMul = phase switch
            {
                MusicalPhase.Establish => 0.9f,
                MusicalPhase.Evolve    => 1.0f,
                MusicalPhase.Intensify => 1.2f,
                MusicalPhase.Release   => 0.85f,
                MusicalPhase.Wildcard  => 1.3f,
                MusicalPhase.Pop       => 1.05f,
                _ => 1f
            };
        }
        cellsPerSecond = Mathf.Max(0.5f, baseCps * roleMul * phaseMul);
    }

    public void ReplanToFarthest()
    {
        if (_drum == null) _drum = GetComponentInParent<DrumTrack>();
        if (_drum == null) return;
        Vector2Int start = _drum.WorldToGridPosition(transform.position);
        Vector2Int goal  = _drum.FarthestReachableCellInComponent(start);
        var path = new List<Vector2Int>();
        if (_drum.TryFindPath(start, goal, path))
            SetPath(path);
    }
    public void SetPath(IList<Vector2Int> cells)
    {
        _path.Clear();
        if (cells != null) _path.AddRange(cells);
        _pathIndex = 0;
    }

    public void SetTargetProvider(System.Func<Vector2Int> provider) => _targetProvider = provider;
    
    void TryPlanToTarget()
    {
        if (_targetProvider == null) return;
        Vector2Int start = _drum.CellOf(_rb.position);
        Vector2Int goal  = _targetProvider.Invoke();
        var scratch = new List<Vector2Int>();
        if (_drum.TryFindPath(start, goal, scratch))
        {
            SetPath(scratch);
            OnPlanStart?.Invoke();     
        }
        else
        {
            // No path? Nudge randomly to nearest free neighbor
            foreach (var n in Neighbors(start))
            {
                if (_drum.IsSpawnCellAvailable(n.x, n.y)) { SetPath(new[]{ n }); break; }
            }
        }

        _lastReplanTime = Time.time;
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
