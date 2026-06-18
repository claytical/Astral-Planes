using System;
using UnityEngine;

namespace MidiPlayerTK
{
    /// <summary>
    /// A SoundFont contains parameters to apply three kinds of effects: low-pass filter, reverb, chorus.\n
    /// These parameters can be specific to each instrument and even each voice.\n
    /// Maestro MPTK effects are based on FluidSynth effect modules.
    /// Furthermore, to get more flexibility than the SoundFont defaults, Maestro can increase or decrease the impact of effects (from the inspector or by script).
    /// To summarize:
    ///     - Effects are applied individually to each voice, yet they are statically defined in the SoundFont.
    ///     - Maestro parameters can be adjusted to increase or decrease the default values set in the SoundFont.
    ///     - These adjustments will be applied across the entire prefab, but the effect will depend on the initial settings defined in the SoundFont preset.
    ///     - Please note that these effects require additional CPU resources.
    /// See more details here: https://paxstellar.fr/sound-effects/
    /// @version Maestro Pro 
    /// @note
    ///     - Effect modules are available only in Maestro MPTK Pro.
    ///     - By default, these effects are disabled in Maestro. 
    ///     - To enable them, adjust settings in the prefab inspector (Synth Parameters / SoundFont Effect) or by script.
    ///     - For better sound quality, enabling the low-pass filter is often useful.
    /// @ingroup soundfont_effects
    /// @code
    /// // Find an MPTK prefab; this also works for MidiStreamPlayer, MidiExternalPlayer, and all classes that inherit from MidiSynth.
    /// MidiFilePlayer fp = FindFirstObjectByType<MidiFilePlayer>();
    /// fp.MPTK_EffectSoundFont.EnableFilter = true;
    /// fp.MPTK_EffectSoundFont.FilterFreqOffset = 500;
    /// @endcode
    /// </summary>
    public partial class MPTKEffectSoundFont : ScriptableObject
    {
        /// <summary>@brief
        /// Frequency cutoff is defined in the SoundFont for each note.\n
        /// This parameter increases or decreases the default SoundFont value. Range: -2000 to 3000 Hz.
        /// @version Maestro Pro 
        /// @code
        /// midiFilePlayer.MPTK_EffectSoundFont.FilterFreqOffset = 10;
        /// @endcode
        /// </summary>
        [Range(-2000f, 3000f)]
        [HideInInspector]
        public float FilterFreqOffset;

        /// <summary>@brief
        /// Quality factor is defined in the SoundFont for each note.\n
        /// This parameter increases or decreases the default SoundFont value. Range: -96 to 96.
        /// @version Maestro Pro 
        /// </summary>
        [HideInInspector]
        public float FilterQModOffset
        {
            get { return filterQModOffset; }
            set
            {
                if (filterQModOffset != value)
                {
                    filterQModOffset = Mathf.Clamp(value, -96f, 96f);
                    if (synth != null && synth.ActiveVoices != null)
                        foreach (fluid_voice voice in synth.ActiveVoices)
                            if (voice.resonant_filter != null)
                                voice.resonant_filter.fluid_iir_filter_set_q(voice.q_dB, filterQModOffset);
                }
            }
        }

        /// <summary>@brief
        /// Sets SoundFont filter default values as defined in FluidSynth.\n
        /// @version Maestro Pro 
        /// </summary>
        public void DefaultFilter()
        {
            FilterFreqOffset = 0f;
            FilterQModOffset = 0f;
        }

        [HideInInspector]
        /// <summary>@brief
        /// Reverberation level is defined in the SoundFont in the range [0, 1].\n
        /// This parameter is added to the default SoundFont value (`reverb_send`).\n
        /// Range must be [-1, 1]
        /// @version Maestro Pro 
        /// </summary>
        [Range(-1f, 1f)]
        public float ReverbAmplify;

        [HideInInspector]
        /// <summary>@brief
        /// Chorus level is defined in the SoundFont in the range [0, 1].\n
        /// This parameter is added to the default SoundFont value (`chorus_send`).\n
        /// Range must be [-1, 1]
        /// @version Maestro Pro 
        /// </summary>
        [Range(-1f, 1f)]
        public float ChorusAmplify;

        /// <summary>@brief
        /// Sets the SoundFont reverb room size. Controls reverb time between 0 (0.7 s) and 1 (12.5 s).
        /// @version Maestro Pro 
        /// </summary>
        [HideInInspector]
        public float ReverbRoomSize
        {
            get { return sfReverbRoomSize; }
            set
            {
                float newval = Mathf.Clamp(value, 0f, 1f);
                if (sfReverbRoomSize != newval)
                {
                    sfReverbRoomSize = newval;
                    SetParamSfReverb();
                }
            }
        }

