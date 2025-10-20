using System;
using System.Collections;
using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using MidiPlayerTK;

public class NoteVisualizer : MonoBehaviour
{
    [Header("Playhead")]
    public RectTransform playheadLine;
    public ParticleSystem playheadParticles;

    [Header("Marker & Tether Prefabs")]
    public GameObject notePrefab;
    public GameObject noteTetherPrefab;

    [Header("Track Rows (one per InstrumentTrack in controller order)")]
    public List<RectTransform> trackRows;

    // --- State ---
    private Canvas _worldSpaceCanvas;
    private Transform _uiParent;
    private bool isInitialized;

    // Velocity shimmer & ghost padding bookkeeping
    private readonly Dictionary<InstrumentTrack, HashSet<int>> _ghostNoteSteps = new();
    private DrumTrack _drumTrack;

    // Public so tracks can look up / place markers by step
    public Dictionary<(InstrumentTrack, int), Transform> noteMarkers = new();

    // Step -> world position (per track, no ribbons needed)
    private readonly Dictionary<InstrumentTrack, Dictionary<int, Vector3>> _trackStepWorldPositions = new();

    // Tiled clones (repeat markers to fill the leader cycle)
    private readonly Dictionary<(InstrumentTrack, int), List<Transform>> _tiledClones = new ();
// burstId -> list of collected (lit) markers for that burst
    private readonly Dictionary<int, List<GameObject>> _burstMarkers = new();

// Optional: track which bursts are animating so UpdateNoteMarkerPositions doesn't fight them
    private readonly HashSet<int> _burstAnimating = new();

    class AscendTask
    {
        public InstrumentTrack track;
        public List<MarkerState> markers = new();   // per-marker state
        public int delayLoopsRemaining;
        public int totalAscendLoops;
        public int stepsCompleted;
        public System.Action onArrive;
        public bool IsArrived => stepsCompleted >= totalAscendLoops || markers.TrueForAll(ms => ms.go == null);
    }

    class MarkerState
    {
        public GameObject go;
        public float startY;    // world-space start Y for THIS marker
        public float targetY;   // world-space anchor Y for THIS marker (same anchor, but stored per marker in case parents differ)
    }


    private readonly Dictionary<InstrumentTrack, AscendTask> _ascendTasks = new();
    private int _lastObservedCompletedLoops = -1;
    private readonly Dictionary<(InstrumentTrack track, int step), int> _stepBurst = new();
    private readonly HashSet<(InstrumentTrack track, int step)> _animatingSteps = new();

