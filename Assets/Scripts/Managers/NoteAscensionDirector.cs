using System;
using System.Collections;
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
        // The noteMarkers dictionary key step at the time this marker entered the ascension
        // task. Uses the committed loop step, NOT tag.step (which can differ after reverse-
        // order manual releases place a note at a different placeholder than its authored step).
        public int committedStep;
        // track.GetNoteCommitTime(committedStep) captured when ascension began for this marker.
        // If the note is re-committed after this time, RemovePersistentNoteAtStep is skipped
        // so a freshly collected note at the same step isn't erased by an older burst's ascent.
        public float commitTimeAtStart;
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
        // Retry any collapses that were deferred because notes were in transit.
        if (_deferredCollapseControllers != null && _deferredCollapseControllers.Count > 0)
        {
            var deferred = new List<InstrumentTrackController>(_deferredCollapseControllers);
            _deferredCollapseControllers.Clear();
            foreach (var ctrl in deferred)
                TryCollapseIfHighestBinEmpty(ctrl);
        }

        if (_ascendTasks.Count == 0) return;

        var keys = new List<InstrumentTrack>(_ascendTasks.Keys);
        HashSet<InstrumentTrackController> affectedControllers = null;

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

                    // Remove the note from the persistent loop so it stops playing.
                    // Guard: if the note was re-committed after this ascension started
                    // (e.g. a new collection landed at the same step in the same frame),
                    // skip removal so the fresh note continues to play.
                    if (ms.tag != null && ms.tag.track != null)
                    {
                        // Use committedStep — the noteMarkers key recorded at ascension
                        // start — not tag.step, which drifts when notes are released in
                        // reverse order (placeholder visual ≠ committed loop position).
                        int removeStep = ms.committedStep >= 0 ? ms.committedStep : ms.tag.step;
                        float currentCommit = ms.tag.track.GetNoteCommitTime(removeStep);
                        bool noteRefreshed = currentCommit > ms.commitTimeAtStart;
                        if (!noteRefreshed)
                        {
                            // Defer by 1 frame so InstrumentTrack.Update() can play the note
                            // at the current bar boundary before it is removed, regardless of
                            // script execution order (DrumTrack fires OnLoopBoundary before
                            // InstrumentTrack runs, which otherwise silences step=0 permanently).
                            StartCoroutine(RemovePersistentNoteNextFrame(ms.tag.track, removeStep, ms.commitTimeAtStart));
                        }
                        var ctrl = ms.tag.track.controller;
                        if (ctrl != null)
                        {
                            affectedControllers ??= new HashSet<InstrumentTrackController>();
                            affectedControllers.Add(ctrl);
                        }
                    }

                    // Destroy the visual marker.
                    Destroy(ms.go);
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

        // After all notes are removed this boundary: collapse the loop if the
        // highest active bin is now empty on every track.
        if (affectedControllers != null)
            foreach (var ctrl in affectedControllers)
                TryCollapseIfHighestBinEmpty(ctrl);
    }

    private IEnumerator RemovePersistentNoteNextFrame(InstrumentTrack track, int step, float commitTimeAtStart)
    {
        yield return null;
        if (track == null) yield break;
        float currentCommit = track.GetNoteCommitTime(step);
        if (currentCommit <= commitTimeAtStart)
            track.RemovePersistentNoteAtStep(step);
    }

    private void TryCollapseIfHighestBinEmpty(InstrumentTrackController ctrl)
    {
        if (ctrl?.tracks == null) return;

        // Find the global highest loop multiplier across all tracks.
        int globalMaxMult = 1;
        foreach (var t in ctrl.tracks)
            if (t != null) globalMaxMult = Mathf.Max(globalMaxMult, t.loopMultiplier);

        if (globalMaxMult <= 1) return; // already at minimum, nothing to trim

        // Determine the step range of the highest bin.
        // All tracks share the same drum track, so BinSize() is uniform.
        int binSize = 0;
        foreach (var t in ctrl.tracks)
        {
            if (t != null) { binSize = t.BinSize(); break; }
        }
        if (binSize <= 0) return;

        int highBinStart = (globalMaxMult - 1) * binSize;
        int highBinEnd   = globalMaxMult * binSize;

        // If any track still has a note in the highest bin, do not collapse.
        foreach (var t in ctrl.tracks)
        {
            if (t == null) continue;
            foreach (var n in t.GetPersistentLoopNotes())
            {
                if (n.stepIndex >= highBinStart && n.stepIndex < highBinEnd)
                    return;
            }
        }

        // If any track has in-transit notes (collectables or in Vehicle queue) in the
        // highest bin, defer collapse until those notes are resolved. Without this guard,
        // ForceSyncMarkersToPersistentLoop would destroy placeholder markers that the
        // Vehicle still needs for manual note release.
        foreach (var t in ctrl.tracks)
        {
            if (t == null) continue;
            if (t.HasOutstandingNotesInRange(highBinStart, highBinEnd))
            {
                _deferredCollapseControllers ??= new HashSet<InstrumentTrackController>();
                _deferredCollapseControllers.Add(ctrl);
                Debug.Log($"[ASCENSION] Collapse deferred — track '{t.name}' has in-transit notes in bin [{highBinStart},{highBinEnd}). Will retry at next loop boundary.");
                return;
            }
        }

        // Highest bin is empty on all tracks — collapse every track that owns it.
        Debug.Log($"[ASCENSION] Highest bin {globalMaxMult - 1} empty on all tracks — collapsing loop by 1.");
        foreach (var t in ctrl.tracks)
            if (t != null && t.loopMultiplier >= globalMaxMult)
                t.RequestLoopCollapseByOne();
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
    /// <param name="committedStepResolver">
    ///     Optional: given a marker GameObject, returns the loop step that note was
    ///     actually committed at (the noteMarkers dictionary key). When null, falls
    ///     back to tag.step — which can differ after reverse-order manual releases.
    /// </param>
    public void TriggerBurstAscend(
        InstrumentTrack track,
        int burstId,
        float totalSeconds,
        Func<InstrumentTrack, int, IEnumerable<GameObject>> markerLookup,
        int ascendLoopsOverride = -1,
        Func<InstrumentTrack, GameObject, int> committedStepResolver = null)
    {
        if (track == null || markerLookup == null) return;
        if (_drum == null) return;

        float loopLen = _drum.GetLoopLengthInSeconds();
        if (loopLen <= 0f) return;

        int loops = Mathf.Max(1, ascendLoopsOverride > 0 ? ascendLoopsOverride : ascendLoops);
        float targetY = GetAscendTargetWorldY();

        // Build marker list for the new burst.
        var newMarkers = new List<MarkerState>();
        foreach (var go in markerLookup(track, burstId))
        {
            if (go == null) continue;

            float totalY = targetY - go.transform.position.y;
            float stepY = totalY / loops;

            var tag = go.GetComponent<MarkerTag>();
            if (tag != null) tag.isAscending = true;

            // committedStep is the loop step this note lives at — authoritative for
            // RemovePersistentNoteAtStep. tag.step may differ when reverse-order releases
            // place a collectable into a placeholder that isn't its authored slot.
            int committedStep = committedStepResolver != null
                ? committedStepResolver(track, go)
                : (tag != null ? tag.step : -1);

            float commitTime = (track != null && committedStep >= 0)
                ? track.GetNoteCommitTime(committedStep)
                : -1f;

            newMarkers.Add(new MarkerState
            {
                go = go,
                stepY = stepY,
                loopsRemaining = loops,
                delayLoopsRemaining = 0,
                tag = tag,
                committedStep = committedStep,
                commitTimeAtStart = commitTime,
            });
        }

        if (newMarkers.Count == 0) return;

        // If existing markers for this track are already ascending, reset their
        // loopsRemaining and stepY so all notes on the track share the same countdown.
        // This way a second burst for the same track refreshes the first burst's timer.
        if (_ascendTasks.TryGetValue(track, out var existing))
        {
            var merged = new List<MarkerState>(existing.markers.Count + newMarkers.Count);
            for (int i = 0; i < existing.markers.Count; i++)
            {
                var ms = existing.markers[i];
                if (ms.go == null) continue; // prune destroyed markers
                float remainingY = targetY - ms.go.transform.position.y;
                ms.stepY = remainingY / loops;
                ms.loopsRemaining = loops;
                // Refresh committedStep — noteMarkers key can shift between bursts.
                if (committedStepResolver != null && ms.go != null)
                {
                    int resolved = committedStepResolver(track, ms.go);
                    if (resolved >= 0) ms.committedStep = resolved;
                }
                // Refresh commit-time snapshot since the ascension countdown is being reset.
                int mStep = ms.committedStep >= 0 ? ms.committedStep
                           : (ms.tag != null ? ms.tag.step : -1);
                ms.commitTimeAtStart = (ms.tag?.track != null && mStep >= 0)
                    ? ms.tag.track.GetNoteCommitTime(mStep)
                    : -1f;
                merged.Add(ms);
            }
            merged.AddRange(newMarkers);
            _ascendTasks[track] = new AscendTask { markers = merged, delayLoopsRemaining = 0 };
        }
        else
        {
            _ascendTasks[track] = new AscendTask { markers = newMarkers, delayLoopsRemaining = 0 };
        }
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
