using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace MidiPlayerTK
{
    /// @ingroup midifileplayer_management
    public partial class MidiFilePlayer : MidiSynth
    {

        /// <summary>@brief
        /// Defines looping conditions inside the MIDI sequencer [Pro].
        /// An instance is automatically created when the MidiFilePlayer prefab is loaded.\n
        /// See #MPTKInnerLoop and #MPTK_RawSeek.

        /// @name Inner Loop
        /// @{

        /// 
        /// @version Maestro Pro 
        /// 
        /// @snippet TestInnerLoop.cs ExampleMidiInnerLoop
        /// </summary>
        /// @ingroup midifileplayer_pro
        public MPTKInnerLoop MPTK_InnerLoop;

        /// @}



        /// @name MIDI Loading and Reading
        /// @{

        /// <summary>@brief
        /// Loads a MIDI file from a local desktop file. Example paths:
        ///     - Mac: "/Users/xxx/Desktop/WellTempered.mid"\n
        ///     - Windows: "C:\Users\xxx\Desktop\BIM\Sound\Midi\DreamOn.mid"\n
        /// @note
        ///     - Logs are displayed in case of error.
        ///     - Look at MPTK_StatusLastMidiLoaded for load status information.\n
        ///     - Look at MPTK_MidiLoaded for detailed information about the MIDI loaded.\n
        /// @version Maestro Pro 
        /// </summary>
        /// <param name="uri">URI or path to the MIDI file.</param>
        /// <returns>A MidiLoad object to access all properties of the loaded MIDI, or null in case of error.</returns>
        /// @ingroup midifileplayer_pro
        /// @ingroup midifileplayer_loading
        public MidiLoad MPTK_Load(string uri)
        {
            try
            {
                MPTK_StatusLastMidiLoaded = LoadingStatusMidiEnum.NotYetDefined;
                if (string.IsNullOrEmpty(uri))
                {
                    MPTK_StatusLastMidiLoaded = LoadingStatusMidiEnum.MidiNameInvalid;
                    Debug.LogWarning($"MPTK_Load: file path not defined");
                    return null;
                }
                else if (!File.Exists(uri)) // file:///C:/Devel/Maestro/MidiFile/beethoven-sonata-14-3.mid
                {
                    MPTK_StatusLastMidiLoaded = LoadingStatusMidiEnum.NotFound;
                    Debug.LogWarning($"MPTK_Load: {uri} not found");
                    return null;
                }
                else
                {
                    try
                    {
                        using (Stream fsMidi = new FileStream(uri, FileMode.Open, FileAccess.Read))
                        {
                            byte[] data = new byte[fsMidi.Length];
                            fsMidi.Read(data, 0, (int)fsMidi.Length);

                            if (data.Length < 4)
                            {
                                MPTK_StatusLastMidiLoaded = LoadingStatusMidiEnum.TooShortSize;
                                //Debug.LogWarning($"Error Loading Midi:{pathmidiNameToPlay} - Not a midi file, too short size");
                            }
                            else if (System.Text.Encoding.Default.GetString(data, 0, 4) != "MThd")
                            {
                                MPTK_StatusLastMidiLoaded = LoadingStatusMidiEnum.NoMThdSignature;
                            }
                            else
                            {
                                midiLoaded = new MidiLoad();
                                midiLoaded.MPTK_KeepNoteOff = MPTK_KeepNoteOff;
                                midiLoaded.MPTK_KeepEndTrack = MPTK_KeepEndTrack;
                                midiLoaded.MPTK_LogLoadEvents = MPTK_LogLoadEvents;
                                midiLoaded.MPTK_EnableChangeTempo = MPTK_EnableChangeTempo;
                                midiLoaded.MPTK_Load(data);
                                MPTK_StatusLastMidiLoaded = LoadingStatusMidiEnum.Success;
                            }
                        }
                    }
                    catch (System.Exception ex)
                    {
                        MPTK_StatusLastMidiLoaded = LoadingStatusMidiEnum.MidiFileInvalid;
                        MidiPlayerGlobal.ErrorDetail(ex);
                        return null;
                    }
                }
            }
            catch (System.Exception ex)
            {
                MidiPlayerGlobal.ErrorDetail(ex);
                return null;
            }
            return midiLoaded;
        }
        /// @}

        /// @name Play and Stop MIDI
        /// @{

        /// <summary>@brief
        /// Finds a MIDI in the Unity Resources folder `MidiDB` whose name contains `searchPartOfName` (case-sensitive).\n
        /// Sets MPTK_MidiIndex and MPTK_MidiName if the MIDI is found.
        /// 
        /// @note
        ///     - Add Midi files to your project with the Unity menu Maestro.
        ///     
        /// @version Maestro Pro 
        /// 
        /// @code
        /// // Find the first MIDI file name in MidiDB that contains "Adagio" in its name
        /// if (midiFilePlayer.MPTK_SearchMidiToPlay("Adagio"))
        /// {
        ///     Debug.Log($"MPTK_SearchMidiToPlay: {MPTK_MidiIndex} {MPTK_MidiName}");
        ///     // And play it
        ///     midiFilePlayer.MPTK_Play();
        /// }
        /// @endcode
        /// </summary>
        /// <param name="searchPartOfName">Part of the MIDI name to search for in the MIDI list (case-sensitive).</param>
        /// <returns>True if found; otherwise, false.</returns>
        /// @ingroup midifileplayer_pro
        /// @ingroup midifileplayer_selection
        public bool MPTK_SearchMidiToPlay(string searchPartOfName)
        {
            int index = -1;
            try
            {
                if (!string.IsNullOrEmpty(searchPartOfName))
                {
                    if (MidiPlayerGlobal.CurrentMidiSet != null && MidiPlayerGlobal.CurrentMidiSet.MidiFiles != null)
                    {
                        index = MidiPlayerGlobal.CurrentMidiSet.MidiFiles.FindIndex(s => s.Contains(searchPartOfName));
                        if (index >= 0)
                        {
                            MPTK_MidiIndex = index;
                            //Debug.Log($"MPTK_SearchMidiToPlay: {MPTK_MidiIndex} - '{MPTK_MidiName}'");
                            return true;
                        }
                        else
                            Debug.LogWarningFormat("No MIDI file found with '{0}' in name", searchPartOfName);
                    }
                    else
                        Debug.LogWarning(MidiPlayerGlobal.ErrorNoMidiFile);
                }
            }
            catch (System.Exception ex)
            {
                MidiPlayerGlobal.ErrorDetail(ex);
            }
            return false;
        }


        /// <summary>@brief
        /// Plays the next or previous MIDI from the MidiDB list.
        /// @version Maestro Pro 
        /// </summary>
        /// <param name="offset">Forward or backward offset in the list. `1` = next, `-1` = previous.</param>
        /// @ingroup midifileplayer_pro
        /// @ingroup midifileplayer_playback_controls
        public void MPTK_PlayNextOrPrevious(int offset)
        {
            try
            {
                if (MidiPlayerGlobal.CurrentMidiSet.MidiFiles != null && MidiPlayerGlobal.CurrentMidiSet.MidiFiles.Count > 0)
                {
                    int selectedMidi = MPTK_MidiIndex + offset;
                    if (selectedMidi < 0)
                        selectedMidi = MidiPlayerGlobal.CurrentMidiSet.MidiFiles.Count - 1;
                    else if (selectedMidi >= MidiPlayerGlobal.CurrentMidiSet.MidiFiles.Count)
                        selectedMidi = 0;
                    MPTK_MidiIndex = selectedMidi;
                    if (offset < 0)
                        prevMidi = true;
                    else
                        nextMidi = true;
                    MPTK_RePlay();
                }
                else
                    Debug.LogWarning(MidiPlayerGlobal.ErrorNoMidiFile);
            }
            catch (System.Exception ex)
            {
                MidiPlayerGlobal.ErrorDetail(ex);
            }
        }

        /// <summary>@brief
        /// Switches playback between two MIDIs with ramp-up.\n
        /// This method is useful for integration with Bolt: main MIDI parameters are defined in one call.
        /// @version Maestro Pro 
        /// </summary>
        /// <param name="index">Index of the MIDI to play. Used only if <paramref name="name"/> is null or empty.</param>
        /// <param name="name">Name of the MIDI to play. Can be part of the MIDI name. If set, this parameter has priority over <paramref name="index"/>.</param>
        /// <param name="volume">Volume of the MIDI. Use `-1` to keep the default volume.</param>
        /// <param name="delayToStopMillisecond">Delay before stopping the currently playing MIDI (with volume decrease), or delay before playback if no MIDI is playing.</param>
        /// <param name="delayToStartMillisecond">Delay to reach full MIDI volume (ramp-up volume).</param>
        /// @ingroup midifileplayer_pro
        /// @ingroup midifileplayer_playback_controls
        public void MPTK_SwitchMidiWithDelay(int index, string name, float volume, float delayToStopMillisecond, float delayToStartMillisecond)
        {
            if (volume >= 0f)
                MPTK_Volume = volume;
            //Debug.Log($"Search for {name}");
            if (delayToStopMillisecond < 0f) delayToStopMillisecond = 0f;
            if (delayToStartMillisecond < 0f) delayToStartMillisecond = 0f;
            MPTK_Stop(delayToStopMillisecond);

            if (!string.IsNullOrWhiteSpace(name))
            {
                MPTK_SearchMidiToPlay(name);
            }
            else
                MPTK_MidiIndex = index;

            Routine.RunCoroutine(TheadPlayWithDelay(delayToStopMillisecond, delayToStartMillisecond), Segment.RealtimeUpdate);
        }

        /// <summary>@brief
        /// Plays the MIDI file defined by MPTK_MidiName or MPTK_MidiIndex with ramp-up to the volume defined by MPTK_Volume.\n
        /// The time to get a MIDI playing at full MPTK_Volume is delayRampUp + startDelay.\n
        /// A delayed start can also be set.
        /// @version Maestro Pro 
        /// </summary>
        /// <param name="delayRampUp">Ramp-up delay, in milliseconds, to reach the default volume.</param>
        /// <param name="startDelay">Delayed start, in milliseconds. V2.89.1</param>
        /// @ingroup midifileplayer_pro
        /// @ingroup midifileplayer_playback_controls
        public virtual void MPTK_Play(float delayRampUp, float startDelay = 0)
        {
            Routine.CallDelayed(startDelay / 1000f, () =>
            {
                //Debug.Log("Delayed play");
                needDelayToStop = false;
                delayRampUpSecond = delayRampUp / 1000f;
                timeRampUpSecond = Time.realtimeSinceStartup + delayRampUpSecond;
                needDelayToStart = true;
                MPTK_Play();
            });
        }
        /// <summary>@brief
        /// Plays a MIDI file from a byte array.\n
        /// Check MPTK_StatusLastMidiLoaded to get the load status.
        /// @version Maestro Pro 
        /// @code
        ///   // Example for Windows or macOS
        ///   using (Stream fsMidi = new FileStream(filepath, FileMode.Open, FileAccess.Read))
        ///   {
        ///         byte[] data = new byte[fsMidi.Length];
        ///         fsMidi.Read(data, 0, (int) fsMidi.Length);
        ///         midiFilePlayer.MPTK_Play(data);
        ///   }
        /// @endcode
        /// </summary>
        /// @ingroup midifileplayer_pro
        /// @ingroup midifileplayer_loading
        /// @ingroup midifileplayer_playback_controls
        public void MPTK_Play(byte[] data)
        {
            try
            {
                MPTK_StatusLastMidiLoaded = LoadingStatusMidiEnum.NotYetDefined;

                if (MPTK_SoundFont.IsReady)
                {
                    playPause = false;

                    if (!MPTK_IsPlaying)
                    {
                        if (data == null)
                        {
                            //Debug.LogWarning("MPTK_Play: set MPTK_MidiName or Midi Url/path in inspector before playing");
                            MPTK_StatusLastMidiLoaded = LoadingStatusMidiEnum.MidiNameNotDefined;
                        }
                        else if (data.Length < 4)
                        {
                            MPTK_StatusLastMidiLoaded = LoadingStatusMidiEnum.TooShortSize;
                            //Debug.LogWarning($"Error Loading Midi:{pathmidiNameToPlay} - Not a midi file, too short size");
                        }
                        else if (System.Text.Encoding.Default.GetString(data, 0, 4) != "MThd")
                        {
                            MPTK_StatusLastMidiLoaded = LoadingStatusMidiEnum.NoMThdSignature;
                            //Debug.LogWarning($"Error Loading Midi:{pathmidiNameToPlay} - Not a midi file, signature MThd not found");
                        }
                    }
                    else
                    {
                        //Debug.LogWarning("Already playing - " + pathmidiNameToPlay);
                        MPTK_StatusLastMidiLoaded = LoadingStatusMidiEnum.AlreadyPlaying;
                    }
                }
                else
                {
                    //Debug.LogWarning("SoundFont not loaded");
                    MPTK_StatusLastMidiLoaded = LoadingStatusMidiEnum.SoundFontNotLoaded;
                }

                // If no error, load and play the midi in background
                if (MPTK_StatusLastMidiLoaded == LoadingStatusMidiEnum.NotYetDefined)
                {
                    MPTK_InitSynth();
                    MPTK_StartSequencerMidi();

                    // Start playing
                    if (MPTK_CorePlayer)
                        Routine.RunCoroutine(ThreadCorePlay(data).CancelWith(gameObject), Segment.RealtimeUpdate);
                    else
                        Routine.RunCoroutine(ThreadLegacyPlay(data, "").CancelWith(gameObject), Segment.RealtimeUpdate);
                }
                else
                {
                    try
                    {
                        if (OnEventEndPlayMidi != null)
                            OnEventEndPlayMidi.Invoke(MPTK_MidiName, EventEndMidiEnum.MidiErr);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError("OnEventEndPlayMidi: exception detected. Check the callback code");
                        Debug.LogException(ex);
                    }
                }
            }

            catch (System.Exception ex)
            {
                MidiPlayerGlobal.ErrorDetail(ex);
            }
        }

        /// <summary>@brief
        /// Plays a MIDI from a MidiFileWriter2 object.
        /// @version Maestro Pro 
        /// @snippet TestMidiGenerator.cs ExampleMIDIPlayFromWriter
        /// </summary>
        /// <param name="mfw2">A MidiFileWriter2 object.</param>
        /// <param name="delayRampUp"></param>
        /// @ingroup midifileplayer_pro
        /// @ingroup midifileplayer_loading
        /// @ingroup midifileplayer_playback_controls
        public void MPTK_Play(MPTKWriter mfw2, float delayRampUp = 0f, float fromPosition = 0, float toPosition = 0, long fromTick = 0, long toTick = 0, bool timePosition = true)
        {
            try
            {
                // There is no duration on noteon when created from MidiFileWriter, we need noteoff to stop the note
                playNoteOff = true;
                MPTK_StatusLastMidiLoaded = LoadingStatusMidiEnum.NotYetDefined;

                if (delayRampUp > 0f)
                {
                    delayRampUpSecond = delayRampUp / 1000f;
                    timeRampUpSecond = Time.realtimeSinceStartup + delayRampUpSecond;
                    needDelayToStart = true;
                }
                else
                    needDelayToStart = false;

                if (MPTK_SoundFont.IsReady)
                {
                    playPause = false;

                    if (!MPTK_IsPlaying)
                    {
                        if (mfw2 == null)
                        {
                            //Debug.LogWarning("MPTK_Play: set MPTK_MidiName or Midi Url/path in inspector before playing");
                            MPTK_StatusLastMidiLoaded = LoadingStatusMidiEnum.MidiNameNotDefined;
                        }
                        //else if (mfw2.Length < 4)
                        //{
                        //    MPTK_StatusLastMidiLoaded = LoadingStatusMidiEnum.TooShortSize;
                        //    //Debug.LogWarning($"Error Loading Midi:{pathmidiNameToPlay} - Not a midi file, too short size");
                        //}
                    }
                    else
                    {
                        //Debug.LogWarning("Already playing - " + pathmidiNameToPlay);
                        MPTK_StatusLastMidiLoaded = LoadingStatusMidiEnum.AlreadyPlaying;
                    }
                }
                else
                {
                    //Debug.LogWarning("SoundFont not loaded");
                    MPTK_StatusLastMidiLoaded = LoadingStatusMidiEnum.SoundFontNotLoaded;
                }

                // If no error, load and play the midi in background (v2.9.0 play also when already playing)
                if (MPTK_StatusLastMidiLoaded == LoadingStatusMidiEnum.NotYetDefined || MPTK_StatusLastMidiLoaded == LoadingStatusMidiEnum.AlreadyPlaying)
                {
                    MPTK_InitSynth(mfw2.ChannelCount); // 2.12.2
                    MPTK_StartSequencerMidi();
                    midiNameToPlay = string.IsNullOrEmpty(mfw2.MidiName) ? "(no name)" : mfw2.MidiName;

                    if (Application.isPlaying)
                        Routine.RunCoroutine(ThreadMFWPlay(mfw2, fromPosition, toPosition, fromTick, toTick, timePosition).CancelWith(gameObject).CancelWith(gameObject), Segment.RealtimeUpdate);
                    else
                        Routine.RunCoroutine(ThreadMFWPlay(mfw2, fromPosition, toPosition, fromTick, toTick, timePosition), Segment.EditorUpdate);
                }
                else
                {
                    try
                    {
                        if (OnEventEndPlayMidi != null)
                            OnEventEndPlayMidi.Invoke(mfw2.MidiName ?? "(no name)", EventEndMidiEnum.MidiErr);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError("OnEventEndPlayMidi: exception detected. Check the callback code");
                        Debug.LogException(ex);
                    }
                }
            }

            catch (System.Exception ex)
            {
                MidiPlayerGlobal.ErrorDetail(ex);
            }
        }

        /// <summary>@brief
        /// Stops playback after a delay. After the stop delay (0 by default), the volume decreases until playback stops.\n
        /// The time to get a real MIDI stop is delayRampDown + stopDelay.
        /// @version Maestro Pro 
        /// </summary>
        /// <param name="delayRampDown">Fade-out time in milliseconds.</param>
        /// <param name="stopDelay">Delayed stop in milliseconds. V2.89.1</param>
        /// @ingroup midifileplayer_pro
        /// @ingroup midifileplayer_playback_controls
        public virtual void MPTK_Stop(float delayRampDown, float stopDelay = 0)
        {
            Routine.CallDelayed(stopDelay / 1000f, () =>
            {
                if (midiLoaded != null && midiIsPlaying)
                {
                    needDelayToStart = false;
                    delayRampDnSecond = delayRampDown / 1000f;
                    timeRampDnSecond = Time.realtimeSinceStartup + delayRampDnSecond;
                    needDelayToStop = true;
                }
            });
        }

        //! @cond NODOC
        public void StopAndPlayMidi(int index, string name)
        {
            MPTK_Stop();
            if (!string.IsNullOrWhiteSpace(name))
                MPTK_SearchMidiToPlay(name);
            else
                MPTK_MidiIndex = index;
            MPTK_Play();
        }

        /// @}

        protected IEnumerator<float> TheadPlayWithDelay(float delayToStopMillisecond, float delayToStartMillisecond)
        {
            //Debug.Log($"TheadPlayWithDelay for {delayToStopMillisecond}");
            yield return Routine.WaitForSeconds((delayToStopMillisecond + 100f) / 1000f);
            //Debug.Log($"TheadPlayWithDelay play {delayToStartMillisecond}");
            MPTK_Play(delayToStartMillisecond);
        }

        public void PlayAndPauseMidi(int index, string name, int pauseMillisecond = -1)
        {
            MPTK_Stop();
            if (!string.IsNullOrWhiteSpace(name))
                MPTK_SearchMidiToPlay(name);
            else
                MPTK_MidiIndex = index;
            MPTK_Play();
            MPTK_Pause(pauseMillisecond);
        }

        protected IEnumerator<float> ThreadMFWPlay(MPTKWriter mfw2, float fromPosition = 0, float toPosition = 0, long fromTick = 0, long toTick = 0, bool timePosition = true)
        {
            StartPlaying();
            string currentMidiName = midiNameToPlay;
            //Debug.Log("Start play " + fromPosition + " " + toPosition);
            try
            {

                midiLoaded = new MidiLoad();
                midiLoaded.MPTK_KeepNoteOff = MPTK_KeepNoteOff;
                midiLoaded.MPTK_KeepEndTrack = MPTK_KeepEndTrack;
                midiLoaded.MPTK_LogLoadEvents = MPTK_LogLoadEvents;
                midiLoaded.MPTK_EnableChangeTempo = MPTK_EnableChangeTempo;
                if (!midiLoaded.MPTK_Load(mfw2))
                    midiLoaded = null;
#if DEBUG_START_MIDI
                Debug.Log("After load midi " + (double)watchStartMidi.ElapsedTicks / ((double)System.Diagnostics.Stopwatch.Frequency / 1000d));
#endif
            }
            catch (System.Exception ex)
            {
                MidiPlayerGlobal.ErrorDetail(ex);
            }
            if (midiLoaded != null)
            {
                if (!timePosition)
                {
                    midiLoaded.MPTK_TickStart = fromTick;
                    midiLoaded.MPTK_TickEnd = toTick;
                }
                if (MPTK_CorePlayer)
                {
                    if (Application.isPlaying)
                        Routine.RunCoroutine(ThreadInternalMidiPlaying(currentMidiName, fromPosition, toPosition).CancelWith(gameObject), Segment.RealtimeUpdate);
                    else
                        Routine.RunCoroutine(ThreadInternalMidiPlaying(currentMidiName, fromPosition, toPosition), Segment.EditorUpdate);
                }
                else
                {
                    Debug.Log($"ThreadMFWPlay ThreadLegacyPlay");
                    Routine.RunCoroutine(ThreadLegacyPlay(midiBytesToPlay: null, currentMidiName, fromPosition, toPosition, alreadyLoaded: true).CancelWith(gameObject), Segment.RealtimeUpdate);
                }
            }
            yield return 0;
        }
        //! @endcond
    }
}

