using UnityEngine;
using System.Collections.Generic;

[RequireComponent(typeof(Rigidbody2D))]
public class MineNodeDustInteractor : MonoBehaviour
{
    [Header("Multipliers while in dust (node-specific)")]
    [Tooltip("Environment feedback scalar consumed by MineNode locomotion while in dust.")]
    [Range(0f, 1f)] public float dustDragScalar = 0.85f;

    [Tooltip("Extra braking applied per FixedUpdate while inside dust.")]
    public float extraBrake = 0.25f;

    [Header("Maze Exhaust Painting")]
    [Tooltip("If true, this node paints adjacent dust cells with its role at reduced energy.")]
    public bool carveMaze = false;

    [Tooltip("Fraction of maxEnergyUnits to assign when exhaust-painting a cell (0=empty, 1=full).")]
    [SerializeField, Range(0f, 1f)] private float exhaustEnergyFraction = 0.4f;

    [SerializeField] private float edgeHugForce = 2f;

    [Tooltip("Force applied to push the node back out when it is grid-inside a dust cell.")]
    [SerializeField] private float escapePushForce = 12f;
    [Tooltip("World-space cap for depenetration correction during swept boundary containment.")]
    [SerializeField] private float maxCorrectionPerTick = 0.5f;

    // ---------------------------------------------------------------
    // Role-hunter: BFS to find nearest dust cell not already our role,
    // steer toward it, paint it on arrival.
    // ---------------------------------------------------------------
    [Header("Role Hunter")]
    [Tooltip("Max BFS cells visited when searching for the nearest untinted dust cell.")]
    [SerializeField] private int huntBfsBudget = 600;

    [Tooltip("How strongly the hunt direction biases _carveDir in MineNode. 0 = no bias, 1 = full override.")]
    [SerializeField, Range(0f, 1f)] private float huntDirWeight = 0.55f;

    [Tooltip("Grid-cell radius around the node's current cell that counts as 'arrived' at the target.")]
    [SerializeField] private int arrivalRadiusCells = 1;

    [Tooltip("Seconds between retarget BFS ticks when no target is held.")]
    [SerializeField] private float retargetInterval = 0.35f;

    private bool       _hasHuntTarget;
    private Vector2Int _huntTargetCell;
    private Vector2    _huntDir;          // normalized world-space direction toward target
    private float      _retargetTimer;

    // BFS reuse buffers (shared; not re-entrant but FixedUpdate is single-threaded)
    private readonly Queue<Vector2Int>   _bfsQueue   = new Queue<Vector2Int>(256);
    private readonly HashSet<Vector2Int> _bfsVisited = new HashSet<Vector2Int>(256);

    private static readonly Vector2Int[] kNeighbours4 =
    {
        new( 1, 0), new(-1, 0), new( 0, 1), new( 0,-1)
    };

    private static readonly Vector2Int[] kNeighbours8 =
    {
        new(-1,-1), new(0,-1), new(1,-1),
        new(-1, 0),            new(1, 0),
        new(-1, 1), new(0, 1), new(1, 1),
    };

    // ---------------------------------------------------------------
    // Existing carve state
    // ---------------------------------------------------------------
    private float carveIntervalSeconds = 0.08f;

    private float _carveTimer;
    private int   _dustCellsCarved = 0;
    private Rigidbody2D _rb;
    private DrumTrack   _drumTrack;
    private MineNode    _node;
    private bool _prevInDust;
    private Vector2 _prevPos;
    private bool _hasPrevPos;

    // ---------------------------------------------------------------
    // Public API
    // ---------------------------------------------------------------

    /// <summary>
    /// World-space direction toward the current MusicalRole.None hunt target.
    /// Returns Vector2.zero when no target is known. MineNode.FixedUpdate blends
    /// this into _carveDir each tick.
    /// </summary>
    public Vector2 GetHuntDir() => _hasHuntTarget ? _huntDir : Vector2.zero;

    /// <summary>Weight applied when blending hunt direction into _carveDir.</summary>
    public float HuntDirWeight => huntDirWeight;

    void Awake()
    {
        _rb          = GetComponent<Rigidbody2D>();
        if (_rb == null) TryGetComponent(out _rb);
        _node        = GetComponent<MineNode>();
        _drumTrack   = (_node != null) ? _node.DrumTrack : null;
    }

