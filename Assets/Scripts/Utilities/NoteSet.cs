using System;
using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using Random = UnityEngine.Random;

public static class ChordLibrary
{
    private static readonly Dictionary<string, int[]> Formulas = new()
    {
        { "Major", new[] { 0, 4, 7 } },
        { "Minor", new[] { 0, 3, 7 } },
        { "Minor7", new[] { 0, 3, 7, 10 } },
        { "Major7", new[] { 0, 4, 7, 11 } },
        { "Sus4", new[] { 0, 5, 7 } },
        { "Fifths", new[] { 0, 7 } },
        { "Diminished", new[] { 0, 3, 6 } },
    };

    public static int[] GetRandomChord()
    {
        var keys = Formulas.Keys.ToList();
        string key = keys[Random.Range(0, keys.Count)];
        Debug.Log($"🎵 Random chord selected: {key}");
        return Formulas[key];
    }
}

public enum ScaleType
{
    Major,
    Minor,
    Mixolydian,
    Dorian,
    Phrygian,
    Lydian,
    Locrian
}

public static class ScalePatterns
{
    public static readonly Dictionary<ScaleType, int[]> Patterns = new Dictionary<ScaleType, int[]>
    {
        { ScaleType.Major,      new [] {0, 2, 4, 5, 7, 9, 11} },
        { ScaleType.Minor,      new [] {0, 2, 3, 5, 7, 8, 10} },
        { ScaleType.Mixolydian, new [] {0, 2, 4, 5, 7, 9, 10} },
        { ScaleType.Dorian,     new [] {0, 2, 3, 5, 7, 9, 10} },
        { ScaleType.Phrygian,   new [] {0, 1, 3, 5, 7, 8, 10} },
        { ScaleType.Lydian,     new [] {0, 2, 4, 6, 7, 9, 11} },
        { ScaleType.Locrian,    new [] {0, 1, 3, 5, 6, 8, 10} },
    };
}

public class RhythmPattern {
    public int[] Offsets;           // The allowed onset offsets (within a measure)
    public float DurationMultiplier; // Multiplier to extend (or shorten) the note duration
    public int LoopMultiplier;       // Multiplier to extend the overall loop length
}

public static class RhythmPatterns
{
    public static readonly Dictionary<RhythmStyle, RhythmPattern> Patterns =
        new Dictionary<RhythmStyle, RhythmPattern>
        {
            { RhythmStyle.FourOnTheFloor, new RhythmPattern {
                Offsets = new[] { 0, 4, 8, 12 }, DurationMultiplier = 1f, LoopMultiplier = 1 } },
            { RhythmStyle.Syncopated,     new RhythmPattern {
                Offsets = new[] { 2, 3, 6, 7, 10, 11, 14, 15 }, DurationMultiplier = 0.8f, LoopMultiplier = 1 } },
            { RhythmStyle.Swing,          new RhythmPattern {
                Offsets = new[] { 0, 3, 4, 7, 8, 11, 12, 15 }, DurationMultiplier = 1f, LoopMultiplier = 1 } }, 
            { RhythmStyle.Sparse,         new RhythmPattern {
                Offsets = new[] { 0, 8 }, DurationMultiplier = 2f, LoopMultiplier = 2 } },
            { RhythmStyle.Dense,          new RhythmPattern {
                Offsets = Enumerable.Range(0, 16).ToArray(), DurationMultiplier = 0.7f, LoopMultiplier = 1 } },
            { RhythmStyle.Steady,         new RhythmPattern {
                Offsets = new[] { 0, 4, 8, 12 }, DurationMultiplier = 1f, LoopMultiplier = 1 } },
            { RhythmStyle.Triplet,        new RhythmPattern {
                Offsets = new[] { 0, 5, 10, 15 }, DurationMultiplier = 0.9f, LoopMultiplier = 1 } },
            { RhythmStyle.PulseBuild,     new RhythmPattern {
                Offsets = new[] { 0, 8, 4, 12, 2, 10 }, DurationMultiplier = 1.2f, LoopMultiplier = 1 } },
            { RhythmStyle.Stutter,        new RhythmPattern {
                Offsets = new[] { 0, 1, 2, 3, 4, 5, 6, 7 }, DurationMultiplier = 0.6f, LoopMultiplier = 1 } },
            { RhythmStyle.Scatter,        new RhythmPattern {
                Offsets = new[] { 1, 5, 7, 10, 13, 14 }, DurationMultiplier = 1f, LoopMultiplier = 1 } },
            { RhythmStyle.StaccatoEighths, new RhythmPattern {
                Offsets = new[] {0,2,4,6,8,10,12,14}, DurationMultiplier = 0.5f, LoopMultiplier = 1 } },
            { RhythmStyle.DroneFade, new RhythmPattern {
                Offsets = new[] {0}, DurationMultiplier = 4.0f, LoopMultiplier = 1 } },
            { RhythmStyle.TripletFlare, new RhythmPattern {
                Offsets = new[] {0,5,10,15}, DurationMultiplier = 0.9f, LoopMultiplier = 1 } },
            { RhythmStyle.CallResponse, new RhythmPattern {
                Offsets = new[] {0,4,8,12}, DurationMultiplier = 0.8f, LoopMultiplier = 1 } },
            { RhythmStyle.PulseBuild2Bar, new RhythmPattern {
                Offsets = new[] {0,8,4,12,2,6,10,14}, DurationMultiplier = 0.9f, LoopMultiplier = 2 } },
            { RhythmStyle.Breakbeat,      new RhythmPattern
            {
                Offsets = new[] { 0, 2, 5, 9, 11, 14 }, DurationMultiplier = 1.1f, LoopMultiplier = 1 } }
        };
}

