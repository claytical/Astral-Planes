using System;
using System.Collections.Generic;
using UnityEngine;

namespace MidiPlayerTK
{
    /// @ingroup midistreamplayer_chord_tools
    /// <summary>
    /// Tonal mode (global harmonic context) for a progression preset.
    /// </summary>
    public enum MPTKTonalMode
    {
        /// <summary>
        /// Built from the major scale, with a major tonic chord and a generally bright, stable sound.
        /// </summary>
        Major,
        /// <summary>
        /// Built from the minor scale, with a minor tonic chord and a generally darker or introspective sound.
        /// </summary>
        Minor,
        /// <summary>
        /// Built from a mode rather than standard major/minor tonality, where color comes from characteristic scale degrees.
        /// </summary>
        Modal
    }

    /// <summary>
    /// Musical family (production/style category) for a progression preset.
    /// </summary>
    public enum MPTKProgressionGenre
    {
        /// <summary>
        /// Song-oriented style with clear hooks and short repeating progressions around a strong tonal center.
        /// </summary>
        Pop,
        /// <summary>
        /// Style that commonly uses seventh and extended chords, with frequent ii-V-I type movement.
        /// </summary>
        Jazz,
        /// <summary>
        /// Film and game scoring style focused on emotional impact, atmosphere, and dramatic harmonic motion.
        /// </summary>
        Cinematic,
        /// <summary>
        /// Common-practice tradition emphasizing voice leading, counterpoint, and clear cadential resolution.
        /// </summary>
        Classical,
        /// <summary>
        /// 1950s popular idiom (including doo-wop), often using familiar loops like I-vi-IV-V.
        /// </summary>
        Vintage50s,
    }

    /// <summary>
    /// Harmonic language used by the progression.
    /// </summary>
    public enum MPTKHarmonyFlavor
    {
        /// <summary>
        /// Chords are mostly taken from the notes of one parent scale, with little or no chromatic alteration.
        /// </summary>
        Diatonic,
        /// <summary>
        /// Uses borrowed chords from the parallel major or minor mode to add contrast and color.
        /// </summary>
        Borrowed,
        /// <summary>
        /// Prioritizes seventh and extended sonorities (9, 11, 13), sometimes with altered tensions.
        /// </summary>
        JazzExtended,
        /// <summary>
        /// Progression is driven by harmonic function and cadence direction, especially motion back to tonic.
        /// </summary>
        Functional,
        /// <summary>
        /// Highlights Phrygian modal color, especially the flattened second degree against the tonic.
        /// </summary>
        Modal
    }

    /// <summary>
    /// Preset chord progressions with emotional tags and helpers to convert Roman numerals
    /// to playable chord-library entries (MPTKChordName + tonic).
    /// Degree token grammar: [accidentals][Roman degree][optional suffix].
    /// - Accidentals: zero or more 'b' or '#', for example "bII", "##IV".
    /// - Roman degree core: I, II, III, IV, V, VI, VII (uppercase or lowercase).
    ///   Case does not change the scale degree index, only the default triad quality
    ///   - uppercase starts from major-triad quality, 
    ///   - lowercase starts from minor-triad quality.
    /// - Optional suffix markers understood by the parser: "7", "maj7", "sus", "sus4", "sus2", "dim", "o", "b5", "#5", "aug", "+".
    /// Practical examples in C:
    /// - "I - V - vi - IV" -> C - G - Am - F
    /// - "I - iii - IV - iv" -> C - Em - F - Fm
    /// Canonical degree cores are 7 values (I..VII, upper/lower case intent), but raw token strings are unbounded
    /// because accidental prefixes and suffix text can be combined freely.
    /// Notes:
    /// - Use ASCII only in the API.
    /// - Half-diminished chords should be written as m7b5-style Roman tokens, for example: "iib57".
    /// - Diminished chords can be written with "o" or "dim", for example: "viio" or "viidim7".
    /// </summary>
    [Serializable]
    public class MPTKChordProgressionPreset
    {
        public string Id;
        public string Name;
        public MPTKTonalMode Mode;
        public MPTKProgressionGenre Genre;
        public MPTKHarmonyFlavor HarmonyFlavor;
        public string[] MoodTags;
        /// <summary>
        /// Progression steps in Roman-degree notation.
        /// Example tokens: "I", "V", "vi", "IV", "bII", "V7", "Imaj7", "viio", "iib57".
        /// </summary>
        public string[] Degrees;
        public string Example;
        public int Intensity;
        public int HarmonicTension;

