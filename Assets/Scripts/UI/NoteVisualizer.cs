using System;
using System.Collections;
using UnityEngine;
using System.Collections.Generic;
using System.Linq;

public class NoteVisualizer : MonoBehaviour
{
    public RectTransform playheadLine;
    private Dictionary<InstrumentTrack, float> _waveAmplitudes = new();
    private Dictionary<InstrumentTrack, HashSet<int>> _ghostNoteSteps = new();
    private readonly Dictionary<(InstrumentTrack track, int localStep, int k), Transform> _cloneMarkers
        = new Dictionary<(InstrumentTrack, int, int), Transform>();
    public ParticleSystem playheadParticles;

    [Header("Note Ribbon Settings")]
    public Material ribbonMaterial;
    public float ribbonWidth = 5f;
    public int ribbonResolution = 64;
    private List<LineRenderer> ribbons = new List<LineRenderer>();
    public GameObject notePrefab;
    public GameObject noteTetherPrefab;
    [Header("Ribbon Animation")]
    public float waveSpeed = 2f;
    public float velocityMultiplier = 0.2f;
    public List<RectTransform> trackRows;
    public Dictionary<(InstrumentTrack, int), Transform> noteMarkers = new();

    private Canvas _worldSpaceCanvas;
    private Transform _uiParent;
    private Dictionary<InstrumentTrack, Dictionary<int, Vector3>> _trackStepWorldPositions = new();
    private Dictionary<InstrumentTrack, float> _ribbonWidths = new();
    private Dictionary<InstrumentTrack, float> _ribbonIntensities = new();
    private Vehicle _energyTransferVehicle;
    private bool isInitialized;
    private DrumTrack _drumTrack;
    private readonly Dictionary<(InstrumentTrack, int), List<Transform>> _tiledClones = new ();
    
