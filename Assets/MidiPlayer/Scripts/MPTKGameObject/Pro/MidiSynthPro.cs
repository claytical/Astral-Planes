// Copyright 2024, Bachmann/MusIT
// This source code is part of Maestro MPTK Pro and is licensed for use only by individuals who have purchased the asset.
// Redistribution or public sharing of this code is prohibited without explicit permission.
#if UNITY_ANDROID && UNITY_OBOE
using Oboe.Stream;
using System.Runtime.InteropServices;
#endif
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

namespace MidiPlayerTK
{
/// @ingroup synth_engine_pro
/// <summary>@brief
/// Maestro MPTK Pro extension for MidiSynth.
/// @version Maestro Pro
/// </summary>
public partial class MidiSynth : MonoBehaviour
    {
        /// @name Synthesizer Events
        /// @brief Events raised by the MIDI synthesizer during playback.
        /// @details
        /// These callbacks allow scripts to interact with the synthesizer during
        /// audio processing. Some events may run on the audio processing thread,
        /// therefore Unity API calls are generally not allowed except Debug.Log.
        ///
        /// Typical uses include:
        /// - monitoring playback timing
        /// - intercepting MIDI events
        /// - synchronizing gameplay with music.
        
        /// @{

        /// <summary>@brief
        /// Delegate for the #OnAudioFrameStart event.
        /// @version Maestro Pro
        /// </summary>
        public delegate void OnAudioFrameStartHandler(double synthTime);

        /// <summary>@brief
        /// Raised at the start of each audio frame by the audio engine.<br>
        /// @details
        /// The callback parameter is the current synth time, in milliseconds.<br>
        /// The callback does not run on the Unity main thread, so you must not call Unity APIs (except Debug.Log).
        /// @version Maestro Pro
        /// @par
        /// @code
        /// // See Assets\MidiPlayer\Demo\ProDemos\Script\EuclideSeq\TestEuclideanRhythme.cs for the full code.
        /// public void Play()
        /// {
        ///     if (IsPlaying)
        ///         midiStream.OnAudioFrameStart += PlayHits;
        ///     else
        ///         midiStream.OnAudioFrameStart -= PlayHits;
        /// }
        /// private void PlayHits(double synthTimeMS)
        /// {
        ///     if (lastSynthTime <= 0d)
        ///         // First call, initialize the last time
        ///         lastSynthTime = synthTimeMS;
        ///     // Time (ms) since the last callback
        ///     double deltaTime = synthTimeMS - lastSynthTime;
        ///     lastSynthTime = synthTimeMS;
        ///     timeMidiFromStartPlay += deltaTime;
        ///
        ///     // Time since last beat played
        ///     timeSinceLastBeat += deltaTime;
        ///
        ///     // Slider SldTempo in BPM.
        ///     //  60 BPM means 60 beats per minute: 1 beat per second, 1000 ms between beats.
        ///     // 120 BPM is twice as fast: 2 beats per second, 500 ms between beats.
        ///     // Delay between two quarter notes (ms)
        ///     CurrentTempo = (60d / SldTempo.Value) * 1000d;
        ///
        ///     // Is it time to play a hit?
        ///     if (IsPlaying && timeSinceLastBeat > CurrentTempo)
        ///     {
        ///         timeSinceLastBeat = 0d;
        ///         CurrentBeat++;
        ///     }
        /// }
        /// @endcode
        /// </summary>
        public event OnAudioFrameStartHandler OnAudioFrameStart;

        /// <summary>@brief
        /// Called by the MIDI sequencer before sending a MIDI message to the synthesizer.\n
        /// From version 2.10.0 onward, the callback must return true to keep the event or false to skip it.
        /// @details
        /// Acts as a MIDI event preprocessor: you can change MIDI values and therefore alter playback.\n
        /// The callback receives an MPTKEvent object by reference (as it is a C# class).\n
        /// See https://mptkapi.paxstellar.com/d9/d50/class_midi_player_t_k_1_1_m_p_t_k_event.html for the event structure.\n
        /// You can change note, velocity, channel, skip events, or even change the MIDI message type.\n
        /// @version Maestro Pro 
        /// @note
        /// @li The callback runs on a system thread, not the Unity main thread. Unity API calls are not allowed except Debug.Log (use sparingly).
        /// @li Avoid heavy processing or waiting in the callback or timing accuracy will degrade.
        /// @li The midiEvent is passed by reference. Re-instantiating it (midiEvent = new MPTKEvent()) or setting it to null has no effect.
        /// @li MIDI position attributes (Tick and RealTime) can be read but changing them has no effect.
        /// @li Changing a SetTempo event is too late for the sequencer (it has already been processed). Use midiFilePlayer.CurrentTempo to change tempo at runtime.
        /// @par
        /// Example:
        /// @code
        /// // See TestMidiFilePlayerScripting.cs for the demo.
        /// void Start()
        /// {
        ///     MidiFilePlayer midiFilePlayer = FindFirstObjectByType<MidiFilePlayer>();
        ///     midiFilePlayer.OnMidiEvent = PreProcessMidi;
        /// }
        /// 
        /// // Example 
        /// bool PreProcessMidi(MPTKEvent midiEvent)
        /// {
        ///     bool playEvent = true;
        /// 
        ///     switch (midiEvent.Command)
        ///     {
        ///         case MPTKCommand.NoteOn:
        ///             if (midiEvent.Channel != 9)
        ///                 // transpose 2 octaves
        ///                 midiEvent.Value += 24;
        ///             else
        ///                 // Drums are muted
        ///                 playEvent = false;
        ///             break;
        ///         case MPTKCommand.PatchChange:
        ///             // Remove all patch changes: all channels will play the default preset 0
        ///             midiEvent.Command = MPTKCommand.MetaEvent;
        ///             midiEvent.Meta = MPTKMeta.TextEvent;
        ///             midiEvent.Info = "Patch Change removed";
        ///             break;
        ///         case MPTKCommand.MetaEvent:
        ///             if (midiEvent.Meta == MPTKMeta.SetTempo)
        ///                 // Tempo forced to 100
        ///                 midiFilePlayer.CurrentTempo = 100;
        ///             break;
        ///     }
        ///     
        ///     // true: play this event, false to skip
        ///     return playEvent;
        /// }
        /// 
        /// @endcode
        /// </summary>
        public Func<MPTKEvent, bool> OnMidiEvent;