        /// <summary>@brief
        /// Sets the SoundFont reverb damping [0,1].\n
        /// Controls the reverb time frequency dependency. This controls the reverb time for the frequency sample rate/2\n
        /// When 0, the reverb time for high frequencies is the same as for DC frequency.\n
        /// When > 0, high frequencies have less reverb time than lower frequencies.\n
        /// @version Maestro Pro 
        /// </summary>
        [HideInInspector]
        public float ReverbDamp
        {
            get { return sfReverbDamp; }
            set
            {
                float newval = Mathf.Clamp(value, 0f, 1f);
                if (sfReverbDamp != newval)
                {
                    sfReverbDamp = newval;
                    SetParamSfReverb();
                }
            }
        }

        /// <summary>@brief
        /// Sets the SoundFont reverb width [0,100].\n
        /// Controls left/right output separation.\n
        /// When 0, there is no separation and the signal on the left and right outputs is the same. This sounds like a monophonic signal.\n
        ///  When 100, the separation between left and right is maximum.\n
        /// @version Maestro Pro 
        /// </summary>
        [HideInInspector]
        public float ReverbWidth
        {
            get { return sfReverbWidth; }
            set
            {
                float newval = Mathf.Clamp(value, 0f, 100f);
                if (sfReverbWidth != newval)
                {
                    sfReverbWidth = newval;
                    SetParamSfReverb();
                }
            }
        }

        /// <summary>@brief
        /// Sets the SoundFont reverb effect level.
        /// @version Maestro Pro 
        /// </summary>
        [HideInInspector]
        public float ReverbLevel
        {
            get { return sfReverbLevel; }
            set
            {
                float newval = Mathf.Clamp(value, 0f, 1f);
                if (sfReverbLevel != newval)
                {
                    sfReverbLevel = newval;
                    SetParamSfReverb();
                }
            }
        }

        /// <summary>@brief
        /// Sets SoundFont reverb default values as defined in FluidSynth.\n
        /// FLUID_REVERB_DEFAULT_ROOMSIZE 0.5f \n
        /// FLUID_REVERB_DEFAULT_DAMP 0.3f     \n
        /// FLUID_REVERB_DEFAULT_WIDTH 0.8f    \n
        /// FLUID_REVERB_DEFAULT_LEVEL 0.7f    \n
        /// @version Maestro Pro 
        /// </summary>
        [HideInInspector]
        public void DefaultReverb()
        {
            ReverbAmplify = 0f;
            ReverbRoomSize = MidiSynth.FLUID_REVERB_DEFAULT_ROOMSIZE;
            ReverbDamp = MidiSynth.FLUID_REVERB_DEFAULT_DAMP;
            ReverbWidth = MidiSynth.FLUID_REVERB_DEFAULT_WIDTH;
            ReverbLevel = MidiSynth.FLUID_REVERB_DEFAULT_LEVEL;
        }

        /// <summary>@brief
        /// Sets the SoundFont chorus effect level [0, 10].\n
        /// Default value is set to 0.9 (was 2f, thanks John).
        /// @version Maestro Pro 
        /// </summary>
        [HideInInspector]
        public float ChorusLevel
        {
            get { return sfChorusLevel; }
            set
            {
                float newval = Mathf.Clamp(value, 0f, 10f);
                if (sfChorusLevel != newval)
                {
                    sfChorusLevel = newval;
                    SetParamSfChorus();
                }
            }
        }

        /// <summary>@brief
        /// Sets the SoundFont chorus effect speed.\n
        /// Chorus speed in Hz [0.1, 5]\n
        /// @version Maestro Pro 
        /// </summary>
        [HideInInspector]
        public float ChorusSpeed
        {
            get { return sfChorusSpeed; }
            set
            {
                float newval = Mathf.Clamp(value, 0.1f, 5f);
                if (sfChorusSpeed != newval)
                {
                    sfChorusSpeed = newval;
                    SetParamSfChorus();
                }
            }
        }

        /// <summary>@brief
        /// Sets the SoundFont chorus effect depth.\n
        /// Chorus depth [0, 256]\n
        /// @version Maestro Pro 
        /// </summary>
        [HideInInspector]
        public float ChorusDepth
        {
            get { return sfChorusDepth; }
            set
            {
                float newval = Mathf.Clamp(value, 0f, 256f);
                if (sfChorusDepth != newval)
                {
                    sfChorusDepth = newval;
                    SetParamSfChorus();
                }
            }
        }

