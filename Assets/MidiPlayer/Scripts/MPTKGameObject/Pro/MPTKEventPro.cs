using System;
using System.Collections.Generic;
using UnityEngine;

namespace MidiPlayerTK
{

    /// @ingroup midi_event_object
    public partial class MPTKEvent : ICloneable
    {

        /// @name Real-Time Generator Modifiers
        /// @brief Real-time SoundFont generator overrides at event level.
        /// @details
        /// These members and methods let you override generator values per event,
        /// both before note start and while voices are already playing.
        /// This enables expressive per-note timbre control in Maestro Pro.
        /// Per-generator modifiers associated with this event.
        /// Null when no modifier is defined.
        /// @version Maestro Pro 
        /// @{

        /// <summary>@brief
        /// list of synth modifier available.
        /// </summary>
        public GenModifier[] GensModifier;

        /// <summary>@brief
        /// Applies a modification to a SoundFont generator value.
        /// The change can be applied per note before playback and in <b>real time</b> while the note is playing.\n
        /// Each generator has a specific value range, <a href=" https://paxstellar.fr/wp-content/uploads/2021/01/GeneratorModulation.png"><b>see the generator list here.</b></a>\n
        /// The input value for this method is normalized (between 0 and 1).
        /// <a href="https://paxstellar.fr/class-mptkevent#Generator-List"><b>See here for more details.</b></a>\n
        /// @version Maestro Pro 
        /// @code
        /// // Create a midi event for a C5 note (60) with default value: infinite duration, channel = 0, and velocity = 127 (max)
        /// mptkEvent = new MPTKEvent() { Value = 60 };
        /// 
        /// // Fine tuning (pitch)
        /// mptkEvent.ModifySynthParameter(fluid_gen_type.GEN_FINETUNE, 0.52f, MPTKModeGeneratorChange.Override);
        /// 
        /// // Change low pass filter frequency
        /// mptkEvent.ModifySynthParameter(fluid_gen_type.GEN_FILTERFC, 0.6f, MPTKModeGeneratorChange.Override);
        /// 
        /// midiStream.MPTK_PlayDirectEvent(mptkEvent);
        /// @endcode
        /// </summary>
        /// <param name="genType">Type of generator to modify. Not all generators support real-time updates.\n
        /// @li  GEN_MODLFOTOPITCH	Modulation LFO to pitch
        /// @li  GEN_VIBLFOTOPITCH	Vibrato LFO to pitch
        /// @li  GEN_MODENVTOPITCH	Modulation envelope to pitch
        /// @li  GEN_FILTERFC	Filter cutoff
        /// @li  GEN_FILTERQ	Filter Q
        /// @li  GEN_MODLFOTOFILTERFC	Modulation LFO to filter cutoff
        /// @li  GEN_MODENVTOFILTERFC	Modulation envelope to filter cutoff
        /// @li  GEN_MODLFOTOVOL	Modulation LFO to volume
        /// @li  GEN_CHORUSSEND	Chorus send amount
        /// @li  GEN_REVERBSEND	Reverb send amount
        /// @li  GEN_PAN	Stereo panning
        /// @li  GEN_MODLFODELAY	Modulation LFO delay
        /// @li  GEN_MODLFOFREQ	Modulation LFO frequency
        /// @li  GEN_VIBLFODELAY	Vibrato LFO delay
        /// @li  GEN_VIBLFOFREQ	Vibrato LFO frequency
        /// @li  GEN_MODENVDELAY	Modulation envelope delay
        /// @li  GEN_MODENVATTACK	Modulation envelope attack
        /// @li  GEN_MODENVHOLD	Modulation envelope hold
        /// @li  GEN_MODENVDECAY	Modulation envelope decay
        /// @li  GEN_MODENVSUSTAIN	Modulation envelope sustain
        /// @li  GEN_MODENVRELEASE	Modulation envelope release
        /// @li  GEN_VOLENVDELAY	Volume envelope delay
        /// @li  GEN_VOLENVATTACK	Volume envelope attack
        /// @li  GEN_VOLENVHOLD	Volume envelope hold
        /// @li  GEN_VOLENVDECAY	Volume envelope decay
        /// @li  GEN_VOLENVSUSTAIN	Volume envelope sustain
        /// @li  GEN_VOLENVRELEASE	Volume envelope release
        /// @li  GEN_ATTENUATION	Volume attenuation
        /// @li  GEN_COARSETUNE	Coarse tuning
        /// @li  GEN_FINETUNE	Fine tuning
        /// </param>
        /// <param name="value">Normalized value for the generator between 0 and 1.\n
        ///  <a href="https://paxstellar.fr/class-mptkevent#Generator-List"><b>See the actual value range for each parameter here.</b></a>\n
        /// @li  0 sets the minimum value for the generator. For example, with an envelope parameter, 0 sets -12000 (minimum for this type of parameter).
        /// @li  1 sets the maximum value for the generator. For example, with an envelope parameter, 1 sets 12000 (maximum for this type of parameter).
        /// </param>
        /// <param name="mode">Defines how the value is applied.\n
        /// @li  Override: replaces the SoundFont value.
        /// @li  Reinforce: adds to the default value.
        /// @li  Restore: uses the default SoundFont value.
        /// </param>
        /// <returns>True when the change is accepted and applied; otherwise false.</returns>
        public bool ModifySynthParameter(fluid_gen_type genType, float value, MPTKModeGeneratorChange mode)
        {
            bool result = false;
            int genId = ConvertIdToIndex(genType);
            if (genId >= 0)
            {
                try
                {
                    // If a list of modifier is already associated to this event ?
                    if (GensModifier == null)
                    {
                        GenModifier.InitListGenerator();
                        GensModifier = new GenModifier[Enum.GetNames(typeof(fluid_gen_type)).Length];
                    }
                    if (GensModifier[genId] == null) GensModifier[genId] = new GenModifier();
                    GensModifier[genId].Mode = mode;
                    GensModifier[genId].NormalizedVal = value;
                    GensModifier[genId].SoundFontVal = Mathf.Lerp(fluid_gen_info.FluidGenInfo[genId].min, fluid_gen_info.FluidGenInfo[genId].max, value);

                    // If event is already playing (voices are defined) applied change in real time
                    if (Voices != null)
                        foreach (fluid_voice voice in Voices)
                            voice.fluid_voice_update_param(genId);
                    result = true;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"ModifySynthParameter - {ex.Message}");
                }
            }
            return result;
        }


