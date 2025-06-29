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
    
    public ParticleSystem playheadParticles;

    [Header("Note Ribbon Settings")]
    public Material ribbonMaterial;
    public float ribbonWidth = 0.05f;
    public int ribbonResolution = 64;
    private List<LineRenderer> ribbons = new List<LineRenderer>();
    public GameObject notePrefab;

    [Header("Ribbon Animation")]
    public float waveSpeed = 2f;
    public float waveFrequency = 10f;
    public float waveAmplitude = 0.05f;
    public float velocityMultiplier = 0.2f;

    public List<RectTransform> trackRows;
    private Canvas worldSpaceCanvas;
    private Transform uiParent;
    private Dictionary<InstrumentTrack, Dictionary<int, Vector3>> trackStepWorldPositions = new();
    private Dictionary<(InstrumentTrack, int), Transform> noteMarkers = new();

    private void Start()
    {
        uiParent = transform.parent;
        worldSpaceCanvas = GetComponentInParent<Canvas>();

        foreach (RectTransform row in trackRows)
        {
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
            waveAmplitudes[track] = 0.05f; // Initial wavy state
        }
    }
    public Vector3 GetWorldPositionForNote(InstrumentTrack track, int stepIndex)
    {
        if (trackStepWorldPositions.TryGetValue(track, out var map))
        {
            if (map.TryGetValue(stepIndex, out var pos))
            {
                return pos;
            }
        }

        return transform.position; // fallback
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

    float globalElapsed = (float)(AudioSettings.dspTime - drums.startDspTime);
    float baseLoopLength = drums.GetLoopLengthInSeconds();
    int globalLoopMultiplier = tracks.GetMaxLoopMultiplier();
    float fullVisualLoopDuration = baseLoopLength * globalLoopMultiplier;

    float globalNormalized = (globalElapsed % fullVisualLoopDuration) / fullVisualLoopDuration;
    float xPos = globalNormalized * GetScreenWidth();
    playheadLine.anchoredPosition = new Vector2(xPos, playheadLine.anchoredPosition.y);

    int longestLoopSteps = tracks.tracks.Max(track => track.GetTotalSteps());

    // 🔹 Determine current step index
    float stepDuration = fullVisualLoopDuration / longestLoopSteps;
    int currentStep = Mathf.FloorToInt((globalElapsed % fullVisualLoopDuration) / stepDuration);

    // 🔹 Find max velocity at this step across all tracks
    float maxVelocity = 0f;
    foreach (var track in tracks.tracks)
    {
        float v = track.GetVelocityAtStep(currentStep);
        maxVelocity = Mathf.Max(maxVelocity, v / 127f);
    }

    // 🔹 Apply visual response to the particle system
    if (playheadParticles != null)
    {
        var main = playheadParticles.main;
        main.startSize = Mathf.Lerp(0.3f, 1.2f, maxVelocity);

        var emission = playheadParticles.emission;
        emission.rateOverTime = Mathf.Lerp(10f, 50f, maxVelocity);

        var colorOverLifetime = playheadParticles.colorOverLifetime;
        if (colorOverLifetime.enabled)
        {
            Gradient grad = new Gradient();
            grad.SetKeys(
                new[] {
                    new GradientColorKey(Color.white, 0.0f),
                    new GradientColorKey(Color.cyan, 1.0f)
                },
                new[] {
                    new GradientAlphaKey(0.4f + maxVelocity * 0.5f, 0.0f),
                    new GradientAlphaKey(0.1f, 1.0f)
                }
            );
            colorOverLifetime.color = grad;
        }
    }

    // 🔹 Animate the ribbons with correct time alignment
    for (int i = 0; i < ribbons.Count; i++)
    {
        LineRenderer lr = ribbons[i];
        RectTransform row = trackRows[i];
        InstrumentTrack track = tracks.tracks[i];

        Vector3[] positions = new Vector3[ribbonResolution];
        float width = row.rect.width;
        int trackSteps = track.GetTotalSteps();

        // NEW: Step position cache
        Dictionary<int, Vector3> stepMap = new Dictionary<int, Vector3>();

        for (int j = 0; j < ribbonResolution; j++)
        {
            float tGlobal = j / (float)(ribbonResolution); // normalized 0–1 across longest loop
            int globalStep = Mathf.FloorToInt(tGlobal * longestLoopSteps);
            int trackStep = Mathf.FloorToInt((globalStep / (float)longestLoopSteps) * trackSteps);

            float x = tGlobal * width;
            float activeNoteVelocity = 0f;

            foreach (var (noteStep, _, _, velocity) in track.GetPersistentLoopNotes())
            {
                if (noteStep == trackStep)
                {
                    activeNoteVelocity = velocity / 127f;
                    break;
                }
            }

            float y = activeNoteVelocity * velocityMultiplier;

            Vector3 localPos = new Vector3(x, y, 0);
            Vector3 worldPos = lr.transform.TransformPoint(localPos);
            positions[j] = localPos;

            if (!stepMap.ContainsKey(trackStep))
                stepMap[globalStep] = worldPos; // ✅ now using global step
            
        }

        lr.SetPositions(positions);

        Color faded = track.trackColor;
        faded.a = 0.1f;
        lr.startColor = lr.endColor = faded;

        trackStepWorldPositions[track] = stepMap;
    }
    UpdateNoteMarkerPositions();
}