public class NoteSet
{
    public int rootMidi;             // e.g. 24 for C1
    public ScaleType scale;          // e.g. Major or Minor
    public ChordPattern chordPattern = ChordPattern.RootTriad;
    public NoteBehavior noteBehavior;
    public RhythmStyle rhythmStyle = RhythmStyle.FourOnTheFloor;
    public InstrumentTrack assignedInstrumentTrack;
    public PatternStrategy patternStrategy;              // NEW: selected algorithm for this NoteSet
    public ScaleType scaleType => scale;                 // alias for clarity with factory
    public List<Chord> chordRegion;                      // NEW: realized chord region for this loop
    public List<NoteBehavior> behaviorsSeed;             // NEW: candidates to apply via rings/auto-variation
    public List<(int step,int note,int duration,float vel)> persistentTemplate; // NEW: initial phrase

    [HideInInspector] public List<int> ghostNoteSequence = new List<int>();

    private int? _dominantNote;
    private List<int> _allowedSteps = new List<int>(); // ✅ The valid spawn steps for notes
    private List<int> _notes = new List<int>(); // ✅ List of possible note values
    private int _lowestNote;           // e.g. 12
    private int _highestNote;          // e.g. 60
    private int _grooveIndex;
    private readonly int[] _accentPattern = { 1, 0, 0, 1 }; // Accent on 1st and 4th