    void FixedUpdate()
    {
        if (_rb == null || _drumTrack == null) return;

        if (_hasPrevPos)
            EnforceSweptContainment(_prevPos, _rb.position);


        if (_drumTrack.TryGetPlayAreaWorld(out var area))
        {
            const float kBoundaryInset = 0.05f;
            Vector2 clamped = new Vector2(
                Mathf.Clamp(_rb.position.x, area.left + kBoundaryInset, area.right - kBoundaryInset),
                Mathf.Clamp(_rb.position.y, area.bottom + kBoundaryInset, area.top - kBoundaryInset));
            if ((clamped - _rb.position).sqrMagnitude > 0.000001f)
            {
                Vector2 correction = clamped - _rb.position;
                _rb.position = clamped;

                if (correction.sqrMagnitude > 0.000001f)
                {
                    Vector2 outward = correction.normalized;
                    float outwardSpeed = Vector2.Dot(_rb.linearVelocity, -outward);
                    if (outwardSpeed > 0f)
                        _rb.linearVelocity += outward * outwardSpeed;
                }
            }
        }

        Vector2    worldPos = _rb.position;
        Vector2Int cell     = _drumTrack.CellOf(worldPos);
        bool       inDust   = _drumTrack.HasDustAt(cell);

        if (inDust && !_prevInDust)
            OnDustEnterGrid(cell);
        _prevInDust = inDust;

        // ---------------------------------------------------------------
        // Dust feel (unchanged)
        // ---------------------------------------------------------------
        if (inDust)
        {
            if (_rb.linearVelocity.sqrMagnitude > 0.0001f)
                _rb.AddForce(-_rb.linearVelocity * extraBrake, ForceMode2D.Force);

            // Push back toward the nearest open (non-dust) neighboring cell so the node
            // can't remain embedded in a wall — this is the complement of the edge-hug force.
            Vector2 escapeDir = SumNeighborDirections(cell, requireDust: false);
            if (escapeDir.sqrMagnitude > 0.0001f)
            {
                _rb.AddForce(escapeDir.normalized * escapePushForce, ForceMode2D.Force);

                // Hard wall: cancel any velocity component pushing deeper into the dust wall.
                // This prevents driving forces from overpowering the escape push and tunneling through.
                Vector2 wallNormal  = escapeDir.normalized;
                float   intoWallVel = Vector2.Dot(_rb.linearVelocity, wallNormal);
                if (intoWallVel < 0f)
                    _rb.linearVelocity -= wallNormal * intoWallVel;
            }
        }

        if (!carveMaze)
        {
            _prevPos = _rb.position;
            _hasPrevPos = true;
            return;
        }

        // ---------------------------------------------------------------
        // None-hunter: retarget tick (no budget gate — runs indefinitely)
        // ---------------------------------------------------------------
        _retargetTimer -= Time.fixedDeltaTime;

        // Validate existing target each tick (it might have been painted by us or another node)
        if (_hasHuntTarget && IsAlreadyNodeRole(_huntTargetCell))
        {
            _hasHuntTarget = false;
            _retargetTimer = 0f; // retarget immediately
        }

        if (!_hasHuntTarget && _retargetTimer <= 0f)
        {
            _retargetTimer = retargetInterval;
            HuntTick(cell);
        }

        // Refresh direction to target each frame (node is moving)
        if (_hasHuntTarget)
        {
            RefreshHuntDir();

            // Arrival check: if we're within arrivalRadiusCells of the target, paint it now
            if (IsArrived(cell, _huntTargetCell))
            {
                PaintTargetCell();
                _hasHuntTarget = false;
                _retargetTimer = 0f; // find next target immediately
            }
        }

        // ---------------------------------------------------------------
        // Carve interval: exhaust-paint adjacent neighbors
        // ---------------------------------------------------------------
        _carveTimer += Time.fixedDeltaTime;
        if (_carveTimer < carveIntervalSeconds) return;
        _carveTimer = 0f;

        var gen = GameFlowManager.Instance?.dustGenerator;
        if (gen == null || _node == null) return;

        MusicalRole role = _node.GetImprintRole();

        // Paint any adjacent dust with our role at reduced energy (exhaust trail)
        for (int i = 0; i < kNeighbours8.Length; i++)
        {
            var neighbor = cell + kNeighbours8[i];
            if (!_drumTrack.HasDustAt(neighbor)) continue;
            if (IsAlreadyNodeRole(neighbor)) continue; // already our color — skip

            gen.PaintDustExhaust(neighbor, role, exhaustEnergyFraction);
            _dustCellsCarved++;
        }

        // Edge-hugging: only when NOT inside dust — inside dust the escape push handles steering,
        // and edge-hug would directly cancel it by pushing back toward the wall.
        if (!inDust)
        {
            Vector2 edgeDir = SumNeighborDirections(cell, requireDust: true);
            if (edgeDir.sqrMagnitude > 0.0001f)
                _rb.AddForce(edgeDir.normalized * edgeHugForce, ForceMode2D.Force);
        }

        _prevPos = _rb.position;
        _hasPrevPos = true;
    }

