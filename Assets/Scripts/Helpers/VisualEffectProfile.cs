using Gameplay.Mining;
using UnityEngine;

[CreateAssetMenu(menuName = "Astral Planes/Visual Effect Profile")]
public class VisualEffectProfile : ScriptableObject
{
    [Header("Role-Based (for NoteSpawners)")]
    public MusicalRole role;

    [Header("Utility-Based (for TrackUtility)")]
    public TrackModifierType utilityType;

    [Header("Visual Settings")]

    public Color glowColor = Color.white;
    public float rotationSpeed = 10f;
    public float pulseSpeed = 2f;
    public float pulseScaleAmount = 1.05f;
    public GameObject particlePrefab;
}