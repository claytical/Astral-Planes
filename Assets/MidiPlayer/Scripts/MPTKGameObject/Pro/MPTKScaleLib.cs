using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace MidiPlayerTK
{
    /// @ingroup midistreamplayer_chord_tools
    /// <summary>
    /// Build scales and play them with MidiStreamPlayer.\n
    /// @version Maestro Pro 
    /// 
    /// See example in TestMidiStream.cs and ExtStreamPlayerPro.cs
    /// @code
    ///
    ///     // Need a reference to the prefab MidiStreamPlayer you have added in your scene hierarchy.
    ///     public MidiStreamPlayer midiStreamPlayer;
    ///     
    ///     new void Start()
    ///     {
    ///         // Find the MidiStreamPlayer. It can also be set directly from the inspector.
    ///         midiStreamPlayer = FindFirstObjectByType<MidiStreamPlayer>();
    ///     }
    ///
    ///     private void PlayScale()
    ///     {
    ///         // Get the currently selected scale
    ///         MPTKRangeLib range = MPTKRangeLib.Range(CurrentScale, true);
    ///         for (int ecart = 0; ecart < range.Count; ecart++)
    ///         {
    ///             NotePlaying = new MPTKEvent()
    ///             {
    ///                 Command = MPTKCommand.NoteOn, // MIDI command
    ///                 Value = CurrentNote + range[ecart], // from 0 to 127, 48 for C3, 60 for C4, ...
    ///                 Channel = StreamChannel, // from 0 to 15, 9 reserved for drum
    ///                 Duration = DelayPlayScale, // note duration in milliseconds, -1 to play indefinitely, MPTK_StopEvent to stop
    ///                 Velocity = Velocity, // from 0 to 127, sound can vary depending on the velocity
    ///                 Delay = ecart * DelayPlayScale, // delay in milliseconds before playing the note
    ///             };
    ///             midiStreamPlayer.MPTK_PlayEvent(NotePlaying);
    ///         }
    ///     }
    /// @endcode
    /// </summary>
    public class MPTKScaleLib
    {
        /// <summary>@brief
        /// Position in the list (from the library)
        /// </summary>
        public int Index;

        /// <summary>@brief
        /// Long name of the scale.
        /// </summary>
        public string Name;

        /// <summary>@brief
        /// Short name of the scale.
        /// </summary>
        public string Short;

        /// <summary>@brief
        /// Some indicator when available.
        /// @li   M = major scale
        /// @li   m = minor scale
        /// @li   _ = undetermined
        /// </summary>
        public string Flag;

        /// <summary>@brief
        /// True for a common scale; otherwise, exotic.
        /// </summary>
        public bool Main;

        /// <summary>@brief
        /// Number of notes in the scale.
        /// </summary>
        public int Count;

        /// <summary>@brief
        /// Indexer on an instance of this class.\n
        /// Provides access to each interval in semitones from the tonic.\n
        /// For a Major Melodic scale, each index returns 0, 2, 4, 5, 7, 9, 11.
        /// The first position (`index=0`) always returns 0 because it is the interval count from the tonic.
        /// </summary>
        /// <param name="index">Index in the scale. If greater than interval count in the scale, the interval in semitones is taken from the next octave.</param>
        /// <returns>Interval in semitones from the tonic.</returns>
        /// @code
        /// // Create a scale from the first scale found in the library: "Major melodic"
        /// // Log enabled to display the content of the scale.
        /// mptkScale = MPTKRangeLib.Range(indexScale:0, log:true);
        /// Debug.Log(mptkScale[0]) // display 0
        /// Debug.Log(mptkScale[4]) // display 7
        /// @endcode
        public int this[int index]
        {
            get
            {
                if (Count == 0) return 0;
                if (octave == null) BuildOctave();
                int delta = 0;
                try
                {
                    delta = octave[index % Count] + ((index / Count) * 12);
                }
                catch (System.Exception ex)
                {
                    MidiPlayerGlobal.ErrorDetail(ex);
                }
                return delta;
            }
        }

        private int[] octave;

        /// <summary>@brief
        /// A full scale is based on 12 semitones. This array contains semitones selected for the scale.
        /// </summary>
        private string[] position;

        private static List<MPTKScaleLib> scales;

        /// <summary>@brief
        /// Gets a scale from an index. Scales are read from `GammeDefinition.csv` in `Resources/GeneratorTemplate`.
        /// </summary>
        /// <param name="index"></param>
        /// <param name="log"></param>
        /// <returns></returns>
        public static MPTKScaleLib CreateScale(MPTKScaleName index, bool log = false)
        {
            if (scales == null) Init(log);
            scales[(int)index].BuildOctave(log);
            return scales[(int)index];
        }

        /// <summary>@brief
        /// Number of available scales in the `GammeDefinition.csv` library in `Resources/GeneratorTemplate`.
        /// </summary>
        public static int RangeCount
        {
            get
            {
                if (scales == null) Init();
                return scales.Count;
            }
        }

        private static void Init(bool log = false)
        {
            try
            {
                if (scales == null)
                {
                    scales = new List<MPTKScaleLib>();
                    TextAsset mytxtData = Resources.Load<TextAsset>("GeneratorTemplate/GammeDefinition");
                    string text = System.Text.Encoding.UTF8.GetString(mytxtData.bytes);
                    string[] list1 = text.Split('\r');
                    if (list1.Length >= 1)
                    {
                        for (int i = 1; i < list1.Length; i++)
                        {
                            string[] c = list1[i].Split(';');
                            if (c.Length >= 15)
                            {
                                MPTKScaleLib scale = new MPTKScaleLib();
                                try
                                {
                                    scale.Index = scales.Count;
                                    scale.Name = c[0];
                                    if (scale.Name[0] == '\n') scale.Name = scale.Name.Remove(0, 1);
                                    scale.Short = c[1];
                                    scale.Flag = c[2];
                                    scale.Main = (c[3].ToUpper() == "X") ? true : false;
                                    scale.Count = Convert.ToInt32(c[4]);
                                    scale.position = new string[12];
                                    for (int j = 5; j <= 16; j++)
                                    {
                                        scale.position[j - 5] = c[j];
                                    }
                                }
                                catch (System.Exception ex)
                                {
                                    MidiPlayerGlobal.ErrorDetail(ex);
                                }
                                scales.Add(scale);
                            }
                        }

                    }
                    if (log)
                        Debug.Log("Ranges loaded: " + MPTKScaleLib.scales.Count);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("MPTKScaleLib error");
                Debug.LogException(ex);
                scales = null; ;
            }
        }

        private void BuildOctave(bool log = false)
        {
            if (octave == null)
            {
                try
                {
                    octave = new int[Count];
                    int iEcart = 0;
                    int vEcart = 1;
                    octave[0] = 0;
                    iEcart++;
                    for (int i = 1; i < position.Length; i++)
                    {
                        if (position[i].Trim().Length == 0)
                        {
                            vEcart++;
                        }
                        else
                        {
                            octave[iEcart] = vEcart;
                            iEcart++;
                            vEcart += 1;
                        }
                    }
                    //octave[octave.Length - 1] = 12;
                }
                catch (System.Exception ex)
                {
                    MidiPlayerGlobal.ErrorDetail(ex);
                }

                if (log)
                {
                    Debug.Log($"Scale: Flag:{Flag}  Name:'{Name}'  Count:{Count} intervals in the scale");
                    Debug.Log("Example with tonic on C4 (48)");
                    Debug.Log("   Index   semitones count  Note result");
                    int index = 0;
                    foreach (int e in octave)
                        Debug.Log($"     {index++}             {e,2}            {HelperNoteLabel.LabelFromMidi(48 + e)}");
                }
            }
        }
    }

    /// @ingroup midistreamplayer_chord_tools
    /// <summary>
    /// List of available scale presets loaded from GammeDefinition.csv.
    /// Each enum value maps to one CSV row (same zero-based index).
    /// Flag indicates the main family when available: M = Major, m = Minor, _ = other or modal.
    /// Main marks commonly used scales in the source library (X/x).
    /// Pitch columns (Do to Si) define the 12 pitch classes of one octave and store degree labels when present.
    /// Per-value documentation shows source names, degree formulas, and active notes in the C reference scale.
    /// During playback, this pitch-class pattern is transposed from C to the selected tonic.
    /// Scale names and formulas were collected from external online sources and can include non-standard naming.
    /// @version Maestro Pro 
    /// </summary>
    public enum MPTKScaleName
    {
        /// <summary>
        /// Scale "Major melodic" (short: "Major"). Family: Major. Main scale in source library.
        /// Intervals from tonic: root, major second, major third, perfect fourth, perfect fifth, major sixth, major seventh.
        /// Notes in C: C, D, E, F, G, A, B. Semitones from tonic: 0, 2, 4, 5, 7, 9, 11.
        /// </summary>
        MajorMelodic = 0,
        /// <summary>
        /// Scale "Major harmonic" (short: "Major Har"). Family: Major. Main scale in source library.
        /// Intervals from tonic: root, major second, major third, perfect fourth, perfect fifth, minor sixth, major seventh.
        /// Notes in C: C, D, E, F, G, G#, B. Semitones from tonic: 0, 2, 4, 5, 7, 8, 11.
        /// </summary>
        MajorHarmonic = 1,
        /// <summary>
        /// Scale "Minor natural" (short: "Minor Nat"). Family: Minor. Main scale in source library.
        /// Intervals from tonic: root, major second, minor third, perfect fourth, perfect fifth, minor sixth, minor seventh.
        /// Notes in C: C, D, D#, F, G, G#, A#. Semitones from tonic: 0, 2, 3, 5, 7, 8, 10.
        /// </summary>
        MinorNatural = 2,
        /// <summary>
        /// Scale "Minor melodic" (short: "Minor Mel"). Family: Minor. Main scale in source library.
        /// Intervals from tonic: root, major second, minor third, perfect fourth, perfect fifth, major sixth, major seventh.
        /// Notes in C: C, D, D#, F, G, A, B. Semitones from tonic: 0, 2, 3, 5, 7, 9, 11.
        /// </summary>
        MinorMelodic = 3,
        /// <summary>
        /// Scale "Minor harmonic" (short: "Minor Har"). Family: Minor. Main scale in source library.
        /// Intervals from tonic: root, major second, minor third, perfect fourth, perfect fifth, minor sixth, major seventh.
        /// Notes in C: C, D, D#, F, G, G#, B. Semitones from tonic: 0, 2, 3, 5, 7, 8, 11.
        /// </summary>
        MinorHarmonic = 4,
        /// <summary>
        /// Scale "Pentatonic Major" (short: "Penta M"). Family: Major. Main scale in source library.
        /// Intervals from tonic: root, major second, major third, perfect fifth, major sixth.
        /// Notes in C: C, D, E, G, A. Semitones from tonic: 0, 2, 4, 7, 9.
        /// </summary>
        PentatonicMajor = 5,
        /// <summary>
        /// Scale "Pentatonic Minor" (short: "Penta m"). Family: Minor. Main scale in source library.
        /// Intervals from tonic: root, minor third, perfect fourth, perfect fifth, minor seventh.
        /// Notes in C: C, D#, F, G, A#. Semitones from tonic: 0, 3, 5, 7, 10.
        /// </summary>
        PentatonicMinor = 6,
        /// <summary>
        /// Scale "Chromatic" (short: "Chromatic"). Family: Other/Modal. Main scale in source library.
        /// Intervals from tonic: root, flat second, major second, minor third, major third, perfect fourth, tritone, perfect fifth, minor sixth, major sixth, minor seventh, major seventh.
        /// Notes in C: C, C#, D, D#, E, F, F#, G, G#, A, A#, B. Semitones from tonic: 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11.
        /// </summary>
        Chromatic = 7,
        /// <summary>
        /// Scale "Blues" (short: "Blues"). Family: Other/Modal. Main scale in source library.
        /// Intervals from tonic: root, minor third, perfect fourth, tritone, perfect fifth, minor seventh, major seventh.
        /// Notes in C: C, D#, F, F#, G, A#, B. Semitones from tonic: 0, 3, 5, 6, 7, 10, 11.
        /// </summary>
        Blues = 8,
        /// <summary>
        /// Scale "Enigmatic 1" (short: "Enigmatic 1"). Family: Other/Modal. Main scale in source library.
        /// Intervals from tonic: root, flat second, major third, tritone, minor sixth, minor seventh, major seventh.
        /// Notes in C: C, C#, E, F#, G#, A#, B. Semitones from tonic: 0, 1, 4, 6, 8, 10, 11.
        /// </summary>
        Enigmatic1 = 9,
        /// <summary>
        /// Scale "Enigmatic 2" (short: "Enigmatic 2"). Family: Other/Modal. Not marked as main in source library.
        /// Intervals from tonic: root, flat second, major third, tritone, minor sixth, minor seventh.
        /// Notes in C: C, C#, E, F#, G#, A#. Semitones from tonic: 0, 1, 4, 6, 8, 10.
        /// </summary>
        Enigmatic2 = 10,
        /// <summary>
        /// Scale "Gitane" (short: "Gitane"). Family: Other/Modal. Main scale in source library.
        /// Intervals from tonic: root, flat second, major third, perfect fourth, perfect fifth, major sixth, minor seventh.
        /// Notes in C: C, C#, E, F, G, A, A#. Semitones from tonic: 0, 1, 4, 5, 7, 9, 10.
        /// </summary>
        Gitane = 11,
        /// <summary>
        /// Scale "Oriental (first form)" (short: "Oriental 1"). Family: Other/Modal. Main scale in source library.
        /// Intervals from tonic: root, flat second, major third, perfect fourth, tritone, minor sixth, minor seventh.
        /// Notes in C: C, C#, E, F, F#, G#, A#. Semitones from tonic: 0, 1, 4, 5, 6, 8, 10.
        /// </summary>
        Oriental1 = 12,
        /// <summary>
        /// Scale "Bebop Major" (short: "Bebop M"). Family: Major. Main scale in source library.
        /// Intervals from tonic: root, major second, major third, perfect fourth, perfect fifth, minor sixth, major sixth, major seventh.
        /// Notes in C: C, D, E, F, G, G#, A, B. Semitones from tonic: 0, 2, 4, 5, 7, 8, 9, 11.
        /// </summary>
        BebopMajor = 13,
        /// <summary>
        /// Scale "Aeolien b5" (short: "Aeolien b5"). Family: Other/Modal. Not marked as main in source library.
        /// Intervals from tonic: root, major second, minor third, perfect fourth, tritone, minor sixth, minor seventh.
        /// Notes in C: C, D, D#, F, F#, G#, A#. Semitones from tonic: 0, 2, 3, 5, 6, 8, 10.
        /// </summary>
        AeolienB5 = 14,
        /// <summary>
        /// Scale "Arabic" (short: "Arabe"). Family: Other/Modal. Not marked as main in source library.
        /// Intervals from tonic: root, major second, major third, perfect fourth, tritone, minor sixth, minor seventh.
        /// Notes in C: C, D, E, F, F#, G#, A#. Semitones from tonic: 0, 2, 4, 5, 6, 8, 10.
        /// </summary>
        Arabic = 15,
        /// <summary>
        /// Scale "Augmented" (short: "increasede"). Family: Other/Modal. Not marked as main in source library.
        /// Intervals from tonic: root, minor third, major third, perfect fifth, minor sixth, major seventh.
        /// Notes in C: C, D#, E, G, G#, B. Semitones from tonic: 0, 3, 4, 7, 8, 11.
        /// </summary>
        Augmented = 16,
        /// <summary>
        /// Scale "Bahar" (short: "Bahar"). Family: Other/Modal. Not marked as main in source library.
        /// Intervals from tonic: root, major second, minor third, perfect fourth, perfect fifth, minor seventh.
        /// Notes in C: C, D, D#, F, G, A#. Semitones from tonic: 0, 2, 3, 5, 7, 10.
        /// </summary>
        Bahar = 17,
        /// <summary>
        /// Scale "Balinaise" (short: "Balinaise"). Family: Other/Modal. Not marked as main in source library.
        /// Intervals from tonic: root, flat second, minor third, perfect fifth, minor sixth.
        /// Notes in C: C, C#, D#, G, G#. Semitones from tonic: 0, 1, 3, 7, 8.
        /// </summary>
        Balinaise = 18,
        /// <summary>
        /// Scale "Bartock" (short: "Bartock"). Family: Other/Modal. Not marked as main in source library.
        /// Intervals from tonic: root, major second, major third, tritone, perfect fifth, major sixth, minor seventh.
        /// Notes in C: C, D, E, F#, G, A, A#. Semitones from tonic: 0, 2, 4, 6, 7, 9, 10.
        /// </summary>
        Bartock = 19,
        /// <summary>
        /// Scale "Bebop dominante" (short: "Bebop dom"). Family: Other/Modal. Not marked as main in source library.
        /// Intervals from tonic: root, major second, major third, perfect fourth, perfect fifth, major sixth, minor seventh, major seventh.
        /// Notes in C: C, D, E, F, G, A, A#, B. Semitones from tonic: 0, 2, 4, 5, 7, 9, 10, 11.
        /// </summary>
        BebopDominante = 20,
        /// <summary>
        /// Scale "Aeolien" (short: "Aeolien"). Family: Other/Modal. Not marked as main in source library.
        /// Intervals from tonic: root, major second, minor third, perfect fourth, perfect fifth, minor sixth, minor seventh.
        /// Notes in C: C, D, D#, F, G, G#, A#. Semitones from tonic: 0, 2, 3, 5, 7, 8, 10.
        /// </summary>
        Aeolien = 21,
        /// <summary>
        /// Scale "Bebop minor" (short: "Bebop m"). Family: Other/Modal. Not marked as main in source library.
        /// Intervals from tonic: root, major second, minor third, major third, perfect fourth, perfect fifth, major sixth, minor seventh.
        /// Notes in C: C, D, D#, E, F, G, A, A#. Semitones from tonic: 0, 2, 3, 4, 5, 7, 9, 10.
        /// </summary>
        BebopMinor = 22,
        /// <summary>
        /// Scale "Bitonal Major chromatique" (short: "Bitonal M"). Family: Major. Not marked as main in source library.
        /// Intervals from tonic: root, minor third, major third, tritone, perfect fifth, major seventh.
        /// Notes in C: C, D#, E, F#, G, B. Semitones from tonic: 0, 3, 4, 6, 7, 11.
        /// </summary>
        BitonalMajorChromatic = 23,
        /// <summary>
        /// Scale "Bitonal minor chromatique" (short: "Bitonal m"). Family: Minor. Not marked as main in source library.
        /// Intervals from tonic: root, major second, minor third, tritone, perfect fifth, major seventh.
        /// Notes in C: C, D, D#, F#, G, B. Semitones from tonic: 0, 2, 3, 6, 7, 11.
        /// </summary>
        BitonalMinorChromatic = 24,
        /// <summary>
        /// Scale "Blues decreased (first form)" (short: "Blues 1 dim"). Family: Other/Modal. Not marked as main in source library.
        /// Intervals from tonic: root, flat second, minor third, major third, tritone, perfect fifth, major sixth, minor seventh.
        /// Notes in C: C, C#, D#, E, F#, G, A, A#. Semitones from tonic: 0, 1, 3, 4, 6, 7, 9, 10.
        /// </summary>
        BluesDecreased1 = 25,
        /// <summary>
        /// Scale "Blues decreased (second form)" (short: "Blues 2 dim"). Family: Other/Modal. Not marked as main in source library.
        /// Intervals from tonic: root, minor third, major third, tritone, perfect fifth, major sixth.
        /// Notes in C: C, D#, E, F#, G, A. Semitones from tonic: 0, 3, 4, 6, 7, 9.
        /// </summary>
        BluesDecreased2 = 26,
        /// <summary>
        /// Scale "Major blues (first form)" (short: "Blues M 1"). Family: Major. Not marked as main in source library.
        /// Intervals from tonic: root, major second, minor third, major third, perfect fifth, major sixth.
        /// Notes in C: C, D, D#, E, G, A. Semitones from tonic: 0, 2, 3, 4, 7, 9.
        /// </summary>
        MajorBlues1 = 27,
        /// <summary>
        /// Scale "Minor blues (first form)" (short: "Blues m 1"). Family: Minor. Not marked as main in source library.
        /// Intervals from tonic: root, minor third, perfect fourth, tritone, perfect fifth, minor seventh.
        /// Notes in C: C, D#, F, F#, G, A#. Semitones from tonic: 0, 3, 5, 6, 7, 10.
        /// </summary>
        MinorBlues1 = 28,
        /// <summary>
        /// Scale "Major blues (second form)" (short: "Blues M 2"). Family: Major. Not marked as main in source library.
        /// Intervals from tonic: root, minor third, perfect fourth, tritone, perfect fifth, major sixth.
        /// Notes in C: C, D#, F, F#, G, A. Semitones from tonic: 0, 3, 5, 6, 7, 9.
        /// </summary>
        MajorBlues2 = 29,
        /// <summary>
        /// Scale "Minor blues (second form)" (short: "Blues m 2"). Family: Minor. Not marked as main in source library.
        /// Intervals from tonic: root, minor third, tritone, minor sixth, major sixth, minor seventh.
        /// Notes in C: C, D#, F#, G#, A, A#. Semitones from tonic: 0, 3, 6, 8, 9, 10.
        /// </summary>
        MinorBlues2 = 30,
        /// <summary>
        /// Scale "Chinese (first  form)" (short: "Chinese 1"). Family: Other/Modal. Not marked as main in source library.
        /// Intervals from tonic: root, major second, perfect fourth, perfect fifth, major sixth.
        /// Notes in C: C, D, F, G, A. Semitones from tonic: 0, 2, 5, 7, 9.
        /// </summary>
        Chinese1 = 31,
        /// <summary>
        /// Scale "Chinese (second form)" (short: "Chinese 2"). Family: Other/Modal. Not marked as main in source library.
        /// Intervals from tonic: root, major third, tritone, perfect fifth, major seventh.
        /// Notes in C: C, E, F#, G, B. Semitones from tonic: 0, 4, 6, 7, 11.
        /// </summary>
        Chinese2 = 32,
        /// <summary>
        /// Scale "Demi-decreased" (short: "Demi-dim"). Family: Other/Modal. Not marked as main in source library.
        /// Intervals from tonic: root, flat second, minor third, major third, tritone, perfect fifth, major sixth, major seventh.
        /// Notes in C: C, C#, D#, E, F#, G, A, B. Semitones from tonic: 0, 1, 3, 4, 6, 7, 9, 11.
        /// </summary>
        DemiDecreased = 33,
        /// <summary>
        /// Scale "Demi-ton tons without sixte" (short: "Demi-ton no sixte"). Family: Other/Modal. Not marked as main in source library.
        /// Intervals from tonic: root, flat second, minor third, major third, tritone, perfect fifth, minor seventh.
        /// Notes in C: C, C#, D#, E, F#, G, A#. Semitones from tonic: 0, 1, 3, 4, 6, 7, 10.
        /// </summary>
        DemiTonNoSixte = 34,
        /// <summary>
        /// Scale "Diminue" (short: "Dim"). Family: Other/Modal. Not marked as main in source library.
        /// Intervals from tonic: root, major second, minor third, perfect fourth, tritone, minor sixth, major sixth, major seventh.
        /// Notes in C: C, D, D#, F, F#, G#, A, B. Semitones from tonic: 0, 2, 3, 5, 6, 8, 9, 11.
        /// </summary>
        Diminish = 35,
        /// <summary>
        /// Scale "Dorien" (short: "Dorien"). Family: Other/Modal. Not marked as main in source library.
        /// Intervals from tonic: root, major second, minor third, perfect fourth, perfect fifth, major sixth, minor seventh.
        /// Notes in C: C, D, D#, F, G, A, A#. Semitones from tonic: 0, 2, 3, 5, 7, 9, 10.
        /// </summary>
        Dorien = 36,
        /// <summary>
        /// Scale "Spanish (first form)" (short: "Spanish 1"). Family: Other/Modal. Not marked as main in source library.
        /// Intervals from tonic: root, flat second, major third, perfect fourth, perfect fifth, minor sixth, minor seventh.
        /// Notes in C: C, C#, E, F, G, G#, A#. Semitones from tonic: 0, 1, 4, 5, 7, 8, 10.
        /// </summary>
        Spanish1 = 37,
        /// <summary>
        /// Scale "Spanish (second form)" (short: "Spanish 2"). Family: Other/Modal. Not marked as main in source library.
        /// Intervals from tonic: root, flat second, minor third, major third, perfect fourth, tritone, minor sixth, minor seventh.
        /// Notes in C: C, C#, D#, E, F, F#, G#, A#. Semitones from tonic: 0, 1, 3, 4, 5, 6, 8, 10.
        /// </summary>
        Spanish2 = 38,
        /// <summary>
        /// Scale "Spanish 8 sons" (short: "Spanish 8"). Family: Other/Modal. Not marked as main in source library.
        /// Intervals from tonic: root, flat second, minor third, major third, perfect fourth, perfect fifth, minor sixth, minor seventh.
        /// Notes in C: C, C#, D#, E, F, G, G#, A#. Semitones from tonic: 0, 1, 3, 4, 5, 7, 8, 10.
        /// </summary>
        Spanish8 = 39,
        /// <summary>
        /// Scale "Gypsy" (short: "Gypsy"). Family: Other/Modal. Not marked as main in source library.
        /// Intervals from tonic: root, flat second, major third, perfect fourth, perfect fifth, minor sixth, major seventh.
        /// Notes in C: C, C#, E, F, G, G#, B. Semitones from tonic: 0, 1, 4, 5, 7, 8, 11.
        /// </summary>
        Gypsy = 40,
        /// <summary>
        /// Scale "Hexa-lydien" (short: "Hexa-lydien"). Family: Other/Modal. Not marked as main in source library.
        /// Intervals from tonic: root, major second, major third, tritone, perfect fifth, major sixth.
        /// Notes in C: C, D, E, F#, G, A. Semitones from tonic: 0, 2, 4, 6, 7, 9.
        /// </summary>
        Hexalydien = 41,
        /// <summary>
        /// Scale "Hexa-melodic" (short: "Hexa-melodic"). Family: Other/Modal. Not marked as main in source library.
        /// Intervals from tonic: root, major second, minor third, perfect fourth, perfect fifth, major sixth.
        /// Notes in C: C, D, D#, F, G, A. Semitones from tonic: 0, 2, 3, 5, 7, 9.
        /// </summary>
        HexaMelodic = 42,
        /// <summary>
        /// Scale "Hexa-phrygien" (short: "Hexa-phrygien"). Family: Other/Modal. Not marked as main in source library.
        /// Intervals from tonic: root, flat second, minor third, perfect fourth, perfect fifth, minor sixth.
        /// Notes in C: C, C#, D#, F, G, G#. Semitones from tonic: 0, 1, 3, 5, 7, 8.
        /// </summary>
        HexaPhrygien = 43,
        /// <summary>
        /// Scale "Hexa-tritonique binaire" (short: "Hexa-trit binaire"). Family: Other/Modal. Not marked as main in source library.
        /// Intervals from tonic: root, minor third, major third, perfect fifth, major sixth, minor seventh.
        /// Notes in C: C, D#, E, G, A, A#. Semitones from tonic: 0, 3, 4, 7, 9, 10.
        /// </summary>
        HexaTritoniqueBinary = 44,
        /// <summary>
        /// Scale "Hexa-tritonique decreased" (short: "Hexa-trit dim 1"). Family: Other/Modal. Not marked as main in source library.
        /// Intervals from tonic: root, major second, minor third, tritone, minor sixth, major sixth.
        /// Notes in C: C, D, D#, F#, G#, A. Semitones from tonic: 0, 2, 3, 6, 8, 9.
        /// </summary>
        HexaTritoniqueDecreased1 = 45,
        /// <summary>
        /// Scale "Hexa-tritonique decreased" (short: "Hexa-trit dim 2"). Family: Other/Modal. Not marked as main in source library.
        /// Intervals from tonic: root, flat second, minor third, tritone, perfect fifth, major sixth.
        /// Notes in C: C, C#, D#, F#, G, A. Semitones from tonic: 0, 1, 3, 6, 7, 9.
        /// </summary>
        HexaTritoniqueDecreased2 = 46,
        /// <summary>
        /// Scale "Hexa-tritonique decreased suspendu" (short: "Hexa-trit dim 3"). Family: Other/Modal. Not marked as main in source library.
        /// Intervals from tonic: root, major second, minor third, tritone, perfect fifth, major sixth.
        /// Notes in C: C, D, D#, F#, G, A. Semitones from tonic: 0, 2, 3, 6, 7, 9.
        /// </summary>
        HexaTritoniqueDecreased3 = 47,
        /// <summary>
        /// Scale "Hindou" (short: "Hindou"). Family: Other/Modal. Not marked as main in source library.
        /// Intervals from tonic: root, major second, major third, perfect fourth, perfect fifth, minor sixth, minor seventh.
        /// Notes in C: C, D, E, F, G, G#, A#. Semitones from tonic: 0, 2, 4, 5, 7, 8, 10.
        /// </summary>
        Hindou = 48,
        /// <summary>
        /// Scale "Hirajoshi" (short: "Hirajoshi"). Family: Other/Modal. Not marked as main in source library.
        /// Intervals from tonic: root, major second, minor third, perfect fifth, minor sixth.
        /// Notes in C: C, D, D#, G, G#. Semitones from tonic: 0, 2, 3, 7, 8.
        /// </summary>
        Hirajoshi = 49,
        /// <summary>
        /// Scale "Hongroise gitane" (short: "Hongroise gitane"). Family: Other/Modal. Not marked as main in source library.
        /// Intervals from tonic: root, major second, minor third, tritone, perfect fifth, minor sixth, minor seventh.
        /// Notes in C: C, D, D#, F#, G, G#, A#. Semitones from tonic: 0, 2, 3, 6, 7, 8, 10.
        /// </summary>
        HongroiseGitane = 50,
        /// <summary>
        /// Scale "Hongroise Major" (short: "Hongroise M"). Family: Major. Not marked as main in source library.
        /// Intervals from tonic: root, minor third, major third, tritone, perfect fifth, major sixth, minor seventh.
        /// Notes in C: C, D#, E, F#, G, A, A#. Semitones from tonic: 0, 3, 4, 6, 7, 9, 10.
        /// </summary>
        HongroiseMajor = 51,
        /// <summary>
        /// Scale "Hongroise Minor" (short: "Hongroise m"). Family: Minor. Not marked as main in source library.
        /// Intervals from tonic: root, major second, minor third, tritone, perfect fifth, minor sixth, major seventh.
        /// Notes in C: C, D, D#, F#, G, G#, B. Semitones from tonic: 0, 2, 3, 6, 7, 8, 11.
        /// </summary>
        HongroiseMinor = 52,
        /// <summary>
        /// Scale "Indoustane" (short: "Indoustane"). Family: Other/Modal. Not marked as main in source library.
        /// Intervals from tonic: root, major third, perfect fourth, perfect fifth, minor sixth, minor seventh.
        /// Notes in C: C, E, F, G, G#, A#. Semitones from tonic: 0, 4, 5, 7, 8, 10.
        /// </summary>
        Indoustane = 53,
        /// <summary>
        /// Scale "Ionien" (short: "Ionien"). Family: Other/Modal. Not marked as main in source library.
        /// Intervals from tonic: root, major second, major third, perfect fourth, perfect fifth, major sixth, major seventh.
        /// Notes in C: C, D, E, F, G, A, B. Semitones from tonic: 0, 2, 4, 5, 7, 9, 11.
        /// </summary>
        Ionien = 54,
        /// <summary>
        /// Scale "Ionien #5" (short: "Ionien 5"). Family: Other/Modal. Not marked as main in source library.
        /// Intervals from tonic: root, major second, major third, perfect fourth, minor sixth, major sixth, major seventh.
        /// Notes in C: C, D, E, F, G#, A, B. Semitones from tonic: 0, 2, 4, 5, 8, 9, 11.
        /// </summary>
        Ionien5 = 55,
        /// <summary>
        /// Scale "Iwato" (short: "Iwato"). Family: Other/Modal. Not marked as main in source library.
        /// Intervals from tonic: root, perfect fourth, tritone, perfect fifth, minor seventh.
        /// Notes in C: C, F, F#, G, A#. Semitones from tonic: 0, 5, 6, 7, 10.
        /// </summary>
        Iwato = 56,
        /// <summary>
        /// Scale "Javanais" (short: "Javanais"). Family: Other/Modal. Not marked as main in source library.
        /// Intervals from tonic: root, flat second, minor third, perfect fourth, perfect fifth, major sixth, minor seventh.
        /// Notes in C: C, C#, D#, F, G, A, A#. Semitones from tonic: 0, 1, 3, 5, 7, 9, 10.
        /// </summary>
        Javanais = 57,
        /// <summary>
        /// Scale "Kokin joshi" (short: "Kokin joshi"). Family: Other/Modal. Not marked as main in source library.
        /// Intervals from tonic: root, flat second, perfect fourth, perfect fifth, minor seventh.
        /// Notes in C: C, C#, F, G, A#. Semitones from tonic: 0, 1, 5, 7, 10.
        /// </summary>
        KokinJoshi = 58,
        /// <summary>
        /// Scale "Kumoi" (short: "Kumoi"). Family: Other/Modal. Not marked as main in source library.
        /// Intervals from tonic: root, flat second, perfect fourth, perfect fifth, minor sixth.
        /// Notes in C: C, C#, F, G, G#. Semitones from tonic: 0, 1, 5, 7, 8.
        /// </summary>
        Kumoi = 59,
        /// <summary>
        /// Scale "Locrien" (short: "Locrien"). Family: Other/Modal. Not marked as main in source library.
        /// Intervals from tonic: root, flat second, minor third, perfect fourth, tritone, minor sixth, minor seventh.
        /// Notes in C: C, C#, D#, F, F#, G#, A#. Semitones from tonic: 0, 1, 3, 5, 6, 8, 10.
        /// </summary>
        Locrien = 60,
        /// <summary>
        /// Scale "Locrien becarre 6" (short: "Locrien 6"). Family: Other/Modal. Not marked as main in source library.
        /// Intervals from tonic: root, flat second, minor third, perfect fourth, tritone, major sixth, minor seventh.
        /// Notes in C: C, C#, D#, F, F#, A, A#. Semitones from tonic: 0, 1, 3, 5, 6, 9, 10.
        /// </summary>
        Locrien6 = 61,
        /// <summary>
        /// Scale "Lydien" (short: "Lydien"). Family: Other/Modal. Not marked as main in source library.
        /// Intervals from tonic: root, major second, major third, tritone, perfect fifth, major sixth, major seventh.
        /// Notes in C: C, D, E, F#, G, A, B. Semitones from tonic: 0, 2, 4, 6, 7, 9, 11.
        /// </summary>
        Lydien1 = 62,
        /// <summary>
        /// Scale "Lydien #2" (short: "Lydien 2"). Family: Other/Modal. Not marked as main in source library.
        /// Intervals from tonic: root, minor third, major third, tritone, perfect fifth, major sixth, major seventh.
        /// Notes in C: C, D#, E, F#, G, A, B. Semitones from tonic: 0, 3, 4, 6, 7, 9, 11.
        /// </summary>
        Lydien2 = 63,
        /// <summary>
        /// Scale "Lydien increased" (short: "Lydien aug"). Family: Other/Modal. Not marked as main in source library.
        /// Intervals from tonic: root, major second, major third, tritone, minor sixth, major sixth, major seventh.
        /// Notes in C: C, D, E, F#, G#, A, B. Semitones from tonic: 0, 2, 4, 6, 8, 9, 11.
        /// </summary>
        Lydien3 = 64,
        /// <summary>
        /// Scale "Mixolydien" (short: "Mixolydien"). Family: Other/Modal. Not marked as main in source library.
        /// Intervals from tonic: root, major second, major third, perfect fourth, perfect fifth, major sixth, minor seventh.
        /// Notes in C: C, D, E, F, G, A, A#. Semitones from tonic: 0, 2, 4, 5, 7, 9, 10.
        /// </summary>
        Mixolydien = 65,
        /// <summary>
        /// Scale "Napolitan Major" (short: "Napolitan M"). Family: Major. Not marked as main in source library.
        /// Intervals from tonic: root, flat second, minor third, perfect fourth, perfect fifth, major sixth, major seventh.
        /// Notes in C: C, C#, D#, F, G, A, B. Semitones from tonic: 0, 1, 3, 5, 7, 9, 11.
        /// </summary>
        NapolitanMajor = 66,
        /// <summary>
        /// Scale "Napolitan Minor" (short: "Napolitan m"). Family: Minor. Not marked as main in source library.
        /// Intervals from tonic: root, flat second, minor third, perfect fourth, perfect fifth, minor sixth, major seventh.
        /// Notes in C: C, C#, D#, F, G, G#, B. Semitones from tonic: 0, 1, 3, 5, 7, 8, 11.
        /// </summary>
        NapolitanMinor = 67,
        /// <summary>
        /// Scale "Oriental (second form)" (short: "Oriental 2"). Family: Other/Modal. Not marked as main in source library.
        /// Intervals from tonic: root, flat second, major third, perfect fourth, tritone, minor sixth, major sixth.
        /// Notes in C: C, C#, E, F, F#, G#, A. Semitones from tonic: 0, 1, 4, 5, 6, 8, 9.
        /// </summary>
        Oriental2 = 68,
        /// <summary>
        /// Scale "Oriental (third form)" (short: "Oriental 3"). Family: Other/Modal. Not marked as main in source library.
        /// Intervals from tonic: root, minor third, major third, perfect fourth, minor sixth, major sixth, major seventh.
        /// Notes in C: C, D#, E, F, G#, A, B. Semitones from tonic: 0, 3, 4, 5, 8, 9, 11.
        /// </summary>
        Oriental3 = 69,
        /// <summary>
        /// Scale "Pentatonic harmonique" (short: "Penta"). Family: Other/Modal. Not marked as main in source library.
        /// Intervals from tonic: root, major second, major third, perfect fifth, minor sixth.
        /// Notes in C: C, D, E, G, G#. Semitones from tonic: 0, 2, 4, 7, 8.
        /// </summary>
        PentatonicHarmonic = 70,
        /// <summary>
        /// Scale "Pentatonic dominante" (short: "Penta dom"). Family: Other/Modal. Not marked as main in source library.
        /// Intervals from tonic: root, major second, major third, perfect fifth, minor seventh.
        /// Notes in C: C, D, E, G, A#. Semitones from tonic: 0, 2, 4, 7, 10.
        /// </summary>
        PentatonicDominante = 71,
        /// <summary>
        /// Scale "Pentatonic egyptian" (short: "Penta egyptian"). Family: Other/Modal. Not marked as main in source library.
        /// Intervals from tonic: root, major second, perfect fourth, perfect fifth, minor seventh.
        /// Notes in C: C, D, F, G, A#. Semitones from tonic: 0, 2, 5, 7, 10.
        /// </summary>
        PentatonicEgyptian = 72,
        /// <summary>
        /// Scale "Pentatonic japanese" (short: "Penta japan"). Family: Other/Modal. Not marked as main in source library.
        /// Intervals from tonic: root, major second, minor third, perfect fifth, major sixth.
        /// Notes in C: C, D, D#, G, A. Semitones from tonic: 0, 2, 3, 7, 9.
        /// </summary>
        PentatonicJapanese = 73,
        /// <summary>
        /// Scale "Pentatonic locrien (first form)" (short: "Penta locrien 1"). Family: Other/Modal. Not marked as main in source library.
        /// Intervals from tonic: root, major second, minor third, tritone, minor sixth.
        /// Notes in C: C, D, D#, F#, G#. Semitones from tonic: 0, 2, 3, 6, 8.
        /// </summary>
        PentatonicLocrien1 = 74,
        /// <summary>
        /// Scale "Pentatonic locrien (second form)" (short: "Penta locrien 2"). Family: Other/Modal. Not marked as main in source library.
        /// Intervals from tonic: root, flat second, minor third, tritone, minor sixth.
        /// Notes in C: C, C#, D#, F#, G#. Semitones from tonic: 0, 1, 3, 6, 8.
        /// </summary>
        PentatonicLocrien2 = 75,
        /// <summary>
        /// Scale "Pentatonic mauritanienne" (short: "Penta mauritanienne"). Family: Other/Modal. Not marked as main in source library.
        /// Intervals from tonic: root, major third, perfect fourth, perfect fifth, major seventh.
        /// Notes in C: C, E, F, G, B. Semitones from tonic: 0, 4, 5, 7, 11.
        /// </summary>
        PentatonicMauritanian = 76,
        /// <summary>
        /// Scale "Pentatonic pelog" (short: "Penta pelog"). Family: Other/Modal. Not marked as main in source library.
        /// Intervals from tonic: root, flat second, minor third, perfect fifth, minor seventh.
        /// Notes in C: C, C#, D#, G, A#. Semitones from tonic: 0, 1, 3, 7, 10.
        /// </summary>
        PentatonicPelog = 77,
        /// <summary>
        /// Scale "Persane (first form)" (short: "Persane 1"). Family: Other/Modal. Not marked as main in source library.
        /// Intervals from tonic: root, flat second, major third, perfect fourth, tritone, perfect fifth, minor seventh.
        /// Notes in C: C, C#, E, F, F#, G, A#. Semitones from tonic: 0, 1, 4, 5, 6, 7, 10.
        /// </summary>
        Persane1 = 78,
        /// <summary>
        /// Scale "Persane (second form)" (short: "Persane 2"). Family: Other/Modal. Not marked as main in source library.
        /// Intervals from tonic: root, flat second, major third, perfect fourth, tritone, minor sixth, major seventh.
        /// Notes in C: C, C#, E, F, F#, G#, B. Semitones from tonic: 0, 1, 4, 5, 6, 8, 11.
        /// </summary>
        Persane2 = 79,
        /// <summary>
        /// Scale "Phrygien" (short: "Phrygien"). Family: Other/Modal. Not marked as main in source library.
        /// Intervals from tonic: root, flat second, minor third, perfect fourth, perfect fifth, minor sixth, minor seventh.
        /// Notes in C: C, C#, D#, F, G, G#, A#. Semitones from tonic: 0, 1, 3, 5, 7, 8, 10.
        /// </summary>
        Phrygien = 80,
        /// <summary>
        /// Scale "Promethee" (short: "Promethee"). Family: Other/Modal. Not marked as main in source library.
        /// Intervals from tonic: root, major second, major third, tritone, major sixth, minor seventh.
        /// Notes in C: C, D, E, F#, A, A#. Semitones from tonic: 0, 2, 4, 6, 9, 10.
        /// </summary>
        Promethee = 81,
        /// <summary>
        /// Scale "Rouman minor" (short: "Rouman m"). Family: Minor. Not marked as main in source library.
        /// Intervals from tonic: root, major second, minor third, tritone, perfect fifth, major sixth, minor seventh.
        /// Notes in C: C, D, D#, F#, G, A, A#. Semitones from tonic: 0, 2, 3, 6, 7, 9, 10.
        /// </summary>
        RoumanMinor = 82,
        /// <summary>
        /// Scale "Superlocrien bb7" (short: "Superlocrien"). Family: Other/Modal. Not marked as main in source library.
        /// Intervals from tonic: root, flat second, minor third, major third, tritone, minor sixth, major sixth.
        /// Notes in C: C, C#, D#, E, F#, G#, A. Semitones from tonic: 0, 1, 3, 4, 6, 8, 9.
        /// </summary>
        SuperlocrienBB7 = 83,
        /// <summary>
        /// Scale "Superlocrien altered" (short: "Superlocrien alt"). Family: Other/Modal. Not marked as main in source library.
        /// Intervals from tonic: root, flat second, minor third, major third, tritone, minor sixth, minor seventh.
        /// Notes in C: C, C#, D#, E, F#, G#, A#. Semitones from tonic: 0, 1, 3, 4, 6, 8, 10.
        /// </summary>
        SuperlocrienAltered = 84,
        /// <summary>
        /// Scale "Ton by ton" (short: "Ton by ton"). Family: Other/Modal. Not marked as main in source library.
        /// Intervals from tonic: root, major second, major third, tritone, minor sixth, minor seventh.
        /// Notes in C: C, D, E, F#, G#, A#. Semitones from tonic: 0, 2, 4, 6, 8, 10.
        /// </summary>
        TonByTon = 85,
    }
}
