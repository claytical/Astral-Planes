using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Motif-scoped NoteSet configuration for a single role.
/// This replaces the old RolePhaseNoteSetConfig, but does NOT depend on global MazeArchetype.
/// </summary>
[CreateAssetMenu(
    fileName = "RoleMotifNoteSetConfig",
    menuName = "Astral Planes/Motif/Role Motif NoteSet Config")]
public class RoleMotifNoteSetConfig : ScriptableObject
{
    [Header("Identity")]
    public string id;                   // Optional stable ID (e.g. "MotifA_BassMain")
    public MusicalRole role;            // Bass, Harmony, Lead, Groove, etc.

    [Header("Pitch / Scale")]
    public List<Weighted<ScaleType>> scales;          // e.g. Major(0.4), Dorian(0.35), Phrygian(0.25)

    [Header("Pattern & Rhythm")]
    public List<Weighted<PatternStrategy>> patterns;  // melodic / bassline strategies
    public List<Weighted<RhythmStyle>> rhythms;       // RhythmStyle palette, weighted

    [Header("Chord Region")]
    // Pick K of N chord functions to build a small progression region (per loop)
    public List<Weighted<string>> chordFunctions;     // "I", "vi", "IV", "V", "iiø", "bVII", etc.
    [Range(1, 8)]
    public int chordsPerRegion = 2;                   // stochastic small cells (2–4 beats each)
    [Header("Ascension Fuse")]
    [Tooltip("How many extended loops notes for this role take to reach the line of ascension.")]
    public int loopsToAscend = 1;

    [Header("Behaviors / Variation")]
    // Behaviors can stack; weights + cap keep it musical
    public List<Weighted<NoteBehavior>> behaviors;    // candidates for remix / auto-variation
    [Range(0, 4)]
    public int maxBehaviorsStack = 2;

    public VariationProfile variation = new VariationProfile();
}