    private void EnforceSweptContainment(Vector2 fromPos, Vector2 toPos)
    {
        Vector2 delta = toPos - fromPos;

        Vector2Int fromCell = _drumTrack.CellOf(fromPos);
        Vector2Int toCell = _drumTrack.CellOf(toPos);
        if (IsDiagonalCutBlocked(fromCell, toCell))
        {
            if (TryFindNearestOpenCell(fromCell, 2, out var legal))
            {
                Vector2 correction = _drumTrack.GridToWorldPosition(legal) - _rb.position;
                _rb.position += Vector2.ClampMagnitude(correction, Mathf.Max(0f, maxCorrectionPerTick));
            }
            _rb.linearVelocity = Vector2.zero;
            return;
        }

        float maxAxis = Mathf.Max(Mathf.Abs(delta.x), Mathf.Abs(delta.y));
        int steps = Mathf.Clamp(Mathf.CeilToInt(maxAxis * 6f), 1, 96);
        Vector2Int prevCell = fromCell;
        for (int i = 1; i <= steps; i++)
        {
            float t = (float)i / steps;
            Vector2 sample = Vector2.Lerp(fromPos, toPos, t);
            Vector2Int sampleCell = _drumTrack.CellOf(sample);

            if (IsDiagonalCutBlocked(prevCell, sampleCell))
            {
                if (TryFindNearestOpenCell(prevCell, 2, out var legalDiag))
                {
                    Vector2 diagCorrection = _drumTrack.GridToWorldPosition(legalDiag) - _rb.position;
                    _rb.position += Vector2.ClampMagnitude(diagCorrection, Mathf.Max(0f, maxCorrectionPerTick));
                }
                _rb.linearVelocity = Vector2.zero;
                return;
            }

            prevCell = sampleCell;
            if (!_drumTrack.HasDustAt(sampleCell)) continue;

            Vector2 tangent = new Vector2(-delta.y, delta.x);
            if (tangent.sqrMagnitude > 0.0001f)
            {
                tangent.Normalize();
                float tangentialSpeed = Vector2.Dot(_rb.linearVelocity, tangent);
                _rb.linearVelocity = tangent * tangentialSpeed;
            }
            else
            {
                _rb.linearVelocity = Vector2.zero;
            }

            if (TryFindNearestOpenCell(sampleCell, 4, out var legal))
            {
                Vector2 correction = _drumTrack.GridToWorldPosition(legal) - _rb.position;
                _rb.position += Vector2.ClampMagnitude(correction, Mathf.Max(0f, maxCorrectionPerTick));
            }
            _rb.linearVelocity = Vector2.zero;
            return;
        }
    }

    private bool IsDiagonalCutBlocked(Vector2Int fromCell, Vector2Int toCell)
    {
        int stepX = toCell.x - fromCell.x;
        int stepY = toCell.y - fromCell.y;
        if (stepX == 0 || stepY == 0) return false;

        stepX = stepX > 0 ? 1 : -1;
        stepY = stepY > 0 ? 1 : -1;

        Vector2Int sideX = _drumTrack.WrapGridCell(fromCell + new Vector2Int(stepX, 0));
        Vector2Int sideY = _drumTrack.WrapGridCell(fromCell + new Vector2Int(0, stepY));
        return _drumTrack.HasDustAt(sideX) || _drumTrack.HasDustAt(sideY);
    }

    private bool TryFindNearestOpenCell(Vector2Int fromCell, int radius, out Vector2Int best)
    {
        if (!_drumTrack.HasDustAt(fromCell))
        {
            best = fromCell;
            return true;
        }

        best = fromCell;
        float bestDist = float.MaxValue;
        bool found = false;
        for (int y = -radius; y <= radius; y++)
        for (int x = -radius; x <= radius; x++)
        {
            var c = _drumTrack.WrapGridCell(fromCell + new Vector2Int(x, y));
            if (_drumTrack.HasDustAt(c)) continue;
            float d = x * x + y * y;
            if (d < bestDist)
            {
                bestDist = d;
                best = c;
                found = true;
            }
        }
        return found;
    }

    // ---------------------------------------------------------------
    // Grid-based dust contact
    // ---------------------------------------------------------------

