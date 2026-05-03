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
