using System;
using System.Collections.Generic;
using System.Numerics;
using UnityEngine;

namespace MidiPlayerTK
{
    // Singleton class to manage all global features of MPTK.
    /// @ingroup runtime_global_config
    public partial class MidiPlayerGlobal : MonoBehaviour
    {
        /// <summary>@brief
        /// @warning MPTK_LiveSoundFont has been deprecated; use &lt;midiSynth&gt;.MPTK_SoundFont.IsReady instead.
        /// Gets or sets the full path to a SoundFont file (.sf2) or a URL to load.
        /// Defined in the MidiPlayerGlobal editor inspector.
        /// Must start with file:// or http:// or https://.
        /// @version Maestro Pro 
        /// </summary>
        public string MPTK_LiveSoundFont;

        /// <summary>@brief 
        /// Status of the last SoundFont loaded. The status is updated in a coroutine, so the status can change at each frame.
        /// @version 2.11.2
        /// </summary>
        [HideInInspector]
        public static LoadingStatusSoundFontEnum MPTK_StatusLastSoundFontLoaded;


        /// <summary>@brief
        /// Changes the current SoundFont on the fly. If MidiFilePlayers are running, they are stopped and optionally restarted.
        /// @version Maestro Pro 
        /// </summary>
        /// <param name="name">SoundFont name</param>
        /// <param name="restartPlayer">If a MIDI is playing, restart the currently playing MIDI.</param>
        public static void MPTK_SelectSoundFont(string name, bool restartPlayer = true)
        {
            if (Application.isPlaying)
                Routine.RunCoroutine(SelectSoundFontThread(name, restartPlayer), Segment.RealtimeUpdate);
            else
                SelectSoundFont(name);
        }

        /// <summary>@brief
        /// Sets the default SoundFont.
        /// @version Maestro Pro 
        /// </summary>
        /// <param name="name"></param>
        /// <param name="restartPlayer"></param>
        /// <returns></returns>
        private static IEnumerator<float> SelectSoundFontThread(string name, bool restartPlayer = true)
        {
            if (!string.IsNullOrEmpty(name))
            {
                int index = CurrentMidiSet.SoundFonts.FindIndex(s => s.Name == name);
                if (index >= 0)
                {
                    MidiPlayerGlobal.CurrentMidiSet.SetActiveSoundFont(index);
                    MidiPlayerGlobal.CurrentMidiSet.Save();
                }
                else
                {
                    Debug.LogWarning("SoundFont not found: " + name);
                    yield return 0;
                }
            }
            // Load selected soundfont
            yield return Routine.WaitUntilDone(Routine.RunCoroutine(LoadSoundFontThread(restartPlayer), Segment.RealtimeUpdate));
        }

        /// <summary>@brief
        /// Selects and loads a SoundFont in the editor.
        /// @version Maestro Pro 
        /// </summary>
        /// <param name="name"></param>
        private static void SelectSoundFont(string name)
        {
            if (!string.IsNullOrEmpty(name))
            {
                int index = CurrentMidiSet.SoundFonts.FindIndex(s => s.Name == name);
                if (index >= 0)
                {
                    MidiPlayerGlobal.CurrentMidiSet.SetActiveSoundFont(index);
                    MidiPlayerGlobal.CurrentMidiSet.Save();
                    // Load selected soundfont
                    LoadSoundFont();
                }
                else
                {
                    Debug.LogWarning("SoundFont not found " + name);
                }
            }
        }

