using System;
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
public sealed class NoteAscensionDirector : MonoBehaviour
{
    // -------------------------------------------------------------------------
    // Config (mirror of fields that lived in NoteVisualizer)
    // -------------------------------------------------------------------------

    [Header("Ascension Target")]
    [Tooltip("World-space UI reference the notes should rise to (e.g., a RectTransform named 'Line of Ascent').")]
    public RectTransform lineOfAscent;

    [Tooltip("Ascend duration in drum loops (1 = one full loop).")]
    [Min(1)] public int ascendLoops = 8;

    [Tooltip("Extra seconds added to the loop-based duration to avoid snapping on the boundary.")]
    public float ascendPaddingSeconds = 0.15f;

    [Tooltip("Small world-space Y padding to keep notes from sitting exactly on the line.")]
    public float ascendLineWorldPadding = 0f;

    [Header("First-Play Particle")]
    [Tooltip("How many particles to emit when a first-play task arrives at the line.")]
    public int firstPlayConfirmEmitCount = 6;

    // -------------------------------------------------------------------------
    // Public output — read by NoteVisualizer to drive playhead brightness
    // -------------------------------------------------------------------------

    /// <summary>
    /// Accumulated charge [0..1] from recently ascended notes.
    /// Decays over time; written here, read by NoteVisualizer.
    /// </summary>
    public float LineCharge01 { get; private set; }

    // -------------------------------------------------------------------------
    // Internal structs (mirrors of NoteVisualizer private structs)
    // -------------------------------------------------------------------------

    private struct MarkerState
    {
        public GameObject go;
        public float stepY;          // world-space Y increment per loop
        public int loopsRemaining;
        public int delayLoopsRemaining;
        // Cached tag so we can clear isAscending on arrival without GetComponent each step.
        public MarkerTag tag;
    }

    private struct AscendTask
    {
        public List<MarkerState> markers;
        public int delayLoopsRemaining;
    }

    private struct FirstPlayTask
    {
        public ParticleSystem ps;
        public Vector3 start;
        public Vector3 end;
        public Color color;
        public double startDsp;
        public double endDsp;
    }

    // -------------------------------------------------------------------------
    // State
    // -------------------------------------------------------------------------

    private readonly Dictionary<InstrumentTrack, AscendTask> _ascendTasks = new();
    private readonly List<FirstPlayTask> _firstPlayTasks = new();

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

        // Step first-play catch-up tasks
        if (_firstPlayTasks.Count == 0) return;

        double now = AudioSettings.dspTime;

        for (int i = _firstPlayTasks.Count - 1; i >= 0; i--)
        {
            var t = _firstPlayTasks[i];

            if (t.ps == null)
            {
                _firstPlayTasks.RemoveAt(i);
                continue;
            }

            double span = t.endDsp - t.startDsp;
            float raw = (span > 0.0) ? (float)((now - t.startDsp) / span) : 1f;
            float eased = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(raw));

            // This makes late spawns appear already in progress instead of racing to catch up.
            Vector3 p = Vector3.Lerp(t.start, t.end, eased);
            t.ps.transform.position = p;