    public void Initialize(InstrumentTrack track, int totalSteps)
    {
        // If a persistentTemplate was supplied (riff-authoritative), use it as the source of truth.
        if (persistentTemplate != null && persistentTemplate.Count > 0)
        {
            BuildTemplateLookup(track, totalSteps);
            BuildAllowedStepsFromTemplate(totalSteps);

            // Optional: keep _notes populated for any systems that expect it (UI/debug/etc).
            BuildNotesFromTemplate(track);
            return;
        }

        // Default generative path
        BuildNotesFromKey(track);
        BuildAllowedStepsFromStyle(totalSteps);
    }
    // Riff-authoritative lookup: step -> (note,dur,vel)
private readonly Dictionary<int, (int note, int dur, float vel)> _templateByStep
    = new Dictionary<int, (int note, int dur, float vel)>();

private void BuildTemplateLookup(InstrumentTrack track, int totalSteps)
{
    _templateByStep.Clear();

    for (int i = 0; i < persistentTemplate.Count; i++)
    {
        var t = persistentTemplate[i];
        int step = t.step;

        if (step < 0 || step >= totalSteps)
            continue;

        int note = t.note;
        if (track != null)
            note = Mathf.Clamp(note, track.lowestAllowedNote, track.highestAllowedNote);

        // If multiple notes share a step, keep the "strongest" by velocity.
        if (_templateByStep.TryGetValue(step, out var existing))
        {
            if (t.vel > existing.vel)
                _templateByStep[step] = (note, t.duration, t.vel);
        }
        else
        {
            _templateByStep.Add(step, (note, t.duration, t.vel));
        }
    }
}

private void BuildAllowedStepsFromTemplate(int totalSteps)
{
    _allowedSteps.Clear();

    // Keep deterministic order: ascending by step.
    // No LINQ: small list, simple sort.
    var steps = new List<int>(_templateByStep.Keys);
    steps.Sort();

    for (int i = 0; i < steps.Count; i++)
    {
        int s = steps[i];
        if (s >= 0 && s < totalSteps)
            _allowedSteps.Add(s);
    }
}

private void BuildNotesFromTemplate(InstrumentTrack track)
{
    _notes.Clear();

    // Populate with unique notes from the template (useful for any systems expecting _notes)
    foreach (var kvp in _templateByStep)
    {
        int n = kvp.Value.note;
        if (track != null)
            n = Mathf.Clamp(n, track.lowestAllowedNote, track.highestAllowedNote);

        // Avoid LINQ/HashSet allocations (tiny list)
        bool exists = false;
        for (int i = 0; i < _notes.Count; i++)
        {
            if (_notes[i] == n) { exists = true; break; }
        }
        if (!exists) _notes.Add(n);
    }

    _notes.Sort();
}
    public int GetRootNote()
    {
        return Mathf.Clamp(rootMidi, assignedInstrumentTrack.lowestAllowedNote, assignedInstrumentTrack.highestAllowedNote);
    }
    public List<int> GetNoteList()
    {
        return _notes;
    }
    public List<int> GetSortedNoteList()
    {
        return GetNoteList().OrderBy(n => n).ToList();
    }
    public List<int> GetStepList()
    {
        return _allowedSteps;
    }
    public int GetNoteForPhaseAndRole(InstrumentTrack track, int step)
    {
        // Riff-authoritative: step->note comes directly from the template
        if (persistentTemplate != null && persistentTemplate.Count > 0)
        {
            if (_templateByStep.TryGetValue(step, out var e))
                return e.note;

            // If called on a non-authored step, fail gracefully:
            // fall back to nearest earlier authored step, otherwise first authored.
            int bestStep = int.MinValue;
            foreach (var k in _templateByStep.Keys)
            {
                if (k <= step && k > bestStep) bestStep = k;
            }
            if (bestStep != int.MinValue)
                return _templateByStep[bestStep].note;

            foreach (var k in _templateByStep.Keys)
                return _templateByStep[k].note;
        }
        var strategy = this.patternStrategy; // prefer the NoteSet’s own strategy; fallback handled below
        if (!Enum.IsDefined(typeof(PatternStrategy), strategy)) strategy = PatternStrategy.Arpeggiated; // safe default
        switch (strategy)
        {
            case PatternStrategy.StaticRoot:
                return GetRootNote();
            case PatternStrategy.WalkingBass:
                return GetNextWalkingNote(step);
            case PatternStrategy.MelodicPhrase:
                return GetPhraseNote(step);
            case PatternStrategy.PercussiveLoop:
                return GetGrooveNote(out _);
            case PatternStrategy.Randomized: // treated the same now
                return GetRandomNote();
            case PatternStrategy.Drone:
                return GetSustainedNote();
            case PatternStrategy.Arpeggiated:
            default:
                return GetNextArpeggiatedNote(step);
        }
    }
    public void AdvanceChord()
    {
        List<ChordPattern> allPatterns = Enum.GetValues(typeof(ChordPattern)).Cast<ChordPattern>().ToList();
        int currentIndex = allPatterns.IndexOf(chordPattern);
        chordPattern = allPatterns[(currentIndex + 1) % allPatterns.Count]; // Cycle through all chords

        Debug.Log($"Chord progression updated: {chordPattern}");
    }
    public int[] GetRandomChordOffsets()
    {
        return ChordLibrary.GetRandomChord();
    }
    public void ShiftRoot(InstrumentTrack track, int semitoneDelta)
    {
        rootMidi += semitoneDelta;
        rootMidi = Mathf.Clamp(rootMidi, track.lowestAllowedNote, track.highestAllowedNote);
        BuildNotesFromKey(track); // rebuild scale
    }
    public void ChangeNoteBehavior(InstrumentTrack track, NoteBehavior newBehavior)
    {
        noteBehavior = newBehavior;
        BuildNotesFromKey(track); // may change octave/root
    }
    public int GetClosestVoiceLeadingNote(int currentNote, List<int> nextChordNotes)
    {
        return nextChordNotes.OrderBy(n => Mathf.Abs(n - currentNote)).First();
    }
    public int GetNextArpeggiatedNote(int stepIndex)
    {
        if (_notes.Count == 0)
        {
            Debug.LogError("❌ No notes available in NoteSet!");
            return rootMidi; // Default to root note if empty
        }

        int note = _notes[stepIndex % _notes.Count];

        // ✅ Ensure the note is within the instrument’s range
        int lowestNote = assignedInstrumentTrack.lowestAllowedNote;
        int highestNote = assignedInstrumentTrack.highestAllowedNote;
        if (note < lowestNote || note > highestNote)
        {
            Debug.LogWarning($"❌ Selected note {note} out of range ({lowestNote} - {highestNote}). Clamping.");
            note = Mathf.Clamp(note, lowestNote, highestNote);
        }

//        Debug.Log($"🎹 Returning arpeggiated note {note} for step {stepIndex}");
        return note;
    }

