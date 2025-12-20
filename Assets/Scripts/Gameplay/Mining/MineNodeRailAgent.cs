using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(Rigidbody2D))]
public class MineNodeRailAgent : MonoBehaviour
{
    public float cellsPerSecond = 4.0f;      // movement speed in cells
    public float replanCooldown = 0.15f;     // avoid thrashing
    public bool  snapAtCorners  = true;

    [Header("Dust gating (optional)")]
    public bool gateByDustDensity = true;
    [Range(0f, 1f)] public float maxAllowedDustDensity01 = 0.35f;

    CosmicDustGenerator _dustGen;

    public event System.Action OnPlanStart;
    public event System.Action OnPlanReady;

    public bool HasPath => _path != null && _pathIndex < _path.Count;

    Rigidbody2D _rb;
    DrumTrack _drum;

    readonly List<Vector2Int> _path = new();
    int _pathIndex = 0;
    float _lastReplanTime = -999f;

    float _cellWorldStep = -1f;

    System.Func<Vector2Int> _targetProvider;

    void Awake()
    {
        _rb = GetComponent<Rigidbody2D>();
        _dustGen = GameFlowManager.Instance != null ? GameFlowManager.Instance.dustGenerator : null;
    }

    public void SetDrumTrack(DrumTrack dt)
    {
        _drum = dt;
        _cellWorldStep = -1f;
    }

    public void SetTargetProvider(System.Func<Vector2Int> provider) => _targetProvider = provider;

    public void SetPath(IList<Vector2Int> cells)
    {
        _path.Clear();
        if (cells != null) _path.AddRange(cells);
        _pathIndex = 0;
        OnPlanReady?.Invoke();
    }

    bool NeedsPath() => _path.Count == 0 || _pathIndex >= _path.Count;

    bool CanReplanNow() => (Time.time - _lastReplanTime) > replanCooldown;

    float GetCellWorldStep()
    {
        if (_cellWorldStep > 0f) return _cellWorldStep;
        if (_drum == null || _rb == null) return 1f;

        var a = _drum.WorldToGridPosition(_rb.position);
        var b = a + Vector2Int.right;

        Vector2 aw = _drum.CellCenter(a);
        Vector2 bw = _drum.CellCenter(b);

        _cellWorldStep = Vector2.Distance(aw, bw);
        if (_cellWorldStep <= 0.0001f) _cellWorldStep = 1f;
        return _cellWorldStep;
    }

    bool IsCellTraversable(Vector2Int cell)
    {
        if (_drum == null) return false;

        // NAV is blocked by dust (3-state model).
        if (!_drum.IsNavCellOpen(cell.x, cell.y))
            return false;

        if (!gateByDustDensity || _dustGen == null)
            return true;

        // Optional density gating (soft bias against thick regions)
        float d = _dustGen.SampleDensity01(_drum.CellCenter(cell));
        return d <= maxAllowedDustDensity01;
    }

    void FixedUpdate()
    {
        if (_drum == null || _rb == null) return;

        Vector2Int curCell = _drum.CellOf(_rb.position);

        // If current space is no longer valid (dust regrew under us), recover.
        if (!IsCellTraversable(curCell))
        {
            RecoverToNearestTraversable(curCell);
            _rb.linearVelocity = Vector2.zero;
            return;
        }

        // Ensure path exists.
        if (NeedsPath())
        {
            TryPlanToTarget();
            _rb.linearVelocity = Vector2.zero;
            return;
        }

        // Validate next step against NAV (dust), not spawn availability.
        Vector2Int nextCell = _path[_pathIndex];
        if (!IsCellTraversable(nextCell))
        {
            if (CanReplanNow()) TryPlanToTarget();
            _rb.linearVelocity = Vector2.zero;
            return;
        }

        // Move toward next cell center at constant speed.
        Vector2 nextPos = _drum.CellCenter(nextCell);
        Vector2 curPos  = _rb.position;
        Vector2 delta   = nextPos - curPos;

        float cellWorld  = GetCellWorldStep();
        float worldSpeed = Mathf.Max(0f, cellsPerSecond) * cellWorld;
        float step       = worldSpeed * Time.fixedDeltaTime;

        float dist = delta.magnitude;
        if (dist <= step || dist <= 0.0001f)
        {
            _rb.MovePosition(nextPos);
            _rb.linearVelocity = Vector2.zero;
            _pathIndex++;

            // If at end, or the next step is blocked, replan.
            if (CanReplanNow())
            {
                if (_pathIndex >= _path.Count || !IsCellTraversable(_path[_pathIndex]))
                    TryPlanToTarget();
            }
        }
        else
        {
            Vector2 dir = delta / dist;

            if (snapAtCorners && _pathIndex > 0)
            {
                Vector2Int prevCell = _path[_pathIndex - 1];
                Vector2 prevPos = _drum.CellCenter(prevCell);
                Vector2 seg = (nextPos - prevPos);
                if (seg.sqrMagnitude > 0.0001f)
                    dir = seg.normalized;
            }

            _rb.MovePosition(curPos + dir * step);
            _rb.linearVelocity = Vector2.zero;
        }
    }