    public void RegisterCollectedMarker(InstrumentTrack track, int burstId, int step, GameObject markerGo)
    {
        if (!track || !markerGo) return;

        if (noteMarkers.TryGetValue((track, step), out var existing) && existing && existing.gameObject != markerGo)
            Destroy(existing.gameObject);

        noteMarkers[(track, step)] = markerGo.transform;
        _stepBurst[(track, step)]  = burstId;

        var tag = markerGo.GetComponent<MarkerTag>();
        if (!tag) tag = markerGo.AddComponent<MarkerTag>();
        tag.track = track;
        tag.step = step;
        tag.burstId = burstId;       // ← mark ownership by this burst
        tag.isPlaceholder = false;   // ← lit now
    }
    private void HardCullRowMarkersForSteps(InstrumentTrack track, List<int> steps)
    {
        int trackIndex = System.Array.IndexOf(GameFlowManager.Instance.controller.tracks, track);
        if (trackIndex < 0 || trackIndex >= trackRows.Count) return;

        var row = trackRows[trackIndex];
        var all = row.GetComponentsInChildren<VisualNoteMarker>(includeInactive: true);
        var stepSet = new HashSet<int>(steps);

        foreach (var vnm in all)
        {
            if (!vnm) continue;
            var tag = vnm.GetComponent<MarkerTag>();
            if (tag == null) continue;
            if (tag.track != track) continue;
            if (!stepSet.Contains(tag.step)) continue;

            // This marker belongs to one of the finished steps; nuke it unconditionally
            if (vnm.TryGetComponent<Explode>(out var explode)) explode.Permanent();
            else Destroy(vnm.gameObject);
        }
    }
    private float ComputeStepX01(InstrumentTrack track, int stepIndex)
    {
        int total = Mathf.Max(1, track.GetTotalSteps());
        int longest = Mathf.Max(1, GetActiveLongestSteps());

        // Default: your current proportional scaling (no segments)
        float baseLocalFraction = total / (float)longest;
        float t01Full = (total <= 1) ? 0f : (stepIndex / (float)(total - 1));

        // If this track just doubled (classic 2x expand), segment the row into two halves.
        int leftHalfWidth = track.LastExpandOldTotal;
        bool justDoubled = (leftHalfWidth > 0) && (total == leftHalfWidth * 2);

        if (!justDoubled)
            return Mathf.Clamp01(t01Full * baseLocalFraction);

        // SEGMENT-AWARE MAPPING
        // Segment 0: steps [0 .. leftHalfWidth-1]   -> x in [0.0 .. 0.5)
        // Segment 1: steps [leftHalfWidth .. total-1] -> x in [0.5 .. 1.0]
        bool inRightHalf = stepIndex >= leftHalfWidth;
        int stepInSegment = inRightHalf ? (stepIndex - leftHalfWidth) : stepIndex;
        int segDenom = Mathf.Max(1, leftHalfWidth - 1);
        float u = segDenom == 0 ? 0f : (stepInSegment / (float)segDenom);

        float segStart = inRightHalf ? 0.5f : 0f;
        float segWidth = 0.5f;

        return Mathf.Clamp01(segStart + u * segWidth);
    }
    public void Initialize()
    {
        isInitialized = true;
        _uiParent = transform.parent;
        _worldSpaceCanvas = _uiParent.GetComponentInParent<Canvas>();

        // Ensure rows stretch horizontally so our mapping stays sane across resolutions
        foreach (var row in trackRows)
        {
            row.anchorMin = new Vector2(0f, row.anchorMin.y);
            row.anchorMax = new Vector2(1f, row.anchorMax.y);
            row.offsetMin = new Vector2(0f, row.offsetMin.y);
            row.offsetMax = new Vector2(0f, row.offsetMax.y);
        }

        _drumTrack = GameFlowManager.Instance.activeDrumTrack;
    }
    void Update()
    {
        if (!isInitialized ||
            playheadLine == null ||
            GameFlowManager.Instance.activeDrumTrack == null ||
            GameFlowManager.Instance.controller.tracks == null)
            return;

        // --- Playhead position across the "leader" loop (max loop multiplier) ---
        float baseLoopLength = GameFlowManager.Instance.activeDrumTrack.GetLoopLengthInSeconds();
        int   globalLoopMultiplier = GameFlowManager.Instance.controller.GetMaxActiveLoopMultiplier();
        float fullVisualLoopDuration = Mathf.Max(0.0001f, baseLoopLength * Mathf.Max(1, globalLoopMultiplier));

        float globalElapsed = (float)(AudioSettings.dspTime - GameFlowManager.Instance.activeDrumTrack.startDspTime);
        float globalNormalized = (globalElapsed % fullVisualLoopDuration) / fullVisualLoopDuration;

        float canvasWidth = GetScreenWidth();
        float xPos = Mathf.Lerp(0f, canvasWidth, Mathf.Clamp01(globalNormalized));
        playheadLine.anchoredPosition = new Vector2(xPos, playheadLine.anchoredPosition.y);

        // --- Drum timing / velocity shimmer ---
        int   drumTotalSteps = GameFlowManager.Instance.activeDrumTrack.totalSteps;
        float drumLoopLength = GameFlowManager.Instance.activeDrumTrack.GetLoopLengthInSeconds();
        float stepDuration   = Mathf.Max(0.0001f, drumLoopLength / Mathf.Max(1, drumTotalSteps));
        float drumElapsed    = (float)((AudioSettings.dspTime - GameFlowManager.Instance.activeDrumTrack.startDspTime) % drumLoopLength);

        int currentStep = Mathf.FloorToInt(drumElapsed / stepDuration) % Mathf.Max(1, drumTotalSteps);

        bool shimmer = false; float maxVelocity = 0f;
        foreach (var track in GameFlowManager.Instance.controller.tracks)
        {
            float v = track.GetVelocityAtStep(currentStep);
            maxVelocity = Mathf.Max(maxVelocity, v / 127f);
            if (_ghostNoteSteps.TryGetValue(track, out var steps) && steps.Contains(currentStep))
            { shimmer = true; break; }
        }

        if (playheadParticles != null)
        {
            var main = playheadParticles.main;
            main.startSize = Mathf.Lerp(0.3f, 1.2f, maxVelocity);
            var emission = playheadParticles.emission;
            emission.rateOverTime = Mathf.Lerp(10f, 50f, maxVelocity);
            emission.enabled = shimmer;

            var col = playheadParticles.colorOverLifetime;
            if (col.enabled)
            {
                Gradient g = new Gradient();
                g.SetKeys(
                    new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.cyan, 1f) },
                    new[] { new GradientAlphaKey(0.4f + maxVelocity * 0.5f, 0f), new GradientAlphaKey(0.1f, 1f) }
                );
                col.color = g;
            }
        }

        // --- Build step → world position maps per row (no lines; direct math) ---
        var controller = GameFlowManager.Instance.controller;
        int longestSteps = GetActiveLongestSteps();

        for (int i = 0; i < controller.tracks.Length && i < trackRows.Count; i++)
        {
            InstrumentTrack track = controller.tracks[i];
            RectTransform row = trackRows[i];
            Rect rowRect = row.rect;

            int trackSteps = Mathf.Max(1, track.GetTotalSteps());

            // Center Y for markers on this row
            float yLocal = (rowRect.yMin + rowRect.yMax) * 0.5f;

            // Fraction of the leader’s width this track occupies (its audible window)
            float localFraction = trackSteps / (float)Mathf.Max(1, longestSteps);

            // Precompute the map of *every* local step for this track
            var stepMap = new Dictionary<int, Vector3>(trackSteps);
            for (int step = 0; step < trackSteps; step++)
            {
                float x01    = ComputeStepX01(track, step); 
                float xLocal = Mathf.Lerp(rowRect.xMin, rowRect.xMax, x01);
                Vector3 localPos = new Vector3(xLocal, yLocal, 0f);
                stepMap[step] = row.TransformPoint(localPos);
            }

            _trackStepWorldPositions[track] = stepMap;
        }

        // Move any live markers to their updated step positions
        UpdateNoteMarkerPositions();

        var drum = GameFlowManager.Instance?.activeDrumTrack;
        if (drum == null) return;

        int loopsNow = drum.completedLoops;
        if (loopsNow != _lastObservedCompletedLoops)
        {
            _lastObservedCompletedLoops = loopsNow;
            OnLoopBoundary();
        }        
    }
    private void OnLoopBoundary()
    {
        if (_ascendTasks.Count == 0) return;

        var finished = new List<InstrumentTrack>();

        foreach (var kv in _ascendTasks)
        {
            var task = kv.Value;

            // prune destroyed markers
            task.markers = task.markers.Where(ms => ms.go != null).ToList();
            if (task.markers.Count == 0) { finished.Add(kv.Key); continue; }

            if (task.delayLoopsRemaining > 0) { task.delayLoopsRemaining--; continue; }

            task.stepsCompleted = Mathf.Min(task.stepsCompleted + 1, task.totalAscendLoops);
            float u = task.totalAscendLoops <= 0 ? 1f : (task.stepsCompleted / (float)task.totalAscendLoops);

            foreach (var ms in task.markers)
            {
                var t = ms.go.transform;
                var p = t.position; // world
                float y = Mathf.Lerp(ms.startY, ms.targetY, u);
                t.position = new Vector3(p.x, y, p.z);
            }

            if (task.IsArrived)
            {
                foreach (var ms in task.markers) if (ms.go) Destroy(ms.go);
                DestroyOrphanRowMarkers(task.track);
                task.onArrive?.Invoke();
                finished.Add(kv.Key);
            }
        }

        foreach (var t in finished) _ascendTasks.Remove(t);
    }
    public void MarkGhostPadding(InstrumentTrack track, int startStepInclusive, int count)
    {
        if (!_ghostNoteSteps.TryGetValue(track, out var set))
            _ghostNoteSteps[track] = set = new HashSet<int>();

        int total = Mathf.Max(1, GameFlowManager.Instance.controller.tracks.Max(t => t.GetTotalSteps()));
        for (int i = 0; i < count; i++)
            set.Add((startStepInclusive + i) % total);
    }
    public float GetTopWorldY()
    {
        RectTransform rt = GetComponent<RectTransform>();
        Vector3[] worldCorners = new Vector3[4];
        rt.GetWorldCorners(worldCorners);
        return worldCorners[1].y;
    }
    public Transform GetUIParent() => _uiParent;
    int GetActiveLongestSteps()
    {
        var ctrl = GameFlowManager.Instance?.controller;
        if (ctrl == null || ctrl.tracks == null) return 1;

        int longest = 1;
        foreach (var t in ctrl.tracks)
            if (t != null) longest = Mathf.Max(longest, t.GetTotalSteps());

        return longest;
    }

    public void CanonicalizeTrackMarkers(InstrumentTrack track, int currentBurstId)
{
    if (track == null) return;

    // Build a set of canonical (track,step) we know about
    var canonical = new HashSet<(InstrumentTrack,int)>(noteMarkers.Keys.Where(k => k.Item1 == track));

    // Scan all MarkerTag in this row and destroy non-canonical duplicates
    int trackIndex = Array.IndexOf(GameFlowManager.Instance.controller.tracks, track);
    if (trackIndex < 0 || trackIndex >= trackRows.Count) return;
    var row = trackRows[trackIndex];

    var tags = row.GetComponentsInChildren<MarkerTag>(includeInactive:true);
    foreach (var tag in tags)
    {
        if (tag == null) continue;
        if (tag.track != track) continue;

        var key = (tag.track, tag.step);
        if (!canonical.Contains(key))
        {
            // Rogue object: not tracked in noteMarkers, safe to delete
            Destroy(tag.gameObject);
        }
    }

    // Normalize burst ids:
    // If the step is in the loop → neutral (-1).
    // If it's a placeholder for the current burst → currentBurstId and isPlaceholder==true.
    // If it's a collected note for the current burst → currentBurstId and isPlaceholder==false.
    var loopSteps = new HashSet<int>(track.GetPersistentLoopNotes().Select(n => n.Item1));

    foreach (var kv in noteMarkers)
    {
        if (kv.Key.Item1 != track) continue;
        var tf = kv.Value;
        if (!tf) continue;

        var t = tf.GetComponent<MarkerTag>() ?? tf.gameObject.AddComponent<MarkerTag>();

        if (loopSteps.Contains(kv.Key.Item2))
        {
            // This is an actual looped note; it should NOT carry a specific burst id.
            t.burstId = -1;
            t.isPlaceholder = false;
        }
        else
        {
            // Not in loop; must be a placeholder if it exists at all
            t.isPlaceholder = true;
            if (t.burstId != currentBurstId) t.burstId = currentBurstId; // placeholder for upcoming/current burst
        }
    }

    // Force a position recompute for all markers on this track
    RecomputeTrackLayout(track);
}