        public MPTKChordProgressionPreset(
            string id,
            string name,
            MPTKTonalMode mode,
            MPTKProgressionGenre genre,
            MPTKHarmonyFlavor harmonyFlavor,
            string[] moodTags,
            string[] degrees,
            string example,
            int intensity,
            int harmonicTension)
        {
            Id = id;
            Name = name;
            Mode = mode;
            Genre = genre;
            HarmonyFlavor = harmonyFlavor;
            MoodTags = moodTags;
            Degrees = degrees;
            Example = example;
            Intensity = intensity;
            HarmonicTension = harmonicTension;
        }
    }

    /// <summary>
    /// One resolved progression step.
    /// </summary>
    public struct MPTKResolvedChordStep
    {
        public string DegreeToken;
        public int TonicMidi;
        public MPTKChordName ChordName;
        public bool IsApproximation;
        public string Note;

        public MPTKResolvedChordStep(
            string degreeToken,
            int tonicMidi,
            MPTKChordName chordName,
            bool isApproximation,
            string note)
        {
            DegreeToken = degreeToken;
            TonicMidi = tonicMidi;
            ChordName = chordName;
            IsApproximation = isApproximation;
            Note = note;
        }
    }

    /// <summary>
    /// Utility library for emotional progression presets and Roman-degree parsing.
    /// Roman roots are resolved from a tonic using a major-reference semitone map:
    /// I=0, II=2, III=4, IV=5, V=7, VI=9, VII=11, plus optional accidentals.
    /// Chord quality is then derived from case and supported ASCII suffixes.
    /// </summary>
    public static class MPTKChordProgressionLib
    {
        private enum TriadQuality
        {
            Major,
            Minor,
            Diminished,
            Augmented,
            Suspended2,
            Suspended4
        }


        private static readonly int[] MajorDegreeSemitone = { 0, 2, 4, 5, 7, 9, 11 };