    void RecoverToNearestTraversable(Vector2Int from)
    {
        foreach (var n in Neighbors(from))
        {
            if (IsCellTraversable(n))
            {
                _path.Clear();
                _path.Add(n);
                _pathIndex = 0;
                _lastReplanTime = Time.time;
                OnPlanReady?.Invoke();
                return;
            }
        }

        ReplanToFarthest();
        _pathIndex = 0;
        _lastReplanTime = Time.time;
    }

    void TryPlanToTarget()
    {
        if (_drum == null) return;
        if (_targetProvider == null) return;

        Vector2Int start = _drum.CellOf(_rb.position);

        // Snap start onto traversable space if needed.
        if (!IsCellTraversable(start))
        {
            bool found = false;
            foreach (var n in Neighbors(start))
            {
                if (IsCellTraversable(n)) { start = n; found = true; break; }
            }
            if (!found) return;
        }

        Vector2Int goal = _targetProvider.Invoke();

        // If goal isn't traversable, fall back.
        if (!IsCellTraversable(goal))
            goal = _drum.FarthestReachableCellInComponent(start);

        var scratch = new List<Vector2Int>();
        if (_drum.TryFindPath(start, goal, scratch) && scratch.Count > 0)
        {
            OnPlanStart?.Invoke();
            SetPath(scratch);
        }
        else
        {
            // No path: attempt a single-step nudge.
            foreach (var n in Neighbors(start))
            {
                if (IsCellTraversable(n))
                {
                    _path.Clear();
                    _path.Add(n);
                    _pathIndex = 0;
                    OnPlanReady?.Invoke();
                    break;
                }
            }
        }

        _lastReplanTime = Time.time;
    }

    public void ReplanToFarthest()
    {
        if (_drum == null) _drum = GetComponentInParent<DrumTrack>();
        if (_drum == null || _rb == null) return;

        Vector2Int start = _drum.WorldToGridPosition(_rb.position);

        if (!IsCellTraversable(start))
        {
            bool found = false;
            foreach (var n in Neighbors(start))
            {
                if (IsCellTraversable(n)) { start = n; found = true; break; }
            }
            if (!found) return;
        }

        Vector2Int goal = _drum.FarthestReachableCellInComponent(start);
        var path = new List<Vector2Int>();
        if (_drum.TryFindPath(start, goal, path) && path.Count > 0)
            SetPath(path);
    }

    public void ApplySpeed(PhaseStarBehaviorProfile profile, MusicalRole role, MusicalPhase phase)
    {
        float baseCps = (profile != null && profile.baseCellsPerSecond > 0f) ? profile.baseCellsPerSecond : 3.5f;

        float roleMul = role switch
        {
            MusicalRole.Bass    => 0.75f,
            MusicalRole.Harmony => 1.00f,
            MusicalRole.Groove  => 1.10f,
            MusicalRole.Lead    => 1.25f,
            _                   => 1.00f
        };

        float phaseMul = 1f;
        if (profile != null)
        {
            phaseMul = phase switch
            {
                MusicalPhase.Establish => profile.establishSpeedMul,
                MusicalPhase.Evolve    => profile.evolveSpeedMul,
                MusicalPhase.Intensify => profile.intensifySpeedMul,
                MusicalPhase.Release   => profile.releaseSpeedMul,
                MusicalPhase.Wildcard  => profile.wildcardSpeedMul,
                MusicalPhase.Pop       => profile.popSpeedMul,
                _ => 1f
            };
        }

        cellsPerSecond = Mathf.Max(0.5f, baseCps * roleMul * phaseMul);
    }

    IEnumerable<Vector2Int> Neighbors(Vector2Int c)
    {
        bool even = (c.y & 1) == 0;
        var dirs = even
            ? new[]
            {
                new Vector2Int(+1, 0),
                new Vector2Int(-1, 0),
                new Vector2Int( 0,+1),
                new Vector2Int(-1,+1),
                new Vector2Int( 0,-1),
                new Vector2Int(-1,-1)
            }
            : new[]
            {
                new Vector2Int(+1, 0),
                new Vector2Int(-1, 0),
                new Vector2Int(+1,+1),
                new Vector2Int( 0,+1),
                new Vector2Int(+1,-1),
                new Vector2Int( 0,-1)
            };

        foreach (var d in dirs) yield return c + d;
    }
}
