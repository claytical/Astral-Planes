using System.Collections.Generic;
using UnityEngine;

namespace MidiPlayerTK
{
    /// @ingroup midistreamplayer_chord_tools
    /// <summary>
    /// Build chords and play them with MidiStreamPlayer.\n
    /// @version Maestro Pro 
    /// See example in TestMidiStream.cs and ExtStreamPlayerPro.cs
    /// </summary>
    public class MPTKChordBuilder
    {
        public enum Modifier3
        {
            Maj, // Accord majeur : tierce majeur, quinte juste ( 1 3 5 ). Do Mi Sol
            Min, // Accord mineur : tierce mineur, quinte juste ( 1 3b 5). Do Re# Sol
            Dim, // Accord diminué : tierce mineur, quinte diminuée ( 1 3b 5b). Do Re# Fa#
            DimHalf, // Accord demi-diminué : tierce majeur, quinte diminuée (1 3 5b). Do Mi Fa#
            Aug, // Accord augmenté : tierce majeur, quinte augmentée (1 3 5#).  Do Mi Sol#
            Sus2, // Accord suspendu 2 : la tierce est remplacée par une seconde (1 2 5). Do Re Sol
            Sus4, // Accord suspendu 4 : la tierce est remplacée par une quarte (1 4 5). Do Fa Sol 
        }

        public enum Modifier4
        {
            Maj6, // tierce majeure ou mineure et une sixieme majeure. Do Mi/Mib Sol La
            Min6, // tierce majeure ou mineure et une sixieme mineure. Do Mi/Mib Sol Lab
            Maj7, // tierce majeure ou mineure et une septième majeure. Do Mi/Mib Sol Si
            Min7, // tierce majeure ou mineure et une septième mineure. Do Mi/Mib Sol Sib
        }

        /// <summary>@brief
        /// Tonic (Root) for the chord. 48=C3, ... , 60=C4, 61=C4#, 62=D4, ... , 72=C5, ....
        /// </summary>
        public int Tonic;

        /// <summary>@brief
        /// Number of notes used to compose the chord. Between 2 and 20.
        /// </summary>
        public int Count;

        /// <summary>@brief
        /// Scale Degree. Between 1 and 7.
        /// @li I   Tonic       First
        /// @li II  Supertonic  Second
        /// @li III Mediant     Maj or min Third
        /// @li IV  Subdominant Fourth
        /// @li V   Dominant    Fifth
        /// @li VI  Submediant  Maj or min Sixth
        /// @li VII Leading Tone/Subtonic Maj or min Seventh
        ///! Good reading here: https://lotusmusic.com/lm_chordnames.html
        /// </summary>
        public int Degree;

        /// <summary>@brief
        /// Index of the chord in the library file `ChordLib.csv` in `Resources/GeneratorTemplate`. To be used with `MidiStreamPlayer.MPTK_PlayChordFromLib(MPTKChord chord)`.
        /// </summary>
        public int FromLib;

        /// <summary>@brief
        /// MIDI channel from 0 to 15 (9 for drums).
        /// </summary>
        public int Channel;

        /// <summary>@brief
        /// Velocity between 0 and 127.
        /// </summary>
        public int Velocity;

        /// <summary>@brief
        /// Duration of the chord in milliseconds. Set `-1` to play indefinitely.
        /// </summary>
        public long Duration;

        /// <summary>@brief
        /// Delay in milliseconds before playing the chord.
        /// </summary>
        public long Delay;

        /// <summary>@brief
        /// Delay in milliseconds between each note in the chord (plays an arpeggio).
        /// </summary>
        public long Arpeggio;

        /// <summary>@brief
        /// List of MIDI events played for this chord. This list is built when `MPTK_PlayChord` or `MPTK_PlayChordFromLib` is called; otherwise, null.
        /// </summary>
        public List<MPTKEvent> Events;

        //// https://www.bellandcomusic.com/building-chords.html
        //public bool Alterations;

        private bool logChord;

        /// <summary>@brief
        /// Creates a default chord: tonic=C4, degree=1, note count=3.
        /// </summary>
        /// <param name="log">True to display log</param>
        public MPTKChordBuilder(bool log = false)
        {
            logChord = log;
            Tonic = 48;
            Degree = 1;
            Count = 3;
            Duration = -1; // indefinitely
            Channel = 0;
            Delay = 0;
            Arpeggio = 0;
            Velocity = 127; // max
        }

