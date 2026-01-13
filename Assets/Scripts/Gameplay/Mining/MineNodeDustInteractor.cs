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

    [Tooltip("How strongly we follow dust lateral/cross-current suggestions.")]
    public float lateralNudgeMul = 1.0f;

    [Tooltip("How strongly we apply dust turbulence wobble.")]
    public float turbulenceMul = 1.0f;

    [Header("Carving")]
    [Tooltip("Whether this node is allowed to carve dust into the maze grid.")]
    public bool enableCarving = true;

    [Tooltip("Maximum number of grid cells this node can clear across its lifetime.")]
    public int maxDustCellsToCarve = 0;

    [Tooltip("How many grid cells wide the carved strip should be (1 = single cell, 3 = trench).")]
    public int carveWidthCells = 1;
    // === NEW: Maze carving hooks ===
    [Header("Maze Carving")]
    [Tooltip("If true, this node will chew a path through the dust maze as it moves.")]
    public bool carveMaze = true;

    [Tooltip("Seconds between carve ticks (how often we nibble dust).")]
    public float carveIntervalSeconds = 0.08f;

    [Tooltip("Appetite multiplier passed into CosmicDustGenerator.ErodeDustDisk.")]
    public float carveAppetiteMul = 1.0f;
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
        if (dustPhysicsLayer >= 0)
            Physics2D.IgnoreLayerCollision(gameObject.layer, dustPhysicsLayer, true);

    }
    private void IgnoreDustCollidersNearNode()
    {
        if (_myColliders == null || _myColliders.Length == 0) return;

        Vector2 p = _rb != null ? _rb.position : (Vector2)transform.position;

        int n = Physics2D.OverlapCircleNonAlloc(p, dustIgnoreQueryRadiusWorld, _dustIgnoreHits, dustTerrainMask);
        if (n <= 0) return;

        for (int i = 0; i < n; i++)
        {
            var dustCol = _dustIgnoreHits[i];
            if (dustCol == null) continue;

            int id = dustCol.GetInstanceID();
            if (_ignoredDustColliderIds.Contains(id)) continue;

            for (int c = 0; c < _myColliders.Length; c++)
            {
                var my = _myColliders[c];
                if (my == null) continue;
                Physics2D.IgnoreCollision(my, dustCol, true);
            }

            _ignoredDustColliderIds.Add(id);
        }
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

        // Optional per-dust behavior
        if (_drumTrack.TryGetDustAt(cell, out var dust) && dust != null)
        {
            if (dust.behavior == CosmicDust.DustBehavior.CrossCurrent && dust.lateralForce > 0f)
            {
                Vector2 v = _rb.linearVelocity;
                if (v.sqrMagnitude > 0.0001f)
                {
                    Vector2 side = new Vector2(-v.y, v.x).normalized;
                    _rb.AddForce(side * dust.lateralForce * lateralNudgeMul, ForceMode2D.Force);
                }
            }

            if (dust.behavior == CosmicDust.DustBehavior.Turbulent && dust.turbulence > 0f)
            {
                Vector2 noise =
                    Random.insideUnitCircle.normalized *
                    (dust.turbulence * turbulenceMul);
                _rb.AddForce(noise, ForceMode2D.Force);
            }

            if (dust.behavior == CosmicDust.DustBehavior.StaticCling)
            {
                _rb.AddForce(-_rb.linearVelocity * 0.5f, ForceMode2D.Force);
            }
        }
    }

    // ------------------------------------------------------------
    // CARVING (Phase 3A)
    // ------------------------------------------------------------
    if (!carveMaze || !enableCarving) return;

    _carveTimer += Time.fixedDeltaTime;
    if (_carveTimer < carveIntervalSeconds) return;
    _carveTimer = 0f;

    // Determine carve target:
    // - If inside dust: carve current cell
    // - Else: carve forward cell IF it contains dust
    Vector3 carveWorld = transform.position;

    if (!inDust)
    {
        Vector2 v = _rb.linearVelocity;
        if (v.sqrMagnitude < 0.0001f)
            return;

        float step = _drumTrack.GetCellWorldSize() * 0.55f;
        Vector2 ahead = worldPos + v.normalized * step;
        Vector2Int aheadCell = _drumTrack.CellOf(ahead);

        if (!_drumTrack.HasDustAt(aheadCell))
            return;

        carveWorld = _drumTrack.GridToWorldPosition(aheadCell);
        inDust = true;
    }

    MusicalPhase phase = _drumTrack.GetCurrentPhaseSafe();

    float healDelay =
        (_node != null) ? _node.GetCorridorHealDelaySeconds() : -1f;

    Color imprintColor =
        (_node != null) ? _node.GetImprintColor() : Color.white;

    Color imprintShadowColor =
        (_node != null) ? _node.GetImprintShadowColor() : Color.black;

    float hardness01 =
        (_node != null) ? _node.GetImprintHardness() : 0f;

    // Width in CELLS, not world radius
    int resolveRadiusCells = Mathf.Max(0, (carveWidthCells - 1) / 2);

    _drumTrack.CarveTemporaryCellFromMineNode(
        carveWorld,
        phase,
        healDelay,
        imprintColor,
        imprintShadowColor,
        hardness01,
        resolveRadiusCells
    );

    if (_node != null)
        _node.NotifyDustErodedAt(carveWorld);
}

    private bool TryGetDustFromCollision(Collision2D coll, out CosmicDust dust)
    {
        // Colliders are often on a child under a CosmicDust parent.
        dust = coll.collider != null ? coll.collider.GetComponentInParent<CosmicDust>() : null;
        return dust != null;
    }

    private void OnCollisionEnter2D(Collision2D coll)
    {
        if (coll == null || coll.collider == null) return;

        // Option 2.2: MineNodes never physically collide with dust.
        // Convert the first dust hit into an ignored collider so we don't pin.
        if (TryIgnoreDustCollider(coll.collider))
        {
            // We do NOT rely on TryGetDustFromCollision anymore.
            // Dust state + carving should be grid-driven in FixedUpdate.
            return;
        }
        Debug.Log($"[MN:DUST_HIT] hit={coll.collider.name} type={coll.collider.GetType().Name} layer={coll.collider.gameObject.layer} isTrigger={coll.collider.isTrigger}");

        // Non-dust collisions (Vehicle, walls, etc.) proceed normally.
    }

    private void OnCollisionStay2D(Collision2D coll)
    {
        if (coll == null || coll.collider == null) return;
        TryIgnoreDustCollider(coll.collider);
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