        /// <summary>@brief 
        /// Invoked on each beat with the following parameters:
        ///     - time: time in milliseconds since MIDI playback started.
        ///     - tick: current tick.
        ///     - measure: current measure (starts at 1).
        ///     - beat: current beat (starts at 1).
        /// @note
        ///     - OnBeatEvent is invoked on every beat, even if there is no MIDI event exactly on that beat.
        ///     - Timing accuracy is guaranteed (runs on an internal thread).
        ///     - Direct calls to the Unity API are not allowed, but Debug.Log and Maestro APIs (for example, playing a sound on each beat) are allowed.
        /// @version Maestro Pro 
        /// @snippet TestMidiFilePlayerScripting.cs Example_OnBeatEvent
        /// </summary>
        public Action<int, long, int, int> OnBeatEvent;

        /// @}

        // private used for 3D audio
        private volatile float _gML = 0.70710678f;
        private volatile float _gMR = 0.70710678f;
        private volatile float _gSL = 1f;
        private volatile float _gSR = 1f;
        private volatile float _globalGain = 1f;
        private volatile float _alpha = 1f;
        // --- Smoothed front/back state (temporal crossfade) ---
        private float _behind01Smoothed = 0f;

        void Update()
        {
            if (enable3DOrientation)
            {
                bool flowControl = CalculateOrientationEffect(MidiPlayerGlobal.MPTK_AudioListener);
                if (!flowControl)
                {
                    return;
                }
            }
        }

        public bool CalculateOrientationEffect(AudioListener listener)
        {
            // Direction from listener to source, flattened to horizontal plane
            Transform listenerTrf = listener.transform;
            Vector3 toSrcWorld = transform.position - listenerTrf.position;
            toSrcWorld.y = 0f;
            if (toSrcWorld.sqrMagnitude < 1e-8f) return false;
            Vector3 toSrc = toSrcWorld.normalized;

            // Listener reference axes (horizontal only)
            Vector3 fwd = listenerTrf.forward; fwd.y = 0f; fwd.Normalize();
            Vector3 right = listenerTrf.right; right.y = 0f; right.Normalize();

            // =========================================================
            // 1) Front / Back evaluation
            // =========================================================
            // frontDot: +1 = fully in front, 0 = on the side, -1 = fully behind
            float frontDot = Mathf.Clamp(Vector3.Dot(fwd, toSrc), -1f, 1f);

            // ---------------------------------------------------------
            // Dead zone around the frontal plane:
            // - No "behind" effect near frontDot ≈ 0
            // - Prevents audible switching when crossing the front plane
            // ---------------------------------------------------------
            float targetBehind01 = 0f;

            if (frontDot < 0f)
            {
                // Map frontDot from [-frontDeadZone .. -1] to [0 .. 1]
                // Effect starts only after the dead zone
                targetBehind01 = Mathf.InverseLerp(-frontDeadZone, -1f, frontDot);
                targetBehind01 = Mathf.Clamp01(targetBehind01);
            }

            // ---------------------------------------------------------
            // Temporal smoothing (crossfade in time)
            // Masks rapid geometric changes and removes "hard" transitions
            // ---------------------------------------------------------
            float k = 1f - Mathf.Exp(-behindSmoothSpeed * Time.deltaTime);
            _behind01Smoothed += (targetBehind01 - _behind01Smoothed) * k;

            float behind01 = _behind01Smoothed;

            // =========================================================
            // 2) Global gain (reacts earlier than the filter)
            // =========================================================
            float behindGainT = Mathf.Pow(behind01, behindGainCurve);
            _globalGain = Mathf.Lerp(1f, behindMinGain, behindGainT);

            // =========================================================
            // 3) Stereo pan (unchanged, equal-power on the Mid)
            // =========================================================
            float rightDot = Mathf.Clamp(Vector3.Dot(right, toSrc), -1f, 1f);
            float pan = rightDot * panAmount;   // [-1 .. +1]
            float u = (pan + 1f) * 0.5f;        // [0 .. 1]


            // Angle signed in degrees, range [-180 .. +180]
            // 0°   = in front
            // +90° = to the right
            // -90° = to the left
            // ±180° = behind
            MPTK_OrientationToListener = Mathf.Atan2(rightDot, frontDot) * Mathf.Rad2Deg;
            // Example to remap [0..360[ MPTK_OrientatioToListener < 0f ? MPTK_OrientatioToListener + 360f : MPTK_OrientatioToListener;

            // Equal-power panning for the Mid component
            float gML = Mathf.Cos(u * Mathf.PI * 0.5f);
            float gMR = Mathf.Sin(u * Mathf.PI * 0.5f);
            _gML = gML;
            _gMR = gMR;

            // Side component partially follows the pan
            // 1 = neutral width, gM* = fully follows Mid
            float s = sideFollow;
            _gSL = Mathf.Lerp(1f, gML, s);
            _gSR = Mathf.Lerp(1f, gMR, s);

            if (cutoffPoleCount > 0)
            {
                // =========================================================
                // 4) Low-pass filter (starts later than gain)
                // =========================================================
                float behindLPFT = Mathf.Pow(behind01, behindLPFCurve);
                float cutoff = Mathf.Lerp(cutoffFrontHz, cutoffBehindHz, behindLPFT);

                // pole low-pass coefficient
                float fs = AudioSettings.outputSampleRate;
                _alpha = 1f - Mathf.Exp(-2f * Mathf.PI * cutoff / fs);
            }
            //Debug.Log($"{this.name} pan!{pan:F2} ML:{_gML:F2} MR:{_gMR:F2} SL:{_gSL:F2} SR:{_gSR:F2} alpha:{_alpha:F2}");

            return true;
        }