        /// <summary>
        /// Built-in emotional progression presets.
        /// Intensity and HarmonicTension are subjective ratings from 1 (low) to 5 (high).
        /// </summary>
        public static readonly List<MPTKChordProgressionPreset> Presets = new List<MPTKChordProgressionPreset>
        {
            // --- Pop / Major / Diatonic ---
            new MPTKChordProgressionPreset("uplifting_pop", "Uplifting Pop", MPTKTonalMode.Major, MPTKProgressionGenre.Pop, MPTKHarmonyFlavor.Diatonic, new[] { "Hopeful", "Uplifting" }, new[] { "I", "V", "vi", "IV" }, "C-G-Am-F", 4, 2),
            new MPTKChordProgressionPreset("warm_resolution", "Warm Resolution", MPTKTonalMode.Major, MPTKProgressionGenre.Pop, MPTKHarmonyFlavor.Diatonic, new[] { "Warm", "Reassuring" }, new[] { "I", "vi", "IV", "V" }, "C-Am-F-G", 3, 2),
            new MPTKChordProgressionPreset("nostalgic_loop", "Nostalgic Loop", MPTKTonalMode.Major, MPTKProgressionGenre.Pop, MPTKHarmonyFlavor.Diatonic, new[] { "Nostalgic", "Bittersweet" }, new[] { "vi", "IV", "I", "V" }, "Am-F-C-G", 3, 2),
            new MPTKChordProgressionPreset("bright_anthem", "Bright Anthem", MPTKTonalMode.Major, MPTKProgressionGenre.Pop, MPTKHarmonyFlavor.Diatonic, new[] { "Anthemic", "Triumphant", "Open" }, new[] { "I", "IV", "V", "IV" }, "C-F-G-F", 4, 2),
            new MPTKChordProgressionPreset("open_road", "Open Road", MPTKTonalMode.Major, MPTKProgressionGenre.Pop, MPTKHarmonyFlavor.Diatonic, new[] { "Optimistic", "Free", "Flowing" }, new[] { "I", "V", "ii", "IV" }, "C-G-Dm-F", 3, 2),
            new MPTKChordProgressionPreset("gentle_lift", "Gentle Lift", MPTKTonalMode.Major, MPTKProgressionGenre.Pop, MPTKHarmonyFlavor.Diatonic, new[] { "Gentle", "Hopeful", "Tender" }, new[] { "I", "IV", "I", "V" }, "C-F-C-G", 2, 1),

            // --- Pop / Major / Functional ---
            new MPTKChordProgressionPreset("circle_flow", "Circle Flow", MPTKTonalMode.Major, MPTKProgressionGenre.Pop, MPTKHarmonyFlavor.Functional, new[] { "Smooth", "ForwardMotion" }, new[] { "vi", "ii", "V", "I" }, "Am-Dm-G-C", 3, 3),
            new MPTKChordProgressionPreset("rising_motion", "Rising Motion", MPTKTonalMode.Major, MPTKProgressionGenre.Pop, MPTKHarmonyFlavor.Functional, new[] { "Forward", "Motivated", "Bright" }, new[] { "I", "ii", "V", "I" }, "C-Dm-G-C", 3, 3),

            // --- Pop / Major / Borrowed ---
            new MPTKChordProgressionPreset("bittersweet_glow", "Bittersweet Glow", MPTKTonalMode.Major, MPTKProgressionGenre.Pop, MPTKHarmonyFlavor.Borrowed, new[] { "Bittersweet", "Warm", "Nostalgic" }, new[] { "I", "IV", "iv", "I" }, "C-F-Fm-C", 2, 3),
            new MPTKChordProgressionPreset("sunset_return", "Sunset Return", MPTKTonalMode.Major, MPTKProgressionGenre.Pop, MPTKHarmonyFlavor.Borrowed, new[] { "Nostalgic", "Emotional", "Soft" }, new[] { "I", "V", "IV", "iv" }, "C-G-F-Fm", 3, 3),

            // --- Pop / Minor / Diatonic ---
            new MPTKChordProgressionPreset("sad_introspective", "Sad Introspective", MPTKTonalMode.Minor, MPTKProgressionGenre.Pop, MPTKHarmonyFlavor.Diatonic, new[] { "Sad", "Reflective" }, new[] { "i", "VI", "III", "VII" }, "Am-F-C-G", 2, 2),
            new MPTKChordProgressionPreset("quiet_resolve", "Quiet Resolve", MPTKTonalMode.Minor, MPTKProgressionGenre.Pop, MPTKHarmonyFlavor.Diatonic, new[] { "Calm", "Reflective", "Resolved" }, new[] { "i", "iv", "VI", "V" }, "Am-Dm-F-E", 2, 3),
            new MPTKChordProgressionPreset("distant_memory", "Distant Memory", MPTKTonalMode.Minor, MPTKProgressionGenre.Pop, MPTKHarmonyFlavor.Diatonic, new[] { "Melancholic", "Nostalgic", "Soft" }, new[] { "i", "III", "VII", "VI" }, "Am-C-G-F", 2, 2),
            new MPTKChordProgressionPreset("inner_fire", "Inner Fire", MPTKTonalMode.Minor, MPTKProgressionGenre.Pop, MPTKHarmonyFlavor.Diatonic, new[] { "Driven", "Restless", "Focused" }, new[] { "i", "VII", "iv", "V" }, "Am-G-Dm-E", 4, 4),

            // --- Cinematic / Major / Diatonic ---
            new MPTKChordProgressionPreset("sacred_light", "Sacred Light", MPTKTonalMode.Major, MPTKProgressionGenre.Cinematic, MPTKHarmonyFlavor.Diatonic, new[] { "Sacred", "Radiant", "Peaceful" }, new[] { "I", "V", "IV", "I" }, "C-G-F-C", 2, 1),

            // --- Cinematic / Major / Borrowed ---
            new MPTKChordProgressionPreset("romantic_color", "Romantic Color", MPTKTonalMode.Major, MPTKProgressionGenre.Cinematic, MPTKHarmonyFlavor.Borrowed, new[] { "Romantic", "Tender" }, new[] { "I", "iii", "IV", "iv" }, "C-Em-F-Fm", 2, 3),

            // --- Cinematic / Major / Functional ---
            new MPTKChordProgressionPreset("heroic_ascent", "Heroic Ascent", MPTKTonalMode.Major, MPTKProgressionGenre.Cinematic, MPTKHarmonyFlavor.Functional, new[] { "Heroic", "Elevated", "Resolute" }, new[] { "I", "iii", "IV", "V" }, "C-Em-F-G", 4, 3),

            // --- Cinematic / Minor / Diatonic ---
            new MPTKChordProgressionPreset("dark_cadence", "Dark Cadence", MPTKTonalMode.Minor, MPTKProgressionGenre.Cinematic, MPTKHarmonyFlavor.Diatonic, new[] { "Dark", "Determined" }, new[] { "i", "VII", "VI", "V" }, "Am-G-F-E", 3, 4),
            new MPTKChordProgressionPreset("epic_minor_loop", "Epic Minor Loop", MPTKTonalMode.Minor, MPTKProgressionGenre.Cinematic, MPTKHarmonyFlavor.Diatonic, new[] { "Epic", "Driving" }, new[] { "i", "VI", "III", "VII" }, "Am-F-C-G", 4, 3),
            new MPTKChordProgressionPreset("minor_hope", "Minor Hope", MPTKTonalMode.Minor, MPTKProgressionGenre.Cinematic, MPTKHarmonyFlavor.Diatonic, new[] { "Emotional", "Hopeful", "Bittersweet" }, new[] { "i", "VI", "iv", "V" }, "Am-F-Dm-E", 3, 4),
            new MPTKChordProgressionPreset("fallen_horizon", "Fallen Horizon", MPTKTonalMode.Minor, MPTKProgressionGenre.Cinematic, MPTKHarmonyFlavor.Diatonic, new[] { "Dark", "Epic", "Heavy" }, new[] { "i", "VI", "VII", "V" }, "Am-F-G-E", 4, 4),

            // --- Cinematic / Minor / Functional ---
            new MPTKChordProgressionPreset("ominous_pulse", "Ominous Pulse", MPTKTonalMode.Minor, MPTKProgressionGenre.Cinematic, MPTKHarmonyFlavor.Functional, new[] { "Ominous", "Suspense", "Driven" }, new[] { "i", "iv", "VII", "V" }, "Am-Dm-G-E", 4, 4),

            // --- Cinematic / Modal / Diatonic ---
            new MPTKChordProgressionPreset("mysterious", "Mysterious", MPTKTonalMode.Modal, MPTKProgressionGenre.Cinematic, MPTKHarmonyFlavor.Diatonic, new[] { "Mysterious", "Unstable", "Phrygian" }, new[] { "i", "bII", "V", "i" }, "Am-Bb-E-Am", 3, 5),

            // --- Cinematic / Modal / Modal ---
            new MPTKChordProgressionPreset("suspense_build", "Suspense Build", MPTKTonalMode.Modal, MPTKProgressionGenre.Cinematic, MPTKHarmonyFlavor.Modal, new[] { "Suspense", "Threat", "Phrygian" }, new[] { "i", "bII", "VI", "V" }, "Am-Bb-F-E", 4, 5),
            new MPTKChordProgressionPreset("phrygian_threat", "Phrygian Threat", MPTKTonalMode.Modal, MPTKProgressionGenre.Cinematic, MPTKHarmonyFlavor.Modal, new[] { "Threat", "Ancient", "Phrygian" }, new[] { "i", "bII", "i", "V" }, "Am-Bb-Am-E", 3, 5),
            new MPTKChordProgressionPreset("dorian_quest", "Dorian Quest", MPTKTonalMode.Modal, MPTKProgressionGenre.Cinematic, MPTKHarmonyFlavor.Modal, new[] { "Ancient", "Quest", "Dorian" }, new[] { "i", "IV", "VII", "i" }, "Am-D-G-Am", 3, 3),
            new MPTKChordProgressionPreset("dorian_drift", "Dorian Drift", MPTKTonalMode.Modal, MPTKProgressionGenre.Cinematic, MPTKHarmonyFlavor.Modal, new[] { "Floating", "Ancient", "Dorian" }, new[] { "i", "VII", "IV", "i" }, "Am-G-D-Am", 2, 2),
            new MPTKChordProgressionPreset("lydian_dream", "Lydian Dream", MPTKTonalMode.Modal, MPTKProgressionGenre.Cinematic, MPTKHarmonyFlavor.Modal, new[] { "Dreamy", "Bright", "Lydian" }, new[] { "I", "II", "V", "I" }, "C-D-G-C", 2, 2),

            // --- Jazz / Major / JazzExtended ---
            new MPTKChordProgressionPreset("jazz_calm", "Jazz Calm", MPTKTonalMode.Major, MPTKProgressionGenre.Jazz, MPTKHarmonyFlavor.JazzExtended, new[] { "Calm", "Sophisticated" }, new[] { "ii7", "V7", "Imaj7" }, "Dm7-G7-Cmaj7", 2, 3),
            new MPTKChordProgressionPreset("late_night_turnaround", "Late Night Turnaround", MPTKTonalMode.Major, MPTKProgressionGenre.Jazz, MPTKHarmonyFlavor.JazzExtended, new[] { "Smooth", "LateNight", "Sophisticated" }, new[] { "Imaj7", "VI7", "ii7", "V7" }, "Cmaj7-A7-Dm7-G7", 3, 4),
            new MPTKChordProgressionPreset("soft_walk", "Soft Walk", MPTKTonalMode.Major, MPTKProgressionGenre.Jazz, MPTKHarmonyFlavor.JazzExtended, new[] { "Soft", "Elegant", "Warm" }, new[] { "iii7", "vi7", "ii7", "V7" }, "Em7-Am7-Dm7-G7", 2, 3),

            // --- Jazz / Minor / JazzExtended ---
            new MPTKChordProgressionPreset("minor_lounge", "Minor Lounge", MPTKTonalMode.Minor, MPTKProgressionGenre.Jazz, MPTKHarmonyFlavor.JazzExtended, new[] { "Moody", "Cool", "Sophisticated" }, new[] { "i7", "iv7", "iib57", "V7" }, "Am7-Dm7-Bm7b5-E7", 3, 4),

            // --- Classical / Major / Functional ---
            new MPTKChordProgressionPreset("plagal_warmth", "Plagal Warmth", MPTKTonalMode.Major, MPTKProgressionGenre.Classical, MPTKHarmonyFlavor.Functional, new[] { "Warm", "Peaceful" }, new[] { "I", "IV", "vi", "IV" }, "C-F-Am-F", 2, 1),
            new MPTKChordProgressionPreset("clear_cadence", "Clear Cadence", MPTKTonalMode.Major, MPTKProgressionGenre.Classical, MPTKHarmonyFlavor.Functional, new[] { "Balanced", "Clear", "Resolved" }, new[] { "I", "ii", "V", "I" }, "C-Dm-G-C", 2, 3),
            new MPTKChordProgressionPreset("pastoral_turn", "Pastoral Turn", MPTKTonalMode.Major, MPTKProgressionGenre.Classical, MPTKHarmonyFlavor.Functional, new[] { "Pastoral", "Gentle", "Peaceful" }, new[] { "I", "IV", "V", "I" }, "C-F-G-C", 2, 2),

            // --- Classical / Minor / Functional ---
            new MPTKChordProgressionPreset("melancholic_classic", "Melancholic Classic", MPTKTonalMode.Minor, MPTKProgressionGenre.Classical, MPTKHarmonyFlavor.Functional, new[] { "Melancholic" }, new[] { "i", "iv", "V", "i" }, "Am-Dm-E-Am", 2, 4),
            new MPTKChordProgressionPreset("solemn_march", "Solemn March", MPTKTonalMode.Minor, MPTKProgressionGenre.Classical, MPTKHarmonyFlavor.Functional, new[] { "Solemn", "Measured", "Grave" }, new[] { "i", "VI", "iv", "V" }, "Am-F-Dm-E", 3, 4),

            // --- Vintage50s / Major / Diatonic ---
            new MPTKChordProgressionPreset("doo_wop", "Doo-Wop", MPTKTonalMode.Major, MPTKProgressionGenre.Vintage50s, MPTKHarmonyFlavor.Diatonic, new[] { "Nostalgic" }, new[] { "I", "vi", "IV", "V" }, "C-Am-F-G", 3, 2),        
        };

