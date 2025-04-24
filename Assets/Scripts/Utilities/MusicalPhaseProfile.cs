using UnityEngine;

[CreateAssetMenu(menuName = "Astral Planes/Musical Phase Profile")]
public class MusicalPhaseProfile : ScriptableObject
{
    public MusicalPhase phase;
    
    [Header("Audio")]
    public AudioClip[] drumClips;

    [Header("Visual")]
    public Color visualColor = Color.white;
    public RotationMode rotationMode = RotationMode.Uniform;
    public float rotationSpeed = 20f;

    [Header("Spawning")]
    public int hitsRequired = 8;
    public int energyPerCollectable = 1;

    [Header("Labels")]
    public string shortLabel;
    [TextArea] public string moodDescription;
}