        // audio state
        private float y1L = 0f, y2L = 0f;
        private float y1R = 0f, y2R = 0f;

        void ApplyMPTK3DSound(float[] data)
        {
            float gML = _gML, gMR = _gMR;
            float gSL = _gSL, gSR = _gSR;
            float g = _globalGain;
            float a = _alpha;

            for (int i = 0; i < data.Length; i += 2)
            {
                float inL = data[i];
                float inR = data[i + 1];

                // Mid/Side
                float M = 0.5f * (inL + inR);
                float S = 0.5f * (inL - inR);

                // Rebuild with Mid move + partial Side follow
                float xL = (M * gML) + (S * gSL);
                float xR = (M * gMR) - (S * gSR);

                // Global attenuation (when behind)
                xL *= g;
                xR *= g;

                switch (cutoffPoleCount)
                {
                    case 0:
                        // no cutoff low-pass
                        data[i] = xL;
                        data[i + 1] = xR;
                        break;
                    case 1:
                        // one pole 
                        y1L += a * (xL - y1L);
                        y1R += a * (xR - y1R);
                        data[i] = y1L;
                        data[i + 1] = y1R;
                        break;
                    case 2:
                        // 1st pole 
                        y1L += a * (xL - y1L);
                        y1R += a * (xR - y1R);
                        // 2nd pole 
                        y2L += a * (y1L - y2L);
                        y2R += a * (y1R - y2R);

                        data[i] = y2L;
                        data[i + 1] = y2R;
                        break;
                }
            }
        }

        /// @name Pause and Resume Voices
        /// @brief Runtime control of active synthesizer voices.
        /// @details
        /// This section provides methods to pause or resume all currently active
        /// voices with smooth transitions. The transition duration controls how
        /// quickly voices fade out or fade back in.
        ///
        /// These functions are useful for:
        /// - implementing transport controls
        /// - temporarily muting playback
        /// - synchronizing audio with gameplay or UI events.
        /// @version Maestro Pro
        
        /// @{

        /// <summary>@brief 
        /// Pauses all active voices using the specified transition duration.
        /// @note
        ///     - Iterates over all active voices and pauses them with the given transition duration.
        /// @see MPTK_ResumeVoices
        /// Example with the MidiStreamPlayer prefab:
        /// @code
        /// midiStreamPlayer.MPTK_PauseVoices(100);
        /// @endcode
        /// @version 2.17.0 Pro 
        /// </summary>
        public void MPTK_PauseVoices(float transitionDuration = 30f)
        {
            foreach (fluid_voice voice in ActiveVoices)
                voice.Pause(Math.Clamp(transitionDuration, 0f, 5000f));
        }

        /// <summary>@brief
        /// Resumes all active voices using the specified transition duration.
        /// @note 
        ///     - Iterates over all active voices and resumes them with the given transition duration.
        /// @see MPTK_PauseVoices
        /// Example with the MidiStreamPlayer prefab:
        /// @code
        /// midiStreamPlayer.MPTK_ResumeVoices(100);
        /// @endcode
        /// @version 2.17.0 Pro 
        /// </summary>
        public void MPTK_ResumeVoices(float transitionDuration = 30f)
        {
            foreach (fluid_voice voice in ActiveVoices)
                voice.Resume(Math.Clamp(transitionDuration, 0f, 5000f));
        }