    private void BuildNotesFromKey(InstrumentTrack track)
    {
        _notes.Clear();
        int[] pattern = ScalePatterns.Patterns[scale];

        // Adjust the root note octave **and determine playable range**
        int adjustedRootMidi = AdjustRootOctave(track, rootMidi);
        int lowestNote = track.lowestAllowedNote; // Min range: 1 octave below root
        int highestNote = track.highestAllowedNote; // Max range: 1 octave above root

        // Ensure dominant note is within the range
        if (_dominantNote.HasValue)
        {
            _dominantNote = Mathf.Clamp(_dominantNote.Value, lowestNote, highestNote);
        }
        
        for (int pitch = lowestNote; pitch <= highestNote; pitch++)
        {
            int semitoneAboveRoot = (pitch - adjustedRootMidi) % 12;
            if (semitoneAboveRoot < 0) semitoneAboveRoot += 12;

            if (Array.IndexOf(pattern, semitoneAboveRoot) >= 0)
            {
                _notes.Add(pitch);
            }
        }
    }
    private int GetNextWalkingNote(int stepIndex)
    {
        if (_notes.Count == 0) return rootMidi;
        var sortedNotes = GetSortedNoteList();
        return sortedNotes[stepIndex % sortedNotes.Count];
    }
    private int GetPhraseNote(int phraseIndex)
    {
        if (ghostNoteSequence != null && ghostNoteSequence.Count > 0)
        {
            return ghostNoteSequence[phraseIndex % ghostNoteSequence.Count];
        }
        return GetNextArpeggiatedNote(phraseIndex);
    }
    private int GetSustainedNote()
    {
        List<int> chord = GetChordNotes();
        return chord[0]; // Root of chord, intended to be held
    }
    private int GetGrooveNote(out float velocity)
    {
        if (_notes.Count == 0)
        {
            velocity = 0.5f;
            return rootMidi;
        }

        int index = _grooveIndex % _notes.Count;
        int patternIndex = _grooveIndex % _accentPattern.Length;

        // Base note selection
        int note = _notes[index];

        // Accent velocity (you can scale this more expressively)
        velocity = _accentPattern[patternIndex] == 1 ? 1.0f : 0.6f;

        _grooveIndex++;
        return note;
    }
    private int GetRandomNote()
    {
        if (_notes.Count == 0) return rootMidi;
        return _notes[Random.Range(0, _notes.Count)];
    }
// Replace your AdjustRootOctave with this (and keep the same method name/signature)
private int AdjustRootOctave(InstrumentTrack track, int baseRoot)
{
    if (track == null)
        return baseRoot;

    int lo   = track.lowestAllowedNote;
    int hi   = track.highestAllowedNote;
    int mid  = (lo + hi) / 2;

    // 1) Start from base root; add a role bias in octaves
    int semitoneBias = 0;
    switch (track.assignedRole)
    {
        case MusicalRole.Bass:    semitoneBias -= 12; break; // down 1 oct
        case MusicalRole.Lead:    semitoneBias += 12; break; // up 1 oct
        case MusicalRole.Harmony: semitoneBias += 0;  break; // centered
        case MusicalRole.Groove:  semitoneBias += 0;  break; // centered
    }

    // 2) Apply behavior-derived octave hints (handles legacy aliases too)
    semitoneBias += GetBehaviorOctaveBias();

    int root = baseRoot + semitoneBias;

    // 3) Fold/clamp into the playable range
    // Prefer folding by octaves to keep pitch class; then clamp as a last resort.
    while (root < lo) root += 12;
    while (root > hi) root -= 12;

    // If still out of range (very narrow instruments), clamp.
    root = Mathf.Clamp(root, lo, hi);

    // 4) Nudge toward the middle if the range is wide and we landed near edges
    // (optional – keeps things from sitting on extremes)
    if (hi - lo >= 24)
    {
        if (root - lo < 2)  root += 12;
        if (hi - root < 2)  root -= 12;
        root = Mathf.Clamp(root, lo, hi);
    }

    return root;
}

