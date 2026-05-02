using UnityEngine;

[RequireComponent(typeof(Rigidbody2D))]
public class MineNodeCharacterVis : MonoBehaviour
{
    [System.Serializable]
    private struct ArchetypeVisualProfile
    {
        public MineNodeLocomotionArchetype archetype;
        [Min(0f)] public float thinkingSpinDegPerSec;
        [Min(0f)] public float faceTurnDegPerSec;
        [Min(0f)] public float wobbleDeg;
    }

    [Header("Sprites (outer/inner optional)")]
    [SerializeField] SpriteRenderer outerSprite;
    [SerializeField] SpriteRenderer innerSprite;

    [Header("Locomotion Intent Animation")]
    [SerializeField] float defaultThinkingSpinDegPerSec = 480f;
    [SerializeField] float defaultFaceTurnDegPerSec = 420f;
    [SerializeField] float defaultWobbleDeg = 12f;
    [SerializeField] Vector2 wobbleHzRange = new Vector2(2.5f, 4.5f);
    [SerializeField] float innerCounterFactor = -0.4f;
    [SerializeField] float escapeSpinBoost = 1.2f;

    [Header("Profile Swim/Think Blend Thresholds")]
    [SerializeField] float defaultThinkToSwimSpeed = 0.12f;
    [SerializeField] float defaultSwimToThinkSpeed = 0.06f;

    [Header("Archetype Visual Overrides")]
    [SerializeField] ArchetypeVisualProfile[] archetypeVisualProfiles;

    [Header("General")]
    [SerializeField] Transform spritePivotOuter;
    [SerializeField] Transform spritePivotInner;

    Rigidbody2D _rb;
    MineNode _mineNode;
    MineNodeBehaviorIntent _intent = MineNodeBehaviorIntent.Thinking;
    bool _isSwimming;
    float _facedAngle;
    float _wobblePhase;

    private void Awake()
    {
        _rb = GetComponent<Rigidbody2D>();
        _mineNode = GetComponent<MineNode>();
        if (!outerSprite) outerSprite = GetComponentInChildren<SpriteRenderer>(true);
        if (!spritePivotOuter && outerSprite) spritePivotOuter = outerSprite.transform;
        if (innerSprite && !spritePivotInner) spritePivotInner = innerSprite.transform;
    }

    private void OnEnable()
    {
        if (_mineNode != null) _mineNode.OnBehaviorIntentChanged += HandleBehaviorIntentChanged;
    }

    private void OnDisable()
    {
        if (_mineNode != null) _mineNode.OnBehaviorIntentChanged -= HandleBehaviorIntentChanged;
    }

    private void HandleBehaviorIntentChanged(MineNodeBehaviorIntent intent)
    {
        _intent = intent;
    }

    private void FixedUpdate()
    {
        Vector2 v = _rb != null ? _rb.linearVelocity : Vector2.zero;
        float speed = v.magnitude;

        ResolveVisualParams(out float spinRate, out float faceTurnRate, out float wobbleRange, out float thinkToSwimSpeed, out float swimToThinkSpeed);

        if (_isSwimming)
            _isSwimming = speed >= swimToThinkSpeed;
        else
            _isSwimming = speed >= thinkToSwimSpeed;

        bool thinkingAnim = _intent == MineNodeBehaviorIntent.Thinking || !_isSwimming;
        bool escapeAnim = _intent == MineNodeBehaviorIntent.Escaping;

        if (thinkingAnim)
        {
            float spinScale = escapeAnim ? escapeSpinBoost : 1f;
            _facedAngle = Mathf.Repeat(_facedAngle + spinRate * spinScale * Time.fixedDeltaTime, 360f);
            ApplyRotation(_facedAngle, 0f);
            return;
        }

        float target = Mathf.Atan2(v.y, v.x) * Mathf.Rad2Deg - 90f;
        _facedAngle = Mathf.MoveTowardsAngle(_facedAngle, target, faceTurnRate * Time.fixedDeltaTime);

        float wobbleHz = Mathf.Lerp(wobbleHzRange.x, wobbleHzRange.y, Mathf.Clamp01(speed));
        _wobblePhase += Time.fixedDeltaTime * wobbleHz * Mathf.PI * 2f;
        float wobble = Mathf.Sin(_wobblePhase) * wobbleRange;

        ApplyRotation(_facedAngle, wobble);
    }

    private void ResolveVisualParams(out float spinRate, out float faceTurnRate, out float wobbleRange, out float thinkToSwimSpeed, out float swimToThinkSpeed)
    {
        spinRate = defaultThinkingSpinDegPerSec;
        faceTurnRate = defaultFaceTurnDegPerSec;
        wobbleRange = defaultWobbleDeg;
        thinkToSwimSpeed = defaultThinkToSwimSpeed;
        swimToThinkSpeed = defaultSwimToThinkSpeed;

        var locomotion = _mineNode != null ? _mineNode.ActiveLocomotionProfile : null;
        if (locomotion == null) return;

        swimToThinkSpeed = Mathf.Max(0f, locomotion.baseSpeed * 0.1f);
        thinkToSwimSpeed = Mathf.Max(swimToThinkSpeed, locomotion.baseSpeed * 0.25f);

        if (archetypeVisualProfiles == null) return;
        for (int i = 0; i < archetypeVisualProfiles.Length; i++)
        {
            if (archetypeVisualProfiles[i].archetype != locomotion.archetype) continue;
            if (archetypeVisualProfiles[i].thinkingSpinDegPerSec > 0f) spinRate = archetypeVisualProfiles[i].thinkingSpinDegPerSec;
            if (archetypeVisualProfiles[i].faceTurnDegPerSec > 0f) faceTurnRate = archetypeVisualProfiles[i].faceTurnDegPerSec;
            if (archetypeVisualProfiles[i].wobbleDeg > 0f) wobbleRange = archetypeVisualProfiles[i].wobbleDeg;
            return;
        }
    }

    private void ApplyRotation(float baseDeg, float wobbleOffsetDeg)
    {
        if (spritePivotOuter)
            spritePivotOuter.localRotation = Quaternion.Euler(0, 0, baseDeg + wobbleOffsetDeg);

        if (spritePivotInner)
            spritePivotInner.localRotation = Quaternion.Euler(0, 0, baseDeg + wobbleOffsetDeg * innerCounterFactor);
    }
}
