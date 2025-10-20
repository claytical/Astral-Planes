#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.IO;

public static class NoteSetConfigGenerator
{
    private const string ConfigRootPath = "Assets/NoteSetConfigs/";

    [MenuItem("Astral Planes/Generate All Note Set Configs")]
    public static void GenerateConfigs()
    {
        if (!AssetDatabase.IsValidFolder(ConfigRootPath))
            AssetDatabase.CreateFolder("Assets", "NoteSetConfigs");

        var allConfigs = new List<RolePhaseNoteSetConfig>();

        foreach (MusicalPhase phase in System.Enum.GetValues(typeof(MusicalPhase)))
        {
            string phaseFolder = Path.Combine(ConfigRootPath, phase.ToString());
            if (!AssetDatabase.IsValidFolder(phaseFolder))
                AssetDatabase.CreateFolder(ConfigRootPath.TrimEnd('/'), phase.ToString());

            foreach (MusicalRole role in System.Enum.GetValues(typeof(MusicalRole)))
            {
                var config = ScriptableObject.CreateInstance<RolePhaseNoteSetConfig>();
                config.phase = phase;
                config.role  = role;

                // --- Scales (weights are a starting point; adjust per taste) ---
                config.scales = new List<Weighted<ScaleType>> {
                    W(ScaleType.Major,      role == MusicalRole.Bass ? .40f : .30f),
                    W(ScaleType.Minor,      .25f),
                    W(ScaleType.Dorian,     .20f),
                    W(ScaleType.Phrygian,   .10f),
                    W(ScaleType.Mixolydian, .05f),
                };

                // --- Patterns (varies by role) ---
                config.patterns = DefaultPatternsFor(role);

                // --- Rhythms (use your existing RhythmStyle set) ---
                config.rhythms = new List<Weighted<RhythmStyle>> {
                    W(RhythmStyle.Steady,          .30f),
                    W(RhythmStyle.FourOnTheFloor,  .20f),
                    W(RhythmStyle.Sparse,          .20f),
                    W(RhythmStyle.Syncopated,      .15f),
                    W(RhythmStyle.Dense,           .10f),
                    W(RhythmStyle.Triplet,         .05f),
                };

                // --- Chord function pool (K-of-N per loop) ---
                config.chordFunctions = new List<Weighted<string>> {
                    W("I",   .35f), W("IV", .20f), W("V", .20f),
                    W("vi",  .15f), W("ii", .08f), W("bVII", .02f),
                };
                config.chordsPerRegion = 2;

                // --- Behavior seeds (optional; stack up to 2) ---
                config.behaviors = new List<Weighted<NoteBehavior>> {
                    W(NoteBehavior.VelocityShape,  .35f),
                    W(NoteBehavior.Staccatify,     .25f),
                    W(NoteBehavior.Legatify,       .20f),
                    W(NoteBehavior.HumanizeTiming, .20f),
                };
                config.maxBehaviorsStack = 2;

                // --- Variation defaults ---
                config.variation = new VariationProfile {
                    extensionBias     = 0.30f,
                    passingToneProb   = 0.15f,
                    neighborOrnProb   = 0.12f,
                    minMidi           = role == MusicalRole.Bass ? 28 : 36,
                    maxMidi           = role == MusicalRole.Lead ? 96 : 84,
                    leapProb          = 0.12f,
                    octaveMoveProb    = 0.10f,
                    restProb          = (phase == MusicalPhase.Release) ? 0.18f : 0.10f,
                    humanizeMs        = 0.02f,
                    durJitter         = 1.0f,
                    mutatePitch       = 0.08f,
                    mutateVelocity    = 0.12f,
                    mutateDuration    = 0.10f
                };

                string assetPath = Path.Combine(phaseFolder, $"{phase}_{role}.asset");
                AssetDatabase.CreateAsset(config, assetPath);
                allConfigs.Add(config);
            }
        }

        var library = ScriptableObject.CreateInstance<NoteSetConfigLibrary>();
        library.configs = allConfigs;

        string libraryPath = Path.Combine(ConfigRootPath, "NoteSetConfigLibrary.asset");
        AssetDatabase.CreateAsset(library, libraryPath);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"✅ Created {allConfigs.Count} configs and NoteSetConfigLibrary.");
    }

    private static Weighted<T> W<T>(T item, float weight) => new Weighted<T> { item = item, weight = Mathf.Clamp01(weight) };

    private static List<Weighted<PatternStrategy>> DefaultPatternsFor(MusicalRole role)
    {
        switch (role)
        {
            case MusicalRole.Bass:
                return new List<Weighted<PatternStrategy>> {
                    W(PatternStrategy.StaticRoot,  .30f),
                    W(PatternStrategy.FifthJump,   .25f),
                    W(PatternStrategy.WalkingBass, .25f),
                    W(PatternStrategy.ArpDown,     .10f),
                    W(PatternStrategy.ScaleWalk,   .10f),
                };
            case MusicalRole.Harmony:
                return new List<Weighted<PatternStrategy>> {
                    W(PatternStrategy.ChordalStab, .35f),
                    W(PatternStrategy.ArpUp,       .25f),
                    W(PatternStrategy.ArpPingPong, .20f),
                    W(PatternStrategy.Drone,       .20f),
                };
            case MusicalRole.Lead:
                return new List<Weighted<PatternStrategy>> {
                    W(PatternStrategy.MelodicPhrase,   .35f),
                    W(PatternStrategy.NeighborOrnament,.25f),
                    W(PatternStrategy.ArpPingPong,     .20f),
                    W(PatternStrategy.SyncopatedHook,  .20f),
                };
            case MusicalRole.Groove:
            default:
                return new List<Weighted<PatternStrategy>> {
                    W(PatternStrategy.PercussiveLoop, .60f),
                    W(PatternStrategy.Ostinato,       .25f),
                    W(PatternStrategy.Randomized,     .15f),
                };
        }
    }
}
#endif