    void Update() {
    if (!isInitialized || playheadLine == null || GameFlowManager.Instance.activeDrumTrack == null || GameFlowManager.Instance.controller.tracks == null)
        return;

    // --- Playhead (same logic, just safer clamps) ---
    float globalElapsed = (float)(AudioSettings.dspTime - GameFlowManager.Instance.activeDrumTrack.startDspTime);
    float baseLoopLength = GameFlowManager.Instance.activeDrumTrack.GetLoopLengthInSeconds();
    int globalLoopMultiplier = GameFlowManager.Instance.controller.GetMaxLoopMultiplier();
    float fullVisualLoopDuration = Mathf.Max(0.0001f, baseLoopLength * Mathf.Max(1, globalLoopMultiplier));
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
        {
            shimmer = true; break;
        }
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


    // --- Ribbons: build in ROW-LOCAL space; write world-space into step map ---
    int   longestLoopSteps = GameFlowManager.Instance.controller.tracks.Max(t => t.GetTotalSteps());
    for (int i = 0; i < ribbons.Count; i++)
    {
        LineRenderer   lr   = ribbons[i];
        RectTransform  row  = trackRows[i];
        InstrumentTrack track = GameFlowManager.Instance.controller.tracks[i];

        Rect rowRect  = row.rect;
        float xLeft   = rowRect.xMin;
        float xRight  = rowRect.xMax;
        float yMid    = (rowRect.yMin + rowRect.yMax) * 0.5f;

        float amplitude = _waveAmplitudes.TryGetValue(track, out var amp) ? amp : 0f;

        // local-space positions for the LR

        int res = Mathf.Max(2, ribbonResolution);
        Vector3[] localPositions = new Vector3[res];

        // world-space lookup for markers/tethers (keyed by GLOBAL step)
        Dictionary<int, Vector3> stepMap = new Dictionary<int, Vector3>(); 
        int   trackSteps    = Mathf.Max(1, track.GetTotalSteps());
        int   longestSteps  = GameFlowManager.Instance.controller.tracks.Max(t => t.GetTotalSteps());
        float localFraction = trackSteps / (float)Mathf.Max(1, longestSteps);

        for (int j = 0; j < res; j++)
        {
            float t01        = j / (float)(res - 1);                    // 0..1 across the row width
            float xLocalLine = Mathf.Lerp(rowRect.xMin, rowRect.xMax, t01);
            int   localStep  = Mathf.FloorToInt(t01 * trackSteps); 
            float tWidth     = t01 * localFraction;                      // compress into local segment
            float xLocal     = Mathf.Lerp(xLeft, xRight, tWidth);
            float xLocalMap  = Mathf.Lerp(rowRect.xMin, rowRect.xMax, tWidth);

            // vertical wave in local Y (kept! no overwrite)
            float yOffset = Mathf.Sin(Time.time * waveSpeed + j * velocityMultiplier) * amplitude;
            float yLocal = yMid + Mathf.Sin(Time.time * waveSpeed + j * velocityMultiplier) * amplitude;
            localPositions[j] = new Vector3(xLocalLine, yLocal, 0f);
            if (!stepMap.ContainsKey(localStep)) 
                stepMap[localStep] = row.TransformPoint(new Vector3(xLocalMap, yLocal, 0f));
        }

        lr.positionCount = res;
        lr.SetPositions(localPositions); // useWorldSpace = false
        _trackStepWorldPositions[track] = stepMap;
        // width/color by density
        int maxNotes   = GameFlowManager.Instance.controller.tracks.Max(t => t.GetNoteDensity());
        int trackNotes = Mathf.Max(1, track.GetNoteDensity());
        float densityT = (maxNotes > 0) ? (float)trackNotes / maxNotes : 0f;

        float minWidth = 1f, maxWidth = 3f;
        float targetWidth = Mathf.Lerp(minWidth, maxWidth, densityT);
        lr.widthMultiplier = targetWidth;
        _ribbonWidths[track] = targetWidth;

        Color baseColor = track.trackColor;
        float vibrancy  = Mathf.Lerp(0.5f, 1f, densityT);
        Color scaled    = baseColor * vibrancy; scaled.a = Mathf.Lerp(0.1f, 0.65f, densityT);
        lr.startColor = lr.endColor = scaled;
        _ribbonIntensities[track] = vibrancy;

        // publish step map
        _trackStepWorldPositions[track] = stepMap;
    }

    // keep marker sync + vine anim
    UpdateNoteMarkerPositions();
    UpdateClonePositions();
    foreach (var track in GameFlowManager.Instance.controller.tracks)
        UpdateTiledClonePositions(track);
    
}
public void SyncTiledClonesForTrack(InstrumentTrack track)
{
    if (_drumTrack == null || GameFlowManager.Instance?.controller == null) return;

    int drumSteps   = _drumTrack.totalSteps;
    int maxMult     = GameFlowManager.Instance.controller.GetMaxLoopMultiplier();
    int myMult      = Mathf.Max(1, track.loopMultiplier);
    int trackSteps  = drumSteps * myMult;
    int globalSteps = drumSteps * Mathf.Max(1, maxMult);

    // How many audible windows this track has across the global phrase:
    int repeats = Mathf.Max(1, globalSteps / Mathf.Max(1, trackSteps));
    int neededK = repeats - 1; // we tile for k=1..neededK

    // 1) Build a set of required clone keys from current base markers (local keys)
    var required = new HashSet<(InstrumentTrack,int,int)>();
    foreach (var kv in noteMarkers)
    {
        var (t, localStep) = kv.Key;
        if (t != track) continue;
        for (int k = 1; k <= neededK; k++)
            required.Add((track, localStep, k));
    }

    // 2) Create any missing clones
    int trackIndex = Array.IndexOf(GameFlowManager.Instance.controller.tracks, track);
    if (trackIndex < 0 || trackIndex >= trackRows.Count) return;
    RectTransform row = trackRows[trackIndex];

    foreach (var key in required)
    {
        if (_cloneMarkers.ContainsKey(key)) continue;

        // Instantiate a visual-only clone under the correct row
        var cloneGO = Instantiate(notePrefab, Vector3.zero, Quaternion.identity, row);
        var t = cloneGO.transform;

        // Mirror lit/grey state from base marker (if available)
        if (noteMarkers.TryGetValue((track, key.Item2), out var baseT) && baseT != null)
        {
            // If the base is lit, light the clone; else keep it grey
            var baseVnm = baseT.GetComponent<VisualNoteMarker>();
            bool baseLit = baseVnm != null && baseVnm.IsLit; // add IsLit bool to your VisualNoteMarker if you don't have it

            if (baseLit)
            {
                var vnm = cloneGO.GetComponent<VisualNoteMarker>(); if (vnm) vnm.Initialize(track.trackColor);
                var ml  = cloneGO.GetComponent<MarkerLight>();      if (ml)  ml.LightUp(track.trackColor);
            }
            else
            {
                var ml = cloneGO.GetComponent<MarkerLight>() ?? cloneGO.AddComponent<MarkerLight>();
                ml.SetGrey(new Color(1f,1f,1f,0.25f));
            }
        }

        _cloneMarkers[key] = t;
    }

    // 3) Destroy any clones no longer required (note removed OR repeats decreased)
    var toRemove = new List<(InstrumentTrack,int,int)>();
    foreach (var kv in _cloneMarkers)
    {
        var (t, localStep, k) = kv.Key;
        if (t != track) continue;

        bool stillNeeded = required.Contains(kv.Key);
        if (!stillNeeded)
        {
            var tr = kv.Value;
            if (tr) Destroy(tr.gameObject);
            toRemove.Add(kv.Key);
        }
    }
    foreach (var k in toRemove) _cloneMarkers.Remove(k);
}
private void UpdateClonePositions()
{
    if (_drumTrack == null || GameFlowManager.Instance?.controller == null) return;

    int drumSteps   = _drumTrack.totalSteps;
    int maxMult     = GameFlowManager.Instance.controller.GetMaxLoopMultiplier();
    int globalSteps = drumSteps * Mathf.Max(1, maxMult);

    // Precompute per-track row & steps
    foreach (var kv in _cloneMarkers)
    {
        var (track, localStep, k) = kv.Key;
        Transform t = kv.Value;
        if (!t) continue;

        int trackIndex = Array.IndexOf(GameFlowManager.Instance.controller.tracks, track);
        if (trackIndex < 0 || trackIndex >= trackRows.Count) continue;
        RectTransform row = trackRows[trackIndex];
        Rect rowRect = row.rect;

        int trackSteps = drumSteps * Mathf.Max(1, track.loopMultiplier);
        int globalStep = (localStep + k * trackSteps) % Mathf.Max(1, globalSteps);

        float u = globalSteps > 1 ? (globalStep / (float)globalSteps) : 0f;
        float xLocal = Mathf.Lerp(rowRect.xMin, rowRect.xMax, u);
        float yLocal = (rowRect.yMin + rowRect.yMax) * 0.5f;

        t.localPosition = new Vector3(xLocal, yLocal, 0f);
    }
}