        /// <summary>@brief
        ///  @warning MPTK_LoadLiveSF has been deprecated; use &lt;midiSynth&gt;.MPTK_SoundFont.IsReady instead.
        ///  Loads a SoundFont on the fly while the application is running.\n
        ///  The SoundFont is loaded from a local file, from the web, or from a cache.\n
        ///  If some MIDIs are playing, they are restarted.\n
        ///  Loading is done in the background (coroutine), so the method returns immediately.\n
        ///  @version Maestro Pro - updated 2.11.2
        ///  @note Look also:
        ///     - #MPTK_LiveSoundFont
        ///     - #MPTK_StatusLastSoundFontLoaded 
        ///     - #MPTK_LoadSoundFontAtStartup 
        ///     - #MPTK_PathSoundFontCache
        ///     - #MPTK_SoundFontLoaded
        ///     - #MPTK_SoundFontIsReady
        /// </summary>
        /// <param name="pPathSF">Full path to the SoundFont file. Must start with `file://` for local desktop loading, or `http://` / `https://` for a web resource.
        /// If null, the path defined in MPTK_LiveSoundFont is used.</param>
        /// <param name="defaultBank">Default bank to use for instruments; use -1 to select the first bank.</param>
        /// <param name="drumBank">Bank to use for the drum kit; use -1 to select the last bank.</param>
        /// <param name="restartPlayer">Restart the MIDI player if needed. Default is true.</param>
        /// <param name="useCache">Reuse an already downloaded SoundFont if it exists. Default is true.</param>
        /// <param name="log">Display logs. Default is false.</param>
        /// <returns>
        ///     - true if loading is in progress; use OnEventPresetLoaded to get information when loading is complete (for example, MPTK_StatusLastSoundFontLoaded)
        ///     - false if an error is detected in the parameters. The OnEventPresetLoaded callback is not called if false is returned.
        /// </returns>
        static public bool MPTK_LoadLiveSF(string pPathSF = null, int defaultBank = -1, int drumBank = -1, bool restartPlayer = true, bool useCache = true, bool log = false)
        {
            MPTK_StatusLastSoundFontLoaded = LoadingStatusSoundFontEnum.InProgress;
            timeToDownloadSoundFont = TimeSpan.Zero;
            timeToLoadSoundFont = TimeSpan.Zero;
            timeToLoadWave = TimeSpan.Zero;
            MPTK_CountWaveLoaded = 0;

            if (!string.IsNullOrEmpty(pPathSF))
                instance.MPTK_LiveSoundFont = pPathSF;

            if (string.IsNullOrEmpty(instance.MPTK_LiveSoundFont))
            {
                MPTK_StatusLastSoundFontLoaded = LoadingStatusSoundFontEnum.InvalidURL;
                Debug.LogWarning("MPTK_LoadLiveSF: SoundFont path not defined");
            }
            else if (!instance.MPTK_LiveSoundFont.ToLower().StartsWith("file://") &&
                     !instance.MPTK_LiveSoundFont.ToLower().StartsWith("http://") &&
                     !instance.MPTK_LiveSoundFont.ToLower().StartsWith("https://"))
            {
                MPTK_StatusLastSoundFontLoaded = LoadingStatusSoundFontEnum.InvalidURL;
                Debug.LogWarning("MPTK_LoadLiveSF: path to SoundFont must start with file:// or http:// or https:// - found: '" + instance.MPTK_LiveSoundFont + "'");
            }
            else
            {
                MidiSynth[] synths = FindObjectsByType<MidiSynth>(FindObjectsSortMode.None);
                if (Application.isPlaying)
                    Routine.RunCoroutine(ImSoundFont.LoadLiveSF(instance.MPTK_LiveSoundFont, defaultBank, drumBank, synths, restartPlayer, useCache, log), Segment.RealtimeUpdate);
                else
                    Routine.RunCoroutine(ImSoundFont.LoadLiveSF(instance.MPTK_LiveSoundFont, defaultBank, drumBank, synths, restartPlayer, useCache, log), Segment.EditorUpdate);
                return true;
            }
            return false;
        }


        // not yet available ... perhaps never!
        static public bool MPTK_MergeLiveSF(string pPathSF)
        {
            string pathSF = string.IsNullOrEmpty(pPathSF) ? instance.MPTK_LiveSoundFont : pPathSF;

            if (string.IsNullOrEmpty(pathSF))
                Debug.LogWarning("MPTK_MergeLiveSF: SoundFont path not defined");
            else if (!pathSF.ToLower().StartsWith("file://") &&
                     !pathSF.ToLower().StartsWith("http://") &&
                     !pathSF.ToLower().StartsWith("https://"))
                Debug.LogWarning("MPTK_MergeLiveSF: path to SoundFont must start with file:// or http:// or https:// - found: '" + pathSF + "'");
            else
            {
                //           Routine.RunCoroutine(ImSoundFont.MergeLiveSF(pathSF), Segment.RealtimeUpdate);
                return true;
            }
            return false;
        }
    }
}
