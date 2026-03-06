using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Vehicle-local release cue: a sprite that scales up as the playhead approaches
/// the target step (pulse 0→1), and a ring of beat dots that count down remaining
/// steps to the armed target.
///
/// Velocity semantics: striking at the earliest moment of the window (~pulse 0)
/// commits at <see cref="velocityAtWindowOpen"/> (~50). Striking at the exact
/// step moment (pulse 1) commits at maximum velocity (127). The mapping is linear
/// so the player has full expressive control through placement timing.
///
/// Setup — add this component to the vehicle GameObject (or a child).
/// Assign the <see cref="releaseCue"/> field on Vehicle to this component.
/// Assign a circle/glow sprite to <see cref="cueSprite"/> in the Inspector.
/// </summary>
[DisallowMultipleComponent]
public class VehicleReleaseCue : MonoBehaviour
{
    // ------------------------------------------------------------------
    // Inspector — Scale Cue
    // ------------------------------------------------------------------
    [Header("Scale Cue")]
    [Tooltip("Sprite used for the scale cue indicator. Assign a soft circle or glow sprite.")]
    [SerializeField] private Sprite cueSprite;

    [Tooltip("World-space scale of the cue when pulse is 0 (window just opened).")]
    [SerializeField] private float cueScaleMin = 0.20f;

    [Tooltip("World-space scale of the cue at pulse = 1 (exact step, max velocity).")]
    [SerializeField] private float cueScaleMax = 0.80f;

    [Tooltip("Color of the cue at low urgency (pulse ≈ 0).")]
    [SerializeField] private Color cueColorIdle  = new Color(1f, 1f, 1f, 0.20f);

    [Tooltip("Color of the cue at full urgency (pulse ≈ 1).")]
    [SerializeField] private Color cueColorReady = new Color(1f, 0.85f, 0.2f, 0.90f);

    [Tooltip("How quickly the displayed scale lerps toward its target value.")]
    [SerializeField] private float scaleLerpSpeed = 12f;

    [Tooltip("Sorting layer name shared with the vehicle sprite.")]
    [SerializeField] private string cueSortingLayer = "Default";

    [Tooltip("Sorting order for the cue sprite (drawn below beat dots).")]
    [SerializeField] private int cueSortingOrder = 1;

    // ------------------------------------------------------------------
    // Inspector — Velocity Mapping
    // ------------------------------------------------------------------
    [Header("Velocity Mapping")]
    [Tooltip("MIDI velocity (0–127) committed when the player releases at the very start of the timing window (pulse ≈ 0).")]
    [Range(1f, 126f)]
    [SerializeField] private float velocityAtWindowOpen = 50f;

    // Max velocity is always 127 at pulse = 1 (no inspector field needed).

    // ------------------------------------------------------------------
    // Inspector — Beat Dots
    // ------------------------------------------------------------------
    [Header("Beat Dots")]
    [Tooltip("Sprite for each beat dot. Leave null to use a procedural circle.")]
    [SerializeField] private Sprite dotSprite;

    [Tooltip("World-space radius at which dots are placed.")]
    [SerializeField] private float dotOrbitRadius = 0.70f;

    [Tooltip("World-space scale of each dot.")]
    [SerializeField] private float dotSize = 0.10f;

    [Tooltip("Sorting layer name for dots.")]
    [SerializeField] private string dotSortingLayer = "Default";

    [Tooltip("Sorting order for dots (drawn on top of cue sprite).")]
    [SerializeField] private int dotSortingOrder = 2;

    [Tooltip("Color for a beat that has NOT yet passed (remaining).")]
    [SerializeField] private Color dotColorActive = new Color(1f, 1f, 1f, 0.90f);

    [Tooltip("Color for a beat that HAS already passed (spent).")]
    [SerializeField] private Color dotColorSpent  = new Color(1f, 1f, 1f, 0.15f);

    // ------------------------------------------------------------------
    // Runtime state
    // ------------------------------------------------------------------
    private SpriteRenderer _cueRenderer;
    private float          _currentScale;   // smoothed display scale
    private float          _targetPulse;    // raw 0→1 from Vehicle

    private readonly List<SpriteRenderer> _dots = new List<SpriteRenderer>();
    private int _lastTotalBeats = -1;

    // ------------------------------------------------------------------
    // Unity
    // ------------------------------------------------------------------
    private void Awake()
    {
        BuildCue();
        SetVisible(false);
    }

    private void Update()
    {
        bool active = _targetPulse > 0.01f;
        SetVisible(active);

        if (active)
        {
            float targetScale = Mathf.Lerp(cueScaleMin, cueScaleMax, _targetPulse);
            _currentScale = Mathf.Lerp(_currentScale, targetScale, scaleLerpSpeed * Time.deltaTime);

            _cueRenderer.transform.localScale = Vector3.one * _currentScale;
            _cueRenderer.color = Color.Lerp(cueColorIdle, cueColorReady, _targetPulse);
        }
        else
        {
            // Reset scale so it starts fresh next time.
            _currentScale = cueScaleMin;
        }
    }

    private void OnDestroy()
    {
        ClearDots();
    }

    // ------------------------------------------------------------------
    // Public API — called by Vehicle
    // ------------------------------------------------------------------

    /// <summary>
    /// Set the pulse amount. 0 = idle / window just opened, 1 = exact step moment.
    /// Called every frame from Vehicle.TickNoteTrail.
    /// </summary>
    public void SetFill(float pulse01)
    {
        _targetPulse = Mathf.Clamp01(pulse01);
    }

    /// <summary>
    /// Returns the MIDI velocity (50→127) that corresponds to the current pulse.
    /// Call this at commit time to replace the old impact-velocity approach.
    /// </summary>
    public float ComputeVelocity(float pulse01)
    {
        float t = Mathf.Clamp01(pulse01);
        return Mathf.Lerp(velocityAtWindowOpen, 127f, t);
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
    // Cue sprite
    // ------------------------------------------------------------------
    private void BuildCue()
    {
        var go = new GameObject("ReleaseCueScale");
        go.transform.SetParent(transform, false);
        go.transform.localPosition = Vector3.zero;
        go.transform.localScale    = Vector3.one * cueScaleMin;

        _cueRenderer = go.AddComponent<SpriteRenderer>();
        _cueRenderer.sprite           = cueSprite;
        _cueRenderer.color            = cueColorIdle;
        _cueRenderer.sortingLayerName = cueSortingLayer;
        _cueRenderer.sortingOrder     = cueSortingOrder;
        _cueRenderer.enabled          = false;

        _currentScale = cueScaleMin;
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
        if (_cueRenderer != null)
            _cueRenderer.enabled = visible;

        foreach (var sr in _dots)
            if (sr != null) sr.gameObject.SetActive(visible);
    }
}