        /// @}

        public bool CheckBeatEvent(int time)
        {
            try
            {
                bool beatChange = midiLoaded.calculateBeatPlayer();

                if (!midiLoaded.EndMidiEvent && beatChange && OnBeatEvent != null)
                {
                    try
                    {
                        OnBeatEvent(time, midiLoaded.MPTK_TickPlayer, midiLoaded.MPTK_CurrentMeasure, midiLoaded.MPTK_CurrentBeat);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError("OnBeatEvent: exception detected. Check your callback code.");
                        Debug.LogException(ex);
                        return false;
                    }
                    // Status has changed in the action ?
                    if (!midiLoaded.ReadyToPlay)
                        return false;
                }
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                return false;
            }
            return true;
        }

        /// @name Audio Effects
        /// @brief Integration of Unity audio effects applied to the synthesizer.
        /// @details
        /// Unlike SoundFont effects which operate at the instrument level,
        /// Unity audio effects are applied to the entire output of the MIDI
        /// synthesizer.
        ///
        /// Maestro integrates selected Unity effects such as Reverb and Chorus.
        /// These effects can be configured from the inspector or controlled
        /// directly from scripts at runtime.

        /// @{

        [SerializeField]
        /// <summary>@brief
        /// Unlike SoundFont effects, Unity audio effects apply to the whole player. Their parameters are richer and rely on Unity's audio algorithms.\n
        /// https://docs.unity3d.com/Manual/class-AudioEffectMixer.html\n
        /// Maestro integrates the most important effects: Reverb and Chorus. Other effects could be added if needed. 
        /// @note
        ///     - Unity effects integration modules are available only in Maestro MPTK Pro. 
        ///     - By default, these effects are disabled in Maestro. 
        ///     - Enable them from the prefab inspector: Synth Parameters / Unity Effect.
        ///     - Each setting is also available by script.
        /// @version Maestro Pro 
        /// @code
        /// // Find an MPTK prefab; this will also work for MidiStreamPlayer, MidiExternalPlayer, and all classes that inherit from MidiSynth.
        /// MidiFilePlayer fp = FindFirstObjectByType<MidiFilePlayer>();
        /// fp.MPTK_EffectUnity.EnableReverb = true;
        /// fp.MPTK_EffectUnity.ReverbDelay = 0.09f;
        /// @endcode
        /// </summary>
        [Header("---------------- MPTK Unity effect ----------------")]
        public MPTKEffectUnity MPTK_EffectUnity;

        /// @}

        private void InitEffectUnity()
        {
            GenModifier.InitListGenerator();

            if (MPTK_EffectUnity == null)
            {
                //Debug.Log($"Create instance for MPTK_EffectUnity. <b>Set default setting in {this.name} Inspector / Synth Parameters / Unity Effect Parameters</b>");
                MPTK_EffectUnity = ScriptableObject.CreateInstance<MPTKEffectUnity>();
                MPTK_EffectUnity.DefaultAll();
            }
            MPTK_EffectUnity.Init(this);
        }


        /// @name MIDI Spatial Mapper
        /// @brief Spatial routing system used by the MidiSpatializer prefab.
        /// @details
        /// The MIDI Spatial Mapper distributes incoming MIDI events from the main
        /// MIDI reader to multiple MidiSynth instances in the scene. This allows
        /// instruments or tracks to be rendered from different spatial positions.
        ///
        /// Two routing modes are available:
        /// - Channel mode: one spatial synth per MIDI channel.
        /// - Track mode: one spatial synth per MIDI track.
        ///
        /// This system is typically used with the MidiSpatializer prefab to create
        /// immersive 3D audio scenes.
        /// @version Maestro Pro

        /// @{

        /// <summary>@brief
        /// Routing mode used by the MidiSpatializer prefab (MIDI Spatial Mapper).\n
        /// Selects how incoming Note On events are dispatched to spatial synth instances.
        /// @version Maestro Pro
        /// </summary>
        public enum ModeSpatializer
        {
            /// <summary>@brief
            /// Dispatch Note On events by MIDI channel.\n
            /// Reminder: a MIDI channel plays one instrument at a time.\n
            /// Instruments (presets) are selected per channel with MPTKCommand.PatchChange.
            /// </summary>
            Channel,

            /// <summary>@brief
            /// Dispatch Note On events by MIDI track.\n
            /// Reminder: a track may contain multiple MIDI channels, therefore multiple instruments.\n
            /// Track names come from the Meta MIDI event SequenceTrackName.\n
            /// This meta event is not always present in MIDI files, so the track name may be empty.
            /// </summary>
            Track,
        }

