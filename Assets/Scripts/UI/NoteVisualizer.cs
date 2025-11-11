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
    private struct BlastTask
    {
        public GameObject go;
        public Vector3 startPos;
        public Vector3 dir;       // world-space direction
        public float startScale;
        public float endScale;
        public float dur;         // seconds
        public float t;           // normalized 0..1
        public System.Action onDone; // optional (SFX, pooling return, etc.)
    }

    private struct MarkerState
    {
        public GameObject go;
        public (InstrumentTrack track, int step) key;
        public float stepY;              // distance moved per loop tick

        public int delayLoopsRemaining;  // per-marker optional delay
        public int loopsRemaining;       // per-marker countdown to arrival

        public System.Action onDonePerMarker; // your old coroutine's onDone lambda
    }
    private struct RushTask
    {
        public GameObject go;        // marker being animated
        public Vector3 startPos;     // cached at enqueue
        public Transform target;     // vehicle (or any target)
        public float dur;            // seconds
        public float t;              // normalized 0..1
        public System.Action onArrive; // called when we reach target (chain effects/cleanup)
    }
    private readonly Dictionary<InstrumentTrack, AscendTask> _ascendTasks = new();
    private int _lastObservedCompletedLoops = -1;
    private readonly Dictionary<(InstrumentTrack track, int step), int> _stepBurst = new();
    private readonly HashSet<(InstrumentTrack track, int step)> _animatingSteps = new();
    private readonly List<BlastTask> _blastTasks = new();
    private readonly List<RushTask> _rushTasks = new();
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
        var ml = markerGo.GetComponent<MarkerLight>() ?? markerGo.AddComponent<MarkerLight>(); 
        ml.LightUp(track.trackColor);
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
        int   globalLoopMultiplier = GameFlowManager.Instance.controller.GetMaxLoopMultiplier();
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
        int longestSteps = GetDeclaredLongestSteps();

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
                float xLocal = ComputeXLocalForTrack(rowRect, track, step);
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
        for (int i = _blastTasks.Count - 1; i >= 0; i--)
        {
            var task = _blastTasks[i];
            if (!task.go) { _blastTasks.RemoveAt(i); continue; }

            task.t += Time.deltaTime / Mathf.Max(0.0001f, task.dur);
            float u = Mathf.Clamp01(task.t);

            // position lerp along a short ray
            var p = task.startPos + task.dir * u;
            task.go.transform.position = p;

            // scale down/up as desired
            float s = Mathf.Lerp(task.startScale, task.endScale, u);
            task.go.transform.localScale = Vector3.one * s;

            if (u >= 1f)
            {
                try { task.onDone?.Invoke(); } catch (System.Exception e) { Debug.LogException(e); }
                if (task.go) Destroy(task.go);
                _blastTasks.RemoveAt(i);
            }
            else
            {
                _blastTasks[i] = task;
            }
        }
        for (int i = _rushTasks.Count - 1; i >= 0; i--)
        {
            var task = _rushTasks[i];

            // If marker or target vanished, drop the task
            if (!task.go || !task.target)
            {
                _rushTasks.RemoveAt(i);
                continue;
            }

            task.t += Time.deltaTime / Mathf.Max(0.0001f, task.dur);
            float u = Mathf.Clamp01(task.t);

            // Lerp in world space
            var p = Vector3.Lerp(task.startPos, task.target.position, u);
            task.go.transform.position = p;

            if (u >= 1f)
            {
                // Arrived — fire callback, then clean up marker however you do it next
                try { task.onArrive?.Invoke(); } catch (System.Exception e) { Debug.LogException(e); }
                if (task.go) Destroy(task.go); // or return to pool if you pool markers
                _rushTasks.RemoveAt(i);
            }
            else
            {
                _rushTasks[i] = task; // write back mutated struct
            }
        }    
    }
