using System;
using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using Random = UnityEngine.Random;
public static class ChordLibrary
{
    public static readonly Dictionary<string, int[]> Formulas = new()
    {
        { "Major",     new[] { 0, 4, 7 } },
        { "Minor",     new[] { 0, 3, 7 } },
        { "Minor7",    new[] { 0, 3, 7, 10 } },
        { "Major7",    new[] { 0, 4, 7, 11 } },
        { "Sus4",      new[] { 0, 5, 7 } },
        { "Fifths",    new[] { 0, 7 } },
        { "Diminished",new[] { 0, 3, 6 } },
    };

    public static int[] GetRandomChord()
    {
        var keys = Formulas.Keys.ToList();
        string key = keys[Random.Range(0, keys.Count)];
        Debug.Log($"🎵 Random chord selected: {key}");
        return Formulas[key];
    }

    public static int[] GetChord(string name)
    {
        if (Formulas.TryGetValue(name, out var chord))
            return chord;

        return Formulas["Major"]; // fallback
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
            { RhythmStyle.FourOnTheFloor, new RhythmPattern { Offsets = new [] {0, 4, 8, 12}, DurationMultiplier = 1f, LoopMultiplier = 1 } },
            { RhythmStyle.Syncopated,     new RhythmPattern { Offsets = new [] {2, 3, 6, 7, 10, 11, 14, 15}, DurationMultiplier = 0.8f, LoopMultiplier = 1 } },
            { RhythmStyle.Swing,          new RhythmPattern { Offsets = new [] {0, 3, 4, 7, 8, 11, 12, 15}, DurationMultiplier = 1f, LoopMultiplier = 1 } },
            { RhythmStyle.Sparse,         new RhythmPattern { Offsets = new [] {0, 8}, DurationMultiplier = 2f, LoopMultiplier = 2 } },
            { RhythmStyle.Dense,          new RhythmPattern { Offsets = Enumerable.Range(0,16).ToArray(), DurationMultiplier = 0.7f, LoopMultiplier = 1 } }
        };
}

[System.Serializable]
public class WeightedNote
{
    [Range(0,127)]
    public int noteValue; // 🎵 MIDI note number (0-127)
    public int weight = 1; // 📊 Weight (higher = more frequent)
}
[System.Serializable]
public class WeightedDuration
{
    public int durationTicks; // ⏳ Duration in MIDI ticks
    public int weight = 1; // 📊 Higher weight = more frequent selection
}
public class NoteSet : MonoBehaviour
{
    public int rootMidi;             // e.g. 24 for C1
    public int finalRootMidi;
    public int? dominantNote;
    public ScaleType scale;          // e.g. Major or Minor
    public ChordPattern chordPattern = ChordPattern.RootTriad;
    public NoteBehavior noteBehavior;
    public RhythmStyle rhythmStyle = RhythmStyle.FourOnTheFloor;
    public InstrumentTrack assignedInstrumentTrack; // ✅ Each NoteSet is now tied to an InstrumentTrack
    
    [Header("Remix System")]
    public RemixUtility remixUtility;

    [HideInInspector] public List<int> ghostNoteSequence = new List<int>();

    private List<int> allowedSteps = new List<int>(); // ✅ The valid spawn steps for notes
    private List<int> notes = new List<int>(); // ✅ List of possible note values
    private int lowestNote;           // e.g. 12
    private int highestNote;          // e.g. 60
//    private float dominantWeight = 2f;
    private int grooveIndex = 0;
    private readonly int[] accentPattern = { 1, 0, 0, 1 }; // Accent on 1st and 4th

