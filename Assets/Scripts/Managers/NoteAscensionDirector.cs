using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Owns all time-sequenced note ascension logic extracted from NoteVisualizer.
///
/// Responsibilities:
///   - Stepping per-track ascend tasks on every drum loop boundary.
///   - Driving "first play" catch-up particle tasks (late-spawn visual alignment).
///   - Exposing LineCharge01 so NoteVisualizer can read the accumulated charge
///     and drive playhead brightness without coupling back here.
///
/// Non-responsibilities:
///   - Marker placement, layout, or track rows (NoteVisualizer owns those).
///   - Playhead rendering or release pulse (NoteVisualizer owns those).
///   - DrumTrack subscription lifecycle beyond what Initialize/Teardown provides.
///
/// Setup:
///   Place on the same GameObject as NoteVisualizer (or any persistent manager GO).
///   NoteVisualizer holds a [SerializeField] reference to this component and
///   calls Initialize(drum) once it has a valid DrumTrack, and Teardown() on
///   scene unload / motif clear.
/// </summary>
public sealed partial class NoteAscensionDirector : MonoBehaviour
{
    // -------------------------------------------------------------------------
    // Config (mirror of fields that lived in NoteVisualizer)
    // -------------------------------------------------------------------------

    [Header("Ascension Target")]
    [Tooltip("World-space UI reference the notes should rise to (e.g., a RectTransform named 'Line of Ascent').")]
    public RectTransform lineOfAscent;

    [Tooltip("Small world-space Y padding to keep notes from sitting exactly on the line.")]
    public float ascendLineWorldPadding = 0f;

    // -------------------------------------------------------------------------
    // Public output — read by NoteVisualizer to drive playhead brightness
    // -------------------------------------------------------------------------

    /// <summary>
    /// Accumulated charge [0..1] from recently ascended notes.
    /// Decays over time; written here, read by NoteVisualizer.
    /// </summary>
    public float LineCharge01 { get; private set; }

    /// <summary>
    /// Fired at the loop boundary when a marker reaches the ascent line (loopsRemaining == 0).
    /// Payload: (InstrumentTrack track, int step, Vector3 worldPosition).
    /// MotifRingGlyphApplicator subscribes per-ring to trigger note-contour dots.
    /// </summary>
    public static event System.Action<InstrumentTrack, int, Vector3> OnNoteReachedAscent;

    // -------------------------------------------------------------------------
    // State
    // -------------------------------------------------------------------------

    private HashSet<InstrumentTrackController> _deferredCollapseControllers;

    private DrumTrack _drum;
    private bool _subscribed;

    // -------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------

    /// <summary>
    /// Call once NoteVisualizer has resolved a valid DrumTrack.
    /// Safe to call multiple times — will re-subscribe only if drum changed.
    /// </summary>
    public void Initialize(DrumTrack drum)
    {
        if (drum == _drum && _subscribed) return;

        Teardown();
        _drum = drum;

        if (_drum != null)
        {
            _drum.OnLoopBoundary += OnLoopBoundary;
            _subscribed = true;
        }
    }

    /// <summary>
    /// Unsubscribes from the current drum and clears all in-flight tasks.
    /// Call on motif clear, scene unload, or when NoteVisualizer tears down.
    /// </summary>
    public void Teardown()
    {
        if (_drum != null && _subscribed)
        {
            _drum.OnLoopBoundary -= OnLoopBoundary;
        }

        _drum = null;
        _subscribed = false;

        _ascendTasks.Clear();
        _firstPlayTasks.Clear();
        _deferredCollapseControllers?.Clear();
        LineCharge01 = 0f;
    }

    /// <summary>
    /// Clear all ascend tasks without tearing down the drum subscription.
    /// Used by NoteVisualizer.BeginNewMotif_ClearAll().
    /// </summary>
    public void ClearAllTasks()
    {
        _ascendTasks.Clear();
        _firstPlayTasks.Clear();
        _deferredCollapseControllers?.Clear();
        LineCharge01 = 0f;
    }

    private void OnDestroy()
    {
        Teardown();
    }

    // -------------------------------------------------------------------------
    // Update — decay LineCharge01 and step FirstPlay tasks
    // -------------------------------------------------------------------------

    [SerializeField] private float lineChargeDecaySpeed = 0.5f;

    private void Update()
    {
        // Decay line charge
        LineCharge01 = Mathf.MoveTowards(LineCharge01, 0f, lineChargeDecaySpeed * Time.deltaTime);

        TickFirstPlayTasks();
    }

    // -------------------------------------------------------------------------
    // Public queries — used by NoteVisualizer without coupling to _ascendTasks
    // -------------------------------------------------------------------------

    /// <summary>
    /// The world-space Y target that ascending markers travel toward.
    /// Used by NoteVisualizer.GetAscendTargetWorldY() and by TriggerBurstAscend
    /// internally to compute stepY — single source of truth.
    /// </summary>
    public float GetAscendTargetWorldY()
    {
        return GetLineWorldY() + ascendLineWorldPadding;
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private float GetLineWorldY()
    {
        if (lineOfAscent == null) return 0f;

        // RectTransform world corners: [0]=BL [1]=TL [2]=TR [3]=BR
        var corners = new Vector3[4];
        lineOfAscent.GetWorldCorners(corners);
        return (corners[0].y + corners[1].y) * 0.5f;
    }
}
