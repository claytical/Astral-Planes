using System;
using System.Collections.Generic;
using UnityEngine;

public enum NoteBehavior
{
    // Canonical, transformation-style behaviors
    None = 0,
    RootShift,
    ChordChange,
    InvertVoicing,
    RegisterExpand,
    RegisterCompress,
    AddNeighborOrnament,
    AddPassingTones,
    HumanizeTiming,
    Staccatify,
    Legatify,
    VelocityShape,
    Swingify,
    DensityPulse, // accent cycles (e.g., 2/3 over 4)

    // ---- Legacy aliases (compile-safe) ----
    // These used to be treated like styles/roles. Keep them to un-break references,
    // but we'll *map* them to the canonical behaviors at runtime.
    [Obsolete("Alias: maps to Legatify + VelocityShape(-)")] Drone,
    [Obsolete("Alias: maps to Staccatify + VelocityShape(+)")] Lead,
    [Obsolete("Alias: maps to Staccatify + HumanizeTiming")] Percussion,
    [Obsolete("Alias: maps to HumanizeTiming + Swingify or jitter")] Glitch,
    [Obsolete("Alias: maps to Legatify (mild)")] Harmony,
    [Obsolete("Alias: maps to Legatify")] Sustain,
    [Obsolete("Alias: maps to VelocityShape + Ostinato-like accents")] Hook,
    [Obsolete("Alias: maps to Staccatify + VelocityShape(+)")] Bass
}
public enum PatternStrategy {
    Arpeggiated, ArpUp, ArpDown, ArpPingPong,
    StaticRoot, FifthJump, WalkingBass, ScaleWalk,
    MelodicPhrase, NeighborOrnament, Ostinato, CallAndResponse,
    ChordalStab, Drone, PercussiveLoop, SyncopatedHook, HemiolaFigure, Randomized
}
[CreateAssetMenu(fileName = "NoteSetConfig", menuName = "Astral Planes/NoteSet Config")]
public class RolePhaseNoteSetConfig :  ScriptableObject {
    public MusicalPhase phase;
    public MusicalRole role;

    public List<Weighted<ScaleType>> scales;             // e.g. Major(0.4), Dorian(0.35), Phrygian(0.25)
    public List<Weighted<PatternStrategy>> patterns;     // multiple, with weights
    public List<Weighted<RhythmStyle>> rhythms;          // your existing RhythmStyle palette, weighted

    // Pick K of N chord functions to build a small progression region (per loop)
    public List<Weighted<string>> chordFunctions;        // e.g. "I", "vi", "IV", "V", "iiø", "bVII"
    [Range(1,8)] public int chordsPerRegion = 2;         // stochastic small cells (2–4 beats each)

    // Behaviors can stack; weights + cap keep it musical
    public List<Weighted<NoteBehavior>> behaviors;       // candidates for remix rings / auto-variation
    [Range(0,4)] public int maxBehaviorsStack = 2;

    public VariationProfile variation = new();
}