        /// <summary>@brief
        /// True if this MidiSynth is the master spatial synth used by MidiSpatializer.\n
        /// The master synth reads MIDI events and dispatches them to the other spatial synth instances.
        /// @version Maestro Pro
        /// </summary>
        public bool MPTK_IsSpatialSynthMaster { get { return isSpatialSynthMaster; } }

        // @cond nodoc
        /// <summary>@brief
        /// Internal flag.\n
        /// True for the master MidiSynth that reads and dispatches MIDI events.\n
        /// False for slave MidiSynth instances that only render audio.\n
        /// </summary>
        protected bool isSpatialSynthMaster = true;
        // @endcond

        [HideInInspector]
        /// <summary>@brief
        /// Current routing mode for MidiSpatializer (MIDI Spatial Mapper).
        /// @version Maestro Pro
        /// </summary>
        public ModeSpatializer MPTK_ModeSpatializer;

        /// <summary>@brief
        /// Gets or sets the maximum number of spatial synth instances that can be created/used by MidiSpatializer.
        /// @version Maestro Pro
        /// </summary>
        [HideInInspector]
        public int MPTK_MaxSpatialSynth;

        /// <summary>@brief
        /// In MidiSpatializer mode, not all MidiSynth instances are active.\n
        /// True if this synth instance is currently enabled for spatial rendering.
        /// @version Maestro Pro
        /// </summary>
        [HideInInspector]
        public bool MPTK_SpatialSynthEnabled;

        /// <summary>@brief
        /// In Track mode, returns the name of the last instrument played on this track.\n
        /// Empty string if unknown.
        /// @version Maestro Pro
        /// </summary>
        public string MPTK_InstrumentPlayed { get { return string.IsNullOrEmpty(instrumentPlayed) ? "" : instrumentPlayed; } }

        // @cond nodoc
        protected string instrumentPlayed;
        // @endcond

        /// <summary>@brief
        /// In Track mode, contains the program number (preset) of the last instrument played.
        /// @version Maestro Pro
        /// </summary>
        public int MPTK_InstrumentNum;

        /// <summary>@brief
        /// In Track mode, returns the last known track name.\n
        /// Empty string if the MIDI file does not provide SequenceTrackName.
        /// @version Maestro Pro
        /// </summary>
        public string MPTK_TrackName { get { return string.IsNullOrEmpty(trackName) ? "" : trackName; } }


        /// @}


        protected string trackName;

        // Play each midi events from the Midi reader (master synth) by sending midi events to the dedicated synth
        private void PlaySpatialEvent(MPTKEvent midievent)
        {
            if (MPTK_ModeSpatializer == ModeSpatializer.Channel)
            {
                // Channel mode, list of synths are indexed by channel, also send only event to the dedicated synth by channel
                MidiFilePlayer spatialChannel = SpatialSynths[midievent.Channel];
                if (VerboseSpatialSynth) Debug.Log($"Send MIDI event by channel to synth index {spatialChannel.MPTK_SpatialSynthIndex} distance:{spatialChannel.distanceToListener}");
                spatialChannel.MPTK_PlayDirectEvent((MPTKEvent)midievent/*.Clone()*/, false);
            }
            else
            {
                // Track mode
                if (midievent.Track < MPTK_MaxSpatialSynth)
                {
                    // List of synths are indexed by tracks
                    MidiSpatializer spatialTrack = (MidiSpatializer)SpatialSynths[(int)midievent.Track];
                    if (VerboseSpatialSynth) Debug.Log($"Send MIDI event by track to synth index {spatialTrack.MPTK_SpatialSynthIndex} distance:{spatialTrack.distanceToListener}");
                    if (spatialTrack.MPTK_SpatialSynthEnabled)
                    {
                        switch (midievent.Command)
                        {
                            case MPTKCommand.NoteOn:
                                // Find which instrument will be played on this track
                                spatialTrack.instrumentPlayed = spatialTrack.MPTK_Channels[midievent.Channel].PresetName;
                                spatialTrack.MPTK_InstrumentNum = spatialTrack.MPTK_Channels[midievent.Channel].PresetNum;

                                //Debug.Log($"{midievent.Track} {midievent.Channel} {spatializer.instrumentPlayed}");
                                spatialTrack.MPTK_PlayDirectEvent((MPTKEvent)midievent/*.Clone()*/, false);
                                break;

                            //case MPTKCommand.NoteOff: --- send noteoff to all tracks because note off can be set to another track than the note-on !!!
                            //    spatializer.MPTK_PlayDirectEvent((MPTKEvent)midievent/*.Clone()*/, false);
                            //    break;

                            case MPTKCommand.MetaEvent:
                                switch (midievent.Meta)
                                {
                                    case MPTKMeta.SequenceTrackName:
                                        spatialTrack.trackName = midievent.Info;
                                        break;
                                }

                                foreach (MidiFilePlayer mfp in SpatialSynths)
                                    mfp.MPTK_PlayDirectEvent((MPTKEvent)midievent/*.Clone()*/, false);
                                break;

                            default:
                                foreach (MidiFilePlayer mfp in SpatialSynths)
                                    mfp.MPTK_PlayDirectEvent((MPTKEvent)midievent/*.Clone()*/, false);
                                break;
                        }
                    }
                }
                else
                    Debug.LogWarning($"Not enough Spatial Synths available Track:{midievent.Track} Max:{MPTK_MaxSpatialSynth}. Increase MPTK_MaxSpatialSynth in the inspector.");
            }
        }

