using System;
using System.Collections.Generic;
using UnityEngine;
[Serializable]
public class PhaseRoleStrategy
{
    public MusicalRole role;
    public List<PatternStrategy> ghostStrategies;
}

[Serializable]

public class RoleStrategy
{
    public MusicalRole role;
    public PatternStrategy strategy;
}
public enum MazeArchetype
{
    Establish = 0,   // early stable loop
    Evolve = 1,      // moderate complexity
    Intensify = 2,   // denser and brighter
    Release = 3,     // breath or breakdown
    Wildcard = 4,    // glitchy, unpredictable
    Pop = 5          // catchy hook
}

[CreateAssetMenu(menuName = "Astral Planes/Musical Phase Profile")]
public class MusicalPhaseProfile : ScriptableObject
{
    public MazeArchetype phase;
    
    [Header("Labels")]
    public string shortLabel;
    [SerializeField]
    private List<RoleStrategy> roleStrategies = new List<RoleStrategy>();

 
    public PatternStrategy GetPatternStrategyForRole(MusicalRole role)
    {
        foreach (var pair in roleStrategies)
        {
            if (pair.role == role)
                return pair.strategy;
        }

        return PatternStrategy.Arpeggiated; // Default fallback
    }
    
}
[Serializable]
public class BridgeSignature {
    public MazeArchetype fromPhase;
    public MazeArchetype toPhase;

    // who plays the bridge
    public bool useOnlyPerfectTracks = true;     // perfect tracks only
    public int maxBridgeTracks = 4;              // cap the count if you want fewer than perfect set
    public bool includeDrums = true;             // drum accent during bridge

    // timing
    public int bars = 2;                         // bridge length in bars of the *current* loop
    public float humanizeMs = 12f;               // small jitter on onsets

    // musical transforms applied temporarily during the bridge
    public RhythmStyle rhythmOverride;           // e.g., StaccatoEighths, DroneFade
    public NoteBehavior noteBehaviorOverride;    // e.g., Arpeggiated, Drone, Percussive
    public float durationScale = 1.0f;           // (0.4..1.6) short/long
    public float velocityScale = 1.0f;           // scale MIDI vel
    public bool staccatissimo = false;           // hard gate time

    // harmony handoff
    public enum HarmonyCommit { AtBridgeStart, MidBridge, AtBridgeEnd }
    public HarmonyCommit commitTiming = HarmonyCommit.AtBridgeEnd;

    // after the bridge: which tracks remain as “seeds” in the next phase
    public int seedTrackCountNextPhase = 1;      // keep this many tracks sounding
    public MusicalRole[] preferredSeedRoles;     // optional bias (e.g., Bass + Groove)

    // visuals
    public bool fadeRibbons = true;              // fade NoteVisualizer ribbons during bridge
    public bool growCoral = true;                // pipe bridge notes to CoralVisualizer
}