        /// <summary>
        /// Find a progression preset by id (case-insensitive).
        /// </summary>
        public static MPTKChordProgressionPreset FindPreset(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return null;

            for (int i = 0; i < Presets.Count; i++)
            {
                if (string.Equals(Presets[i].Id, id, StringComparison.OrdinalIgnoreCase))
                    return Presets[i];
            }

            return null;
        }

        /// <summary>
        /// Resolve a progression to concrete tonic + chord-library entries.
        /// The tonic is the root note of degree I/i (for example C4 = 60).
        /// </summary>
        /// <param name="degreeTokens">
        /// Degree token grammar: [accidentals][Roman degree][optional suffix].
        /// - Accidentals: zero or more 'b' or '#', for example "bII", "##IV".
        /// - Roman degree core: I, II, III, IV, V, VI, VII (uppercase or lowercase).
        ///   Case does not change the scale degree index, only the default triad quality
        ///   - uppercase starts from major-triad quality, 
        ///   - lowercase starts from minor-triad quality.
        /// - Optional suffix markers understood by the parser: "7", "maj7", "sus", "sus4", "sus2", "dim", "o", "b5", "#5", "aug", "+".
        /// Practical examples in C:
        /// - "I - V - vi - IV" -> C - G - Am - F
        /// - "I - iii - IV - iv" -> C - Em - F - Fm
        /// Canonical degree cores are 7 values (I..VII, upper/lower case intent), but raw token strings are unbounded
        /// because accidental prefixes and suffix text can be combined freely.
        /// Notes:
        /// - Use ASCII only in the API.
        /// - Half-diminished chords should be written as m7b5-style Roman tokens, for example: "iib57".
        /// - Diminished chords can be written with "o" or "dim", for example: "viio" or "viidim7".
        /// </param>
        /// <param name="tonicMidi">MIDI note number used as the tonic reference for degree I/i.</param>
        /// <returns>Resolved chord steps ready to map into chord-library names and tonic notes.</returns>
        public static List<MPTKResolvedChordStep> ResolveDegrees(string[] degreeTokens, int tonicMidi)
        {
            List<MPTKResolvedChordStep> result = new List<MPTKResolvedChordStep>();
            if (degreeTokens == null)
                return result;

            tonicMidi = Mathf.Clamp(tonicMidi, 0, 127);

            for (int i = 0; i < degreeTokens.Length; i++)
            {
                string token = degreeTokens[i];
                MPTKResolvedChordStep step = ResolveOneDegree(token, tonicMidi);
                result.Add(step);
            }

            return result;
        }