    private void OnDustEnterGrid(Vector2Int cell)
    {
        if (_node == null || _drumTrack == null) return;

        var gen = GameFlowManager.Instance?.dustGenerator;
        if (gen == null) return;
        if (!gen.TryGetCellGo(cell, out var go) || go == null) return;
        if (!go.TryGetComponent<CosmicDust>(out var dust)) return;

        MusicalRole role = _node.GetImprintRole();
        var         prof = MusicalRoleProfileLibrary.GetProfile(role);
        if (prof == null) return;

        Color roleColor = prof.GetBaseColor();
        roleColor.a = 1f;
        dust.ApplyRoleAndCharge(role, roleColor, 1f, prof.maxEnergyUnits);
        dust.clearing.carveResistance01 = prof.GetCarveResistance01();
        dust.clearing.drainResistance01 = prof.GetDrainResistance01();

        gen.PaintDustExhaust(cell, role, 1f);
    }

    // ---------------------------------------------------------------
    // Hunt BFS
    // ---------------------------------------------------------------

    /// <summary>
    /// BFS outward from <paramref name="fromCell"/>.
    /// Traverses open (non-dust) cells; evaluates dust boundary cells for Role == None.
    /// First None-role cell found is nearest — sets as hunt target.
    /// </summary>
    private void HuntTick(Vector2Int fromCell)
    {
        int w = _drumTrack.GetSpawnGridWidth();
        int h = _drumTrack.GetSpawnGridHeight();
        if (w <= 0 || h <= 0) return;

        _bfsQueue.Clear();
        _bfsVisited.Clear();
        _bfsQueue.Enqueue(fromCell);
        _bfsVisited.Add(fromCell);

        int visited = 0;
        while (_bfsQueue.Count > 0 && visited < huntBfsBudget)
        {
            var cell = _bfsQueue.Dequeue();
            visited++;

            for (int i = 0; i < kNeighbours4.Length; i++)
            {
                var nb = _drumTrack.WrapGridCell(cell + kNeighbours4[i]);
                if (nb.x < 0 || nb.y < 0 || nb.x >= w || nb.y >= h) continue;
                if (_bfsVisited.Contains(nb)) continue;
                _bfsVisited.Add(nb);

                if (!_drumTrack.HasDustAt(nb))
                {
                    _bfsQueue.Enqueue(nb); // open cell — traverse
                    continue;
                }

                // Dust boundary: skip cells already our role
                if (IsAlreadyNodeRole(nb)) continue;

                // Found the nearest unpainted dust cell
                _hasHuntTarget  = true;
                _huntTargetCell = nb;
                RefreshHuntDir();
                return;
            }
        }

        // No unpainted dust reachable within budget — clear target
        _hasHuntTarget = false;
    }

    private void RefreshHuntDir()
    {
        Vector2 cellWorld = _drumTrack.GridToWorldPosition(_huntTargetCell);
        Vector2 dir       = cellWorld - _rb.position;
        if (dir.sqrMagnitude > 0.0001f)
            _huntDir = dir.normalized;
    }

    private void PaintTargetCell()
    {
        var gen = GameFlowManager.Instance?.dustGenerator;
        if (gen == null || _node == null) return;

        MusicalRole role = _node.GetImprintRole();
        gen.PaintDustExhaust(_huntTargetCell, role, exhaustEnergyFraction);
        _dustCellsCarved++;
    }

    private bool IsArrived(Vector2Int currentCell, Vector2Int targetCell)
    {
        int dx = Mathf.Abs(currentCell.x - targetCell.x);
        int dy = Mathf.Abs(currentCell.y - targetCell.y);
        return dx <= arrivalRadiusCells && dy <= arrivalRadiusCells;
    }

    private bool IsAlreadyNodeRole(Vector2Int gp)
    {
        var gen = GameFlowManager.Instance?.dustGenerator;
        if (gen == null || _node == null) return false;
        if (!gen.TryGetCellGo(gp, out var go) || go == null) return false;
        if (!go.TryGetComponent<CosmicDust>(out var dust)) return false;
        return dust.Role == _node.GetImprintRole();
    }


    private Vector2 SumNeighborDirections(Vector2Int center, bool requireDust)
    {
        Vector2 dir = Vector2.zero;
        for (int i = 0; i < kNeighbours8.Length; i++)
        {
            var offset = kNeighbours8[i];
            bool hasDust = _drumTrack.HasDustAt(center + offset);
            if (hasDust == requireDust)
                dir += new Vector2(offset.x, offset.y);
        }
        return dir;
    }

    public void SetLevelAuthority(DrumTrack drumTrack)
    {
        _drumTrack = drumTrack;
    }

    public bool IsInDustAtCurrentCell()
    {
        if (_rb == null || _drumTrack == null) return false;
        return _drumTrack.HasDustAt(_drumTrack.CellOf(_rb.position));
    }
}