    public void BuildNotesFromKey()
    {
        notes.Clear();
        int[] pattern = ScalePatterns.Patterns[scale];

        // Adjust the root note octave **and determine playable range**
        int adjustedRootMidi = AdjustRootOctave(rootMidi);
        int lowestNote = assignedInstrumentTrack.lowestAllowedNote; // Min range: 1 octave below root
        int highestNote = assignedInstrumentTrack.highestAllowedNote; // Max range: 1 octave above root

        // Ensure dominant note is within the range
        if (dominantNote.HasValue)
        {
            dominantNote = Mathf.Clamp(dominantNote.Value, lowestNote, highestNote);
        }
        
        for (int pitch = lowestNote; pitch <= highestNote; pitch++)
        {
            int semitoneAboveRoot = (pitch - adjustedRootMidi) % 12;
            if (semitoneAboveRoot < 0) semitoneAboveRoot += 12;

            if (System.Array.IndexOf(pattern, semitoneAboveRoot) >= 0)
            {
                notes.Add(pitch);
            }
        }
    }
    public int GetRootNote()
    {
        return Mathf.Clamp(rootMidi, assignedInstrumentTrack.lowestAllowedNote, assignedInstrumentTrack.highestAllowedNote);
    }
    public int GetNextWalkingNote(int stepIndex)
    {
        if (notes.Count == 0) return rootMidi;
        var sortedNotes = GetSortedNoteList();
        return sortedNotes[stepIndex % sortedNotes.Count];
    }
    public int GetPhraseNote(int phraseIndex)
    {
        if (ghostNoteSequence != null && ghostNoteSequence.Count > 0)
        {
            return ghostNoteSequence[phraseIndex % ghostNoteSequence.Count];
        }
        return GetNextArpeggiatedNote(phraseIndex);
    }
    public int GetPercussiveHit()
    {
        if (notes.Count == 0) return rootMidi;
        return notes[Random.Range(0, notes.Count)];
    }
    public int GetSustainedNote()
    {
        List<int> chord = GetChordNotes();
        return chord[0]; // Root of chord, intended to be held
    }

    public int GetGrooveNote(out float velocity)
    {
        if (notes.Count == 0)
        {
            velocity = 0.5f;
            return rootMidi;
        }

        int index = grooveIndex % notes.Count;
        int patternIndex = grooveIndex % accentPattern.Length;

        // Base note selection
        int note = notes[index];

        // Accent velocity (you can scale this more expressively)
        velocity = accentPattern[patternIndex] == 1 ? 1.0f : 0.6f;

        grooveIndex++;
        return note;
    }

    public int GetRandomNote()
    {
        if (notes.Count == 0) return rootMidi;
        return notes[Random.Range(0, notes.Count)];
    }

    public void GenerateGhostNoteSequence()
    {
        if (remixUtility == null || notes.Count == 0)
        {
            ghostNoteSequence.Clear();
            return;
        }

        ghostNoteSequence = remixUtility.GeneratePhrase(this);
    }

    public int GetPitchIndexInSet(int pitch)
    {
        var sorted = GetSortedNoteList();
        return sorted.IndexOf(pitch);
    }

    public List<int> GetSortedNoteList()
    {
        return GetNoteList().OrderBy(n => n).ToList();
    }
    public bool IsAccentStep(int step)
    {
        // Simple 16-step accent pattern: accents on downbeats
        // You can swap this for a pattern based on rhythmStyle if available
        return step % 4 == 0; // accents every 4 steps (0, 4, 8, 12)
    }

    public int GetNoteForPhaseAndRole(InstrumentTrack track, int step)
    {
        var currentPhase = track.drumTrack.currentPhase; // assuming this exists on the same track ref
        var strategy = MusicalPhaseLibrary.GetGhostStrategyForRole(currentPhase, track.assignedRole);
        switch (strategy)
        {
            case GhostPatternStrategy.StaticRoot:
                return GetRootNote();
            case GhostPatternStrategy.WalkingBass:
                return GetNextWalkingNote(step);
            case GhostPatternStrategy.MelodicPhrase:
                return GetPhraseNote(step);
            case GhostPatternStrategy.PercussiveLoop:
                return GetGrooveNote(out _);
            case GhostPatternStrategy.Randomized: // treated the same now
                return GetRandomNote();
            case GhostPatternStrategy.Drone:
                return GetSustainedNote();
            case GhostPatternStrategy.Arpeggiated:
            default:
                return GetNextArpeggiatedNote(step);
        }
    }

    public int[] GetRandomChordOffsets()
    {
        return ChordLibrary.GetRandomChord();
    }

    public void ShiftRoot(int semitoneDelta)
    {
        rootMidi += semitoneDelta;
        rootMidi = Mathf.Clamp(rootMidi, assignedInstrumentTrack.lowestAllowedNote, assignedInstrumentTrack.highestAllowedNote);
        BuildNotesFromKey(); // rebuild scale
    }

    public void ChangeNoteBehavior(NoteBehavior newBehavior)
    {
        noteBehavior = newBehavior;
        BuildNotesFromKey(); // may change octave/root
    }

