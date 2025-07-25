using System.Collections.Generic;
using System.Linq;
using UnityEngine;
[System.Serializable]
public class PhaseRoleStrategy
{
    public MusicalRole role;
    public List<GhostPatternStrategy> ghostStrategies;
}
[System.Serializable]
public class RoleNoteSetSeries
{
    public MusicalRole role;
    public NoteSetSeries noteSetSeries;
}

[System.Serializable]
public class RoleGhostStrategy
{
    public MusicalRole role;
    public GhostPatternStrategy strategy;
}

[CreateAssetMenu(menuName = "Astral Planes/Musical Phase Profile")]
public class MusicalPhaseProfile : ScriptableObject
{
    public MusicalPhase phase;
    
    [Range(1, 8)]
    public int ghostLoopCount = 4; // Default value, customize per phase

    [Header("Note Set Series Per Role")]
    public List<RoleNoteSetSeries> roleNoteSetSeriesList = new();
    
    [Header("Audio")]
    public AudioClip[] drumClips;

    [Header("Visual")]
    public Color visualColor = Color.white;
    public RotationMode rotationMode = RotationMode.Uniform;
    public float rotationSpeed = 20f;

    [Header("Track Utility")]
    public TrackUtilityStrategy defaultRemixStrategy;
    public List<RemixRoleStrategy> remixRoleOverrides = new List<RemixRoleStrategy>();

    [Header("Spawning")]
    public int hitsRequired = 8;
    public int energyPerCollectable = 1;

    [Header("Labels")]
    public string shortLabel;
    [TextArea] public string moodDescription;
    [SerializeField]
    private List<RoleGhostStrategy> roleGhostStrategies = new List<RoleGhostStrategy>();

    public TrackUtilityStrategy GetRemixStrategyForRole(MusicalRole role)
    {
        foreach (var overrideEntry in remixRoleOverrides)
        {
            if (overrideEntry.role == role)
                return overrideEntry.strategy;
        }

        return defaultRemixStrategy;
    }

    public NoteSetSeries GetNoteSetSeriesForRole(MusicalRole role)
    {
        foreach (var pair in roleNoteSetSeriesList)
        {
            if (pair.role == role)
                return pair.noteSetSeries;
        }

        return null; // fallback if needed
    }

    public GhostPatternStrategy GetGhostStrategyForRole(MusicalRole role)
    {
        foreach (var pair in roleGhostStrategies)
        {
            if (pair.role == role)
                return pair.strategy;
        }

        return GhostPatternStrategy.Arpeggiated; // Default fallback
    }


}
[System.Serializable]
public class RemixRoleStrategy
{
    public MusicalRole role;
    public TrackUtilityStrategy strategy;
}
