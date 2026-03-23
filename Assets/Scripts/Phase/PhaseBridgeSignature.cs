using System;
using UnityEngine;
//CHATGPT REMOVAL HICCUP - NOT CURRENTLY INTEGRATED
public enum HarmonyCommit { AtBridgeStart, MidBridge, AtBridgeEnd }

[Serializable]
public class PhaseBridgeSignature
{
    [Header("Who plays")]
    public bool useOnlyPerfectTracks = true;
    [Range(1,4)] public int maxBridgeTracks = 4;
    public bool includeDrums = true;

    [Header("Timing")]
    public int bars = 2;
    [Range(0f, 30f)] public float humanizeMs = 8f;

    [Header("Temporary musical transforms")]
    public RhythmStyle rhythmOverride = RhythmStyle.Steady;

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
    public static PhaseBridgeSignature Default() => new PhaseBridgeSignature {
        useOnlyPerfectTracks = true, maxBridgeTracks = 4, includeDrums = true,
        bars = 1, humanizeMs = 8f,
        rhythmOverride = RhythmStyle.Steady,
        durationScale = 1.0f, velocityScale = 1.0f, staccatissimo = false,
        commitTiming = HarmonyCommit.AtBridgeEnd,
        seedTrackCountNextPhase = 1,
        fadeRibbons = true, growCoral = true
    };
    
}
