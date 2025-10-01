using UnityEngine;

[RequireComponent(typeof(Rigidbody2D))]
public class MineNodeCharacterVis : MonoBehaviour
{
    [Header("Sprites (outer/inner optional)")]
    [SerializeField] SpriteRenderer outerSprite;  // your triangle outline
    [SerializeField] SpriteRenderer innerSprite;  // optional (tiny inner glyph)

    [Header("Thinking (at intersections / planning)")]
    [SerializeField] float frenzySpinDegPerSec = 480f;   // fast spin while “deciding”
    [SerializeField] Vector2 thinkBurstRange = new Vector2(0.12f, 0.25f); // sec
    [SerializeField] float thinkDampen = 7f;             // how quickly spin eases if planning stalls

    [Header("Swimming (on-rails wobble)")]
    [SerializeField] float faceTurnDegPerSec = 420f;     // snap-to-heading when it picks a path
    [SerializeField] float wobbleDeg = 12f;              // tailbeat wobble
    [SerializeField] Vector2 wobbleHzRange = new Vector2(2.5f, 4.5f); // scales with speed
    [SerializeField] float innerCounterFactor = -0.4f;   // inner rotates a bit opposite

    [Header("General")]
    [SerializeField] float minSpeedForSwim = 0.05f;      // below this → treat as thinking
    [SerializeField] Transform spritePivotOuter;         // rotate these; defaults to SR transform
    [SerializeField] Transform spritePivotInner;         // optional

    Rigidbody2D rb;
    MineNodeRailAgent agent;

    // state
    float thinkTimer;
    float lastPlanReadyTime;
    float facedAngle;  // current facing
    float wobblePhase;

    void Awake()
    {
        rb = GetComponent<Rigidbody2D>();
        agent = GetComponent<MineNodeRailAgent>();
        if (!outerSprite) outerSprite = GetComponentInChildren<SpriteRenderer>(true);
        if (!spritePivotOuter && outerSprite) spritePivotOuter = outerSprite.transform;
        if (innerSprite && !spritePivotInner) spritePivotInner = innerSprite.transform;
    }

    void OnEnable()
    {
        if (agent != null)
        {
            agent.OnPlanStart  += HandlePlanStart;
            agent.OnPlanReady  += HandlePlanReady;
        }
    }
    void OnDisable()
    {
        if (agent != null)
        {
            agent.OnPlanStart  -= HandlePlanStart;
            agent.OnPlanReady  -= HandlePlanReady;
        }
    }
    void FixedUpdate()
    {
        var v = rb ? rb.linearVelocity : Vector2.zero;
        float speed = v.magnitude;
        bool hasPath = agent ? agent.HasPath : speed > minSpeedForSwim; // fallback

        // Decide if we’re THINKING (spin) or SWIMMING (wobble)
        bool thinking = (!hasPath || speed < minSpeedForSwim || thinkTimer > 0f);

        if (thinking)
        {
            // Countdown frenzy burst; if it runs long, damp the spin
            if (thinkTimer > 0f) thinkTimer -= Time.fixedDeltaTime;
            float spin = frenzySpinDegPerSec * Time.fixedDeltaTime * (1f / (1f + thinkDampen * Mathf.Max(0f, -thinkTimer)));
            facedAngle = Mathf.Repeat(facedAngle + spin, 360f);
            ApplyRotation(facedAngle, wobbleOffsetDeg: 0f);
            return;
        }

        // SWIM: face movement quickly, with a small tailbeat wobble
        float target = Mathf.Atan2(v.y, v.x) * Mathf.Rad2Deg - 90f;
        facedAngle = Mathf.MoveTowardsAngle(facedAngle, target, faceTurnDegPerSec * Time.fixedDeltaTime);

        // wobble frequency scales with speed inside a range
        float wobbleHz = Mathf.Lerp(wobbleHzRange.x, wobbleHzRange.y, Mathf.Clamp01(speed));
        wobblePhase += Time.fixedDeltaTime * wobbleHz * Mathf.PI * 2f;
        float wob = Mathf.Sin(wobblePhase) * wobbleDeg;

        ApplyRotation(facedAngle, wob);
    }

    void HandlePlanStart()
    {
        // start/refresh a short frenzy burst
        thinkTimer = Random.Range(thinkBurstRange.x, thinkBurstRange.y);
    }

    void HandlePlanReady()
    {
        lastPlanReadyTime = Time.time;
        // quick snap toward new heading feels decisive
        var v = rb ? rb.linearVelocity : Vector2.zero;
        if (v.sqrMagnitude > 1e-4f)
            facedAngle = Mathf.Atan2(v.y, v.x) * Mathf.Rad2Deg - 90f;
        // kick wobble phase so the “swim” starts immediately
        wobblePhase = 0f;
    }


    void ApplyRotation(float baseDeg, float wobbleOffsetDeg)
    {
        if (spritePivotOuter)
            spritePivotOuter.localRotation = Quaternion.Euler(0,0, baseDeg + wobbleOffsetDeg);

        if (spritePivotInner)
            spritePivotInner.localRotation = Quaternion.Euler(0,0, baseDeg + wobbleOffsetDeg * innerCounterFactor);
    }
}
