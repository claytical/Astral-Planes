using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Vehicle-local release cue: a filled circle that grows behind the vehicle,
/// colored with the pending note's track color, plus a ring of beat dots that
/// count down remaining steps to the armed target.
///
/// Setup — add this component to the vehicle GameObject (or a child).
/// Assign the <see cref="releaseCue"/> field on Vehicle to this component.
///
/// The growing circle is a SpriteRenderer drawn BEHIND the vehicle (lower
/// sorting order), so white-outline vehicles remain visible on top of it.
/// Each beat dot is a small SpriteRenderer quad spawned at runtime from a
/// configurable sprite (or Unity's built-in circle if left null).
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

    [Header("Beat Dots")]
    [Tooltip("Sprite for each beat dot. Leave null for a plain quad.")]
    [SerializeField] private Sprite dotSprite;

    [Tooltip("World-space radius at which dots are placed.")]
    [SerializeField] private float dotOrbitRadius = 0.75f;

    [Tooltip("World-space scale of each dot.")]
    [SerializeField] private float dotSize = 0.10f;

    [Tooltip("Sorting layer name shared with the vehicle sprite.")]
    [SerializeField] private string dotSortingLayer = "Default";

    [Tooltip("Sorting order for dots (drawn on top of circle, ideally on top of vehicle too).")]
    [SerializeField] private int dotSortingOrder = 2;

    [Tooltip("Color for a beat that has NOT yet passed (remaining).")]
    [SerializeField] private Color dotColorActive = new Color(1f, 1f, 1f, 0.90f);

    [Tooltip("Color for a beat that HAS already passed (spent).")]
    [SerializeField] private Color dotColorSpent  = new Color(1f, 1f, 1f, 0.15f);

    // ------------------------------------------------------------------
    // Runtime state
    // ------------------------------------------------------------------
    private SpriteRenderer  _circle;
    private float           _currentPulse;   // 0→1, smoothed
    private float           _targetPulse;
    private Color           _trackColor = Color.white;

    private readonly List<SpriteRenderer> _dots = new List<SpriteRenderer>();
    private int _lastTotalBeats = -1;

    // ------------------------------------------------------------------
    // Unity
    // ------------------------------------------------------------------
    private void Awake()
    {
        BuildCircle();
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

    private void OnDestroy()
    {
        ClearDots();
    }

    // ------------------------------------------------------------------
    // Public API — called by Vehicle
    // ------------------------------------------------------------------



    /// <summary>
    /// Update the beat-dot countdown display.
    /// Called on every musical step tick while a note is armed.
    /// </summary>
    public void SetBeatsRemaining(int remaining, int total)
    {
        if (total != _lastTotalBeats)
            RebuildDots(total);

        for (int i = 0; i < _dots.Count; i++)
        {
            if (_dots[i] == null) continue;
            // Dots are laid out clockwise from 12 o'clock.
            // Active dots are the rightmost `remaining` ones.
            bool isActive = i >= (_dots.Count - remaining);
            _dots[i].color = isActive ? dotColorActive : dotColorSpent;
            _dots[i].gameObject.SetActive(true);
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

    // ------------------------------------------------------------------
    // Beat dots (SpriteRenderers)
    // ------------------------------------------------------------------
    private void RebuildDots(int count)
    {
        ClearDots();
        _lastTotalBeats = count;

        for (int i = 0; i < count; i++)
        {
            var go = new GameObject($"ReleaseDot_{i}");
            go.transform.SetParent(transform, false);

            float angle = (i / (float)count) * Mathf.PI * 2f - Mathf.PI * 0.5f; // 12 o'clock first
            go.transform.localPosition = new Vector3(
                Mathf.Cos(angle) * dotOrbitRadius,
                Mathf.Sin(angle) * dotOrbitRadius,
                0f);

            go.transform.localScale = Vector3.one * dotSize;

            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite           = dotSprite;
            sr.color            = dotColorSpent;
            sr.sortingLayerName = dotSortingLayer;
            sr.sortingOrder     = dotSortingOrder;

            _dots.Add(sr);
        }
    }

    private void ClearDots()
    {
        foreach (var sr in _dots)
        {
            if (sr != null)
                Destroy(sr.gameObject);
        }
        _dots.Clear();
        _lastTotalBeats = -1;
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------
    private void SetVisible(bool visible)
    {
        if (_circle != null)
            _circle.enabled = visible;

        foreach (var sr in _dots)
            if (sr != null) sr.gameObject.SetActive(visible);
    }
}