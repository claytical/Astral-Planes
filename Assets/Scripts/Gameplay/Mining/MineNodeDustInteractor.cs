using UnityEngine;
using System.Collections.Generic;

[RequireComponent(typeof(Rigidbody2D))]
public class MineNodeDustInteractor : MonoBehaviour
{
    private bool InsideDust { get; set; }
    private CosmicDust CurrentDust { get; set; }

    [Header("Multipliers while in dust (node-specific)")]
    [Tooltip("Clamp max speed while inside dust (multiplies your locomotion maxSpeed).")]
    public float speedCapMul = 0.9f;

    [Tooltip("Extra braking applied per FixedUpdate while inside dust.")]
    public float extraBrake = 0.25f;

    [Header("Maze Tinting")]
    [Tooltip("If true, this node applies a temporary overlay to adjacent dust as it moves.")]
    public bool carveMaze = true;

    [Header("Overlay Settings")]
    [Tooltip("Seconds the overlay persists after the node moves away from a cell.")]
    [SerializeField] private float overlayDecaySeconds = 2.0f;
    [Tooltip("Multiplier applied to dust carve resistance while overlay is active (0.5 = 50% easier to carve).")]
    [SerializeField, Range(0.1f, 1f)] private float overlayResistanceMult = 0.5f;

    [SerializeField] private float edgeHugForce = 2f;

    [Tooltip("Force applied to push the node back out when it is grid-inside a dust cell.")]
    [SerializeField] private float escapePushForce = 12f;

    // ---------------------------------------------------------------
    // Role-hunter: BFS to find nearest dust cell not already our role,
    // steer toward it, tint it on arrival.
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

    // ---------------------------------------------------------------
    // Existing carve state
    // ---------------------------------------------------------------
    private float carveIntervalSeconds = 0.08f;

    private float _carveTimer;
    private int   _dustCellsCarved = 0;
    private Rigidbody2D _rb;
    private DrumTrack   _drumTrack;
    private MineNode    _node;
    private float _desiredSpeed     = 0f;
    private float _desiredSpeedFloor = 0.25f;

    private bool    _hasDustContactPoint;
    private Vector2 _lastDustContactPoint;

    // ---------------------------------------------------------------
    // Per-node tint budget
    // ---------------------------------------------------------------
    private int  _tintBudget        = 0;   // 0 = unlimited
    private int  _tintedCellCount   = 0;
    private bool _budgetInitialized = false;

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

    /// <summary>
    /// Fraction of tint budget remaining: 1 = untouched, 0 = exhausted.
    /// Always 1 when budget is unlimited (mineNodeTintBudget == 0).
    /// </summary>
    public float TintBudgetRemainingRatio =>
        (_tintBudget <= 0) ? 1f : 1f - Mathf.Clamp01((float)_tintedCellCount / _tintBudget);

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