        /// <summary>
        /// Build chord-builder instances ready for MidiStreamPlayer.MPTK_PlayChordFromLib().
        /// </summary>
        public static List<MPTKChordBuilder> CreateChordBuilders(
            string[] degreeTokens,
            int tonicMidi,
            int channel = 0,
            int velocity = 100,
            long duration = 500,
            long delayBetweenChords = 500)
        {
            List<MPTKResolvedChordStep> steps = ResolveDegrees(degreeTokens, tonicMidi);
            List<MPTKChordBuilder> chords = new List<MPTKChordBuilder>(steps.Count);

            channel = Mathf.Clamp(channel, 0, 15);
            velocity = Mathf.Clamp(velocity, 0, 127);
            duration = duration < -1 ? -1 : duration;
            delayBetweenChords = Math.Max(0, delayBetweenChords);

            long delay = 0;
            for (int i = 0; i < steps.Count; i++)
            {
                MPTKChordBuilder chord = new MPTKChordBuilder()
                {
                    Tonic = steps[i].TonicMidi,
                    FromLib = (int)steps[i].ChordName,
                    Channel = channel,
                    Velocity = velocity,
                    Duration = duration,
                    Delay = delay,
                    Arpeggio = 0,
                };

                chords.Add(chord);
                delay += delayBetweenChords;
            }

            return chords;
        }