public Vector3 ComputeRibbonWorldPosition(InstrumentTrack track, int stepIndex)
{
    int trackIndex = System.Array.IndexOf(tracks.tracks, track);
    if (trackIndex < 0 || trackIndex >= ribbons.Count) return transform.position;

    RectTransform row = trackRows[trackIndex];
    LineRenderer lr = ribbons[trackIndex];

    int totalSteps = track.GetTotalSteps();
    float t = stepIndex / (float)totalSteps;

    float x = t * row.rect.width;
    float velocity = track.GetVelocityAtStep(stepIndex) / 127f;
    float y = velocity * velocityMultiplier;

    Vector3 localPos = new Vector3(x, y, 0f);

    // ✅ Convert local UI-space to world-space using RectTransformUtility
    Vector3 worldPos = row.TransformPoint(localPos);
    return worldPos;
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

public GameObject PlacePersistentNoteMarker(InstrumentTrack track, int stepIndex)
{
    var key = (track, stepIndex);
    if (noteMarkers.ContainsKey(key)) return null;

    if (!trackStepWorldPositions.TryGetValue(track, out var map)) return null;

    // Convert local step to global step index
    int globalSteps = tracks.tracks.Max(t => t.GetTotalSteps());
    int trackSteps = track.GetTotalSteps();
    int globalStep = Mathf.FloorToInt((stepIndex / (float)trackSteps) * globalSteps);

    if (!map.TryGetValue(globalStep, out var worldPos)) return null;

    GameObject marker = Instantiate(notePrefab, worldPos, Quaternion.identity, transform);
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

        // Null check to avoid the MissingReferenceException
        if (marker == null)
        {
            deadKeys.Add(kvp.Key); // Mark for cleanup
            continue;
        }

        if (!trackStepWorldPositions.TryGetValue(track, out var map))
            continue;

        int trackSteps = track.GetTotalSteps();
        int globalStep = Mathf.FloorToInt((localStep / (float)trackSteps) * globalSteps);

        if (map.TryGetValue(globalStep, out var worldPos))
        {
            marker.position = worldPos;
        }
    }

    // Cleanup dead entries
    foreach (var key in deadKeys)
    {
        noteMarkers.Remove(key);
    }
}


public void TriggerNoteRushToVehicle(InstrumentTrack track, Vector3 vehiclePos)
{
    foreach (var marker in GetNoteMarkers(track))
    {
        StartCoroutine(RushToVehicle(marker, vehiclePos));
    }
}
public void TriggerNoteBlastOff(InstrumentTrack track)
{
    foreach (var marker in GetNoteMarkers(track))
    {
        if (marker == null) continue;
        StartCoroutine(BlastOffAndDestroy(marker));
    }
}

private IEnumerator BlastOffAndDestroy(GameObject marker)
{
    Vector3 start = marker.transform.position;
    Vector3 offset = UnityEngine.Random.insideUnitSphere * 2f;
    Vector3 end = start + offset;
    float duration = 0.4f;
    float t = 0f;

    while (t < duration)
    {
        t += Time.deltaTime;
        float eased = Mathf.SmoothStep(0f, 1f, t / duration);
        marker.transform.position = Vector3.Lerp(start, end, eased);
        marker.transform.localScale = Vector3.Lerp(Vector3.one, Vector3.zero, eased);
        yield return null;
    }
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

private IEnumerator RushToVehicle(GameObject marker, Vector3 target)
{
    Vector3 start = marker.transform.position;
    float duration = 0.6f;
    float t = 0f;

    while (t < duration)
    {
        t += Time.deltaTime;
        float eased = Mathf.SmoothStep(0f, 1f, t / duration);
        marker.transform.position = Vector3.Lerp(start, target, eased);
        yield return null;
    }
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

private float GetDriftoneWaveAmplitude(InstrumentTrack track)
{
    // Default low wave for settled state
    float baseAmp = 0.015f;

    if (track.isDriftoneActive && !track.isDriftoneLocked)
    {
        // Wavy during Driftone
        return 0.05f;
    }

    if (track.isDriftoneLocked)
    {
        if (track.wasLockedByPlayer)
        {
            // Pop to flat
            return 0f;
        }
        else
        {
            // Fade down gradually
            float current = Mathf.Clamp(waveAmplitudes[track], 0f, 0.05f);
            float newVal = Mathf.Lerp(current, baseAmp, Time.deltaTime * 1.5f);
            waveAmplitudes[track] = newVal;
            return newVal;
        }
    }

    return baseAmp;
}


    private float GetScreenWidth()
    {
        RectTransform rt = worldSpaceCanvas.GetComponent<RectTransform>();
        return rt.rect.width;
    }
}
