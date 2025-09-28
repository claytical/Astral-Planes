using System;
using System.Collections.Generic;
using Steamworks;
using UnityEngine;

public class VehicleRemixController : MonoBehaviour
{
    public Vehicle vehicle;
    public InstrumentTrackController trackController;
    public NoteVisualizer visualizer;

    // 🎨 Color Cycling
    private float colorCycleTimer = 0f;
    private float colorCycleRate = 0.5f;
    private int colorCycleIndex = 0;
    private List<Color> collectedColors = new();

    // 🔁 Remix State
    private HashSet<MusicalRole> collectedRemixRoles = new();
    private Dictionary<MusicalRole, MinedObjectSpawnDirective> remixDirectives = new();

    // ⚡ Boost Timing
    private float boostTimeThisLoop = 0f;
    private float requiredBoostTime = 1.5f;
    private bool remixPrimed = false;

    // 🌊 Visual Feedback
    private float remixWaveSpeed = 0f;
    private float maxWaveSpeed = 3f;
    private float waveSpeedLerpUp = 2f;
    private float amplitudeLerpUp = 3f;
    private float remixPrimingThreshold = 0.9f;

    private void Start()
    {
        vehicle = GetComponent<Vehicle>();
        trackController = GameFlowManager.Instance.activeDrumTrack.trackController;
        visualizer = GameFlowManager.Instance.activeDrumTrack.trackController.noteVisualizer;
    }

    public bool HasRemixRoles() => collectedRemixRoles.Count > 0;

    public void AddRemixRole(MusicalRole role, Color color, MinedObjectSpawnDirective _directive)
    {
        if (collectedRemixRoles.Contains(role)) return;

        collectedRemixRoles.Add(role);
        remixDirectives[role] = _directive;
        collectedColors.Add(color);

        vehicle.remixRingHolder?.ActivateRing(role, color);
    }

    public void FixedUpdateBoosting(float deltaTime, int currentStep)
    {
        if (!HasRemixRoles()) return;

        AnimateRemixWaveFeedback(deltaTime);

        // Color cycling visuals
        colorCycleTimer += deltaTime;
        if (colorCycleTimer >= colorCycleRate && collectedColors.Count > 1)
        {
            colorCycleTimer = 0f;
            colorCycleIndex = (colorCycleIndex + 1) % collectedColors.Count;
            Color c1 = collectedColors[colorCycleIndex];
            Color c2 = collectedColors[(colorCycleIndex + 1) % collectedColors.Count];

            vehicle.remixRingHolder.SetColor(c1);
            vehicle.SetColor(c2);
        }
        boostTimeThisLoop += deltaTime;
    }
    public void ResetRemixVisuals()
    {
        vehicle.remixRingHolder?.SetColor(Color.white);
        vehicle.SetColor(vehicle.profileColor); // Restore base color
        colorCycleIndex = 0;
        colorCycleTimer = 0f;
    }

    public void AnimateRemixWaveFeedback(float deltaTime)
    {
        float chargeRatio = Mathf.Clamp01(boostTimeThisLoop / requiredBoostTime);
        float smoothedSpeed = Mathf.Lerp(visualizer.waveSpeed, maxWaveSpeed, deltaTime * waveSpeedLerpUp);
        visualizer.SetWaveSpeed(smoothedSpeed);

        foreach (var role in collectedRemixRoles)
        {
            InstrumentTrack track = trackController.FindTrackByRole(role);
            float current = visualizer.GetWaveAmplitude(track);
            float fullAmp = Mathf.Pow(2f, collectedRemixRoles.Count);
            float target = Mathf.Lerp(0f, fullAmp, chargeRatio);
            float smoothed = Mathf.Lerp(current, target, deltaTime * amplitudeLerpUp);
            visualizer.SetWaveAmplitudeForTrack(track, smoothed);
        }

        if (!remixPrimed && chargeRatio >= remixPrimingThreshold)
        {
            remixPrimed = true;
            // Optional: Add visual or audio feedback here
        }
    }

    public bool EvaluateRemixCondition()
    {
        if (!HasRemixRoles()) return false;

        if (boostTimeThisLoop >= requiredBoostTime)
        {
            TriggerRemixBlast();
            vehicle.remixRingHolder.ClearAllRings();
            ResetRemixVisuals();
            return true;
        }

        remixPrimed = false;
        boostTimeThisLoop = 0f;
        remixWaveSpeed = 0f;
        return false;
    }
    
    public void TriggerRemixBlast()
    {
        Debug.Log($"Triggering Remix Blast");
        foreach (var role in collectedRemixRoles)
        {
            if (remixDirectives.TryGetValue(role, out var directive))
            {
                Debug.Log($"Remix Directive Out: {directive}");
                var track = trackController.FindTrackByRole(role);
                Debug.Log($"Track: {track}");

                if (track != null && directive?.remixUtility != null && directive.noteSet != null)
                {
                    Debug.Log($"Track: {track.name} Started with Directive Note Set: {directive.noteSet}");
                    track.SetNoteSet(directive.noteSet);
                    track.PerformSmartNoteModification(vehicle.transform.position);
                }
            }
        }

        visualizer.SetWaveSpeed(0f);
        foreach (var role in collectedRemixRoles)
        {
            var track = trackController.FindTrackByRole(role);
            visualizer.SetWaveAmplitudeForTrack(track, 0f);
        }

        remixPrimed = false;
        colorCycleTimer = 0f;
        colorCycleIndex = 0;
        collectedRemixRoles.Clear();
        collectedColors.Clear();
    }
}
