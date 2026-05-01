using UnityEngine;
[RequireComponent(typeof(Rigidbody2D))]
public class MineNodeCharacterVis : MonoBehaviour
{
    [Header("Sprites (outer/inner optional)")]
    [SerializeField] SpriteRenderer outerSprite;  // your triangle outline
    [SerializeField] SpriteRenderer innerSprite;  // optional (tiny inner glyph)

    [Header("Thinking (at intersections / planning)")]
    [SerializeField] float frenzySpinDegPerSec = 480f;   // fast spin while “deciding”

    [Header("Swimming (on-rails wobble)")]
    [SerializeField] float faceTurnDegPerSec = 420f;     // snap-to-heading when it picks a path
    [SerializeField] float wobbleDeg = 12f;              // tailbeat wobble
    [SerializeField] Vector2 wobbleHzRange = new Vector2(2.5f, 4.5f); // scales with speed
    [SerializeField] float innerCounterFactor = -0.4f;   // inner rotates a bit opposite

    [Header("General")]
    [SerializeField] float minSpeedForSwim = 0.05f;      // below this → treat as thinking
    [SerializeField] Transform spritePivotOuter;         // rotate these; defaults to SR transform
    [SerializeField] Transform spritePivotInner;         // optional

    private Rigidbody2D _rb;

    // state
    float facedAngle;  // current facing
    float wobblePhase;
    private void Awake()
    {
        _rb = GetComponent<Rigidbody2D>();
        if (!outerSprite) outerSprite = GetComponentInChildren<SpriteRenderer>(true);
        if (!spritePivotOuter && outerSprite) spritePivotOuter = outerSprite.transform;
        if (innerSprite && !spritePivotInner) spritePivotInner = innerSprite.transform;
    }
    private void FixedUpdate()
    {
        var v = _rb != null ? _rb.linearVelocity : Vector2.zero;
        float speed = v.magnitude;
 
        // Decide if we’re THINKING (spin) or SWIMMING (wobble)
        bool thinking = speed < minSpeedForSwim;

        if (thinking)
        {
            float spin = frenzySpinDegPerSec * Time.fixedDeltaTime;
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
    private void ApplyRotation(float baseDeg, float wobbleOffsetDeg)
    {
        if (spritePivotOuter)
            spritePivotOuter.localRotation = Quaternion.Euler(0,0, baseDeg + wobbleOffsetDeg);

        if (spritePivotInner)
            spritePivotInner.localRotation = Quaternion.Euler(0,0, baseDeg + wobbleOffsetDeg * innerCounterFactor);
    }
    
}