    public void Initialize()
    {
        isInitialized = true;
        _uiParent = transform.parent;
        _worldSpaceCanvas = _uiParent.GetComponentInParent<Canvas>();
        foreach (RectTransform row in trackRows) {
            GameObject ribbonGO = new GameObject("TrackRibbon");
            ribbonGO.transform.SetParent(row, false);
            LineRenderer lr = ribbonGO.AddComponent<LineRenderer>();

            lr.material = ribbonMaterial;
            lr.widthMultiplier = ribbonWidth;
            lr.positionCount = ribbonResolution;
            lr.useWorldSpace = false;
            lr.numCapVertices = 4;
            lr.alignment = LineAlignment.TransformZ;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            lr.textureMode = LineTextureMode.Stretch;

            ribbons.Add(lr);
        }
        foreach (InstrumentTrack track in GameFlowManager.Instance.controller.tracks)
        {
            _waveAmplitudes[track] = 0; // Initial wavy state
        }
        foreach (var row in trackRows)
        {
            row.anchorMin = new Vector2(0f, row.anchorMin.y);
            row.anchorMax = new Vector2(1f, row.anchorMax.y);
            row.offsetMin = new Vector2(0f, row.offsetMin.y);
            row.offsetMax = new Vector2(0f, row.offsetMax.y);
        }
        _drumTrack = GameFlowManager.Instance.activeDrumTrack;
    }
// NoteVisualizer.cs
    public void MarkGhostPadding(InstrumentTrack track, int startStepInclusive, int count)
    {
        if (!_ghostNoteSteps.TryGetValue(track, out var set))
            _ghostNoteSteps[track] = set = new HashSet<int>();

        int total = Mathf.Max(1, GameFlowManager.Instance.controller.tracks.Max(t => t.GetTotalSteps()));
        for (int i = 0; i < count; i++)
            set.Add((startStepInclusive + i) % total);
    }

