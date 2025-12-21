using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
public readonly struct StepDomain
{
    public readonly int stepsPerBin;   // canonical, e.g. 16
    public readonly int bins;          // how many bins you want the set to span during generation
    public int stepsTotal => stepsPerBin * bins;

    public StepDomain(int stepsPerBin, int bins)
    {
        this.stepsPerBin = Mathf.Max(1, stepsPerBin);
        this.bins = Mathf.Max(1, bins);
    }
}
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
    int total = GetAuthoritativeStepsPerLoop(track); 
    var steps = ExpandWithinDomainFrom16(rp.Offsets, rp.LoopMultiplier, total);
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

public NoteSet Generate(InstrumentTrack track, MotifProfile motif, int entropy = 0)
{
    if (track == null)
    {
        Debug.LogWarning("[NoteSetFactory] Motif-based Generate called with null track.");
        return null;
    }

    if (motif == null)
    {
        Debug.LogWarning("[NoteSetFactory] Motif is null; falling back to phase-based path.");
        var ptm   = GameFlowManager.Instance?.phaseTransitionManager;
        var phase = (ptm != null) ? ptm.currentPhase : MusicalPhase.Establish;
        return Generate(track, phase, entropy);
    }

    // Look up the motif config for this role
    var cfg = motif.GetConfigForRole(track.assignedRole);
    if (cfg == null)
    {
        Debug.LogWarning($"[NoteSetFactory] No RoleMotifNoteSetConfig for role {track.assignedRole} in motif {motif.name}. Falling back to phase-based path.");
        var ptm   = GameFlowManager.Instance?.phaseTransitionManager;
        var phase = (ptm != null) ? ptm.currentPhase : MusicalPhase.Establish;
        return Generate(track, phase, entropy);
    }

    var rng  = SessionGenome.For($"{motif.name}-{track.assignedRole}-{track.GetInstanceID()}-n{entropy}");
    string key = $"{motif.name}/{track.assignedRole}";

    return GenerateFromMotifConfig(track, cfg, rng, key);
}

    private NoteSet GenerateFromMotifConfig(
        InstrumentTrack track,
        RoleMotifNoteSetConfig cfg,
        System.Random rng,
        string debugKey)
    {
        // --- 1) Pick high-level musical parameters from the motif config ---
        var scale  = PickWeighted(cfg.scales,   rng);
        var pat    = PickWeighted(cfg.patterns, rng);
        var rhythm = PickWeighted(cfg.rhythms,  rng);

        var chosenFns = (cfg.chordFunctions != null && cfg.chordFunctions.Count > 0)
            ? PickK(cfg.chordFunctions, Mathf.Max(1, cfg.chordsPerRegion), rng)
            : new List<string> { "I" };

        var chordSeq = RealizeChordFunctions(chosenFns, track, scale);
        if (chordSeq == null || chordSeq.Count == 0)
        {
            chordSeq = new List<Chord>
            {
                new Chord
                {
                    rootNote  = track.lowestAllowedNote,
                    intervals = new List<int> { 0, 4, 7 }
                }
            };
        }

        // --- 2) Build base step grid from rhythm pattern and extended loop length ---
        var rp    = RhythmPatterns.Patterns[rhythm];
        int total = GetAuthoritativeStepsPerLoop(track); 
        var steps = ExpandWithinDomainFrom16(rp.Offsets, rp.LoopMultiplier, total);
        if (steps.Count == 0) steps.Add(0);

        // --- 3) Apply BeatMood-based step density scaling ---
        var moodProfile = GetActiveBeatMoodProfile();
        
        steps = ApplyBeatMoodStepDensity(steps, moodProfile, rng);

        // --- 4) Generate pitches for each step, with BeatMood-influenced rest density ---
        var v     = cfg.variation;
        var notes = new List<int>(steps.Count);

        for (int i = 0; i < steps.Count; i++)
        {
            // Rest probability possibly scaled by BeatMood.noteDensityMultiplier
            float restProb = v.restProb;
            if (moodProfile != null)
            {
                float density = Mathf.Max(0f, moodProfile.noteDensityMultiplier);
                // density = 1 → no change
                // density < 1 → more rests
                // density > 1 → fewer rests
                float adjust = (1f - density) * 0.5f;
                restProb = Mathf.Clamp01(restProb + adjust);
            }

            float finalRestProb = v.restProb;  
            if (moodProfile != null)
            {
                // Lower density = more rests; higher density = fewer rests
                finalRestProb = Mathf.Clamp01(v.restProb + (1f - moodProfile.noteDensityMultiplier) * 0.4f);
            }

            if (rng.NextDouble() < finalRestProb)
            {
                notes.Add(int.MinValue);
                continue;
            }


            // Map this index into the chord sequence
            var chord = chordSeq[(i * chordSeq.Count) / Mathf.Max(1, steps.Count)];

            // Base pitch selection from pattern strategy
            var midi = GeneratePitchForStrategy(
                pat,
                chord,
                scale,
                v,
                rng,
                track.lowestAllowedNote,
                track.highestAllowedNote
            );

            // Extensions / octave motion according to variation profile
            if (rng.NextDouble() < v.extensionBias)
            {
                midi = AddExtension(midi, chord, rng);
            }
            if (rng.NextDouble() < v.octaveMoveProb)
            {
                midi += 12 * (rng.Next(0, 2) == 0 ? -1 : 1);
            }

            notes.Add(Mathf.Clamp(midi, track.lowestAllowedNote, track.highestAllowedNote));
        }

        // --- 5) Build persistent template with durations & velocities ---
        var persistent = new List<(int step, int note, int duration, float vel)>(steps.Count);
        var proxy      = new NoteSet { scale = scale, rhythmStyle = rhythm }; // plain object proxy

        for (int i = 0; i < steps.Count; i++)
        {
            int step = steps[i];
            int midi = notes[i];
            if (midi == int.MinValue) continue;

            int dur   = track.CalculateNoteDuration(step, proxy);
            dur       = (int)(dur * Mathf.Lerp(0.85f, 1.15f, (float)rng.NextDouble()) * v.durJitter);
            float vel = Mathf.Lerp(80, 115, (float)rng.NextDouble());

            persistent.Add((step, midi, dur, vel));
        }

        // --- 6) Pick behaviors and construct the NoteSet ---
        var behaviors = (cfg.behaviors != null && cfg.behaviors.Count > 0)
            ? PickUpTo(cfg.behaviors, Mathf.Max(0, cfg.maxBehaviorsStack), rng)
            : new List<NoteBehavior>(NoteBehaviorPolicy.GetDefaults(MusicalPhase.Establish, track.assignedRole));

        var ns = new NoteSet
        {
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
    private int GetAuthoritativeStepsPerLoop(InstrumentTrack track) { 
        var mood = GetActiveBeatMoodProfile(); 
        if (mood != null && mood.stepsPerLoop > 0) 
            return mood.stepsPerLoop;
        // Conservative fallback: preserve legacy behavior.
        return 16;
    }

    // --- BeatMood integration helpers ---

    private BeatMoodProfile GetActiveBeatMoodProfile()
    {
        var gfm  = GameFlowManager.Instance;
        if (gfm == null) return null;

        var drum = gfm.activeDrumTrack;
        if (drum == null) return null;

        return drum.ActiveBeatMoodProfile;
    }

    /// <summary>
    /// Adjusts the list of step positions according to the BeatMood's stepDensityMultiplier.
    /// Multiplier < 1 => fewer steps (sparser). Multiplier > 1 => more steps (denser, via duplication).
    /// Always returns a non-empty, sorted list.
    /// </summary>
    private List<int> ApplyBeatMoodStepDensity(List<int> steps, BeatMoodProfile mood, System.Random rng)
    {
        if (steps == null || steps.Count == 0)
            return new List<int> { 0 };

        if (mood == null)
            return new List<int>(steps); // no change

        float mult = Mathf.Max(0.05f, mood.stepDensityMultiplier);
        int baseCount    = steps.Count;
        int desiredCount = Mathf.RoundToInt(baseCount * mult);
        desiredCount     = Mathf.Clamp(desiredCount, 1, Mathf.Max(1, baseCount * 4));

        // No change needed
        if (desiredCount == baseCount)
            return new List<int>(steps);
        
        if (desiredCount < baseCount)
        {
            // Downsample: random subset, then sort
            // (we preserve rhythmic variety while reducing density)
            var shuffled = steps
                .Select(s => (step: s, key: (float)rng.NextDouble()))
                .OrderBy(t => t.key)
                .Take(desiredCount)
                .Select(t => t.step)
                .ToList();

            shuffled.Sort();
            return shuffled;
        }
        // Upsample: add NEW steps by interpolating/jittering into empty positions.
        // We need an authoritative domain length to know which step positions are "available".
        int domain = 16;
        // Prefer BeatMood's step grid authority.
        if (mood.stepsPerLoop > 0) domain = mood.stepsPerLoop;
        else
        {
            // Fallback to active track grid if BeatMood doesn't specify it.
            // (This preserves current runtime behavior where InstrumentTrack BinSize references drumTrack.totalSteps.)
            domain = Mathf.Max(16, domain);
        }

        // Normalize into domain and ensure uniqueness.
        var used = new HashSet<int>();
        for (int i = 0; i < steps.Count; i++)
        {
            int s = steps[i] % domain;
            if (s < 0) s += domain;
            used.Add(s);
        }
        if (used.Count == 0) used.Add(0);

        // If the domain is small, cap at domain size (cannot exceed unique positions).
        desiredCount = Mathf.Clamp(desiredCount, 1, domain);

        // Early exit if already at/above desired unique density.
        if (used.Count >= desiredCount)
            return used.OrderBy(x => x).ToList();

        // Helper to compute cyclic distance forward (a -> b) in [0, domain)
        int CycDist(int a, int b)
        {
            int d = b - a;
            if (d < 0) d += domain;
            return d;
        }

        // Insert new steps into the largest gaps first.
        // Each insertion chooses a point near the midpoint of a gap, with jitter, preferring empty slots.
        int safety = 0;
        while (used.Count < desiredCount && safety++ < domain * 8)
        {
           var ordered = used.OrderBy(x => x).ToList();
            if (ordered.Count == 0) { used.Add(0); continue; }

            // Find the largest forward gap between consecutive used points (cyclic).
            int bestA = ordered[0];
            int bestB = ordered[0];
            int bestGap = -1;

            for (int i = 0; i < ordered.Count; i++)
            {
                int a = ordered[i];
                int b = ordered[(i + 1) % ordered.Count];
                int gap = CycDist(a, b);
                if (gap > bestGap)
                {
                    bestGap = gap;
                    bestA = a;
                    bestB = b;
                }
            }

            // If all positions are filled (shouldn't happen due to clamp), break.
            if (bestGap <= 1) break;

            // Midpoint inside the gap (exclusive of endpoints).
            int half = bestGap / 2;
            int mid = (bestA + half) % domain;

            // Jitter: bias towards mid, but allow small variation.
            // The larger the gap, the larger the permissible jitter.
            int maxJitter = Mathf.Clamp(bestGap / 4, 1, 4);
            int jitter = rng.Next(-maxJitter, maxJitter + 1);
            int candidate = (mid + jitter) % domain;
            if (candidate < 0) candidate += domain;

            // If candidate occupied, walk outward from mid to find the nearest empty slot.
            if (used.Contains(candidate))
            {
                bool found = false;
                for (int r = 1; r < bestGap; r++)
                {
                    int c1 = (mid + r) % domain;
                    int c2 = (mid - r) % domain;
                    if (c2 < 0) c2 += domain;

                    if (!used.Contains(c1))
                    {
                        candidate = c1;
                        found = true;
                        break;
                    }
                    if (!used.Contains(c2))
                    {
                        candidate = c2;
                        found = true;
                        break;
                    }
                }
                if (!found) break;
            }

            used.Add(candidate);
        }

        return used.OrderBy(x => x).ToList();
    }
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
    
    NoteSet TrackToNoteSetProxy(ScaleType scale, RhythmStyle rhythm) { 
        // minimal proxy NoteSet so CalculateNoteDuration can read rhythmStyle
        return new NoteSet { scale = scale, rhythmStyle = rhythm };
    }
    /// <summary>
    /// Expands a rhythm offset pattern authored in a 16-step reference grid into the current step domain.
    /// - If totalSteps == 16: offsets are used directly.
    /// - If totalSteps == 32: offsets are scaled (e.g., 4 -> 8) so musical positions remain comparable.
    /// - loopMult repeats the pattern within the domain, but always returns steps in [0..totalSteps-1].
    /// This prevents the "multi-bar steps >= 16 then modulo-collapse" duplication bug.
    /// </summary>
    List<int> ExpandWithinDomainFrom16(int[] offsets16, int loopMult, int totalSteps)
    {
        totalSteps = Mathf.Max(1, totalSteps);
        int repeats = Mathf.Max(1, loopMult);

        var steps = new HashSet<int>();
        if (offsets16 == null || offsets16.Length == 0)
        {
            steps.Add(0);
            return steps.OrderBy(s => s).ToList();
        }

        // Scale 16-based offsets into the active domain.
        // We mod into domain to guarantee [0..totalSteps-1] and avoid any implicit "bar" semantics.
        float scale = totalSteps / 16f;

        // Repeat the same pattern within the domain by phase-shifting it.
        // If repeats == 1, it's just the base pattern.
        // If repeats > 1, we distribute repeats evenly around the loop.
        for (int r = 0; r < repeats; r++)
        {
            int phaseShift = Mathf.RoundToInt((r * totalSteps) / (float)repeats);

            for (int i = 0; i < offsets16.Length; i++)
            {
                int o16 = offsets16[i];
                // Keep authored values stable even if they exceed 0..15 (defensive).
                int scaled = Mathf.RoundToInt(o16 * scale);
                int s = (phaseShift + scaled) % totalSteps;
                if (s < 0) s += totalSteps;
                steps.Add(s);
            }
        }

        // Ensure non-empty and sorted
        if (steps.Count == 0) steps.Add(0);
        return steps.OrderBy(s => s).ToList();
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
    List<int> ProjectIntoRange(List<int> src, int lo, int hi)
    {
        var result = new List<int>();
        if (src == null || src.Count == 0) return result;

        foreach (var baseMidi in src)
        {
            // Walk octaves up/down until we land in [lo, hi] (if possible)
            for (int octaveShift = -4; octaveShift <= 4; octaveShift++)
            {
                int candidate = baseMidi + 12 * octaveShift;
                if (candidate >= lo && candidate <= hi)
                    result.Add(candidate);
            }
        }

        // Deduplicate
        result = result.Distinct().OrderBy(x => x).ToList();
        return result;
    }

    List<int> BuildScalePitches(int rootMidi, ScaleType scale, int lo, int hi)
    {
        int[] degrees;
        switch (scale)
        {
            case ScaleType.Minor:
                degrees = new[] { 0, 2, 3, 5, 7, 8, 10 };
                break;
            case ScaleType.Dorian:
                degrees = new[] { 0, 2, 3, 5, 7, 9, 10 };
                break;
            case ScaleType.Phrygian:
                degrees = new[] { 0, 1, 3, 5, 7, 8, 10 };
                break;
            case ScaleType.Lydian:
                degrees = new[] { 0, 2, 4, 6, 7, 9, 11 };
                break;
            case ScaleType.Mixolydian:
                degrees = new[] { 0, 2, 4, 5, 7, 9, 10 };
                break;
            case ScaleType.Locrian:
                degrees = new[] { 0, 1, 3, 5, 6, 8, 10 };
                break;
            case ScaleType.Major:
            default:
                degrees = new[] { 0, 2, 4, 5, 7, 9, 11 };
                break;
        }

        var result = new List<int>();
        // Search a few octaves around the chord root for scale tones in [lo, hi]
        for (int octaveShift = -4; octaveShift <= 4; octaveShift++)
        {
            int baseOct = rootMidi + 12 * octaveShift;
            foreach (var d in degrees)
            {
                int midi = baseOct + d;
                if (midi >= lo && midi <= hi)
                    result.Add(midi);
            }
        }

        return result.Distinct().OrderBy(x => x).ToList();
    }

int GeneratePitchForStrategy(
    PatternStrategy pat,
    Chord chord,
    ScaleType scale,
    VariationProfile v,
    System.Random rng,
    int lo,
    int hi)
{
    // Defensive: ensure range is sane
    if (lo > hi)
    {
        int tmp = lo;
        lo = hi;
        hi = tmp;
    }

    // ---- 1) Build chord tones in [lo, hi] ----

    var chordBaseTones = new List<int>();

    if (chord.intervals != null && chord.intervals.Count > 0)
    {
        foreach (var iv in chord.intervals)
            chordBaseTones.Add(chord.rootNote + iv);
    }
    else
    {
        // Fallback: simple major triad on the root
        chordBaseTones.Add(chord.rootNote);
        chordBaseTones.Add(chord.rootNote + 4);
        chordBaseTones.Add(chord.rootNote + 7);
    }

    var chordTonesInRange = ProjectChordTonesIntoRange(chordBaseTones, lo, hi);
    if (chordTonesInRange.Count == 0)
        chordTonesInRange.Add(Mathf.Clamp(chord.rootNote, lo, hi));

    chordTonesInRange = chordTonesInRange
        .Distinct()
        .OrderBy(x => x)
        .ToList();

    // ---- 2) Build diatonic scale tones in [lo, hi] ----

    var scalePitches = BuildScalePitchesInRange(chord.rootNote, scale, lo, hi);
    if (scalePitches.Count == 0)
        scalePitches.AddRange(chordTonesInRange);

    scalePitches = scalePitches
        .Distinct()
        .OrderBy(x => x)
        .ToList();

    // ---- 3) Small helpers to pick notes ----

    int PickFrom(List<int> list)
    {
        if (list == null || list.Count == 0)
            return Mathf.Clamp(chord.rootNote, lo, hi);
        return list[rng.Next(list.Count)];
    }

    int PickNear(List<int> list, int target)
    {
        if (list == null || list.Count == 0)
            return Mathf.Clamp(target, lo, hi);

        int best = list[0];
        int bestDist = Mathf.Abs(best - target);

        for (int i = 1; i < list.Count; i++)
        {
            int d = Mathf.Abs(list[i] - target);
            if (d < bestDist)
            {
                best = list[i];
                bestDist = d;
            }
        }

        // Occasionally choose a slightly different neighbor for variation.
        if (list.Count > 1 && rng.NextDouble() < 0.35)
        {
            int alt = list[rng.Next(list.Count)];
            if (Mathf.Abs(alt - target) <= bestDist + 2)
                best = alt;
        }

        return best;
    }

    // ---- 4) PatternStrategy → pitch behavior ----

    int midi;
    int midRange = (lo + hi) / 2;
    var chordSorted = chordTonesInRange;
    var scaleSorted = scalePitches;

    switch (pat)
    {
        // Arpeggio families: chord-focused
        case PatternStrategy.Arpeggiated:
            midi = PickFrom(chordSorted);
            break;

        case PatternStrategy.ArpUp:
        {
            int half = Mathf.Max(1, chordSorted.Count / 2);
            var upper = chordSorted.Skip(half).ToList();
            if (upper.Count == 0) upper = chordSorted;
            midi = PickFrom(upper);
            break;
        }

        case PatternStrategy.ArpDown:
        {
            int half = Mathf.Max(1, chordSorted.Count / 2);
            var lower = chordSorted.Take(half).ToList();
            if (lower.Count == 0) lower = chordSorted;
            midi = PickFrom(lower);
            break;
        }

        case PatternStrategy.ArpPingPong:
            // Randomly hit either lowest or highest chord tone
            midi = (rng.NextDouble() < 0.5)
                ? chordSorted[0]
                : chordSorted[chordSorted.Count - 1];
            break;

        // Static / drone
        case PatternStrategy.StaticRoot:
        case PatternStrategy.Drone:
            midi = chordSorted[0]; // lowest chord tone
            break;

        // Bass-y motion
        case PatternStrategy.FifthJump:
        {
            int rootCandidate = Mathf.Clamp(chord.rootNote, lo, hi);
            int fifthCandidate = Mathf.Clamp(chord.rootNote + 7, lo, hi);
            midi = (rng.NextDouble() < 0.5) ? rootCandidate : fifthCandidate;
            break;
        }

        case PatternStrategy.WalkingBass:
        {
            // Prefer lower third of scale
            int cutoff = lo + (hi - lo) / 3;
            var low = scaleSorted.Where(p => p <= cutoff).ToList();
            if (low.Count == 0) low = scaleSorted;
            midi = PickFrom(low);
            break;
        }

        case PatternStrategy.ScaleWalk:
            // Any diatonic pitch in range
            midi = PickFrom(scaleSorted);
            break;

        // Melodic foreground
        case PatternStrategy.MelodicPhrase:
            midi = PickNear(scaleSorted, midRange);
            break;

        case PatternStrategy.NeighborOrnament:
        {
            midi = PickNear(scaleSorted, midRange);
            // Add a small neighbor step with decent probability
            if (rng.NextDouble() < 0.6)
            {
                int step = (rng.Next(0, 2) == 0 ? -1 : 1) *
                           (rng.NextDouble() < 0.7 ? 1 : 2); // ±1 or ±2 semitones
                midi += step;
            }
            break;
        }

        case PatternStrategy.Ostinato:
        {
            // Repeated chord tone in mid register
            int idx = Mathf.Clamp(chordSorted.Count / 2, 0, chordSorted.Count - 1);
            midi = chordSorted[idx];
            break;
        }

        case PatternStrategy.CallAndResponse:
        {
            // Sometimes mid, sometimes upper register scale tones
            if (rng.NextDouble() < 0.5)
            {
                midi = PickNear(scaleSorted, midRange);
            }
            else
            {
                var upper = scaleSorted.Where(p => p >= midRange).ToList();
                if (upper.Count == 0) upper = scaleSorted;
                midi = PickFrom(upper);
            }
            break;
        }

        case PatternStrategy.ChordalStab:
            // High chord tone / top of voicing
            midi = chordSorted[chordSorted.Count - 1];
            break;

        // Percussive / supporting loop
        case PatternStrategy.PercussiveLoop:
            // Any chord tone; pitch is less critical
            midi = PickFrom(chordSorted);
            break;

        // Hook / rhythmic figure that should pop a bit
        case PatternStrategy.SyncopatedHook:
        {
            var upper = scaleSorted.Where(p => p >= midRange).ToList();
            if (upper.Count == 0) upper = scaleSorted;
            midi = PickFrom(upper);
            break;
        }

        case PatternStrategy.HemiolaFigure:
        {
            // Strong chord tone near mid-high register
            int target = midRange + (hi - lo) / 6;
            midi = PickNear(chordSorted, target);
            break;
        }

        case PatternStrategy.Randomized:
        default:
        {
            // Randomly choose either a chord tone or a scale tone
            if (rng.NextDouble() < 0.5)
                midi = PickFrom(chordSorted);
            else
                midi = PickFrom(scaleSorted);
            break;
        }
    }

    return Mathf.Clamp(midi, lo, hi);
}
List<int> ProjectChordTonesIntoRange(List<int> chordBaseTones, int lo, int hi)
{
    var result = new List<int>();
    if (chordBaseTones == null || chordBaseTones.Count == 0) return result;

    foreach (int baseMidi in chordBaseTones)
    {
        for (int oct = -4; oct <= 4; oct++)
        {
            int candidate = baseMidi + 12 * oct;
            if (candidate >= lo && candidate <= hi)
                result.Add(candidate);
        }
    }

    return result;
}

List<int> BuildScalePitchesInRange(int rootMidi, ScaleType scale, int lo, int hi)
{
    int[] degrees;
    switch (scale)
    {
        case ScaleType.Minor:
            degrees = new[] { 0, 2, 3, 5, 7, 8, 10 };
            break;
        case ScaleType.Dorian:
            degrees = new[] { 0, 2, 3, 5, 7, 9, 10 };
            break;
        case ScaleType.Phrygian:
            degrees = new[] { 0, 1, 3, 5, 7, 8, 10 };
            break;
        case ScaleType.Lydian:
            degrees = new[] { 0, 2, 4, 6, 7, 9, 11 };
            break;
        case ScaleType.Mixolydian:
            degrees = new[] { 0, 2, 4, 5, 7, 9, 10 };
            break;
        case ScaleType.Locrian:
            degrees = new[] { 0, 1, 3, 5, 6, 8, 10 };
            break;
        case ScaleType.Major:
        default:
            degrees = new[] { 0, 2, 4, 5, 7, 9, 11 };
            break;
    }

    var result = new List<int>();

    for (int oct = -4; oct <= 4; oct++)
    {
        int baseOct = rootMidi + 12 * oct;
        foreach (int d in degrees)
        {
            int candidate = baseOct + d;
            if (candidate >= lo && candidate <= hi)
                result.Add(candidate);
        }
    }

    return result;
}

    int AddExtension(int midi, Chord chord, System.Random rng)
    {
        int[] ext = {14, 17, 21}; // 9th/11th/13th
        int choice = ext[rng.Next(ext.Length)];
        int target = chord.rootNote + choice;
        return Mathf.Abs(target - midi) < 5 ? target : midi;
    }
    
}