        /// <summary>@brief
        /// Sets the SoundFont chorus effect width.\n
        /// The chorus unit processes a monophonic input signal and produces stereo output controlled by the WIDTH macro.\n
        /// Width allows a gradual stereo effect from minimum (monophonic) to maximum stereo effect. [0, 10]\n
        /// @version Maestro Pro 
        /// </summary>
        [HideInInspector]
        public float ChorusWidth
        {
            get { return sfChorusWidth; }
            set
            {
                float newval = Mathf.Clamp(value, 0f, 10f);
                if (sfChorusWidth != newval)
                {
                    sfChorusWidth = newval;
                    SetParamSfChorus();
                }
            }
        }

        /// <summary>@brief
        /// Sets SoundFont chorus default values as defined in FluidSynth.\n
        /// FLUID_CHORUS_DEFAULT_N 3        \n
        /// FLUID_CHORUS_DEFAULT_LEVEL 0.6 but set to 0.9 (thank John) \n
        /// FLUID_CHORUS_DEFAULT_SPEED 0.2 \n
        /// FLUID_CHORUS_DEFAULT_DEPTH 4.25 \n
        /// FLUID_CHORUS_DEFAULT_WIDTH 10 (can be modified with MPTK) \n
        /// FLUID_CHORUS_DEFAULT_TYPE FLUID_CHORUS_MOD_SINE \n
        /// WIDTH 10
        /// @version Maestro Pro 
        /// </summary>
        public void DefaultChorus()
        {
            ChorusAmplify = 0f;
            ChorusLevel = MidiSynth.FLUID_CHORUS_DEFAULT_LEVEL; // 2.0 in fluidsynthn set to 0.9 
            ChorusSpeed = MidiSynth.FLUID_CHORUS_DEFAULT_SPEED;
            ChorusDepth = MidiSynth.FLUID_CHORUS_DEFAULT_DEPTH;
            ChorusWidth = MidiSynth.FLUID_CHORUS_DEFAULT_WIDTH;
        }

        [HideInInspector, SerializeField]
        private float filterQModOffset;


        [HideInInspector, SerializeField]
        private float sfReverbRoomSize = MidiSynth.FLUID_REVERB_DEFAULT_ROOMSIZE; // before SF 2.3    0.2f;

        [HideInInspector, SerializeField]
        private float sfReverbDamp = MidiSynth.FLUID_REVERB_DEFAULT_DAMP; // before SF 2.3     0f;

        [HideInInspector, SerializeField]
        private float sfReverbWidth = MidiSynth.FLUID_REVERB_DEFAULT_WIDTH; // before SF 2.3    0.5f;

        [HideInInspector, SerializeField]
        private float sfReverbLevel = MidiSynth.FLUID_REVERB_DEFAULT_LEVEL;// before SF 2.3    0.9f; 


        [HideInInspector, SerializeField]
        private float sfChorusLevel = MidiSynth.FLUID_CHORUS_DEFAULT_LEVEL; // before SF 2.3 was 2.0 in fluidsynth  ... but too much

        [HideInInspector, SerializeField]
        private float sfChorusSpeed = MidiSynth.FLUID_CHORUS_DEFAULT_SPEED; // before SF 2.3     0.3f;

        [HideInInspector, SerializeField]
        private float sfChorusDepth = MidiSynth.FLUID_CHORUS_DEFAULT_DEPTH; // before SF 2.3    8f;

        [HideInInspector, SerializeField]
        private float sfChorusWidth = MidiSynth.FLUID_CHORUS_DEFAULT_WIDTH;


        /// <summary>
        /// Sets all SoundFont effects to default values.
        /// @code
        /// midiFilePlayer.MPTK_EffectSoundFont.DefaultAll();
        /// @endcode
        /// </summary>
        public void DefaultAll()
        {
            DefaultReverb();
            DefaultChorus();
        }
        private void SetParamSfReverb()
        {
            if (reverb != null)
                reverb.fluid_revmodel_set(/*(int)fluid_revmodel.fluid_revmodel_set_t.FLUID_REVMODEL_SET_ALL*/0xFF,
                    ReverbRoomSize, ReverbDamp, ReverbWidth, ReverbLevel);
        }

        private void SetParamSfChorus()
        {
            if (chorus != null)
                chorus.fluid_chorus_set((int)fluid_chorus.fluid_chorus_set_t.FLUID_CHORUS_SET_ALL,
                    MidiSynth.FLUID_CHORUS_DEFAULT_N, ChorusLevel, ChorusSpeed, ChorusDepth, fluid_chorus.FLUID_CHORUS_DEFAULT_TYPE, ChorusWidth);
        }
    }
}

