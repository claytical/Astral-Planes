using System;
using UnityEngine;

public enum HarmonyCommit { AtBridgeStart, MidBridge, AtBridgeEnd }

[Serializable]
public class PhaseBridgeSignature
{
    public MusicalPhase fromPhase;
    public MusicalPhase toPhase;

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
    public static PhaseBridgeSignature Default(MusicalPhase from, MusicalPhase to) => new PhaseBridgeSignature {
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

    public static PhaseBridgeSignature Establish_To_Intensify() => new PhaseBridgeSignature {
        fromPhase = MusicalPhase.Establish, toPhase = MusicalPhase.Intensify,
        bars = 2, humanizeMs = 6f,
        rhythmOverride = RhythmStyle.Dense,
        noteBehaviorOverride = NoteBehavior.Percussion,
        durationScale = 0.6f, velocityScale = 1.1f, staccatissimo = true,
        commitTiming = HarmonyCommit.AtBridgeStart,
        seedTrackCountNextPhase = 1,
        preferredSeedRoles = new [] { MusicalRole.Groove, MusicalRole.Bass },
        includeDrums = true, fadeRibbons = true, growCoral = true
    };

    public static PhaseBridgeSignature Evolve_To_Intensify() => new PhaseBridgeSignature {
        fromPhase = MusicalPhase.Evolve, toPhase = MusicalPhase.Intensify,
        bars = 2, humanizeMs = 10f,
        rhythmOverride = RhythmStyle.Breakbeat,
        noteBehaviorOverride = NoteBehavior.Lead,
        durationScale = 0.8f, velocityScale = 1.15f,
        commitTiming = HarmonyCommit.MidBridge,
        seedTrackCountNextPhase = 2,
        preferredSeedRoles = new [] { MusicalRole.Groove, MusicalRole.Lead },
        includeDrums = true, fadeRibbons = true, growCoral = true
    };

    public static PhaseBridgeSignature Intensify_To_Release() => new PhaseBridgeSignature {
        fromPhase = MusicalPhase.Intensify, toPhase = MusicalPhase.Release,
        bars = 1, humanizeMs = 0f,
        rhythmOverride = RhythmStyle.Sparse,
        noteBehaviorOverride = NoteBehavior.Drone,
        durationScale = 1.4f, velocityScale = 0.85f,
        commitTiming = HarmonyCommit.AtBridgeEnd,
        seedTrackCountNextPhase = 1,
        preferredSeedRoles = new [] { MusicalRole.Harmony },
        includeDrums = true, fadeRibbons = true, growCoral = true
    };

    public static PhaseBridgeSignature Release_To_Wildcard() => new PhaseBridgeSignature {
        fromPhase = MusicalPhase.Release, toPhase = MusicalPhase.Wildcard,
        bars = 1, humanizeMs = 0f,
        rhythmOverride = RhythmStyle.Triplet,
        noteBehaviorOverride = NoteBehavior.Glitch,
        durationScale = 0.9f, velocityScale = 1.0f,
        commitTiming = HarmonyCommit.AtBridgeStart,
        seedTrackCountNextPhase = 1,
        preferredSeedRoles = new [] { MusicalRole.Lead },
        includeDrums = true, fadeRibbons = true, growCoral = true
    };

    public static PhaseBridgeSignature Wildcard_To_Pop() => new PhaseBridgeSignature {
        fromPhase = MusicalPhase.Wildcard, toPhase = MusicalPhase.Pop,
        bars = 2, humanizeMs = 8f,
        rhythmOverride = RhythmStyle.Syncopated,
        noteBehaviorOverride = NoteBehavior.Hook,
        durationScale = 1.0f, velocityScale = 1.2f,
        commitTiming = HarmonyCommit.AtBridgeEnd,
        seedTrackCountNextPhase = 2,
        preferredSeedRoles = new [] { MusicalRole.Lead, MusicalRole.Groove },
        includeDrums = true, fadeRibbons = true, growCoral = true
    };

    public static PhaseBridgeSignature Pop_To_Evolve() => new PhaseBridgeSignature {
        fromPhase = MusicalPhase.Pop, toPhase = MusicalPhase.Evolve,
        bars = 2, humanizeMs = 12f,
        rhythmOverride = RhythmStyle.Steady,
        noteBehaviorOverride = NoteBehavior.Lead,
        durationScale = 1.1f, velocityScale = 0.95f,
        commitTiming = HarmonyCommit.AtBridgeEnd,
        seedTrackCountNextPhase = 2,
        preferredSeedRoles = new [] { MusicalRole.Harmony, MusicalRole.Bass },
        includeDrums = true, fadeRibbons = true, growCoral = true
    };

    public static PhaseBridgeSignature For(MusicalPhase from, MusicalPhase to)
    {
        if (from == MusicalPhase.Establish && to == MusicalPhase.Intensify) return Establish_To_Intensify();
        if (from == MusicalPhase.Evolve     && to == MusicalPhase.Intensify) return Evolve_To_Intensify();
        if (from == MusicalPhase.Intensify  && to == MusicalPhase.Release)   return Intensify_To_Release();
        if (from == MusicalPhase.Release    && to == MusicalPhase.Wildcard)  return Release_To_Wildcard();
        if (from == MusicalPhase.Wildcard   && to == MusicalPhase.Pop)       return Wildcard_To_Pop();
        if (from == MusicalPhase.Pop        && to == MusicalPhase.Evolve)    return Pop_To_Evolve();
        return Default(from, to);
    }
}