        private static MPTKResolvedChordStep ResolveOneDegree(string token, int tonicMidi)
        {
            string trimmed = string.IsNullOrWhiteSpace(token) ? "I" : token.Trim();

            int accidentalShift;
            string romanCore;
            string suffix;
            bool parsed = TrySplitDegree(trimmed, out accidentalShift, out romanCore, out suffix);
            if (!parsed)
            {
                return new MPTKResolvedChordStep(
                    trimmed,
                    tonicMidi,
                    MPTKChordName.Major,
                    true,
                    "Invalid degree token. Fallback to I major.");
            }

            int degreeIndex;
            if (!TryRomanToDegreeIndex(romanCore, out degreeIndex))
            {
                return new MPTKResolvedChordStep(
                    trimmed,
                    tonicMidi,
                    MPTKChordName.Major,
                    true,
                    "Unsupported Roman numeral. Fallback to I major.");
            }

            int semitone = MajorDegreeSemitone[degreeIndex] + accidentalShift;
            while (semitone < 0) semitone += 12;
            semitone %= 12;

            int rootMidi = Mathf.Clamp(tonicMidi + semitone, 0, 127);

            bool isUpper = string.Equals(romanCore, romanCore.ToUpperInvariant(), StringComparison.Ordinal);
            TriadQuality quality = isUpper ? TriadQuality.Major : TriadQuality.Minor;

            string suffixLower = suffix.ToLowerInvariant();

            bool hasMaj7 = suffixLower.Contains("maj7");
            bool has7 = suffixLower.Contains("7");
            bool hasFlat5 = suffixLower.Contains("b5");
            bool hasSharp5 = suffixLower.Contains("#5") || suffixLower.Contains("aug") || suffixLower.Contains("+");
            bool hasSus2 = suffixLower.Contains("sus2");
            bool hasSus4 = suffixLower.Contains("sus4") || (suffixLower.Contains("sus") && !hasSus2);

            // ASCII-only parsing:
            // - "dim" or leading "o" => diminished
            // - "b5"+"7" on a minor Roman chord can resolve to m7b5
            bool forceDim = suffixLower.Contains("dim")
                         || suffixLower.StartsWith("o", StringComparison.Ordinal);

            if (hasSus2)
                quality = TriadQuality.Suspended2;
            else if (hasSus4)
                quality = TriadQuality.Suspended4;
            else if (forceDim)
                quality = TriadQuality.Diminished;
            else if (hasSharp5)
                quality = TriadQuality.Augmented;
            // Important: do NOT force minor+b5 to Diminished here.
            // It may need to map to m7b5 when a 7th is present.

            bool approximate = false;
            string note = string.Empty;

            MPTKChordName chordName = ChooseChordName(
                quality,
                hasMaj7,
                has7,
                hasFlat5,
                hasSharp5,
                ref approximate,
                ref note);

            return new MPTKResolvedChordStep(trimmed, rootMidi, chordName, approximate, note);
        }

