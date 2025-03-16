using UnityEngine;
using System.Collections.Generic;
using System.Linq;

public enum NoteBehavior
{
    Bass,       // Sustained, lower-frequency notes
    Lead,       // Fast-moving, melodic notes
    Harmony,    // Chord-based behavior
    Percussion, // Rhythm-based placement
    Drone       // Continuous background texture
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
public enum RhythmStyle
{
    FourOnTheFloor,
    Syncopated,
    Swing,
    Sparse,
    Dense
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
            { RhythmStyle.FourOnTheFloor, new RhythmPattern { Offsets = new [] {0, 4, 8, 12}, DurationMultiplier = 1f, LoopMultiplier = 2 } },
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
    public NoteBehavior noteBehavior;
    public InstrumentTrack assignedInstrumentTrack; // ✅ Each NoteSet is now tied to an InstrumentTrack
    private List<int> allowedSteps = new List<int>(); // ✅ The valid spawn steps for notes
    private List<int> notes = new List<int>(); // ✅ List of possible note values
    public RhythmStyle rhythmStyle = RhythmStyle.FourOnTheFloor;
    public int rootMidi;             // e.g. 24 for C1
    public ScaleType scale;          // e.g. Major or Minor
    public int lowestNote;           // e.g. 12
    public int highestNote;          // e.g. 60
    public int? dominantNote;
    public float dominantWeight = 2f;
    public float dominantNoteFrequency = .7f;
    public int allowedDuration = -1;
    public int dropBackIndex = 0;

    public void BuildNotesFromKey()
    {
        notes.Clear(); // Clear out any old notes
        int[] pattern = ScalePatterns.Patterns[scale];

        for (int pitch = lowestNote; pitch <= highestNote; pitch++)
        {
            // Check if this pitch is in the scale
            // e.g. if scalePattern contains ((pitch - rootMidi) mod 12)
            int semitoneAboveRoot = (pitch - rootMidi) % 12;
            if (semitoneAboveRoot < 0) semitoneAboveRoot += 12;

            if (System.Array.IndexOf(pattern, semitoneAboveRoot) >= 0)
            {
                // It's part of the scale, so add it
                notes.Add(pitch);

                // If it's the dominant note, add extra copies
                if (dominantNote.HasValue && pitch == dominantNote.Value)
                {
                    int extraCopies = Mathf.RoundToInt(dominantWeight) - 1;
                    for (int i = 0; i < extraCopies; i++)
                    {
                        notes.Add(pitch);
                    }
                }
            }
        }

        // At this point, `notes` holds a “weighted” list of pitches that respect the key & range.
        Debug.Log($"BuildNotesFromKey created {notes.Count} entries in the NoteSet.");
    }
    public void BuildAllowedStepsFromStyle(int totalSteps)
    {
        allowedSteps.Clear();

        // Retrieve the extended pattern for the chosen style.
        RhythmPattern pattern = RhythmPatterns.Patterns[rhythmStyle];

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

        Debug.Log($"Built {allowedSteps.Count} allowed steps for style {rhythmStyle} with loop multiplier {pattern.LoopMultiplier}");
    }

    public int GetRandomNote()
    {
        return notes[Random.Range(0, notes.Count)];
    }
    public List<int> GetNoteList()
    {
        return notes;
    }

    public List<int> GetStepList()
    {
        return allowedSteps;
    }

    public int GetNoteCount()
    {
        return notes.Count;
    }
    public int GetStepCount()
    {
        return allowedSteps.Count;
    }
}
