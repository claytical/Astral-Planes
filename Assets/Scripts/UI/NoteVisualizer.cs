using System;
using System.Collections;
using UnityEngine;
using System.Collections.Generic;
using System.Linq;

public class NoteVisualizer : MonoBehaviour
{
    public RectTransform playheadLine;
    public DrumTrack drums;
    public InstrumentTrackController tracks;
    private Dictionary<InstrumentTrack, float> waveAmplitudes = new();
    private Dictionary<InstrumentTrack, HashSet<int>> ghostNoteSteps = new();

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
    private Canvas worldSpaceCanvas;
    private Transform uiParent;
    private Dictionary<InstrumentTrack, Dictionary<int, Vector3>> trackStepWorldPositions = new();
    public Dictionary<(InstrumentTrack, int), Transform> noteMarkers = new();
    private Dictionary<InstrumentTrack, float> ribbonWidths = new();
    private Dictionary<InstrumentTrack, float> ribbonIntensities = new();
    private Vehicle energyTransferVehicle;
    private void Start()
    {
        uiParent = transform.parent;
        worldSpaceCanvas = uiParent.GetComponentInParent<Canvas>();
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
        foreach (InstrumentTrack track in tracks.tracks)
        {
            waveAmplitudes[track] = 0; // Initial wavy state
        }
        foreach (var row in trackRows)
        {
            row.anchorMin = new Vector2(0f, row.anchorMin.y);
            row.anchorMax = new Vector2(1f, row.anchorMax.y);
            row.offsetMin = new Vector2(0f, row.offsetMin.y);
            row.offsetMax = new Vector2(0f, row.offsetMax.y);
        }
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
        return uiParent;
    }