        private static MPTKChordName ChooseChordName(
            TriadQuality quality,
            bool hasMaj7,
            bool has7,
            bool hasFlat5,
            bool hasSharp5,
            ref bool approximate,
            ref string note)
        {
            switch (quality)
            {
                case TriadQuality.Suspended2:
                    if (has7)
                    {
                        approximate = true;
                        note = "sus2 seventh is not directly available. Using sus2 triad.";
                    }
                    return MPTKChordName.Ssus2;

                case TriadQuality.Suspended4:
                    if (has7)
                        return MPTKChordName.S7sus4;
                    return MPTKChordName.Ssus4;

                case TriadQuality.Diminished:
                    if (has7)
                        return MPTKChordName.Dim7;
                    return MPTKChordName.Dim;

                case TriadQuality.Augmented:
                    if (has7)
                        return MPTKChordName.A7aug;
                    if (hasMaj7)
                        return MPTKChordName.M7Sharp5;
                    return MPTKChordName.MSharp5;

                case TriadQuality.Minor:
                    if (hasFlat5 && has7)
                        return MPTKChordName.m7b5;

                    if (hasFlat5)
                    {
                        approximate = true;
                        note = "Minor flat-five triad is not directly available. Using diminished triad.";
                        return MPTKChordName.Dim;
                    }

                    if (hasSharp5 && has7)
                        return MPTKChordName.mm7Sharp5;
                    if (hasSharp5)
                        return MPTKChordName.mSharp5;
                    if (hasMaj7)
                        return MPTKChordName.mM7;
                    if (has7)
                        return MPTKChordName.m7;
                    return MPTKChordName.minor;

                default:
                    if (hasSharp5 && has7)
                        return MPTKChordName.A7aug;
                    if (hasSharp5 && hasMaj7)
                        return MPTKChordName.M7Sharp5;
                    if (hasSharp5)
                        return MPTKChordName.MSharp5;

                    if (hasFlat5 && has7)
                        return MPTKChordName.D7b5;

                    if (hasMaj7)
                        return MPTKChordName.M7;

                    if (has7)
                    {
                        // Closest dominant-family shape available in MPTKChordName:
                        // root + major third + minor seventh.
                        approximate = true;
                        note = "Dominant seventh mapped to M7no5 (root, major third, minor seventh).";
                        return MPTKChordName.M7no5;
                    }

                    return MPTKChordName.Major;
            }
        }