        private long Clamp(long val, long min, long max)
        {
            return val > max ? max : val < min ? min : val;
        }

        /// <summary>@brief
        /// Builds a chord from the current selected scale/range. Tonic and Degree must be defined in the `MPTKChordBuilder` instance.
        /// The major scale is selected if no scale is defined. After the call, `Events` contains all notes for the chord.
        /// @version Maestro Pro 
        /// </summary>
        /// <param name="scale"></param>
        public void MPTK_BuildFromRange(MPTKScaleLib scale = null)
        {
            if (scale == null) scale = MPTKScaleLib.CreateScale(0, logChord);
            Tonic = Mathf.Clamp(Tonic, 0, 127);
            Count = Mathf.Clamp(Count, 2, 50);
            Degree = Mathf.Clamp(Degree, 1, 7);
            Velocity = Mathf.Clamp(Velocity, 0, 127);
            Duration = Clamp(Duration, -1, 999999);
            Delay = Clamp(Delay, 0, 999999);
            Arpeggio = Clamp(Arpeggio, 0, 1000);

            Events = new List<MPTKEvent>();

            for (int iNote = 0; iNote < Count; iNote++)
            {
                int value = Tonic + scale[Degree - 1 + iNote * 2];
                if (value > 127) break;
                Events.Add(new MPTKEvent()
                {
                    Command = MPTKCommand.NoteOn,
                    Value = value,
                    Delay = Delay + Arpeggio * iNote, // time to start playing the note
                    Channel = Channel,
                    Duration = Duration, // real duration. Set to -1 to indefinitely
                    Velocity = Velocity
                });
            }

            if (logChord)
            {
                string info = string.Format("Tonic:{0} Degree:{1}", HelperNoteLabel.LabelFromMidi(Tonic), Degree);
                foreach (MPTKEvent evnt in Events)
                    info += " " + HelperNoteLabel.LabelFromMidi(evnt.Value);
                Debug.Log(info);
            }
        }


        /// <summary>@brief
        /// Builds a chord from the current chord in the `ChordLib.csv` library in `Resources/GeneratorTemplate`.
        /// @version Maestro Pro 
        /// </summary>
        /// <param name="chordName">Name of the chord.</param>
        public void MPTK_BuildFromLib(MPTKChordName chordName)
        {
            MPTK_BuildFromLib((int)chordName);
        }

        /// <summary>@brief
        /// Builds a chord.
        /// @version Maestro Pro 
        /// </summary>
        /// <param name="pindex">Position starting from 0 in `ChordLib.csv`.</param>
        public void MPTK_BuildFromLib(int pindex)
        {
            int index = Mathf.Clamp(pindex, 0, MPTKChordLib.ChordCount - 1);
            MPTKChordLib chorLib = MPTKChordLib.Chords[index];

            Tonic = Mathf.Clamp(Tonic, 0, 127);
            Velocity = Mathf.Clamp(Velocity, 0, 127);
            Duration = Clamp(Duration, -1, 999999);
            Delay = Clamp(Delay, 0, 999999);
            Arpeggio = Clamp(Arpeggio, 0, 1000);

            Events = new List<MPTKEvent>();

            // Add each notes to compose the chord. 
            for (int iNote = 0; iNote < chorLib.Count; iNote++)
            {
                int value = Tonic + chorLib[iNote];
                Events.Add(new MPTKEvent()
                {
                    Command = MPTKCommand.NoteOn,
                    Value = value,
                    Delay = Delay + Arpeggio * iNote, // time to start playing the note
                    Channel = Channel,
                    Duration = Duration, // real duration. Set to -1 to indefinitely
                    Velocity = Velocity
                });
            }

            if (logChord)
            {
                string info = string.Format("Tonic:{0} Degree:{1}", HelperNoteLabel.LabelFromMidi(Tonic), Degree);
                foreach (MPTKEvent evnt in Events)
                    info += " " + HelperNoteLabel.LabelFromMidi(evnt.Value);
                Debug.Log(info);
            }
        }
    }
}
