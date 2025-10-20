using System.Collections.Generic;
using UnityEngine;

public class VehicleRemixController : MonoBehaviour
{
    public Vehicle vehicle;
    public InstrumentTrackController trackController;
    public NoteVisualizer visualizer;

    // 🎨 Color Cycling
    private float _colorCycleTimer = 0f;
    private float _colorCycleRate = 0.5f;
    private int _colorCycleIndex = 0;
    private List<Color> _collectedColors = new();

    // 🔁 Remix State
    private HashSet<MusicalRole> _collectedRemixRoles = new();
    private Dictionary<MusicalRole, MinedObjectSpawnDirective> _remixDirectives = new();

    // ⚡ Boost Timing
    private float _boostTimeThisLoop = 0f;
    private readonly float _requiredBoostTime = 1.5f;
    private bool _remixPrimed = false;

    // 🌊 Visual Feedback
    private readonly float _maxWaveSpeed = 3f;
    private readonly float _waveSpeedLerpUp = 2f;
    private readonly float _amplitudeLerpUp = 3f;
    private readonly float _remixPrimingThreshold = 0.9f;

    private void Start()
    {
        vehicle = GetComponent<Vehicle>();
        trackController = GameFlowManager.Instance.controller;
        visualizer = GameFlowManager.Instance.controller.noteVisualizer;
    }
    public void FixedUpdateBoosting(float deltaTime, int currentStep)
    {
        if (!HasRemixRoles()) return;

        
        // Color cycling visuals
        _colorCycleTimer += deltaTime;
        if (_colorCycleTimer >= _colorCycleRate && _collectedColors.Count > 1)
        {
            _colorCycleTimer = 0f;
            _colorCycleIndex = (_colorCycleIndex + 1) % _collectedColors.Count;
            Color c1 = _collectedColors[_colorCycleIndex];
            Color c2 = _collectedColors[(_colorCycleIndex + 1) % _collectedColors.Count];

            vehicle.remixRingHolder.SetColor(c1);
            vehicle.SetColor(c2);
        }
        _boostTimeThisLoop += deltaTime;
    }
    public bool EvaluateRemixCondition()
    {
        if (!HasRemixRoles()) return false;

        if (_boostTimeThisLoop >= _requiredBoostTime)
        {
            TriggerRemixBlast();
            vehicle.remixRingHolder.ClearAllRings();
            ResetRemixVisuals();
            return true;
        }

        _remixPrimed = false;
        _boostTimeThisLoop = 0f;
        return false;
    }

    public bool HasRemixRoles() => _collectedRemixRoles.Count > 0;
    public void AddRemixRole(MusicalRole role, Color color, MinedObjectSpawnDirective _directive)
    {
        if (_collectedRemixRoles.Contains(role)) return;

        _collectedRemixRoles.Add(role);
        _remixDirectives[role] = _directive;
        _collectedColors.Add(color);

        vehicle.remixRingHolder?.ActivateRing(role, color);
    }
    public void ResetRemixVisuals()
    {
        vehicle.remixRingHolder?.SetColor(Color.white);
        vehicle.SetColor(vehicle.profileColor); // Restore base color
        _colorCycleIndex = 0;
        _colorCycleTimer = 0f;
    }

    private void TriggerRemixBlast()
    {
        Debug.Log($"Triggering Remix Blast");
        foreach (var role in _collectedRemixRoles)
        {
            if (_remixDirectives.TryGetValue(role, out var directive))
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
        
        _remixPrimed = false;
        _colorCycleTimer = 0f;
        _colorCycleIndex = 0;
        _collectedRemixRoles.Clear();
        _collectedColors.Clear();
    }

}
