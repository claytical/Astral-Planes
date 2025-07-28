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
    float drumLoopLength   = drums.GetLoopLengthInSeconds();
    int   drumTotalSteps   = drums.totalSteps;
    float stepDuration     = drumLoopLength / drumTotalSteps;
    float windowSeconds    = stepDuration * 2f;
    float drumElapsed      = (float)((AudioSettings.dspTime - drums.startDspTime) % drumLoopLength);
    
    int currentStep = Mathf.FloorToInt(drumElapsed / stepDuration) % drumTotalSteps; 
    bool shimmer = false;
    float maxVelocity = 0;

    foreach (var track in tracks.tracks) {
        float v = track.GetVelocityAtStep(currentStep);
        maxVelocity = Mathf.Max(maxVelocity, v / 127f);
        if (ghostNoteSteps.TryGetValue(track, out var steps) && steps.Contains(currentStep))
        {
            shimmer = true;
            break;
        }
    }
    
    // 🔹 Apply visual response to the particle system
    if (playheadParticles != null)
    {
        var main = playheadParticles.main;
        main.startSize = Mathf.Lerp(0.3f, 1.2f, maxVelocity);
        var emission = playheadParticles.emission;
        emission.rateOverTime = Mathf.Lerp(10f, 50f, maxVelocity);
        emission.enabled = shimmer;

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
        Vector3[] corners = new Vector3[4];
        row.GetWorldCorners(corners);
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

            if (ghostNoteSteps.TryGetValue(track, out var ghostSteps))
            {
                if (ghostSteps.Contains(trackStep))
                {
                    activeNoteVelocity = 0.3f; // or any fixed ghost visibility value
                }
            } 
           
//            float y = Mathf.Sin(Time.time * waveSpeed + j * velocityMultiplier) * waveAmplitude;
            float amplitude = waveAmplitudes.TryGetValue(track, out var amp) ? amp : 0f;
            float y = Mathf.Sin(Time.time * waveSpeed + j * velocityMultiplier) * amplitude;

            Vector3 localPos = new Vector3(x, y, 0);
            Vector3 worldPos = lr.transform.TransformPoint(localPos);
            lr.useWorldSpace = false;
            positions[j] = localPos;

            if (!stepMap.ContainsKey(trackStep))
                stepMap[globalStep] = worldPos; // ✅ now using global step
        }

        lr.SetPositions(positions);
// Relative density calc
        int maxNotes = tracks.tracks.Max(t => t.GetNoteDensity());
        int trackNotes = track.GetNoteDensity();
        if (trackNotes == 0)
        {
            trackNotes = 1;
        }
        float densityT = (maxNotes > 0) ? (float)trackNotes / maxNotes : 0f;
// Width scaling (pop well-worn track)
        float minWidth = 1f;
        float maxWidth = 3f;
        float targetWidth = Mathf.Lerp(minWidth, maxWidth, densityT);
        lr.widthMultiplier = targetWidth;
        ribbonWidths[track] = targetWidth;

// Color vibrancy scaling
        Color baseColor = track.trackColor;
        float vibrancy = Mathf.Lerp(0.5f, 1f, densityT); // Half-saturated to full
        Color scaledColor = baseColor * vibrancy;
        scaledColor.a = Mathf.Lerp(0.1f, 0.65f, densityT);
        lr.startColor = lr.endColor = scaledColor;
        ribbonIntensities[track] = vibrancy;


        trackStepWorldPositions[track] = stepMap;
    }
    UpdateNoteMarkerPositions();

    // 2) Compute how far we are into the *drum* loop
    double dspNow       = AudioSettings.dspTime;
    double sinceStart   = dspNow - drums.startDspTime;
    
    foreach (var track in tracks.tracks)
    {
        foreach (var collectableGO in track.spawnedCollectables)
        {
            if (collectableGO == null) continue;
            if (!collectableGO.TryGetComponent(out GhostNoteVine vine)) continue;
            vine.AnimateToNextStep(drumElapsed, stepDuration, windowSeconds);
        }
    }

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
    public Vector3 ComputeRibbonWorldPosition(InstrumentTrack track, int stepIndex)
    {
        int trackIndex = System.Array.IndexOf(tracks.tracks, track);
        if (trackIndex < 0 || trackIndex >= ribbons.Count) return transform.position;

        RectTransform row = trackRows[trackIndex];
        Vector3[] corners = new Vector3[4];
        row.GetWorldCorners(corners);
        Debug.Log($"Row WorldCorners: BL={corners[0]}, TL={corners[1]}, TR={corners[2]}, BR={corners[3]}");

        int totalSteps = track.GetTotalSteps();
        float t = stepIndex / (float)totalSteps;

        float x = t * row.rect.width;
        float y = row.rect.height * 0.5f; // 🧠 Use midpoint of the ribbon row

        Vector3 localPos = new Vector3(x, y, 0f);
        Vector3 worldPos = row.TransformPoint(localPos); // ⬅️ Converts to world space
        
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
        StartCoroutine(RushToVehicle(marker, v));
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

private IEnumerator RushToVehicle(GameObject marker, Vehicle target)
{
    Vector3 start = marker.transform.position;
    float duration = 0.6f;
    float t = 0f;

    while (t < duration)
    {
        t += Time.deltaTime;
        float eased = Mathf.SmoothStep(0f, 1f, t / duration);
        marker.transform.position = Vector3.Lerp(start, target.transform.position, eased);
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


private float GetScreenWidth()
    {
        RectTransform rt = worldSpaceCanvas.GetComponent<RectTransform>();
        return rt.rect.width;
    }
}
