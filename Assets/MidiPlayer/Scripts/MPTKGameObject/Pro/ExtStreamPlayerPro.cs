//#define DEBUGPERF
using UnityEngine;

namespace MidiPlayerTK
{
    public partial class MidiStreamPlayer : MidiSynth
    {
        // @cond nodoc
        // Log chord building process in the console. It can be useful to understand how chords are built and to debug the chord library.
        // It is not recommended to set this parameter to true in production as it can generate a lot of logs.
        // Replaced with VerboseChord 
        [HideInInspector] public bool MPTK_LogChord;
        // @endcond 

        private MPTKScaleName currentScaleIndex;
        private MPTKScaleLib scaleLib;

        /// <summary>@brief
        /// Plays a MIDI pitch change event for all notes on the channel.
        /// @ingroup midistreamplayer_pro
        /// @version Maestro Pro 
        /// </summary>
        /// <param name="channel">MIDI channel in the range 0 to 15.</param>
        /// <param name="pitchWheel">Normalized pitch wheel value in the range 0 to 1 (normalized since v2.88.2).
        /// @li  0      minimum (equivalent to value 0 in the MIDI standard event)
        /// @li  0.5    centered (equivalent to value 8192 in the MIDI standard event)
        /// @li  1      maximum (equivalent to value 16383 in the MIDI standard event)
        /// </param> 
        public void MPTK_PlayPitchWheelChange(int channel, float pitchWheel)
        {
            int pitch = (int)Mathf.Lerp(0f, 16383f, pitchWheel);
            MPTK_PlayEvent(new MPTKEvent() { Command = MPTKCommand.PitchWheelChange, Value = pitch, Channel = channel });
        }

        /// <summary>@brief
        /// Plays a MIDI pitch sensitivity change for all notes on the channel.
        /// @ingroup midistreamplayer_pro
        /// @version Maestro Pro 
        /// </summary>
        /// <param name="channel">MIDI channel in the range 0 to 15.</param>
        /// <param name="sensitivity">Pitch bend sensitivity from 0 to 24 semitones. Default is 2.
        /// Example: 4 means the range is -4 to +4 semitones when MPTK_PlayPitchWheelChange moves from 0 to 1.
        /// </param>
        public void MPTK_PlayPitchWheelSensitivity(int channel, int sensitivity)
        {
            sensitivity = Mathf.Clamp(sensitivity, 0, 24);
            // Select the registered parameter number to pitch bend range change
            MPTK_PlayEvent(new MPTKEvent() { Command = MPTKCommand.ControlChange, Controller = MPTKController.RPN_MSB, Value = 0, Channel = channel });
            MPTK_PlayEvent(new MPTKEvent() { Command = MPTKCommand.ControlChange, Controller = MPTKController.RPN_LSB, Value = (int)midi_rpn_event.RPN_PITCH_BEND_RANGE, Channel = channel });
            // Set the new value
            MPTK_PlayEvent(new MPTKEvent() { Command = MPTKCommand.ControlChange, Controller = MPTKController.DATA_ENTRY_MSB, Value = sensitivity, Channel = channel });
            MPTK_PlayEvent(new MPTKEvent() { Command = MPTKCommand.ControlChange, Controller = MPTKController.DATA_ENTRY_LSB, Value = 0, Channel = channel });
        }

        /// <summary>@brief
        /// Name of the currently selected musical scale.
        /// @ingroup midistreamplayer_pro
        /// @ingroup midistreamplayer_chord_tools
        /// @version Maestro Pro 
        /// </summary>
        public string MPTK_ScaleName
        {
            get
            {
                return scaleLib != null ? scaleLib.Name : "Not set";
            }
        }

        /// <summary>@brief
        /// Currently selected scale.
        /// @ingroup midistreamplayer_pro
        /// @ingroup midistreamplayer_chord_tools
        /// @version Maestro Pro 
        /// </summary>
        public MPTKScaleName MPTK_ScaleSelected
        {
            get { return currentScaleIndex; }
            set
            {
                if (currentScaleIndex != value || scaleLib == null)
                {
                    currentScaleIndex = value;
                    scaleLib = MPTKScaleLib.CreateScale(currentScaleIndex, MPTK_LogChord || VerboseChord);
                }
            }
        }

        /// <summary>@brief
        /// Plays a chord from the currently selected scale (MPTK_ScaleSelected).
        /// Tonic and degree are defined in the MPTKChordBuilder parameter.
        /// If no scale is selected, the major scale is used by default.
        /// @ingroup midistreamplayer_pro
        /// @ingroup midistreamplayer_chord_tools
        /// See `GammeDefinition.csv` in `Resources/GeneratorTemplate`.
        /// @version Maestro Pro 
        /// @code
        /// using MidiPlayerTK; // Add a reference to the MPTK namespace at the top of your script
        /// using UnityEngine;
        ///
        /// public class YourClass : MonoBehaviour
        /// {

        ///     // Reference the MidiStreamPlayer prefab added to your scene hierarchy.
        ///     public MidiStreamPlayer midiStreamPlayer;
        /// 
        ///     // This object is passed to MPTK_PlayEvent to play an event.
        ///     MPTKEvent mptkEvent;