    public void ClearGhostPadding(InstrumentTrack track)
    {
        if (_ghostNoteSteps.ContainsKey(track))
            _ghostNoteSteps[track].Clear();
    }

    public float GetTopWorldY()
    {
        RectTransform rt = GetComponent<RectTransform>();
        Vector3[] worldCorners = new Vector3[4];
        rt.GetWorldCorners(worldCorners);
        return worldCorners[1].y; // Top-left corner
    }
    public Transform GetUIParent()
    {
        return _uiParent;
    }
    
    public Transform EnsureMarker(InstrumentTrack track, int step) {
    var key = (track, step);
    if (noteMarkers.TryGetValue(key, out var t) && t && t.gameObject) return t;
    noteMarkers.Remove(key); // purge stale
    var go = PlacePersistentNoteMarker(track, step);
    return go ? go.transform : null;
}

    public List<Vector3> GetRibbonWorldPositionsForSteps(InstrumentTrack track, List<int> steps) {
    var result = new List<Vector3>();
    if (_trackStepWorldPositions.TryGetValue(track, out var stepMap))
    {
        foreach (int step in steps)
        {
            if (stepMap.TryGetValue(step, out var pos))
                result.Add(pos);
        }
    }
    return result;
}
    public Vector3 ComputeRibbonWorldPosition(InstrumentTrack track, int stepIndex)
    {
        int trackIndex = System.Array.IndexOf(GameFlowManager.Instance.controller.tracks, track);
        if (trackIndex < 0 || trackIndex >= ribbons.Count) return transform.position;

        RectTransform row = trackRows[trackIndex];
        Rect rowRect = row.rect;

        int totalSteps = track.GetTotalSteps();
        float t = Mathf.Clamp01(stepIndex / (float)totalSteps);

        float yLocal = (rowRect.yMin + rowRect.yMax) * 0.5f;
        int   longestSteps = GameFlowManager.Instance.controller.tracks.Max(tk => tk.GetTotalSteps());
        float localFraction = totalSteps / (float)Mathf.Max(1, longestSteps);
        float xLocal = Mathf.Lerp(rowRect.xMin, rowRect.xMax, t * localFraction);

        Vector3 localPos = new Vector3(xLocal, yLocal, 0f);
        return row.TransformPoint(localPos);
    }
// NoteVisualizer.cs – REPLACE your EnsureTiledClones with this
public void EnsureTiledClones(InstrumentTrack track, int localStep, bool lit)
{
    int drumSteps   = _drumTrack.totalSteps;
    int maxMult     = GameFlowManager.Instance.controller.GetMaxLoopMultiplier();
    int myMult      = Mathf.Max(1, track.loopMultiplier);
    int trackSteps  = drumSteps * myMult;
    int globalSteps = drumSteps * maxMult;
    int repeats     = Mathf.Max(1, globalSteps / trackSteps);

    // nothing to tile if we are the longest
    if (repeats <= 1)
    {
        // clean up any past clones
        if (_tiledClones.TryGetValue((track, localStep), out var old))
        {
            foreach (var t in old) if (t) Destroy(t.gameObject);
            _tiledClones.Remove((track, localStep));
        }
        return;
    }

    if (!_tiledClones.TryGetValue((track, localStep), out var clones))
        clones = _tiledClones[(track, localStep)] = new List<Transform>();

    // we need (repeats - 1) clones for k = 1..repeats-1
    int desired = repeats - 1;

    // create or reuse
    int trackIndex = Array.IndexOf(GameFlowManager.Instance.controller.tracks, track);
    if (trackIndex < 0 || trackIndex >= trackRows.Count) return;
    RectTransform row = trackRows[trackIndex];

    // grow
    for (int i = clones.Count; i < desired; i++)
    {
        var go = Instantiate(notePrefab, Vector3.zero, Quaternion.identity, row);
        var t  = go.transform;

        if (lit)
        {
            var vnm = go.GetComponent<VisualNoteMarker>(); if (vnm) vnm.Initialize(track.trackColor);
            var ml  = go.GetComponent<MarkerLight>();      if (ml)  ml.LightUp(track.trackColor);
        }
        else
        {
            var ml = go.GetComponent<MarkerLight>() ?? go.AddComponent<MarkerLight>();
            ml.SetGrey(new Color(1f,1f,1f,0.25f));
        }
        clones.Add(t);
    }

    // shrink
    for (int i = clones.Count - 1; i >= desired; i--)
    {
        if (clones[i]) Destroy(clones[i].gameObject);
        clones.RemoveAt(i);
    }

    // initial placement now; ongoing placement handled in UpdateTiledClonePositions()
    UpdateTiledClonePositions(track);
}
// NoteVisualizer.cs – ADD this helper
private void UpdateTiledClonePositions(InstrumentTrack track)
{
    int drumSteps   = _drumTrack.totalSteps;
    int maxMult     = GameFlowManager.Instance.controller.GetMaxLoopMultiplier();
    int myMult      = Mathf.Max(1, track.loopMultiplier);
    int trackSteps  = drumSteps * myMult;
    int globalSteps = drumSteps * maxMult;

    int trackIndex = Array.IndexOf(GameFlowManager.Instance.controller.tracks, track);
    if (trackIndex < 0 || trackIndex >= trackRows.Count) return;
    RectTransform row = trackRows[trackIndex];
    Rect rowRect = row.rect;

    float yLocal = (rowRect.yMin + rowRect.yMax) * 0.5f;

    // For each registered (track, localStep), position its clones at localStep + k*trackSteps
    foreach (var kv in _tiledClones.ToArray())
    {
        if (kv.Key.Item1 != track) continue;

        int localStep = kv.Key.Item2;
        var clones    = kv.Value;

        for (int i = 0; i < clones.Count; i++)
        {
            var t = clones[i]; if (!t) continue;
            int k = i + 1; // clone index represents k=1..repeats-1
            int globalStep = (localStep + k * trackSteps) % Mathf.Max(1, globalSteps);

            // map globalStep (0..globalSteps-1) to full row width [xMin, xMax]
            float u = globalSteps > 1 ? (globalStep / (float)globalSteps) : 0f;
            float xLocal = Mathf.Lerp(rowRect.xMin, rowRect.xMax, u);

            t.localPosition = new Vector3(xLocal, yLocal, 0f);
        }
    }
}