        private static bool TrySplitDegree(string token, out int accidentalShift, out string romanCore, out string suffix)
        {
            accidentalShift = 0;
            romanCore = string.Empty;
            suffix = string.Empty;

            if (string.IsNullOrWhiteSpace(token))
                return false;

            int i = 0;
            while (i < token.Length && (token[i] == 'b' || token[i] == '#'))
            {
                accidentalShift += token[i] == 'b' ? -1 : 1;
                i++;
            }

            int romanStart = i;
            while (i < token.Length && IsRomanChar(token[i]))
                i++;

            if (i <= romanStart)
                return false;

            romanCore = token.Substring(romanStart, i - romanStart);
            suffix = i < token.Length ? token.Substring(i) : string.Empty;
            return true;
        }

        private static bool IsRomanChar(char c)
        {
            switch (c)
            {
                case 'I':
                case 'V':
                case 'i':
                case 'v':
                    return true;
                default:
                    return false;
            }
        }

        private static bool TryRomanToDegreeIndex(string romanCore, out int degreeIndex)
        {
            degreeIndex = 0;
            if (string.IsNullOrEmpty(romanCore))
                return false;

            switch (romanCore.ToUpperInvariant())
            {
                case "I":
                    degreeIndex = 0;
                    return true;
                case "II":
                    degreeIndex = 1;
                    return true;
                case "III":
                    degreeIndex = 2;
                    return true;
                case "IV":
                    degreeIndex = 3;
                    return true;
                case "V":
                    degreeIndex = 4;
                    return true;
                case "VI":
                    degreeIndex = 5;
                    return true;
                case "VII":
                    degreeIndex = 6;
                    return true;
                default:
                    return false;
            }
        }
    }
}