        // Send midi events to the UI thru the OnEventNotesMidi event
        protected void SpatialSendEvents(List<MPTKEvent> midievents)
        {
            if (midievents.Count == 1)
            {
                int indexSynth = (int)(MPTK_ModeSpatializer == ModeSpatializer.Channel ? midievents[0].Channel : midievents[0].Track);
                try
                {
                    SpatialSynths[indexSynth].OnEventNotesMidi.Invoke(midievents);
                }
                catch (Exception ex)
                {
                    Debug.LogError("OnEventNotesMidi: exception detected. Check the callback code");
                    Debug.LogException(ex);
                }
            }
            else
            {
                // Send to the channel synth
                List<MPTKEvent> synthEvents = new List<MPTKEvent>();
                foreach (MPTKEvent midievent in midievents)
                {
                    int indexSynth = (int)(MPTK_ModeSpatializer == ModeSpatializer.Channel ? midievent.Channel : midievent.Track);
                    // A warning is already log when not enough SpatialSynths are available
                    if (indexSynth < SpatialSynths.Count)
                    {
                        if (SpatialSynths[indexSynth].OnEventNotesMidi != null)
                        {
                            synthEvents.Clear();
                            synthEvents.Add(midievent);

                            try
                            {
                                SpatialSynths[indexSynth].OnEventNotesMidi.Invoke(synthEvents);
                            }
                            catch (Exception ex)
                            {
                                Debug.LogError("OnEventNotesMidi: exception detected. Check the callback code");
                                Debug.LogException(ex);
                            }
                        }
                    }
                }
            }
        }

        private void BuildSpatialSynth()
        {
            // Only the main midi reader instanciate all the others synths
            if (this is MidiSpatializer && MPTK_SpatialSynthIndex < 0)
            {
                MPTK_MaxSpatialSynth = Mathf.Clamp(MPTK_MaxSpatialSynth, 16, 100);
                SpatialSynths = new List<MidiFilePlayer>();//  new MidiFilePlayer[16];
                for (int idSynth = 0; idSynth < MPTK_MaxSpatialSynth; idSynth++)
                {
                    if (VerboseSpatialSynth) Debug.Log($"BuildSpatialSynth: instantiate synth IdSynth:{idSynth} from '{this.name}'");
                    // Bad parameters could run in an infinite loop, bodyguard below
                    if (lastIdSynth > 100) break;

                    MidiFilePlayer mfp = Instantiate<MidiFilePlayer>((MidiFilePlayer)this);
                    //Debug.Log($"BuildSpatialSynth: after Instantiate, internal synth ID:{mfp.IdSynth}");

                    mfp.spatialSynthIndex = idSynth;
                    mfp.name = $"Synth Id{idSynth + 1}";
                    mfp.MPTK_PlayOnStart = false;
                    mfp.MPTK_InitSynth();
                    mfp.isSpatialSynthMaster = false;
                    mfp.trackName = "";
                    mfp.instrumentPlayed = "";
                    //mfp.hideFlags = HideFlags.DontSave;
                    SpatialSynths.Add(mfp);
                }

                // Avoid set parent in the previous loop because infinite loop are created. Why? I don't known!!!
                // Classically, parent is also defined by the user script to assign MIDI synth to dedicated game object.
                foreach (MidiFilePlayer mfp in SpatialSynths)
                {
                    try
                    {
                        if (VerboseSpatialSynth) Debug.Log($"BuildSpatialSynth: set parent to '{this.name}' for id {mfp.IdSynth}");
                        mfp.transform.SetParent(this.transform);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"BuildSpatialSynth: can't set parent:{mfp.IdSynth} {ex}");
                    }
                }
            }
        }

        private void ApplyDistanceAttenuationForSpatializer()
        {
            foreach (MidiFilePlayer mfp in SpatialSynths)
            {
                if (mfp != null)
                {
                    //Debug.Log($"ApplySpatialToAudioSource to {mfp.name}");
                    ApplyDistanceAttenuationToAudioSource(mfp.CoreAudioSource);
                }
            }
        }
        private void OnDestroy()
        {
            //Debug.Log($"OnDestroy {MPTK_SpatialSynthIndex}");
            RemoveSpatialSynth();
        }

        private void RemoveSpatialSynth()
        {
            // Only the main midi reader instanciate all the others synths
            if (this is MidiSpatializer && MPTK_SpatialSynthIndex < 0)
            {
                MidiSpatializer[] goMidiGlobal = FindObjectsByType<MidiSpatializer>(FindObjectsSortMode.None);
                if (goMidiGlobal != null)
                    foreach (MidiSpatializer go in goMidiGlobal)
                    {
                        if (VerboseSpatialSynth) Debug.Log($"RemoveSpatialSynth: find {go.IdSynth} {go.name}");
                        UnityEngine.Object.Destroy(go);
                    }
            }
        }