    public GameObject PlacePersistentNoteMarker(InstrumentTrack track, int stepIndex, bool lit = true)
    {
        var key = (track, stepIndex);
        // If we have a stale/null Transform, purge it so we can recreate.
        if (noteMarkers.TryGetValue(key, out var existing))
        {
            if (existing == null || existing.gameObject == null)
            {
                noteMarkers.Remove(key);
            }
            else if (existing == null || existing.gameObject == null || !existing.gameObject.activeInHierarchy)
            {
                noteMarkers.Remove(key); // stale, recreate
            }
            else
            {
                return null;
            }
        }
        int trackIndex = Array.IndexOf(GameFlowManager.Instance.controller.tracks, track);
        if (trackIndex < 0 || trackIndex >= trackRows.Count) return null;

        // Compute local position in the row (not world)
        RectTransform row = trackRows[trackIndex];
        Rect rowRect = row.rect;

        int totalSteps = track.GetTotalSteps();
        float t = Mathf.Clamp01(stepIndex / (float)totalSteps);
        int   longestSteps = GameFlowManager.Instance.controller.tracks.Max(tk => tk.GetTotalSteps());
        float localFraction = totalSteps / (float)Mathf.Max(1, longestSteps); 
        float xLocal = Mathf.Lerp(rowRect.xMin, rowRect.xMax, t * localFraction);
        float yLocal = (rowRect.yMin + rowRect.yMax) * 0.5f;

        // Parent under the row and place in local space
        GameObject marker = Instantiate(notePrefab, Vector3.zero, Quaternion.identity, row);
        marker.transform.localPosition = new Vector3(xLocal, yLocal, 0f);
        noteMarkers[key] = marker.transform;
        if (lit)
        {
            var vnm = marker.GetComponent<VisualNoteMarker>();
            if (vnm != null) vnm.Initialize(track.trackColor);
            var light = marker.GetComponent<MarkerLight>();
            if (light != null) light.LightUp(track.trackColor);
        }
        return marker;
    }