        ///     void Start()
        ///     {
        ///         // Find the MidiStreamPlayer. It can also be set directly from the inspector.
        ///         midiStreamPlayer = FindFirstObjectByType<MidiStreamPlayer>();
        ///     }
        ///     private void PlayOneChordFromLib()
        ///     {
        ///         // Start playing a new chord
        ///         MPTKChordBuilder ChordLibPlaying = new MPTKChordBuilder(true)
        ///         {
        ///             // Parameters to build the chord
        ///             Tonic = 60,
        ///             FromLib = 2,
        /// 
        ///             // MIDI parameters for how to play the chord
        ///             Channel = 0,
        ///             // Delay in milliseconds between each note of the chord
        ///             Arpeggio = 100,
        ///             // millisecond, -1 to play indefinitely
        ///             Duration = 500,
        ///             // Sound can vary depending on the velocity
        ///             Velocity = 100,
        ///             Delay = 0,
        ///         };
        ///     midiStreamPlayer.MPTK_PlayChordFromLib(ChordLibPlaying);
        ///     }
        /// }
        /// 
        /// @endcode
        /// </summary>
        /// <param name="chord">Required: Tonic and Degree, in addition to the standard MIDI parameters.</param>
        /// <returns>The same MPTKChordBuilder instance, enriched with generated events.</returns>
        public MPTKChordBuilder MPTK_PlayChordFromScale(MPTKChordBuilder chord)
        {
            try
            {
                if (MPTK_SoundFont.IsReady)
                {
                    chord.Channel = Mathf.Clamp(chord.Channel, 0, MPTK_Channels.Length - 1);

                    // Set a default range
                    if (MPTK_ScaleSelected < 0)
                        // Load scale index 0 (instanciate scaleLib)
                        MPTK_ScaleSelected = 0;

                    chord.MPTK_BuildFromRange(scaleLib);

                    if (!MPTK_CorePlayer)
                        Routine.RunCoroutine(TheadPlay(chord.Events), Segment.RealtimeUpdate);
                    else
                    {
                        lock (this) // V2.83
                        {
                            foreach (MPTKEvent evnt in chord.Events)
                                QueueSynthCommand.Enqueue(new SynthCommand() { Command = SynthCommand.enCmd.StartEvent, MidiEvent = evnt });
                        }
                    }
                }
                else
                    Debug.LogWarningFormat("SoundFont not yet loaded, Chord cannot be processed Tonic:{0} Degree:{1}", chord.Tonic, chord.Degree);
            }
            catch (System.Exception ex)
            {
                MidiPlayerGlobal.ErrorDetail(ex);
            }
            return chord;
        }

        /// <summary>@brief
        /// Plays a chord from the chord library. See `ChordLib.csv` in `Resources/GeneratorTemplate`.
        /// The tonic is used to build the chord.
        /// @ingroup midistreamplayer_pro
        /// @ingroup midistreamplayer_chord_tools
        /// @version Maestro Pro 
        /// @code
        /// private void PlayOneChordFromLib()
        /// {
        ///    // Start playing a new chord
        ///    ChordLibPlaying = new MPTKChordBuilder(true)
        ///    {
        ///        // Parameters to build the chord
        ///        Tonic = CurrentNote,
        ///        FromLib = CurrentChord,
        ///
        ///        // MIDI parameters for how to play the chord
        ///        Channel = StreamChannel,
        ///        // Delay in milliseconds between each note of the chord
        ///        Arpeggio = ArpeggioPlayChord, 
        ///        // millisecond, -1 to play indefinitely
        ///        Duration = Convert.ToInt64(NoteDuration * 1000f), 
        ///        // Sound can vary depending on the velocity
        ///        Velocity = Velocity, 
        ///        Delay = Convert.ToInt64(NoteDelay * 1000f),
        ///    };
        ///    midiStreamPlayer.MPTK_PlayChordFromLib(ChordLibPlaying);
        /// }
        /// @endcode
        /// </summary>
        /// <param name="chord">Required: Tonic and FromLib, in addition to the standard MIDI parameters.</param>
        /// <returns>The same MPTKChordBuilder instance, enriched with generated events.</returns>
        public MPTKChordBuilder MPTK_PlayChordFromLib(MPTKChordBuilder chord)
        {
            try
            {
                if (MPTK_SoundFont.IsReady)
                {
                    chord.Channel = Mathf.Clamp(chord.Channel, 0, MPTK_Channels.Length - 1);
                    chord.MPTK_BuildFromLib(chord.FromLib);

                    if (!MPTK_CorePlayer)
                        Routine.RunCoroutine(TheadPlay(chord.Events), Segment.RealtimeUpdate);
                    else
                    {
                        lock (this) // V2.83
                        {
                            foreach (MPTKEvent evnt in chord.Events)
                                QueueSynthCommand.Enqueue(new SynthCommand() { Command = SynthCommand.enCmd.StartEvent, MidiEvent = evnt });
                        }
                    }
                }
                else
                    Debug.LogWarningFormat("SoundFont not yet loaded, Chord cannot be processed Tonic:{0} Degree:{1}", chord.Tonic, chord.Degree);
            }
            catch (System.Exception ex)
            {
                MidiPlayerGlobal.ErrorDetail(ex);
            }
            return chord;
        }

        /// <summary>@brief
        /// Stops the chord. All samples associated with the chord are stopped by sending a note-off.
        /// @ingroup midistreamplayer_pro
        /// @ingroup midistreamplayer_chord_tools
        /// @version Maestro Pro 
        /// </summary>
        /// <param name="chord">Chord definition and generated events to stop.</param>
        public void MPTK_StopChord(MPTKChordBuilder chord)
        {
            if (chord.Events != null)
            {
                foreach (MPTKEvent evt in chord.Events)
                {
                    if (!MPTK_CorePlayer)
                        StopEvent(evt);
                    else
                        lock (this) // V2.83
                        {
                            QueueSynthCommand.Enqueue(new SynthCommand() { Command = SynthCommand.enCmd.StopEvent, MidiEvent = evt });
                        }
                }
            }
        }
    }
}

