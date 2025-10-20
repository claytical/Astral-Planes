using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public class NoteSetFactory : MonoBehaviour
{
    [Header("Config Library (ScriptableObject)")]
    public NoteSetConfigLibrary configLibrary;

public NoteSet Generate(InstrumentTrack track, MusicalPhase phase, int entropy = 0)
{
    var cfg = configLibrary?.GetConfig(track.assignedRole, phase);
    if (cfg == null) { Debug.LogWarning($"Missing config for {track.assignedRole}/{phase}"); return null; }

    var rng = SessionGenome.For($"{phase}-{track.assignedRole}-{track.GetInstanceID()}-n{entropy}");

    // (weighted picks + guards as you have now)
    var scale  = PickWeighted(cfg.scales, rng);
    var pat    = PickWeighted(cfg.patterns, rng);
    var rhythm = PickWeighted(cfg.rhythms, rng);

    var chosenFns = (cfg.chordFunctions?.Count > 0)
        ? PickK(cfg.chordFunctions, Mathf.Max(1, cfg.chordsPerRegion), rng)
        : new List<string>{ "I" };

    var chordSeq = RealizeChordFunctions(chosenFns, track, scale);
    if (chordSeq == null || chordSeq.Count == 0)
        chordSeq = new List<Chord>{ new Chord{ rootNote = track.lowestAllowedNote, intervals = new List<int>{0,4,7} } };

    var rp    = RhythmPatterns.Patterns[rhythm];
    int total = Mathf.Max(16, track.GetTotalSteps());
    var steps = ExpandAcrossBars(rp.Offsets, rp.LoopMultiplier, total);
    if (steps.Count == 0) steps.Add(0);

    var v = cfg.variation;
    var notes = new List<int>(steps.Count);
    for (int i = 0; i < steps.Count; i++)
    {
        if (rng.NextDouble() < v.restProb) { notes.Add(int.MinValue); continue; }
        var chord = chordSeq[(i * chordSeq.Count) / Mathf.Max(1, steps.Count)];
        var midi = GeneratePitchForStrategy(pat, chord, scale, v, rng, track.lowestAllowedNote, track.highestAllowedNote);
        if (rng.NextDouble() < v.extensionBias)  midi = AddExtension(midi, chord, rng);
        if (rng.NextDouble() < v.octaveMoveProb) midi += 12 * (rng.Next(0,2)==0 ? -1 : 1);
        notes.Add(Mathf.Clamp(midi, track.lowestAllowedNote, track.highestAllowedNote));
    }

    var persistent = new List<(int step,int note,int duration,float vel)>(steps.Count);
    var proxy = new NoteSet { scale = scale, rhythmStyle = rhythm }; // plain object proxy
    for (int i = 0; i < steps.Count; i++)
    {
        int step = steps[i], midi = notes[i];
        if (midi == int.MinValue) continue;
        int dur = track.CalculateNoteDuration(step, proxy);
        dur = (int)(dur * Mathf.Lerp(0.85f, 1.15f, (float)rng.NextDouble()) * v.durJitter);
        float vel = Mathf.Lerp(80, 115, (float)rng.NextDouble());
        persistent.Add((step, midi, dur, vel));
    }

    var behaviors = (cfg.behaviors != null && cfg.behaviors.Count > 0)
        ? PickUpTo(cfg.behaviors, Mathf.Max(0, cfg.maxBehaviorsStack), rng)
        : new List<NoteBehavior>(NoteBehaviorPolicy.GetDefaults(phase, track.assignedRole));

    var ns = new NoteSet {
        assignedInstrumentTrack = track,
        rootMidi           = track.lowestAllowedNote,
        scale              = scale,
        patternStrategy    = pat,
        rhythmStyle        = rhythm,
        chordRegion        = chordSeq,
        behaviorsSeed      = behaviors,
        persistentTemplate = persistent
    };

    ns.Initialize(track, total);
    return ns;
}


    // ---------- helpers ----------
    T PickWeighted<T>(List<Weighted<T>> list, System.Random rng)
    {
        if (list == null || list.Count == 0) throw new Exception("Empty weighted list");
        float sum = list.Sum(w => Mathf.Max(0f, w.weight));
        if (sum <= 0f) return list[rng.Next(list.Count)].item;

        float r = (float)rng.NextDouble() * sum;
        float acc = 0f;
        foreach (var w in list)
        {
            float ww = Mathf.Max(0f, w.weight);
            acc += ww;
            if (r <= acc) return w.item;
        }
        return list[list.Count - 1].item;
    }

    List<T> PickK<T>(List<Weighted<T>> list, int k, System.Random rng)
    {
        if (list == null || list.Count == 0) return new List<T>();
        k = Mathf.Clamp(k, 1, list.Count);
        // weighted sample w/o replacement: shuffle by weight*rand and take first k
        var shuffled = list
            .Select(w => (w.item, key: (float)rng.NextDouble() * Mathf.Max(0.0001f, w.weight)))
            .OrderByDescending(t => t.key)
            .Take(k)
            .Select(t => t.item)
            .ToList();
        return shuffled;
    }

    List<NoteBehavior> PickUpTo(List<Weighted<NoteBehavior>> list, int max, System.Random rng)
    {
        if (list == null || list.Count == 0 || max <= 0) return new List<NoteBehavior>();
        int k = Mathf.Clamp(Mathf.RoundToInt((float)rng.NextDouble() * max), 0, Mathf.Min(max, list.Count));
        if (k == 0) return new List<NoteBehavior>();
        return PickK(list, k, rng);
    }

    List<int> ExpandAcrossBars(int[] offsets, int loopMult, int baseSteps)
    {
        int total = baseSteps * Mathf.Max(1, loopMult);
        int bars  = Mathf.Max(1, total / 16);
        var steps = new List<int>(offsets.Length * bars);
        for (int bar = 0; bar < bars; bar++)
        {
            int start = bar * 16;
            for (int i = 0; i < offsets.Length; i++)
            {
                int s = start + offsets[i];
                if (s < total) steps.Add(s);
            }
        }
        return steps;
    }
    NoteSet TrackToNoteSetProxy(ScaleType scale, RhythmStyle rhythm) { 
        // minimal proxy NoteSet so CalculateNoteDuration can read rhythmStyle
        return new NoteSet { scale = scale, rhythmStyle = rhythm };
    }

    List<Chord> RealizeChordFunctions(List<string> fns, InstrumentTrack track, ScaleType scale)
    {
        // simple diatonic mapping; expand as you wish
        var map = new Dictionary<string, (int deg, string quality)> {
            {"I",(0,"Major")}, {"ii",(1,"Minor")}, {"iii",(2,"Minor")},
            {"IV",(3,"Major")},{"V",(4,"Major")}, {"vi",(5,"Minor")}, {"vii°",(6,"Diminished")},
            {"bVII",(6,"Major")}
        };

        int root = track.lowestAllowedNote;
        var chords = new List<Chord>();
        foreach (var fn in fns)
        {
            if (!map.TryGetValue(fn, out var t)) continue;
            int[] scaleInts = ScalePatterns.Patterns[scale];
            int rel = Mathf.Clamp(t.deg, 0, scaleInts.Length - 1);
            int chordRoot = root + scaleInts[rel];

            int[] ivals = t.quality switch
            {
                "Minor"      => new[] {0,3,7},
                "Major"      => new[] {0,4,7},
                "Diminished" => new[] {0,3,6},
                _            => new[] {0,4,7}
            };
            chords.Add(new Chord { rootNote = chordRoot, intervals = ivals.ToList() });
        }
        if (chords.Count == 0)
            chords.Add(new Chord { rootNote = root, intervals = new List<int>{0,4,7} });

        return chords;
    }

    int GeneratePitchForStrategy(
        PatternStrategy pat, Chord chord, ScaleType scale, VariationProfile v, System.Random rng, int lo, int hi)
    {
        // ... your existing strategy logic (unchanged) ...
        // keep it returning a midi in [lo, hi]
        int midi = chord.rootNote; // placeholder for brevity
        return Mathf.Clamp(midi, lo, hi);
    }

    int AddExtension(int midi, Chord chord, System.Random rng)
    {
        int[] ext = {14, 17, 21}; // 9th/11th/13th
        int choice = ext[rng.Next(ext.Length)];
        int target = chord.rootNote + choice;
        return Mathf.Abs(target - midi) < 5 ? target : midi;
    }
}
