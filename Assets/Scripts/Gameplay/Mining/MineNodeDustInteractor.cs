using UnityEngine;

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

    private Rigidbody2D _rb;

    void Awake()
    {
        _rb = GetComponent<Rigidbody2D>();
    }

    void OnTriggerEnter2D(Collider2D other)
    {
        if (!other.TryGetComponent(out CosmicDust dust)) return;
        CurrentDust = dust;
        InsideDust = true;
    }

    void OnTriggerStay2D(Collider2D other)
    {
        if (!InsideDust || CurrentDust == null) return;
        if (!other.TryGetComponent(out CosmicDust dust) || dust != CurrentDust) return;

        // Apply *environmental feel* based on the dust’s behavior fields.
        var vel = _rb.linearVelocity;
        if (vel.sqrMagnitude > 0.0001f)
        {
            // Cap top speed while inside (like the Vehicle handler does)
            float cap = vel.magnitude * speedCapMul;
            if (_rb.linearVelocity.magnitude > cap) _rb.linearVelocity = vel.normalized * cap;
        }

        // Thicken the air: extra braking
        _rb.AddForce(-_rb.linearVelocity.normalized * (extraBrake), ForceMode2D.Force);

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
            Vector2 noise = Random.insideUnitCircle.normalized * (CurrentDust.turbulence * 0.05f * turbulenceMul);
            _rb.AddForce(noise, ForceMode2D.Force);
        }

        // StaticCling -> add temporary drag feel (small, continuous)
        if (CurrentDust.behavior == CosmicDust.DustBehavior.StaticCling)
        {
            _rb.AddForce(-_rb.linearVelocity * 0.5f * Time.fixedDeltaTime, ForceMode2D.Force);
        }
    }

    void OnTriggerExit2D(Collider2D other)
    {
        if (!other.TryGetComponent(out CosmicDust dust) || dust != CurrentDust) return;
        InsideDust = false;
        CurrentDust = null;
    }
}