public GameObject PlacePersistentNoteMarker(InstrumentTrack track, int stepIndex, bool lit = true, int burstId = -1)
{
    Debug.Log($"Placing Persistent Note marker for {track} at {stepIndex}, lit {lit} burst id {burstId}");
    
    var key = (track, stepIndex);

    // If an existing marker is present, maybe convert it:
    if (noteMarkers.TryGetValue(key, out var existing) && existing && existing.gameObject.activeInHierarchy)
    {

        if (lit)
        {
            // Light ONLY if actually in the loop
            bool inLoop = track.GetPersistentLoopNotes().Any(n => n.Item1 == stepIndex);
            if (inLoop)
            {
                var vnm = existing.GetComponent<VisualNoteMarker>();
                if (vnm != null) vnm.Initialize(track.trackColor);

                var light = existing.GetComponent<MarkerLight>() ?? existing.gameObject.AddComponent<MarkerLight>();
                light.LightUp(track.trackColor);

                var existingTag = existing.GetComponent<MarkerTag>() ?? existing.gameObject.AddComponent<MarkerTag>();
                existingTag.isPlaceholder = false;
                // keep any prior non-neutral id; otherwise set to neutral
                if (existingTag.burstId == 0 || existingTag.burstId == -1)
                    existingTag.burstId = burstId; // -1 for loop render, or currentBurstId on collection
            }
        }
        return existing.gameObject;
    }

    // … (create new marker as you already do)
    int trackIndex = Array.IndexOf(GameFlowManager.Instance.controller.tracks, track);
    if (trackIndex < 0 || trackIndex >= trackRows.Count) return null;

    RectTransform row = trackRows[trackIndex];
    Rect rowRect = row.rect;

    int totalSteps = Mathf.Max(1, track.GetTotalSteps());
    float x01 = ComputeStepX01(track, stepIndex);
    float xLocal = Mathf.Lerp(rowRect.xMin, rowRect.xMax, x01);
    
    float bottomWorldY = GetBottomWorldY();
    float bottomLocalY = row.InverseTransformPoint(new Vector3(0f, bottomWorldY, 0f)).y;

    GameObject marker = Instantiate(notePrefab, Vector3.zero, Quaternion.identity, row);
    marker.transform.localPosition = new Vector3(xLocal, bottomLocalY, 0f);

    var tag = marker.GetComponent<MarkerTag>() ?? marker.AddComponent<MarkerTag>();
    tag.track = track;
    tag.step = stepIndex;
    tag.burstId = burstId;              // ⬅️ IMPORTANT: don’t hardcode 0
    tag.isPlaceholder = !lit;

    noteMarkers[key] = marker.transform;

    if (lit)
    {
        // Only lit-at-creation when caller KNOWS it’s in loop (e.g., bootstrapping from loop data)
        var vnm = marker.GetComponent<VisualNoteMarker>();
        if (vnm != null) vnm.Initialize(track.trackColor);
        var light = marker.GetComponent<MarkerLight>() ?? marker.AddComponent<MarkerLight>();
        light.LightUp(track.trackColor);
    }
    else
    {
        var ml = marker.GetComponent<MarkerLight>() ?? marker.AddComponent<MarkerLight>();
        ml.SetGrey(new Color(1f,1f,1f,0.05f));
    }
    return marker;
}

    private static bool IsGreyPlaceholder(GameObject go)
    {
        if (!go) return false;
        var ml = go.GetComponent<MarkerLight>();
        if (ml == null) return false;

        // Heuristic: your greys use very low alpha (e.g., 0.05f). Adjust if you store an explicit flag.
        var r = go.GetComponent<UnityEngine.UI.Image>();
        if (r != null) return r.color.a <= 0.1f;

        // Fallback: treat markers without a LightUp call as grey (extend MarkerLight to expose a flag if you can)
        return ml.enabled && ml.gameObject.activeInHierarchy && ml.name.Contains("grey", StringComparison.OrdinalIgnoreCase);
    }
    private IEnumerator AscendAndDestroy_WithCount(GameObject marker, float targetWorldY, float seconds, System.Action onOneDone)
    {
        if (!marker) { onOneDone?.Invoke(); yield break; }

        Vector3 start = marker.transform.position;
        Vector3 end   = new Vector3(start.x, targetWorldY, start.z);
        float t = 0f;

        while (t < seconds && marker)
        {
            t += Time.deltaTime;
            float u = Mathf.SmoothStep(0f, 1f, t / Mathf.Max(0.0001f, seconds));
            marker.transform.position   = Vector3.Lerp(start, end, u);
            marker.transform.localScale = Vector3.Lerp(Vector3.one, Vector3.one * 0.9f, u);
            yield return null;
        }

        if (marker) Destroy(marker);
        onOneDone?.Invoke();
    }
    private float GetBottomWorldY()
    {
        RectTransform rt = GetComponent<RectTransform>();
        Vector3[] worldCorners = new Vector3[4];
        rt.GetWorldCorners(worldCorners);
        return worldCorners[0].y; // bottom-left corner
    }
    private void UpdateNoteMarkerPositions()
    {
        var deadKeys = new List<(InstrumentTrack, int)>();
        foreach (var kvp in noteMarkers)
        {
            var track  = kvp.Key.Item1;
            var step   = kvp.Key.Item2;
            var marker = kvp.Value;
            if (_animatingSteps.Contains((track, step))) 
                continue;
            if (marker == null) { deadKeys.Add(kvp.Key); continue; }

            if (!_trackStepWorldPositions.TryGetValue(track, out var map)) continue;

            int trackIndex = Array.IndexOf(GameFlowManager.Instance.controller.tracks, track);
            if (trackIndex < 0 || trackIndex >= trackRows.Count) continue;

            RectTransform row = trackRows[trackIndex];

            if (map.TryGetValue(step, out var worldPos)) { 
                // Move all markers unless this step is actively animating (already guarded above)
                marker.localPosition = row.InverseTransformPoint(worldPos);
            }
        }
        foreach (var k in deadKeys) noteMarkers.Remove(k);
    }
    public void TriggerBurstAscend(InstrumentTrack track, int burstId, float seconds)
    {
        if (!track) return;

        // Gather steps that belong to this burst
        var steps = new List<int>();
        foreach (var kv in _stepBurst)
        {
            if (kv.Key.track == track && kv.Value == burstId)
                steps.Add(kv.Key.step);
        }
        if (steps.Count == 0) return;

        // Collect GOs and detach from snapping
        var toAnimate = new List<GameObject>();
        foreach (var step in steps)
        {
            var key = (track, step);

            if (noteMarkers.TryGetValue(key, out var tr) && tr != null)
            {
                toAnimate.Add(tr.gameObject);
                noteMarkers.Remove(key);      // stop UpdateNoteMarkerPositions from moving it
            }
            _animatingSteps.Add(key);
            _stepBurst.Remove(key);           // this step is now owned by the ascent animation
        }

        // Animate and clean up
        int pending = 0;
        float targetY = GetTopWorldY();
        foreach (var go in toAnimate)
        {
            if (!go) continue;
            pending++;
            StartCoroutine(AscendAndDestroy(go, targetY, seconds, () =>
            {
                pending--;
                if (pending == 0)
                {
                    // Clear animating flags for all steps of this burst
                    foreach (var s in steps) _animatingSteps.Remove((track, s));

                    DestroyOrphanRowMarkers(track);
                    CullGhostBottomMarkers(track);           
                    HardCullRowMarkersForSteps(track, steps);
                    track.RemoveNotesForBurst(burstId); // stop audio for this burst
                }
            }));
        }

        if (pending == 0)
        {
            foreach (var s in steps) _animatingSteps.Remove((track, s));
            DestroyOrphanRowMarkers(track);
            CullGhostBottomMarkers(track);
            HardCullRowMarkersForSteps(track, steps);
            track.RemoveNotesForBurst(burstId);
        }
    }
    private void CullGhostBottomMarkers(InstrumentTrack track)
    {
        var removeKeys = new List<(InstrumentTrack,int)>();
        foreach (var kv in noteMarkers)
        {
            if (kv.Key.Item1 != track) continue;

            bool ownedByBurst = _stepBurst.ContainsKey((kv.Key.Item1, kv.Key.Item2));
            bool animating    = _animatingSteps.Contains((kv.Key.Item1, kv.Key.Item2));            
            if (!ownedByBurst && !animating)
            {
                // orphan placeholder — safe to destroy
                if (kv.Value) Destroy(kv.Value.gameObject);
                removeKeys.Add(kv.Key);
            }
        }
        foreach (var k in removeKeys) noteMarkers.Remove(k);
    }
    private IEnumerator AscendAndDestroy(GameObject marker, float targetWorldY, float seconds, System.Action onDone)
{
    if (!marker) { onDone?.Invoke(); yield break; }

    Vector3 start = marker.transform.position;
    Vector3 end   = new Vector3(start.x, targetWorldY, start.z);
    float t = 0f;

    while (t < seconds && marker)
    {
        t += Time.deltaTime;
        float u = Mathf.SmoothStep(0f, 1f, t / Mathf.Max(0.0001f, seconds));
        marker.transform.position   = Vector3.Lerp(start, end, u);
        marker.transform.localScale = Vector3.Lerp(Vector3.one, Vector3.one * 0.9f, u);
        yield return null;
    }

    if (marker) Destroy(marker);
    onDone?.Invoke();
}
    public void TriggerNoteRushToVehicle(InstrumentTrack track, Vehicle v)
    {
        foreach (var marker in GetNoteMarkers(track))
            StartCoroutine(RushToVehicle(marker, marker.transform.position, v));
    }
    public void TriggerNoteBlastOff(InstrumentTrack track)
    {
        var gos = GetNoteMarkers(track);
        var keys = new List<(InstrumentTrack,int)>();
        foreach (var kv in noteMarkers)
            if (kv.Key.Item1 == track) keys.Add(kv.Key);
        foreach (var k in keys) noteMarkers.Remove(k);

        foreach (var go in gos)
            if (go) StartCoroutine(BlastOffAndDestroy(go));

        DestroyOrphanRowMarkers(track);
    }
    private List<GameObject> GetNoteMarkers(InstrumentTrack track)
    {
        var result = new List<GameObject>();
        foreach (var kvp in noteMarkers)
        {
            if (kvp.Key.Item1 != track) continue;
            var transform = kvp.Value;
            if (transform == null) continue;
            var go = transform.gameObject;
            if (go != null) result.Add(go);
        }
        return result;
    }
    private void DestroyOrphanRowMarkers(InstrumentTrack track)
    {
        int trackIndex = Array.IndexOf(GameFlowManager.Instance.controller.tracks, track);
        if (trackIndex < 0 || trackIndex >= trackRows.Count) return;

        var row = trackRows[trackIndex];
        var orphans = row.GetComponentsInChildren<VisualNoteMarker>(includeInactive: true);
        foreach (var vnm in orphans)
        {
            bool referenced = false;
            foreach (var kvp in noteMarkers)
            {
                if (kvp.Key.Item1 != track) continue;
                if (kvp.Value == vnm.transform) { referenced = true; break; }
            }
            if (!referenced)
            {
                if (vnm.TryGetComponent<Explode>(out var explode)) explode.Permanent();
                else Destroy(vnm.gameObject);
            }
        }
    }
    private IEnumerator BlastOffAndDestroy(GameObject marker)
    {
        if (marker == null) yield break;
        Vector3 start = marker.transform.position;
        Vector3 end   = start + UnityEngine.Random.insideUnitSphere * 2f;
        float duration = 0.4f, t = 0f;

        while (t < duration)
        {
            t += Time.deltaTime;
            float u = Mathf.SmoothStep(0f, 1f, t / duration);
            if (marker != null)
            {
                marker.transform.position = Vector3.Lerp(start, end, u);
                marker.transform.localScale = Vector3.Lerp(Vector3.one, Vector3.zero, u);
            }
            yield return null;
        }

        if (marker == null) yield break;
        if (marker.TryGetComponent<Explode>(out var explode)) explode.Permanent();
        else Destroy(marker);
    }
    private IEnumerator RushToVehicle(GameObject marker, Vector3 position, Vehicle target)
    {
        float duration = 0.6f, t = 0f;

        while (t < duration)
        {
            t += Time.deltaTime;
            float u = Mathf.SmoothStep(0f, 1f, t / duration);
            position = Vector3.Lerp(position, target.transform.position, u);
            yield return null;
        }

        if (marker != null)
        {
            if (marker.TryGetComponent<Explode>(out var explode)) explode.Permanent();
            else Destroy(marker);
        }
    }
    private float GetScreenWidth()
    {
        RectTransform rt = _worldSpaceCanvas.GetComponent<RectTransform>();
        return rt.rect.width;
    }

    public void RecomputeTrackLayout(InstrumentTrack track)
    {
        if (track == null) return;

        // find this track's row
        int trackIndex = Array.IndexOf(GameFlowManager.Instance.controller.tracks, track);
        if (trackIndex < 0 || trackIndex >= trackRows.Count) return;

        RectTransform row = trackRows[trackIndex];
        Rect rowRect      = row.rect;

        int totalSteps    = Mathf.Max(1, track.GetTotalSteps());
        int longestSteps  = Mathf.Max(1, GetActiveLongestSteps());
        float localFraction = totalSteps / (float)longestSteps;

        // compute the Y once using the same scheme you already use
        float bottomWorldY = GetBottomWorldY();
        float bottomLocalY = row.InverseTransformPoint(new Vector3(0f, bottomWorldY, 0f)).y; 
        
        // walk all markers and reposition only those for this track
        // (key is (InstrumentTrack, stepIndex) → Transform)
        var kvs = noteMarkers.ToArray(); // avoid modifying during enumeration
        foreach (var kv in kvs)
        {
            var key = kv.Key;
            var tf  = kv.Value;
            if (key.Item1 != track || !tf) continue;

            int stepIndex = key.Item2;
            float x01 = ComputeStepX01(track, stepIndex);
            float xLocal = Mathf.Lerp(rowRect.xMin, rowRect.xMax, x01);
            var lp = tf.localPosition;
            float yLocal = IsAscending(tf) ? lp.y : bottomLocalY; 
            tf.localPosition = new Vector3(xLocal, yLocal, lp.z);
        }
    }

    bool IsAscending(Transform tf) {
        if (tf == null) return false;
        foreach (var kv in _ascendTasks) // _ascendTasks: track -> task
        {
            var task = kv.Value;
            if (task == null || task.markers == null) continue;
            for (int i = 0; i < task.markers.Count; i++)
            {
                var ms = task.markers[i];
                if (ms != null && ms.go != null && ms.go.transform == tf)
                    return true;
            }
        }
        return false;
    }
}