        // ---------------------------------------------------------------
        // Dust feel (unchanged)
        // ---------------------------------------------------------------
        if (inDust)
        {
            float desired = Mathf.Max(_desiredSpeed, _desiredSpeedFloor);
            float cap     = desired * Mathf.Max(0.05f, speedCapMul);
            float speed   = _rb.linearVelocity.magnitude;

            if (speed > cap && speed > 0.0001f)
                _rb.linearVelocity = _rb.linearVelocity.normalized * cap;

            if (_rb.linearVelocity.sqrMagnitude > 0.0001f)
                _rb.AddForce(-_rb.linearVelocity * extraBrake, ForceMode2D.Force);

            // Push back toward the nearest open (non-dust) neighboring cell so the node
            // can't remain embedded in a wall — this is the complement of the edge-hug force.
            Vector2 escapeDir = Vector2.zero;
            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                if (!_drumTrack.HasDustAt(cell + new Vector2Int(dx, dy)))
                    escapeDir += new Vector2(dx, dy);
            }
            if (escapeDir.sqrMagnitude > 0.0001f)
                _rb.AddForce(escapeDir.normalized * escapePushForce, ForceMode2D.Force);
        }

        if (!carveMaze) return;

        EnsureBudgetInitialized();
        bool budgetExhausted = _tintBudget > 0 && _tintedCellCount >= _tintBudget;

        // ---------------------------------------------------------------
        // None-hunter: retarget tick (suppress when budget exhausted)
        // ---------------------------------------------------------------
        _retargetTimer -= Time.fixedDeltaTime;

        // Validate existing target each tick (it might have been tinted by us or another node)
        if (_hasHuntTarget && IsAlreadyNodeRole(_huntTargetCell))
        {
            _hasHuntTarget = false;
            _retargetTimer = 0f; // retarget immediately
        }

        if (!budgetExhausted && !_hasHuntTarget && _retargetTimer <= 0f)
        {
            _retargetTimer = retargetInterval;
            HuntTick(cell);
        }

        // Refresh direction to target each frame (node is moving)
        if (_hasHuntTarget)
        {
            RefreshHuntDir();

            // Arrival check: if we're within arrivalRadiusCells of the target, tint it now
            if (IsArrived(cell, _huntTargetCell))
            {
                TintTargetCell();
                _hasHuntTarget = false;
                _retargetTimer = 0f; // find next target immediately
            }
        }

        // ---------------------------------------------------------------
        // Carve interval: tint adjacent neighbors
        // ---------------------------------------------------------------
        _carveTimer += Time.fixedDeltaTime;
        if (_carveTimer < carveIntervalSeconds) return;
        _carveTimer = 0f;

        var gen = GameFlowManager.Instance?.dustGenerator;
        if (gen == null || _node == null) return;

        MusicalRole role = _node.GetImprintRole();

        // Flip any adjacent dust to our role
        for (int dx = -1; dx <= 1; dx++)
        for (int dy = -1; dy <= 1; dy++)
        {
            if (dx == 0 && dy == 0) continue;
            if (budgetExhausted) break; // no cells left to paint

            var neighbor = cell + new Vector2Int(dx, dy);

            if (!_drumTrack.HasDustAt(neighbor)) continue;
            if (IsAlreadyNodeRole(neighbor)) continue; // already our color — skip

            gen.ApplyMineNodeOverlay(neighbor, role, overlayResistanceMult, overlayDecaySeconds);
            _tintedCellCount++;
            budgetExhausted = _tintBudget > 0 && _tintedCellCount >= _tintBudget;
        }

        // Edge-hugging: only when NOT inside dust — inside dust the escape push handles steering,
        // and edge-hug would directly cancel it by pushing back toward the wall.
        if (!inDust)
        {
            Vector2 edgeDir           = Vector2.zero;
            int     dustNeighborCount = 0;
            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                var neighbor = cell + new Vector2Int(dx, dy);
                if (_drumTrack.HasDustAt(neighbor))
                {
                    edgeDir += new Vector2(dx, dy);
                    dustNeighborCount++;
                }
            }
            if (dustNeighborCount > 0)
                _rb.AddForce(edgeDir.normalized * edgeHugForce, ForceMode2D.Force);
        }
    }

    // ---------------------------------------------------------------
    // Budget init
    // ---------------------------------------------------------------

    /// <summary>
    /// Reads mineNodeTintBudget from the first matching RoleMotifNoteSetConfig for this
    /// node's role. Called lazily on first carve tick so the motif is guaranteed to be set.
    /// </summary>
    private void EnsureBudgetInitialized()
    {
        if (_budgetInitialized) return;
        _budgetInitialized = true;
        if (_node == null) return;
        var motif = GameFlowManager.Instance?.phaseTransitionManager?.currentMotif;
        if (motif == null) return;
        MusicalRole role = _node.GetImprintRole();
        var configs = motif.roleNoteConfigs;
        for (int i = 0; i < configs.Count; i++)
            if (configs[i] != null && configs[i].role == role)
            {
                _tintBudget = configs[i].mineNodeTintBudget;
                return;
            }
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
                var nb = cell + kNeighbours4[i];
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

                // Found the nearest untinted dust cell
                _hasHuntTarget  = true;
                _huntTargetCell = nb;
                RefreshHuntDir();
                return;
            }
        }

        // No untinted dust reachable within budget — clear target
        _hasHuntTarget = false;
    }

    private void RefreshHuntDir()
    {
        Vector2 cellWorld = _drumTrack.GridToWorldPosition(_huntTargetCell);
        Vector2 dir       = cellWorld - _rb.position;
        if (dir.sqrMagnitude > 0.0001f)
            _huntDir = dir.normalized;
    }

    private void TintTargetCell()
    {
        var gen = GameFlowManager.Instance?.dustGenerator;
        if (gen == null || _node == null) return;

        bool budgetExhausted = _tintBudget > 0 && _tintedCellCount >= _tintBudget;
        if (budgetExhausted) return;

        MusicalRole role = _node.GetImprintRole();
        gen.ApplyMineNodeOverlay(_huntTargetCell, role, overlayResistanceMult, overlayDecaySeconds);
        _tintedCellCount++;
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
        // Check overlay rather than dust.Role — MineNode no longer writes authoritative role.
        return gen.HasMineNodeOverlayWithRole(gp, _node.GetImprintRole());
    }

    // ---------------------------------------------------------------
    // Existing helpers (unchanged)
    // ---------------------------------------------------------------

    private bool TryGetDustFromCollision(Collision2D coll, out CosmicDust dust)
    {
        dust = coll.collider != null ? coll.collider.GetComponentInParent<CosmicDust>() : null;
        return dust != null;
    }

    private void OnCollisionEnter2D(Collision2D coll)
    {
        if (!TryGetDustFromCollision(coll, out var dust)) return;
        InsideDust  = true;
        CurrentDust = dust;
    }

    private void OnCollisionExit2D(Collision2D coll)
    {
        if (!InsideDust || CurrentDust == null) return;
        if (!TryGetDustFromCollision(coll, out var dust) || dust != CurrentDust) return;
        InsideDust  = false;
        CurrentDust = null;
    }

    public void SetLevelAuthority(DrumTrack drumTrack)
    {
        _drumTrack = drumTrack;
    }

    public void SetDesiredSpeed(float desiredSpeed)
    {
        _desiredSpeed = Mathf.Max(0f, desiredSpeed);
    }

    private void OnDestroy()
    {
        // Best-effort cleanup: remove overlays from the node's last known vicinity.
        // Any cells missed here will decay naturally within overlayDecaySeconds.
        var gen = GameFlowManager.Instance?.dustGenerator;
        if (gen == null || _drumTrack == null) return;
        Vector2Int cell = _drumTrack.CellOf(_rb != null ? _rb.position : (Vector2)transform.position);
        for (int dx = -1; dx <= 1; dx++)
        for (int dy = -1; dy <= 1; dy++)
            gen.RemoveMineNodeOverlay(cell + new Vector2Int(dx, dy));
    }
}
