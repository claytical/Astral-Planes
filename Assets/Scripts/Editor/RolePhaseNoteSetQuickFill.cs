// Assets/Editor/RolePhaseNoteSetQuickFill.cs
#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

public static class RolePhaseNoteSetQuickFill
{
    // ===== MENU =====
    [MenuItem("Astral Planes/NoteSet Configs/Quick Fill (only empties)")]
    public static void QuickFillOnlyEmpties() => ProcessAll(overwrite:false);

    [MenuItem("Astral Planes/NoteSet Configs/Quick Fill (overwrite all)")]
    public static void QuickFillOverwriteAll() => ProcessAll(overwrite:true);

    // ===== MAIN =====
    private static void ProcessAll(bool overwrite)
    {
        var guids = AssetDatabase.FindAssets("t:RolePhaseNoteSetConfig");
        if (guids == null || guids.Length == 0)
        {
            Debug.LogWarning("No RolePhaseNoteSetConfig assets found.");
            return;
        }

        int changed = 0;
        foreach (var guid in guids)
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var cfg = AssetDatabase.LoadAssetAtPath<RolePhaseNoteSetConfig>(path);
            if (cfg == null) continue;

            bool modified = Populate(cfg, overwrite);
            if (modified)
            {
                EditorUtility.SetDirty(cfg);
                changed++;
            }
        }
        if (changed > 0)
        {
            AssetDatabase.SaveAssets();
            Debug.Log($"✅ Quick Fill complete. Modified {changed} RolePhaseNoteSetConfig asset(s).");
        }
        else
        {
            Debug.Log("No changes made (assets already populated and 'only empties' selected).");
        }
    }

    // ===== POPULATE ONE CONFIG =====
    private static bool Populate(RolePhaseNoteSetConfig cfg, bool overwrite)
    {
        bool changed = false;

        // Ensure lists exist
        cfg.scales         ??= new List<Weighted<ScaleType>>();
        cfg.patterns       ??= new List<Weighted<PatternStrategy>>();
        cfg.rhythms        ??= new List<Weighted<RhythmStyle>>();
        cfg.chordFunctions ??= new List<Weighted<string>>();
        cfg.behaviors      ??= new List<Weighted<NoteBehavior>>();
        cfg.variation      ??= new VariationProfile();

        // 1) Core defaults per role
        changed |= FillScales(cfg, overwrite);
        changed |= FillPatterns(cfg, overwrite);
        changed |= FillRhythms(cfg, overwrite);
        changed |= FillChordFunctions(cfg, overwrite);
        changed |= FillBehaviors(cfg, overwrite);
        changed |= FillVariation(cfg, overwrite);

        // 2) Phase-specific adjustments
        ApplyPhaseAdjustments(cfg);

        // Clamp chords per region sane
        cfg.chordsPerRegion = Mathf.Clamp(cfg.chordsPerRegion, 1, 4);

        return changed;
    }

    // ===== DEFAULTS BY ROLE =====
    private static bool FillScales(RolePhaseNoteSetConfig cfg, bool overwrite)
    {
        if (!overwrite && cfg.scales.Count > 0) return false;
        cfg.scales.Clear();

        switch (cfg.role)
        {
            case MusicalRole.Bass:
                cfg.scales.AddRange(new[]{
                    W(ScaleType.Major, .30f), W(ScaleType.Minor, .25f),
                    W(ScaleType.Dorian,.25f), W(ScaleType.Mixolydian,.15f),
                    W(ScaleType.Phrygian,.05f)
                });
                break;
            case MusicalRole.Harmony:
                cfg.scales.AddRange(new[]{
                    W(ScaleType.Major,.35f), W(ScaleType.Minor,.20f),
                    W(ScaleType.Lydian,.20f),W(ScaleType.Dorian,.15f),
                    W(ScaleType.Mixolydian,.10f)
                });
                break;
            case MusicalRole.Lead:
                cfg.scales.AddRange(new[]{
                    W(ScaleType.Dorian,.30f), W(ScaleType.Minor,.25f),
                    W(ScaleType.Major,.20f),  W(ScaleType.Mixolydian,.15f),
                    W(ScaleType.Phrygian,.10f)
                });
                break;
            case MusicalRole.Groove:
            default:
                cfg.scales.AddRange(new[]{
                    W(ScaleType.Major,.30f), W(ScaleType.Minor,.30f),
                    W(ScaleType.Dorian,.20f),W(ScaleType.Mixolydian,.20f)
                });
                break;
        }
        return true;
    }

    private static bool FillPatterns(RolePhaseNoteSetConfig cfg, bool overwrite)
    {
        if (!overwrite && cfg.patterns.Count > 0) return false;
        cfg.patterns.Clear();

        switch (cfg.role)
        {
            case MusicalRole.Bass:
                cfg.patterns.AddRange(new[]{
                    W(PatternStrategy.StaticRoot,.30f),
                    W(PatternStrategy.FifthJump,.25f),
                    W(PatternStrategy.WalkingBass,.25f),
                    W(PatternStrategy.ArpDown,.10f),
                    W(PatternStrategy.ScaleWalk,.10f),
                });
                break;

            case MusicalRole.Harmony:
                cfg.patterns.AddRange(new[]{
                    W(PatternStrategy.ChordalStab,.35f),
                    W(PatternStrategy.ArpUp,.25f),
                    W(PatternStrategy.ArpPingPong,.20f),
                    W(PatternStrategy.Drone,.20f),
                });
                break;

            case MusicalRole.Lead:
                cfg.patterns.AddRange(new[]{
                    W(PatternStrategy.MelodicPhrase,.35f),
                    W(PatternStrategy.NeighborOrnament,.25f),
                    W(PatternStrategy.ArpPingPong,.20f),
                    W(PatternStrategy.SyncopatedHook,.20f),
                });
                break;

            case MusicalRole.Groove:
            default:
                cfg.patterns.AddRange(new[]{
                    W(PatternStrategy.PercussiveLoop,.60f),
                    W(PatternStrategy.Ostinato,.25f),
                    W(PatternStrategy.Randomized,.15f),
                });
                break;
        }
        return true;
    }

    private static bool FillRhythms(RolePhaseNoteSetConfig cfg, bool overwrite)
    {
        if (!overwrite && cfg.rhythms.Count > 0) return false;
        cfg.rhythms.Clear();

        switch (cfg.role)
        {
            case MusicalRole.Bass:
                cfg.rhythms.AddRange(new[]{
                    W(RhythmStyle.Steady,.30f),
                    W(RhythmStyle.FourOnTheFloor,.25f),
                    W(RhythmStyle.Sparse,.20f),
                    W(RhythmStyle.Syncopated,.15f),
                    W(RhythmStyle.Triplet,.10f),
                });
                break;

            case MusicalRole.Harmony:
                cfg.rhythms.AddRange(new[]{
                    W(RhythmStyle.Steady,.30f),
                    W(RhythmStyle.Syncopated,.25f),
                    W(RhythmStyle.Swing,.20f),
                    W(RhythmStyle.Sparse,.15f),
                    W(RhythmStyle.PulseBuild,.10f),
                });
                break;

            case MusicalRole.Lead:
                cfg.rhythms.AddRange(new[]{
                    W(RhythmStyle.Syncopated,.35f),
                    W(RhythmStyle.Swing,.25f),
                    W(RhythmStyle.StaccatoEighths,.20f),
                    W(RhythmStyle.Scatter,.10f),
                    W(RhythmStyle.Triplet,.10f),
                });
                break;

            case MusicalRole.Groove:
            default:
                cfg.rhythms.AddRange(new[]{
                    W(RhythmStyle.Steady,.25f),
                    W(RhythmStyle.Dense,.25f),
                    W(RhythmStyle.FourOnTheFloor,.20f),
                    W(RhythmStyle.Breakbeat,.15f),
                    W(RhythmStyle.Stutter,.10f),
                    W(RhythmStyle.Scatter,.05f),
                });
                break;
        }
        return true;
    }

    private static bool FillChordFunctions(RolePhaseNoteSetConfig cfg, bool overwrite)
    {
        if (!overwrite && cfg.chordFunctions.Count > 0 && cfg.chordsPerRegion > 0) return false;
        cfg.chordFunctions.Clear();

        // Default diatonic pool
        cfg.chordFunctions.AddRange(new[]{
            W("I", .35f), W("V", .20f), W("IV", .20f), W("vi", .15f), W("ii", .08f), W("bVII", .02f)
        });
        cfg.chordsPerRegion = 2;
        return true;
    }

    private static bool FillBehaviors(RolePhaseNoteSetConfig cfg, bool overwrite)
    {
        if (!overwrite && cfg.behaviors.Count > 0 && cfg.maxBehaviorsStack > 0) return false;
        cfg.behaviors.Clear();

        switch (cfg.role)
        {
            case MusicalRole.Bass:
                cfg.behaviors.AddRange(new[]{
                    W(NoteBehavior.Staccatify,.35f),
                    W(NoteBehavior.VelocityShape,.30f),
                    W(NoteBehavior.HumanizeTiming,.20f),
                    W(NoteBehavior.Legatify,.15f),
                });
                cfg.maxBehaviorsStack = 2;
                break;

            case MusicalRole.Harmony:
                cfg.behaviors.AddRange(new[]{
                    W(NoteBehavior.Legatify,.35f),
                    W(NoteBehavior.InvertVoicing,.25f),
                    W(NoteBehavior.VelocityShape,.20f),
                    W(NoteBehavior.HumanizeTiming,.20f),
                });
                cfg.maxBehaviorsStack = 2;
                break;

            case MusicalRole.Lead:
                cfg.behaviors.AddRange(new[]{
                    W(NoteBehavior.VelocityShape,.35f),
                    W(NoteBehavior.HumanizeTiming,.25f),
                    W(NoteBehavior.Staccatify,.20f),
                    W(NoteBehavior.Legatify,.20f),
                });
                cfg.maxBehaviorsStack = 2;
                break;

            case MusicalRole.Groove:
            default:
                cfg.behaviors.AddRange(new[]{
                    W(NoteBehavior.Staccatify,.40f),
                    W(NoteBehavior.HumanizeTiming,.30f),
                    W(NoteBehavior.Swingify,.20f),
                    W(NoteBehavior.DensityPulse,.10f),
                });
                cfg.maxBehaviorsStack = 2;
                break;
        }
        return true;
    }

    private static bool FillVariation(RolePhaseNoteSetConfig cfg, bool overwrite)
    {
        // If not overwriting and variation looks filled, skip
        if (!overwrite && cfg.variation != null && cfg.variation.durJitter != 0f) return false;

        // Phase-wide defaults
        var v = new VariationProfile();
        switch (cfg.phase)
        {
            case MusicalPhase.Establish:
                v.restProb = 0.08f; v.humanizeMs = 0.015f; v.durJitter = 1.00f;
                v.extensionBias = 0.25f; v.passingToneProb = 0.12f; v.neighborOrnProb = 0.10f;
                v.mutatePitch = 0.06f; v.mutateVelocity = 0.10f; v.mutateDuration = 0.08f;
                break;
            case MusicalPhase.Evolve:
                v.restProb = 0.10f; v.humanizeMs = 0.020f; v.durJitter = 1.00f;
                v.extensionBias = 0.30f; v.passingToneProb = 0.15f; v.neighborOrnProb = 0.14f;
                v.mutatePitch = 0.08f; v.mutateVelocity = 0.12f; v.mutateDuration = 0.10f;
                break;
            case MusicalPhase.Intensify:
                v.restProb = 0.04f; v.humanizeMs = 0.018f; v.durJitter = 0.95f;
                v.extensionBias = 0.35f; v.passingToneProb = 0.18f; v.neighborOrnProb = 0.16f;
                v.mutatePitch = 0.10f; v.mutateVelocity = 0.14f; v.mutateDuration = 0.12f;
                break;
            case MusicalPhase.Release:
                v.restProb = 0.16f; v.humanizeMs = 0.022f; v.durJitter = 1.05f;
                v.extensionBias = 0.28f; v.passingToneProb = 0.12f; v.neighborOrnProb = 0.12f;
                v.mutatePitch = 0.06f; v.mutateVelocity = 0.10f; v.mutateDuration = 0.10f;
                break;
            case MusicalPhase.Wildcard:
                v.restProb = 0.12f; v.humanizeMs = 0.030f; v.durJitter = 1.05f;
                v.extensionBias = 0.32f; v.passingToneProb = 0.20f; v.neighborOrnProb = 0.18f;
                v.mutatePitch = 0.12f; v.mutateVelocity = 0.16f; v.mutateDuration = 0.14f;
                break;
            case MusicalPhase.Pop:
                v.restProb = 0.06f; v.humanizeMs = 0.015f; v.durJitter = 1.00f;
                v.extensionBias = 0.26f; v.passingToneProb = 0.10f; v.neighborOrnProb = 0.10f;
                v.mutatePitch = 0.06f; v.mutateVelocity = 0.10f; v.mutateDuration = 0.08f;
                break;
            default:
                v.restProb = 0.10f; v.humanizeMs = 0.02f; v.durJitter = 1.0f;
                v.extensionBias = 0.30f; v.passingToneProb = 0.15f; v.neighborOrnProb = 0.12f;
                v.mutatePitch = 0.08f; v.mutateVelocity = 0.12f; v.mutateDuration = 0.10f;
                break;
        }

        // Role ranges
        switch (cfg.role)
        {
            case MusicalRole.Bass:    v.minMidi = 28; v.maxMidi = 55; break;
            case MusicalRole.Harmony: v.minMidi = 48; v.maxMidi = 84; break;
            case MusicalRole.Lead:    v.minMidi = 60; v.maxMidi = 96; break;
            case MusicalRole.Groove:  v.minMidi = 42; v.maxMidi = 72; break;
        }

        cfg.variation = v;
        return true;
    }

    // ===== PHASE ADJUSTMENTS (small nudges over defaults) =====
    private static void ApplyPhaseAdjustments(RolePhaseNoteSetConfig cfg)
    {
        switch (cfg.phase)
        {
            case MusicalPhase.Establish:
                if (cfg.role == MusicalRole.Bass)   Bump(cfg.rhythms, RhythmStyle.Steady, +.10f, RhythmStyle.Syncopated, -.10f);
                if (cfg.role == MusicalRole.Harmony) Bump(cfg.patterns, PatternStrategy.Drone, +.10f, PatternStrategy.ArpPingPong, -.10f);
                if (cfg.role == MusicalRole.Lead)    Bump(cfg.rhythms, RhythmStyle.Swing, +.05f, RhythmStyle.Scatter, -.05f);
                if (cfg.role == MusicalRole.Groove)  Bump(cfg.rhythms, RhythmStyle.Steady, +.10f, RhythmStyle.Dense, -.10f);
                cfg.chordsPerRegion = Mathf.Max(cfg.chordsPerRegion, 2);
                break;

            case MusicalPhase.Evolve:
                if (cfg.role == MusicalRole.Bass)   Bump(cfg.scales, ScaleType.Dorian, +.10f, ScaleType.Major, -.10f);
                if (cfg.role == MusicalRole.Harmony) Bump(cfg.patterns, PatternStrategy.ArpUp, +.10f, PatternStrategy.Drone, -.10f);
                if (cfg.role == MusicalRole.Lead)    Bump(cfg.patterns, PatternStrategy.NeighborOrnament, +.10f, PatternStrategy.SyncopatedHook, -.10f);
                if (cfg.role == MusicalRole.Groove)  Bump(cfg.rhythms, RhythmStyle.Syncopated, +.05f, RhythmStyle.Steady, 0f);
                AddOrBump(cfg.chordFunctions, "ii", +.05f);
                AddOrBump(cfg.chordFunctions, "vi", +.05f);
                cfg.chordsPerRegion = Mathf.Max(cfg.chordsPerRegion, 2);
                break;

            case MusicalPhase.Intensify:
                if (cfg.role == MusicalRole.Bass)   Bump(cfg.patterns, PatternStrategy.WalkingBass, +.10f, PatternStrategy.StaticRoot, -.10f);
                if (cfg.role == MusicalRole.Harmony) AddOrBump(cfg.behaviors, NoteBehavior.DensityPulse, +.10f);
                if (cfg.role == MusicalRole.Lead)    Bump(cfg.patterns, PatternStrategy.ArpPingPong, +.10f, PatternStrategy.NeighborOrnament, -.10f);
                if (cfg.role == MusicalRole.Groove)  Bump(cfg.rhythms, RhythmStyle.Dense, +.10f, RhythmStyle.Sparse, -.10f);
                AddOrBump(cfg.chordFunctions, "V", +.10f);
                AddOrBump(cfg.chordFunctions, "ii", +.05f);
                cfg.chordsPerRegion = 3;
                break;

            case MusicalPhase.Release:
                if (cfg.role == MusicalRole.Bass)    Bump(cfg.rhythms, RhythmStyle.Sparse, +.10f, RhythmStyle.Dense, -.10f);
                if (cfg.role == MusicalRole.Harmony) Bump(cfg.patterns, PatternStrategy.Drone, +.10f, PatternStrategy.ChordalStab, -.10f);
                if (cfg.role == MusicalRole.Lead)    AddOrBump(cfg.behaviors, NoteBehavior.Legatify, +.10f);
                if (cfg.role == MusicalRole.Groove)  Bump(cfg.rhythms, RhythmStyle.Steady, +.10f, RhythmStyle.Dense, -.10f);
                AddOrBump(cfg.chordFunctions, "IV", +.10f);
                AddOrBump(cfg.chordFunctions, "I", +.10f);
                cfg.chordsPerRegion = Mathf.Max(cfg.chordsPerRegion, 2);
                break;

            case MusicalPhase.Wildcard:
                if (cfg.role == MusicalRole.Bass)    AddOrBump(cfg.scales, ScaleType.Phrygian, +.10f);
                if (cfg.role == MusicalRole.Harmony) Bump(cfg.patterns, PatternStrategy.ArpPingPong, +.10f, PatternStrategy.Drone, -.10f);
                if (cfg.role == MusicalRole.Lead)    Bump(cfg.rhythms, RhythmStyle.Scatter, +.10f, RhythmStyle.StaccatoEighths, 0f);
                if (cfg.role == MusicalRole.Groove)  { AddOrBump(cfg.rhythms, RhythmStyle.Scatter, +.10f); AddOrBump(cfg.rhythms, RhythmStyle.Stutter, +.10f); }
                AddOrBump(cfg.chordFunctions, "bVII", +.08f);
                cfg.chordsPerRegion = Mathf.Max(cfg.chordsPerRegion, 2);
                break;

            case MusicalPhase.Pop:
                // rhythm biases
                if (cfg.role == MusicalRole.Bass)    AddOrBump(cfg.rhythms, RhythmStyle.FourOnTheFloor, +.15f);
                if (cfg.role == MusicalRole.Harmony) Bump(cfg.patterns, PatternStrategy.ChordalStab, +.15f, PatternStrategy.Drone, 0f);
                if (cfg.role == MusicalRole.Lead)    Bump(cfg.patterns, PatternStrategy.SyncopatedHook, +.15f, PatternStrategy.MelodicPhrase, +.10f);
                if (cfg.role == MusicalRole.Groove)  { AddOrBump(cfg.rhythms, RhythmStyle.Steady, +.10f); AddOrBump(cfg.rhythms, RhythmStyle.FourOnTheFloor, +.10f); }

                // chord loop: I–V–vi–IV
                cfg.chordFunctions.Clear();
                cfg.chordFunctions.AddRange(new[]{
                    W("I", .30f), W("V", .25f), W("vi", .25f), W("IV", .20f)
                });
                cfg.chordsPerRegion = 4;
                break;
        }
    }

    // ===== HELPERS =====
    private static Weighted<T> W<T>(T item, float weight) => new Weighted<T> { item = item, weight = Mathf.Clamp01(weight) };

    private static void Bump<T>(List<Weighted<T>> list, T up, float upAmt, T down = default, float downAmt = 0f)
    {
        AddOrBump(list, up, upAmt);
        if (!EqualityComparer<T>.Default.Equals(down, default) && downAmt != 0f)
            AddOrBump(list, down, downAmt);
    }

    private static void AddOrBump<T>(List<Weighted<T>> list, T item, float delta)
    {
        var w = list.FirstOrDefault(x => EqualityComparer<T>.Default.Equals(x.item, item));
        int idx = list.FindIndex(x => EqualityComparer<T>.Default.Equals(x.item, item));
        if (idx >= 0)
        {
            var nw = w; nw.weight = Mathf.Clamp01(nw.weight + delta);
            list[idx] = nw;
        }
        else
        {
            list.Add(W(item, Mathf.Clamp01(Mathf.Abs(delta))));
        }
    }
}
#endif