            if (now >= t.endDsp)
            {
                var emitParams = new ParticleSystem.EmitParams
                {
                    position = t.end,
                    startColor = t.color
                };
                t.ps.Emit(emitParams, firstPlayConfirmEmitCount);

                t.ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                Destroy(t.ps.gameObject, 0.35f);

                _firstPlayTasks.RemoveAt(i);
            }
            else
            {
                _firstPlayTasks[i] = t;
            }
        }
    }

    // -------------------------------------------------------------------------
    // Loop boundary — step all ascend tasks
    // -------------------------------------------------------------------------

    private void OnLoopBoundary()
    {
        if (_ascendTasks.Count == 0) return;

        var keys = new List<InstrumentTrack>(_ascendTasks.Keys);

        foreach (var trk in keys)
        {
            var task = _ascendTasks[trk];

            if (task.delayLoopsRemaining > 0)
            {
                task.delayLoopsRemaining--;
                _ascendTasks[trk] = task;
                continue;
            }

            bool anyAlive = false;

            for (int i = 0; i < task.markers.Count; i++)
            {
                var ms = task.markers[i];

                if (ms.go == null) continue;

                if (ms.delayLoopsRemaining > 0)
                {
                    ms.delayLoopsRemaining--;
                    task.markers[i] = ms;
                    anyAlive = true;
                    continue;
                }

                var tf = ms.go.transform;
                var pos = tf.position;
                pos.y += ms.stepY;
                tf.position = pos;

                ms.loopsRemaining = Mathf.Max(0, ms.loopsRemaining - 1);

                if (ms.loopsRemaining <= 0)
                {
                    // Arrived at ascent line — bump line charge
                    LineCharge01 = Mathf.Min(1f, LineCharge01 + 0.25f);

                    // Snap to line world position
                    var lp = tf.position;
                    lp.y = GetAscendTargetWorldY();
                    tf.position = lp;

                    // Clear ascending flag so RecomputeTrackLayout stops preserving Y
                    if (ms.tag != null) ms.tag.isAscending = false;
                }
                else
                {
                    anyAlive = true;
                }

                task.markers[i] = ms;
            }

            if (anyAlive)
                _ascendTasks[trk] = task;
            else
                _ascendTasks.Remove(trk);
        }
    }

    // -------------------------------------------------------------------------
    // Public API — called by NoteVisualizer / InstrumentTrack
    // -------------------------------------------------------------------------

    /// <summary>
    /// Enqueue an ascension animation for all markers belonging to the given
    /// track and burst. Mirrors NoteVisualizer.TriggerBurstAscend().
    /// </summary>
    /// <param name="track">The InstrumentTrack whose markers should ascend.</param>
    /// <param name="burstId">Only markers tagged with this burstId will be animated.</param>
    /// <param name="totalSeconds">Total real-time duration for the ascension.</param>
    /// <param name="markerLookup">
    ///     Delegate that returns the live marker GameObjects for (track, burstId).
    ///     NoteVisualizer supplies this so the director never needs to know about
    ///     noteMarkers or trackRows directly.
    /// </param>
    public void TriggerBurstAscend(
        InstrumentTrack track,
        int burstId,
        float totalSeconds,
        Func<InstrumentTrack, int, IEnumerable<GameObject>> markerLookup)
    {
        if (track == null || markerLookup == null) return;
        if (_drum == null) return;

        float loopLen = _drum.GetLoopLengthInSeconds();
        if (loopLen <= 0f) return;

        var markers = new List<MarkerState>();

        foreach (var go in markerLookup(track, burstId))
        {
            if (go == null) continue;

            float startY = go.transform.position.y;
            float totalY = (GetAscendTargetWorldY()) - startY;

            float durationSec = totalSeconds + ascendPaddingSeconds;
            int loops = Mathf.Max(1, Mathf.RoundToInt(durationSec / loopLen));

            float stepY = (loops > 0) ? totalY / loops : totalY;

            var tag = go.GetComponent<MarkerTag>();
            if (tag != null) tag.isAscending = true;

            markers.Add(new MarkerState
            {
                go = go,
                stepY = stepY,
                loopsRemaining = loops,
                delayLoopsRemaining = 0,
                tag = tag
            });
        }

        if (markers.Count == 0) return;

        // Merge with any existing task for this track (restart/extend)
        _ascendTasks[track] = new AscendTask
        {
            markers = markers,
            delayLoopsRemaining = 0
        };
    }

    /// <summary>
    /// Register a "first play" catch-up particle task.
    /// The particle will travel from startWorld to endWorld over durationSeconds (DSP time).
    /// </summary>
    public void EnqueueFirstPlayTask(
        ParticleSystem ps,
        Vector3 startWorld,
        Vector3 endWorld,
        Color color,
        float durationSeconds)
    {
        if (ps == null) return;

        double now = AudioSettings.dspTime;
        _firstPlayTasks.Add(new FirstPlayTask
        {
            ps = ps,
            start = startWorld,
            end = endWorld,
            color = color,
            startDsp = now,
            endDsp = now + durationSeconds
        });
    }

    // -------------------------------------------------------------------------
    // Public queries — used by NoteVisualizer without coupling to _ascendTasks
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns true if any marker for this track is currently mid-ascension.
    /// NoteVisualizer calls this as the implementation of IsAscending(track)
    /// so RecomputeTrackLayout never needs to touch _ascendTasks directly.
    /// </summary>
    public bool IsTrackAscending(InstrumentTrack track)
    {
        return track != null && _ascendTasks.ContainsKey(track);
    }

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
