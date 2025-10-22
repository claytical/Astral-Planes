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

    private Canvas _worldSpaceCanvas;
    private Transform _uiParent;
    private bool isInitialized;
    private readonly Dictionary<InstrumentTrack, HashSet<int>> _ghostNoteSteps = new();
    public Dictionary<(InstrumentTrack, int), Transform> noteMarkers = new();
    private readonly Dictionary<InstrumentTrack, Dictionary<int, Vector3>> _trackStepWorldPositions = new();
    private readonly Dictionary<(InstrumentTrack, int), List<Transform>> _tiledClones = new ();
    private readonly Dictionary<int, List<GameObject>> _burstMarkers = new();

    private readonly HashSet<int> _burstAnimating = new();
    private readonly HashSet<(InstrumentTrack, int)> _ascendingMarkers = new();
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
        Debug.Log($"[RegisterCollected] {track.name} burstId={burstId} step={step}, markerGo y={markerGo.transform.position.y:F1}");
        if (noteMarkers.TryGetValue((track, step), out var existing) && existing && existing.gameObject != markerGo)
        {
            Debug.Log($"  → DESTROYING old marker at step {step}, was at y={existing.position.y:F1}");
            Destroy(existing.gameObject);
        }

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
    // Uniform mapping across the FULL current loop width.
    // Example with width 1920:
    //  - 16 steps: x = (step / 16) * 1920  → step 0 = 0, step 4 = 480
    //  - 32 steps: x = (step / 32) * 1920  → step 0 = 0, step 4 = 240
    int total = Mathf.Max(1, track.GetTotalSteps());
    float x01 = stepIndex / (float)total;     // NOTE: divide by total, not (total-1)
    return Mathf.Clamp01(x01);
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

    // Resolve row
    int trackIndex = Array.IndexOf(GameFlowManager.Instance.controller.tracks, track);
    if (trackIndex < 0 || trackIndex >= trackRows.Count) return;
    var row = trackRows[trackIndex];

    // CURRENT loop steps (authoritative)
    var loopSteps = new HashSet<int>(track.GetPersistentLoopNotes().Select(n => n.Item1));

    // Remove any stale entries for this track first
    var toRemove = new List<(InstrumentTrack,int)>();
    foreach (var kv in noteMarkers)
    {
        if (kv.Key.Item1 == track && (kv.Value == null || kv.Value.gameObject == null))
            toRemove.Add(kv.Key);
    }
    foreach (var k in toRemove) noteMarkers.Remove(k);

    // Pass 1: normalize tags
    var tags = row.GetComponentsInChildren<MarkerTag>(includeInactive: true);
    foreach (var tag in tags)
    {
        if (!tag || tag.track != track) continue;

        bool isLoop = loopSteps.Contains(tag.step); // loop is the source of truth

        if (isLoop)
        {
            tag.burstId = -1;       // neutral for persistent loop
            tag.isPlaceholder = false;

            var key = (track, tag.step);
            noteMarkers[key] = tag.transform;
            continue;
        }

        // Not in loop: placeholders only
        if (tag.isPlaceholder)
        {
            if (tag.burstId != currentBurstId)
            {
#if UNITY_EDITOR
                if (!Application.isPlaying) UnityEngine.Object.DestroyImmediate(tag.gameObject);
                else UnityEngine.Object.Destroy(tag.gameObject);
#else
                UnityEngine.Object.Destroy(tag.gameObject);
#endif
                continue;
            }

            // current burst placeholder → keep
            tag.burstId = currentBurstId;
            var key = (track, tag.step);
            noteMarkers[key] = tag.transform;
        }
        else
        {
            // Unexpected non-placeholder not in loop — don’t destroy; neutralize to loop for safety
            tag.burstId = -1;
            tag.isPlaceholder = false;
            var key = (track, tag.step);
            noteMarkers[key] = tag.transform;
        }
    }

    // Pass 2: prune dictionary entries that no longer have a child object
    toRemove.Clear();
    foreach (var kv in noteMarkers)
    {
        if (kv.Key.Item1 != track) continue;
        if (kv.Value == null || kv.Value.gameObject == null) toRemove.Add(kv.Key);
    }
    foreach (var k in toRemove) noteMarkers.Remove(k);

    // Recompute X without stomping Y
    RecomputeTrackLayout(track);

    // NEW: actually remove orphans now that the dictionary is authoritative
    DestroyOrphanRowMarkers(track, dryRun: false);
}
    public GameObject PlacePersistentNoteMarker(InstrumentTrack track, int stepIndex, bool lit = true, int burstId = -1)
{
    Debug.Log($"[NoteViz] Placing Persistent Note marker for {track} at {stepIndex}, lit {lit} burst id {burstId}");
    var key = (track, stepIndex);

    // Resolve row (we'll need it for adoption or creation)
    int trackIndex = Array.IndexOf(GameFlowManager.Instance.controller.tracks, track);
    if (trackIndex < 0 || trackIndex >= trackRows.Count) return null;
    RectTransform row = trackRows[trackIndex];
    Rect rowRect = row.rect;

    // REUSE if we already have one in the dictionary
    if (noteMarkers.TryGetValue(key, out var existing) && existing && existing.gameObject.activeInHierarchy)
    {
        Debug.Log($"[NoteViz] REUSE marker track={track.name} step={stepIndex} lit={lit} burst={burstId} go={existing.gameObject.GetInstanceID()}");

        // Do NOT create a new one while animating—just keep the existing and (optionally) defer lighting
        if (_animatingSteps.Contains(key))
        {
            Debug.Log($"[NoteViz] [Reuse-WhileAnimating] step {stepIndex} is animating → keep existing, no new spawn");
            return existing.gameObject;
        }
//Need to refactor for terminology: a note that's been collected will alwayas be lit. one that hasn't won't be.
//there might not be a need for a placeholder
        Debug.Log($"[NoteViz] [Reuse]  → Reusing existing marker at step {stepIndex}");
        if (lit)
        {
            // Light ONLY if actually in the loop
            bool inLoop = track.GetPersistentLoopNotes().Any(n => n.Item1 == stepIndex);
            if (inLoop)
            {
                var existingTag = existing.GetComponent<MarkerTag>() ?? existing.gameObject.AddComponent<MarkerTag>();
                existingTag.isPlaceholder = false;

                // Don't overwrite a real burst id with -1
                if (existingTag.burstId <= 0 && burstId >= 0)
                    existingTag.burstId = burstId;
            }
        }
        return existing.gameObject;
    }

    // ADOPT any existing scene marker at (track,step) to avoid dupes
    var adopt = TryAdoptExistingAt(track, stepIndex, row);
    if (adopt)
    {
        Debug.Log($"[NoteViz] Found note to adopt. This shouldn't happen.");
        noteMarkers[key] = adopt;

        var tag = adopt.GetComponent<MarkerTag>() ?? adopt.gameObject.AddComponent<MarkerTag>();
        tag.track = track;
        tag.step = stepIndex;

        if (lit)
        {
            tag.isPlaceholder = false;
            if (tag.burstId <= 0 && burstId >= 0) tag.burstId = burstId;
        }
        else
        {
            Debug.Log($"[NoteViz] This is actually called");
            tag.isPlaceholder = true;
            if (burstId >= 0) tag.burstId = burstId;
            var ml = adopt.GetComponent<MarkerLight>() ?? adopt.gameObject.AddComponent<MarkerLight>();
            ml.SetGrey(track.trackColor);
        }

        Debug.Log($"[NoteViz] ADOPT marker track={track.name} step={stepIndex} lit={lit} burst={burstId} go={adopt.gameObject.GetInstanceID()}");
        return adopt.gameObject;
    }

    // Positioning for creation
    int totalSteps = Mathf.Max(1, track.GetTotalSteps());
    float x01 = ComputeStepX01(track, stepIndex);
    float xLocal = Mathf.Lerp(rowRect.xMin, rowRect.xMax, x01);

    float bottomWorldY = GetBottomWorldY();
    float bottomLocalY = row.InverseTransformPoint(new Vector3(0f, bottomWorldY, 0f)).y;

    // CREATE (final fallback; idempotent guard just in case)
    if (noteMarkers.TryGetValue(key, out var appeared) && appeared) return appeared.gameObject;

    GameObject marker = Instantiate(notePrefab, Vector3.zero, Quaternion.identity, row);
    marker.transform.localPosition = new Vector3(xLocal, bottomLocalY, 0f);
    Debug.Log($"[NoteViz] CREATE marker track={track.name} step={stepIndex} lit={lit} burst={burstId} go={marker.GetInstanceID()}");

    var newTag = marker.GetComponent<MarkerTag>() ?? marker.AddComponent<MarkerTag>();
    newTag.track = track;
    newTag.step = stepIndex;
    newTag.isPlaceholder = !lit;
    newTag.burstId = (newTag.isPlaceholder ? burstId : (burstId >= 0 ? burstId : newTag.burstId));

    noteMarkers[key] = marker.transform;

    if (lit)
    {
        var vnm = marker.GetComponent<VisualNoteMarker>();
        if (vnm != null) vnm.Initialize(track.trackColor);
        var light = marker.GetComponent<MarkerLight>() ?? marker.AddComponent<MarkerLight>();
        light.LightUp(track.trackColor);
    }
    else
    {
        var vnm = marker.GetComponent<VisualNoteMarker>();
        if (vnm != null) vnm.SetWaitingParticles(track.trackColor);
        var ml = marker.GetComponent<MarkerLight>() ?? marker.AddComponent<MarkerLight>();
        ml.SetGrey(track.trackColor);
        
    }

    return marker;
}
    public void AssertNoDuplicateMarkers(InstrumentTrack track)
{
    var seen = new HashSet<int>(); // instance IDs
    var perStep = new Dictionary<int, List<GameObject>>();

    foreach (var kv in noteMarkers)
    {
        if (kv.Key.Item1 != track) continue;
        int step = kv.Key.Item2;
        var go = kv.Value ? kv.Value.gameObject : null;
        if (!go) continue;

        if (!perStep.TryGetValue(step, out var list)) perStep[step] = list = new List<GameObject>();
        list.Add(go);
    }

    foreach (var kv in perStep)
    {
        if (kv.Value.Count > 1)
        {
            Debug.LogError($"[NoteViz] DUPLICATE markers at track={track.name} step={kv.Key}: " +
                           string.Join(", ", kv.Value.Select(g => g.GetInstanceID())));
        }
    }
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

            if (map.TryGetValue(step, out var worldPos))
            {
                // Preserve current Y/Z; only update X from worldPos
                var lp = marker.localPosition;
                float newX = row.InverseTransformPoint(worldPos).x;
                marker.localPosition = new Vector3(newX, lp.y, lp.z);
            }
        }
        foreach (var k in deadKeys) noteMarkers.Remove(k);
    }
    public void TriggerBurstAscend(InstrumentTrack track, int burstId, float seconds)
{
    Debug.Log($"[TriggerBurstAscend] CALLED for {track.name} burstId={burstId}");
    
    if (!track) return;
    // Gather steps that belong to this burst
    var steps = new List<int>();
    foreach (var kv in _stepBurst)
    {
        if (kv.Key.track == track && kv.Value == burstId)
            steps.Add(kv.Key.step);
    }
    Debug.Log($"[TriggerBurstAscend] Found {steps.Count} steps for burstId={burstId}: {string.Join(",", steps)}");
    if (steps.Count == 0) return;

    // Get track row for positioning
    int trackIndex = Array.IndexOf(GameFlowManager.Instance.controller.tracks, track);
    if (trackIndex < 0 || trackIndex >= trackRows.Count) return;
    RectTransform row = trackRows[trackIndex];
    Rect rowRect = row.rect;

    // Collect GOs and mark as animating (but keep in noteMarkers dict during animation)
    var toAnimate = new List<GameObject>();
    foreach (var step in steps)
    {
        var key = (track, step);
        
        // Mark as animating FIRST so positioning logic skips them
        _animatingSteps.Add(key);
        
        if (noteMarkers.TryGetValue(key, out var tr) && tr != null)
        {
            Debug.Log($"  → Step {step}: found marker at y={tr.position.y:F1}, will animate");
            // CRITICAL: Update X position ONCE to reflect any split layout changes
            float x01 = ComputeStepX01(track, step);
            float xLocal = Mathf.Lerp(rowRect.xMin, rowRect.xMax, x01);
            var lp = tr.localPosition;
            tr.localPosition = new Vector3(xLocal, lp.y, lp.z);
            
            toAnimate.Add(tr.gameObject);
        }

        else
        {
            Debug.Log($"  → Step {step}: NO MARKER FOUND in noteMarkers dict!");
        }
        
        _stepBurst.Remove(key);
    }
    Debug.Log($"[TriggerBurstAscend] Will animate {toAnimate.Count} markers");
    // Animate and clean up
    int pending = toAnimate.Count;
    if (pending == 0)
    {
        // No markers to animate - just clean up tracking state
        foreach (var s in steps) _animatingSteps.Remove((track, s));
        DestroyOrphanRowMarkers(track);
        CullGhostBottomMarkers(track);
        HardCullRowMarkersForSteps(track, steps);
        track.RemoveNotesForBurst(burstId);
        return;
    }

    float targetY = GetTopWorldY();
    foreach (var go in toAnimate)
    {
        if (!go)
        {
            pending--;
            continue;
        }
        Debug.Log($"  → Starting ascend for marker at y={go.transform.position.y:F1}");
        StartCoroutine(AscendAndDestroy(go, targetY, seconds, () =>
        {
            pending--;
            if (pending == 0)
            {
                Debug.Log($"[AscendComplete] ALL markers done for burstId={burstId}, cleaning up");                // NOW remove from noteMarkers dict after all animations complete
                foreach (var s in steps)
                {
                    var key = (track, s);
                    noteMarkers.Remove(key);
                    _animatingSteps.Remove(key);
                }

                DestroyOrphanRowMarkers(track);
                CullGhostBottomMarkers(track);
                HardCullRowMarkersForSteps(track, steps);
                track.RemoveNotesForBurst(burstId); // stop audio for this burst
            }
        }));
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
        if (!marker) { 
            Debug.Log($"[AscendAndDestroy] Marker is null, calling onDone immediately");
            onDone?.Invoke(); 
            yield break; 
        }

        Debug.Log($"[AscendAndDestroy] Starting: marker y={marker.transform.position.y:F1} → targetY={targetWorldY:F1}");
    
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

        Debug.Log($"[AscendAndDestroy] Animation complete, destroying marker");
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
    public void DestroyOrphanRowMarkers(InstrumentTrack track, bool dryRun = true)
    {
        List<GameObject> orphans = new List<GameObject>();

        int trackIndex = Array.IndexOf(GameFlowManager.Instance.controller.tracks, track);
        if (trackIndex < 0 || trackIndex >= trackRows.Count) return;
        var row = trackRows[trackIndex];

        int currentBurst = track.currentBurstId;
        var toDestroy = new List<GameObject>();

        for (int i = 0; i < row.childCount; i++)
        {
            var child = row.GetChild(i);
            var tag = child.GetComponent<MarkerTag>();
            if (!tag || tag.track != track) continue;
            var key = (track, tag.step);
            bool inDict = noteMarkers.TryGetValue(key, out var tr) && tr && tr.gameObject == tag.gameObject;
            if (!inDict)
            {
                orphans.Add(tag.gameObject);
            }

            // Only stale placeholders are safe to destroy here.
            if (tag.isPlaceholder && tag.burstId != currentBurst)
                toDestroy.Add(child.gameObject);
        }
        if (orphans.Count > 0)
        {
            Debug.LogWarning($"[NoteViz] {(dryRun ? "FOUND" : "DESTROYING")} orphans track={track.name} :: " +
                             string.Join(", ", orphans.Select(o => o.GetInstanceID())));
            if (!dryRun) foreach (var go in orphans) Destroy(go);
        }
        foreach (var go in toDestroy)
        {
#if UNITY_EDITOR
            if (!Application.isPlaying) UnityEngine.Object.DestroyImmediate(go);
            else UnityEngine.Object.Destroy(go);
#else
        UnityEngine.Object.Destroy(go);
#endif
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

        int trackIndex = Array.IndexOf(GameFlowManager.Instance.controller.tracks, track);
        if (trackIndex < 0 || trackIndex >= trackRows.Count) return;

        RectTransform row = trackRows[trackIndex];
        Rect rowRect      = row.rect;

        int totalSteps    = Mathf.Max(1, track.GetTotalSteps());
        int longestSteps  = Mathf.Max(1, GetActiveLongestSteps());
        float localFraction = totalSteps / (float)longestSteps;

        float bottomWorldY = GetBottomWorldY();
        float bottomLocalY = row.InverseTransformPoint(new Vector3(0f, bottomWorldY, 0f)).y; 

        var kvs = noteMarkers.ToArray();
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
    private Transform TryAdoptExistingAt(InstrumentTrack track, int stepIndex, RectTransform row)
    {
        // Look in the row for any marker with the same (track,step)
        var tag = row.GetComponentsInChildren<MarkerTag>(includeInactive: true)
            .FirstOrDefault(t => t && t.track == track && t.step == stepIndex);
        return tag ? tag.transform : null;
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
