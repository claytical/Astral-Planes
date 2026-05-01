using UnityEngine;

/// <summary>
/// Vehicle-local release cue: a filled circle that grows behind the vehicle,
/// colored with the pending note's track color, plus a step bar that
/// counts down remaining steps to the armed target.
///
/// Setup — add this component to the vehicle GameObject (or a child).
/// Assign the <see cref="releaseCue"/> field on Vehicle to this component.
///
/// The growing circle is a SpriteRenderer drawn BEHIND the vehicle (lower
/// sorting order), so white-outline vehicles remain visible on top of it.
/// </summary>
[DisallowMultipleComponent]
public class VehicleReleaseCue : MonoBehaviour
{
    // ------------------------------------------------------------------
    // Inspector
    // ------------------------------------------------------------------
    [Header("Growing Circle")]
    [Tooltip("Maximum world-space radius of the filled circle at pulse = 1.")]
    [SerializeField] private float circleMaxRadius = 0.65f;

    [Tooltip("Minimum world-space radius when the circle first becomes visible.")]
    [SerializeField] private float circleMinRadius = 0.35f;

    [Tooltip("Alpha of the circle at pulse = 0 (just appeared).")]
    [SerializeField] private float circleAlphaMin = 0.50f;

    [Tooltip("Alpha of the circle at pulse = 1 (fully grown).")]
    [SerializeField] private float circleAlphaMax = 0.90f;

    [Tooltip("Sorting layer shared with the vehicle sprite.")]
    [SerializeField] private string circleSortingLayer = "Default";

    [Tooltip("Sorting order for the circle — must be LOWER than the vehicle sprite's order so it renders behind.")]
    [SerializeField] private int circleSortingOrder = -1;

    [Tooltip("How quickly the circle size lerps toward its target value.")]
    [SerializeField] private float circleLerpSpeed = 10f;

    [Tooltip("Sprite to use for the filled circle. Assign Unity's built-in 'Knob' or any filled circle sprite. Leave null for a white quad fallback (less round).")]
    [SerializeField] private Sprite circleSprite;

    [Header("Step Bar")]
    [Tooltip("Sprite for the horizontal step-countdown bar. A 1×1 white quad works well.")]
    [SerializeField] private Sprite stepBarSprite;

    [Tooltip("Full width of the bar in world units (at max beats remaining).")]
    [SerializeField] private float stepBarWidth = 0.80f;

    [Tooltip("Height of the step bar in world units.")]
    [SerializeField] private float stepBarHeight = 0.07f;

    [Tooltip("World-space Y offset from the vehicle center (negative = below).")]
    [SerializeField] private float stepBarYOffset = -0.55f;

    [Tooltip("Sorting order for the step bar.")]
    [SerializeField] private int stepBarSortingOrder = 3;

    private SpriteRenderer _stepBar;
    private float _stepBarFullWidth = 1f;

    // ------------------------------------------------------------------
    // Runtime state
    // ------------------------------------------------------------------
    private SpriteRenderer  _circle;
    private float           _currentPulse;   // 0→1, smoothed
    private float           _targetPulse;
    private Color           _trackColor = Color.white;

    // ------------------------------------------------------------------
    // Unity
    // ------------------------------------------------------------------
    private void Awake()
    {
        BuildCircle();
        BuildStepBar();
        SetVisible(false);
    }

    private void Update()
    {
        // Snap off immediately when the cue is cleared — no fizzle/linger.
        if (_targetPulse <= 0f)
            _currentPulse = 0f;
        else
            _currentPulse = Mathf.Lerp(_currentPulse, _targetPulse, circleLerpSpeed * Time.deltaTime);

        // Use _targetPulse for visibility so the circle appears/disappears on musical time,
        // not on the lagged smoothed value.
        bool active = _targetPulse > 0.01f;
        SetVisible(active);

        if (active)
            UpdateCircle(_currentPulse);
    }

    // ------------------------------------------------------------------
    // Public API — called by Vehicle
    // ------------------------------------------------------------------

    /// <summary>
    /// Update the step countdown display.
    /// Called on every musical step tick while a note is armed.
    /// </summary>
    public void SetBeatsRemaining(int remaining, int total)
    {
        // Shrink step bar proportionally to beats remaining.
        if (_stepBar != null && total > 0)
        {
            float fraction = Mathf.Clamp01((float)remaining / total);
            float barW = Mathf.Max(0f, _stepBarFullWidth * fraction);
            _stepBar.transform.localScale = new Vector3(barW, stepBarHeight, 1f);
            // Anchor left edge so bar drains left-to-right.
            _stepBar.transform.localPosition = new Vector3(
                -(_stepBarFullWidth - barW) * 0.5f,
                stepBarYOffset,
                0f);
        }
    }

    // ------------------------------------------------------------------
    // Growing circle (SpriteRenderer)
    // ------------------------------------------------------------------
    private void BuildCircle()
    {
        var go = new GameObject("ReleaseCueCircle");
        go.transform.SetParent(transform, false);
        go.transform.localPosition = Vector3.zero;

        _circle = go.AddComponent<SpriteRenderer>();
        _circle.sprite           = circleSprite;   // assign a filled circle sprite in the Inspector
        _circle.sortingLayerName = circleSortingLayer;
        _circle.sortingOrder     = circleSortingOrder;
        _circle.color            = new Color(1f, 1f, 1f, 0f);
        _circle.enabled          = false;
    }

    private void UpdateCircle(float pulse)
    {
        if (_circle == null) return;

        float radius = Mathf.Lerp(circleMinRadius, circleMaxRadius, pulse);
        // Scale the GameObject so the sprite fills 'radius' world units.
        // A default Unity sprite is 1 unit wide at scale 1, so diameter = radius * 2.
        _circle.transform.localScale = Vector3.one * (radius * 2f);

        float alpha = Mathf.Lerp(circleAlphaMin, circleAlphaMax, pulse);
        _circle.color = new Color(_trackColor.r, _trackColor.g, _trackColor.b, alpha);
    }

    private void BuildStepBar()
    {
        var go = new GameObject("StepBar");
        go.transform.SetParent(transform, false);
        go.transform.localPosition = new Vector3(0f, stepBarYOffset, 0f);
        go.transform.localScale = new Vector3(stepBarWidth, stepBarHeight, 1f);

        _stepBar = go.AddComponent<SpriteRenderer>();
        _stepBar.sprite           = stepBarSprite;
        _stepBar.sortingLayerName = circleSortingLayer;
        _stepBar.sortingOrder     = stepBarSortingOrder;
        _stepBar.color            = new Color(1f, 1f, 1f, 0.75f);
        _stepBar.enabled          = false;

        _stepBarFullWidth = stepBarWidth;
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------
    private void SetVisible(bool visible)
    {
        if (_circle != null)
            _circle.enabled = visible;

        if (_stepBar != null)
            _stepBar.enabled = visible;

    }
}
