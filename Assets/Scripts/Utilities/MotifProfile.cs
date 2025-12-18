using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A complete musical motif: beat mood + chord progression + role strategies + per-role NoteSet configs.
/// This is what a PhaseStar should reference.
/// </summary>
[CreateAssetMenu(
    fileName = "MotifProfile",
    menuName = "Astral Planes/Motif/Motif Profile")]
public class MotifProfile : ScriptableObject
{
    [Header("Identity")]
    public string motifId;                  // Stable identifier (e.g. "RockSteady_A")
    public string displayName;             // Designer-friendly label

    [Tooltip("Beat mood for this motif (controls drum clips/BPM/time-feel).")]
    public BeatMood beatMood;
    public int keyRootMidi = 60;   // default C4
    
    [Header("Harmony")]
    [Tooltip("Chord progression used while this motif is active.")]
    public ChordProgressionProfile chordProgression;

    [Tooltip("Per-role strategies (bridge behavior, ghost strategies, etc.).")]
    public MusicalPhaseProfile phaseProfile;   // Reuses your existing phase profile type

    [Header("Per-role NoteSet configs")]
    [Tooltip("Per-role NoteSet generation configs for this motif.")]
    public List<RoleMotifNoteSetConfig> roleNoteConfigs = new List<RoleMotifNoteSetConfig>();

    [Header("Selection")]
    [Tooltip("Optional weight for random selection in a MotifLibrary.")]
    public float selectionWeight = 1f;

    /// <summary>
    /// Get the RoleMotifNoteSetConfig for a specific role in this motif, if present.
    /// </summary>
    public RoleMotifNoteSetConfig GetRoleConfig(MusicalRole role)
    {
        if (roleNoteConfigs == null) return null;
        for (int i = 0; i < roleNoteConfigs.Count; i++)
        {
            var cfg = roleNoteConfigs[i];
            if (cfg != null && cfg.role == role)
                return cfg;
        }
        return null;
    }
    public int GetLoopsToAscendFor(MusicalRole role, int fallback = 1)
    {
        var cfg = GetRoleConfig(role);
        return (cfg != null && cfg.loopsToAscend > 0)
            ? cfg.loopsToAscend
            : fallback;
    }

    public RoleMotifNoteSetConfig GetConfigForRole(MusicalRole role)
    {
        if (roleNoteConfigs == null) return null;
        for (int i = 0; i < roleNoteConfigs.Count; i++)
        {
            var cfg = roleNoteConfigs[i];
            if (cfg != null && cfg.role == role)
                return cfg;
        }
        return null;
    }

}