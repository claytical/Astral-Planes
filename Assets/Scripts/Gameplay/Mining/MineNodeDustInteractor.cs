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
    private DrumTrack _drumTrack;
    private MineNode _node;
    private float _desiredSpeed = 0f;
    private float _desiredSpeedFloor = 0.25f; // prevents cap collapsing to ~0
    // Track which cells we've already carved so we don't burn budget twice on the same spot.
    private readonly HashSet<Vector2Int> _carvedCells = new HashSet<Vector2Int>();

    void Awake()
    {
        _rb = GetComponent<Rigidbody2D>();
        _node = GetComponent<MineNode>();
    }
    void FixedUpdate()
    {
        if (!carveMaze) return;
        if (!enableCarving) return;
        if (_rb == null) return;
        if (_drumTrack == null) return;

        _carveTimer += Time.fixedDeltaTime;
        if (_carveTimer < carveIntervalSeconds) return;

        _carveTimer = 0f;

        // Phase comes from DrumTrack's level context (avoid GFM).
        MusicalPhase phase = _drumTrack.GetCurrentPhaseSafe();

        float healDelay = (_node != null) ? _node.GetCorridorHealDelaySeconds() : -1f;
        Color imprintColor = _node.GetImprintColor();
        float hardness01   = _node.GetImprintHardness();

        _drumTrack.CarveTemporaryDiskFromMineNode(
            transform.position,
            carveAppetiteMul,
            phase,
            healDelay,
            imprintColor,
            hardness01
        );
        if (_node != null) _node.NotifyDustErodedAt(transform.position);
    }

    public void SetLevelAuthority(DrumTrack drumTrack)
    {
        _drumTrack = drumTrack;
    }

    public void SetDesiredSpeed(float desiredSpeed)
    {
        _desiredSpeed = Mathf.Max(0f, desiredSpeed);
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
// Option A: cap based on MineNode's intended speed.
// This is a real cap, not a per-frame decay.
            float desired = Mathf.Max(_desiredSpeed, _desiredSpeedFloor);
            float cap = desired * Mathf.Max(0.05f, speedCapMul);

            float speed = _rb.linearVelocity.magnitude;
            if (speed > cap && speed > 0.0001f)
            {
                _rb.linearVelocity = _rb.linearVelocity.normalized * cap;
            }

        }

        // Thicken the air: extra braking
        if (_rb.linearVelocity.sqrMagnitude > 0.0001f)
        {
// Proportional brake: scales with current speed so it doesn't "pin" the node at low velocity.
            float speed = _rb.linearVelocity.magnitude;
            if (speed > 0.0001f)
            {
                // extraBrake is now interpreted as "fraction of speed to damp per second" style.
                Vector2 brake = -_rb.linearVelocity * extraBrake;
                _rb.AddForce(brake, ForceMode2D.Force);
            }
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
            _rb.AddForce(-_rb.linearVelocity * 0.5f, ForceMode2D.Force);        }

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