  void Update()
{
    if (playheadLine == null || drums == null || tracks == null)
        return;

    // --- Playhead (same logic, just safer clamps) ---
    float globalElapsed = (float)(AudioSettings.dspTime - drums.startDspTime);
    float baseLoopLength = drums.GetLoopLengthInSeconds();
    int globalLoopMultiplier = tracks.GetMaxLoopMultiplier();
    float fullVisualLoopDuration = Mathf.Max(0.0001f, baseLoopLength * Mathf.Max(1, globalLoopMultiplier));
    float globalNormalized = (globalElapsed % fullVisualLoopDuration) / fullVisualLoopDuration;

    float canvasWidth = GetScreenWidth();
    float xPos = Mathf.Lerp(0f, canvasWidth, Mathf.Clamp01(globalNormalized));
    playheadLine.anchoredPosition = new Vector2(xPos, playheadLine.anchoredPosition.y);

    // --- Drum timing / velocity shimmer ---
    int   drumTotalSteps = drums.totalSteps;
    float drumLoopLength = drums.GetLoopLengthInSeconds();
    float stepDuration   = Mathf.Max(0.0001f, drumLoopLength / Mathf.Max(1, drumTotalSteps));
    float drumElapsed    = (float)((AudioSettings.dspTime - drums.startDspTime) % drumLoopLength);

    int currentStep = Mathf.FloorToInt(drumElapsed / stepDuration) % Mathf.Max(1, drumTotalSteps);

    bool shimmer = false; float maxVelocity = 0f;
    foreach (var track in tracks.tracks)
    {
        float v = track.GetVelocityAtStep(currentStep);
        maxVelocity = Mathf.Max(maxVelocity, v / 127f);
        if (ghostNoteSteps.TryGetValue(track, out var steps) && steps.Contains(currentStep))
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
    int longestLoopSteps = tracks.tracks.Max(t => t.GetTotalSteps());

    for (int i = 0; i < ribbons.Count; i++)
    {
        LineRenderer   lr   = ribbons[i];
        RectTransform  row  = trackRows[i];
        InstrumentTrack track = tracks.tracks[i];

        Rect rowRect  = row.rect;
        float xLeft   = rowRect.xMin;
        float xRight  = rowRect.xMax;
        float yMid    = (rowRect.yMin + rowRect.yMax) * 0.5f;

        float amplitude = waveAmplitudes.TryGetValue(track, out var amp) ? amp : 0f;

        // local-space positions for the LR
        int res = Mathf.Max(2, ribbonResolution);
        Vector3[] localPositions = new Vector3[res];

        // world-space lookup for markers/tethers (keyed by GLOBAL step)
        Dictionary<int, Vector3> stepMap = new Dictionary<int, Vector3>();

        for (int j = 0; j < res; j++)
        {
            float t01        = j / (float)(res - 1);                    // 0..1 across the row width
            int   globalStep = Mathf.FloorToInt(t01 * longestLoopSteps);

            float xLocal = Mathf.Lerp(xLeft, xRight, t01);

            // vertical wave in local Y (kept! no overwrite)
            float yOffset = Mathf.Sin(Time.time * waveSpeed + j * velocityMultiplier) * amplitude;
            float yLocal  = yMid + yOffset;

            Vector3 localPos = new Vector3(xLocal, yLocal, 0f);
            localPositions[j] = localPos;

            // store a precise WORLD position for this global step
            if (!stepMap.ContainsKey(globalStep))
                stepMap[globalStep] = row.TransformPoint(localPos);
        }

        lr.positionCount = res;
        lr.SetPositions(localPositions); // useWorldSpace = false

        // width/color by density
        int maxNotes   = tracks.tracks.Max(t => t.GetNoteDensity());
        int trackNotes = Mathf.Max(1, track.GetNoteDensity());
        float densityT = (maxNotes > 0) ? (float)trackNotes / maxNotes : 0f;

        float minWidth = 1f, maxWidth = 3f;
        float targetWidth = Mathf.Lerp(minWidth, maxWidth, densityT);
        lr.widthMultiplier = targetWidth;
        ribbonWidths[track] = targetWidth;

        Color baseColor = track.trackColor;
        float vibrancy  = Mathf.Lerp(0.5f, 1f, densityT);
        Color scaled    = baseColor * vibrancy; scaled.a = Mathf.Lerp(0.1f, 0.65f, densityT);
        lr.startColor = lr.endColor = scaled;
        ribbonIntensities[track] = vibrancy;

        // publish step map
        trackStepWorldPositions[track] = stepMap;
    }

    // keep marker sync + vine anim
    UpdateNoteMarkerPositions();

    float windowSeconds = stepDuration * 2f;
    float dElapsed      = drumElapsed;
    foreach (var track in tracks.tracks)
    {
        foreach (var go in track.spawnedCollectables)
        {
            if (go == null) continue;
            if (!go.TryGetComponent(out GhostNoteVine vine)) continue;
            vine.AnimateToNextStep(dElapsed, stepDuration, windowSeconds);
        }
    }
}
public Transform EnsureMarker(InstrumentTrack track, int step)
{
    var go = PlacePersistentNoteMarker(track, step);
    if (go != null) return go.transform;
    return noteMarkers.TryGetValue((track, step), out var t) ? t : null;
}
    public List<Vector3> GetRibbonWorldPositionsForSteps(InstrumentTrack track, List<int> steps)
{
    var result = new List<Vector3>();
    if (trackStepWorldPositions.TryGetValue(track, out var stepMap))
    {
        foreach (int step in steps)
        {
            if (stepMap.TryGetValue(step, out var pos))
                result.Add(pos);
        }
    }
    return result;
}
// NoteVisualizer.cs
    public Vector3 ComputeRibbonWorldPosition(InstrumentTrack track, int stepIndex)
    {
        int trackIndex = System.Array.IndexOf(tracks.tracks, track);
        if (trackIndex < 0 || trackIndex >= ribbons.Count) return transform.position;

        RectTransform row = trackRows[trackIndex];
        Rect rowRect = row.rect;

        int totalSteps = track.GetTotalSteps();
        float t = Mathf.Clamp01(stepIndex / (float)totalSteps);

        float xLocal = Mathf.Lerp(rowRect.xMin, rowRect.xMax, t);
        float yLocal = (rowRect.yMin + rowRect.yMax) * 0.5f;

        Vector3 localPos = new Vector3(xLocal, yLocal, 0f);
        return row.TransformPoint(localPos);
    }

public Vector3 GetNoteMarkerPosition(InstrumentTrack track, int stepIndex)
{
    if (noteMarkers.TryGetValue((track, stepIndex), out var markerTransform))
    {
        return markerTransform.position;
    }

    // fallback
    return ComputeRibbonWorldPosition(track, stepIndex);
}

// NoteVisualizer.cs
    public GameObject PlacePersistentNoteMarker(InstrumentTrack track, int stepIndex)
    {
        var key = (track, stepIndex);
        if (noteMarkers.ContainsKey(key)) return null;

        int trackIndex = Array.IndexOf(tracks.tracks, track);
        if (trackIndex < 0 || trackIndex >= trackRows.Count) return null;

        // Compute local position in the row (not world)
        RectTransform row = trackRows[trackIndex];
        Rect rowRect = row.rect;

        int totalSteps = track.GetTotalSteps();
        float t = Mathf.Clamp01(stepIndex / (float)totalSteps);
        float xLocal = Mathf.Lerp(rowRect.xMin, rowRect.xMax, t);
        float yLocal = (rowRect.yMin + rowRect.yMax) * 0.5f;

        // Parent under the row and place in local space
        GameObject marker = Instantiate(notePrefab, Vector3.zero, Quaternion.identity, row);
        marker.transform.localPosition = new Vector3(xLocal, yLocal, 0f);

        noteMarkers[key] = marker.transform;
        return marker;
    }

    private void UpdateNoteMarkerPositions()
    {
        int globalSteps = tracks.tracks.Max(t => t.GetTotalSteps());
        var deadKeys = new List<(InstrumentTrack, int)>();

        foreach (var kvp in noteMarkers)
        {
            InstrumentTrack track = kvp.Key.Item1;
            int localStep = kvp.Key.Item2;
            Transform marker = kvp.Value;

            if (marker == null) { deadKeys.Add(kvp.Key); continue; }

            if (!trackStepWorldPositions.TryGetValue(track, out var map))
                continue;

            int trackIndex = Array.IndexOf(tracks.tracks, track);
            if (trackIndex < 0 || trackIndex >= trackRows.Count) continue;

            RectTransform row = trackRows[trackIndex];

            // Convert the world point map back to the row's local space,
            // then assign localPosition so it never exceeds the row width.
            int trackSteps = track.GetTotalSteps();
            int globalStep = Mathf.FloorToInt((localStep / (float)trackSteps) * globalSteps);

            if (map.TryGetValue(globalStep, out var worldPos))
            {
                Vector3 localPos = row.InverseTransformPoint(worldPos);
                marker.localPosition = localPos;
            }
        }

        foreach (var key in deadKeys)
            noteMarkers.Remove(key);
    }

public float GetWaveAmplitude(InstrumentTrack track)
{
    if (waveAmplitudes.TryGetValue(track, out var amplitude))
        return amplitude;

    return 0f;
}

public void SetWaveAmplitudeForTrack(InstrumentTrack track, float amplitude)
{
    if (waveAmplitudes.ContainsKey(track))
    {
        waveAmplitudes[track] = amplitude;
    }
}
public void SetWaveSpeed(float speed)
{
    waveSpeed = Mathf.Clamp(speed, 2f, 32f);
}
public void ResetWaves()
{
    foreach (var track in tracks.tracks)
        waveAmplitudes[track] = 0f;
        waveSpeed = 2f;
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
    Debug.Log($"Triggered note blast off");
    foreach (var marker in GetNoteMarkers(track))
    {
        if (marker == null) continue;
        Debug.Log($"Blasting off {marker.name}");
        StartCoroutine(BlastOffAndDestroy(marker));
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

public List<GameObject> GetNoteMarkers(InstrumentTrack track)
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
        RectTransform rt = worldSpaceCanvas.GetComponent<RectTransform>();
        return rt.rect.width;
    }
}
