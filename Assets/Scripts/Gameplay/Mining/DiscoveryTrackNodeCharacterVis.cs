using UnityEngine;

[RequireComponent(typeof(Rigidbody2D))]
public class DiscoveryTrackNodeCharacterVis : MonoBehaviour
{
    [SerializeField] private DiscoveryTrackNodeCharacterVisConfig config;

    [Header("Sprites (outer/inner optional)")]
    [SerializeField] SpriteRenderer outerSprite;
    [SerializeField] SpriteRenderer innerSprite;

    [Header("General")]
    [SerializeField] Transform spritePivotOuter;
    [SerializeField] Transform spritePivotInner;

    const float kDefaultThinkToSwimSpeed = 0.12f; // used only when no locomotion profile has resolved yet
    const float kDefaultSwimToThinkSpeed = 0.06f;

    Rigidbody2D _rb;
    DiscoveryTrackNode _mineNode;
    DiscoveryTrackNodeBehaviorIntent _intent = DiscoveryTrackNodeBehaviorIntent.Thinking;
    bool _isSwimming;
    float _facedAngle;
    float _wobblePhase;

    private void Awake()
    {
        _rb = GetComponent<Rigidbody2D>();
        _mineNode = GetComponent<DiscoveryTrackNode>();
        if (!outerSprite) outerSprite = GetComponentInChildren<SpriteRenderer>(true);
        if (!spritePivotOuter && outerSprite) spritePivotOuter = outerSprite.transform;
        if (innerSprite && !spritePivotInner) spritePivotInner = innerSprite.transform;
        Debug.Assert(config != null,
            $"[DiscoveryTrackNodeCharacterVis] {name} has no config asset assigned — visuals will be inert.");
    }

    private void OnEnable()
    {
        if (_mineNode != null) _mineNode.OnBehaviorIntentChanged += HandleBehaviorIntentChanged;
    }

    private void OnDisable()
    {
        if (_mineNode != null) _mineNode.OnBehaviorIntentChanged -= HandleBehaviorIntentChanged;
    }

    private void HandleBehaviorIntentChanged(DiscoveryTrackNodeBehaviorIntent intent)
    {
        _intent = intent;
    }

    private void FixedUpdate()
    {
        if (config == null) return;

        Vector2 v = _rb != null ? _rb.linearVelocity : Vector2.zero;
        float speed = v.magnitude;

        ResolveVisualParams(out float spinRate, out float faceTurnRate, out float wobbleRange, out float thinkToSwimSpeed, out float swimToThinkSpeed);

        if (_isSwimming)
            _isSwimming = speed >= swimToThinkSpeed;
        else
            _isSwimming = speed >= thinkToSwimSpeed;

        bool thinkingAnim = _intent == DiscoveryTrackNodeBehaviorIntent.Thinking || !_isSwimming;
        bool escapeAnim = _intent == DiscoveryTrackNodeBehaviorIntent.Escaping;

        if (thinkingAnim)
        {
            float spinScale = escapeAnim ? config.escapeSpinBoost : 1f;
            _facedAngle = Mathf.Repeat(_facedAngle + spinRate * spinScale * Time.fixedDeltaTime, 360f);
            ApplyRotation(_facedAngle, 0f);
            return;
        }

        float target = Mathf.Atan2(v.y, v.x) * Mathf.Rad2Deg - 90f;
        _facedAngle = Mathf.MoveTowardsAngle(_facedAngle, target, faceTurnRate * Time.fixedDeltaTime);

        float wobbleHz = Mathf.Lerp(config.wobbleHzRange.x, config.wobbleHzRange.y, Mathf.Clamp01(speed));
        _wobblePhase += Time.fixedDeltaTime * wobbleHz * Mathf.PI * 2f;
        float wobble = Mathf.Sin(_wobblePhase) * wobbleRange;

        ApplyRotation(_facedAngle, wobble);
    }

    private void ResolveVisualParams(out float spinRate, out float faceTurnRate, out float wobbleRange, out float thinkToSwimSpeed, out float swimToThinkSpeed)
    {
        spinRate = config.defaultThinkingSpinDegPerSec;
        faceTurnRate = config.defaultFaceTurnDegPerSec;
        wobbleRange = config.defaultWobbleDeg;

        var locomotion = _mineNode != null ? _mineNode.ActiveLocomotionProfile : null;
        if (locomotion == null)
        {
            thinkToSwimSpeed = kDefaultThinkToSwimSpeed;
            swimToThinkSpeed = kDefaultSwimToThinkSpeed;
            return;
        }

        swimToThinkSpeed = Mathf.Max(0f, locomotion.baseSpeed * 0.1f);
        thinkToSwimSpeed = Mathf.Max(swimToThinkSpeed, locomotion.baseSpeed * 0.25f);
    }

    private void ApplyRotation(float baseDeg, float wobbleOffsetDeg)
    {
        if (spritePivotOuter)
            spritePivotOuter.localRotation = Quaternion.Euler(0, 0, baseDeg + wobbleOffsetDeg);

        if (spritePivotInner)
            spritePivotInner.localRotation = Quaternion.Euler(0, 0, baseDeg + wobbleOffsetDeg * config.innerCounterFactor);
    }
}