private void OnLoopBoundary()
{
    if (_ascendTasks.Count == 0) return;

    // Take a snapshot of keys so we can safely modify the dictionary.
    var keys = new List<InstrumentTrack>(_ascendTasks.Keys);

    foreach (var trk in keys)
    {
        var task = _ascendTasks[trk];

        // Optional: keep batch-level delay if you still use it.
        if (task.delayLoopsRemaining > 0)
        {
            task.delayLoopsRemaining--;
            _ascendTasks[trk] = task;   // <-- WRITE BACK
            continue;
        }

        bool anyAlive = false;

        for (int i = 0; i < task.markers.Count; i++)
        {
            var ms = task.markers[i];

            // Already finished?
            if (ms.go == null) continue;

            // Per-marker delay (optional)
            if (ms.delayLoopsRemaining > 0)
            {
                ms.delayLoopsRemaining--;
                task.markers[i] = ms;
                anyAlive = true;
                continue;
            }

            // Move one "loop step" toward target
            var t = ms.go.transform;
            var p = t.position; // world space
            p.y += ms.stepY;    // step size was computed at enqueue time
            t.position = p;

            // Countdown loops; when zero, this marker has arrived
            ms.loopsRemaining = Mathf.Max(0, ms.loopsRemaining - 1);

            if (ms.loopsRemaining <= 0)
            {
                // Per-marker finish (what the coroutine tail used to do)
                _animatingSteps.Remove(ms.key);
                if (ms.go) Destroy(ms.go);

                try { ms.onDonePerMarker?.Invoke(); } catch (System.Exception e) { Debug.LogException(e); }

                ms.go = null; // mark finished
                task.markers[i] = ms;
                // don’t set anyAlive here; marker is done
            }
            else
            {
                // Still moving next loop
                task.markers[i] = ms;
                anyAlive = true;
            }
        }

        task.stepsCompleted++; // keep if you use it elsewhere

        if (!anyAlive)
        {
            // All markers for this track are finished
            try { task.onArrive?.Invoke(); } catch (System.Exception e) { Debug.LogException(e); }
            _ascendTasks.Remove(trk);

            // IMPORTANT: don’t mass-destroy markers or call DestroyOrphanRowMarkers() here.
            // Burst-level cleanup (remove from noteMarkers, culls, audio) is handled by the
            // pending--/if(pending==0) lambda you pass per marker.
        }
        else
        {
            _ascendTasks[trk] = task;   // <-- WRITE BACK
        }
    }
}
    public void MarkGhostPadding(InstrumentTrack track, int startStepInclusive, int count) {
        if (!_ghostNoteSteps.TryGetValue(track, out var set))
            _ghostNoteSteps[track] = set = new HashSet<int>();

        var ctrl = GameFlowManager.Instance?.controller;
        var drum = GameFlowManager.Instance?.activeDrumTrack;
        int leaderBins = (ctrl != null) ? Mathf.Max(1, ctrl.GetMaxLoopMultiplier()) : 1;
        int binSize    = (drum != null) ? Mathf.Max(1, drum.totalSteps) : 16;
        int total      = Mathf.Max(1, leaderBins * binSize);

        for (int i = 0; i < count; i++)
            set.Add((startStepInclusive + i) % total);
    }
    private void EnqueueRush(GameObject marker, Transform target, float durationSeconds, System.Action onArrive = null)
    {
        if (!marker || !target) return;

        _rushTasks.Add(new RushTask
        {
            go       = marker,
            startPos = marker.transform.position,
            target   = target,
            dur      = Mathf.Max(0.01f, durationSeconds),
            t        = 0f,
            onArrive = onArrive
        });
    }

    private void EnqueueBlast(GameObject marker, Vector3 dir, float durationSeconds, float startScale = 1f, float endScale = 0.2f, System.Action onDone = null)
    {
        if (!marker) return;

        // Normalize dir (fallback to a tiny random nudge if zero)
        var d = dir;
        if (d.sqrMagnitude < 1e-6f) d = UnityEngine.Random.insideUnitSphere * 0.5f;
        d = d.normalized * 0.8f; // tune pop distance

        _blastTasks.Add(new BlastTask
        {
            go         = marker,
            startPos   = marker.transform.position,
            dir        = d,
            startScale = startScale,
            endScale   = endScale,
            dur        = Mathf.Max(0.01f, durationSeconds),
            t          = 0f,
            onDone     = onDone
        });
    }

    public float GetTopWorldY()
    {
        RectTransform rt = GetComponent<RectTransform>();
        Vector3[] worldCorners = new Vector3[4];
        rt.GetWorldCorners(worldCorners);
        return worldCorners[1].y;
    }
    public Transform GetUIParent() => _uiParent;
    int GetDeclaredLongestSteps()
    {
        var ctrl = GameFlowManager.Instance?.controller;
        var drum = GameFlowManager.Instance?.activeDrumTrack;
        if (ctrl == null || drum == null) return 1;

        int leaderBins  = Mathf.Max(1, ctrl.GetMaxLoopMultiplier());
        int binSize     = Mathf.Max(1, drum.totalSteps);
        return leaderBins * binSize;
    }

    public void CanonicalizeTrackMarkers(InstrumentTrack track, int currentBurstId)
{
    if (track == null) return;

    // Resolve row
    int trackIndex = Array.IndexOf(GameFlowManager.Instance.controller.tracks, track);
Debug.Log($"[CANONICALIZE TRACK MARKERS] {track.name} for {currentBurstId}");
    if (trackIndex < 0 || trackIndex >= trackRows.Count) return;
    var row = trackRows[trackIndex];

    // CURRENT loop steps (authoritative)
    var loopSteps = new HashSet<int>(track.GetPersistentLoopNotes().Select(n => n.Item1));
    Debug.Log($"[CANONICALIZE TRACK MARKERS] {track.name} for {currentBurstId} Loop Steps Count: {loopSteps.Count}");

    // Remove any stale entries for this track first
    var toRemove = new List<(InstrumentTrack,int)>();
    foreach (var kv in noteMarkers)
    {
        if (kv.Key.Item1 == track && (kv.Value == null || kv.Value.gameObject == null))
            toRemove.Add(kv.Key);
    }
    Debug.Log($"[CANONICALIZE TRACK MARKERS] {track.name} for {currentBurstId} Loop Steps Count: {loopSteps.Count} Removed {toRemove.Count}");

    foreach (var k in toRemove)
    {
        Debug.Log($"[CANONICALIZE TRACK MARKERS] Remove {k.Item1}");
        noteMarkers.Remove(k);
    }

    // Pass 1: normalize tags
    var tags = row.GetComponentsInChildren<MarkerTag>(includeInactive: true);
    Debug.Log($"[CANONICALIZE TRACK MARKERS] Tags: {tags.Length}");

    foreach (var tag in tags)
    {
        if (!tag || tag.track != track) continue;

        bool isLoop = loopSteps.Contains(tag.step); // loop is the source of truth
        bool inFilledBin = true;
        try { inFilledBin = track.IsStepInFilledBin(tag.step); } catch {} 
        if (!isLoop && tag.isPlaceholder) { 
            // NEW: keep placeholders that belong to *this* canonicalization burst,
            // // even if the bin isn't filled yet. This prevents just-placed markers
            // (e.g., expansion steps 16–32) from being destroyed immediately,
            // // which is what was breaking your NoteTethers (end became Missing).
            if (tag.burstId == currentBurstId) { 
                var key = (track, tag.step); 
                noteMarkers[key] = tag.transform; // ensure tether rebind can find it
                // ensure greyed look persists
                var ml = tag.GetComponent<MarkerLight>() ?? tag.gameObject.AddComponent<MarkerLight>(); 
                ml.SetGrey(track.trackColor); 
                continue;
            }
            
            // Placeholders from other bursts in unfilled bins can still be culled
            if (!inFilledBin) {
                #if UNITY_EDITOR
                if (!Application.isPlaying) UnityEngine.Object.DestroyImmediate(tag.gameObject);else UnityEngine.Object.Destroy(tag.gameObject);
                #else
                    UnityEngine.Object.Destroy(tag.gameObject);
                #endif
                continue;
            }
            // else: placeholder in a filled bin (legacy visual) → keep; it'll be
            // // // normalized by later lighting/loop writes if appropriate.
        }
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
            Debug.Log($"[CANONICALIZE TRACK MARKERS] Placeholder Tag: {tag.gameObject.name}");

            if (tag.burstId != currentBurstId)
            {
                Debug.Log($"[CANONICALIZE TRACK MARKERS] Placeholder Tag: {tag.gameObject.name} BurstID is not Current BurstID");

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
    // NEW: remove orphans using the SAME burst id the caller used
    int activeBurst = (currentBurstId >= 0) ? currentBurstId : track.currentBurstId; 
    DestroyOrphanRowMarkers(track, activeBurst, dryRun: false);
}

    private int GetLeaderStepsSafe()
    {
        var drum = GameFlowManager.Instance?.activeDrumTrack;
        if (drum != null)
        {
            // Uses DrumTrack.GetLeaderSteps(), which already inspects loopMultiplier among active tracks.
            // DrumTrack.GetLeaderSteps is defined here.
            // (Falls back to drum.totalSteps when no tracks are active)
            int n = drum.GetLeaderSteps();
            return Mathf.Max(1, n);
        }

        // No drum? Fall back to the largest step count among tracks (or 16 if none).
        var ctrl = GameFlowManager.Instance?.controller;
        if (ctrl?.tracks != null && ctrl.tracks.Length > 0)
        {
            int best = 0;
            foreach (var t in ctrl.tracks)
                if (t != null) best = Mathf.Max(best, Mathf.Max(1, t.GetTotalSteps()));
            return best > 0 ? best : 16;
        }
        return 16;
    }

    private static bool SafeIsStepInFilledBin(InstrumentTrack track, int stepIndex)
    {
        try
        {
            // New API name
            if (track != null && track.IsStepInFilledBin(stepIndex)) return true;
            return false;
        }
        catch
        {
            // Older builds without IsStepInFilledBin → assume filled so UI doesn’t disappear
            return true;
        }
    }
public GameObject PlacePersistentNoteMarker(InstrumentTrack track, int stepIndex, bool lit = true, int burstId = -1)
{
    Debug.Log($"[PLACE] Starting for {stepIndex} on {track.name}");
    Debug.Log($"[NoteViz] Placing Persistent Note marker for {track} at {stepIndex}, lit {lit} burst id {burstId}");
    var key = (track, stepIndex);

    // Resolve row (we'll need it for adoption or creation)
    int trackIndex = Array.IndexOf(GameFlowManager.Instance.controller.tracks, track);
    Debug.Log($"[PLACE] Starting for {stepIndex} on {track.name} index: {trackIndex}");

    if (trackIndex < 0 || trackIndex >= trackRows.Count) return null;
    RectTransform row = trackRows[trackIndex];
    Rect rowRect = row.rect;
    Debug.Log($"[PLACE] Using {row.name} on {track.name} index: {trackIndex}");

    // Compute once and reuse everywhere (prevents multiple declarations)
    bool inFilledBin = SafeIsStepInFilledBin(track, stepIndex);
    bool isLoopOwned = (burstId < 0);
    bool shouldLight = isLoopOwned ? lit : (lit && inFilledBin);
    Debug.Log($"[PLACE] {stepIndex} on {track.name} index: {trackIndex} Filled bin: {inFilledBin} Loop Owned: {isLoopOwned} Lit: {shouldLight}");
    // REUSE if we already have one in the dictionary
    if (noteMarkers.TryGetValue(key, out var existing) && existing && existing.gameObject.activeInHierarchy)
    {
        Debug.Log($"[NoteViz] REUSE marker track={track.name} step={stepIndex} lit={lit} burst={burstId} go={existing.gameObject.GetInstanceID()}");
        Debug.Log($"[PLACE] REUSE {stepIndex} on {track.name} index: {trackIndex} Filled bin: {inFilledBin} Loop Owned: {isLoopOwned} Lit: {shouldLight}");

        // Do NOT create a new one while animating—just keep the existing and (optionally) defer lighting
        if (_animatingSteps.Contains(key))
        {
            Debug.Log($"[PLACE] Animating Steps Contains {stepIndex} on {track.name} index: {trackIndex} Filled bin: {inFilledBin} Loop Owned: {isLoopOwned} Lit: {shouldLight}");

            Debug.Log($"[NoteViz] [Reuse-WhileAnimating] step {stepIndex} is animating → keep existing, no new spawn");
            return existing.gameObject;
        }

        if (shouldLight)
        {
            // Light ONLY if actually in the loop (i.e., this step persisted)
            bool inLoop = track.GetPersistentLoopNotes().Any(n => n.Item1 == stepIndex);
            if (inLoop)
            {
                var existingTag = existing.GetComponent<MarkerTag>() ?? existing.gameObject.AddComponent<MarkerTag>();
                existingTag.isPlaceholder = false;
                if (existingTag.burstId <= 0 && burstId >= 0)
                    existingTag.burstId = burstId;
            }
        }
        else
        {
            // Force placeholder/grey if the bin is empty or caller requested placeholder
            var tag = existing.GetComponent<MarkerTag>() ?? existing.gameObject.AddComponent<MarkerTag>();
            tag.isPlaceholder = true;
            if (burstId >= 0) tag.burstId = burstId;
            var ml = existing.GetComponent<MarkerLight>() ?? existing.gameObject.AddComponent<MarkerLight>();
            ml.SetGrey(track.trackColor);
        }
        Debug.Log($"[PLACE] Returning existing object {stepIndex} on {track.name} index: {trackIndex} Filled bin: {inFilledBin} Loop Owned: {isLoopOwned} Lit: {shouldLight}");

        return existing.gameObject;
    }

    // ADOPT any existing scene marker at (track,step) to avoid dupes
    var adopt = TryAdoptExistingAt(track, stepIndex, row);
    if (adopt)
    {
        Debug.Log($"[PLACE] Trying to adopt {stepIndex} on {track.name} index: {trackIndex} Filled bin: {inFilledBin} Loop Owned: {isLoopOwned} Lit: {shouldLight}");

        Debug.Log($"[NoteViz] Found note to adopt. This shouldn't happen.");
        noteMarkers[key] = adopt;

        var tag = adopt.GetComponent<MarkerTag>() ?? adopt.gameObject.AddComponent<MarkerTag>();
        tag.track = track;
        tag.step = stepIndex;

        if (shouldLight)
        {
            tag.isPlaceholder = false;
            if (tag.burstId <= 0 && burstId >= 0) tag.burstId = burstId;
        }
        else
        {
            tag.isPlaceholder = true;
            if (burstId >= 0) tag.burstId = burstId;
            var ml = adopt.GetComponent<MarkerLight>() ?? adopt.gameObject.AddComponent<MarkerLight>();
            ml.SetGrey(track.trackColor);
        }

        Debug.Log($"[NoteViz] ADOPT marker track={track.name} step={stepIndex} lit={lit} burst={burstId} go={adopt.gameObject.GetInstanceID()}");
        Debug.Log($"[PLACE] Adopting {stepIndex} on {track.name} index: {trackIndex} Filled bin: {inFilledBin} Loop Owned: {isLoopOwned} Lit: {shouldLight}");

        return adopt.gameObject;
    }

    // Positioning for creation
    int totalSteps = Mathf.Max(1, track.GetTotalSteps());
    float xLocal = ComputeXLocalForTrack(rowRect, track, stepIndex);
Debug.Log($"xLocal : {xLocal} for track {track.name} stepIndex {stepIndex} lit={lit}");
    float bottomWorldY = GetBottomWorldY();
    float bottomLocalY = row.InverseTransformPoint(new Vector3(0f, bottomWorldY, 0f)).y;

    // CREATE (final fallback; idempotent guard just in case)
    if (noteMarkers.TryGetValue(key, out var appeared) && appeared)
    {
        Debug.Log($"[PLACE] Returning Fallback {stepIndex} on {track.name} index: {trackIndex} Filled bin: {inFilledBin} Loop Owned: {isLoopOwned} Lit: {shouldLight}");

        return appeared.gameObject;
    }
        // Instantiate as a child, then set LOCAL (row-space) coordinates
    GameObject marker = Instantiate(notePrefab, new Vector3(xLocal, bottomLocalY, 0f), Quaternion.identity, row);
    var newTag = marker.GetComponent<MarkerTag>() ?? marker.AddComponent<MarkerTag>();
    newTag.track = track;
    newTag.step = stepIndex;
    newTag.isPlaceholder = !shouldLight;
    newTag.burstId = (newTag.isPlaceholder ? burstId : (burstId >= 0 ? burstId : newTag.burstId));

    noteMarkers[key] = marker.transform;
Debug.Log($"[NOTEMARKER] Size: {noteMarkers.Count} Key: {key}");
    if (shouldLight)
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
        Debug.Log($"[NOTEMARKER] Setting Marker Grey... position: {ml.transform.position}");
    }
    Debug.Log($"Returning final marker [PLACE] {stepIndex} on {track.name} index: {trackIndex} Filled bin: {inFilledBin} Loop Owned: {isLoopOwned} Lit: {shouldLight}");

    return marker;
}

    private float GetBottomWorldY()
    {
        RectTransform rt = GetComponent<RectTransform>();
        Vector3[] worldCorners = new Vector3[4];
        rt.GetWorldCorners(worldCorners);
        return worldCorners[0].y; // bottom-left corner
    }
    public void UpdateNoteMarkerPositions()
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
    private float ComputeSnappedXLocalForLeader(Rect rowRect, InstrumentTrack track, int step, int snapCellSize = 0)
    {
        // 1) Resolve a valid leaderSteps (fallback to the track’s loop span)
        var drum = GameFlowManager.Instance.activeDrumTrack;
        int leaderSteps = (drum != null) ? drum.GetLeaderSteps() : 0;

        int trackSteps = Mathf.Max(1, track.GetTotalSteps());

        trackSteps = Mathf.Max(1, trackSteps);

        // If leaderSteps is not ready yet (0/negative), fallback to trackSteps
        if (leaderSteps <= 0) leaderSteps = trackSteps;

        // 2) Map track step → leader space
        float uTrack = (step % trackSteps) / (float)trackSteps;   // [0..1)
        float leaderPos = uTrack * leaderSteps;                   // [0..leaderSteps)

        // 3) Optional snap in leader cells (snapCellSize == 0 → no snapping)
        if (snapCellSize > 0)
        {
            // Round to nearest cell (avoid bias-to-zero)
            leaderPos = Mathf.Round(leaderPos / snapCellSize) * snapCellSize;
            leaderPos = Mathf.Clamp(leaderPos, 0, leaderSteps);   // cap at right edge if exactly == leaderSteps
        }

        // 4) Convert back to [0..1] and then to pixel X
        float uLeader = (leaderSteps > 0) ? Mathf.Clamp01(leaderPos / leaderSteps) : 0f;
        return Mathf.Lerp(rowRect.xMin, rowRect.xMax, uLeader);
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

    // Track row for positioning
    int trackIndex = Array.IndexOf(GameFlowManager.Instance.controller.tracks, track);
    if (trackIndex < 0 || trackIndex >= trackRows.Count) return;
    RectTransform row = trackRows[trackIndex];
    Rect rowRect = row.rect;
// ---- LATE-ARM COHORT IF NEEDED ----
    var ctrl = GameFlowManager.Instance.controller;
    var drum = GameFlowManager.Instance.activeDrumTrack;

// 1) Define the same window used by the controller: [0..cohortWindowFraction of leader]
    int leaderSteps = (drum != null) ? drum.GetLeaderSteps() : 0;
    if (leaderSteps <= 0 && ctrl != null && ctrl.tracks != null)
        leaderSteps = ctrl.tracks.Where(t => t != null).Select(t => t.GetTotalSteps()).DefaultIfEmpty(32).Max();

    int endLeader = Mathf.Max(1, Mathf.RoundToInt(leaderSteps * 0.5f)); // keep in sync with controller field if public
    int trackSteps = Mathf.Max(1, track.GetTotalSteps());
    int endTrack   = Mathf.Clamp(endLeader, 1, trackSteps);

// 2) If not armed, try to arm a real cohort from the current loop
    if (!track.ascensionCohort.armed)
    {
        track.ArmAscensionCohort(0, endTrack);
        Debug.Log($"[CHORD][LATE ARM] real cohort window [0,{endTrack}) armed={track.ascensionCohort.armed} " +
                  $"count={(track.ascensionCohort.stepsRemaining!=null?track.ascensionCohort.stepsRemaining.Count:0)}");
    }

// 3) If still not armed (loop had no notes in the window), arm a synthetic cohort
    if (!track.ascensionCohort.armed)
    {
        // Build a cohort from the steps we’re actually ascending that fall in the window
        var inWindow = new HashSet<int>(steps.Where(s => s >= 0 && s < endTrack));
        track.ascensionCohort = new AscensionCohort {
            windowStartInclusive = 0,
            windowEndExclusive   = endTrack,
            stepsRemaining       = inWindow,
            armed                = inWindow.Count > 0
        };
        Debug.Log($"[CHORD][SYNTH ARM] window [0,{endTrack}) from burst steps -> count={inWindow.Count} armed={track.ascensionCohort.armed}");
    }

    // CONFIG: set to 1 for per-cell snapping, 16 to snap to 0/16/32 on a 32-leader loop, etc.
    const int SNAP_CELL = 0; // ← 16 gives you 0,16,32,... on a 32 grid. Set to 1 to snap to every leader step.

    // Collect GOs and mark as animating (keep in dict during anim)
    var toAnimate = new List<(GameObject go, int step)>();
    foreach (var step in steps)
    {
        var key = (track, step);
        _animatingSteps.Add(key); // mark as animating first

        if (noteMarkers.TryGetValue(key, out var tr) && tr != null)
        {
            float xLocal =
                // Use snapped X so the burst lines up with leader grid (0,16,32,...)
                ComputeSnappedXLocalForLeader(rowRect, track, step, SNAP_CELL);
                // If you prefer your original behavior, use:
                // ComputeXLocalForTrack(rowRect, track, step);

            var lp = tr.localPosition;
            tr.localPosition = new Vector3(xLocal, lp.y, lp.z);
            toAnimate.Add((tr.gameObject, step));
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
        // No visible markers, but the musical removal still happens.
        foreach (var s in steps) _animatingSteps.Remove((track, s));
        DestroyOrphanRowMarkers(track,burstId);
        CullGhostBottomMarkers(track);
        HardCullRowMarkersForSteps(track, steps);

        // Remove notes for this burst and notify per-step so cohorts can complete
        track.RemoveNotesForBurst(burstId);
        foreach (var s in steps)
            track.NotifyNoteAscendedOrRemovedAtStep(s);

        return;
    }

    float targetY = GetTopWorldY();
    var loops = SecondsToLoops(seconds, GameFlowManager.Instance.activeDrumTrack);

    foreach (var item in toAnimate)
    {
        var go = item.go;
        var step = item.step;
        if (!go)
        {
            pending--;
            // Even if GO is null, we still want the musical side-effects
            track.NotifyNoteAscendedOrRemovedAtStep(step);
            continue;
        }

        Debug.Log($"  → Starting ascend for marker at y={go.transform.position.y:F1}");

        // Enqueue one job per item; per-item callback handles per-step cleanup + notify
        EnqueueAscendForTrack(
            track,
            new[] {
                (go, (track, step), targetY, /*delayLoops*/ 0, /*totalLoops*/ loops, (System.Action)(() =>
                {
                    // Per-step cleanup
                    var k = (track, step);
                    noteMarkers.Remove(k);
                    _animatingSteps.Remove(k);

                    // *** THE IMPORTANT PART: tell the track this step ascended/vanished ***
                    track.NotifyNoteAscendedOrRemovedAtStep(step);

                    // Batch end cleanup
                    pending--;
                    if (pending == 0)
                    {
                        Debug.Log($"[AscendComplete] ALL markers done for burstId={burstId}, cleaning up");
                        DestroyOrphanRowMarkers(track,burstId);
                        CullGhostBottomMarkers(track);
                        HardCullRowMarkersForSteps(track, steps);

                        // Musical removal by burst (safe even if individual steps already pruned)
                        track.RemoveNotesForBurst(burstId);
                    }
                }))
            }
        );
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
    public void TriggerNoteRushToVehicle(InstrumentTrack track, Vehicle v)
    {
        foreach (var marker in GetNoteMarkers(track))
            EnqueueRush(marker, v.transform, 0.6f, () =>
            {
                EnqueueBlast(marker, UnityEngine.Random.insideUnitSphere, 0.25f);
            });

    }
    public void TriggerNoteBlastOff(InstrumentTrack track)
    {
        var gos = GetNoteMarkers(track);
        var keys = new List<(InstrumentTrack,int)>();
        foreach (var kv in noteMarkers)
            if (kv.Key.Item1 == track) keys.Add(kv.Key);
        foreach (var k in keys) noteMarkers.Remove(k);

        foreach (var go in gos)
            if (go) EnqueueBlast(go, UnityEngine.Random.insideUnitSphere, 0.25f);

        Debug.Log($"[DESTROY] Note Blast off {track.name}");
        DestroyOrphanRowMarkers(track,-1);
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
    public void DestroyOrphanRowMarkers(InstrumentTrack track, int activeBurstId, bool dryRun = true)
    {
        
        List<GameObject> orphans = new List<GameObject>();

        int trackIndex = Array.IndexOf(GameFlowManager.Instance.controller.tracks, track);
        if (trackIndex < 0 || trackIndex >= trackRows.Count) return;
        var row = trackRows[trackIndex];

        int currentBurst = activeBurstId; 
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
            Debug.Log($"[ORPHAN] considering {tag.step} ph={tag.isPlaceholder} bid={tag.burstId} keep={tag.isPlaceholder} (active={currentBurst})");

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
        int longestSteps  = Mathf.Max(1, GetDeclaredLongestSteps());
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
//            float x01 = ComputeStepX01(track, stepIndex);
//            float xLocal = Mathf.Lerp(rowRect.xMin, rowRect.xMax, x01);
            float xLocal = ComputeXLocalForTrack(rowRect, track, stepIndex);
            var lp = tf.localPosition;
            float yLocal = IsAscending(tf) ? lp.y : bottomLocalY; 
            tf.localPosition = new Vector3(xLocal, yLocal, lp.z);
        }
    }
    private void EnqueueAscendForTrack(
        InstrumentTrack track,
        IEnumerable<(GameObject go, (InstrumentTrack,int) key, float targetY, int delayLoops, int totalLoops, System.Action onDonePerMarker)> items)
    {
        // Ensure task exists for this track
        if (!_ascendTasks.TryGetValue(track, out var task))
        {
            task = new AscendTask
            {
                track = track,
                markers = new List<MarkerState>(),
                delayLoopsRemaining = 0,  // batch-level delay (unused if you rely on per-marker delays)
                totalAscendLoops = 0,     // not used now; per-marker loops control arrival
                stepsCompleted = 0,
                onArrive = null
            };
        }

        foreach (var it in items)
        {
            if (!it.go) continue;

            var startY = it.go.transform.position.y;
            var loops  = Mathf.Max(1, it.totalLoops);
            var stepY  = (it.targetY - startY) / loops;

            task.markers.Add(new MarkerState
            {
                go = it.go,
                key = it.key,
                stepY = stepY,
                delayLoopsRemaining = Mathf.Max(0, it.delayLoops),
                loopsRemaining = loops,
                onDonePerMarker = it.onDonePerMarker
            });

            // keep your existing guard against editing animating steps
            _animatingSteps.Add(it.key);
        }

        _ascendTasks[track] = task;
    }

    public void RequestLeaderGridChange(int newLeaderSteps) {
        // Apply immediately to prevent left-half folding during growth
        var ctrl = GameFlowManager.Instance?.controller; 
        if (ctrl?.tracks == null) return; 
        foreach (var t in ctrl.tracks) 
            if (t) RecomputeTrackLayout(t);
        
        UpdateNoteMarkerPositions();
    }

    float ComputeXLocalForTrack(Rect rowRect, InstrumentTrack track, int stepIndex)
    {
        int trackSteps = track.GetTotalSteps(); 
        if (trackSteps <= 0) trackSteps = 16;
        int leaderBinsActive = Mathf.Max(1, GameFlowManager.Instance.controller.GetMaxActiveLoopMultiplier());
            // …and declared bins (track.loopMultiplier) ignoring “active” gating
        int leaderBinsDeclared = Mathf.Max(1, GameFlowManager.Instance.controller.GetMaxLoopMultiplier());

        int binSize       = Mathf.Max(1, track.BinSize());
        // Which bin does this step belong to on THIS track?
        int binIndex = stepIndex / binSize;
        // --- Key change: placement should *at minimum* include the bin that contains this step.
        // If we’re spawning steps in bin #1 while leaderBinsNow==1, expand the *placement* grid to 2.
        int leaderBinsForPlacement = Mathf.Max(leaderBinsDeclared, binIndex + 1);

        
        // Local index inside the bin
        int localInBin = stepIndex % binSize;
        float uMin = (float)binIndex / leaderBinsForPlacement; 
        float uMax = (float)(binIndex + 1) / leaderBinsForPlacement;

        // Position inside the bin: center in each step cell
        float uLocal = (localInBin + 0.5f) / binSize;

        // Lerp into rowRect
        float u = Mathf.Lerp(uMin, uMax, uLocal);
        return Mathf.Lerp(rowRect.xMin, rowRect.xMax, u);
    }

    private int SecondsToLoops(float seconds, DrumTrack drum)
    {
        var loopLen = Mathf.Max(0.0001f, drum.GetLoopLengthInSeconds());
        return Mathf.Max(1, Mathf.RoundToInt(seconds / loopLen));
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
                if (ms.go != null && ms.go.transform == tf)
                    return true;
            }
        }
        return false;
    }
    // Returns all (step, go, tag) markers currently drawn for a given track.
    private IEnumerable<(int step, Transform tr, MarkerTag tag)> EnumerateTrackMarkers(InstrumentTrack track)
    {
        foreach (var kv in noteMarkers)
        {
            var (t, step) = kv.Key;
            if (t != track) continue;
            var tr = kv.Value;
            if (!tr || !tr.gameObject) continue;

            var tag = tr.GetComponent<MarkerTag>();
            yield return (step, tr, tag);
        }
    }

    private void DestroyMarkerImmediateSafe(Transform tr)
    {
        if (!tr) return;
#if UNITY_EDITOR
        if (!Application.isPlaying) UnityEngine.Object.DestroyImmediate(tr.gameObject);
        else UnityEngine.Object.Destroy(tr.gameObject);
#else
    UnityEngine.Object.Destroy(tr.gameObject);
#endif
    }

}