        private void StartFrame()
        {
            try
            {
                if (OnAudioFrameStart != null)
                    OnAudioFrameStart.Invoke(SynthElapsedMilli);
            }
            catch (Exception ex)
            {
                Debug.LogError("OnAudioFrameStart: exception detected. Check the callback code");
                Debug.LogException(ex);
            }
        }

        private bool StartMidiEvent(MPTKEvent midi)
        {
            try
            {
                if (OnMidiEvent != null)
                    return OnMidiEvent(midi);
            }
            catch (Exception ex)
            {
                Debug.LogError("OnMidiEvent: exception detected. Check the callback code");
                Debug.LogException(ex);
            }
            return true;
        }

        private void AnalyseActionMeta(MPTKEvent midiEvent)
        {
            if (midiEvent.Info != null && midiEvent.Info.Length > 0 && midiEvent.Info[0] == '@' /* just a quick check for efficiency */)
            {
                try
                {
                    if (midiEvent.Info.StartsWith("@ACTION:", StringComparison.InvariantCultureIgnoreCase))
                    {
                        // Search string between : and ( 
                        Match match = Regex.Match(midiEvent.Info, @":(.+)\(");
                        if (match.Success)
                        {
                            string action = match.Groups[1].Value.ToUpper().Trim();
                            //Debug.Log(action);

                            /* Si vous voulez extraire la chaîne entre ( et ), vous pouvez modifier l'expression régulière comme suit: `\((.+)\)`. 
                               Cette expression régulière correspond à n'importe quelle chaîne entre ( et ) et utilise le groupe de capture `(.+)` pour capturer 
                               la chaîne recherchée. Thanks ChatGPT ;-) 
                            */
                            // Search string between ( and )
                            match = Regex.Match(midiEvent.Info, @"\((.+)\)");
                            if (match.Success)
                            {
                                string[] param = match.Groups[1].Value.ToUpper().Trim().Split(',');
                                for (int i = 0; i < param.Length; i++)
                                {
                                    param[i] = param[i].Trim();
                                    //Debug.Log(param[i]);
                                }
                                switch (action)
                                {
                                    case "INNER_LOOP":
                                        if (param.Length < 3)
                                            throw new Exception($"Bad count of parameters for '{action}'. Must be tree integer: Resume, End, Max");
                                        MPTKInnerLoop innerLoop = ((MidiFilePlayer)this).MPTK_InnerLoop;
                                        try { innerLoop.Resume = Convert.ToInt64(param[0]); } catch { throw new Exception($"Action '{action}', parameter Resume incorrect."); }
                                        try { innerLoop.End = Convert.ToInt64(param[1]); } catch { throw new Exception($"Action '{action}', parameter End incorrect."); }
                                        try { innerLoop.Max = Convert.ToInt32(param[2]); } catch { throw new Exception($"Action '{action}', parameter Max incorrect."); }
                                        if (innerLoop.Resume < 0) innerLoop.Resume = 0;
                                        if (innerLoop.End < 0) innerLoop.End = 0;
                                        if (innerLoop.Max < 0) innerLoop.Max = 0;
                                        innerLoop.Finished = false;
                                        innerLoop.Enabled = true;
                                        innerLoop.Count = 0; // reset count
                                        //innerLoop.Log = true;
                                        Debug.Log("Inner loop command detected in META " + innerLoop.ToString());
                                        break;
                                    //case "EVENT":
                                    //    if (param.Length < 2)
                                    //        throw new Exception($"Bad count of parameters for '{action}'. Must be tree integer: Resume, End, Max.");
                                    //    break;
                                    default:
                                        throw new Exception($"Action '{action}' unknown.");
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"Format error in '{midiEvent.Info}' at tick {midiEvent.Tick} - {ex}");
                }
            }
        }


#if UNITY_ANDROID && UNITY_OBOE
        public AudioStream oboeAudioStream;
        public void InitOboe()
        {
            if (Application.isPlaying)
            {
                //MPTK_StopSynth();
                if (oboeAudioStream != null)
                {
                    oboeAudioStream.Stop();
                    oboeAudioStream.Close();
                }

                OboeManager.Initialize();
                OboeMixer mixer = new OboeMixer();

                if (VerboseSynth)
                    Debug.Log($"Init Oboe rate:{MPTK_SynthRate} buffer size:{DspBufferSize}");

                using (AudioStreamBuilder audioStreamBuilder = new AudioStreamBuilder
                {
                    Format = AudioFormat.Float,
                    ChannelCount = 2,
                    SampleRate = MPTK_SynthRate, // 0,// MPTK_SynthRate; // changing rate with Oboe is not a good idea
                    DataCallback = mixer,
                    AudioApi = AudioApi.Unspecified,
                    PerformanceMode = PerformanceMode.LowLatency,
                    SharingMode = SharingMode.Exclusive,
                    //BufferCapacityInFrames = DspBufferSize, // https://developer.android.com/ndk/reference/group/audio#aaudiostream_setbuffersizeinframes
                    FramesPerCallback = DspBufferSize, // https://github.com/search?q=repo%3Agoogle%2Foboe%20multiple%2064&type=code
                    IsFormatConversionAllowed = true,
                    Direction = Direction.Output
                })
                {
                    Result result = audioStreamBuilder.OpenStream(out oboeAudioStream);
                    if (result != Result.OK)
                        Debug.LogError($"Oboe Error - result:{result} ");
                    else
                    {
                        mixer.processors.Add(this);
                        oboeAudioStream.Start();
                        OutputRate = oboeAudioStream.SampleRate;
                        if (VerboseSynth)
                            LogInfoAudio();
                    }
                }
            }
        }

        static void TestAndroidSamples(int numFrames)
        {
            Debug.Log($"TestAndroidSamples numFrames:{numFrames}");
            int block = 0;
            int buffSize;
            while (block < numFrames)
            {
                if (numFrames - block < 64)
                    // Occurs when numFrame is not a multiple of 64, the last frame will be lower than 64
                    // We tried to limit the processing to a value lower than 64 (with not greater success)
                    buffSize = numFrames - block;
                else
                    buffSize = 64;

                Debug.Log($"    Block:{block} Write {buffSize} float");
                block += buffSize;
            }
        }

        unsafe private void WriteAndroidSamples(void* dataArray, long ticks, int numFrames)
        {
#if DEBUG_HISTO_SYNTH
            synthInfo.NextHistoPosition();
            synthInfo.SizeBuffOnAudio = numFrames;
#endif
            float* data = (float*)dataArray;
            int block = 0;
            if (ActiveVoices.Count > 0)
            {
                //if (numFrames < FLUID_BUFSIZE)
                //    Debug.LogWarning($"numframes = {numFrames} is bellow {FLUID_BUFSIZE}");
                //else
                while (block < numFrames)
                {
                    //if (numFrames - block < 64)
                    //    // Occurs when numFrame is not a multiple of 64, the last frame will be lower than 64
                    //    // We tried to limit the processing to a value lower than 64 (with no greater success)
                    //    FLUID_BUFSIZE = numFrames - block;
                    //else
                    //FLUID_BUFSIZE = 64;
                    Array.Clear(left_buf, 0, FLUID_BUFSIZE);
                    Array.Clear(right_buf, 0, FLUID_BUFSIZE);

                    float[] reverb_buf = null;
                    float[] chorus_buf = null;

                    //                    if (oboeAudioStream.BufferSizeInFrames % 64 == 0)
                    // Effect don't like buffer length not 64
                    MPTK_EffectSoundFont.PrepareBufferEffect(out reverb_buf, out chorus_buf);

                    WriteAllSamples(ticks, reverb_buf, chorus_buf);

                    //                   if (oboeAudioStream.BufferSizeInFrames % 64 == 0)
                    MPTK_EffectSoundFont.ProcessEffect(reverb_buf, chorus_buf, left_buf, right_buf);

                    float vol = MPTK_Volume * volumeStartStop;

                    for (int i = 0; i < FLUID_BUFSIZE; i++)
                    {
                        int j = (block + i) << 1;// * 2; v2.10.0
                        data[j] = left_buf[i] * vol;
                        data[j + 1] = right_buf[i] * vol;
                    }
                    block += FLUID_BUFSIZE;
                }
            }
        }
#endif
    }
#if UNITY_ANDROID && UNITY_OBOE

    public class OboeMixer : AudioStreamDataCallback
    {
        public List<IMixerProcessor> processors = new List<IMixerProcessor>();
        float[] zeroArray;

        public unsafe override DataCallbackResult OnAudioData(AudioStream audioStream, void* dataArray, int numFrames)
        {
            // Seems no working well ...
            //try
            //{
            //    if (zeroArray == null)
            //        // Allocate and set to 0
            //        zeroArray = new float[5000];

            //    // The Marshal.Copy method is copying x elements from the zeroArray to dataArray.
            //    // Since zeroArray is an array of float, and each float is 4 bytes,
            //    // the total number of bytes copied will be x * 4 bytes.
            //    Marshal.Copy(zeroArray, 0, (IntPtr)dataArray, numFrames * audioStream.ChannelCount);
            //}
            //catch (Exception ex)
            //{
            //    Debug.LogError($" DataCallbackResult OnAudioData {ex}");
            //    return DataCallbackResult.Stop;
            //}

            float* data = (float*)dataArray;
            for (int i = 0; i < numFrames; ++i)
            {
                data[i * 2] = 0;
                data[i * 2 + 1] = 0;
            }
            //Debug.Log($"cp:{processors.Count} numFrames:{numFrames}");
            foreach (var cp in processors)
            {
                cp.OnAudioData(audioStream, dataArray, numFrames);
            }
            return DataCallbackResult.Continue;
        }
    }

    public interface IMixerProcessor
    {
        unsafe void OnAudioData(AudioStream audioStream, void* dataArray, int numFrames);
    }
#endif

}