    private int AdjustRootOctave(int baseRoot)
    {
        int adjustedRoot = baseRoot;

        // ✅ Ensure that currentNoteSet has an assigned track
        if (assignedInstrumentTrack == null)
        {
            Debug.LogError($"AdjustRootOctave: No assigned InstrumentTrack for {name}");
            return adjustedRoot; // Return original root if no track is assigned
        }

        int lowestAllowed = assignedInstrumentTrack.lowestAllowedNote;
        int highestAllowed = assignedInstrumentTrack.highestAllowedNote;

        // ✅ Apply octave shifts based on NoteBehavior
        switch (noteBehavior)
        {
            case NoteBehavior.Bass:
                adjustedRoot -= 12; // Shift down 1 octave
                break;
            case NoteBehavior.Lead:
                adjustedRoot += 12; // Shift up 1 octave
                break;
            case NoteBehavior.Drone:
                adjustedRoot -= 24; // Shift down 2 octaves
                break;
            default:
                break; // Keep unchanged for Harmony & Percussion
        }

        // ✅ Clamp the adjusted root note within the instrument’s playable range
        adjustedRoot = Mathf.Clamp(adjustedRoot, lowestAllowed, highestAllowed);
        return adjustedRoot;
    }
    public void Initialize(int totalSteps)
    {
        if (assignedInstrumentTrack == null)
        {
            Debug.LogError($"❌ NoteSet '{name}' is missing an assignedInstrumentTrack during Initialize.");
            return;
        }

        BuildNotesFromKey();
        BuildAllowedStepsFromStyle(totalSteps);

    }
    
    public List<int> GetChordNotes()
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
    public int GetClosestVoiceLeadingNote(int currentNote, List<int> nextChordNotes)
    {
        return nextChordNotes.OrderBy(n => Mathf.Abs(n - currentNote)).First();
    }
    
    public int GetNoteGridRow(int pitch, int totalRows)
    {
        if (assignedInstrumentTrack == null)
        {
            Debug.LogError("❌ GetNoteGridRow: No assigned InstrumentTrack!");
            return 0;
        }

        int lowestNote = assignedInstrumentTrack.lowestAllowedNote;
        int highestNote = assignedInstrumentTrack.highestAllowedNote;

        if (lowestNote >= highestNote)
        {
            Debug.LogError($"❌ Invalid note range for {assignedInstrumentTrack.name}: {lowestNote} - {highestNote}");
            return 0;
        }

        int totalNotes = highestNote - lowestNote + 1;
        int pitchIndex = Mathf.Clamp(pitch - lowestNote, 0, totalNotes - 1);

        // ✅ Scale pitch into the available rows correctly
        float rowStep = (float)(totalRows - 1) / (totalNotes - 1);
        int row = Mathf.RoundToInt(pitchIndex * rowStep); 

        // ✅ Clamp to ensure valid row assignment
        int finalRow = Mathf.Clamp(row, 0, totalRows - 1);
        return finalRow;
    }

    public void AdvanceChord()
    {
        List<ChordPattern> allPatterns = Enum.GetValues(typeof(ChordPattern)).Cast<ChordPattern>().ToList();
        int currentIndex = allPatterns.IndexOf(chordPattern);
        chordPattern = allPatterns[(currentIndex + 1) % allPatterns.Count]; // Cycle through all chords

        Debug.Log($"Chord progression updated: {chordPattern}");
    }


    public int GetNextArpeggiatedNote(int stepIndex)
    {
        if (notes.Count == 0)
        {
            Debug.LogError("❌ No notes available in NoteSet!");
            return rootMidi; // Default to root note if empty
        }

        int note = notes[stepIndex % notes.Count];

        // ✅ Ensure the note is within the instrument’s range
        int lowestNote = assignedInstrumentTrack.lowestAllowedNote;
        int highestNote = assignedInstrumentTrack.highestAllowedNote;
        if (note < lowestNote || note > highestNote)
        {
            Debug.LogWarning($"❌ Selected note {note} out of range ({lowestNote} - {highestNote}). Clamping.");
            note = Mathf.Clamp(note, lowestNote, highestNote);
        }

        Debug.Log($"🎹 Returning arpeggiated note {note} for step {stepIndex}");
        return note;
    }

    private void BuildAllowedStepsFromStyle(int totalSteps)
    {
        //        Debug.Log($"NoteSet '{name}' building allowed steps for style {rhythmStyle} over {totalSteps} steps.");

        allowedSteps.Clear();

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
                    allowedSteps.Add(step);
            }
        }
    }
    
    public List<int> GetNoteList()
    {
        return notes;
    }

    public List<int> GetStepList()
    {
        return allowedSteps;
    }

}