        /// <summary>@brief
        /// Clears generator modifiers for this event and restores default SoundFont behavior.
        /// @version Maestro Pro 
        /// </summary>
        public void ClearSynthParameter()
        {
            GensModifier = null;
        }


        /// @}

        private static int ConvertIdToIndex(fluid_gen_type genType)
        {
            int genId = (int)genType;
            if (genId < 0 || genId >= Enum.GetNames(typeof(fluid_gen_type)).Length || !fluid_gen_info.FluidGenInfo[genId].RealTimeChange)
            {
                Debug.LogWarning($"ConvertIdToIndex - fluid_gen_type {genType} {genId} - cannot be modified or outside range");
                return -1;
            }
            return genId;
        }

        /// @name Generator Query Utilities
        /// @brief Read-only helpers for modifiable generator metadata and values.
        /// @details
        /// Provides access to default/current normalized generator values,
        /// generator labels, and the list of generators that support runtime changes.
        /// @{
        
        /// <summary>@brief
        /// Gets the default value for a SoundFont generator.\n
        /// Each generator has a specific value range, <a href=" https://paxstellar.fr/wp-content/uploads/2021/01/GeneratorModulation.png"><b>see the generator list here.</b></a>\n
        /// The returned value is normalized (between 0 and 1).
        /// @version Maestro Pro 
        /// </summary>
        /// <param name="genType">Generator type. See #ModifySynthParameter.</param>
        /// <returns>Normalized default value for the generator.</returns>
        public float GetSynthParameterDefaultValue(fluid_gen_type genType)
        {
            float result = 0f;
            int genId = ConvertIdToIndex(genType);
            if (genId >= 0)
            {
                try
                {
                    result = Mathf.InverseLerp(fluid_gen_info.FluidGenInfo[genId].min, fluid_gen_info.FluidGenInfo[genId].max, fluid_gen_info.FluidGenInfo[genId].def);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"GetSynthParameterDefaultValue - {ex.Message}");
                }
            }
            return result;
        }

        /// <summary>@brief
        /// Gets the current value for a generator on this event.\n
        /// Each generator has a specific value range, <a href=" https://paxstellar.fr/wp-content/uploads/2021/01/GeneratorModulation.png"><b>see the generator list here.</b></a>\n
        /// The returned value is normalized (between 0 and 1).
        /// @version Maestro Pro 
        /// </summary>
        /// <param name="genType">Generator type. See #ModifySynthParameter.</param>
        /// <returns>Normalized current value, or -1 when no modifier is defined for this generator.</returns>
        public float GetSynthParameterCurrentValue(fluid_gen_type genType)
        {
            float result = 0f;
            int genId = ConvertIdToIndex(genType);
            if (genId >= 0)
            {
                try
                {
                    if (GensModifier[genId] == null)
                        // No generator modifier exists for this MIDI event
                        result = -1;
                    else
                        result = Mathf.InverseLerp(fluid_gen_info.FluidGenInfo[genId].min, fluid_gen_info.FluidGenInfo[genId].max, 
                            GensModifier[genId].SoundFontVal);
                }
                catch (Exception)
                {
                    result = -1;
                    //Debug.LogWarning($"GetSynthParameterCurrentValue - {ex.Message}");
                }
            }
            return result;
        }

        /// <summary>@brief
        /// Gets the display label for a generator.
        /// @version Maestro Pro 
        /// </summary>
        /// <param name="genType">Generator type. See #ModifySynthParameter.</param>
        /// <returns>Generator label, or "not found" if unavailable.</returns>
        public static string GetSynthParameterLabel(fluid_gen_type genType)
        {
            int genId = ConvertIdToIndex(genType);
            if (genId >= 0)
            {
                try
                {
                    GenModifier.InitListGenerator();
                    foreach (MPTKListItem item in GenModifier.RealTimeGenerator)
                        if (item.Index == genId)
                        {
                            return item.Label;// GenModifier.RealTimeGenerator
                        }

                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"GetSynthParameterLabel - {ex.Message}");
                }
            }
            return "not found";
        }

        /// <summary>@brief
        /// Gets the list of modifiable generators.\n
        /// Returns a list of MPTKListItem where:
        /// @li  MPTKListItem#Index: generator ID
        /// @li  MPTKListItem#Label: display text for the generator
        /// @li  MPTKListItem#Position: position in the list, starting at 0
        /// @version Maestro Pro 
        /// </summary>
        /// <returns>List of modifiable generators (do not modify this list).</returns>
        public static List<MPTKListItem> GetSynthParameterListGenerator()
        {
            GenModifier.InitListGenerator();
            return GenModifier.RealTimeGenerator;
        }

        /// @}


        /// @name Event Playback Control
        /// @brief Event-level playback actions for active notes.
        /// @details
        /// Contains direct control helpers such as `StopEvent`, which sends
        /// a matching NoteOff for this event when applicable.
        /// @{
        
        /// <summary>@brief 
        /// If this event is a NoteOn, sends the corresponding NoteOff event.
        /// This is equivalent to sending a note-off to the synth, so the sound enters its release phase.
        /// Has an effect only for NoteOn events.
        /// @version 2.9.0 Pro
        /// </summary>
        public void StopEvent()
        {
            if (Command == MPTKCommand.NoteOn)
            {
                if (Voices != null && Voices.Count > 0)
                    if (Voices[0].synth != null)
                        Voices[0].synth.MPTK_PlayDirectEvent(new MPTKEvent() { Command = MPTKCommand.NoteOff, Channel = Channel, Value = Value });

            }
        }

        /// @}

    }
}

