using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Vehicle-local release cue: a radial ring fill (driven by pulse 0→1) and a
/// ring of beat dots that count down remaining steps to the armed target.
///
/// Setup — add this component to the vehicle GameObject (or a child).
/// Assign the <see cref="releaseCue"/> field on Vehicle to this component.
///
/// The ring is drawn procedurally via LineRenderer so no sprite/shader asset
/// is required.  Each beat dot is a small SpriteRenderer quad spawned at
/// runtime from a configurable sprite (or Unity's built-in circle if left null).
/// </summary>
[DisallowMultipleComponent]
public class VehicleReleaseCue : MonoBehaviour
{
    // ------------------------------------------------------------------
    // Inspector
    // ------------------------------------------------------------------
    [Header("Ring")]
    [Tooltip("Radius of the countdown ring in world units.")]
    [SerializeField] private float ringRadius = 0.55f;

    [Tooltip("Width of the ring line in world units.")]
    [SerializeField] private float ringLineWidth = 0.06f;

    [Tooltip("Number of vertices used to approximate the full circle (higher = smoother).")]
    [Range(16, 64)]
    [SerializeField] private int ringSegments = 40;

    [Tooltip("Color of the ring at low urgency (pulse ≈ 0).")]
    [SerializeField] private Color ringColorIdle  = new Color(1f, 1f, 1f, 0.15f);

    [Tooltip("Color of the ring at full urgency (pulse ≈ 1).")]
    [SerializeField] private Color ringColorReady = new Color(1f, 0.85f, 0.2f, 0.90f);

    [Tooltip("How quickly the ring fill lerps toward its target value.")]
    [SerializeField] private float ringFillLerpSpeed = 10f;

    [Header("Beat Dots")]
    [Tooltip("Sprite for each beat dot. Leave null to use a procedural circle.")]
    [SerializeField] private Sprite dotSprite;

    [Tooltip("World-space radius at which dots are placed (should be slightly larger than ringRadius).")]
    [SerializeField] private float dotOrbitRadius = 0.70f;

    [Tooltip("World-space scale of each dot.")]
    [SerializeField] private float dotSize = 0.10f;

    [Tooltip("Sorting layer name shared with the vehicle sprite.")]
    [SerializeField] private string dotSortingLayer = "Default";

    [Tooltip("Sorting order for dots (drawn on top of ring).")]
    [SerializeField] private int dotSortingOrder = 2;

    [Tooltip("Color for a beat that has NOT yet passed (remaining).")]
    [SerializeField] private Color dotColorActive = new Color(1f, 1f, 1f, 0.90f);

    [Tooltip("Color for a beat that HAS already passed (spent).")]
    [SerializeField] private Color dotColorSpent  = new Color(1f, 1f, 1f, 0.15f);

    [Tooltip("Sorting order for the ring LineRenderer.")]
    [SerializeField] private int ringSortingOrder = 1;

    // ------------------------------------------------------------------
    // Runtime state
    // ------------------------------------------------------------------
    private LineRenderer   _ring;
    private float          _currentFill;   // 0→1, smoothed
    private float          _targetFill;

    private readonly List<SpriteRenderer> _dots = new List<SpriteRenderer>();
    private int _lastTotalBeats = -1;

    // ------------------------------------------------------------------
    // Unity
    // ------------------------------------------------------------------
    private void Awake()
    {
        BuildRing();
        SetVisible(false);
    }

    private void Update()
    {
        // Smooth the fill toward target.
        _currentFill = Mathf.Lerp(_currentFill, _targetFill, ringFillLerpSpeed * Time.deltaTime);

        bool active = _targetFill > 0.01f || _currentFill > 0.01f;
        SetVisible(active);

        if (active)
            UpdateRingMesh(_currentFill);
    }

    private void OnDestroy()
    {
        ClearDots();
    }

    // ------------------------------------------------------------------
    // Public API — called by Vehicle
    // ------------------------------------------------------------------

    /// <summary>
    /// Set the ring fill amount. 0 = empty (idle), 1 = full (release imminent).
    /// Called every frame from Vehicle.TickNoteTrail.
    /// </summary>
    public void SetFill(float pulse01)
    {
        _targetFill = Mathf.Clamp01(pulse01);
    }

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
    // Ring (LineRenderer)
    // ------------------------------------------------------------------
    private void BuildRing()
    {
        var go = new GameObject("ReleaseCueRing");
        go.transform.SetParent(transform, false);

        _ring = go.AddComponent<LineRenderer>();
        _ring.useWorldSpace       = false;
        _ring.loop                = false;
        _ring.widthMultiplier     = ringLineWidth;
        _ring.positionCount       = 2;   // minimum; UpdateRingMesh overrides this immediately
        _ring.shadowCastingMode   = UnityEngine.Rendering.ShadowCastingMode.Off;
        _ring.receiveShadows      = false;
        _ring.sortingLayerName    = dotSortingLayer;
        _ring.sortingOrder        = ringSortingOrder;
        _ring.material            = new Material(Shader.Find("Sprites/Default"));
        _ring.enabled             = false; // hidden until first real fill
    }

    /// <summary>
    /// Rebuilds the LineRenderer positions to represent a filled arc from
    /// 12 o'clock clockwise, covering <paramref name="fill"/> fraction of the circle.
    /// positionCount is set to exactly the number of points needed for the arc,
    /// so there are never any zero-collapsed vertices.
    /// </summary>
    private void UpdateRingMesh(float fill)
    {
        if (_ring == null) return;

        // Minimum 2 points so the LineRenderer doesn't complain; disable via enabled flag instead.
        int pointCount = Mathf.Max(2, Mathf.RoundToInt(fill * ringSegments));
        pointCount = Mathf.Clamp(pointCount, 2, ringSegments);

        _ring.positionCount = pointCount;
        _ring.loop = false; // only loop when fill == 1 (full circle)

        Color col = Color.Lerp(ringColorIdle, ringColorReady, fill);
        _ring.startColor = col;
        _ring.endColor   = col;

        for (int i = 0; i < pointCount; i++)
        {
            // Map i across the filled fraction of the full circle, starting at 12 o'clock.
            float t     = (float)i / Mathf.Max(1, pointCount - 1); // 0..1 along the arc
            float angle = t * fill * Mathf.PI * 2f - Mathf.PI * 0.5f;
            _ring.SetPosition(i, new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0f) * ringRadius);
        }

        // Close the loop only when visually complete.
        _ring.loop = fill >= 0.99f;
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
            sr.sprite       = dotSprite; // null = Unity renders nothing; assign a circle sprite in inspector
            sr.color        = dotColorSpent;
            sr.sortingLayerName = dotSortingLayer;
            sr.sortingOrder = dotSortingOrder;

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
        // Ring only renders when fill is above a small threshold — avoids a
        // degenerate 2-point stub at the top of the circle when pulse is near zero.
        if (_ring != null)
            _ring.enabled = visible && _currentFill > 0.03f;

        foreach (var sr in _dots)
            if (sr != null) sr.gameObject.SetActive(visible);
    }
}
