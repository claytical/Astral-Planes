namespace MidiPlayerTK
{
    /// @ingroup midistreamplayer_chord_tools
    /// <summary>
    /// List of available chord presets loaded from ChordLib.csv.
    /// Each enum value maps to one CSV row (same zero-based index).
    /// Chord quality is inferred from Modifier3:
    /// M = Major, m = Minor, D = Diminished, A = Augmented, S = Suspended.
    /// The additional modifier column is used for extra chord color tones (such as sevenths).
    /// The 12 pitch columns (C to B) form a chromatic bitmap where 1 means the pitch class is part of the chord.
    /// Per-value documentation shows interval names from the root and the active notes in the C reference scale.
    /// During playback, this pitch-class pattern is transposed from C to the selected tonic.
    /// Chord names and formulas were collected from an external online source and can include non-standard naming.
    /// @version Maestro Pro 
    /// </summary>
    public enum MPTKChordName
    {
        /// <summary>
        /// Major chord. Intervals: root, major third, perfect fifth. Notes: C, E, G.
        /// </summary>
        Major = 0,
        /// <summary>
        /// Diminished chord. Intervals: root, minor third, flat fifth. Notes: C, D#, F#.
        /// </summary>
        Dim = 1,
        /// <summary>
        /// Minor chord. Intervals: root, minor third, perfect fifth. Notes: C, D#, G.
        /// </summary>
        minor = 2,
        /// <summary>
        /// Minor chord. Intervals: root, minor third, sharp fifth. Notes: C, D#, G#.
        /// </summary>
        mSharp5 = 3,
        /// <summary>
        /// Major chord. Intervals: root, major third, minor seventh. Notes: C, E, A#.
        /// </summary>
        M7no5 = 4,
        /// <summary>
        /// Suspended chord. Intervals: root, major ninth, perfect fifth. Notes: C, D, G.
        /// </summary>
        Ssus2 = 5,
        /// <summary>
        /// Major chord. Intervals: root, major third, sharp fifth. Notes: C, E, G#.
        /// </summary>
        MSharp5 = 6,
        /// <summary>
        /// Suspended chord. Intervals: root, perfect fourth, perfect fifth. Notes: C, F, G.
        /// </summary>
        Ssus4 = 7,
        /// <summary>
        /// Major chord. Intervals: root, major third, perfect fifth, major seventh. Notes: C, E, G, B.
        /// </summary>
        M7 = 8,
        /// <summary>
        /// Major chord. Intervals: root, major third, perfect fifth, major sixth. Notes: C, E, G, A.
        /// </summary>
        M6 = 9,
        /// <summary>
        /// Minor chord. Intervals: root, flat ninth, minor third, sharp fifth. Notes: C, C#, D#, G#.
        /// </summary>
        mb6b9 = 10,
        /// <summary>
        /// Minor chord. Intervals: root, major ninth, minor third, perfect fifth. Notes: C, D, D#, G.
        /// </summary>
        madd9 = 11,
        /// <summary>
        /// Diminished chord. Intervals: root, minor third, flat fifth, major sixth. Notes: C, D#, F#, A.
        /// </summary>
        Dim7 = 12,
        /// <summary>
        /// Minor chord. Intervals: root, minor third, flat fifth, minor seventh. Notes: C, D#, F#, A#.
        /// </summary>
        m7b5 = 13,
        /// <summary>
        /// Minor chord. Intervals: root, minor third, perfect fifth, major sixth. Notes: C, D#, G, A.
        /// </summary>
        m6 = 14,
        /// <summary>
        /// Minor chord. Intervals: root, minor third, perfect fifth, minor seventh. Notes: C, D#, G, A#.
        /// </summary>
        m7 = 15,
        /// <summary>
        /// Minor chord. Intervals: root, minor third, perfect fifth, major seventh. Notes: C, D#, G, B.
        /// </summary>
        mM7 = 16,
        /// <summary>
        /// Minor chord. Intervals: root, minor third, sharp fifth, minor seventh. Notes: C, D#, G#, A#.
        /// </summary>
        mm7Sharp5 = 17,
        /// <summary>
        /// Minor chord. Intervals: root, minor third, flat thirteenth, major seventh. Notes: C, D#, G#, B.
        /// </summary>
        mb6M7 = 18,
        /// <summary>
        /// Minor chord. Intervals: root, major ninth, major third, minor seventh. Notes: C, D, E, A#.
        /// </summary>
        m9no5 = 19,
        /// <summary>
        /// Diminished chord. Intervals: root, major third, flat fifth, minor seventh. Notes: C, E, F#, A#.
        /// </summary>
        D7b5 = 20,
        /// <summary>
        /// Augmented chord. Intervals: root, major third, sharp fifth, minor seventh. Notes: C, E, G#, A#.
        /// </summary>
        A7aug = 21,
        /// <summary>
        /// Augmented chord. Intervals: root, major third, sharp fifth, minor seventh. Notes: C, E, G#, A#.
        /// </summary>
        Aaug7 = 22,
        /// <summary>
        /// Suspended chord. Intervals: root, flat ninth, perfect fourth, perfect fifth. Notes: C, C#, F, G.
        /// </summary>
        S7b9sus = 23,
        /// <summary>
        /// Suspended chord. Intervals: root, flat ninth, perfect fourth, perfect fifth. Notes: C, C#, F, G.
        /// </summary>
        Susb9 = 24,
        /// <summary>
        /// Major chord. Intervals: root, major ninth, major third, sharp fifth. Notes: C, D, E, G#.
        /// </summary>
        MSharp5add9 = 25,
        /// <summary>
        /// Major chord. Intervals: root, major third, flat thirteenth, major seventh. Notes: C, E, G#, B.
        /// </summary>
        M7Sharp5 = 26,
        /// <summary>
        /// Suspended chord. Intervals: root, perfect fourth, perfect fifth, minor seventh. Notes: C, F, G, A#.
        /// </summary>
        S7sus4 = 27,
        /// <summary>
        /// Major chord. Intervals: root, flat ninth, major third, perfect fifth, minor seventh. Notes: C, C#, E, G, A#.
        /// </summary>
        M7b9 = 28,
        /// <summary>
        /// Major chord. Intervals: root, major ninth, major third, perfect fifth, minor seventh. Notes: C, D, E, G, A#.
        /// </summary>
        M9 = 29,
        /// <summary>
        /// Diminished chord. Intervals: root, major third, sharp eleventh, perfect fifth, minor seventh. Notes: C, E, F#, G, A#.
        /// </summary>
        D7Sharp11 = 30,
        /// <summary>
        /// Major chord. Intervals: root, major third, perfect fifth, sharp fifth, minor seventh. Notes: C, E, G, G#, A#.
        /// </summary>
        M7b13 = 31,
        /// <summary>
        /// Major chord. Intervals: root, major third, perfect fifth, sharp fifth, minor seventh. Notes: C, E, G, G#, A#.
        /// </summary>
        M7b6 = 32,
        /// <summary>
        /// Major chord. Intervals: root, major ninth, major third, perfect fifth, thirteenth. Notes: C, D, E, G, A.
        /// </summary>
        M44445 = 33,
        /// <summary>
        /// Major chord. Intervals: root, major ninth, major third, perfect fifth, major seventh. Notes: C, D, E, G, B.
        /// </summary>
        M9b = 34,
        /// <summary>
        /// Minor chord. Intervals: root, sharp ninth, major third, perfect fifth, minor seventh. Notes: C, D#, E, G, A#.
        /// </summary>
        m7Sharp11 = 35,
        /// <summary>
        /// Minor chord. Intervals: root, sharp ninth, major third, sharp fifth, minor seventh. Notes: C, D#, E, G#, A#.
        /// </summary>
        m7Sharp5Sharp9 = 36,
        /// <summary>
        /// Minor chord. Intervals: root, sharp ninth, major third, sharp fifth, minor seventh. Notes: C, D#, E, G#, A#.
        /// </summary>
        m7b5Sharp9 = 37,
        /// <summary>
        /// Diminished chord. Intervals: root, major ninth, minor third, flat fifth, minor seventh. Notes: C, D, D#, F#, A#.
        /// </summary>
        D9b5 = 38,
        /// <summary>
        /// Minor chord. Intervals: root, major ninth, minor third, perfect fifth, thirteenth. Notes: C, D, D#, G, A.
        /// </summary>
        m69 = 39,
        /// <summary>
        /// Minor chord. Intervals: root, major ninth, minor third, perfect fifth, minor seventh. Notes: C, D, D#, G, A#.
        /// </summary>
        m9 = 40,
        /// <summary>
        /// Minor chord. Intervals: root, major ninth, minor third, perfect fifth, major seventh. Notes: C, D, D#, G, B.
        /// </summary>
        mM9 = 41,
        /// <summary>
        /// Minor chord. Intervals: root, major ninth, minor third, sharp fifth, minor seventh. Notes: C, D, D#, G#, A#.
        /// </summary>
        m9Sharp5 = 42,
        /// <summary>
        /// Minor chord. Intervals: root, sharp ninth, major third, perfect fifth, major seventh. Notes: C, D#, E, G, B.
        /// </summary>
        m7Sharp11b = 43,
        /// <summary>
        /// Minor chord. Intervals: root, minor third, perfect fifth, flat thirteenth, major seventh. Notes: C, D#, G, G#, B.
        /// </summary>
        mM7b6 = 44,
        /// <summary>
        /// Diminished chord. Intervals: root, flat ninth, major third, sharp fifth, minor seventh. Notes: C, C#, E, G#, A#.
        /// </summary>
        D7Sharp5b9 = 45,
        /// <summary>
        /// Diminished chord. Intervals: root, major ninth, major third, flat fifth, minor seventh. Notes: C, D, E, F#, A#.
        /// </summary>
        D9b5x = 46,
        /// <summary>
        /// Diminished chord. Intervals: root, major ninth, major third, sharp fifth, minor seventh. Notes: C, D, E, G#, A#.
        /// </summary>
        D9Sharp5 = 47,
        /// <summary>
        /// Diminished chord. Intervals: root, major third, flat fifth, major sixth, minor seventh. Notes: C, E, F#, A, A#.
        /// </summary>
        D13b5 = 48,
        /// <summary>
        /// Suspended chord. Intervals: root, flat ninth, perfect fourth, perfect fifth, minor seventh. Notes: C, C#, F, G, A#.
        /// </summary>
        S7sus4b9 = 49,
        /// <summary>
        /// Suspended chord. Intervals: root, flat ninth, perfect fourth, perfect fifth, minor seventh. Notes: C, C#, F, G, A#.
        /// </summary>
        S7susb9 = 50,
        /// <summary>
        /// Major chord. Intervals: root, major ninth, major third, flat thirteenth, major seventh. Notes: C, D, E, G#, B.
        /// </summary>
        MM9Sharp5 = 51,
        /// <summary>
        /// Suspended chord. Intervals: root, major ninth, eleventh, perfect fifth, minor seventh. Notes: C, D, F, G, A#.
        /// </summary>
        S9sus4 = 52,
        /// <summary>
        /// Major chord. Intervals: root, flat ninth, major third, sharp eleventh, perfect fifth, minor seventh. Notes: C, C#, E, F#, G, A#.
        /// </summary>
        M7b5b9 = 53,
        /// <summary>
        /// Major chord. Intervals: root, flat ninth, major third, sharp eleventh, perfect fifth, minor seventh. Notes: C, C#, E, F#, G, A#.
        /// </summary>
        M7b9Sharp11 = 54,
        /// <summary>
        /// Major chord. Intervals: root, flat ninth, major third, perfect fifth, sharp fifth, minor seventh. Notes: C, C#, E, G, G#, A#.
        /// </summary>
        M7b9b13 = 55,
        /// <summary>
        /// Major chord. Intervals: root, flat ninth, major third, perfect fifth, major sixth, minor seventh. Notes: C, C#, E, G, A, A#.
        /// </summary>
        M13b9 = 56,
        /// <summary>
        /// Major chord. Intervals: root, major ninth, major third, eleventh, perfect fifth, minor seventh. Notes: C, D, E, F, G, A#.
        /// </summary>
        M11 = 57,
        /// <summary>
        /// Major chord. Intervals: root, major ninth, major third, sharp eleventh, perfect fifth, minor seventh. Notes: C, D, E, F#, G, A#.
        /// </summary>
        M9Sharp11 = 58,
        /// <summary>
        /// Major chord. Intervals: root, major ninth, major third, perfect fifth, sharp fifth, minor seventh. Notes: C, D, E, G, G#, A#.
        /// </summary>
        M9b13 = 59,
        /// <summary>
        /// Major chord. Intervals: root, major ninth, major third, perfect fifth, thirteenth, minor seventh. Notes: C, D, E, G, A, A#.
        /// </summary>
        M13 = 60,
        /// <summary>
        /// Major chord. Intervals: root, major third, sharp eleventh, perfect fifth, sharp fifth, minor seventh. Notes: C, E, F#, G, G#, A#.
        /// </summary>
        M7Sharp11b13 = 61,
        /// <summary>
        /// Major chord. Intervals: root, major third, sharp eleventh, perfect fifth, sharp fifth, minor seventh. Notes: C, E, F#, G, G#, A#.
        /// </summary>
        M7b5b13 = 62,
        /// <summary>
        /// Minor chord. Intervals: root, sharp ninth, major third, sharp eleventh, perfect fifth, minor seventh. Notes: C, D#, E, F#, G, A#.
        /// </summary>
        m7Sharp9Sharp11 = 63,
        /// <summary>
        /// Minor chord. Intervals: root, sharp ninth, major third, perfect fifth, sharp fifth, minor seventh. Notes: C, D#, E, G, G#, A#.
        /// </summary>
        m7Sharp9b13 = 64,
        /// <summary>
        /// Minor chord. Intervals: root, sharp ninth, major third, perfect fifth, major sixth, minor seventh. Notes: C, D#, E, G, A, A#.
        /// </summary>
        m13Sharp9 = 65,
        /// <summary>
        /// Minor chord. Intervals: root, major ninth, minor third, eleventh, flat fifth, minor seventh. Notes: C, D, D#, F, F#, A#.
        /// </summary>
        m11b5 = 66,
        /// <summary>
        /// Minor chord. Intervals: root, major ninth, minor third, eleventh, perfect fifth, minor seventh. Notes: C, D, D#, F, G, A#.
        /// </summary>
        m11 = 67,
        /// <summary>
        /// Minor chord. Intervals: root, major ninth, minor third, eleventh, sharp fifth, minor seventh. Notes: C, D, D#, F, G#, A#.
        /// </summary>
        m11Sharp5 = 68,
        /// <summary>
        /// Minor chord. Intervals: root, minor third, perfect fourth, sharp eleventh, perfect fifth, minor seventh. Notes: C, D#, F, F#, G, A#.
        /// </summary>
        mBlues = 69,
        /// <summary>
        /// Diminished chord. Intervals: root, flat ninth, major third, flat fifth, sharp fifth, minor seventh. Notes: C, C#, E, F#, G#, A#.
        /// </summary>
        D7Sharp5b9Sharp11 = 70,
        /// <summary>
        /// Diminished chord. Intervals: root, major ninth, major third, flat fifth, sharp fifth, minor seventh. Notes: C, D, E, F#, G#, A#.
        /// </summary>
        D9Sharp5Sharp11 = 71,
        /// <summary>
        /// Suspended chord. Intervals: root, flat ninth, perfect fourth, perfect fifth, sharp fifth, minor seventh. Notes: C, C#, F, G, G#, A#.
        /// </summary>
        S7b9b13sus4 = 72,
        /// <summary>
        /// Suspended chord. Intervals: root, flat ninth, perfect fourth, perfect fifth, sharp fifth, minor seventh. Notes: C, C#, F, G, G#, A#.
        /// </summary>
        S7sus4b9b13 = 73,
        /// <summary>
        /// Suspended chord. Intervals: root, major ninth, eleventh, perfect fifth, thirteenth, minor seventh. Notes: C, D, F, G, A, A#.
        /// </summary>
        S13sus4 = 74,
        /// <summary>
        /// Major chord. Intervals: root, flat ninth, major third, sharp eleventh, perfect fifth, sharp fifth, minor seventh. Notes: C, C#, E, F#, G, G#, A#.
        /// </summary>
        M7b5b9b13 = 75,
        /// <summary>
        /// Major chord. Intervals: root, flat ninth, major third, sharp eleventh, perfect fifth, sharp fifth, minor seventh. Notes: C, C#, E, F#, G, G#, A#.
        /// </summary>
        M7b9Sharp11b13 = 76,
        /// <summary>
        /// Major chord. Intervals: root, flat ninth, major third, sharp eleventh, perfect fifth, sharp fifth, minor seventh. Notes: C, C#, E, F#, G, G#, A#.
        /// </summary>
        M7b9b13Sharp11 = 77,
        /// <summary>
        /// Major chord. Intervals: root, flat ninth, major third, sharp eleventh, perfect fifth, major sixth, minor seventh. Notes: C, C#, E, F#, G, A, A#.
        /// </summary>
        M13b9Sharp11 = 78,
        /// <summary>
        /// Major chord. Intervals: root, major ninth, major third, sharp eleventh, perfect fifth, sharp fifth, minor seventh. Notes: C, D, E, F#, G, G#, A#.
        /// </summary>
        M9Sharp11b13 = 79,
        /// <summary>
        /// Major chord. Intervals: root, major ninth, major third, sharp eleventh, perfect fifth, sharp fifth, minor seventh. Notes: C, D, E, F#, G, G#, A#.
        /// </summary>
        M9b5b13 = 80,
        /// <summary>
        /// Major chord. Intervals: root, major ninth, major third, sharp eleventh, perfect fifth, thirteenth, minor seventh. Notes: C, D, E, F#, G, A, A#.
        /// </summary>
        M13Sharp11 = 81,
        /// <summary>
        /// Minor chord. Intervals: root, major ninth, minor third, eleventh, perfect fifth, thirteenth, minor seventh. Notes: C, D, D#, F, G, A, A#.
        /// </summary>
        m13 = 82,
        /// <summary>
        /// Minor chord. Intervals: root, sharp ninth, major third, sharp eleventh, perfect fifth, sharp fifth, minor seventh. Notes: C, D#, E, F#, G, G#, A#.
        /// </summary>
        m7Sharp9Sharp11b13 = 83,
        /// <summary>
        /// Minor chord. Intervals: root, sharp ninth, major third, sharp eleventh, perfect fifth, major sixth, minor seventh. Notes: C, D#, E, F#, G, A, A#.
        /// </summary>
        m13Sharp9Sharp11 = 84,
    }
}