    private int GetBehaviorOctaveBias()
    {
        int bias = 0;
        var source = behaviorsSeed != null && behaviorsSeed.Count > 0
            ? behaviorsSeed
            : new List<NoteBehavior> { NoteBehavior.None };

        foreach (var raw in source)
        foreach (var b in NoteBehaviorPolicy.MapLegacy(raw))
        {
            switch (b)
            {
                case NoteBehavior.Drone: bias -= 12; break;
                case NoteBehavior.Lead:  bias += 12; break;
                case NoteBehavior.Hook:  bias += 12; break;
                case NoteBehavior.Bass:  bias -= 12; break;
                // others neutral
            }
        }
        return bias;
    }

    private List<int> GetChordNotes()
    {
        int root = rootMidi;
        switch (chordPattern)
        {
            case ChordPattern.RootTriad:
                return new List<int> { root, root + 4, root + 7 }; // Major triad (C, E, G)
            case ChordPattern.SeventhChord:
                return new List<int> { root, root + 4, root + 7, root + 10 }; // C7 (C, E, G, Bb)
            case ChordPattern.Fifths:
                return new List<int> { root, root + 7 }; // Open power chord (C, G)
            case ChordPattern.Sus4:
                return new List<int> { root, root + 5, root + 7 }; // Suspended chord (C, F, G)
            case ChordPattern.Arpeggiated:
                return new List<int> { root, root + 4, root + 7, root + 4 }; // Up and down
            default:
                return new List<int> { root, root + 4, root + 7 }; // Default to major triad
        }
    }
    private void BuildAllowedStepsFromStyle(int totalSteps)
    {
        //        Debug.Log($"NoteSet '{name}' building allowed steps for style {rhythmStyle} over {totalSteps} steps.");

        _allowedSteps.Clear();

        // Retrieve the extended pattern for the chosen style.
        RhythmPattern pattern = RhythmPatterns.Patterns[rhythmStyle];
        if (pattern == null)
        {
            Debug.LogError($"❌ RhythmPattern not found for style {rhythmStyle}");
            return;
        }

        if (pattern.Offsets == null || pattern.Offsets.Count() == 0)
        {
            Debug.LogError($"❌ RhythmPattern '{rhythmStyle}' has no offsets defined.");
            return;
        }

        // Adjust totalSteps if you want to double (or otherwise scale) the loop length.
        int effectiveTotalSteps = totalSteps * pattern.LoopMultiplier;

        int stepsPerBar = 16; // e.g., 16, 32, 64, 128, etc.
        // Suppose we treat the effective loop as bars of 16 steps (you can parameterize this too)
        int bars = effectiveTotalSteps / stepsPerBar; // e.g., 64 becomes 64 or 128 if doubled

        for (int barIndex = 0; barIndex < bars; barIndex++)
        {
            int barStart = barIndex * stepsPerBar;
            foreach (int offset in pattern.Offsets)
            {
                int step = barStart + offset;
                if (step < effectiveTotalSteps)
                    _allowedSteps.Add(step);
            }
        }
    }

}
