using UnityEngine;

[RequireComponent(typeof(Rigidbody2D))]
public class MineNodeDustInteractor : MonoBehaviour
{
    public bool insideDust { get; private set; }
    public CosmicDust currentDust { get; private set; }

    [Header("Multipliers while in dust (node-specific)")]
    [Tooltip("Clamp max speed while inside dust (multiplies your locomotion maxSpeed).")]
    public float speedCapMul = 0.9f;
    [Tooltip("Extra braking applied per FixedUpdate while inside dust.")]
    public float extraBrake = 0.25f;
    [Tooltip("How strongly we follow dust lateral/cross-current suggestions.")]
    public float lateralNudgeMul = 1.0f;
    [Tooltip("How strongly we apply dust turbulence wobble.")]
    public float turbulenceMul = 1.0f;

    private Rigidbody2D rb;

    void Awake()
    {
        rb = GetComponent<Rigidbody2D>();
    }

    void OnTriggerEnter2D(Collider2D other)
    {
        if (!other.TryGetComponent(out CosmicDust dust)) return;
        currentDust = dust;
        insideDust = true;
    }

    void OnTriggerStay2D(Collider2D other)
    {
        if (!insideDust || currentDust == null) return;
        if (!other.TryGetComponent(out CosmicDust dust) || dust != currentDust) return;

        // Apply *environmental feel* based on the dust’s behavior fields.
        var vel = rb.linearVelocity;
        if (vel.sqrMagnitude > 0.0001f)
        {
            // Cap top speed while inside (like the Vehicle handler does)
            float cap = vel.magnitude * speedCapMul;
            if (rb.linearVelocity.magnitude > cap) rb.linearVelocity = vel.normalized * cap;
        }

        // Thicken the air: extra braking
        rb.AddForce(-rb.linearVelocity.normalized * (extraBrake), ForceMode2D.Force);

        // Lateral cross-current pulse (immediate nudge on enter; gentle bias while staying)
        if (currentDust.behavior == CosmicDust.DustBehavior.CrossCurrent && currentDust.lateralForce > 0f)
        {
            Vector2 v = rb.linearVelocity;
            if (v.sqrMagnitude > 0.0001f)
            {
                Vector2 side = new Vector2(-v.y, v.x).normalized;
                rb.AddForce(side * (currentDust.lateralForce * 0.25f * lateralNudgeMul), ForceMode2D.Force);
            }
        }

        // Turbulence wobble
        if (currentDust.behavior == CosmicDust.DustBehavior.Turbulent && currentDust.turbulence > 0f)
        {
            Vector2 noise = Random.insideUnitCircle.normalized * (currentDust.turbulence * 0.05f * turbulenceMul);
            rb.AddForce(noise, ForceMode2D.Force);
        }

        // StaticCling -> add temporary drag feel (small, continuous)
        if (currentDust.behavior == CosmicDust.DustBehavior.StaticCling)
        {
            rb.AddForce(-rb.linearVelocity * 0.5f * Time.fixedDeltaTime, ForceMode2D.Force);
        }
    }

    void OnTriggerExit2D(Collider2D other)
    {
        if (!other.TryGetComponent(out CosmicDust dust) || dust != currentDust) return;
        insideDust = false;
        currentDust = null;
    }
}
