
//using MEC;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using UnityEngine;
using UnityEngine.Networking;

namespace MidiPlayerTK
{
    /// @ingroup pro_external_playback
    /// <summary>
    /// Plays a local MIDI file or a MIDI file from a website. This class must be used with the MidiExternalPlayer prefab.\n
    /// 
    /// You do not need to write a script for simple usage; everything can be configured in the prefab inspector.\n\n
    /// A set of methods is also available to control playback from scripts.\n
    /// This class inherits from MidiFilePlayer and MidiSynth, so all properties, events, and methods from both classes are available.\n\n
    /// More information here: https://paxstellar.fr/midi-external-player-v2/
    ///
    /// @attention MidiExternalPlayer inherits from MidiFilePlayer and MidiSynth. For clarity, only MidiExternalPlayer attributes are documented here.
    /// See MidiFilePlayer and MidiSynth for the full API surface.
    ///
    /// @version 
    ///     Maestro Pro 
    /// 
    /// Example for loading and playing a MIDI file from a website.
    /// @code
    /// // Example script. See TestMidiExternalPlayer.cs for a more detailed use case.
    /// // A reference to the prefab is required (set in the hierarchy or by script).
    /// MidiExternalPlayer midiExternalPlayer;
    /// 
    /// if (midiExternalPlayer == null)
    ///    Debug.LogError("TestMidiExternalPlayer: there is no MidiExternalPlayer Prefab set in Inspector.");
    /// // Load a MIDI file from a website.
    /// midiExternalPlayer.MPTK_MidiName = "http://www.midiworld.com/midis/other/c2/bolero.mid";
    /// midiExternalPlayer.MPTK_Play();
    /// // Later, load from a local folder (macOS example).
    /// midiExternalPlayer.MPTK_MidiName = "file:///Users/thierry/Desktop/Nirvana.mid"
    /// midiExternalPlayer.MPTK_Play();
    /// @endcode
    /// </summary>
    [HelpURL("https://paxstellar.fr/midi-external-player-v2/")]
    public class MidiExternalPlayer : MidiFilePlayer
    {
        /// @name MIDI Playback Control
        /// @brief Controls playback of MIDI events and sequences.
        /// @details
        /// These methods allow scripts to start, stop, pause, or control the flow
        /// of MIDI playback. They interact with the internal MIDI sequencer and
        /// control how events are scheduled and rendered by the synthesizer.
        ///
        /// This includes transport control and runtime playback management.        

        /// @{

        /// <summary>@brief
        /// Sets the full URI to a MIDI file: use file:// for a local file, or http:// / https:// for a web resource.
        /// The MIDI file is then loaded and played.\n
        /// 
        /// See https://en.wikipedia.org/wiki/File_URI_scheme for URI examples.
        /// @snippet SimplestMidiExternalPlayer.cs SimplestMidiExternalPlayer
        /// </summary>
        public new string MPTK_MidiName
        {
            get
            {
                return pathmidiNameToPlay;
            }
            set
            {
                pathmidiNameToPlay = value.Trim();
                base.MPTK_MidiName = pathmidiNameToPlay;
            }
        }

        /// @}

        [SerializeField]
        [HideInInspector]
        private string pathmidiNameToPlay;

        protected new void Awake()
        {
            //Debug.Log("Awake MidiExternalPlayer:" + MPTK_IsPlaying + " " + MPTK_PlayOnStart + " " + MPTK_IsPaused);
            base.AwakeMidiFilePlayer(); // V2.83
            //base.Awake(); 
        }

        protected new void Start()
        {
            //Debug.Log("Start MidiExternalPlayer:" + MPTK_IsPlaying + " " + MPTK_PlayOnStart + " " + MPTK_IsPaused);
            base.StartMidiFilePlayer(); // V2.83

            OnEventStartPlayMidi.AddListener((string midiname) =>
            {
                //Debug.Log($"Start playing {midiname}");
                MPTK_StatusLastMidiLoaded = 0;
            });

            OnEventEndPlayMidi.AddListener((string midiname, EventEndMidiEnum reason) =>
            {
                //Debug.Log($"End playing {midiname} {reason}");
                //if (reason == EventEndMidiEnum.MidiErr && MPTK_StatusLastMidiLoaded == LoadingStatusMidiEnum.NotYetDefined)
                //    MPTK_StatusLastMidiLoaded = LoadingStatusMidiEnum.MidiFileInvalid;
            });
        }

