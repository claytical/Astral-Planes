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

    [Header("Star Structure")]
    [Min(1), Tooltip("Total shards this PhaseStar ejects. Distribute roles across shards via round-robin.")]
    public int nodesPerStar = 4;

    [Tooltip("Star behavior profile for this motif (speed, drift, keep-clear, safety bubble, etc.). Overrides the phase-level personality registry.")]
    public PhaseStarBehaviorProfile starBehavior;

    [Header("Maze")]
    [Tooltip("Pattern config for maze generation while this motif is active. If null, falls back to FullFill.")]
    public MazePatternConfig mazePattern;

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

    /// <summary>
    /// Returns the config to use for a given role and bin index.
    ///
    /// One config  → same config for every bin (only harmonic shift varies between bins).
    /// N configs   → configs[binIndex % N] so each bin gets a distinct variation in round-robin order.
    ///
    /// This is deterministic: given the same binIndex you always get the same config,
    /// which is essential for saturation detection and for MineNode re-spawns being
    /// consistent with what was originally placed.
    /// </summary>
    public RoleMotifNoteSetConfig GetConfigForRoleAtBin(MusicalRole role, int binIndex, int totalBins)
    {
        if (roleNoteConfigs == null || roleNoteConfigs.Count == 0)
            return null;
        List<RoleMotifNoteSetConfig> matches = null;
        for (int i = 0; i < roleNoteConfigs.Count; i++)        {
            var cfg = roleNoteConfigs[i];
            if (cfg != null && cfg.role == role)
            {
                matches ??= new List<RoleMotifNoteSetConfig>();
                matches.Add(cfg);
            }
        }
 
        if (matches == null || matches.Count == 0) return null;

        // Single config: same pattern every bin (root-shift via chord progression handles variation).
        if (matches.Count == 1) return matches[0];

        // Multiple configs: deterministic round-robin so bin 0 → cfg[0], bin 1 → cfg[1], etc.
        int idx = Mathf.Abs(binIndex) % matches.Count;
        return matches[idx];
    }

    /// <summary>
    /// Returns distinct MusicalRoles present in roleNoteConfigs, in first-appearance order.
    /// The first role in this list is treated as primary (gets the largest Voronoi territory).
    /// </summary>
    public List<MusicalRole> GetActiveRoles()
    {
        var seen = new HashSet<MusicalRole>();
        var result = new List<MusicalRole>();
        for (int i = 0; i < roleNoteConfigs.Count; i++)
        {
            var cfg = roleNoteConfigs[i];
            if (cfg != null && cfg.role != MusicalRole.None && seen.Add(cfg.role))
                result.Add(cfg.role);
        }
        return result;
    }

}
