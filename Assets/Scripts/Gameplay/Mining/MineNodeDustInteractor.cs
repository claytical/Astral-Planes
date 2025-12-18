using UnityEngine;
using System.Collections.Generic;
using UnityEditor.Experimental.GraphView;

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

    private float _carveTimer;
    private int _dustCellsCarved = 0;
    private Rigidbody2D _rb;

    // Track which cells we've already carved so we don't burn budget twice on the same spot.
    private readonly HashSet<Vector2Int> _carvedCells = new HashSet<Vector2Int>();

    void Awake()
    {
        _rb = GetComponent<Rigidbody2D>();
    }
    void FixedUpdate()
    {
        if (!carveMaze) return;

        var gfm = GameFlowManager.Instance;
        if (gfm == null || gfm.dustGenerator == null) return;

        // Only carve when actually moving a bit – keeps stationary nodes from grinding a crater.
        if (_rb == null || _rb.linearVelocity.sqrMagnitude < 0.0001f)
            return;

        _carveTimer += Time.fixedDeltaTime;
        if (_carveTimer >= carveIntervalSeconds)
        {
            _carveTimer = 0f;
            // Use the node’s current world position and appetite multiplier.
            
            gfm.dustGenerator.ErodeDustDisk(transform.position, carveAppetiteMul);
            // AFTER erosion succeeds:
            var node = GetComponent<MineNode>();

            if (node != null)
                node.NotifyDustErodedAt(transform.position);

        }
    }

    void OnTriggerEnter2D(Collider2D other)
    {
        if (!other.TryGetComponent(out CosmicDust dust)) return;
        CurrentDust = dust;
        InsideDust  = true;
    }

    void OnTriggerStay2D(Collider2D other)
    {
        if (!InsideDust || CurrentDust == null) return;
        if (!other.TryGetComponent(out CosmicDust dust) || dust != CurrentDust) return;

        // === 1) Apply *environmental feel* based on the dust’s behavior fields. ===
        var vel = _rb.linearVelocity;
        if (vel.sqrMagnitude > 0.0001f)
        {
            // Cap top speed while inside (like the Vehicle handler does)
            float cap = vel.magnitude * speedCapMul;
            if (_rb.linearVelocity.magnitude > cap)
                _rb.linearVelocity = vel.normalized * cap;
        }

        // Thicken the air: extra braking
        if (_rb.linearVelocity.sqrMagnitude > 0.0001f)
        {
            _rb.AddForce(-_rb.linearVelocity.normalized * extraBrake, ForceMode2D.Force);
        }

        // Lateral cross-current pulse (immediate nudge on enter; gentle bias while staying)
        if (CurrentDust.behavior == CosmicDust.DustBehavior.CrossCurrent && CurrentDust.lateralForce > 0f)
        {
            Vector2 v = _rb.linearVelocity;
            if (v.sqrMagnitude > 0.0001f)
            {
                Vector2 side = new Vector2(-v.y, v.x).normalized;
                _rb.AddForce(side * (CurrentDust.lateralForce * 0.25f * lateralNudgeMul), ForceMode2D.Force);
            }
        }

        // Turbulence wobble
        if (CurrentDust.behavior == CosmicDust.DustBehavior.Turbulent && CurrentDust.turbulence > 0f)
        {
            Vector2 noise = Random.insideUnitCircle.normalized *
                            (CurrentDust.turbulence * 0.05f * turbulenceMul);
            _rb.AddForce(noise, ForceMode2D.Force);
        }

        // StaticCling -> add temporary drag feel (small, continuous)
        if (CurrentDust.behavior == CosmicDust.DustBehavior.StaticCling)
        {
            _rb.AddForce(-_rb.linearVelocity * 0.5f * Time.fixedDeltaTime, ForceMode2D.Force);
        }

    }
    public void ConfigureCarving(float intervalSeconds, float appetiteMul)
    {
        carveIntervalSeconds = Mathf.Max(0.01f, intervalSeconds);
        carveAppetiteMul     = Mathf.Max(0.05f, appetiteMul);
    }
    void OnTriggerExit2D(Collider2D other)
    {
        if (!other.TryGetComponent(out CosmicDust dust) || dust != CurrentDust) return;
        InsideDust  = false;
        CurrentDust = null;
    }


}