        /// <summary>
        /// Plays the MIDI file defined with MPTK_MidiName.
        /// </summary>
        /// <param name="alreadyLoaded">True if the MIDI has already been loaded (see MPTK_Load(), v2.9.0).</param>
        public override void MPTK_Play(bool alreadyLoaded = false)
        {
            try
            {
                MPTK_StatusLastMidiLoaded = LoadingStatusMidiEnum.NotYetDefined;

                if (MPTK_SoundFont.IsReady)
                {
                    playPause = false;

                    if (!MPTK_IsPlaying)
                    {
                        if (string.IsNullOrEmpty(pathmidiNameToPlay))
                        {
                            //Debug.LogWarning("MPTK_Play: set MPTK_MidiName or Midi Url/path in inspector before playing");
                            MPTK_StatusLastMidiLoaded = LoadingStatusMidiEnum.MidiNameNotDefined;
                        }
                        else if (!pathmidiNameToPlay.ToLower().StartsWith("file://") &&
                                !pathmidiNameToPlay.ToLower().StartsWith("http://") &&
                                !pathmidiNameToPlay.ToLower().StartsWith("https://"))
                        {
                            //Debug.LogWarning("MPTK_MidiName must start with file:// or http:// or https:// - found: '" + pathmidiNameToPlay + "'");
                            MPTK_StatusLastMidiLoaded = LoadingStatusMidiEnum.MidiNameInvalid;
                        }
                        else if (pathmidiNameToPlay.ToLower().StartsWith("file://") && !File.Exists(pathmidiNameToPlay.Remove(0, 7)))
                        {
                            //Debug.LogWarning("Midi file not found '" + pathmidiNameToPlay + "'");
                            MPTK_StatusLastMidiLoaded = LoadingStatusMidiEnum.NotFound;
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
                    StartCoroutine(ThreadLoadDataAndPlay());
                }

                // removed with V2.16.0 - useful?
                //else
                //{
                //    try
                //    {
                //        OnEventEndPlayMidi.Invoke(pathmidiNameToPlay, EventEndMidiEnum.MidiErr);
                //    }
                //    catch (Exception ex)
                //    {
                //        Debug.LogError("OnEventEndPlayMidi: exception detected. Check the callback code");
                //        Debug.LogException(ex);
                //    }
                //}
            }
            catch (System.Exception ex)
            {
                MidiPlayerGlobal.ErrorDetail(ex);
            }
        }


        /// <summary>@brief
        /// Loads the MIDI file in the background and starts playback.
        /// </summary>
        /// <returns></returns>
        private IEnumerator ThreadLoadDataAndPlay()
        {
            base.MPTK_MidiName = pathmidiNameToPlay;
            MPTK_WebRequestError = "";
            using (UnityWebRequest webRequest = UnityWebRequest.Get(pathmidiNameToPlay))
            {
                yield return webRequest.SendWebRequest();
                byte[] data = null;
                //Debug.Log($"result:{req.result} {pathmidiNameToPlay}");
                MPTK_WebRequestError = webRequest.error;

                if (webRequest.result != UnityWebRequest.Result.ConnectionError)
                {
                    data = webRequest.downloadHandler.data;
                    if (data == null || webRequest.result == UnityWebRequest.Result.ConnectionError ||
                        webRequest.result == UnityWebRequest.Result.DataProcessingError || webRequest.result == UnityWebRequest.Result.ProtocolError)
                    {
                        MPTK_StatusLastMidiLoaded = LoadingStatusMidiEnum.NotFound;
                        Debug.LogWarning($"Error Loading Midi: '{pathmidiNameToPlay}'");
                        Debug.LogWarning($"Response code: {webRequest.responseCode}");
                    }
                    else if (data.Length == 0)
                    {
                        MPTK_StatusLastMidiLoaded = LoadingStatusMidiEnum.MidiFileEmpty;
                        Debug.LogWarning($"Error Loading Midi: '{pathmidiNameToPlay}'");
                        Debug.LogWarning($"Read 0 bytes from the MIDI file.");
                    }
                    else if (data.Length < 4)
                    {
                        MPTK_StatusLastMidiLoaded = LoadingStatusMidiEnum.TooShortSize;
                        Debug.LogWarning($"Error Loading Midi: '{pathmidiNameToPlay}'");
                        Debug.LogWarning($"Not a midi file, too short size");
                    }
                    else if (System.Text.Encoding.Default.GetString(data, 0, 4) != "MThd")
                    {
                        MPTK_StatusLastMidiLoaded = LoadingStatusMidiEnum.NoMThdSignature;
                        Debug.LogWarning($"Error Loading Midi: '{pathmidiNameToPlay}'");
                        Debug.LogWarning($"Not a midi file, signature MThd not found");
                    }
                }
                else
                {
                    MPTK_StatusLastMidiLoaded = LoadingStatusMidiEnum.NetworkError; // Web site not found
                    Debug.LogWarning($"Error Loading Midi: '{pathmidiNameToPlay}'");
                    Debug.LogWarning($"Network error {webRequest.error}");
                }

                if (MPTK_StatusLastMidiLoaded == LoadingStatusMidiEnum.NotYetDefined)
                {
                    //Debug.Log($"ThreadLoadDataAndPlay1 {AudiosourceTemplate}");
                    // Start playing
                    if (MPTK_CorePlayer)
                        Routine.RunCoroutine(ThreadCorePlay(data).CancelWith(gameObject), Segment.RealtimeUpdate);
                    else
                        Routine.RunCoroutine(ThreadLegacyPlay(data).CancelWith(gameObject), Segment.RealtimeUpdate);
                }
                else
                {
                    try
                    {
                        OnEventEndPlayMidi.Invoke(pathmidiNameToPlay, EventEndMidiEnum.MidiErr);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError("OnEventEndPlayMidi: exception detected. Check the callback code");
                        Debug.LogException(ex);
                    }
                }
            }
        }

        //        /// <summary>@brief
        //        /// Drive the MidiPlayer system thread from the Unity thread
        //        /// </summary>
        //        /// <param name="midiBytesToPlay"></param>
        //        /// <param name="fromPosition">time to start in millisecond</param>
        //        /// <param name="toPosition">time to end in millisecond</param>
        //        /// <returns></returns>
        //        public IEnumerator<float> ThreadWritePlay(MidiFileWriter2 mfw2, float fromPosition = 0, float toPosition = 0)
        //        {
        //            StartPlaying();
        //            string currentMidiName = MPTK_MidiName;
        //            //Debug.Log("Start play " + fromPosition + " " + toPosition);
        //            try
        //            {

        //                midiLoaded = new MidiLoad();
        //                midiLoaded.KeepNoteOff = MPTK_KeepNoteOff;
        //                midiLoaded.EnableChangeTempo = MPTK_EnableChangeTempo;
        //                if (!midiLoaded.MPTK_Load(mfw2))
        //                    midiLoaded = null;
        //#if DEBUG_START_MIDI
        //                Debug.Log("After load midi " + (double)watchStartMidi.ElapsedTicks / ((double)System.Diagnostics.Stopwatch.Frequency / 1000d));
        //#endif
        //            }
        //            catch (System.Exception ex)
        //            {
        //                MidiPlayerGlobal.ErrorDetail(ex);
        //            }

        //            Routine.RunCoroutine(ThreadMidiPlaying(currentMidiName, fromPosition, toPosition).CancelWith(gameObject), Segment.RealtimeUpdate);
        //            yield return 0;
        //        }



        //! @cond NODOC

        /// <summary>@brief
        /// Not applicable to external playback.
        /// </summary>
        public new int MPTK_MidiIndex
        {
            get
            {
                Debug.LogWarning("MPTK_MidiIndex not available for MidiExternalPlayer");
                return -1;
            }
            set
            {
                Debug.LogWarning("MPTK_MidiIndex not available for MidiExternalPlayer");
            }
        }
        /// <summary>@brief
        /// Not applicable to external playback.
        /// </summary>
        public new MidiLoad MPTK_Load()
        {
            return null;
        }


        /// <summary>@brief
        /// Not applicable to external playback.
        /// </summary>
        public new void MPTK_Next()
        {
            try
            {
                Debug.LogWarning("MPTK_Next not available for MidiExternalPlayer");
            }
            catch (System.Exception ex)
            {
                MidiPlayerGlobal.ErrorDetail(ex);
            }
        }

        /// <summary>@brief
        /// Not applicable to external playback.
        /// </summary>
        public new void MPTK_Previous()
        {
            try
            {
                Debug.LogWarning("MPTK_Previous not available for MidiExternalPlayer");
            }
            catch (System.Exception ex)
            {
                MidiPlayerGlobal.ErrorDetail(ex);
            }
        }
        //! @endcond

    }
}

