using System.Collections.Generic;
using UnityEngine;
public enum MusicalRole
{
    Bass,
    Harmony,
    Lead,
    Groove
}

[CreateAssetMenu(menuName = "Astral Planes/Musical Role Profile", fileName = "NewMusicalRoleProfile")]
public class MusicalRoleProfile : ScriptableObject
{
    public MusicalRole role;

    [Header("Visuals & Styling")]
    public Color defaultColor = Color.white;
    [TextArea]
    public string description;
    public GameObject envelopeParticlePrefab; // for PlayEnvelopePulse()
    public GameObject sparkParticlePrefab;    // for ghost path sparks

    [Header("Behavior & Presets")]
    public NoteBehavior defaultBehavior;
    public List<int> allowedMidiPresets = new List<int>();

    [Header("Audio Tags (Optional/Future Use)")]
    public string textureDescriptor;
    public AudioClip samplePreview;
}