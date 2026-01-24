using System;
using UnityEngine;
//CHATGPT REMOVAL HICCUP - NOT CURRENTLY INTEGRATED
public enum HarmonyCommit { AtBridgeStart, MidBridge, AtBridgeEnd }

[Serializable]
public class PhaseBridgeSignature
{
    public MazeArchetype fromPhase;
    public MazeArchetype toPhase;

    [Header("Who plays")]
    public bool useOnlyPerfectTracks = true;
    [Range(1,4)] public int maxBridgeTracks = 4;
    public bool includeDrums = true;

    [Header("Timing")]
    public int bars = 2;
    [Range(0f, 30f)] public float humanizeMs = 8f;

    [Header("Temporary musical transforms")]
    public RhythmStyle rhythmOverride = RhythmStyle.Steady;

    public NoteBehavior noteBehaviorOverride = NoteBehavior.Lead;
    [Range(0.4f,1.6f)] public float durationScale = 1.0f;
    [Range(0.6f,1.5f)] public float velocityScale = 1.0f;
    public bool staccatissimo = false;

    [Header("Harmony handoff")]
    public HarmonyCommit commitTiming = HarmonyCommit.AtBridgeEnd;

    [Header("Seeds for next phase")]
    [Range(0,4)] public int seedTrackCountNextPhase = 1;
    public MusicalRole[] preferredSeedRoles;

    [Header("Visuals")]
    public bool fadeRibbons = true;
    public bool growCoral = true;
}

public static class BridgeLibrary
{
    private static PhaseBridgeSignature Default(MazeArchetype from, MazeArchetype to) => new PhaseBridgeSignature {
        fromPhase = from, toPhase = to,
        useOnlyPerfectTracks = true, maxBridgeTracks = 4, includeDrums = true,
        bars = 1, humanizeMs = 8f,
        rhythmOverride = RhythmStyle.Steady,
        noteBehaviorOverride = NoteBehavior.Lead,
        durationScale = 1.0f, velocityScale = 1.0f, staccatissimo = false,
        commitTiming = HarmonyCommit.AtBridgeEnd,
        seedTrackCountNextPhase = 1,
        fadeRibbons = true, growCoral = true
    };

    private static PhaseBridgeSignature Establish_To_Intensify() => new PhaseBridgeSignature {
        fromPhase = MazeArchetype.Establish, toPhase = MazeArchetype.Intensify,
        bars = 2, humanizeMs = 6f,
        rhythmOverride = RhythmStyle.Dense,
        noteBehaviorOverride = NoteBehavior.Percussion,
        durationScale = 0.6f, velocityScale = 1.1f, staccatissimo = true,
        commitTiming = HarmonyCommit.AtBridgeStart,
        seedTrackCountNextPhase = 1,
        preferredSeedRoles = new [] { MusicalRole.Groove, MusicalRole.Bass },
        includeDrums = true, fadeRibbons = true, growCoral = true
    };

    private static PhaseBridgeSignature Evolve_To_Intensify() => new PhaseBridgeSignature {
        fromPhase = MazeArchetype.Evolve, toPhase = MazeArchetype.Intensify,
        bars = 2, humanizeMs = 10f,
        rhythmOverride = RhythmStyle.Breakbeat,
        noteBehaviorOverride = NoteBehavior.Lead,
        durationScale = 0.8f, velocityScale = 1.15f,
        commitTiming = HarmonyCommit.MidBridge,
        seedTrackCountNextPhase = 2,
        preferredSeedRoles = new [] { MusicalRole.Groove, MusicalRole.Lead },
        includeDrums = true, fadeRibbons = true, growCoral = true
    };

    private static PhaseBridgeSignature Intensify_To_Release() => new PhaseBridgeSignature {
        fromPhase = MazeArchetype.Intensify, toPhase = MazeArchetype.Release,
        bars = 1, humanizeMs = 0f,
        rhythmOverride = RhythmStyle.Sparse,
        noteBehaviorOverride = NoteBehavior.Drone,
        durationScale = 1.4f, velocityScale = 0.85f,
        commitTiming = HarmonyCommit.AtBridgeEnd,
        seedTrackCountNextPhase = 1,
        preferredSeedRoles = new [] { MusicalRole.Harmony },
        includeDrums = true, fadeRibbons = true, growCoral = true
    };

    private static PhaseBridgeSignature Release_To_Wildcard() => new PhaseBridgeSignature {
        fromPhase = MazeArchetype.Release, toPhase = MazeArchetype.Wildcard,
        bars = 1, humanizeMs = 0f,
        rhythmOverride = RhythmStyle.Triplet,
        noteBehaviorOverride = NoteBehavior.Glitch,
        durationScale = 0.9f, velocityScale = 1.0f,
        commitTiming = HarmonyCommit.AtBridgeStart,
        seedTrackCountNextPhase = 1,
        preferredSeedRoles = new [] { MusicalRole.Lead },
        includeDrums = true, fadeRibbons = true, growCoral = true
    };

    private static PhaseBridgeSignature Wildcard_To_Pop() => new PhaseBridgeSignature {
        fromPhase = MazeArchetype.Wildcard, toPhase = MazeArchetype.Pop,
        bars = 2, humanizeMs = 8f,
        rhythmOverride = RhythmStyle.Syncopated,
        noteBehaviorOverride = NoteBehavior.Hook,
        durationScale = 1.0f, velocityScale = 1.2f,
        commitTiming = HarmonyCommit.AtBridgeEnd,
        seedTrackCountNextPhase = 2,
        preferredSeedRoles = new [] { MusicalRole.Lead, MusicalRole.Groove },
        includeDrums = true, fadeRibbons = true, growCoral = true
    };

    private static PhaseBridgeSignature Pop_To_Evolve() => new PhaseBridgeSignature {
        fromPhase = MazeArchetype.Pop, toPhase = MazeArchetype.Evolve,
        bars = 2, humanizeMs = 12f,
        rhythmOverride = RhythmStyle.Steady,
        noteBehaviorOverride = NoteBehavior.Lead,
        durationScale = 1.1f, velocityScale = 0.95f,
        commitTiming = HarmonyCommit.AtBridgeEnd,
        seedTrackCountNextPhase = 2,
        preferredSeedRoles = new [] { MusicalRole.Harmony, MusicalRole.Bass },
        includeDrums = true, fadeRibbons = true, growCoral = true
    };

    public static PhaseBridgeSignature For(MazeArchetype from, MazeArchetype to)
    {
        if (from == MazeArchetype.Establish && to == MazeArchetype.Intensify) return Establish_To_Intensify();
        if (from == MazeArchetype.Evolve     && to == MazeArchetype.Intensify) return Evolve_To_Intensify();
        if (from == MazeArchetype.Intensify  && to == MazeArchetype.Release)   return Intensify_To_Release();
        if (from == MazeArchetype.Release    && to == MazeArchetype.Wildcard)  return Release_To_Wildcard();
        if (from == MazeArchetype.Wildcard   && to == MazeArchetype.Pop)       return Wildcard_To_Pop();
        if (from == MazeArchetype.Pop        && to == MazeArchetype.Evolve)    return Pop_To_Evolve();
        return Default(from, to);
    }
}