    private void UpdateNoteMarkerPositions()
    {
        var deadKeys = new List<(InstrumentTrack, int)>();

        foreach (var kvp in noteMarkers)
        {
            InstrumentTrack track = kvp.Key.Item1;
            int localStep = kvp.Key.Item2;
            Transform marker = kvp.Value;

            if (marker == null) { deadKeys.Add(kvp.Key); continue; }

            if (!_trackStepWorldPositions.TryGetValue(track, out var map))
                continue;

            int trackIndex = Array.IndexOf(GameFlowManager.Instance.controller.tracks, track);
            if (trackIndex < 0 || trackIndex >= trackRows.Count) continue;

            RectTransform row = trackRows[trackIndex];

            // Convert the world point map back to the row's local space,
            // then assign localPosition so it never exceeds the row width.

            if (map.TryGetValue(localStep, out var worldPos))
            {
                marker.localPosition = row.InverseTransformPoint(worldPos);
            }
            
        }

        foreach (var key in deadKeys)
            noteMarkers.Remove(key);
    }
    public float GetWaveAmplitude(InstrumentTrack track)
{
    if (_waveAmplitudes.TryGetValue(track, out var amplitude))
        return amplitude;

    return 0f;
}
    public void SetWaveAmplitudeForTrack(InstrumentTrack track, float amplitude)
{
    if (_waveAmplitudes.ContainsKey(track))
    {
        _waveAmplitudes[track] = amplitude;
    }
}
    public void SetWaveSpeed(float speed)
{
    waveSpeed = Mathf.Clamp(speed, 2f, 32f);
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
            if (go != null)
            {
                result.Add(go);
            }
        }
        return result;
    }
    public void TriggerNoteRushToVehicle(InstrumentTrack track, Vehicle v)
{
    foreach (var marker in GetNoteMarkers(track))
    {
        StartCoroutine(RushToVehicle(marker, marker.transform.position, v));
    }
}
    public void TriggerNoteBlastOff(InstrumentTrack track)
{
    // 1) Capture all live marker GOs for this track BEFORE purging keys
    var gos = GetNoteMarkers(track); // uses noteMarkers; OK while keys still present

    // 2) Purge this track’s keys from the dictionary
    var keys = new List<(InstrumentTrack,int)>();
    foreach (var kvp in noteMarkers)
        if (kvp.Key.Item1 == track) keys.Add(kvp.Key);
    foreach (var k in keys) noteMarkers.Remove(k);

    // 3) Animate & destroy the captured GOs
    foreach (var go in gos)
        if (go) StartCoroutine(BlastOffAndDestroy(go));

    // 4) Safety: also nuke any stray row children that match the prefab type
    DestroyOrphanRowMarkers(track);
}
    public IEnumerator FadeRibbonsTo(float targetAlpha, float seconds)
{
    float t = 0f;
    // capture current alphas once
    var startColors = new List<Color>(ribbons.Count);
    for (int i = 0; i < ribbons.Count; i++)
        startColors.Add(ribbons[i].startColor);

    while (t < seconds)
    {
        t += Time.deltaTime;
        float u = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / seconds));
        for (int i = 0; i < ribbons.Count; i++)
        {
            var c = startColors[i];
            c.a = Mathf.Lerp(c.a, targetAlpha, u);
            ribbons[i].startColor = ribbons[i].endColor = c;
        }
        yield return null;
    }
}
    private void DestroyOrphanRowMarkers(InstrumentTrack track)
{
    int trackIndex = Array.IndexOf(GameFlowManager.Instance.controller.tracks, track);
    if (trackIndex < 0 || trackIndex >= trackRows.Count) return;

    var row = trackRows[trackIndex];
    var orphans = row.GetComponentsInChildren<VisualNoteMarker>(includeInactive: true);
    foreach (var vnm in orphans)
    {
        // If the dictionary no longer points at this Transform, it’s safe to remove
        bool referenced = false;
        foreach (var kvp in noteMarkers)
        {
            if (kvp.Key.Item1 != track) continue;
            if (kvp.Value == vnm.transform) { referenced = true; break; }
        }
        if (!referenced)
        {
            // optional: quick “pop” instead of silent destroy
            if (vnm.TryGetComponent<Explode>(out var explode)) explode.Permanent();
            else Destroy(vnm.gameObject);
        }
    }
}
    private IEnumerator BlastOffAndDestroy(GameObject marker)
{
    if (marker == null) yield break;
    Vector3 start = marker.transform.position;
    Vector3 offset = UnityEngine.Random.insideUnitSphere * 2f;
    Vector3 end = start + offset;
    float duration = 0.4f;
    float t = 0f;

    while (t < duration)
    {
        t += Time.deltaTime;
        float eased = Mathf.SmoothStep(0f, 1f, t / duration);
        if (marker != null)
        {
            marker.transform.position = Vector3.Lerp(start, end, eased);
            marker.transform.localScale = Vector3.Lerp(Vector3.one, Vector3.zero, eased);
        }

        yield return null;
    }

    if (marker == null) yield break;

    if (marker.TryGetComponent<Explode>(out var explode))
    {
        explode.Permanent();
    }
    else
    {
        Destroy(marker);
    }
    yield return null;
}
    private IEnumerator RushToVehicle(GameObject marker, Vector3 position, Vehicle target)
{
    float duration = 0.6f;
    float t = 0f;

    while (t < duration)
    {
        t += Time.deltaTime;
        float eased = Mathf.SmoothStep(0f, 1f, t / duration);
        position = Vector3.Lerp(position, target.transform.position, eased);
        yield return null;
    }

    if (marker != null)
    {
        Explode explode = marker.GetComponent<Explode>();
        if (explode != null)
        {
            explode.Permanent();
        }
        else
        {
            Destroy(marker);
        }
        
    }
}

    private float GetScreenWidth()
    {
        RectTransform rt = _worldSpaceCanvas.GetComponent<RectTransform>();
        return rt.rect.width;
    }
}
