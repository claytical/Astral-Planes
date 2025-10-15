using UnityEngine;
using UnityEngine.Rendering;

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
    private bool _lockTint;
    private Color _tint;
    float thinkTimer;
    float lastPlanReadyTime;
    float facedAngle;  // current facing
    float wobblePhase;
    float wobbleHz;

    public void Configure(MusicalRole role, MusicalPhase phase)
    {
        // Base per-role personality
        float baseFaceTurn = 0f, baseWobHz = 0f, baseWobDeg = 0f, baseThinkSpin = 0f;
        Vector2 baseThinkBurst = Vector2.zero;
        switch (role)
        {
            case MusicalRole.Bass:
                baseFaceTurn = 240f; baseWobHz = 0.35f; baseWobDeg = 8f; baseThinkSpin = 240f; baseThinkBurst = new Vector2(0.18f, 0.28f);
                break;
            case MusicalRole.Harmony:
                baseFaceTurn = 300f; baseWobHz = 0.45f; baseWobDeg = 6f; baseThinkSpin = 360f; baseThinkBurst = new Vector2(0.14f, 0.24f);
                break;
            case MusicalRole.Groove:
                baseFaceTurn = 330f; baseWobHz = 0.55f; baseWobDeg = 7f; baseThinkSpin = 420f; baseThinkBurst = new Vector2(0.12f, 0.22f);
                break;
            case MusicalRole.Lead:
            default:
                baseFaceTurn = 420f; baseWobHz = 0.70f; baseWobDeg = 5f; baseThinkSpin = 540f; baseThinkBurst = new Vector2(0.10f, 0.18f);
                break;
        }

        // Phase modifiers
        float phaseSpeedMul = phase switch
        {
            MusicalPhase.Establish => 0.85f,
            MusicalPhase.Evolve    => 1.00f,
            MusicalPhase.Intensify => 1.20f,
            MusicalPhase.Release   => 0.85f,
            MusicalPhase.Wildcard  => 1.30f,
            MusicalPhase.Pop       => 1.05f,
            _ => 1f
        };

        faceTurnDegPerSec   = Mathf.Lerp(faceTurnDegPerSec,   baseFaceTurn * phaseSpeedMul, 1f);
        wobbleHz            = Mathf.Lerp(wobbleHz,            baseWobHz   * phaseSpeedMul, 1f);
        wobbleDeg           = Mathf.Lerp(wobbleDeg,           baseWobDeg,                  1f);
        frenzySpinDegPerSec = Mathf.Lerp(frenzySpinDegPerSec, baseThinkSpin * phaseSpeedMul, 1f);
        thinkBurstRange     = Vector2.Lerp(thinkBurstRange,   baseThinkBurst / phaseSpeedMul, 1f);
        thinkDampen         = Mathf.Lerp(thinkDampen,         6f, 1f);
    }
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

    public void SetTint(Color c) { _lockTint = true; _tint = c; Paint(c); }
    public void ClearTintLock()  { _lockTint = false; }
    private void Paint(Color c) { 
        var outc = c; outc.a = innerSprite.color.a; 
        innerSprite.color = outc;
        outc = c; outc.a = outerSprite.color.a;
        outerSprite.color = outc;
    }
    void ApplyRotation(float baseDeg, float wobbleOffsetDeg)
    {
        if (spritePivotOuter)
            spritePivotOuter.localRotation = Quaternion.Euler(0,0, baseDeg + wobbleOffsetDeg);

        if (spritePivotInner)
            spritePivotInner.localRotation = Quaternion.Euler(0,0, baseDeg + wobbleOffsetDeg * innerCounterFactor);
    }
    
}
