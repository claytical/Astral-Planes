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

    public int keyRootMidi = 60;   // default C4
    [Header("Drums")]
    [Tooltip("Entry loops: minimal / nearly silent. One will be chosen at motif start.")]
    public List<AudioClip> entryDrumLoops = new();

    [Tooltip("Intensity loops: index maps to intensity (0 = B / low, last = E / high).")]
    public List<AudioClip> intensityDrumLoops = new();

    [Min(0), Tooltip("How many full drum loops to stay in the entry loop(s) before mapping intensity.")]
    public int entryLoopCount = 1;

    [Header("Timing")]
    public float bpm = 120f;
    public int stepsPerLoop = 16;

    [Header("Harmony")]
    [Tooltip("Chord progression used while this motif is active.")]
    public ChordProgressionProfile chordProgression;

    [Header("Per-role NoteSet configs")]
    [Tooltip("Per-role NoteSet generation configs for this motif.")]
    public List<RoleMotifNoteSetConfig> roleNoteConfigs = new List<RoleMotifNoteSetConfig>();

    [Header("Selection")]
    [Tooltip("Optional weight for random selection in a MotifLibrary.")]
    public float selectionWeight = 1f;
    
    [Tooltip("How many entries at the front of beatProfileSequence play once per motif, then are skipped on wrap.")]
    public int beatIntroCount = 0;

    [Tooltip("If true, drums are driven by player energy expenditure through beatProfileSequence.")]
    public bool driveBeatsFromEnergy = true;
    private void OnValidate() { 
        if (beatIntroCount < 0) beatIntroCount = 0; 
        if (entryDrumLoops == null) entryDrumLoops = new List<AudioClip>();
        if (intensityDrumLoops == null) intensityDrumLoops = new List<AudioClip>();
        if (beatIntroCount > entryDrumLoops.Count) beatIntroCount = entryDrumLoops.Count; 
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