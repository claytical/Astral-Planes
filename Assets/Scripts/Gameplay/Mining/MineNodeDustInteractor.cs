using UnityEngine;
using System.Collections.Generic;

[RequireComponent(typeof(Rigidbody2D))]
public class MineNodeDustInteractor : MonoBehaviour
{
    private bool InsideDust { get; set; }
    private CosmicDust CurrentDust { get; set; }
    public int dustPhysicsLayer = -1;

    [Header("Multipliers while in dust (node-specific)")]
    [Tooltip("Clamp max speed while inside dust (multiplies your locomotion maxSpeed).")]
    public float speedCapMul = 0.9f;

    [Tooltip("Extra braking applied per FixedUpdate while inside dust.")]
    public float extraBrake = 0.25f;

    [Header("Carving")]
    [Tooltip("Whether this node is allowed to carve dust into the maze grid.")]
    public bool enableCarving = true;
    
    [Tooltip("How many grid cells wide the carved strip should be (1 = single cell, 3 = trench).")]
    public int carveWidthCells = 1;
    // === NEW: Maze carving hooks ===
    [Header("Maze Tinting")]
    [Tooltip("If true, this node will tint adjacent dust with its MusicalRole as it moves through corridors.")]
    public bool carveMaze = true; // kept as carveMaze to avoid breaking prefab references; controls tinting

    [SerializeField] private float edgeHugForce = 2f;

    private float carveIntervalSeconds = 0.08f;
    private float carveAppetiteMul     = 1.0f;
    private bool _ignoredDustCollisions = false;
    [Header("Dust Collision Ignore (MineNode only)")]
    [SerializeField] private LayerMask dustTerrainMask;   // should include CosmicDust (layer 7)
    [SerializeField] private float dustIgnoreQueryRadiusWorld = 2.0f;
    [SerializeField] private int dustIgnoreMaxHits = 32;

    private readonly Collider2D[] _dustIgnoreHits = new Collider2D[64];
    [SerializeField] private int dustLayerIndex = 7; // CosmicDust
    private Collider2D[] _myColliders;
    private readonly HashSet<int> _ignoredDustColliderIds = new HashSet<int>();

    private float _carveTimer;
    private int _dustCellsCarved = 0;
    private Rigidbody2D _rb;
    private DrumTrack _drumTrack;
    private MineNode _node;
    private float _desiredSpeed = 0f;
    private float _desiredSpeedFloor = 0.25f; // prevents cap collapsing to ~0
    // Track which cells we've already carved so we don't burn budget twice on the same spot.
    private readonly HashSet<Vector2Int> _carvedCells = new HashSet<Vector2Int>();
    private bool _hasDustContactPoint;
    private Vector2 _lastDustContactPoint;

    void Awake()
    {
        _rb = GetComponent<Rigidbody2D>();
        if (_rb == null) TryGetComponent(out _rb);
        _myColliders = GetComponentsInChildren<Collider2D>();
        _node = GetComponent<MineNode>();
        _drumTrack = (_node != null) ? _node.DrumTrack : null;
        // MineNode now physically collides with dust walls so it stays in corridors naturally.
        // Layer-wide dust collision ignore removed.

    }

    void FixedUpdate()
{
    if (_rb == null) return;
    if (_drumTrack == null) return;

    // ------------------------------------------------------------
    // GRID AUTHORITY: determine dust from grid, not collisions
    // ------------------------------------------------------------
    Vector2 worldPos = _rb.position;
    Vector2Int cell = _drumTrack.CellOf(worldPos);
    bool inDust = _drumTrack.HasDustAt(cell);

    // ------------------------------------------------------------
    // DUST FEEL (optional but recommended)
    // Physics is "feel", not authority
    // ------------------------------------------------------------
    if (inDust)
    {
        // Speed cap
        float desired = Mathf.Max(_desiredSpeed, _desiredSpeedFloor);
        float cap = desired * Mathf.Max(0.05f, speedCapMul);

        float speed = _rb.linearVelocity.magnitude;
        if (speed > cap && speed > 0.0001f)
            _rb.linearVelocity = _rb.linearVelocity.normalized * cap;

        // Extra braking
        if (_rb.linearVelocity.sqrMagnitude > 0.0001f)
        {
            Vector2 brake = -_rb.linearVelocity * extraBrake;
            _rb.AddForce(brake, ForceMode2D.Force);
        }
    }

    // ------------------------------------------------------------
    // CARVING (Phase 3A)
    // ------------------------------------------------------------
    if (!carveMaze) return;

    _carveTimer += Time.fixedDeltaTime;
    if (_carveTimer < carveIntervalSeconds) return;
    _carveTimer = 0f;

    // Tint adjacent solid dust with this node's MusicalRole
    var gen = GameFlowManager.Instance?.dustGenerator;
    if (gen != null && _node != null)
    {
        MusicalRole role = _node.GetImprintRole();
        for (int dx = -1; dx <= 1; dx++)
        for (int dy = -1; dy <= 1; dy++)
        {
            if (dx == 0 && dy == 0) continue;
            var neighbor = cell + new Vector2Int(dx, dy);
            if (_drumTrack.HasDustAt(neighbor))
                gen.TintDustCellWithRole(neighbor, role);
        }
    }

    // Edge-hugging: apply small force toward nearest dust wall
    Vector2 edgeDir = Vector2.zero;
    int dustNeighborCount = 0;
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
    {
        edgeDir = edgeDir.normalized;
        _rb.AddForce(edgeDir * edgeHugForce, ForceMode2D.Force);
    }
}
    private bool TryGetDustFromCollision(Collision2D coll, out CosmicDust dust)
    {
        // Colliders are often on a child under a CosmicDust parent.
        dust = coll.collider != null ? coll.collider.GetComponentInParent<CosmicDust>() : null;
        return dust != null;
    }
    private void OnCollisionEnter2D(Collision2D coll)
    {
        // MineNode now physically collides with dust walls (they act as corridor boundaries).
        // No dust ignore — collisions are handled naturally by the physics engine.
    }
    private void OnCollisionStay2D(Collision2D coll)
    {
        // No dust ignore needed.
    }
    private void OnCollisionExit2D(Collision2D coll)
    {
        if (!InsideDust || CurrentDust == null) return;
        if (!TryGetDustFromCollision(coll, out var dust) || dust != CurrentDust) return;

        InsideDust  = false;
        CurrentDust = null;
    }
    private bool TryIgnoreDustCollider(Collider2D dustCol)
    {
        if (dustCol == null) return false;
        if (dustCol.gameObject.layer != dustLayerIndex) return false;

        int id = dustCol.GetInstanceID();
        if (_ignoredDustColliderIds.Contains(id)) return true;

        if (_myColliders == null || _myColliders.Length == 0)
            _myColliders = GetComponentsInChildren<Collider2D>();

        for (int i = 0; i < _myColliders.Length; i++)
        {
            var mineCol = _myColliders[i];
            if (mineCol == null) continue;
            Physics2D.IgnoreCollision(mineCol, dustCol, true);
        }

        _ignoredDustColliderIds.Add(id);
        return true;
    }
    public void SetLevelAuthority(DrumTrack drumTrack)
    {
        _drumTrack = drumTrack;
    }
    public void SetDesiredSpeed(float desiredSpeed)
    {
        _desiredSpeed = Mathf.Max(0f, desiredSpeed);
    }
    public void ConfigureCarving(float intervalSeconds, float appetiteMul)
    {
        carveIntervalSeconds = Mathf.Max(0.01f, intervalSeconds);
        carveAppetiteMul     = Mathf.Max(0.05f, appetiteMul);
    }
    
}
