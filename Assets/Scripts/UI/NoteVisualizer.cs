using System;
using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using UnityEngine.UI;

public class NoteVisualizer : MonoBehaviour
{
    public RectTransform playheadLine;
    public DrumTrack drums;
    public InstrumentTrackController tracks;

    private float screenWidth = 1920f;

    public ParticleSystem playheadParticles;

    [Header("Note Ribbon Settings")]
    public Material ribbonMaterial;
    public float ribbonWidth = 0.05f;
    public int ribbonResolution = 64;
    private List<LineRenderer> ribbons = new List<LineRenderer>();

    [Header("Ribbon Animation")]
    public float waveSpeed = 2f;
    public float waveFrequency = 10f;
    public float waveAmplitude = 0.05f;
    public float velocityMultiplier = 0.2f;

    public List<RectTransform> trackRows;
    private Canvas worldSpaceCanvas;

    private void Start()
    {
        worldSpaceCanvas = GetComponentInParent<Canvas>();
        screenWidth = GetScreenWidth();

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
    float xPos = globalNormalized * screenWidth;
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
        main.startSize = Mathf.Lerp(0.2f, 1.2f, maxVelocity);

        var emission = playheadParticles.emission;
        emission.rateOverTime = Mathf.Lerp(5f, 50f, maxVelocity);

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

    // 🔹 Animate the ribbons
    for (int i = 0; i < ribbons.Count; i++)
    {
        LineRenderer lr = ribbons[i];
        RectTransform row = trackRows[i];
        InstrumentTrack track = tracks.tracks[i];

        Vector3[] positions = new Vector3[ribbonResolution];
        float width = row.rect.width;

        int trackSteps = track.GetTotalSteps();
        int repeats = Mathf.Max(1, longestLoopSteps / trackSteps);
        var loopNotes = track.GetPersistentLoopNotes();

        for (int j = 0; j < ribbonResolution; j++)
        {
            float t = j / (float)(ribbonResolution - 1); // 0 to 1
            float x = t * width;
            float y = 0f;

            int visualStep = Mathf.FloorToInt(t * longestLoopSteps);
            float activeNoteVelocity = 0f;

            for (int r = 0; r < repeats; r++)
            {
                int localStep = visualStep - r * trackSteps;
                if (localStep < 0) continue;

                foreach (var (noteStep, _, _, velocity) in loopNotes)
                {
                    if (noteStep == localStep)
                    {
                        activeNoteVelocity = velocity / 127f;
                        break;
                    }
                }

                if (activeNoteVelocity > 0f) break;
            }

            float baseWave = Mathf.Sin(Time.time * waveSpeed + t * waveFrequency) * waveAmplitude;
            y = baseWave + activeNoteVelocity * velocityMultiplier;

            positions[j] = new Vector3(x, y, 0);
        }

        lr.SetPositions(positions);

        Color faded = track.trackColor;
        faded.a = 0.6f;
        lr.startColor = lr.endColor = faded;
    }
}

    private float GetScreenWidth()
    {
        RectTransform rt = worldSpaceCanvas.GetComponent<RectTransform>();
        return rt.rect.width;
    }
}
